using System.Collections.Immutable;

namespace Xenon.Compiler.Mir;

/// <summary>The shared graph over explicit normal, unwind and resume edges.</summary>
public sealed class MirControlFlow
{
    public MirFunction Function { get; }
    public ImmutableDictionary<MirBlockId, MirBasicBlock> Blocks { get; }
    public ImmutableDictionary<MirBlockId, ImmutableArray<MirEdge>> Successors { get; }
    public ImmutableDictionary<MirBlockId, ImmutableArray<(MirBlockId Source, MirEdgeKind Kind)>> Predecessors { get; }
    public ImmutableHashSet<MirBlockId> Reachable { get; }

    public MirControlFlow(MirFunction function, Func<MirBasicBlock, MirEdge, bool>? includeEdge = null)
    {
        Function = function;
        Blocks = function.Blocks.ToImmutableDictionary(block => block.Id);
        Successors = function.Blocks.ToImmutableDictionary(block => block.Id,
            block => block.Terminator.Successors.Where(edge => includeEdge?.Invoke(block, edge) ?? true).Distinct().ToImmutableArray());
        var predecessors = function.Blocks.ToDictionary(block => block.Id,
            _ => ImmutableArray.CreateBuilder<(MirBlockId Source, MirEdgeKind Kind)>());
        foreach (var (source, edges) in Successors)
            foreach (MirEdge edge in edges) predecessors[edge.Target].Add((source, edge.Kind));
        Predecessors = predecessors.ToImmutableDictionary(pair => pair.Key, pair => pair.Value.ToImmutable());
        var reachable = ImmutableHashSet.CreateBuilder<MirBlockId>();
        var pending = new Stack<MirBlockId>();
        pending.Push(function.Entry);
        while (pending.TryPop(out MirBlockId current))
        {
            if (!reachable.Add(current)) continue;
            foreach (MirEdge edge in Successors[current]) pending.Push(edge.Target);
        }
        Reachable = reachable.ToImmutable();
    }
}
