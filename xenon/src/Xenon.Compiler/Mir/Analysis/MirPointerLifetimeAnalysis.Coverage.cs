using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirPointerLifetimeAnalysis
{
    // When exit inference exhausts its SCC budget, close only the may-operation
    // dependencies. This reuses frozen provenance and never produces death/live
    // proofs. Guards widen here; unrelated parameters and fields remain separate.
    private static Dictionary<FunctionSymbol, ImmutableHashSet<MirLifetimeUncertainty>> ComponentCoverage(
        FunctionSymbol[] component, Dictionary<FunctionSymbol, MirFunction> bodies,
        IReadOnlyDictionary<FunctionSymbol, MirReferenceValue> returns,
        IReadOnlyDictionary<FunctionSymbol, MirLifetimeSummary> summaries, CancellationToken cancellation)
    {
        var effects = summaries.ToDictionary(pair => pair.Key, pair => pair.Value.Effects);
        var analyses = component.ToDictionary(symbol => symbol, symbol =>
            new MirPointerLifetimeAnalysis(bodies[symbol], returns, cancellation, effects, symbolicParameters: true));
        var coverage = new Dictionary<FunctionSymbol, ImmutableHashSet<MirLifetimeUncertainty>>();
        foreach (var symbol in component)
        {
            var analysis = analyses[symbol];
            var operations = analysis.SummarizeEffects(cancellation, summaries).Where(effect => effect.Phase == MirLifetimeEffectPhase.Operation);
            coverage[symbol] = operations.Select(effect => new MirLifetimeUncertainty(effect.Parameter, effect.Path,
                    effect.UnknownOffset, null, effect.Kind, "recursive-fixed-point-budget"))
                .Concat(analysis._uncertainties.Select(item => item with { Guard = null, Reason = "recursive-fixed-point-budget" })).ToImmutableHashSet();
        }
        bool changed;
        do
        {
            changed = false;
            foreach (var symbol in component)
            {
                cancellation.ThrowIfCancellationRequested();
                var analysis = analyses[symbol];
                var next = coverage[symbol].ToBuilder();
                foreach (var block in bodies[symbol].Blocks.Where(block => analysis._graph.Reachable.Contains(block.Id)))
                {
                    if (block.Terminator is not MirCall original) continue;
                    foreach (var call in analysis._origins.BoundCalls(original, analysis._provenance.Output[block.Id]))
                    {
                    if (DirectCallee(call) is not { } callee || !coverage.TryGetValue(callee, out var dependency)) continue;
                    foreach (var item in dependency)
                        foreach (var root in analysis._origins.MapPointerParameter(call.Arguments[item.Parameter], item.Path, analysis._provenance.Output[block.Id]).Roots)
                            if ((root.HandleParameter ?? (root.Kind == MirReferenceOriginKind.Parameter ? root.Ordinal : null)) is { } parameter)
                                next.Add(item with { Parameter = parameter, Path = root.Path, UnknownOffset = item.UnknownOffset || root.UnknownObjectOffset });
                            else if (root.Kind == MirReferenceOriginKind.Unknown)
                                next.UnionWith(MirLifetimeOperations.Coverage(symbol, null).Select(fallback => fallback with { Reason = "recursive-fixed-point-budget" }));
                    }
                }
                if (coverage[symbol].SetEquals(next)) continue;
                coverage[symbol] = next.ToImmutable();
                changed = true;
            }
        } while (changed);
        return coverage;
    }
}
