using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed partial class FunctionBodyBinder
{
    // Applicability and argument/result binding around a conversion use standard conversions only.
    private int _userConversionDepth;

    private BoundExpression CreateOperatorCall(FunctionSymbol selected,
        ImmutableArray<BoundExpression> arguments, bool explicitConversion = false)
    {
        if (selected.ContainingStruct is { IsOpenGenericType: true, GenericDefinition: not null } owner)
        {
            FunctionSymbol definition = _fileScope.GenericStructSpecializer!.GetFunctionDefinition(selected);
            // Persist the declaration and structural owner, never an open specialization as a declaration.
            return new BoundDeferredGenericOperationExpression(BoundDeferredGenericOperationKind.OperatorCall,
                null, definition, arguments, null, SyntaxKind.EqualsToken,
                explicitConversion, selected.ReturnType, [owner]);
        }
        return new BoundCallExpression(selected, arguments) { IsExplicitConversion = explicitConversion };
    }

    private static BoundExpression CaptureOperatorOperand(BoundExpression expression, BoundExpression operand)
    {
        if (ReferenceEquals(expression, operand))
            return new BoundCapturedPlaceExpression(operand.Type, OwnsValue: operand is BoundMethodCallExpression);
        return expression switch
        {
            BoundCallExpression call => call with
                { Arguments = call.Arguments.Select(argument => CaptureOperatorOperand(argument, operand)).ToImmutableArray() },
            BoundDeferredGenericOperationExpression call => call with
                { Arguments = call.Arguments.Select(argument => CaptureOperatorOperand(argument, operand)).ToImmutableArray() },
            BoundReferenceConversionExpression reference => reference with { Source = CaptureOperatorOperand(reference.Source, operand) },
            BoundReferenceDereferenceExpression reference => reference with { Reference = CaptureOperatorOperand(reference.Reference, operand) },
            BoundCopyExpression copy => copy with { Source = CaptureOperatorOperand(copy.Source, operand) },
            BoundInterfaceConversionExpression conversion => conversion with { Source = CaptureOperatorOperand(conversion.Source, operand) },
            _ => expression,
        };
    }

    private IEnumerable<FunctionSymbol> DiscoverOperators(IEnumerable<TypeSymbol> types, OperatorKind kind)
    {
        var discovered = new HashSet<FunctionSymbol>();
        foreach (StructTypeSymbol type in types.Select(OperatorFacts.ValueType).OfType<StructTypeSymbol>().Distinct())
        {
            var visible = new List<FunctionSymbol>();
            for (StructTypeSymbol? current = type; current is not null; current = current.BaseType)
            foreach (FunctionSymbol method in current.Methods)
            {
                // Apply accessibility before inherited signature hiding as well as before ranking.
                if (!method.IsStatic || method.OperatorKind != kind || !IsAccessible(method) ||
                    visible.Any(candidate => candidate.HasSameSignature(method))) continue;
                visible.Add(method);
                if (discovered.Add(method)) yield return method;
            }
        }
    }

    private bool TryBindUserOperator(SyntaxKind operation, ImmutableArray<BoundExpression> operands,
        ImmutableArray<ExpressionSyntax> operandSyntax, SyntaxNode syntax, TextLocation location,
        out BoundExpression? result)
    {
        result = null;
        OperatorKind kind = OperatorFacts.FromSyntax(operation, operands.Length);
        if (!OperatorFacts.IsAllowed(kind, operands.Length)) return false;
        string spelling = OperatorFacts.GetSpelling(kind);
        FunctionSymbol[] applicable = DiscoverOperators(operands.Select(operand => operand.Type), kind)
            .Where(method => method.Parameters.Length == operands.Length && method.Parameters.Zip(operands)
                .All(pair => GetArgumentConversionCost(pair.First.Type, pair.Second) is not null)).ToArray();
        if (applicable.Length == 0) return false;
        FunctionSymbol? selected = ResolveCallableOverload(applicable, operands, location,
            $"operator '{spelling}'", syntax, out _);
        if (selected is null) { result = new BoundErrorExpression(); return true; }
        result = CreateOperatorCall(selected,
            ValidateFunctionArguments(selected, operands, operandSyntax, location));
        return true;
    }

    private (FunctionSymbol Function, int[] Costs)[] FindUserConversions(BoundExpression source,
        TypeSymbol destination, bool explicitContext)
    {
        if (_userConversionDepth != 0 || destination is ReferenceTypeSymbol { IsReadonly: false }) return [];
        TypeSymbol valueDestination = OperatorFacts.ValueType(destination);
        if (TypeIdentity.AreSame(source.Type, BuiltinTypes.Error) ||
            TypeIdentity.AreSame(valueDestination, BuiltinTypes.Error)) return [];
        _userConversionDepth++;
        try
        {
            TypeSymbol[] types = [source.Type, valueDestination];
            IEnumerable<FunctionSymbol> candidates = DiscoverOperators(types, OperatorKind.ImplicitConversion);
            if (explicitContext) candidates = candidates.Concat(DiscoverOperators(types, OperatorKind.ExplicitConversion));
            var matches = new List<(FunctionSymbol Function, int[] Costs)>();
            foreach (FunctionSymbol candidate in candidates.Distinct())
            {
                if (candidate.Parameters.Length != 1 || candidate.ReturnType is ReferenceTypeSymbol) continue;
                int? input = GetStandardArgumentConversionCost(candidate.Parameters[0].Type, source);
                int? output = TypeFacts.GetImplicitConversionCost(valueDestination, candidate.ReturnType);
                if (input is not null && output is not null)
                    matches.Add((candidate, [input.Value, output.Value]));
            }
            return matches.Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other.Function, candidate.Function) &&
                IsBetterConversionSequence(other.Costs, candidate.Costs))).ToArray();
        }
        finally { _userConversionDepth--; }
    }

    private bool TryBindUserConversion(BoundExpression source, TypeSymbol destination, TextLocation location,
        bool explicitContext, out BoundExpression? result, SyntaxNode? syntax = null)
    {
        result = null;
        var best = FindUserConversions(source, destination, explicitContext);
        if (best.Length == 0) return false;
        if (best.Length != 1)
        {
            _diagnostics.Report(location,
                $"conversion from '{source.Type.ToDisplayString()}' to '{destination.ToDisplayString()}' is ambiguous between: {FormatCallableCandidates(best.Select(match => match.Function))}",
                DiagnosticIds.AmbiguousConversion);
            result = new BoundErrorExpression();
            return true;
        }
        FunctionSymbol selected = best[0].Function;
        syntax ??= _expressionSyntax.GetValueOrDefault(source);
        _userConversionDepth++;
        try
        {
            BoundExpression call = CreateOperatorCall(selected,
                ValidateFunctionArguments(selected, [source], [], location), explicitContext);
            if (_argumentFlowCandidates.TryGetValue(source, out ArgumentFlowTransaction? transaction))
                transaction.CommitMaterializedCandidate(source);
            _expressionLocations[call] = location;
            if (syntax is not null)
            {
                _semanticInfo.Conversions[syntax] = SymbolInfo.FromSymbol(selected);
                _semanticInfo.ExplicitReferences.Add(new ResolvedSymbolReference(selected, location, ResolvedReferenceKind.Call));
                if (syntax is CastExpressionSyntax or LiteralExpressionSyntax)
                    RecordCandidates(syntax, selected, [selected], CandidateReason.None);
            }
            RecordExceptionalFlow();
            result = ContextualizeConversion(call, destination, location);
            return true;
        }
        finally { _userConversionDepth--; }
    }

    private FunctionValueTypeSymbol? GetLambdaUserConversionInput(TypeSymbol destination)
    {
        if (_userConversionDepth != 0) return null;
        TypeSymbol valueDestination = OperatorFacts.ValueType(destination);
        FunctionValueTypeSymbol[] inputs = DiscoverOperators([valueDestination], OperatorKind.ImplicitConversion)
            .Where(candidate => candidate.Parameters.Length == 1 &&
                candidate.Parameters[0].Type is FunctionValueTypeSymbol &&
                TypeFacts.GetImplicitConversionCost(valueDestination, candidate.ReturnType) is not null)
            .Select(candidate => (FunctionValueTypeSymbol)candidate.Parameters[0].Type)
            .DistinctBy(TypeSignature.Get)
            .ToArray();
        return inputs.Length == 1 ? inputs[0] : null;
    }
}
