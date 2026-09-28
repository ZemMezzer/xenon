using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>Checks typed exceptional completion after all source and library bodies are available.</summary>
internal sealed class ResumableExceptionAnalyzer
{
    private readonly Dictionary<FunctionSymbol, BoundBlockStatement> _bodies = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionSymbol, HashSet<TypeSymbol>> _effects = new(ReferenceEqualityComparer.Instance);

    public static ImmutableArray<Diagnostic> Analyze(Compilation compilation, CancellationToken cancellationToken)
    {
        BoundFunction[] resumable = compilation.SemanticModel.Functions.Where(function => function.Body.IsResumable).ToArray();
        if (resumable.Length == 0) return [];
        var analyzer = new ResumableExceptionAnalyzer();
        analyzer.AddCompilation(compilation);
        analyzer.ComputeEffects(cancellationToken);
        var diagnostics = new DiagnosticBag();
        foreach (BoundFunction function in resumable)
        {
            // Recheck concrete array layout after source-free generic specialization.
            if (!function.Symbol.IsSourceDefined)
            {
                ResumableFrameAnalysis frame = ResumableFrameAnalysis.Analyze(function.Symbol, function.Body, compilation.TypeFactory);
                TextLocation location = new(compilation.SyntaxTrees[0].Source, new TextSpan(0, 0));
                foreach (BoundArrayCreationExpression array in frame.RetainedArrays)
                    if (array.Storage == ArrayStorageKind.Stack && !ResumableFrameAnalysis.TryGetConstantLength(array, out _))
                        diagnostics.Report(location, "runtime-sized array backing storage crosses suspension in specialized resumable function",
                            DiagnosticIds.BorrowAcrossAwait);
            }
            var region = (BoundTryStatement)function.Body.Statements[1];
            HashSet<TypeSymbol> escaping = analyzer.Visit(region.Body, [], completionReturns: true);
            foreach (ParameterSymbol parameter in function.Symbol.Parameters)
                analyzer.AddFunction(escaping, TypeFacts.GetCompleteDestructor(parameter.Type));
            foreach (TypeSymbol error in escaping)
                if (!region.Catches.Any(handler => Matches(error, handler.Type)))
                    diagnostics.Report(function.Symbol.Locations.IsEmpty
                            ? new TextLocation(compilation.SyntaxTrees[0].Source, new TextSpan(0, 0))
                            : function.Symbol.Locations[0],
                        error == BuiltinTypes.Error
                            ? "resumable function has an exception of unknown type; handle it before completion"
                            : $"resumable return type has no suitable operator reject for escaping exception '{error.ToDisplayString()}'",
                        DiagnosticIds.MissingCompletionOperator);
        }
        return diagnostics.ToImmutableArray();
    }

    internal static IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>> InferEffects(
        IEnumerable<BoundFunction> functions, CancellationToken cancellationToken)
    {
        var analyzer = new ResumableExceptionAnalyzer();
        foreach (var function in functions) analyzer._bodies[function.Symbol] = function.Body;
        analyzer.ComputeEffects(cancellationToken);
        return analyzer._effects;
    }

    private void ComputeEffects(CancellationToken cancellationToken)
    {
        foreach (FunctionSymbol function in _bodies.Keys) _effects[function] = NewSet();
        bool changed;
        do
        {
            changed = false;
            foreach ((FunctionSymbol function, BoundBlockStatement body) in _bodies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Calls to a resumable wrapper can throw during return-object construction.
                // Exceptions from its running body are consumed by the completion protocol.
                HashSet<TypeSymbol> effects = Visit(body.IsResumable ? body.Statements[0] : body, [], body.IsResumable);
                if (!body.IsResumable)
                    foreach (ParameterSymbol parameter in function.Parameters)
                        AddFunction(effects, TypeFacts.GetCompleteDestructor(parameter.Type));
                int before = _effects[function].Count;
                _effects[function].UnionWith(effects);
                changed |= before != _effects[function].Count;
            }
        } while (changed);
    }

    private void AddCompilation(Compilation compilation)
    {
        foreach (BoundFunction function in compilation.SemanticModel.Functions) _bodies.TryAdd(function.Symbol, function.Body);
        foreach (CompilationReference reference in compilation.References)
            if (reference is SourceCompilationReference source) AddCompilation(source.Compilation);
            else if (reference is LibraryCompilationReference library)
                foreach (BoundFunction function in library.ImplementationFunctions) _bodies.TryAdd(function.Symbol, function.Body);
    }

    private static HashSet<TypeSymbol> NewSet() => new(TypeIdentity.Comparer);
    private static bool Matches(TypeSymbol thrown, TypeSymbol? caught)
    {
        if (caught is null) return true;
        for (TypeSymbol? type = thrown; type is not null; type = (type as StructTypeSymbol)?.BaseType)
            if (TypeIdentity.Equals(type, caught)) return true;
        return false;
    }

    private void AddFunction(HashSet<TypeSymbol> result, FunctionSymbol? function)
    {
        if (function is null || function.IsExtern) return;
        if (_effects.TryGetValue(function, out HashSet<TypeSymbol>? effects)) result.UnionWith(effects);
        else result.Add(BuiltinTypes.Error);
    }

    private void AddDynamic(HashSet<TypeSymbol> result, TypeSymbol returns, IEnumerable<TypeSymbol> parameters)
    {
        TypeSymbol[] signature = parameters.ToArray();
        foreach (FunctionSymbol candidate in _bodies.Keys)
            if (TypeIdentity.Equals(candidate.ReturnType, returns) &&
                candidate.Parameters.Select(parameter => parameter.Type).SequenceEqual(signature, TypeIdentity.Comparer))
                AddFunction(result, candidate);
    }

    private HashSet<TypeSymbol> Visit(BoundNode node, IEnumerable<TypeSymbol> active, bool completionReturns)
    {
        var result = NewSet();
        if (node is BoundTryStatement region)
        {
            HashSet<TypeSymbol> pending = Visit(region.Body, active, completionReturns);
            foreach (BoundCatchClause handler in region.Catches)
            {
                TypeSymbol[] caught = pending.Where(error => Matches(error, handler.Type)).ToArray();
                pending.ExceptWith(caught);
                result.UnionWith(Visit(handler.Body, caught, completionReturns));
            }
            result.UnionWith(pending);
            if (region.FinallyBody is not null) result.UnionWith(Visit(region.FinallyBody, active, completionReturns));
            return result;
        }
        // Completion itself is terminal, and a throwing completion operator terminates.
        if (completionReturns && node is BoundReturnStatement { Expression: { } expression })
        {
            BoundExpression inner = expression is BoundFullExpression full ? full.Expression : expression;
            if (inner is BoundCallExpression completion)
            {
                foreach (BoundExpression argument in completion.Arguments)
                {
                    result.UnionWith(Visit(argument, active, completionReturns));
                    AddFunction(result, TypeFacts.GetCompleteDestructor(argument.Type));
                }
                if (expression is BoundFullExpression temporaries)
                    foreach (BoundFullExpressionTemporary temporary in temporaries.Temporaries) AddFunction(result, temporary.Destructor);
                return result;
            }
        }
        foreach (BoundNode child in BoundTree.Children(node)) result.UnionWith(Visit(child, active, completionReturns));
        switch (node)
        {
            case BoundThrowStatement { Expression: { } error }: result.Add(OperatorFacts.ValueType(error.Type)); break;
            case BoundThrowStatement: result.UnionWith(active); break;
            case BoundCallExpression call: AddFunction(result, call.Function); break;
            case BoundMethodCallExpression call:
                AddFunction(result, call.Method);
                if (call.Method.IsVirtual || call.Method.IsOverride)
                    foreach (FunctionSymbol candidate in _bodies.Keys.Where(candidate => candidate.Name == call.Method.Name && candidate.IsOverride))
                        AddFunction(result, candidate);
                break;
            case BoundInterfaceMethodCallExpression call:
                AddDynamic(result, call.Method.ReturnType, call.Method.Parameters.Select(parameter => parameter.Type)); break;
            case BoundFunctionValueCallExpression call:
                AddDynamic(result, call.Type, call.FunctionValueType.ParameterTypes); break;
            case BoundIndirectCallExpression call:
                AddDynamic(result, call.Type, call.FunctionPointerType.ParameterTypes); break;
            case BoundConstructorCallExpression call: AddFunction(result, call.Constructor); break;
            case BoundBaseLifecycleCallExpression call: AddFunction(result, call.Function); break;
            case BoundNewExpression allocation: AddFunction(result, allocation.Constructor); break;
            case BoundStorageConstructExpression construction: AddFunction(result, construction.Constructor); break;
            case BoundExplicitDestructExpression destruction: AddFunction(result, destruction.Destructor); break;
            case BoundDeleteExpression deletion: AddFunction(result, deletion.Destructor); break;
            case BoundVariableDeclarationStatement variable: AddFunction(result, variable.Variable.Destructor); break;
            case BoundFullExpression full:
                foreach (BoundFullExpressionTemporary temporary in full.Temporaries) AddFunction(result, temporary.Destructor);
                break;
            case BoundOwnershipDestructionExpression destruction: AddFunction(result, destruction.ElementDestructor); break;
            case BoundStorageDestructionExpression destruction: AddFunction(result, destruction.ElementDestructor); break;
            case BoundDestroyFieldsExpression destruction:
                foreach (FieldSymbol field in destruction.StructType.AllInstanceFields) AddFunction(result, TypeFacts.GetCompleteDestructor(field.Type));
                if (destruction.StructType.BaseType is { } parent) AddFunction(result, TypeFacts.GetCompleteDestructor(parent));
                break;
            case BoundPropertySetExpression set: AddFunction(result, set.Property.Setter); break;
            case BoundIndexerSetExpression set: AddFunction(result, set.Indexer.Setter); break;
            case BoundCompoundAccessorAssignmentExpression assignment:
                AddFunction(result, assignment.Getter); AddFunction(result, assignment.Setter); break;
        }
        return result;
    }
}
