using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

[Flags]
public enum MirInitialization { None = 0, Uninitialized = 1, Initialized = 2, Moved = 4, PartiallyMoved = 8, Reserved = 16 }
public enum MirOwnershipStatus { Uninitialized, Initialized, Moved, PartiallyMoved, MaybeInitialized, MaybeMoved }

/// <summary>
/// Projection-sensitive may-state lattice. Each bit denotes a possible incoming
/// state, so joining branches never makes a conditional initialization definite.
/// The place universe is finite and derived before the fixed-point iteration.
/// </summary>
public sealed class MirMoveAnalysis : IMirDataflowAnalysis<ImmutableDictionary<MirPlace, MirInitialization>>
{
    private readonly MirFunction _function;
    private readonly ImmutableHashSet<MirPlace> _places;
    private readonly Dictionary<MirPlace, ImmutableArray<MirPlace>> _fields = [];
    private readonly MirStorageOrigins _origins;
    private readonly MirDataflowResult<ImmutableDictionary<MirLocalId, MirStorageOrigin>> _originStates;
    private readonly Dictionary<MirStatement, ImmutableDictionary<MirLocalId, MirStorageOrigin>> _before = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MirTerminator, ImmutableDictionary<MirLocalId, MirStorageOrigin>> _terminators = new(ReferenceEqualityComparer.Instance);
    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public ImmutableDictionary<MirPlace, MirInitialization> Bottom => ImmutableDictionary<MirPlace, MirInitialization>.Empty;

    public MirMoveAnalysis(MirFunction function, CancellationToken cancellation = default)
    {
        _function = function;
        _origins = new(function);
        var reaching = MirDataflow.Solve(new MirControlFlow(function), _origins, cancellation);
        _originStates = reaching;
        foreach (MirBasicBlock block in function.Blocks.Where(block => reaching.Graph.Reachable.Contains(block.Id)))
        {
            for (int index = 0; index < block.Statements.Length; index++)
                _before[block.Statements[index]] = reaching.Before[new(block.Id, index)];
            _terminators[block.Terminator] = reaching.Before[new(block.Id, block.Statements.Length)];
        }
        var places = new HashSet<MirPlace>();
        foreach (MirLocal local in function.Locals)
        {
            Add(new(local.Id), local.Type, []);
            if (local.Kind == MirLocalKind.Receiver && local.Type is PointerTypeSymbol pointer)
                Add(new MirPlace(local.Id).Project(new MirDerefProjection()), pointer.ElementType, []);
        }
        foreach (MirBasicBlock block in function.Blocks)
        {
            foreach (MirStatement statement in block.Statements)
            {
                if (statement is MirAssign assign)
                {
                    Track(assign.Destination);
                    if (_before.TryGetValue(assign, out var origins))
                        foreach (MirPlace resolved in _origins.Resolve(assign.Destination, origins)) Track(resolved);
                    foreach (MirPlace place in MirOperands.Of(assign.Value).SelectMany(MirOperands.Places)) Track(place);
                    if (assign.Value is MirBorrow borrow) Track(borrow.Place);
                }
                if (statement is MirForget forget) Track(forget.Place);
            }
            foreach (MirPlace place in MirOperands.Of(block.Terminator).SelectMany(MirOperands.Places)) Track(place);
            if (block.Terminator is MirDrop drop) Track(drop.Place);
            if (Destination(block.Terminator) is { } destination) Track(destination);
        }
        _places = places.ToImmutableHashSet();

        void Track(MirPlace place)
        {
            for (int length = 0; length <= place.Projections.Length; length++)
                places.Add(new(place.Local, place.Projections.Take(length).ToImmutableArray()));
        }
        void Add(MirPlace place, TypeSymbol type, HashSet<TypeSymbol> ancestors)
        {
            places.Add(place);
            if (!ancestors.Add(type)) return;
            if (type is IFieldStorageTypeSymbol structure)
            {
                var children = structure.AllInstanceFields.Select(field => place.Project(new MirFieldProjection(field))).ToImmutableArray();
                if (!children.IsEmpty) _fields[place] = children;
                foreach (FieldSymbol field in structure.AllInstanceFields)
                    Add(place.Project(new MirFieldProjection(field)), field.Type, new(ancestors));
            }
            else if (type is LifetimeModifierTypeSymbol modifier)
            {
                MirPlace child = place.Project(new MirLifetimeProjection());
                if (type is not StorageTypeSymbol) _fields[place] = [child];
                Add(child, modifier.ElementType, new(ancestors));
            }
        }
    }

    public static MirDataflowResult<ImmutableDictionary<MirPlace, MirInitialization>> Analyze(MirFunction function,
        CancellationToken cancellation = default) =>
        MirDataflow.Solve(new(function), new MirMoveAnalysis(function, cancellation), cancellation);

    public ImmutableDictionary<MirPlace, MirInitialization> Boundary(MirControlFlow graph, MirBasicBlock block)
    {
        if (block.Id != _function.Entry) return Bottom;
        var parameters = _function.Locals.Where(local =>
            local.Kind is MirLocalKind.Parameter or MirLocalKind.Receiver or MirLocalKind.Capture)
            .Select(local => local.Id).ToHashSet();
        var state = _places.ToImmutableDictionary(place => place, place =>
            parameters.Contains(place.Local) ? MirInitialization.Initialized : MirInitialization.Uninitialized);
        if (_function.Symbol.FunctionKind is FunctionKind.Constructor or FunctionKind.InstanceInitializer &&
            _function.Symbol.ContainingStruct is { } owner)
            foreach (MirLocal receiver in _function.Locals.Where(local => local.Kind == MirLocalKind.Receiver))
                foreach (FieldSymbol field in owner.Fields)
                    state = Set(new MirPlace(receiver.Id).Project(new MirDerefProjection())
                        .Project(new MirFieldProjection(field)), MirInitialization.Uninitialized, state);
        return state;
    }

    public ImmutableDictionary<MirPlace, MirInitialization> Join(
        IEnumerable<ImmutableDictionary<MirPlace, MirInitialization>> states)
    {
        var result = Bottom.ToBuilder();
        foreach (var state in states)
            foreach (var (place, value) in state)
                result[place] = result.GetValueOrDefault(place) | value;
        return result.ToImmutable();
    }

    public bool Same(ImmutableDictionary<MirPlace, MirInitialization> left,
        ImmutableDictionary<MirPlace, MirInitialization> right) =>
        left.Count == right.Count && left.All(pair => right.GetValueOrDefault(pair.Key) == pair.Value);

    public MirOwnershipStatus Status(MirPlace place, ImmutableDictionary<MirPlace, MirInitialization> state)
    {
        MirInitialization value = state.GetValueOrDefault(place);
        if (value.HasFlag(MirInitialization.PartiallyMoved)) return MirOwnershipStatus.PartiallyMoved;
        if (value.HasFlag(MirInitialization.Reserved)) return MirOwnershipStatus.Moved;
        // A deferred argument move reserves the source until argument evaluation
        // succeeds. Its ancestors are unavailable as complete values, but their
        // real initialization state must survive an exceptional rollback.
        if (state.Any(pair => !pair.Key.Equals(place) && place.IsPrefixOf(pair.Key) &&
            pair.Value.HasFlag(MirInitialization.Reserved))) return MirOwnershipStatus.PartiallyMoved;
        return value switch
        {
            MirInitialization.Initialized => MirOwnershipStatus.Initialized,
            MirInitialization.Moved => MirOwnershipStatus.Moved,
            MirInitialization.Uninitialized or MirInitialization.None => MirOwnershipStatus.Uninitialized,
            _ when value.HasFlag(MirInitialization.Moved) => MirOwnershipStatus.MaybeMoved,
            _ => MirOwnershipStatus.MaybeInitialized,
        };
    }

    private ImmutableDictionary<MirPlace, MirInitialization> Set(MirPlace place, MirInitialization value,
        ImmutableDictionary<MirPlace, MirInitialization> state)
    {
        if (state.IsEmpty) return state; // No reachable predecessor has supplied a state yet.
        foreach (MirPlace target in _places.Where(place.IsPrefixOf)) state = state.SetItem(target, value);
        foreach (var (parent, children) in _fields.Where(pair => !pair.Key.Equals(place) && pair.Key.IsPrefixOf(place))
            .OrderByDescending(pair => pair.Key.Projections.Length))
        {
            MirInitialization[] values = children.Select(child => state.GetValueOrDefault(child)).ToArray();
            MirInitialization merged = values.Aggregate(MirInitialization.None, (result, child) => result | child);
            if (merged.HasFlag(MirInitialization.Moved))
                merged |= MirInitialization.PartiallyMoved;
            state = state.SetItem(parent, merged);
        }
        return state;
    }

    public IEnumerable<MirPlace> Resolve(MirPlace place, MirLocation location) =>
        _originStates.Before.TryGetValue(location, out var origins) ? _origins.Resolve(place, origins) : [place];

    public IEnumerable<MirPlace> Resolve(MirPlace place, MirStatement statement) =>
        _before.TryGetValue(statement, out var origins) ? _origins.Resolve(place, origins) : [place];

    private ImmutableDictionary<MirPlace, MirInitialization> Write(MirPlace place, MirInitialization value,
        ImmutableDictionary<MirPlace, MirInitialization> state,
        ImmutableDictionary<MirLocalId, MirStorageOrigin>? origins)
    {
        MirPlace[] targets = origins is null ? [place] : _origins.Resolve(place, origins).ToArray();
        foreach (MirPlace target in targets)
            state = Set(target, targets.Length == 1 ? value : state.GetValueOrDefault(target) | value, state);
        return state;
    }

    private ImmutableDictionary<MirPlace, MirInitialization> Consume(IEnumerable<MirOperand> operands,
        ImmutableDictionary<MirPlace, MirInitialization> state,
        ImmutableDictionary<MirLocalId, MirStorageOrigin>? origins = null)
    {
        foreach (MirMove move in operands.OfType<MirMove>())
            state = Write(move.OwnershipPlace ?? move.Place, MirInitialization.Moved, state, origins);
        return state;
    }

    public ImmutableDictionary<MirPlace, MirInitialization> Statement(MirStatement statement,
        ImmutableDictionary<MirPlace, MirInitialization> state) => statement switch
    {
        MirAssign assign => Assign(assign, state),
        MirSetStorageState storage => Set(storage.Place.Project(new MirLifetimeProjection()),
            storage.Initialized ? MirInitialization.Initialized : MirInitialization.Uninitialized, state),
        MirForget forget => Set(forget.Place, MirInitialization.Moved, state),
        MirStorageLive live => Set(new(live.Local), _function.Resumable?.Result == live.Local
            ? MirInitialization.Initialized : MirInitialization.Uninitialized, state),
        MirStorageDead dead => Set(new(dead.Local), MirInitialization.Uninitialized, state),
        _ => state,
    };

    private ImmutableDictionary<MirPlace, MirInitialization> Assign(MirAssign assign,
        ImmutableDictionary<MirPlace, MirInitialization> state)
    {
        var origins = _before.GetValueOrDefault(assign);
        state = Consume(MirOperands.Of(assign.Value), state, origins);
        state = Write(assign.Destination, MirInitialization.Initialized, state, origins);
        if (assign.Value is MirDefault)
            state = DefaultStorage(assign.Destination, assign.Value.Type, state, []);
        if (assign.ReservedMove is { } reserved)
            foreach (MirPlace place in _places.Where(reserved.IsPrefixOf))
                state = state.SetItem(place, state.GetValueOrDefault(place) | MirInitialization.Reserved);
        return state;
    }

    private ImmutableDictionary<MirPlace, MirInitialization> DefaultStorage(MirPlace place, TypeSymbol type,
        ImmutableDictionary<MirPlace, MirInitialization> state, HashSet<TypeSymbol> visited)
    {
        if (!visited.Add(type)) return state;
        if (type is StorageTypeSymbol)
            return Set(place.Project(new MirLifetimeProjection()), MirInitialization.Uninitialized, state);
        if (type is PinTypeSymbol pin)
            return DefaultStorage(place.Project(new MirLifetimeProjection()), pin.ElementType, state, visited);
        if (type is IFieldStorageTypeSymbol structure)
            foreach (FieldSymbol field in structure.AllInstanceFields)
                state = DefaultStorage(place.Project(new MirFieldProjection(field)), field.Type, state, new(visited));
        return state;
    }
    public ImmutableDictionary<MirPlace, MirInitialization> Terminator(MirTerminator terminator,
        ImmutableDictionary<MirPlace, MirInitialization> state)
    {
        state = Consume(MirOperands.Of(terminator), state, _terminators.GetValueOrDefault(terminator));
        return terminator is MirDrop drop ? Set(drop.Place, MirInitialization.Moved, state) : state;
    }

    public ImmutableDictionary<MirPlace, MirInitialization> Edge(MirBasicBlock source, MirEdge edge,
        ImmutableDictionary<MirPlace, MirInitialization> state)
    {
        if (edge.Kind == MirEdgeKind.Unwind)
            return state.ToImmutableDictionary(pair => pair.Key, pair => pair.Value & ~MirInitialization.Reserved);
        if (edge.Kind != MirEdgeKind.Normal) return state;
        var origins = _terminators.GetValueOrDefault(source.Terminator);
        if (source.Terminator is MirCall { Callee: MirFunctionOperand { Function.FunctionKind: FunctionKind.Constructor },
            Receiver: MirCopy receiver })
            state = Write(receiver.Place.Project(new MirDerefProjection()), MirInitialization.Initialized, state, origins);
        if (source.Terminator is MirCall { Callee: MirFunctionOperand { Function.FunctionKind: FunctionKind.InstanceInitializer } initializer,
            Receiver: MirCopy initializedReceiver } && initializer.Function.ContainingStruct is { } owner)
            foreach (FieldSymbol field in owner.Fields.Where(field => field.HasInitializer))
                state = Write(initializedReceiver.Place.Project(new MirDerefProjection()).Project(new MirFieldProjection(field)),
                    MirInitialization.Initialized, state, origins);
        if (source.Terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.AtomicInitialize or MirIntrinsicKind.AtomicStore or
            MirIntrinsicKind.AtomicExchange or MirIntrinsicKind.AtomicUpdate, Arguments: [MirCopy address, ..] })
            state = Write(address.Place.Project(new MirDerefProjection()), MirInitialization.Initialized, state, origins);
        return Destination(source.Terminator) is { } destination
            ? Write(destination, MirInitialization.Initialized, state, origins) : state;
    }

    private static MirPlace? Destination(MirTerminator terminator) => terminator switch
    {
        MirCall call => call.Destination,
        MirIntrinsicCall intrinsic => intrinsic.Destination,
        _ => null,
    };
}