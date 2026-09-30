using System.Collections.Immutable;

namespace Xenon.Compiler.Mir.Analysis;

public enum MirDataflowDirection { Forward, Backward }
public readonly record struct MirLocation(MirBlockId Block, int Statement);

/// <summary>
/// Transfers are pure; states are immutable. Join and transfer must be monotone
/// over a finite-height lattice. Edge transfer receives state in analysis order:
/// predecessor output for forward analyses, successor input for backward ones.
/// </summary>
public interface IMirDataflowAnalysis<T>
{
    MirDataflowDirection Direction { get; }
    T Bottom { get; }
    T Boundary(MirControlFlow graph, MirBasicBlock block);
    T Join(IEnumerable<T> states);
    bool Same(T left, T right);
    T Statement(MirStatement statement, T state);
    T Terminator(MirTerminator terminator, T state);
    T Edge(MirBasicBlock source, MirEdge edge, T state);
}

public sealed record MirDataflowResult<T>(
    MirControlFlow Graph,
    ImmutableDictionary<MirBlockId, T> Input,
    ImmutableDictionary<MirBlockId, T> Output,
    ImmutableDictionary<MirLocation, T> Before,
    ImmutableDictionary<MirLocation, T> After);

public static class MirDataflow
{
    public static MirDataflowResult<T> Solve<T>(MirControlFlow graph, IMirDataflowAnalysis<T> analysis,
        CancellationToken cancellation = default)
    {
        var input = graph.Blocks.Keys.ToDictionary(id => id, _ => analysis.Bottom);
        var output = graph.Blocks.Keys.ToDictionary(id => id, _ => analysis.Bottom);
        bool forward = analysis.Direction == MirDataflowDirection.Forward;
        var queue = new Queue<MirBlockId>(graph.Function.Blocks.Select(block => block.Id)
            .Where(graph.Reachable.Contains));
        var queued = queue.ToHashSet();
        while (queue.TryDequeue(out MirBlockId id))
        {
            cancellation.ThrowIfCancellationRequested();
            queued.Remove(id);
            MirBasicBlock block = graph.Blocks[id];
            IEnumerable<T> incoming = forward
                ? graph.Predecessors[id].Where(edge => graph.Reachable.Contains(edge.Source))
                    .Select(edge => analysis.Edge(graph.Blocks[edge.Source], new(id, edge.Kind), output[edge.Source]))
                : graph.Successors[id].Select(edge => analysis.Edge(block, edge, input[edge.Target]));
            T joined = analysis.Join(incoming.Prepend(analysis.Boundary(graph, block)));
            T transferred = Transfer(block, joined, null, null);
            bool changed = forward
                ? !analysis.Same(input[id], joined) || !analysis.Same(output[id], transferred)
                : !analysis.Same(output[id], joined) || !analysis.Same(input[id], transferred);
            if (forward) { input[id] = joined; output[id] = transferred; }
            else { output[id] = joined; input[id] = transferred; }
            if (!changed) continue;
            IEnumerable<MirBlockId> affected = forward
                ? graph.Successors[id].Select(edge => edge.Target)
                : graph.Predecessors[id].Select(edge => edge.Source);
            foreach (MirBlockId target in affected)
                if (graph.Reachable.Contains(target) && queued.Add(target)) queue.Enqueue(target);
        }

        var before = ImmutableDictionary.CreateBuilder<MirLocation, T>();
        var after = ImmutableDictionary.CreateBuilder<MirLocation, T>();
        foreach (MirBasicBlock block in graph.Function.Blocks.Where(block => graph.Reachable.Contains(block.Id)))
            Transfer(block, forward ? input[block.Id] : output[block.Id], before, after);
        return new(graph, input.ToImmutableDictionary(), output.ToImmutableDictionary(), before.ToImmutable(), after.ToImmutable());

        T Transfer(MirBasicBlock block, T state, ImmutableDictionary<MirLocation, T>.Builder? before,
            ImmutableDictionary<MirLocation, T>.Builder? after)
        {
            if (forward)
            {
                for (int index = 0; index <= block.Statements.Length; index++)
                {
                    var location = new MirLocation(block.Id, index);
                    if (before is not null) before[location] = state;
                    state = index == block.Statements.Length ? analysis.Terminator(block.Terminator, state)
                        : analysis.Statement(block.Statements[index], state);
                    if (after is not null) after[location] = state;
                }
            }
            else
            {
                for (int index = block.Statements.Length; index >= 0; index--)
                {
                    var location = new MirLocation(block.Id, index);
                    if (after is not null) after[location] = state;
                    state = index == block.Statements.Length ? analysis.Terminator(block.Terminator, state)
                        : analysis.Statement(block.Statements[index], state);
                    if (before is not null) before[location] = state;
                }
            }
            return state;
        }
    }
}
