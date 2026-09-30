using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirLifetimeAnalysis
{
    public MirLifetimeState Statement(MirStatement statement, MirLifetimeState state)
    {
        if (!state.Reachable) return state;
        MirLocation location = _location = _statements[statement];
        switch (statement)
        {
            case MirAssign assign:
                foreach (MirMove move in MirOperands.Of(assign.Value).OfType<MirMove>())
                    foreach (MirPlace place in Resolve(move.OwnershipPlace ?? move.Place, location))
                        CheckPending(_owners[place.Local], state, assign.Source);
                MirLifetimeValue value = Value(assign.Value, state);
                MirLocal target = _locals[assign.Destination.Local];
                if (assign.IsDeclaration && target.Type is ReferenceTypeSymbol)
                {
                    var extended = value.BorrowedStorage.Where(dependency => dependency.LocalOwner is { } owner &&
                        _locals.Values.Any(local => local.Kind == MirLocalKind.Temporary && ReferenceEquals(_owners[local.Id], owner))).ToHashSet();
                    if (extended.Count != 0)
                    {
                        var owner = new ValueLifetimeDependency(_owners[target.Id], null);
                        value = value with
                        {
                            Dependencies = value.Dependencies.Except(extended).Add(owner),
                            BorrowedStorage = value.BorrowedStorage.Except(extended).Add(owner),
                        };
                    }
                }
                if (!MirLifetimeValue.Carries(assign.Value.Type) && assign.Value is not MirBorrow &&
                    !(assign.Value.Type is PointerTypeSymbol && target.Kind == MirLocalKind.Temporary))
                    value = MirLifetimeValue.Empty;
                // Pointers denote storage edges. Expanding their pointee fields
                // would recursively inline linked cleanup/history structures.
                if (assign.Value.Type is PointerTypeSymbol)
                    value = value with { Fields = MirLifetimeValue.Empty.Fields, Callables = MirLifetimeValue.Empty.Callables };
                return Write(assign.Destination, value, state, location, assign.Source, check: assign.IsSemanticWrite || target.Variable is not null);
            case MirCompleteOperation complete:
                MirLifetimeValue completed = Operand(complete.Operation, state);
                state = state with { Pending = state.Pending.RemoveRange(completed.Operations) };
                if (complete.Result is { } result && MirLifetimeValue.CarriesBorrow(_locals[result.Local].Type))
                    state = Write(result, completed with { Operations = [], Fields = MirLifetimeValue.Empty.Fields },
                        state, location, complete.Source, check: false);
                return state;
            case MirStorageLive live:
                return state with { Values = state.Values.Remove(live.Local) };
            case MirStorageDead dead:
                CheckPending(_owners[dead.Local], state, dead.Source);
                return state with { Values = state.Values.Remove(dead.Local) };
            default:
                return state;
        }
    }

    public MirLifetimeState Terminator(MirTerminator terminator, MirLifetimeState state)
    {
        if (!state.Reachable) return state;
        _location = _terminators[terminator];
        switch (terminator)
        {
            case MirDrop drop:
                foreach (MirPlace place in Resolve(drop.Place, _terminators[terminator]))
                    CheckPending(_owners[place.Local], state, drop.Source);
                break;
            case MirSuspend:
                if (_collect && _frame?.Suspensions.TryGetValue(_terminators[terminator].Block, out var suspension) == true)
                    foreach (MirLocalId local in suspension.Live)
                    {
                        if (_locals[local].Kind == MirLocalKind.Receiver) _returns.Add(new(LifetimeDependencyKind.ReceiverBorrow));
                        foreach (ValueLifetimeDependency dependency in state.Values.GetValueOrDefault(local, MirLifetimeValue.Empty).Dependencies)
                            if (dependency.Input is { } input) _returns.Add(input);
                    }
                break;
            case MirReturn returned:
                if (!_function.Symbol.IsAsync && returned.Value is { } result)
                    state = Return(Operand(result, state), state, returned.Source);
                EscapePending(state, returned.Source);
                break;
            case MirResumeUnwind:
                EscapePending(state, terminator.Source);
                break;
        }
        return state;
    }

    private MirLifetimeState Return(MirLifetimeValue value, MirLifetimeState state, MirSourceInfo source)
    {
        if (_collect && MirLifetimeValue.Carries(_function.Symbol.ReturnType))
        {
            foreach (ValueLifetimeDependency dependency in value.Dependencies)
                if (dependency.Input is { } input) _returns.Add(input);
                else if (dependency.LocalOwner is { } owner)
                    Report(source, DiagnosticIds.ValueLifetimeEscape,
                        $"returned value retains a borrow of local '{owner.Name}', which dies before the returned operation");
            ReturnsOperation |= !value.Operations.IsEmpty;
        }
        return state with { Pending = state.Pending.RemoveRange(value.Operations) };
    }

    private void EscapePending(MirLifetimeState state, MirSourceInfo source)
    {
        if (!_collect) return;
        foreach (MirLifetimeValue value in state.Pending.Values)
            foreach (ValueLifetimeDependency dependency in value.Dependencies)
                if (dependency.Input is { } input) _stores.Add(new(-2, input));
                else if (dependency.LocalOwner is { } owner) CheckPending(owner, state, source);
    }

    public MirLifetimeState Edge(MirBasicBlock source, MirEdge edge, MirLifetimeState state)
    {
        if (!state.Reachable || edge.Kind != MirEdgeKind.Normal) return state;
        MirLocation location = _location = _terminators[source.Terminator];
        return source.Terminator switch
        {
            MirCall call => Call(call, state, location),
            MirIntrinsicCall intrinsic => Intrinsic(intrinsic, state, location),
            _ => state,
        };
    }
}