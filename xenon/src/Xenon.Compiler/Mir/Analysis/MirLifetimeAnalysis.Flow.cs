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
                if (!MirLifetimeValue.Carries(assign.Value.Type) && assign.Value.Type is not AtomicTypeSymbol { ElementType: ArrayTypeSymbol } && assign.Value is not MirBorrow &&
                    !(assign.Value.Type is PointerTypeSymbol && target.Kind == MirLocalKind.Temporary))
                    value = MirLifetimeValue.Empty;
                // Pointers denote storage edges. Expanding their pointee fields
                // would recursively inline linked cleanup/history structures.
                if (assign.Value.Type is PointerTypeSymbol)
                    value = value with { Fields = MirLifetimeValue.Empty.Fields, Callables = MirLifetimeValue.Empty.Callables };
                return Write(assign.Destination, value, state, location, assign.Source, check: assign.IsSemanticWrite || target.Variable is not null, aggregateInitialization: assign.IsAggregateInitialization);
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
        if (_collect && terminator is MirCall call)
            foreach (MirOperand argument in call.Arguments)
                if (argument.Type is ArrayTypeSymbol && !Operand(argument, state).StackArrayBacking.IsEmpty)
                    Report(call.Source, call.Callee is MirFunctionOperand { Function.OperatorKind: OperatorKind.Resolve }
                        ? DiagnosticIds.StackArrayReturn : DiagnosticIds.StackArrayPassedAsArgument,
                        call.Callee is MirFunctionOperand { Function.OperatorKind: OperatorKind.Resolve }
                            ? "resumable completion cannot retain stack-backed array storage" : "stack array cannot be passed to another function");
        if (_collect && terminator is MirIntrinsicCall intrinsic)
        {
            if (intrinsic.Intrinsic == MirIntrinsicKind.Free && intrinsic.Arguments.Length > 0 &&
                intrinsic.Arguments[0].Type is ArrayTypeSymbol && !Operand(intrinsic.Arguments[0], state).StackArrayBacking.IsEmpty)
                Report(intrinsic.Source, DiagnosticIds.StackArrayFree, "stack array cannot be freed");
            if (intrinsic.Intrinsic is MirIntrinsicKind.CompareExchange or MirIntrinsicKind.CompareExchangeOwned &&
                intrinsic.Arguments.Length > 2 && !Operand(intrinsic.Arguments[2], state).StackArrayBacking.IsEmpty)
                Report(intrinsic.Source, DiagnosticIds.StackArrayEscape, "stack array cannot escape into an atomic array handle through compare-exchange");
            if (intrinsic.Intrinsic == MirIntrinsicKind.Swap && intrinsic.Arguments.Length == 2)
                for (int index = 0; index < 2; index++)
                    if (intrinsic.Arguments[index].Type is PointerTypeSymbol { ElementType: AtomicTypeSymbol { ElementType: ArrayTypeSymbol } } &&
                        intrinsic.Arguments[1 - index] is MirCopy opposite && !Read(opposite.Place.Project(new MirDerefProjection()), state).StackArrayBacking.IsEmpty)
                        Report(intrinsic.Source, DiagnosticIds.StackArrayEscape, "stack array cannot escape into an atomic array handle through swap");
        }
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
            if (!value.StackArrayBacking.IsEmpty && _function.Symbol.ReturnType is ArrayTypeSymbol)
                Report(source, DiagnosticIds.StackArrayReturn, "cannot return-move this value because its backing storage belongs to the current function's stack frame and cannot outlive the function call; use heap-backed or caller-owned storage");
            foreach (ValueLifetimeDependency dependency in (_function.Symbol.ReturnType is ArrayTypeSymbol ? value.Dependencies.Except(value.StackArrayBacking) : value.Dependencies))
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