using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal static class MirStorageDiagnostics
{
    public static void Analyze(IEnumerable<BoundFunction> functions, TypeFactory types, DiagnosticBag diagnostics,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        var invalid = locations.Where(pair => errors.Any(error =>
            ReferenceEquals(pair.Value.Source, error.Location.Source) &&
            error.Location.Span.Start >= pair.Value.Span.Start && error.Location.Span.End <= pair.Value.Span.End))
            .Select(pair => pair.Key).ToHashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        foreach (var function in functions.DistinctBy(function => function.Symbol))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!BoundTree.DescendantsAndSelf(function.Body).Any(node => node is BoundStorageMoveExpression or BoundStorageConstructExpression or
                BoundLifetimeValueExpression { ModifierType: StorageTypeSymbol } or BoundExplicitDestructExpression { Target.Type: StorageTypeSymbol })) continue;
            var mir = MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true, invalidExpressions: invalid);
            var analysis = new MirStorageStateAnalysis(mir, cancellation);
            var flow = MirDataflow.Solve(MirFlowFacts.Graph(mir, cancellation), analysis, cancellation);
            var reported = new HashSet<(TextLocation, string)>();
            foreach (var block in mir.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
            {
                if (block.Terminator is not MirIntrinsicCall { StorageCheck: not MirStorageCheckPurpose.None } check) continue;
                var state = analysis.State(check, flow.Output[block.Id]);
                bool initialize = check.StorageCheck == MirStorageCheckPurpose.Initialize;
                if (state != (initialize ? MirStorageContent.Live : MirStorageContent.Empty)) continue;
                string id = initialize ? DiagnosticIds.StorageAlreadyInitialized :
                    check.StorageCheck == MirStorageCheckPurpose.Destruct ? DiagnosticIds.ExplicitDestructionRequiresLiveValue : DiagnosticIds.StorageNotInitialized;
                if (!reported.Add((check.Source.Location, id))) continue;
                string message = initialize ? "cannot initialize storage because it already contains a live value" :
                    check.StorageCheck == MirStorageCheckPurpose.Destruct ? "cannot invoke the destructor of empty storage" :
                    "cannot use storage before constructing its value";
                diagnostics.Report(check.Source.Location, message, id);
            }
        }
    }
}