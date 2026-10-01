using System.Collections.Immutable;
namespace Xenon.Compiler.Mir.Analysis;

/// <summary>Erases operations on infeasible MIR paths while preserving stable block identities.</summary>
public static class MirReachability
{
    public static MirFunction Prune(MirFunction function, CancellationToken cancellation = default)
    {
        var graph = MirFlowFacts.Graph(function, cancellation);
        var reachable = graph.Reachable;
        if (function.HasDynamicCleanupOrder)
        {
            var partitions = new MirScalarPartitions<MirFlowState>(graph, new MirFlowFacts(), cancellation);
            var flow = MirDataflow.Solve(graph, partitions, cancellation);
            reachable = reachable.Where(block => !flow.Input[block].IsEmpty).ToImmutableHashSet();
        }
        var result = function with
        {
            Blocks = [.. function.Blocks.Select(block => reachable.Contains(block.Id) ? block :
                new MirBasicBlock(block.Id, [], new MirUnreachable(block.Terminator.Source)))],
            Resumable = function.Resumable is { } resumable
                ? resumable with { Initialization = Prune(resumable.Initialization, cancellation) } : null,
        };
        MirVerifier.VerifyOrThrow(result);
        return result;
    }
}