using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirLifetimeAnalysis
{
    private Dictionary<MirLocalId, HashSet<MirReferenceOrigin>>? _automaticRawOwners;
    private MirDataflowResult<ImmutableDictionary<MirPlace, MirInitialization>>? _rawOwnerMoves;
    private MirLifetimeValue BorrowOperand(MirOperand operand, MirLifetimeState state)
    {
        MirLifetimeValue value = Operand(operand, state);
        if (!value.BorrowedStorage.IsEmpty || operand.Type is ReferenceTypeSymbol or ArrayTypeSymbol) return value;
        return operand switch
        {
            MirCopy copy => Borrow(copy.Place, state),
            MirMove move => Borrow(move.Place, state),
            _ => MirLifetimeValue.Empty,
        };
    }

    private MirLifetimeValue RawStorage(MirOperand operand)
    {
        EnsureRawOrigins();
        EnsureAutomaticRawOwners();
        var rawState = _rawValues!.Before[_location];
        var value = _rawOrigins!.Operand(operand, rawState);
        return MirLifetimeValue.Union(RawRoots(value, operand.Type).Select(root =>
        {
            // A raw view does not keep a unique owner alive. Recover the
            // current owning carrier from allocation provenance, including
            // aliases created before an ownership move. Unknown heap pointers
            // intentionally acquire no invented owner dependency.
            var owners = _locals.Values.Where(local => local.Variable is not null &&
                rawState.TryGetValue(local.Id, out var owned) &&
                (UniqueLeaves(local.Type, owned, new(local.Id), []).Any(candidate =>
                    SameAllocation(candidate.Root, root) && MayOwn(candidate.Place)) ||
                 _automaticRawOwners!.TryGetValue(local.Id, out var cleaned) && cleaned.Any(candidate => SameAllocation(candidate, root)) &&
                 owned.Roots.Any(candidate => SameAllocation(candidate, root))))
                .Select(local => MirLifetimeValue.Local(_owners[local.Id])).ToArray();
            if (owners.Length != 0) return MirLifetimeValue.Union(owners);
            int? parameter = root.HandleParameter ?? (root.Kind == MirReferenceOriginKind.Parameter ? root.Ordinal : null);
            if (parameter is { } ordinal)
                return MirLifetimeValue.Empty with { Dependencies = [new(null, new(LifetimeDependencyKind.ParameterBorrow, ordinal,
                    _function.Symbol.Parameters[ordinal].Type is PointerTypeSymbol ? "" : "*"))] };
            return root.Kind == MirReferenceOriginKind.Local && _owners.TryGetValue(new(root.Ordinal), out var owner)
                ? MirLifetimeValue.Local(owner) : MirLifetimeValue.Empty;
        }));
    }

    private bool MayOwn(MirPlace place)
    {
        // Provenance intentionally survives moves so existing raw aliases keep
        // their allocation identity. Ownership does not: use the established
        // projection-sensitive move analysis to discard moved-from carriers.
        _rawOwnerMoves ??= MirMoveAnalysis.Analyze(_function, _cancellation);
        var initialization = _rawOwnerMoves.Before[_location].GetValueOrDefault(place);
        return (initialization & MirInitialization.Initialized) != 0 &&
            (initialization & MirInitialization.Reserved) == 0;
    }

    private void EnsureAutomaticRawOwners()
    {
        if (_automaticRawOwners is not null) return;
        _automaticRawOwners = [];
        if (!_function.HasScopeExitCleanup) return;
        var copies = _function.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>()
            .Where(assign => assign.Destination.Projections.IsEmpty)
            .ToLookup(assign => assign.Destination.Local);
        foreach (var block in _function.Blocks.Where(block => _graph.Reachable.Contains(block.Id)))
        {
            if (block.Terminator is not MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Free, Source.IsAutomaticCleanup: true } free ||
                free.Arguments.Length == 0) continue;
            var value = _rawOrigins!.Operand(free.Arguments[0], _rawValues!.Output[block.Id]);
            foreach (var owner in Carriers(free.Arguments[0], []))
            {
                if (!_automaticRawOwners.TryGetValue(owner, out var roots)) _automaticRawOwners[owner] = roots = [];
                roots.UnionWith(value.Roots);
            }
        }

        IEnumerable<MirLocalId> Carriers(MirOperand operand, HashSet<MirLocalId> seen)
        {
            if (operand is not MirCopy { Place: { Projections.IsEmpty: true } place } || !seen.Add(place.Local)) yield break;
            if (_locals[place.Local].Variable is not null)
            {
                yield return place.Local;
                yield break;
            }
            foreach (var assignment in copies[place.Local])
            {
                MirOperand? input = assignment.Value switch { MirUse use => use.Operand, MirCast cast => cast.Operand, _ => null };
                if (input is not null)
                    foreach (var carrier in Carriers(input, seen)) yield return carrier;
            }
        }
    }

    private static bool SameAllocation(MirReferenceOrigin left, MirReferenceOrigin right) =>
        left.Kind == right.Kind && left.Ordinal == right.Ordinal && left.HandleIdentity == right.HandleIdentity &&
        left.AllocationSite == right.AllocationSite && left.AllocationContext == right.AllocationContext &&
        left.PriorInstance == right.PriorInstance && left.Kind is not (MirReferenceOriginKind.Unknown or MirReferenceOriginKind.Static);

    private static IEnumerable<(MirPlace Place, MirReferenceOrigin Root)> UniqueLeaves(TypeSymbol type,
        MirReferenceValue value, MirPlace place, HashSet<TypeSymbol> seen)
    {
        while (type is LifetimeModifierTypeSymbol modifier)
        {
            type = modifier.ElementType;
            place = place.Project(new MirLifetimeProjection());
        }
        if (type is UniqueTypeSymbol)
        {
            foreach (var root in value.Roots) yield return (place, root);
        }
        else if (type is IFieldStorageTypeSymbol structure && seen.Add(type))
        {
            foreach (var field in structure.AllInstanceFields)
                foreach (var root in UniqueLeaves(field.Type, value.Project(field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    place.Project(new MirFieldProjection(field)), seen))
                    yield return root;
            seen.Remove(type);
        }
    }

    private MirLifetimeValue Map(LifetimeDependency dependency, MirCall call, MirLifetimeState state,
        ImmutableArray<MirLifetimeValue> captures)
    {
        MirLifetimeValue value = dependency.Kind switch
        {
            LifetimeDependencyKind.ParameterValue when dependency.Ordinal >= 0 && dependency.Ordinal < call.Arguments.Length =>
                Operand(call.Arguments[dependency.Ordinal], state).AsValue(
                    call.Arguments[dependency.Ordinal].Type is ReferenceTypeSymbol reference ? reference.ElementType : call.Arguments[dependency.Ordinal].Type),
            LifetimeDependencyKind.ParameterBorrow when dependency.Ordinal >= 0 && dependency.Ordinal < call.Arguments.Length =>
                call.Arguments[dependency.Ordinal].Type is PointerTypeSymbol || dependency.FieldPath == "*" &&
                    MirPointerLifetimeAnalysis.CarriesPointer(call.Arguments[dependency.Ordinal].Type) ? RawStorage(call.Arguments[dependency.Ordinal]) :
                    BorrowOperand(call.Arguments[dependency.Ordinal], state),
            LifetimeDependencyKind.ReceiverValue when call.Receiver is MirCopy receiver => Read(receiver.Place.Project(new MirDerefProjection()), state),
            LifetimeDependencyKind.ReceiverBorrow when call.Receiver is { } receiver => BorrowOperand(receiver, state),
            LifetimeDependencyKind.CaptureValue or LifetimeDependencyKind.CaptureBorrow when dependency.Ordinal >= 0 && dependency.Ordinal < captures.Length =>
                captures[dependency.Ordinal],
            _ => MirLifetimeValue.Empty,
        };
        if (dependency.FieldPath.Length != 0)
            foreach (string field in dependency.FieldPath.Split('/')) value = value.Project(field);
        return value;
    }

    private MirLifetimeState Call(MirCall call, MirLifetimeState state, MirLocation location)
    {
        FunctionSymbol? function = call.Callee switch
        {
            MirFunctionOperand direct => direct.Function,
            MirRequirementOperand { Requirement: FunctionSymbol requirement } => requirement,
            _ => null,
        };
        MirLifetimeValue value;
        if (function is not null)
            (state, value) = Invoke(function, call, state, location, []);
        else
        {
            MirLifetimeValue callable = Operand(call.Callee, state);
            value = MirLifetimeValue.Empty;
            if (!callable.Callables.IsEmpty)
                foreach (var (target, captures) in callable.Callables)
                {
                    var invocation = Invoke(target, call, state, location, captures);
                    state = invocation.State;
                    value = value.Union(invocation.Value);
                }
            else
            {
                value = callable.Union(MirLifetimeValue.Union(call.Arguments.Select(argument => Operand(argument, state))));
                if (call.Callee.Type is FunctionValueTypeSymbol signature && MirLifetimeValue.Carries(signature.ReturnType) && !value.Dependencies.IsEmpty)
                    (state, value) = Start(value, state, location);
            }
        }
        if (call.Destination is { } destination)
            state = Write(destination, value, state, location, call.Source, check: false);
        return state;
    }

    private (MirLifetimeState State, MirLifetimeValue Value) Invoke(FunctionSymbol function, MirCall call,
        MirLifetimeState state, MirLocation location, ImmutableArray<MirLifetimeValue> captures)
    {
        MirLifetimeValue value = MirLifetimeValue.Union(function.ResultLifetimeDependencies
            .Where(dependency => function.FunctionKind != FunctionKind.Constructor || dependency.Kind != LifetimeDependencyKind.ReceiverBorrow)
            .Select(dependency => Map(dependency, call, state, captures)));
        if (function.ReturnType is ReferenceTypeSymbol)
            foreach (ReferenceReturnOrigin origin in function.ReferenceReturnOrigins)
                if (origin.Kind == ReferenceReturnOriginKind.Parameter && origin.ParameterOrdinal >= 0 && origin.ParameterOrdinal < call.Arguments.Length)
                    value = value.Union(BorrowOperand(call.Arguments[origin.ParameterOrdinal], state));
                else if (origin.Kind == ReferenceReturnOriginKind.Receiver && call.Receiver is { } receiver)
                    value = value.Union(BorrowOperand(receiver, state));

        foreach (LifetimeStore store in function.LifetimeStores)
        {
            // References initialized to another field of the same object are
            // carried by that object's storage, not by a constructor temporary.
            if (store.Destination == -1 && store.Source.Kind == LifetimeDependencyKind.ReceiverBorrow &&
                function.FunctionKind is FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer) continue;
            MirLifetimeValue stored = Map(store.Source, call, state, captures);
            MirOperand? target = store.Destination == -1 ? call.Receiver :
                store.Destination >= 0 && store.Destination < call.Arguments.Length ? call.Arguments[store.Destination] : null;
            if (target is MirCopy address)
            {
                MirPlace place = address.Place.Project(new MirDerefProjection());
                TypeSymbol type = target.Type is ReferenceTypeSymbol reference ? reference.ElementType :
                    target.Type is PointerTypeSymbol pointer ? pointer.ElementType : target.Type;
                foreach (string component in store.FieldPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (type is not IFieldStorageTypeSymbol structure || !int.TryParse(component, out int ordinal) ||
                        structure.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal) is not { } field) break;
                    place = place.Project(new MirFieldProjection(field)); type = field.Type;
                }
                state = Write(place, stored, state, location, call.Source);
            }
            else if (_collect)
                foreach (ValueLifetimeDependency dependency in stored.Dependencies)
                    if (dependency.Input is { } input) _stores.Add(new(-2, input, store.FieldPath));
                    else if (dependency.LocalOwner is { } owner)
                        Report(call.Source, DiagnosticIds.ValueLifetimeEscape,
                            $"value assigned to 'external storage' depends on local '{owner.Name}', which does not outlive the destination");
        }
        if (function.ReturnType is not ReferenceTypeSymbol) value = value with { BorrowedStorage = [] };
        if (function.CreatesResumableOperation) value = value with { Operations = [], Fields = MirLifetimeValue.Empty.Fields };
        bool erasedOperation = call.InterfaceType is not null && function.ReturnType is StructTypeSymbol operationType &&
            operationType.Methods.Any(method => method.OperatorKind == OperatorKind.Resolve);
        if (call.InterfaceType is not null)
        {
            MirLifetimeValue inputs = MirLifetimeValue.Union(call.Arguments.Select(argument => Operand(argument, state)));
            if (call.Receiver is { } receiver)
                inputs = inputs.Union(erasedOperation ? BorrowOperand(receiver, state) : Operand(receiver, state));
            value = value.Union(erasedOperation ? inputs : inputs.AsValue(function.ReturnType));
        }
        if ((function.ReturnsResumableOperation || erasedOperation) && !value.Dependencies.IsEmpty)
            (state, value) = Start(value, state, location);
        if (_function.Symbol.IsAsync && function.OperatorKind == OperatorKind.Resolve && call.Arguments.Length == 2)
            state = Return(Operand(call.Arguments[1], state), state, call.Source);
        return (state, value);
    }

    private static (MirLifetimeState State, MirLifetimeValue Value) Start(MirLifetimeValue value, MirLifetimeState state, MirLocation location)
    {
        value = value with { Operations = value.Operations.Add(location) };
        return (state with { Pending = state.Pending.SetItem(location, value) }, value);
    }

    private MirLifetimeState Intrinsic(MirIntrinsicCall call, MirLifetimeState state, MirLocation location)
    {
        MirLifetimeValue value = MirLifetimeValue.Empty;
        switch (call.Intrinsic)
        {
            case MirIntrinsicKind.Swap when call.Arguments is [MirCopy left, MirCopy right]:
                MirPlace leftPlace = left.Place.Project(new MirDerefProjection()), rightPlace = right.Place.Project(new MirDerefProjection());
                MirLifetimeValue leftValue = Read(leftPlace, state), rightValue = Read(rightPlace, state);
                state = Write(leftPlace, rightValue, state, location, call.Source, check: false);
                state = Write(rightPlace, leftValue, state, location, call.Source, check: false);
                break;
            case MirIntrinsicKind.MakeCallable:
                ImmutableArray<MirLifetimeValue> captures = [.. call.Arguments.Select((argument, index) =>
                    index < call.Captures.Length && call.Captures[index].IsBorrow ? BorrowOperand(argument, state) : Operand(argument, state))];
                value = MirLifetimeValue.Union(captures);
                if (call.Function is { } function) value = value with { Callables = value.Callables.SetItem(function, captures) };
                break;
            case MirIntrinsicKind.CloneValue or MirIntrinsicKind.AdoptUnique or MirIntrinsicKind.AdoptShared or
                MirIntrinsicKind.ConvertWeak or MirIntrinsicKind.LockWeak or MirIntrinsicKind.SharedPayload or
                MirIntrinsicKind.AtomicLoad or MirIntrinsicKind.AtomicExchange:
                value = MirLifetimeValue.Union(call.Arguments.Select(argument => Operand(argument, state)));
                break;
            case MirIntrinsicKind.AllocateStackArray when call.Destination is { } array:
                var owner = new ValueLifetimeDependency(_owners[array.Local], null);
                value = MirLifetimeValue.Local(_owners[array.Local]) with { StackArrayBacking = [owner], ArrayStorage = [array.Local] };
                break;
            case MirIntrinsicKind.AllocateHeapArray when call.Destination is { } heapArray:
                value = MirLifetimeValue.Empty with { ArrayStorage = [heapArray.Local] };
                break;
            case MirIntrinsicKind.AtomicInitialize or MirIntrinsicKind.AtomicStore when call.Arguments.Length >= 2 && call.Arguments[0] is MirCopy address:
                value = Operand(call.Arguments[1], state);
                state = Write(address.Place.Project(new MirDerefProjection()), value, state, location, call.Source);
                break;
        }
        if (call.Destination is { } destination)
            state = Write(destination, value, state, location, call.Source, check: false);
        return state;
    }
}
