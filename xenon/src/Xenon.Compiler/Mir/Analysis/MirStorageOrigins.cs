using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirStorageOrigin(ImmutableHashSet<MirLocalId> Owners,
    ImmutableHashSet<int> Allocations, ImmutableHashSet<MirPlace> Addresses)
{
    public static MirStorageOrigin Empty { get; } = new([], [], []);
    public MirStorageOrigin Union(MirStorageOrigin other) =>
        new(Owners.Union(other.Owners), Allocations.Union(other.Allocations), Addresses.Union(other.Addresses));
    public bool Same(MirStorageOrigin other) => Owners.SetEquals(other.Owners) &&
        Allocations.SetEquals(other.Allocations) && Addresses.SetEquals(other.Addresses);
}

/// <summary>Reaching storage origins for frame selection, including aliases and backing allocations.</summary>
public sealed class MirStorageOrigins(MirFunction function) :
    IMirDataflowAnalysis<ImmutableDictionary<MirLocalId, MirStorageOrigin>>
{

    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public ImmutableDictionary<MirLocalId, MirStorageOrigin> Bottom => ImmutableDictionary<MirLocalId, MirStorageOrigin>.Empty;
    public ImmutableDictionary<MirLocalId, MirStorageOrigin> Boundary(MirControlFlow graph, MirBasicBlock block) =>
        block.Id == graph.Function.Entry ? function.Locals
            .Where(local => local.Kind is MirLocalKind.Parameter or MirLocalKind.Receiver or MirLocalKind.Capture)
            .Where(local => CarriesStorage(local.Type))
            .ToImmutableDictionary(local => local.Id, local => new MirStorageOrigin([local.Id], [], [])) : Bottom;

    public static bool CarriesStorage(TypeSymbol type) => CarriesStorage(type, []);
    private static bool CarriesStorage(TypeSymbol type, HashSet<TypeSymbol> visited) =>
        type is PointerTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol or FunctionValueTypeSymbol or GenericParameterSymbol ||
        type is LifetimeModifierTypeSymbol modifier && CarriesStorage(modifier.ElementType, visited) ||
        type is IFieldStorageTypeSymbol structure && visited.Add(type) &&
            structure.AllInstanceFields.Any(field => CarriesStorage(field.Type, visited));

    public ImmutableDictionary<MirLocalId, MirStorageOrigin> Join(IEnumerable<ImmutableDictionary<MirLocalId, MirStorageOrigin>> states)
    {
        var result = Bottom.ToBuilder();
        foreach (var state in states)
            foreach (var (local, origin) in state)
                result[local] = result.TryGetValue(local, out var previous) ? previous.Union(origin) : origin;
        return result.ToImmutable();
    }

    public bool Same(ImmutableDictionary<MirLocalId, MirStorageOrigin> left, ImmutableDictionary<MirLocalId, MirStorageOrigin> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value.Same(value));

    public MirStorageOrigin Read(MirPlace place, ImmutableDictionary<MirLocalId, MirStorageOrigin> state) =>
        Resolve(place, state, []).Select(target => state.GetValueOrDefault(target.Local, MirStorageOrigin.Empty))
            .Aggregate(MirStorageOrigin.Empty, (result, origin) => result.Union(origin));

    public IEnumerable<MirPlace> Resolve(MirPlace place, ImmutableDictionary<MirLocalId, MirStorageOrigin> state) =>
        Resolve(place, state, []);

    private IEnumerable<MirPlace> Resolve(MirPlace place, ImmutableDictionary<MirLocalId, MirStorageOrigin> state, HashSet<MirLocalId> seen)
    {
        int dereference = -1;
        for (int i = 0; i < place.Projections.Length; i++)
            if (place.Projections[i] is MirDerefProjection) { dereference = i; break; }
        if (dereference < 0 || !seen.Add(place.Local) ||
            !state.TryGetValue(place.Local, out var origin) || origin.Addresses.IsEmpty)
            return [place];
        return origin.Addresses.SelectMany(target => Resolve(
            new(target.Local, target.Projections.AddRange(place.Projections.Skip(dereference + 1))),
            state, new(seen))).ToArray();
    }

    private MirStorageOrigin Operand(MirOperand value, ImmutableDictionary<MirLocalId, MirStorageOrigin> state) =>
        MirOperands.Places(value).Select(place => Read(place, state))
            .Aggregate(MirStorageOrigin.Empty, (result, origin) => result.Union(origin));

    private MirStorageOrigin Value(MirRValue value, ImmutableDictionary<MirLocalId, MirStorageOrigin> state)
    {
        if (!CarriesStorage(value.Type)) return MirStorageOrigin.Empty;
        if (value is MirBorrow borrow)
        {
            MirPlace[] addresses = Resolve(borrow.Place, state, []).ToArray();
            MirStorageOrigin contents = Read(borrow.Place, state);
            return new(contents.Owners.Union(addresses.Select(place => place.Local)),
                contents.Allocations, addresses.ToImmutableHashSet());
        }
        return MirOperands.Of(value).Select(operand => Operand(operand, state))
            .Aggregate(MirStorageOrigin.Empty, (result, origin) => result.Union(origin));
    }

    private ImmutableDictionary<MirLocalId, MirStorageOrigin> Write(MirPlace place, MirStorageOrigin origin,
        ImmutableDictionary<MirLocalId, MirStorageOrigin> state)
    {
        MirPlace[] targets = Resolve(place, state, []).ToArray();
        foreach (MirPlace target in targets)
        {
            bool strong = targets.Length == 1 && target.Projections.IsEmpty;
            MirStorageOrigin stored = strong ? origin : state.GetValueOrDefault(target.Local, MirStorageOrigin.Empty).Union(origin);
            state = stored.Same(MirStorageOrigin.Empty) ? state.Remove(target.Local) : state.SetItem(target.Local, stored);
        }
        return state;
    }

    public ImmutableDictionary<MirLocalId, MirStorageOrigin> Statement(MirStatement statement,
        ImmutableDictionary<MirLocalId, MirStorageOrigin> state) => statement switch
    {
        MirAssign assign => Write(assign.Destination, Value(assign.Value, state), state),
        MirStorageLive live => state.Remove(live.Local),
        MirStorageDead dead => state.Remove(dead.Local),
        _ => state,
    };

    public ImmutableDictionary<MirLocalId, MirStorageOrigin> Terminator(MirTerminator terminator,
        ImmutableDictionary<MirLocalId, MirStorageOrigin> state) => state;

    public ImmutableDictionary<MirLocalId, MirStorageOrigin> Edge(MirBasicBlock source, MirEdge edge,
        ImmutableDictionary<MirLocalId, MirStorageOrigin> state)
    {

        MirPlace? destination = source.Terminator switch
        {
            MirCall call => call.Destination,
            MirIntrinsicCall intrinsic => intrinsic.Destination,
            _ => null,
        };
        MirStorageOrigin origin = MirOperands.Of(source.Terminator).Select(operand => Operand(operand, state))
            .Aggregate(MirStorageOrigin.Empty, (result, item) => result.Union(item));
        if (source.Terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.AllocateStackArray or MirIntrinsicKind.AllocateHeapArray } allocation)
            origin = allocation.Source.OriginId is { } id ? new([], [id], []) : MirStorageOrigin.Empty;
        if (source.Terminator is MirCall called)
        {
            // Mutating reference arguments and constructor receivers can fill
            // borrowed storage with values supplied by another argument.
            foreach (MirOperand argument in called.Arguments
                .Where(value => value.Type is ReferenceTypeSymbol { IsReadonly: false })
                .Concat(called.Receiver is { } receiver ? [receiver] : []))
                foreach (MirPlace address in Operand(argument, state).Addresses)
                    // A mutation can retain any input owner, but its stored
                    // pointer is not proven to be the address of an out parameter.
                    state = Write(address, Read(address, state).Union(origin) with { Addresses = [] }, state);
        }
        if (edge.Kind == MirEdgeKind.Normal && destination is not null)
        {
            TypeSymbol resultType = source.Terminator switch
            {
                MirIntrinsicCall intrinsic => intrinsic.ResultType,
                MirCall call => call.Callee.Type switch
                {
                    FunctionPointerTypeSymbol pointer => pointer.ReturnType,
                    FunctionValueTypeSymbol callable => callable.ReturnType,
                    _ => BuiltinTypes.Error,
                },
                _ => BuiltinTypes.Error,
            };
            state = Write(destination, CarriesStorage(resultType) ? origin : MirStorageOrigin.Empty, state);
        }
        return state;
    }
}
