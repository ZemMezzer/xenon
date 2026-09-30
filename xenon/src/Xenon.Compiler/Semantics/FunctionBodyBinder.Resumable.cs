using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed partial class FunctionBodyBinder
{
    private LocalVariableSymbol? _resumableResult;
    private BoundExpression? _resumableConstruction;
    private int _suspensionIndex;



    private void BeginResumableBinding(BlockStatementSyntax body)
    {
        if (!_function.IsAsync) return;
        var nested = SyntaxNavigator.DescendantNodesAndSelf(body).OfType<LambdaExpressionSyntax>()
            .SelectMany(SyntaxNavigator.DescendantNodesAndSelf).ToHashSet(ReferenceEqualityComparer.Instance);

        TextLocation location = body.OpenBraceToken.Location;
        foreach (TryStatementSyntax region in SyntaxNavigator.DescendantNodesAndSelf(body).OfType<TryStatementSyntax>())
        {
            IEnumerable<SyntaxNode> unsupported = region.Catches.SelectMany(handler => SyntaxNavigator.DescendantNodesAndSelf(handler.Body));
            if (region.FinallyBody is { } finalizer) unsupported = unsupported.Concat(SyntaxNavigator.DescendantNodesAndSelf(finalizer));
            foreach (AwaitExpressionSyntax suspension in unsupported.OfType<AwaitExpressionSyntax>().Where(node => !nested.Contains(node)))
                _diagnostics.Report(suspension.AwaitKeyword.Location,
                    "suspension inside catch or finally cannot retain the thread-local active exception; await inside the protected try body is supported",
                    DiagnosticIds.InvalidAwaitContext);
        }
        if (_function.FunctionKind is FunctionKind.Constructor or FunctionKind.Destructor ||
            _function.ReturnType is not StructTypeSymbol { IsStatic: false, IsAbstract: false } type)
        {
            _diagnostics.Report(location, $"type '{_function.ReturnType.ToDisplayString()}' cannot be used as an async return type because it does not provide the required completion protocol",
                DiagnosticIds.InvalidResumableReturn);
            return;
        }
        if (!DiscoverOperators([type], OperatorKind.Resolve).Any(candidate => IsAccessible(candidate) &&
                candidate.Parameters.Length is 1 or 2 && OperatorFacts.ProtocolSignatureError(candidate) is null))
            _diagnostics.Report(location,
                $"async return type '{type.ToDisplayString()}' requires an accessible operator resolve({type.ToDisplayString()}&, value) or operator resolve({type.ToDisplayString()}&)",
                DiagnosticIds.MissingCompletionOperator);
        _resumableResult = new LocalVariableSymbol("__resumable_result", type, _function, false)
            { Destructor = TypeFacts.GetCompleteDestructor(type) };
        _definitelyAssigned.Add(_resumableResult);
        if (SyntaxNavigator.DescendantNodesAndSelf(body).OfType<AwaitExpressionSyntax>().Any(node => !nested.Contains(node)) && !TypeFacts.CanCopy(type))
            _diagnostics.Report(location, "resumable return object must support ordinary copying so the caller and frame can own handles to the same logical state",
                DiagnosticIds.InvalidResumableReturn);
        if (type.Constructors.IsEmpty)
        {
            ValidateDefaultInitialization(type, location);
            _resumableConstruction = new BoundStructConstructionExpression(type, []) { IsDefaultInitialization = true };
        }
        else
        {
            FunctionSymbol? constructor = type.Constructors.FirstOrDefault(candidate => candidate.Parameters.IsEmpty && IsAccessible(candidate));
            if (constructor is null)
            {
                _diagnostics.Report(location, "async return type requires an accessible parameterless constructor",
                    DiagnosticIds.InvalidResumableReturn);
                _resumableConstruction = new BoundErrorExpression();
            }
            else _resumableConstruction = new BoundConstructorCallExpression(type, constructor, []);
        }
        ValidateDestructorAccessibility(type, location);

    }

    private BoundExpression BindCompletionOperator(OperatorKind kind, ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax, SyntaxNode syntax, TextLocation location)
    {
        FunctionSymbol[] candidates = DiscoverOperators(arguments.Select(argument => argument.Type), kind)
            .Where(candidate => candidate.Parameters.Length == arguments.Length && OperatorFacts.ProtocolSignatureError(candidate) is null)
            .ToArray();
        FunctionSymbol? selected = ResolveCallableOverload(candidates, arguments, location,
            $"operator '{OperatorFacts.GetSpelling(kind)}'", syntax, out _);
        if (selected is null)
        {
            if (candidates.Length == 0)
                _diagnostics.Report(location, $"no suitable operator {OperatorFacts.GetSpelling(kind)} for completion target",
                    DiagnosticIds.MissingCompletionOperator);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        RecordExceptionalFlow();
        return CreateOperatorCall(selected, ValidateFunctionArguments(selected, arguments, argumentSyntax, location));
    }

    private BoundReturnStatement BindResumableReturn(ReturnStatementSyntax syntax)
    {
        BoundExpression target = new BoundVariableExpression(_resumableResult!);
        ImmutableArray<BoundExpression> arguments = syntax.Expression is null ? [target] :
            [target, BindDeferredContextualArgument(syntax.Expression)];
        BoundExpression completion = BindCompletionOperator(OperatorKind.Resolve, arguments, [], syntax, syntax.ReturnKeyword.Location);
        ImmutableArray<BoundExpression> completedArguments = completion switch
        {
            BoundCallExpression call => call.Arguments,
            BoundDeferredGenericOperationExpression call => call.Arguments,
            _ => [],
        };
        if (completedArguments.Length == 2)
        {
            BoundExpression value = completedArguments[1];
            if (value.Type is ReferenceTypeSymbol)
                ValidateReturnedReference(value, syntax.ReturnKeyword.Location);
            else if (ContainsValueReferenceStorage(value.Type) &&
                GetValueReferenceMetadata(value, value.Type).Any(reference => !IsSafeReferenceReturnSource(reference.Source)))
                _diagnostics.Report(syntax.ReturnKeyword.Location,
                    "resumable completion cannot retain a value borrowing storage owned by the completed frame",
                    DiagnosticIds.AggregateReferenceEscape);
            if (HasCalleeStackBoundRuntimeStorage(value))
                _diagnostics.Report(syntax.ReturnKeyword.Location,
                    "resumable completion cannot retain stack-backed array storage", DiagnosticIds.StackArrayReturn);
        }
        RecordAbruptFinalizerFlow(_ => true);
        return new BoundReturnStatement(CompleteFullExpression(completion, resultConsumed: false));
    }

    private BoundBlockStatement FinishResumableBinding(BoundBlockStatement body, BlockStatementSyntax syntax)
    {
        TextLocation location = syntax.CloseBraceToken.Location;
        if (!AlwaysReturns(body))
        {
            if (!DiscoverOperators([_function.ReturnType], OperatorKind.Resolve).Any(candidate => candidate.Parameters.Length == 1))
                _diagnostics.Report(location, "not all code paths produce a value", DiagnosticIds.MissingCompletionOperator);
            BoundExpression completion = BindCompletionOperator(OperatorKind.Resolve,
                [new BoundVariableExpression(_resumableResult!)], [], syntax, location);
            body = body with { Statements = body.Statements.Add(new BoundReturnStatement(completion)) };
        }

        var catches = ImmutableArray.CreateBuilder<BoundCatchClause>();
        FunctionSymbol[] rejects = DiscoverOperators([_function.ReturnType], OperatorKind.Reject)
            .Where(candidate => candidate.Parameters.Length == 2 && IsAccessible(candidate) &&
                OperatorFacts.ProtocolSignatureError(candidate) is null).ToArray();
        foreach (TypeSymbol errorType in rejects.Select(candidate => OperatorFacts.ValueType(candidate.Parameters[1].Type))
                     .Distinct(TypeIdentity.Comparer).OrderByDescending(ExceptionInheritanceDepth))
        {
            ReferenceTypeSymbol reference = _fileScope.TypeFactory.ReferenceTo(errorType, isReadonly: true);
            var error = new LocalVariableSymbol("__resumable_error", reference, _function, true);
            _definitelyAssigned.Add(error);
            BoundExpression errorValue = new BoundReferenceDereferenceExpression(new BoundVariableExpression(error), reference);
            BoundExpression rejection = BindCompletionOperator(OperatorKind.Reject,
                [new BoundVariableExpression(_resumableResult!), errorValue], [], syntax, location);
            catches.Add(new BoundCatchClause(errorType, error,
                new BoundBlockStatement([new BoundReturnStatement(CompleteFullExpression(rejection, resultConsumed: false))])));
        }
        BoundBlockStatement result = new([
            new BoundVariableDeclarationStatement(_resumableResult!, _resumableConstruction),
            new BoundTryStatement(body, catches.ToImmutable(), null)]) { IsResumable = true, RequiresSuspensionStateMachine = _suspensionIndex != 0 };
        FunctionCleanupAnalyzer.Recompute(_function, result);

        return result;
    }

    private static int ExceptionInheritanceDepth(TypeSymbol type) =>
        type is StructTypeSymbol { BaseType: { } parent } ? 1 + ExceptionInheritanceDepth(parent) : 0;

    private BoundExpression BindAwaitExpression(AwaitExpressionSyntax syntax)
    {
        TextLocation location = syntax.AwaitKeyword.Location;
        if (!_function.IsAsync)
        {
            _diagnostics.Report(location, "'await' may only be used inside an async function", DiagnosticIds.InvalidAwaitContext);
            return new BoundErrorExpression();
        }
        if (_resumableResult is null)
        {
            _diagnostics.Report(location, "await requires a function with a valid resumable return type", DiagnosticIds.InvalidAwaitContext);
            return new BoundErrorExpression();
        }
        BoundExpression operand = BindExpression(syntax.Operand);
        var candidates = DiscoverOperators([operand.Type], OperatorKind.Await)
            .Where(candidate => OperatorFacts.ProtocolSignatureError(candidate) is null)
            .Select(candidate => (Function: candidate, Cost: GetArgumentConversionCost(candidate.Parameters[0].Type, operand)))
            .Where(candidate => candidate.Cost.HasValue).ToArray();
        if (candidates.Length == 0)
        {
            _diagnostics.Report(location, $"type '{operand.Type.ToDisplayString()}' has no suitable operator await", DiagnosticIds.MissingAwaitOperator);
            return new BoundErrorExpression();
        }
        int bestCost = candidates.Min(candidate => candidate.Cost!.Value);
        FunctionSymbol[] best = candidates.Where(candidate => candidate.Cost == bestCost).Select(candidate => candidate.Function).ToArray();
        if (best.Length != 1)
        {
            _diagnostics.Report(location, $"operator await is ambiguous between: {FormatCallableCandidates(best)}", DiagnosticIds.AmbiguousCall);
            return new BoundErrorExpression();
        }
        FunctionSymbol selected = best[0];
        RecordCandidates(syntax, selected, candidates.Select(candidate => candidate.Function), CandidateReason.None);
        StorageTypeSymbol? storageType = selected.Parameters.Length == 3
            ? (StorageTypeSymbol)((ReferenceTypeSymbol)selected.Parameters[1].Type).ElementType : null;
        var continuation = new LocalVariableSymbol($"__continuation_{_suspensionIndex}", selected.Parameters[^1].Type, _function, false);
        LocalVariableSymbol? storage = storageType is null ? null :
            new LocalVariableSymbol($"__await_result_{_suspensionIndex}", storageType, _function, false);
        _suspensionIndex++;
        _definitelyAssigned.Add(continuation);
        _valueReferenceMetadata[new MovePlace(continuation, [])] = [];
        if (storage is not null) _definitelyAssigned.Add(storage);
        ImmutableArray<BoundExpression> arguments = storage is null
            ? [operand, new BoundVariableExpression(continuation)]
            : [operand, new BoundVariableExpression(storage), new BoundVariableExpression(continuation)];
        ImmutableArray<BoundExpression> converted = ValidateFunctionArguments(selected, arguments, [], location);
        // Keep the materialized operand stable across retries, including any reference conversion.
        converted = converted.SetItem(0, CaptureOperatorOperand(converted[0], operand));
        if (selected.Parameters[0].Type is not ReferenceTypeSymbol && converted[0] is not BoundCopyExpression)
        {
            if (!TypeFacts.CanCopy(converted[0].Type))
                _diagnostics.Report(location, "a by-value await operand must be copyable for retries; use a reference operand for a move-only awaitable",
                    DiagnosticIds.ValueNotCopyable);
            else converted = converted.SetItem(0, new BoundCopyExpression(converted[0]));
        }
        RecordExceptionalFlow();
        var awaiting = new BoundAwaitExpression(operand, CreateOperatorCall(selected, converted), storage, continuation,
            storageType?.ElementType ?? BuiltinTypes.Void);
        return awaiting;
    }
}
