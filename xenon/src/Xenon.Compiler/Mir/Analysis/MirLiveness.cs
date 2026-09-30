using System.Collections.Immutable;

namespace Xenon.Compiler.Mir.Analysis;

/// <summary>
/// Backward storage liveness including unwind and resume edges. A call result
/// kills the previous local only on its normal edge. Projected writes retain
/// their base address and index operands; they never kill an entire aggregate.
/// </summary>
public sealed class MirLiveness : IMirDataflowAnalysis<ImmutableHashSet<MirLocalId>>
{
    public MirDataflowDirection Direction => MirDataflowDirection.Backward;
    public ImmutableHashSet<MirLocalId> Bottom => [];
    public ImmutableHashSet<MirLocalId> Boundary(MirControlFlow graph, MirBasicBlock block) => [];
    public ImmutableHashSet<MirLocalId> Join(IEnumerable<ImmutableHashSet<MirLocalId>> states) =>
        states.Aggregate(Bottom, (result, state) => result.Union(state));
    public bool Same(ImmutableHashSet<MirLocalId> left, ImmutableHashSet<MirLocalId> right) => left.SetEquals(right);

    public static MirDataflowResult<ImmutableHashSet<MirLocalId>> Analyze(MirFunction function,
        CancellationToken cancellation = default) => MirDataflow.Solve(new MirControlFlow(function), new MirLiveness(), cancellation);

    public ImmutableHashSet<MirLocalId> Statement(MirStatement statement, ImmutableHashSet<MirLocalId> state)
    {
        switch (statement)
        {
            case MirAssign assign:
                state = Write(assign.Destination, state);
                state = Use(MirOperands.Of(assign.Value), state);
                return assign.Value switch
                {
                    MirBorrow borrow => Use(borrow.Place, state),
                    MirStorageState storage => Use(storage.Place, state),
                    _ => state,
                };
            case MirStorageLive live: return state.Remove(live.Local);
            case MirStorageDead dead: return state.Remove(dead.Local);
            case MirForget: return state;
            case MirInitializeDispatch dispatch: return Use(dispatch.Place, state);
            case MirSetStorageState storage: return Use(storage.Place, state);
            case MirReleaseException release: return Use([release.Record], state);
            case MirStackRestore restore: return Use([restore.Token], state);
            default: throw new InvalidOperationException($"Unknown MIR statement {statement.GetType().Name}.");
        }
    }

    public ImmutableHashSet<MirLocalId> Terminator(MirTerminator terminator, ImmutableHashSet<MirLocalId> state)
    {
        state = Use(MirOperands.Of(terminator), state);
        return terminator is MirDrop drop ? Use(drop.Place, state) : state;
    }

    public ImmutableHashSet<MirLocalId> Edge(MirBasicBlock source, MirEdge edge, ImmutableHashSet<MirLocalId> state)
    {
        MirPlace? destination = source.Terminator switch
        {
            MirCall call => call.Destination,
            MirIntrinsicCall intrinsic => intrinsic.Destination,
            _ => null,
        };
        return edge.Kind == MirEdgeKind.Normal && destination is not null ? Write(destination, state) : state;
    }

    private static ImmutableHashSet<MirLocalId> Write(MirPlace place, ImmutableHashSet<MirLocalId> state) =>
        place.Projections.IsEmpty ? state.Remove(place.Local) : Use(place, state);
    private static ImmutableHashSet<MirLocalId> Use(MirPlace place, ImmutableHashSet<MirLocalId> state) =>
        Use(MirOperands.Indices(place), state.Add(place.Local));
    private static ImmutableHashSet<MirLocalId> Use(IEnumerable<MirOperand> values, ImmutableHashSet<MirLocalId> state) =>
        state.Union(values.SelectMany(MirOperands.Places).Select(place => place.Local));
}
