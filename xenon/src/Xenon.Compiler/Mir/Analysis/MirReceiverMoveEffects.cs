using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirReceiverMoveSummary(ImmutableArray<MirPlace> Moved, ImmutableArray<MirPlace> Inconsistent);

/// <summary>Caller-visible receiver state at successful MIR exits.</summary>
public static class MirReceiverMoveEffects
{
    public static MirReceiverMoveSummary Analyze(MirFunction function, CancellationToken cancellation = default)
    {
        var receiver = function.Locals.FirstOrDefault(local => local.Kind == MirLocalKind.Receiver);
        if (receiver is null || function.Symbol.FunctionKind is FunctionKind.Destructor or FunctionKind.DestructorGlue)
            return new([], []);
        var analysis = new MirMoveAnalysis(function, cancellation);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(function, cancellation), analysis, cancellation);
        var exits = function.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id) && block.Terminator is MirReturn)
            .Select(block => flow.Before[new(block.Id, block.Statements.Length)]).ToArray();
        if (exits.Length == 0) return new([], []);
        var moved = new List<MirPlace>();
        var inconsistent = new List<MirPlace>();
        foreach (MirPlace place in exits.SelectMany(state => state.Keys).Distinct()
            .Where(place => place.Local == receiver.Id && place.Projections.Length > 1 &&
                place.Projections[0] is MirDerefProjection &&
                place.Projections.Skip(1).All(projection => projection is MirFieldProjection))
            .OrderBy(place => place.Projections.Length))
        {
            if (moved.Concat(inconsistent).Any(parent => parent.IsPrefixOf(place))) continue;
            MirOwnershipStatus[] states = exits.Select(state => analysis.Status(place, state)).ToArray();
            if (states.All(state => state == MirOwnershipStatus.Moved)) moved.Add(place);
            else if (states.Any(state => state is MirOwnershipStatus.Moved or MirOwnershipStatus.MaybeMoved))
                inconsistent.Add(place);
        }
        return new([.. moved], [.. inconsistent]);
    }
}