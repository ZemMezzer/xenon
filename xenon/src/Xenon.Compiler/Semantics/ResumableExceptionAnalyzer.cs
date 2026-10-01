using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>Checks typed exceptional completion after all source and library bodies are available.</summary>
internal sealed class ResumableExceptionAnalyzer
{
    private readonly Dictionary<FunctionSymbol, BoundBlockStatement> _bodies = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionSymbol, HashSet<TypeSymbol>> _effects = new(ReferenceEqualityComparer.Instance);
    private MirExceptionEffects _analysis = null!;

    public static ImmutableArray<Diagnostic> Analyze(Compilation compilation, CancellationToken cancellationToken)
    {
        BoundFunction[] resumable = compilation.SemanticModel.Functions.Where(function => function.Body.IsResumable).ToArray();
        if (resumable.Length == 0) return [];
        var analyzer = new ResumableExceptionAnalyzer();
        analyzer.AddCompilation(compilation);
        analyzer.ComputeEffects(compilation.TypeFactory, cancellationToken);
        var diagnostics = new DiagnosticBag();
        foreach (BoundFunction function in resumable)
        {
            // Recheck concrete array layout after source-free generic specialization.
            if (!function.Symbol.IsSourceDefined)
            {
                ResumableFrameAnalysis frame = ResumableFrameAnalysis.Analyze(function.Symbol, function.Body, compilation.TypeFactory);
                foreach (BoundArrayCreationExpression array in frame.RetainedArrays)
                    if (array.Storage == ArrayStorageKind.Stack && !ResumableFrameAnalysis.TryGetConstantLength(array, out _))
                        MirDiagnosticReporter.Report(diagnostics, function.Symbol, TextLocation.None, "runtime-sized array backing storage crosses suspension in specialized resumable function",
                            DiagnosticIds.BorrowAcrossAwait);
            }
            MirFunction mir = MirLowerer.Lower(function, compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations, cancellationToken);
            HashSet<TypeSymbol> escaping = analyzer._analysis.Escaping(mir with { Entry = mir.ResumableBodyEntry ?? mir.Entry });
            foreach (TypeSymbol error in escaping)
                    MirDiagnosticReporter.Report(diagnostics, mir, mir.Source,
                        error == BuiltinTypes.Error
                            ? "resumable function has an exception of unknown type; handle it before completion"
                            : $"resumable return type has no suitable operator reject for escaping exception '{error.ToDisplayString()}'",
                        DiagnosticIds.MissingCompletionOperator, error.ToDisplayString());
        }
        return diagnostics.ToImmutableArray();
    }

    internal static IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>> InferEffects(
        IEnumerable<BoundFunction> functions, TypeFactory types, CancellationToken cancellationToken)
    {
        var analyzer = new ResumableExceptionAnalyzer();
        foreach (var function in functions) analyzer._bodies[function.Symbol] = function.Body;
        analyzer.ComputeEffects(types, cancellationToken);
        return analyzer._effects;
    }

    private void ComputeEffects(TypeFactory types, CancellationToken cancellationToken)
    {
        var bodies = new List<MirFunction>();
        foreach (var (symbol, body) in _bodies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TypeIdentity.AreSame(symbol.ReturnType, BuiltinTypes.Error) ||
                symbol.Parameters.Any(parameter => TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void))) continue;
            var function = new BoundFunction(symbol, body);
            bodies.Add(body.IsResumable ? MirLowerer.LowerResumableInitialization(function, types, cancellation: cancellationToken) :
                MirLowerer.Lower(function, types, cancellation: cancellationToken, diagnosticRecovery: true));
        }
        _analysis = new MirExceptionEffects(bodies, cancellationToken);
        foreach (var (symbol, effects) in _analysis.Infer()) _effects[symbol] = effects;
    }
    private void AddCompilation(Compilation compilation)
    {
        foreach (BoundFunction function in compilation.SemanticModel.Functions) _bodies.TryAdd(function.Symbol, function.Body);
        foreach (CompilationReference reference in compilation.References)
            if (reference is SourceCompilationReference source) AddCompilation(source.Compilation);
            else if (reference is LibraryCompilationReference library)
                foreach (BoundFunction function in library.ImplementationFunctions) _bodies.TryAdd(function.Symbol, function.Body);
    }

}
