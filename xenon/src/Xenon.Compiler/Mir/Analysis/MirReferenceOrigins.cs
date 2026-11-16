using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public enum MirLifetimeAuthority { Owner, ReferenceParameter, StorageValue, ReferenceField }

public enum MirAllocationKind { Unknown, Raw, Typed }

public enum MirReferenceOriginKind { Local, Parameter, Receiver, Static, Unknown, RawPointee, UniquePointee, SharedPointee }
public sealed record MirAllocationSite(FunctionSymbol Function, MirLocalId Local);
public sealed record MirAllocationContext(MirAllocationSite CallSite, MirAllocationContext? Parent);

/// <summary>A flattened shared ancestor of an inline unique handle; paths widen finitely.</summary>
public sealed record MirSharedOwner(string? Identity, bool IsFresh, TypeSymbol? Type, string Path);

/// <summary>A storage origin and a finite projection path; independent of source syntax.</summary>
public sealed record MirReferenceOrigin(MirReferenceOriginKind Kind, int Ordinal = -1, string Path = "")
{
    // Used by the opt-in pointer lifetime flow. Death travels with each value,
    // rather than being joined independently from its possible allocation roots.
    public MirSourceInfo? DestroyedAt { get; init; }
    public MirLocation? DestroyedLocation { get; init; }
    public MirPointerCallDeath? CallDeath { get; init; }
    public bool StorageFreed { get; init; }
    public bool AutomaticLifetimeEnd { get; init; }
    public bool UnknownObjectOffset { get; init; }
    public bool PriorInstance { get; init; }
    public MirAllocationSite? AllocationSite { get; init; }
    public MirAllocationContext? AllocationContext { get; init; }
    public FieldSymbol? StaticField { get; init; }
    public MirAllocationKind AllocationKind { get; init; }
    public StructTypeSymbol? AllocatorType { get; init; }
    public MirLifetimeAuthority Authority { get; init; }
    public bool IsReadonly { get; init; }
    public bool IsFresh { get; init; }
    public int? HandleParameter { get; init; }
    public string? HandleIdentity { get; init; }
    public TypeSymbol? PointeeType { get; init; }
    public MirSharedOwner? SharedOwner { get; init; }
    public bool IsSafeReference => Kind is MirReferenceOriginKind.Parameter or MirReferenceOriginKind.Receiver or MirReferenceOriginKind.Static;
    public MirReferenceOrigin Project(string path) => this with { Path = Append(Path, path) };
    internal static string Append(string left, string right)
    {
        if (right.Length == 0) return left;
        var parts = (left.Length == 0 ? right : left + "/" + right).Split('/');
        // Keep the allocation and seven structural components. A terminal
        // wildcard at the depth bound denotes the remaining subtree, including
        // handle loads; later projections must not turn it back into an exact path.
        if (parts.Length > 8 || parts.Length >= 8 && parts[7] == "*")
            return string.Join('/', parts.Take(7)) + "/*";
        return string.Join('/', parts);
    }
    public ReferenceReturnOrigin Contract => new(Kind switch
    {
        MirReferenceOriginKind.Parameter => ReferenceReturnOriginKind.Parameter,
        MirReferenceOriginKind.Receiver => ReferenceReturnOriginKind.Receiver,
        MirReferenceOriginKind.Static => ReferenceReturnOriginKind.Static,
        _ => ReferenceReturnOriginKind.Unknown,
    }, Kind == MirReferenceOriginKind.Parameter ? Ordinal : -1,
        !IsSafeReference ? [] : Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => int.TryParse(part, out _)).Select(int.Parse).ToImmutableArray());
}

public sealed record MirCallableTarget(FunctionSymbol Function, MirLocalId Site);

public sealed record MirReferenceValue(ImmutableHashSet<MirReferenceOrigin> Roots,
    ImmutableDictionary<string, ImmutableHashSet<MirReferenceOrigin>> Fields)
{
    public ImmutableHashSet<MirLocalId> Aliases { get; init; } = [];
    public ImmutableHashSet<MirLocalId> Lineage { get; init; } = [];
    public ImmutableDictionary<string, ImmutableHashSet<MirCallableTarget>> CallableTargets { get; init; } =
        ImmutableDictionary<string, ImmutableHashSet<MirCallableTarget>>.Empty;
    public static MirReferenceValue Empty { get; } = new([], ImmutableDictionary<string, ImmutableHashSet<MirReferenceOrigin>>.Empty);
    public static MirReferenceValue Of(MirReferenceOrigin origin) => Empty with { Roots = [origin] };
    public MirReferenceValue Union(MirReferenceValue other)
    {
        var fields = Fields.ToBuilder();
        foreach (var (path, roots) in other.Fields) fields[path] = fields.GetValueOrDefault(path, []).Union(roots);
        var callables = CallableTargets.ToBuilder();
        foreach (var (path, targets) in other.CallableTargets) callables[path] = callables.GetValueOrDefault(path, []).Union(targets);
        return new(Roots.Union(other.Roots), fields.ToImmutable()) { Aliases = Aliases.Union(other.Aliases), Lineage = Lineage.Union(other.Lineage), CallableTargets = callables.ToImmutable() };
    }
    public bool Same(MirReferenceValue other) => ReferenceEquals(this, other) || Aliases.SetEquals(other.Aliases) && Lineage.SetEquals(other.Lineage) && Roots.SetEquals(other.Roots) && Fields.Count == other.Fields.Count &&
        Fields.All(pair => other.Fields.TryGetValue(pair.Key, out var roots) && roots.SetEquals(pair.Value)) &&
        CallableTargets.Count == other.CallableTargets.Count && CallableTargets.All(pair => other.CallableTargets.TryGetValue(pair.Key, out var targets) && targets.SetEquals(pair.Value));
    public MirReferenceValue Project(string path)
    {
        if (path.Length == 0) return this;
        var fields = Empty.Fields.ToBuilder();
        foreach (var (key, roots) in Fields)
            if (key.StartsWith(path + "/", StringComparison.Ordinal)) fields[key[(path.Length + 1)..]] = roots;
            else if (key.Contains('*'))
            {
                var pattern = key.Split('/'); var projection = path.Split('/');
                if (pattern.Length > projection.Length && pattern.Take(projection.Length).Zip(projection)
                    .All(pair => pair.First == "*" || pair.First == pair.Second))
                {
                    string suffix = string.Join('/', pattern.Skip(projection.Length));
                    fields[suffix] = fields.GetValueOrDefault(suffix, []).Union(roots).Add(new(MirReferenceOriginKind.Unknown));
                }
            }
        var projected = Fields.GetValueOrDefault(path, Roots.Select(root => root.Project(path)).ToImmutableHashSet());
        foreach (var (key, roots) in Fields.Where(pair => pair.Key.Contains('*') && Covers(pair.Key, path)))
        {
            // A widened stored payload must survive every subsequent projection
            // through its subtree. It is a may-set, including an unknown choice,
            // and cannot establish an exact raw pointee.
            projected = projected.Union(roots).Add(new(MirReferenceOriginKind.Unknown));
            fields["*"] = fields.GetValueOrDefault("*", []).Union(roots);
        }
        var callables = Empty.CallableTargets.ToBuilder();
        foreach (var (key, targets) in CallableTargets)
            if (key == path) callables[""] = callables.GetValueOrDefault("", []).Union(targets);
            else if (key.StartsWith(path + "/", StringComparison.Ordinal))
            {
                string suffix = key[(path.Length + 1)..];
                callables[suffix] = callables.GetValueOrDefault(suffix, []).Union(targets);
            }
            else if (key.Contains('*'))
            {
                if (Covers(key, path)) callables[""] = callables.GetValueOrDefault("", []).Union(targets);
                else
                {
                    var pattern = key.Split('/'); var projection = path.Split('/');
                    if (pattern.Length > projection.Length && pattern.Take(projection.Length).Zip(projection)
                        .All(pair => pair.First == "*" || pair.First == pair.Second))
                    {
                        string suffix = string.Join('/', pattern.Skip(projection.Length));
                        callables[suffix] = callables.GetValueOrDefault(suffix, []).Union(targets);
                    }
                }
            }
        return new(projected, fields.ToImmutable()) { Aliases = Aliases, Lineage = Lineage, CallableTargets = callables.ToImmutable() };
    }
    internal bool HasStoredPath(string path) => Fields.ContainsKey(path) || Fields.Keys.Any(key => key.Contains('*') && Covers(key, path));
    private static bool Covers(string pattern, string path)
    {
        var a = pattern.Split('/'); var b = path.Split('/');
        for (int i = 0; i < a.Length; i++)
        {
            if (i >= b.Length) return false;
            if (a[i] == "*" && i == a.Length - 1) return true;
            if (a[i] != "*" && a[i] != b[i]) return false;
        }
        return a.Length == b.Length;
    }
    public MirReferenceValue Store(string path, MirReferenceValue value, bool strong)
    {
        if (path.Length == 0) return strong ? value : Union(value);
        strong &= !path.Contains('*');
        var fields = Fields.ToBuilder();
        if (strong)
            foreach (string key in Fields.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal))) fields.Remove(key);
        fields[path] = strong ? value.Roots : fields.GetValueOrDefault(path, []).Union(value.Roots);
        foreach (var (key, roots) in value.Fields)
        {
            string projected = MirReferenceOrigin.Append(path, key);
            fields[projected] = strong ? roots : fields.GetValueOrDefault(projected, []).Union(roots);
        }
        var callables = CallableTargets.ToBuilder();
        if (strong)
            foreach (var key in CallableTargets.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal))) callables.Remove(key);
        foreach (var (key, targets) in value.CallableTargets)
        {
            string projected = MirReferenceOrigin.Append(path, key);
            callables[projected] = strong ? targets : callables.GetValueOrDefault(projected, []).Union(targets);
        }
        return this with { Fields = fields.ToImmutable(), Aliases = Aliases.Union(value.Aliases), Lineage = Lineage.Union(value.Lineage), CallableTargets = callables.ToImmutable() };
    }
}

/// <summary>Reference provenance over MIR assignments, calls, joins and explicit exits.</summary>
public sealed partial class MirReferenceOrigins(MirFunction function, bool pointerLifetimes = false,
    IReadOnlyDictionary<FunctionSymbol, MirReferenceValue>? pointerReturns = null, bool symbolicPointerParameters = false,
    bool checkedPointerIdentities = false) :
    IMirDataflowAnalysis<ImmutableDictionary<MirLocalId, MirReferenceValue>>
{
    private readonly Dictionary<MirLocalId, MirLocal> _locals = function.Locals.ToDictionary(local => local.Id);
    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Bottom => ImmutableDictionary<MirLocalId, MirReferenceValue>.Empty;
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Boundary(MirControlFlow graph, MirBasicBlock block)
    {
        if (block.Id != function.Entry) return Bottom;
        var state = Bottom.ToBuilder();
        foreach (MirLocal local in function.Locals)
            if (local.Variable is ParameterSymbol parameter)
            {
                var value = IsHandle(local.Type) ? Handle(local.Type, local.Id, "parameter", parameter: parameter.Ordinal) :
                    Carries(local.Type) ? MirReferenceValue.Of(new(MirReferenceOriginKind.Parameter, parameter.Ordinal)
                    {
                        IsReadonly = local.Type is ReferenceTypeSymbol { IsReadonly: true },
                        Authority = local.Type is ReferenceTypeSymbol reference && reference.ElementType is not StorageTypeSymbol
                            ? MirLifetimeAuthority.ReferenceParameter : MirLifetimeAuthority.Owner,
                    }) : MirReferenceValue.Empty;
                // By-value aggregates carry handle fields even when they contain no borrowed references.
                if (!IsHandle(local.Type)) value = value.Union(OwnedFields(local.Type, local.Id, "parameter-field", pointerLifetimes ? parameter.Ordinal : null));
                if (!value.Same(MirReferenceValue.Empty)) state[local.Id] = value;
            }
            else if (local.Kind == MirLocalKind.Receiver)
                state[local.Id] = MirReferenceValue.Of(new(MirReferenceOriginKind.Receiver));
            else if (local.Kind == MirLocalKind.Capture)
            {
                // The closure owns/carries these handles, while captured references
                // keep their unknown external storage origin.
                var value = IsHandle(local.Type) ? Handle(local.Type, local.Id, "capture") :
                    Carries(local.Type) ? MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)) : MirReferenceValue.Empty;
                if (!IsHandle(local.Type)) value = value.Union(OwnedFields(local.Type, local.Id, "capture-field"));
                if (!value.Same(MirReferenceValue.Empty)) state[local.Id] = value;
            }
        return state.ToImmutable();
    }
    private static bool Carries(TypeSymbol type) => type is PointerTypeSymbol or OwnershipTypeSymbol || MirLifetimeValue.CarriesBorrow(type);
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Join(IEnumerable<ImmutableDictionary<MirLocalId, MirReferenceValue>> states)
    {
        var inputs = states.ToArray();
        if (inputs.Length == 1) return inputs[0];
        var result = Bottom.ToBuilder();
        foreach (var state in inputs)
            foreach (var (local, value) in state) result[local] = result.TryGetValue(local, out var old) ? old.Union(value) : value;
        return result.ToImmutable();
    }
    public bool Same(ImmutableDictionary<MirLocalId, MirReferenceValue> left, ImmutableDictionary<MirLocalId, MirReferenceValue> right) =>
        ReferenceEquals(left, right) || left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value.Same(value));

    private MirLocalId? StorageKey(MirReferenceOrigin address) => address.Kind switch
    {
        MirReferenceOriginKind.Static when pointerLifetimes && address.StaticField is not null => StaticStorageKey(address.StaticField),
        MirReferenceOriginKind.RawPointee or MirReferenceOriginKind.UniquePointee or MirReferenceOriginKind.SharedPointee
            when pointerLifetimes && !address.PriorInstance => HeapStorageKey(address),
        MirReferenceOriginKind.Local => new(address.Ordinal),
        MirReferenceOriginKind.Receiver => new(-1),
        MirReferenceOriginKind.Parameter => new(-2 - address.Ordinal),
        _ => null,
    };
    private MirReferenceValue Load(MirReferenceOrigin address, ImmutableDictionary<MirLocalId, MirReferenceValue> state,
        TypeSymbol? type = null)
    {
        if (StorageKey(address) is { } key && state.TryGetValue(key, out var stored))
        {
            if (address.Path.Split('/').Contains("*"))
            {
                var pattern = address.Path.Split('/');
                var alternatives = MirReferenceValue.Empty;
                foreach (var path in stored.Fields.Keys)
                {
                    var parts = path.Split('/');
                    if (parts.Length < pattern.Length || !pattern.Zip(parts).All(pair => pair.First == "*" || pair.First == pair.Second)) continue;
                    alternatives = alternatives.Union(stored.Project(string.Join('/', parts.Take(pattern.Length))));
                }
                // An unknown index can also select an unrecorded element. It is
                // a may-alias set for loans, never an exact allocation proof.
                return alternatives.Union(MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)));
            }
            var value = stored.Project(address.Path);
            // An explicitly stored value already describes its pointee. An opaque field
            // inherits only its carrier's address, so derive the handle's own provenance.
            return type is not null && IsHandle(type) && address.Path.Length != 0 && !stored.HasStoredPath(address.Path)
                ? pointerLifetimes && !checkedPointerIdentities ? MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)) : OpaqueHandle(type, value) : value;
        }
        if (address.Kind == MirReferenceOriginKind.Local) return MirReferenceValue.Empty;
        var opaque = MirReferenceValue.Of(address);
        return type is not null && IsHandle(type) ? OpaqueHandle(type, opaque) : opaque;
    }
    private MirReferenceValue Contents(MirReferenceValue addresses, ImmutableDictionary<MirLocalId, MirReferenceValue> state) =>
        addresses.Roots.Aggregate(MirReferenceValue.Empty, (value, root) => value.Union(Load(root, state)));
    public ImmutableHashSet<MirReferenceOrigin> Address(MirPlace place, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        ImmutableHashSet<MirReferenceOrigin> roots = [new(MirReferenceOriginKind.Local, place.Local.Value)];
        TypeSymbol type = _locals[place.Local].Type;
        foreach (MirProjection projection in place.Projections)
        {
            if (projection is MirIndexProjection or MirLinearIndexProjection &&
                (type is PointerTypeSymbol || pointerLifetimes && type is ArrayTypeSymbol))
                roots = roots.SelectMany(root => Load(root, state, type).Roots).ToImmutableHashSet();
            roots = projection switch
            {
                MirDerefProjection => roots.SelectMany(root => Load(root, state, type).Roots).ToImmutableHashSet(),
                MirFieldProjection field => roots.Select(root => root.Project(field.Field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToImmutableHashSet(),
                MirIndexProjection or MirLinearIndexProjection => roots.Select(root => Index(root, projection)).ToImmutableHashSet(),
                _ => roots,
            };
            type = projection switch
            {
                MirDerefProjection when type is ReferenceTypeSymbol reference => reference.ElementType,
                MirDerefProjection when Element(type) is { } element => element,
                MirFieldProjection field => field.Field.Type,
                MirLifetimeProjection when type is LifetimeModifierTypeSymbol modifier => modifier.ElementType,
                MirIndexProjection or MirLinearIndexProjection when type is PointerTypeSymbol pointer => pointer.ElementType,
                MirIndexProjection or MirLinearIndexProjection when type is ArrayTypeSymbol array => array.ElementType,
                _ => type,
            };
        }
        return roots;
    }
    public MirReferenceValue Read(MirPlace place, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var value = Address(place, state).Aggregate(MirReferenceValue.Empty, (result, root) => result.Union(Load(root, state, PlaceType(place))));
        if (place.Projections.LastOrDefault() is MirFieldProjection { Field.Type: ReferenceTypeSymbol })
            value = Restrict(value, MirLifetimeAuthority.ReferenceField);
        if (_locals[place.Local].Variable is LocalVariableSymbol or ParameterSymbol && ReferenceLeaves(_locals[place.Local].Type).Any())
            value = value with { Aliases = [place.Local], Lineage = value.Lineage.Union(value.Aliases).Remove(place.Local) };
        if (_locals[place.Local].Kind == MirLocalKind.Temporary && _locals[place.Local].Type is ReferenceTypeSymbol && value.Aliases.IsEmpty)
            value = value with { Aliases = [place.Local] };
        return value;
    }
    public ImmutableHashSet<MirLocalId> ThroughAliases(MirPlace place, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        ImmutableHashSet<MirLocalId> aliases = [];
        var prefix = new MirPlace(place.Local);
        foreach (var projection in place.Projections)
        {
            if (projection is MirDerefProjection)
            {
                var value = Read(prefix, state);
                aliases = aliases.Union(value.Aliases).Union(value.Lineage);
            }
            prefix = prefix.Project(projection);
        }
        return aliases;
    }
    public MirReferenceValue Operand(MirOperand operand, ImmutableDictionary<MirLocalId, MirReferenceValue> state) => operand switch
    {
        MirConstant when pointerLifetimes && operand.Type is PointerTypeSymbol or ArrayTypeSymbol => MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)),
        MirCopy copy => Read(copy.Place, state),
        MirMove move => Read(move.Place, state),
        _ => MirReferenceValue.Empty,
    };
    private ImmutableDictionary<MirLocalId, MirReferenceValue> Write(MirPlace place, MirReferenceValue value,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var addresses = Address(place, state);
        int storageCount = pointerLifetimes ? addresses.Select(address => address with { DestroyedAt = null, DestroyedLocation = null, StorageFreed = false, AutomaticLifetimeEnd = false }).Distinct().Count() : addresses.Count;
        foreach (var address in addresses)
        {
            if (StorageKey(address) is not { } local) continue;
            var empty = address.Kind == MirReferenceOriginKind.Local ? MirReferenceValue.Empty : MirReferenceValue.Of(address with { Path = "" });
            state = state.SetItem(local, state.GetValueOrDefault(local, empty)
                .Store(address.Path, value, storageCount == 1 && !address.Path.Contains('*')));
        }
        return state;
    }
    private static MirReferenceValue Readonly(MirReferenceValue value) => value with { Roots = value.Roots.Select(origin => origin with { IsReadonly = true }).ToImmutableHashSet() };
    public MirReferenceValue Value(MirRValue value, ImmutableDictionary<MirLocalId, MirReferenceValue> state) => value switch
    {
        MirBorrow borrow => Borrow(borrow, state),
        MirStaticFieldAddress field => MirReferenceValue.Of(new(MirReferenceOriginKind.Static) { StaticField = pointerLifetimes ? field.Field : null }),
        MirUse use when pointerLifetimes && use.Type is not ReferenceTypeSymbol && !MirPointerLifetimeAnalysis.CarriesPointer(use.Type) => MirReferenceValue.Empty,
        MirUse use => Operand(use.Operand, state),
        MirCast cast => Cast(cast, state),
        MirAtomicValue atomic when pointerLifetimes => Operand(atomic.Value, state),
        MirDefault when pointerLifetimes && MirPointerLifetimeAnalysis.CarriesPointer(value.Type) => DefaultPointers(value.Type),
        MirBinary binary when pointerLifetimes && binary.Type is PointerTypeSymbol =>
            PointerArithmetic(binary, state),
        MirAggregate aggregate => aggregate.Fields.Select((operand, index) => (operand, index)).Aggregate(MirReferenceValue.Empty,
            (result, field) => result.Store(field.index.ToString(System.Globalization.CultureInfo.InvariantCulture), Operand(field.operand, state), true)),
        _ => MirReferenceValue.Empty,
    };
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Statement(MirStatement statement,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state) => statement switch
    {
        MirAssign assign => Write(assign.Destination,
            assign.Destination.Projections.IsEmpty && _locals[assign.Destination.Local].Variable is not null &&
                assign.Value.Type is PointerTypeSymbol or OwnershipTypeSymbol
                ? pointerLifetimes ? CheckedHandle(assign, state) : StoredHandle(assign.Value.Type, assign.Destination.Local, Value(assign.Value, state)) : Value(assign.Value, state), state),
        MirForget forget => Write(forget.Place, MirReferenceValue.Empty, state),
        MirSetStorageState { Initialized: false } storage => Write(storage.Place, MirReferenceValue.Empty, state),
        MirStorageLive live => state.Remove(live.Local),
        MirStorageDead dead => state.Remove(dead.Local),
        _ => state,
    };
    private MirReferenceValue CheckedHandle(MirAssign assign, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var value = Value(assign.Value, state);
        if (!checkedPointerIdentities || !value.Roots.Any(root => root.Kind == MirReferenceOriginKind.Unknown)) return value;
        // Identity of an established loan through this carrier, not proof of
        // allocation identity or of safety for arbitrary raw accesses.
        return value with { Roots = value.Roots.Where(root => root.Kind != MirReferenceOriginKind.Unknown).ToImmutableHashSet()
            .Union(Handle(assign.Value.Type, assign.Destination.Local, "checked-opaque").Roots) };
    }
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Terminator(MirTerminator terminator,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state) => state;

    private MirReferenceValue Map(ReferenceReturnOrigin origin, MirCall call, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        MirReferenceValue value = origin.Kind switch
        {
            ReferenceReturnOriginKind.Parameter when origin.ParameterOrdinal >= 0 && origin.ParameterOrdinal < call.Arguments.Length => Operand(call.Arguments[origin.ParameterOrdinal], state),
            ReferenceReturnOriginKind.Receiver when call.Receiver is { } receiver => Operand(receiver, state),
            ReferenceReturnOriginKind.Static => MirReferenceValue.Of(new(MirReferenceOriginKind.Static)),
            _ => MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)),
        };
        FunctionSymbol? callee = call.Callee switch { MirFunctionOperand direct => direct.Function, MirRequirementOperand { Requirement: FunctionSymbol requirement } => requirement, _ => null };
        TypeSymbol? type = origin.Kind == ReferenceReturnOriginKind.Receiver ? callee?.ContainingType :
            origin.Kind == ReferenceReturnOriginKind.Parameter && callee is not null && origin.ParameterOrdinal >= 0 && origin.ParameterOrdinal < callee.Parameters.Length ? callee.Parameters[origin.ParameterOrdinal].Type : null;
        bool address = origin.Kind == ReferenceReturnOriginKind.Receiver || type is ReferenceTypeSymbol;
        if (type is ReferenceTypeSymbol reference) type = reference.ElementType;
        bool storageValue = type is StorageTypeSymbol &&
            callee?.ReturnType is ReferenceTypeSymbol { ElementType: not StorageTypeSymbol };
        foreach (int ordinal in origin.FieldOrdinals)
        {
            while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
            FieldSymbol? field = (type as IFieldStorageTypeSymbol)?.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal);
            string path = ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            value = address ? value with { Roots = value.Roots.Select(root => root.Project(path)).ToImmutableHashSet() } : value.Project(path);
            type = field?.Type;
            if (type is ReferenceTypeSymbol fieldReference)
            {
                if (address) value = Contents(value, state);
                value = Restrict(value, MirLifetimeAuthority.ReferenceField);
                address = true;
                type = fieldReference.ElementType;
            }
        }
        return storageValue ? Restrict(value, MirLifetimeAuthority.StorageValue) : value;
    }
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Edge(MirBasicBlock source, MirEdge edge,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        if (edge.Kind != MirEdgeKind.Normal) return pointerLifetimes && source.Terminator is MirCall exceptionalCall
            ? PointerCallEffects(exceptionalCall, state) : state;
        if (source.Terminator is MirCall call)
        {
            FunctionSymbol? callee = call.Callee switch
            {
                MirFunctionOperand direct => direct.Function,
                MirRequirementOperand { Requirement: FunctionSymbol requirement } => requirement,
                _ => null,
            };
            MirReferenceValue result = MirReferenceValue.Empty;
            if (callee is not null)
            {
                foreach (var origin in callee.ReferenceReturnOrigins) result = result.Union(Map(origin, call, state));
                foreach (var field in callee.ReferenceFieldOrigins)
                    result = result.Store(string.Join('/', field.FieldOrdinals), field.IsReadonly ? Readonly(Map(field.Origin, call, state)) : Map(field.Origin, call, state), false);
                if (callee.FunctionKind is FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer && call.Receiver is MirCopy receiver)
                {
                    var initialized = OwnedFields(callee.ContainingType!, receiver.Place.Local, "constructor-field").Union(result);
                    MirPlace place = receiver.Place.Project(new MirDerefProjection());
                    if (callee.ContainingStruct is { } owner && owner.AllInstanceFields.Any(field => field.IsAllocatorField))
                    {
                        // The phases share one receiver. A summary for a later phase
                        // cannot replace the bindings installed by an earlier one.
                        foreach (FieldSymbol field in owner.AllInstanceFields.Where(field => callee.FunctionKind switch
                                 {
                                     FunctionKind.AllocationConstructor => field.IsAllocatorField && TypeIdentity.AreSame(field.ContainingType, owner),
                                     FunctionKind.InstanceInitializer => field.HasInitializer && TypeIdentity.AreSame(field.ContainingType, owner),
                                     _ => !field.IsAllocatorField,
                                 }))
                            state = Write(place.Project(new MirFieldProjection(field)),
                                initialized.Project(field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)), state);
                    }
                    else state = Write(place, initialized, state);
                    if (pointerLifetimes && pointerReturns is not null && pointerReturns.ContainsKey(callee))
                    {
                        var pointerFields = PointerCallResult(callee, call, receiver.Place.Local, state, MirReferenceValue.Empty);
                        foreach (var (path, roots) in pointerFields.Fields)
                        {
                            // Preserve constructor/reference bindings while adding
                            // the pointer payload fields inferred from the body.
                            foreach (var address in Address(place, state))
                                if (StorageKey(address) is { } key)
                                    state = state.SetItem(key, state.GetValueOrDefault(key, MirReferenceValue.Empty)
                                        .Store(MirReferenceOrigin.Append(address.Path, path), MirReferenceValue.Empty with { Roots = roots }, true));
                        }
                    }
                }
                else if (callee.ReturnType is ReferenceTypeSymbol && result.Roots.IsEmpty && !callee.IsDefinition)
                    result = MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown));
            }
            else result = MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown));
            if (callee is { IsVirtual: true } or { IsOverride: true } || call.InterfaceType is not null)
                result = MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown));
            if (callee is not null && IsHandle(callee.ReturnType) && call.Destination is { } handleResult)
                result = CallHandle(callee, call, handleResult.Local, state);
            if (callee is not null && call.Destination is { } aggregateResult && !IsHandle(callee.ReturnType))
                result = OwnedFields(callee.ReturnType, aggregateResult.Local, "call-field").Union(result);
            if (pointerLifetimes && call.Destination is { } pointerResult)
                result = PointerCallResult(callee, call, pointerResult.Local, state, result);
            if (pointerLifetimes) state = PointerCallEffects(call, state);
            return call.Destination is { } destination ? Write(destination, result, state) : state;
        }
        if (pointerLifetimes && source.Terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Swap, Arguments: [MirCopy left, MirCopy right] })
        {
            var a = left.Place.Project(new MirDerefProjection());
            var b = right.Place.Project(new MirDerefProjection());
            var leftValue = Read(a, state);
            var rightValue = Read(b, state);
            return Write(b, leftValue, Write(a, rightValue, state));
        }
        if (pointerLifetimes && source.Terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.AtomicLoad or
            MirIntrinsicKind.AtomicInitialize or MirIntrinsicKind.AtomicStore or MirIntrinsicKind.AtomicUpdate or
            MirIntrinsicKind.AtomicExchange or MirIntrinsicKind.CompareExchange or MirIntrinsicKind.CompareExchangeOwned,
            Arguments: [MirCopy atomicAddress, ..] } atomic)
        {
            var place = atomicAddress.Place.Project(new MirDerefProjection());
            var previous = Read(place, state);
            var desired = atomic.Arguments.Length > 1 ? Operand(atomic.Arguments[^1], state) : previous;
            var replacement = atomic.Intrinsic switch
            {
                MirIntrinsicKind.AtomicLoad or MirIntrinsicKind.AtomicUpdate => previous,
                MirIntrinsicKind.CompareExchange or MirIntrinsicKind.CompareExchangeOwned => previous.Union(desired),
                _ => desired,
            };
            var result = atomic.Intrinsic switch
            {
                MirIntrinsicKind.AtomicExchange or MirIntrinsicKind.AtomicLoad => previous,
                MirIntrinsicKind.CompareExchange => MirReferenceValue.Empty,
                MirIntrinsicKind.CompareExchangeOwned => MirReferenceValue.Empty.Store("1", previous.Union(desired), true),
                _ => replacement,
            };
            if (atomic.Intrinsic != MirIntrinsicKind.AtomicLoad) state = Write(place, replacement, state);
            return atomic.Destination is { } target ? Write(target, result, state) : state;
        }
        if (source.Terminator is MirIntrinsicCall intrinsic && intrinsic.Destination is { } output)
        {
            if (intrinsic.Intrinsic == MirIntrinsicKind.MakeCallable && pointerLifetimes)
                state = StoreCallableCaptures(intrinsic, output.Local, state);
            MirReferenceValue result = intrinsic.Intrinsic switch
            {
                MirIntrinsicKind.MakeCallable => CapturedReferences(intrinsic, state),
                MirIntrinsicKind.Allocate or MirIntrinsicKind.Malloc or MirIntrinsicKind.AlignedMalloc or MirIntrinsicKind.Calloc or
                    MirIntrinsicKind.AdoptUnique or MirIntrinsicKind.AdoptShared => Handle(intrinsic.ResultType, output.Local, "allocation", fresh: true),
                MirIntrinsicKind.AllocateHeapArray => pointerLifetimes ? Handle(intrinsic.ResultType, output.Local, "allocation", fresh: true) : MirReferenceValue.Of(new(MirReferenceOriginKind.Static)),
                MirIntrinsicKind.AllocateStackArray => MirReferenceValue.Of(new(MirReferenceOriginKind.Local, output.Local.Value)),
                _ => intrinsic.Arguments.Aggregate(MirReferenceValue.Empty, (value, argument) => value.Union(Operand(argument, state))),
            };
            if (pointerLifetimes && intrinsic.Intrinsic is MirIntrinsicKind.AdoptUnique or MirIntrinsicKind.AdoptShared)
                result = Operand(intrinsic.Arguments[0], state);
            if (intrinsic.Intrinsic is MirIntrinsicKind.Allocate or MirIntrinsicKind.AllocateHeapArray or MirIntrinsicKind.Malloc or MirIntrinsicKind.AlignedMalloc or MirIntrinsicKind.Calloc)
                result = result with { Roots = result.Roots.Select(root => root with
                {
                    AllocationKind = intrinsic.Intrinsic is MirIntrinsicKind.Allocate or MirIntrinsicKind.AllocateHeapArray ? MirAllocationKind.Typed : MirAllocationKind.Raw,
                    AllocatorType = intrinsic.Function?.AllocationOperation == AllocationOperation.Allocate && intrinsic.Arguments[^1].Type is PointerTypeSymbol { ElementType: StructTypeSymbol provider } ? provider : null,
                }).ToImmutableHashSet() };
            return Write(output, result, state);
        }
        return state;
    }
    private MirReferenceValue CapturedReferences(MirIntrinsicCall call, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var roots = ImmutableHashSet.CreateBuilder<MirReferenceOrigin>();
        for (int index = 0; index < call.Arguments.Length; index++)
        {
            MirReferenceValue value = Operand(call.Arguments[index], state);
            if (index >= call.Captures.Length || call.Captures[index].IsBorrow) roots.UnionWith(value.Roots);
            else if (pointerLifetimes && !checkedPointerIdentities && MirPointerLifetimeAnalysis.CarriesPointer(call.Captures[index].StorageType))
            {
                roots.UnionWith(value.Roots);
                foreach (var field in value.Fields.Values) roots.UnionWith(field);
            }
            else foreach (var leaf in ReferenceLeaves(call.Captures[index].StorageType))
                roots.UnionWith(value.Project(string.Join('/', leaf.Path)).Roots);
        }
        var result = new MirReferenceValue(roots.ToImmutable(), MirReferenceValue.Empty.Fields);
        if (pointerLifetimes && call.Function is { } target && call.Destination is { } destination)
            result = result with { CallableTargets = result.CallableTargets.Add("", [new(target, destination.Local)]) };
        return result;
    }
    public static IEnumerable<(ImmutableArray<int> Path, bool IsReadonly)> ReferenceLeaves(TypeSymbol type) => ReferenceLeaves(type, [], []);
    private static IEnumerable<(ImmutableArray<int> Path, bool IsReadonly)> ReferenceLeaves(TypeSymbol type, ImmutableArray<int> path, HashSet<TypeSymbol> seen)
    {
        if (type is ReferenceTypeSymbol reference) { yield return (path, reference.IsReadonly); yield break; }
        if (type is FunctionValueTypeSymbol) { yield return (path, false); yield break; }
        if (type is LifetimeModifierTypeSymbol modifier)
        {
            foreach (var leaf in ReferenceLeaves(modifier.ElementType, path, seen)) yield return leaf;
            yield break;
        }

        if (type is not IFieldStorageTypeSymbol structure || !seen.Add(type)) yield break;
        foreach (FieldSymbol field in structure.AllInstanceFields)
            foreach (var leaf in ReferenceLeaves(field.Type, path.Add(field.Ordinal), seen)) yield return leaf;
        seen.Remove(type);
    }
    public ImmutableArray<ReferenceFieldOrigin> ReferenceFields(CancellationToken cancellation = default,
        MirDataflowResult<ImmutableDictionary<MirLocalId, MirReferenceValue>>? facts = null)
    {
        bool constructor = function.Symbol.FunctionKind is FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer;
        TypeSymbol type = constructor ? function.Symbol.ContainingType! : function.ReturnType;
        var flow = facts ?? MirDataflow.Solve(MirFlowFacts.Graph(function, cancellation), this, cancellation);
        var result = new List<ReferenceFieldOrigin>();
        foreach (MirBasicBlock block in function.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id) && block.Terminator is MirReturn))
        {
            var state = flow.Output[block.Id];
            MirReferenceValue value = constructor ? Load(new(MirReferenceOriginKind.Receiver), state) :
                ((MirReturn)block.Terminator).Value is { } operand ? Operand(operand, state) : MirReferenceValue.Empty;
            foreach (var (path, isReadonly) in ReferenceLeaves(type))
                foreach (MirReferenceOrigin origin in value.Project(string.Join('/', path)).Roots)
                    result.Add(new(path, origin.Contract, isReadonly || origin.IsReadonly));
        }
        return result.GroupBy(origin => $"{string.Join('/', origin.FieldOrdinals)}:{origin.Origin.Kind}:{origin.Origin.ParameterOrdinal}:{string.Join('/', origin.Origin.FieldOrdinals)}:{origin.IsReadonly}").OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => group.First()).ToImmutableArray();
    }
    public ImmutableHashSet<MirReferenceOrigin> Returns(CancellationToken cancellation = default)
    {
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(function, cancellation), this, cancellation);
        return function.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id) && block.Terminator is MirReturn { Value: not null })
            .SelectMany(block => Operand(((MirReturn)block.Terminator).Value!, flow.Output[block.Id]).Roots).ToImmutableHashSet();
    }
}
