using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public enum MirLifetimeAuthority { Owner, ReferenceParameter, StorageValue, ReferenceField }

public enum MirReferenceOriginKind { Local, Parameter, Receiver, Static, Unknown, RawPointee, UniquePointee, SharedPointee }

/// <summary>A storage origin and a finite projection path; independent of source syntax.</summary>
public sealed record MirReferenceOrigin(MirReferenceOriginKind Kind, int Ordinal = -1, string Path = "")
{
    public MirLifetimeAuthority Authority { get; init; }
    public bool IsReadonly { get; init; }
    public bool IsFresh { get; init; }
    public int? HandleParameter { get; init; }
    public string? HandleIdentity { get; init; }
    public TypeSymbol? PointeeType { get; init; }
    public bool IsSafeReference => Kind is MirReferenceOriginKind.Parameter or MirReferenceOriginKind.Receiver or MirReferenceOriginKind.Static;
    public MirReferenceOrigin Project(string path) => this with { Path = Append(Path, path) };
    internal static string Append(string left, string right) => left == "*" || right == "*" ||
        left.Count(c => c == '/') + right.Count(c => c == '/') >= 7 ? "*" :
        left.Length == 0 ? right : right.Length == 0 ? left : left + "/" + right;
    public ReferenceReturnOrigin Contract => new(Kind switch
    {
        MirReferenceOriginKind.Parameter => ReferenceReturnOriginKind.Parameter,
        MirReferenceOriginKind.Receiver => ReferenceReturnOriginKind.Receiver,
        MirReferenceOriginKind.Static => ReferenceReturnOriginKind.Static,
        _ => ReferenceReturnOriginKind.Unknown,
    }, Kind == MirReferenceOriginKind.Parameter ? Ordinal : -1,
        !IsSafeReference ? [] : Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(part => int.TryParse(part, out _)).Select(int.Parse).ToImmutableArray());
}

public sealed record MirReferenceValue(ImmutableHashSet<MirReferenceOrigin> Roots,
    ImmutableDictionary<string, ImmutableHashSet<MirReferenceOrigin>> Fields)
{
    public ImmutableHashSet<MirLocalId> Aliases { get; init; } = [];
    public ImmutableHashSet<MirLocalId> Lineage { get; init; } = [];
    public static MirReferenceValue Empty { get; } = new([], ImmutableDictionary<string, ImmutableHashSet<MirReferenceOrigin>>.Empty);
    public static MirReferenceValue Of(MirReferenceOrigin origin) => Empty with { Roots = [origin] };
    public MirReferenceValue Union(MirReferenceValue other)
    {
        var fields = Fields.ToBuilder();
        foreach (var (path, roots) in other.Fields) fields[path] = fields.GetValueOrDefault(path, []).Union(roots);
        return new(Roots.Union(other.Roots), fields.ToImmutable()) { Aliases = Aliases.Union(other.Aliases), Lineage = Lineage.Union(other.Lineage) };
    }
    public bool Same(MirReferenceValue other) => Aliases.SetEquals(other.Aliases) && Lineage.SetEquals(other.Lineage) && Roots.SetEquals(other.Roots) && Fields.Count == other.Fields.Count &&
        Fields.All(pair => other.Fields.TryGetValue(pair.Key, out var roots) && roots.SetEquals(pair.Value));
    public MirReferenceValue Project(string path)
    {
        if (path.Length == 0) return this;
        var fields = Empty.Fields.ToBuilder();
        foreach (var (key, roots) in Fields)
            if (key.StartsWith(path + "/", StringComparison.Ordinal)) fields[key[(path.Length + 1)..]] = roots;
        return new(Fields.GetValueOrDefault(path, Roots.Select(root => root.Project(path)).ToImmutableHashSet()), fields.ToImmutable()) { Aliases = Aliases, Lineage = Lineage };
    }
    public MirReferenceValue Store(string path, MirReferenceValue value, bool strong)
    {
        if (path.Length == 0) return strong ? value : Union(value);
        var fields = Fields.ToBuilder();
        if (strong)
            foreach (string key in Fields.Keys.Where(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal))) fields.Remove(key);
        fields[path] = strong ? value.Roots : fields.GetValueOrDefault(path, []).Union(value.Roots);
        foreach (var (key, roots) in value.Fields)
        {
            string projected = MirReferenceOrigin.Append(path, key);
            fields[projected] = strong ? roots : fields.GetValueOrDefault(projected, []).Union(roots);
        }
        return this with { Fields = fields.ToImmutable(), Aliases = Aliases.Union(value.Aliases), Lineage = Lineage.Union(value.Lineage) };
    }
}

/// <summary>Reference provenance over MIR assignments, calls, joins and explicit exits.</summary>
public sealed partial class MirReferenceOrigins(MirFunction function) :
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
            if (local.Variable is ParameterSymbol parameter && Carries(local.Type))
                state[local.Id] = IsHandle(local.Type) ? Handle(local.Type, local.Id, "parameter", parameter: parameter.Ordinal) :
                    MirReferenceValue.Of(new(MirReferenceOriginKind.Parameter, parameter.Ordinal) { IsReadonly = local.Type is ReferenceTypeSymbol { IsReadonly: true },
                        Authority = local.Type is ReferenceTypeSymbol reference && reference.ElementType is not StorageTypeSymbol
                            ? MirLifetimeAuthority.ReferenceParameter : MirLifetimeAuthority.Owner });
            else if (local.Kind == MirLocalKind.Receiver)
                state[local.Id] = MirReferenceValue.Of(new(MirReferenceOriginKind.Receiver));
            else if (local.Kind == MirLocalKind.Capture && Carries(local.Type))
                state[local.Id] = MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown));
        return state.ToImmutable();
    }
    private static bool Carries(TypeSymbol type) => type is PointerTypeSymbol or OwnershipTypeSymbol || MirLifetimeValue.CarriesBorrow(type);
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Join(IEnumerable<ImmutableDictionary<MirLocalId, MirReferenceValue>> states)
    {
        var result = Bottom.ToBuilder();
        foreach (var state in states)
            foreach (var (local, value) in state) result[local] = result.TryGetValue(local, out var old) ? old.Union(value) : value;
        return result.ToImmutable();
    }
    public bool Same(ImmutableDictionary<MirLocalId, MirReferenceValue> left, ImmutableDictionary<MirLocalId, MirReferenceValue> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value.Same(value));

    private static MirLocalId? StorageKey(MirReferenceOrigin address) => address.Kind switch
    {
        MirReferenceOriginKind.Local => new(address.Ordinal),
        MirReferenceOriginKind.Receiver => new(-1),
        MirReferenceOriginKind.Parameter => new(-2 - address.Ordinal),
        _ => null,
    };
    private MirReferenceValue Load(MirReferenceOrigin address, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        if (StorageKey(address) is { } key && state.TryGetValue(key, out var stored)) return stored.Project(address.Path);
        return address.Kind == MirReferenceOriginKind.Local ? MirReferenceValue.Empty : MirReferenceValue.Of(address);
    }
    private MirReferenceValue Contents(MirReferenceValue addresses, ImmutableDictionary<MirLocalId, MirReferenceValue> state) =>
        addresses.Roots.Aggregate(MirReferenceValue.Empty, (value, root) => value.Union(Load(root, state)));
    public ImmutableHashSet<MirReferenceOrigin> Address(MirPlace place, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        ImmutableHashSet<MirReferenceOrigin> roots = [new(MirReferenceOriginKind.Local, place.Local.Value)];
        TypeSymbol type = _locals[place.Local].Type;
        foreach (MirProjection projection in place.Projections)
        {
            if (projection is MirIndexProjection or MirLinearIndexProjection && type is PointerTypeSymbol)
                roots = roots.SelectMany(root => Load(root, state).Roots).ToImmutableHashSet();
            roots = projection switch
            {
                MirDerefProjection => roots.SelectMany(root => Load(root, state).Roots).ToImmutableHashSet(),
                MirFieldProjection field => roots.Select(root => root.Project(field.Field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToImmutableHashSet(),
                MirIndexProjection or MirLinearIndexProjection => roots.Select(root => root.Project("*")).ToImmutableHashSet(),
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
        var value = Address(place, state).Aggregate(MirReferenceValue.Empty, (result, root) => result.Union(Load(root, state)));
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
        MirCopy copy => Read(copy.Place, state),
        MirMove move => Read(move.Place, state),
        _ => MirReferenceValue.Empty,
    };
    private ImmutableDictionary<MirLocalId, MirReferenceValue> Write(MirPlace place, MirReferenceValue value,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var addresses = Address(place, state);
        foreach (var address in addresses)
        {
            if (StorageKey(address) is not { } local) continue;
            var empty = address.Kind == MirReferenceOriginKind.Local ? MirReferenceValue.Empty : MirReferenceValue.Of(address with { Path = "" });
            state = state.SetItem(local, state.GetValueOrDefault(local, empty)
                .Store(address.Path, value, addresses.Count == 1 && address.Path != "*"));
        }
        return state;
    }
    private static MirReferenceValue Readonly(MirReferenceValue value) => value with { Roots = value.Roots.Select(origin => origin with { IsReadonly = true }).ToImmutableHashSet() };
    public MirReferenceValue Value(MirRValue value, ImmutableDictionary<MirLocalId, MirReferenceValue> state) => value switch
    {
        MirBorrow borrow => Borrow(borrow, state),
        MirStaticFieldAddress => MirReferenceValue.Of(new(MirReferenceOriginKind.Static)),
        MirUse use => Operand(use.Operand, state),
        MirCast cast => Cast(cast, state),
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
                ? StoredHandle(assign.Value.Type, assign.Destination.Local, Value(assign.Value, state)) : Value(assign.Value, state), state),
        MirForget forget => Write(forget.Place, MirReferenceValue.Empty, state),
        MirSetStorageState { Initialized: false } storage => Write(storage.Place, MirReferenceValue.Empty, state),
        MirStorageLive live => state.Remove(live.Local),
        MirStorageDead dead => state.Remove(dead.Local),
        _ => state,
    };
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
        if (edge.Kind != MirEdgeKind.Normal) return state;
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
                if (callee.FunctionKind is FunctionKind.Constructor or FunctionKind.InstanceInitializer && call.Receiver is MirCopy receiver)
                    state = Write(receiver.Place.Project(new MirDerefProjection()),
                        OwnedFields(callee.ContainingType!, receiver.Place.Local, "constructor-field").Union(result), state);
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
            return call.Destination is { } destination ? Write(destination, result, state) : state;
        }
        if (source.Terminator is MirIntrinsicCall intrinsic && intrinsic.Destination is { } output)
        {
            MirReferenceValue result = intrinsic.Intrinsic switch
            {
                MirIntrinsicKind.MakeCallable => CapturedReferences(intrinsic, state),
                MirIntrinsicKind.Allocate or MirIntrinsicKind.Malloc or MirIntrinsicKind.AlignedMalloc or MirIntrinsicKind.Calloc or
                    MirIntrinsicKind.AdoptUnique or MirIntrinsicKind.AdoptShared => Handle(intrinsic.ResultType, output.Local, "allocation", fresh: true),
                MirIntrinsicKind.AllocateHeapArray => MirReferenceValue.Of(new(MirReferenceOriginKind.Static)),
                MirIntrinsicKind.AllocateStackArray => MirReferenceValue.Of(new(MirReferenceOriginKind.Local, output.Local.Value)),
                _ => intrinsic.Arguments.Aggregate(MirReferenceValue.Empty, (value, argument) => value.Union(Operand(argument, state))),
            };
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
            else foreach (var leaf in ReferenceLeaves(call.Captures[index].StorageType))
                roots.UnionWith(value.Project(string.Join('/', leaf.Path)).Roots);
        }
        return new(roots.ToImmutable(), MirReferenceValue.Empty.Fields);
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
        bool constructor = function.Symbol.FunctionKind is FunctionKind.Constructor or FunctionKind.InstanceInitializer;
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