using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

/// <summary>Immutable provenance lattice shared by lifetime contracts and escape checks.</summary>
public sealed record MirLifetimeValue(
    ImmutableHashSet<ValueLifetimeDependency> Dependencies,
    ImmutableHashSet<ValueLifetimeDependency> BorrowedStorage,
    ImmutableHashSet<ValueLifetimeDependency> StackArrayBacking,
    ImmutableHashSet<MirLocation> Operations,
    ImmutableDictionary<string, MirLifetimeValue> Fields,
    ImmutableDictionary<FunctionSymbol, ImmutableArray<MirLifetimeValue>> Callables)
{
    public ImmutableHashSet<MirLocalId> StorageOwners { get; init; } = [];
    public bool TransfersBacking { get; init; }
    public ImmutableHashSet<MirLocalId> ArrayStorage { get; init; } = [];
    public static MirLifetimeValue Empty { get; } = new([], [], [], [],
        ImmutableDictionary<string, MirLifetimeValue>.Empty,
        ImmutableDictionary<FunctionSymbol, ImmutableArray<MirLifetimeValue>>.Empty);

    public static MirLifetimeValue Input(LifetimeDependencyKind kind, int ordinal = -1)
    {
        var origin = new ValueLifetimeDependency(null, new(kind, ordinal));
        return Empty with
        {
            Dependencies = [origin],
            BorrowedStorage = kind is LifetimeDependencyKind.ParameterBorrow or LifetimeDependencyKind.ReceiverBorrow or
                LifetimeDependencyKind.CaptureBorrow ? [origin] : [],
        };
    }

    public static MirLifetimeValue Local(Symbol owner)
    {
        var origin = new ValueLifetimeDependency(owner, null);
        return Empty with { Dependencies = [origin], BorrowedStorage = [origin] };
    }

    public MirLifetimeValue Union(MirLifetimeValue other, bool includeBacking = true)
    {
        var fields = Fields.ToBuilder();
        foreach (var (field, value) in other.Fields)
            fields[field] = fields.TryGetValue(field, out var previous) ? previous.Union(value) : value;
        var callables = Callables.ToBuilder();
        foreach (var (function, captures) in other.Callables)
            callables[function] = callables.TryGetValue(function, out var previous)
                ? [.. previous.Zip(captures, (left, right) => left.Union(right))] : captures;
        return new(Dependencies.Union(other.Dependencies), BorrowedStorage.Union(other.BorrowedStorage),
            includeBacking ? StackArrayBacking.Union(other.StackArrayBacking) : StackArrayBacking,
            Operations.Union(other.Operations), fields.ToImmutable(), callables.ToImmutable()) { StorageOwners = StorageOwners.Union(other.StorageOwners), TransfersBacking = TransfersBacking || other.TransfersBacking, ArrayStorage = ArrayStorage.Union(other.ArrayStorage) };
    }

    public static MirLifetimeValue Union(IEnumerable<MirLifetimeValue> values) =>
        values.Aggregate(Empty, (result, value) => result.Union(value));

    public bool Same(MirLifetimeValue other) => StorageOwners.SetEquals(other.StorageOwners) && TransfersBacking == other.TransfersBacking && ArrayStorage.SetEquals(other.ArrayStorage) && Dependencies.SetEquals(other.Dependencies) &&
        BorrowedStorage.SetEquals(other.BorrowedStorage) && StackArrayBacking.SetEquals(other.StackArrayBacking) &&
        Operations.SetEquals(other.Operations) && Fields.Count == other.Fields.Count &&
        Fields.All(pair => other.Fields.TryGetValue(pair.Key, out var value) && pair.Value.Same(value)) &&
        Callables.Count == other.Callables.Count &&
        Callables.All(pair => other.Callables.TryGetValue(pair.Key, out var captures) &&
            pair.Value.Length == captures.Length && pair.Value.Zip(captures).All(pair => pair.First.Same(pair.Second)));

    public MirLifetimeValue AsValue(TypeSymbol type) => this with
    {
        Dependencies = CarriesBorrow(type) ? Dependencies : Dependencies.Except(BorrowedStorage),
        BorrowedStorage = [],
    };

    public MirLifetimeValue Project(string field)
    {
        if (Fields.TryGetValue(field, out var value)) return value;
        return this with
        {
            Fields = Empty.Fields, Operations = [], StackArrayBacking = [],
            Dependencies = Dependencies.Select(dependency =>
            {
                if (dependency.Input is not { Kind: LifetimeDependencyKind.ParameterValue or
                    LifetimeDependencyKind.ReceiverValue or LifetimeDependencyKind.CaptureValue } input) return dependency;
                string path = input.FieldPath;
                // Recursive generic/storage contracts have a finite widened path.
                path = field == "*" || path == "*" || path.Count(character => character == '/') >= 7
                    ? "*" : path.Length == 0 ? field : path + "/" + field;
                return dependency with { Input = input with { FieldPath = path } };
            }).ToImmutableHashSet(),
        };
    }

    public MirLifetimeValue WithField(string path, MirLifetimeValue value)
    {
        int separator = path.IndexOf('/');
        if (separator < 0) return Union(value, includeBacking: false) with { Fields = Fields.SetItem(path, value) };
        string field = path[..separator];
        MirLifetimeValue nested = Project(field).WithField(path[(separator + 1)..], value);
        return Union(value, includeBacking: false) with { Fields = Fields.SetItem(field, nested) };
    }

    public static bool Carries(TypeSymbol type) => type is not PointerTypeSymbol &&
        type is GenericParameterSymbol or StructTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol or
            FunctionValueTypeSymbol or InterfaceTypeSymbol or LifetimeModifierTypeSymbol or OwnershipTypeSymbol;

    public static bool CarriesBorrow(TypeSymbol type) => CarriesBorrow(type, []);
    private static bool CarriesBorrow(TypeSymbol type, HashSet<TypeSymbol> seen) =>
        type is ReferenceTypeSymbol or ArrayTypeSymbol or FunctionValueTypeSymbol ||
        type is LifetimeModifierTypeSymbol modifier && CarriesBorrow(modifier.ElementType, seen) ||
        type is IFieldStorageTypeSymbol structure && seen.Add(type) &&
            structure.AllInstanceFields.Any(field => CarriesBorrow(field.Type, seen));
}

public sealed record MirLifetimeState(bool Reachable,
    ImmutableDictionary<MirLocalId, MirLifetimeValue> Values,
    ImmutableDictionary<MirLocation, MirLifetimeValue> Pending)
{
    public static MirLifetimeState Empty { get; } = new(false,
        ImmutableDictionary<MirLocalId, MirLifetimeValue>.Empty,
        ImmutableDictionary<MirLocation, MirLifetimeValue>.Empty);
}