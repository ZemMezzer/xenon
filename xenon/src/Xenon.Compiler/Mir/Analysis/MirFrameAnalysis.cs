using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirSuspensionState(ImmutableHashSet<MirLocalId> Live,
    ImmutableHashSet<MirBlockId> Allocations);

/// <summary>Storage retained at each suspension, including transitive borrowed owners.</summary>
public sealed class MirFrameAnalysis
{
    public MirDataflowResult<ImmutableHashSet<MirLocalId>> Liveness { get; }
    public ImmutableDictionary<MirBlockId, MirSuspensionState> Suspensions { get; }
    public ImmutableHashSet<MirLocalId> LiveAcross { get; }

    public PinTypeSymbol CreateFrameType(MirFunction function, TypeFactory types)
    {
        var frame = new StructTypeSymbol(function.Symbol.Name + ".frame", function.Symbol.ContainingNamespace,
            false, SymbolOrigin.CompilerGenerated, accessibility: Accessibility.Private);
        frame.SetFields(function.Locals.Where(local => LiveAcross.Contains(local.Id)).OrderBy(local => local.Id.Value)
            .Select((local, ordinal) => new FieldSymbol(local.Name + "." + local.Id.Value, frame, local.Type, ordinal,
                Accessibility.Private, false, false, false, false, null)).ToImmutableArray());
        return types.PinOf(frame);
    }
    public MirFrameAnalysis(MirFunction function, CancellationToken cancellation = default)
    {
        var graph = MirFlowFacts.Graph(function, cancellation);
        Liveness = MirDataflow.Solve(graph, new MirLiveness(), cancellation);
        var reaching = MirDataflow.Solve(graph, new MirStorageOrigins(function), cancellation);
        var suspensions = ImmutableDictionary.CreateBuilder<MirBlockId, MirSuspensionState>();
        foreach (MirBasicBlock block in function.Blocks)
        {
            cancellation.ThrowIfCancellationRequested();
            if (block.Terminator is not MirSuspend || !graph.Reachable.Contains(block.Id)) continue;
            var state = reaching.Output[block.Id];
            var live = Liveness.Output[block.Id].ToHashSet();
            var allocations = ImmutableHashSet.CreateBuilder<MirBlockId>();
            var queue = new Queue<MirLocalId>(live);
            while (queue.TryDequeue(out MirLocalId local))
            {
                if (!state.TryGetValue(local, out var origin)) continue;
                allocations.UnionWith(origin.Allocations);
                foreach (MirLocalId owner in origin.Owners)
                    if (live.Add(owner)) queue.Enqueue(owner);
            }
            suspensions.Add(block.Id, new(live.ToImmutableHashSet(), allocations.ToImmutable()));
        }
        Suspensions = suspensions.ToImmutable();
        LiveAcross = Suspensions.Values.SelectMany(state => state.Live).ToImmutableHashSet();
    }
}