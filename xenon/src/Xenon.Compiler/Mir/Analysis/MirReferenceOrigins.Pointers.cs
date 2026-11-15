using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirReferenceOrigins
{
    private MirPointerConstants? _pointerConstants;
    private MirPointerConstants PointerConstants => _pointerConstants ??= new(function);
    private MirReferenceValue PointerArithmetic(MirBinary binary, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var value = Operand(binary.Left.Type is PointerTypeSymbol ? binary.Left : binary.Right, state);
        var offset = PointerConstants.Value(binary.Left.Type is PointerTypeSymbol ? binary.Right : binary.Left);
        if (offset is sbyte or byte or short or ushort or int or uint or long or ulong && Convert.ToDecimal(offset) == 0)
            return value;
        // The allocation remains known, but arithmetic does not establish which
        // individual object's lifetime is affected by destruct/placement.
        return value with { Roots = value.Roots.Select(root => Offset(root, unknown: true)).ToImmutableHashSet() };
    }

    private static MirReferenceOrigin Offset(MirReferenceOrigin root, bool unknown = false) => root with
    {
        UnknownObjectOffset = root.UnknownObjectOffset || unknown,
        DestroyedAt = root.StorageFreed ? root.DestroyedAt : null,
        DestroyedLocation = root.StorageFreed ? root.DestroyedLocation : null,
        CallDeath = root.StorageFreed ? root.CallDeath : null,
        AutomaticLifetimeEnd = root.StorageFreed && root.AutomaticLifetimeEnd,
    };

    internal MirReferenceValue MapPointerParameter(MirOperand argument, string path,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var value = Operand(argument, state);
        TypeSymbol type = argument.Type;
        bool carrier = false;
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int component = 0; component < components.Length; component++)
        {
            string part = components[component];
            if (part == "*" && (component == 7 || Aggregate(type) &&
                (component == components.Length - 1 || components[component + 1] == "^")))
                return ReachableSubtree(value, type, carrier, state);
            if (part == "*")
            {
                ProjectType(part);
                value = MirReferenceValue.Empty with { Roots = value.Roots.Concat(value.Fields.Values.SelectMany(roots => roots))
                    .Select(root => root.Project("*")).ToImmutableHashSet() };
                continue;
            }
            if (part != "^")
            {
                ProjectType(part);
                if (part.StartsWith('[') && part != "[0]" || part == "*")
                    value = value with { Roots = value.Roots.Select(root => Offset(root, part.Contains('*'))).ToImmutableHashSet() };
                value = value.Project(part);
                continue;
            }
            // A field/index projection already denotes the carrier's address.
            // Loading it yields its declared type. Only an unprojected handle
            // value needs its pointer layer removed before loading the pointee.
            while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
            type = type switch { ReferenceTypeSymbol reference => reference.ElementType,
                PointerTypeSymbol pointer when !carrier => pointer.ElementType,
                OwnershipTypeSymbol owner when !carrier => owner.ElementType, _ => type };
            carrier = false;
            value = value.Roots.Aggregate(MirReferenceValue.Empty, (result, root) => result.Union(Load(root, state, type)));
        }
        return value;

        static bool Aggregate(TypeSymbol current)
        {
            while (current is LifetimeModifierTypeSymbol modifier) current = modifier.ElementType;
            if (current is ReferenceTypeSymbol reference) current = reference.ElementType;
            if (current is PointerTypeSymbol pointer) current = pointer.ElementType;
            return current is IFieldStorageTypeSymbol;
        }

        void ProjectType(string part)
        {
            while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
            if (type is ReferenceTypeSymbol reference) { type = reference.ElementType; carrier = true; }
            if (type is PointerTypeSymbol pointer) { type = pointer.ElementType; carrier = true; }
            else if (type is OwnershipTypeSymbol owner) { type = owner.ElementType; carrier = true; }
            else if (type is ArrayTypeSymbol array && (part == "*" || part.StartsWith('['))) { type = array.ElementType; carrier = true; }
            if (int.TryParse(part, out int ordinal) && type is IFieldStorageTypeSymbol structure &&
                structure.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal) is { } field) type = field.Type;
        }
    }

    // A depth-widened path is a may-reach set, never a raw must-alias proof.
    // Traverse the remaining type graph with memoized bounded carrier identities.
    // This retains loaded targets (including opaque checked loans), not just the
    // address of the aggregate that contains their handles.
    private MirReferenceValue ReachableSubtree(MirReferenceValue value, TypeSymbol type, bool carrier,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var result = MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown));
        var seen = new HashSet<(TypeSymbol, bool, MirReferenceOrigin)>();
        Visit(value, type, carrier);
        return result;

        void Visit(MirReferenceValue current, TypeSymbol currentType, bool address)
        {
            while (currentType is LifetimeModifierTypeSymbol modifier) currentType = modifier.ElementType;
            current = current with { Roots = current.Roots.Where(root => seen.Add((currentType, address, root))).ToImmutableHashSet() };
            if (current.Roots.IsEmpty && current.Fields.IsEmpty) return;
            result = result.Union(current);
            result = result with { Roots = result.Roots.Union(current.Fields.Values.SelectMany(roots => roots)) };
            if (currentType is ReferenceTypeSymbol reference)
            { Visit(current, reference.ElementType, true); return; }
            if (currentType is PointerTypeSymbol or OwnershipTypeSymbol)
            {
                if (address)
                    current = current.Roots.Aggregate(MirReferenceValue.Empty, (loaded, root) => loaded.Union(Load(root, state, currentType)));
                result = result.Union(current);
                Visit(current, Element(currentType)!, true);
                return;
            }
            if (currentType is ArrayTypeSymbol array)
            { Visit(current.Project("*"), array.ElementType, true); return; }
            if (currentType is IFieldStorageTypeSymbol structure)
            {
                if (address)
                    foreach (var root in current.Roots)
                    {
                        // Legacy widened paths can also denote indexed aggregate
                        // carriers. Retain their explicitly stored payloads and
                        // visit indexed carriers with the same element type, so
                        // nested loaded pointers remain reachable as well.
                        var contents = Load(root, state, currentType);
                        result = result.Union(contents);
                        result = result with { Roots = result.Roots.Union(contents.Fields.Values.SelectMany(roots => roots)) };
                        foreach (string index in contents.Fields.Keys.Select(path => path.Split('/')[0])
                            .Where(part => part == "*" || part.StartsWith('[')).Distinct())
                            Visit(MirReferenceValue.Of(root.Project(index)), currentType, true);
                    }
                foreach (var field in structure.AllInstanceFields)
                    Visit(current.Project(field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)), field.Type, address);
            }
        }
    }

    internal MirReferenceValue MapPointerEffect(MirOperand argument, MirLifetimeEffect effect,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var value = MapPointerParameter(argument, effect.Path, state);
        return !effect.UnknownOffset ? value : value with { Roots = value.Roots.Select(root => Offset(root, unknown: true)).ToImmutableHashSet() };
    }

    internal MirReferenceOrigin Index(MirReferenceOrigin root, MirProjection projection)
    {
        string path = IndexPath(projection);
        if (pointerLifetimes && path != "[0]") root = Offset(root, unknown: path.Contains('*'));
        return root.Project(path);
    }

    private static MirReferenceValue DefaultPointers(TypeSymbol type)
    {
        var result = MirReferenceValue.Empty;
        Visit(type, "", []);
        return result;
        void Visit(TypeSymbol current, string path, HashSet<TypeSymbol> seen)
        {
            if (current is PointerTypeSymbol or ArrayTypeSymbol or OwnershipTypeSymbol or FunctionValueTypeSymbol)
                result = result.Store(path, MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)), true);
            else if (current is LifetimeModifierTypeSymbol modifier) Visit(modifier.ElementType, path, seen);
            else if (current is IFieldStorageTypeSymbol structure && seen.Add(current))
            {
                foreach (var field in structure.AllInstanceFields)
                    Visit(field.Type, MirReferenceOrigin.Append(path, field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)), seen);
                seen.Remove(current);
            }
        }
    }
    private readonly Dictionary<FieldSymbol, MirLocalId> _staticStorageKeys = [];
    private MirLocalId StaticStorageKey(FieldSymbol field)
    {
        if (!_staticStorageKeys.TryGetValue(field, out var id)) _staticStorageKeys[field] = id = new(int.MaxValue - _staticStorageKeys.Count);
        return id;
    }

    public IEnumerable<MirReferenceValue> ExternalValues(ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        foreach (var (field, key) in _staticStorageKeys)
            if (state.TryGetValue(key, out var value)) yield return Payload(value, field.Type);
        foreach (var (identity, key) in _heapStorageKeys)
            if (identity.Item3?.StartsWith("parameter:", StringComparison.Ordinal) == true &&
                _locals.TryGetValue(new(identity.Item2), out var local) &&
                state.TryGetValue(key, out var value))
            {
                if (local.Type is PointerTypeSymbol pointer) yield return Payload(value, pointer.ElementType);
                else if (local.Type is SharedTypeSymbol shared) yield return Payload(value, shared.ElementType);
            }
        foreach (var parameter in function.Symbol.Parameters)
            if (parameter.Type is ReferenceTypeSymbol reference && state.TryGetValue(new(-2 - parameter.Ordinal), out var value))
                yield return Payload(value, reference.ElementType);
            else if (parameter.Type is ArrayTypeSymbol array && state.TryGetValue(new(-2 - parameter.Ordinal), out var elements))
                yield return Payload(elements, array);
        if (function.Symbol.FunctionKind is not (FunctionKind.Destructor or FunctionKind.OwnershipDestructor or FunctionKind.StorageDestructor) &&
            function.Symbol.ContainingType is { } owner && state.TryGetValue(new(-1), out var receiver)) yield return Payload(receiver, owner);

        static MirReferenceValue Payload(MirReferenceValue value, TypeSymbol type)
        {
            if (type is PointerTypeSymbol or FunctionValueTypeSymbol) return value with { Fields = MirReferenceValue.Empty.Fields };
            if (type is ArrayTypeSymbol array)
            {
                var payload = value with { Fields = MirReferenceValue.Empty.Fields };
                foreach (string index in value.Fields.Keys.Select(key => key.Split('/')[0]).Where(key => key.StartsWith('[')).Distinct())
                    payload = payload.Union(Payload(value.Project(index), array.ElementType));
                return payload;
            }
            if (type is LifetimeModifierTypeSymbol modifier) return Payload(value, modifier.ElementType);
            if (type is AtomicTypeSymbol atomic) return Payload(value, atomic.ElementType);
            if (type is not IFieldStorageTypeSymbol structure) return MirReferenceValue.Empty;
            var result = MirReferenceValue.Empty;
            foreach (var field in structure.AllInstanceFields)
            {
                string path = field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (value.Fields.Keys.Any(key => key == path || key.StartsWith(path + "/", StringComparison.Ordinal)))
                    result = result.Union(Payload(value.Project(path), field.Type));
            }
            return result;
        }
    }
    private ImmutableDictionary<MirLocalId, MirReferenceValue> PointerCallEffects(MirCall call,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        // A mutable reference permits a callee to replace pointer-bearing storage.
        // Without a store contract, keeping the old value would invent a proof.
        foreach (var argument in call.Arguments)
            if (argument is MirCopy copy && (argument.Type is ReferenceTypeSymbol { IsReadonly: false } reference &&
                MirPointerLifetimeAnalysis.CarriesPointer(reference.ElementType) ||
                argument.Type is PointerTypeSymbol pointer && MirPointerLifetimeAnalysis.CarriesPointer(pointer.ElementType)))
                state = Write(copy.Place.Project(new MirDerefProjection()), MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)), state);
        if (call is { Receiver: MirCopy receiver, Callee: MirFunctionOperand { Function: { IsReadonly: false } method } } &&
            method.FunctionKind is not (FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer) &&
            method.ContainingType is { } containing && MirPointerLifetimeAnalysis.CarriesPointer(containing))
            state = Write(receiver.Place.Project(new MirDerefProjection()), MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)), state);
        return state;
    }
    private readonly Dictionary<(MirReferenceOriginKind, int, string?, MirAllocationSite?, MirAllocationContext?), MirLocalId> _heapStorageKeys = [];
    private MirLocalId HeapStorageKey(MirReferenceOrigin origin)
    {
        var key = (origin.Kind, origin.Ordinal, origin.HandleIdentity, origin.AllocationSite, origin.AllocationContext);
        if (!_heapStorageKeys.TryGetValue(key, out var id)) _heapStorageKeys[key] = id = new(int.MinValue + _heapStorageKeys.Count);
        return id;
    }
    private string IndexPath(MirProjection projection)
    {
        var indices = projection is MirIndexProjection index ? index.Indices : [((MirLinearIndexProjection)projection).Index];
        var parts = new List<string>();
        foreach (var operand in indices)
        {
            var value = PointerConstants.Value(operand);
            if (value is null) return "*";
            parts.Add(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!);
        }
        return "[" + string.Join(',', parts) + "]";
    }

    private MirReferenceValue PointerCallResult(FunctionSymbol? callee, MirCall call, MirLocalId site,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state, MirReferenceValue fallback)
    {
        if (callee is not null && !callee.IsVirtual && !callee.IsOverride && call.InterfaceType is null &&
            pointerReturns is not null && pointerReturns.TryGetValue(callee, out var summary))
        {
            MirReferenceValue MapRoots(IEnumerable<MirReferenceOrigin> roots) => roots.Aggregate(MirReferenceValue.Empty,
                (result, root) => result.Union(root.HandleParameter is { } parameter && parameter >= 0 && parameter < call.Arguments.Length
                    ? ParameterValue(parameter, root)
                    : root.Kind == MirReferenceOriginKind.Parameter && root.Ordinal >= 0 && root.Ordinal < call.Arguments.Length
                        ? ParameterValue(root.Ordinal, root)
                        : root.Kind == MirReferenceOriginKind.Static ? MirReferenceValue.Of(root)
                        : root.IsFresh ? FreshResult(root, site)
                        : MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown))));
            var mapped = MapRoots(summary.Roots);
            foreach (var (path, roots) in summary.Fields) mapped = mapped.Store(path, MapRoots(roots), true);
            return mapped;

            MirReferenceValue ParameterValue(int parameter, MirReferenceOrigin returned)
            {
                var value = MapPointerParameter(call.Arguments[parameter], returned.Path, state);
                // A summary must retain the distinction between allocation and
                // object identity when it forwards an interior pointer.
                if (!returned.UnknownObjectOffset && !returned.Path.Split('/').Any(part => part.StartsWith('[') && part != "[0]"))
                    return value;
                return value with { Roots = value.Roots.Select(root => Offset(root, returned.UnknownObjectOffset)).ToImmutableHashSet() };
            }
        }
        // No body/contract is not evidence of freshness or of an input alias.
        return callee?.ReturnType is PointerTypeSymbol or ArrayTypeSymbol ? MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown)) : fallback;
    }

    private MirReferenceValue FreshResult(MirReferenceOrigin root, MirLocalId site)
    {
        // Distinguish multiple calls to the same factory inside a returned value.
        // A bounded structural context uses symbol identity, never hash equality.
        // Recursive/deep allocation contexts widen to unknown instead of aliasing
        // unrelated allocations or growing an unbounded call string.
        var callSite = new MirAllocationSite(function.Symbol, site);
        int depth = 0;
        for (var context = root.AllocationContext; context is not null; context = context.Parent)
            if (++depth >= 4 || context.CallSite == callSite) return MirReferenceValue.Of(new(MirReferenceOriginKind.Unknown));
        return MirReferenceValue.Of(root with { Ordinal = site.Value, HandleIdentity = $"call:{site.Value}",
            AllocationContext = new(callSite, root.AllocationContext) });
    }
}
