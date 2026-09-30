using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Mir.Analysis;

internal sealed partial class MirReadonlyAnalysis
{
    private HashSet<object> Read(IEnumerable<object> storage, TypeSymbol type)
    {
        type = UnwrapValueStorage(type);
        HashSet<object> result = new(ReferenceEqualityComparer.Instance);
        // Aggregate values retain their field shape. StoreValue performs the
        // field-by-field copy; pointer/reference reads still load capabilities.
        if (type is IFieldStorageTypeSymbol) return new(storage, ReferenceEqualityComparer.Instance);
        if (!ContainsAccess(type)) return result;
        foreach (object location in storage)
        {
            object origin = Unwrap(location);
            if (ReferenceEquals(origin, _hidden) || ReferenceEquals(origin, _external)) result.Add(origin);
            else if (_memory.TryGetValue(origin, out HashSet<object>? contents)) result.UnionWith(contents);
        }
        return result;
    }

    private HashSet<object> Project(IEnumerable<object> storage, FieldSymbol field)
    {
        HashSet<object> result = new(ReferenceEqualityComparer.Instance);
        foreach (object parent in storage)
        {
            if (parent is UncertainLocation uncertain)
            {
                result.UnionWith(Uncertain(Project([uncertain.Origin], field)));
                continue;
            }
            // Unknown external objects keep their receiver provenance. Local
            // objects, including local aliases, have distinct storage per field.
            if (ReferenceEquals(parent, _hidden) || ReferenceEquals(parent, _external))
            {
                result.Add(parent);
                continue;
            }
            if (!_fields.TryGetValue(parent, out var fields))
                _fields.Add(parent, fields = []);
            if (!fields.TryGetValue(field, out object? location))
            {
                // A legal by-value field path cannot repeat the same field
                // symbol (that would require an infinite struct layout). Coarse
                // call summaries can form such recursive aliases; widen those
                // paths to the earlier location to keep the fixed point finite.
                for (object current = parent; current is FieldLocation ancestor; current = ancestor.Parent)
                {
                    if (!ReferenceEquals(ancestor.Field, field)) continue;
                    location = ancestor;
                    _summaryLocations.Add(location);
                    break;
                }
                fields.Add(field, location ??= new FieldLocation(parent, field));
            }
            result.Add(location);
        }
        return result;
    }

    private sealed class FieldLocation(object parent, FieldSymbol field)
    {
        public object Parent { get; } = parent;
        public FieldSymbol Field { get; } = field;
    }

    private void StoreValue(HashSet<object> storage, HashSet<object> values, TypeSymbol type) =>
        StoreValue(storage, values, type, []);

    private void StoreValue(HashSet<object> storage, HashSet<object> values, TypeSymbol type, HashSet<TypeSymbol> path)
    {
        type = UnwrapValueStorage(type);
        if (type is not IFieldStorageTypeSymbol structure)
        {
            if (ContainsAccess(type)) Store(storage, values, strong: true);
            return;
        }
        if (type is StructTypeSymbol) StoreReceiverTypes(storage, KnownReceiverTypes(values));
        // Invalid recursive value layouts already have a binding diagnostic.
        if (!path.Add(type)) return;
        foreach (FieldSymbol field in structure.AllInstanceFields)
        {
            StoreValue(Project(storage, field), Read(Project(values, field), field.Type), field.Type, path);
        }
        path.Remove(type);
    }

    private void StoreUnknown(HashSet<object> storage, HashSet<object> capabilities, TypeSymbol type, HashSet<TypeSymbol> path)
    {
        type = UnwrapValueStorage(type);
        ForgetReceiverTypes(storage);
        if (!ContainsAccess(type)) return;
        if (type is not IFieldStorageTypeSymbol structure)
        {
            Store(storage, capabilities);
            return;
        }
        if (!path.Add(type)) return;
        foreach (FieldSymbol field in structure.AllInstanceFields)
            StoreUnknown(Project(storage, field), capabilities, field.Type, path);
        path.Remove(type);
    }

    private void CollectReachableStorage(HashSet<object> storage, TypeSymbol type, HashSet<object> result,
        HashSet<(object, TypeSymbol)> visited, HashSet<TypeSymbol>? valuePath = null)
    {
        type = UnwrapValueStorage(type);
        HashSet<object> local = [];
        foreach (object origin in storage)
        {
            // A valid readonly callee cannot export hidden writable access.
            if (ReferenceEquals(origin, _hidden)) continue;
            result.Add(origin);
            if (!ReferenceEquals(origin, _external) && visited.Add((origin, type))) local.Add(origin);
        }
        if (local.Count == 0) return;
        if (type is IFieldStorageTypeSymbol structure)
        {
            valuePath ??= [];
            if (!valuePath.Add(type)) return;
            foreach (FieldSymbol field in structure.AllInstanceFields)
                CollectReachableStorage(Project(local, field), field.Type, result, visited, valuePath);
            valuePath.Remove(type);
        }
        else if (type is ArrayTypeSymbol array)
            CollectReachableStorage(ArrayElements(Read(local, type)), array.ElementType, result, visited);
        else if (ElementType(type) is { } element)
            CollectReachableStorage(Read(local, type), element, result, visited);
        else if (type is InterfaceTypeSymbol)
        {
            foreach (object value in Read(local, type))
            {
                if (Unwrap(value) is InterfaceValue view)
                {
                    result.Add(view);
                    CollectReachableStorage(Read([view], type), view.SourceType, result, visited);
                }
                else CollectReachableStorage([value], type, result, visited);
            }
        }
    }

    private static TypeSymbol? ElementType(TypeSymbol type) => type switch
    {
        PointerTypeSymbol pointer => pointer.ElementType,
        ReferenceTypeSymbol reference => reference.ElementType,
        ArrayTypeSymbol array => array.ElementType,
        UniqueTypeSymbol unique => unique.ElementType,
        SharedTypeSymbol shared => shared.ElementType,
        WeakTypeSymbol weak => weak.ElementType,
        _ => null,
    };

    private object Root(object identity)
    {
        if (!_context.Roots.TryGetValue(identity, out object? root))
            _context.Roots.Add(identity, root = new object());
        if (_context.IsRecursive) _summaryLocations.Add(root);
        return root;
    }

    private void Store(IEnumerable<object> storage, HashSet<object> values, bool strong = false)
    {
        object[] destinations = storage.ToArray();
        bool replace = strong && destinations.Length == 1 && IsExact(destinations[0]);
        foreach (object destination in destinations)
        {
            object origin = Unwrap(destination);
            if (ReferenceEquals(origin, _hidden) || ReferenceEquals(origin, _external)) continue;
            if (replace)
            {
                _memory[origin] = new(values, ReferenceEqualityComparer.Instance);
                continue;
            }
            if (values.Count == 0) continue;
            if (!_memory.TryGetValue(origin, out HashSet<object>? contents))
                _memory.Add(origin, contents = new(ReferenceEqualityComparer.Instance));
            contents.UnionWith(values);
        }
    }

    private bool IsExact(object location)
    {
        if (location is UncertainLocation) return false;
        if (ReferenceEquals(location, _hidden) || ReferenceEquals(location, _external)) return false;
        for (object current = location; ;)
        {
            if (_summaryLocations.Contains(current)) return false;
            if (current is ArrayElement element) return !_arrays[element.Array].Repeated;
            if (current is not FieldLocation field) return true;
            current = field.Parent;
        }
    }

    private sealed class UncertainLocation(object origin)
    {
        public object Origin { get; } = origin;
    }

    private static object Unwrap(object location) => location is UncertainLocation uncertain ? uncertain.Origin : location;

    private HashSet<object> Uncertain(IEnumerable<object> origins)
    {
        HashSet<object> result = [];
        foreach (object origin in origins)
        {
            if (ArrayAliasLocations(origin) is { } locations)
            {
                foreach (object possible in locations)
                {
                    if (!_uncertainLocations.TryGetValue(possible, out var uncertain))
                        _uncertainLocations.Add(possible, uncertain = new(possible));
                    result.Add(uncertain);
                }
                continue;
            }
            if (origin is UncertainLocation || ReferenceEquals(origin, _hidden) || ReferenceEquals(origin, _external))
                result.Add(origin);
            else
            {
                if (!_uncertainLocations.TryGetValue(origin, out var uncertain))
                    _uncertainLocations.Add(origin, uncertain = new(origin));
                result.Add(uncertain);
            }
        }
        return result;
    }

    private void CheckWrite(HashSet<object> storage, MirEffectSite site)
    {
        if (storage.Contains(_hidden))
            Report(site, "cannot mutate hidden state; use an explicitly mutable pointer/reference parameter",
                DiagnosticIds.HiddenStateMutation);
    }

    private bool HasHiddenAccess(HashSet<object> origins, TypeSymbol type) =>
        HasHiddenAccess(origins, type, [], []);

    private bool HasHiddenAccess(HashSet<object> origins, TypeSymbol type,
        HashSet<(object, TypeSymbol)> visited, HashSet<TypeSymbol> valuePath)
    {
        type = UnwrapValueStorage(type);
        if (!ExposesWritableAccess(type)) return false;
        if (origins.Contains(_hidden)) return true;
        HashSet<object> fresh = new(origins.Where(origin => visited.Add((origin, type))));
        if (fresh.Count == 0) return false;
        if (type is IFieldStorageTypeSymbol structure)
        {
            if (!valuePath.Add(type)) return false;
            foreach (FieldSymbol field in structure.AllInstanceFields)
            {
                if (HasHiddenAccess(Read(Project(fresh, field), field.Type), field.Type, visited, valuePath))
                    return true;
            }
            valuePath.Remove(type);
            return false;
        }
        // Origins already address the referent/pointee storage. Read using the
        // element type, NOT the pointer/reference type: a struct read preserves
        // its field locations, while int*& must load the referenced int* binding.
        if (type is ArrayTypeSymbol array)
            return HasHiddenAccess(Read(ArrayElements(fresh), array.ElementType), array.ElementType, visited, []);
        if (ElementType(type) is { } element)
            return HasHiddenAccess(Read(ArrayCapabilityRange(fresh), element), element, visited, []);
        // Interface values erase the concrete field layout. Follow the known
        // graph conservatively rather than losing capabilities in that view.
        if (type is InterfaceTypeSymbol)
        {
            var pending = new Stack<object>(fresh);
            var seen = new HashSet<object>();
            while (pending.TryPop(out object? origin))
            {
                origin = Unwrap(origin);
                if (ReferenceEquals(origin, _hidden)) return true;
                if (!seen.Add(origin)) continue;
                if (_memory.TryGetValue(origin, out var contents))
                    foreach (object value in contents) pending.Push(value);
                if (_fields.TryGetValue(origin, out var fields))
                    foreach (object value in fields.Values) pending.Push(value);
            }
        }
        return false;
    }

    private void CheckCall(FunctionSymbol callee, MirEffectSite site)
    {
        if (!callee.IsReadonly)
            Report(site, $"cannot call non-readonly function or member '{callee.Name}'",
                DiagnosticIds.NonReadonlyCallFromReadonlyFunction);
    }

    private void Report(MirEffectSite site, string message, string id)
    {
        TextLocation location = site.Source.Location.Source is not null ? site.Source.Location : _location;
        string diagnostic = $"readonly function '{function.Name}' {message}";
        if (_reported.Add((location, diagnostic))) diagnostics.Report(location, diagnostic, id);
    }

    private static bool IsMutableParameter(TypeSymbol type) =>
        type is PointerTypeSymbol { IsReadonly: false } or ReferenceTypeSymbol { IsReadonly: false };

    // Lifetime wrappers keep the element in the same logical storage. Their
    // payload must retain its field shape and pointer origins across value moves.
    private static TypeSymbol UnwrapValueStorage(TypeSymbol type)
    {
        while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
        return type;
    }

    private static bool ContainsAccess(TypeSymbol type) => ContainsAccess(type, []);

    private static bool ContainsAccess(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        type = UnwrapValueStorage(type);
        return type switch
        {
            PointerTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol or OwnershipTypeSymbol or
                InterfaceTypeSymbol or FunctionValueTypeSymbol => true,
            IFieldStorageTypeSymbol structure when visited.Add(type) =>
                structure.AllInstanceFields.Any(field => ContainsAccess(field.Type, visited)),
            _ => false,
        };
    }

    private static bool ExposesWritableAccess(TypeSymbol type) => ExposesWritableAccess(type, []);

    private static bool ExposesWritableAccess(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        type = UnwrapValueStorage(type);
        if (!visited.Add(type)) return false;
        return type switch
        {
            PointerTypeSymbol pointer => !pointer.IsReadonly || ExposesWritableAccess(pointer.ElementType, visited),
            ReferenceTypeSymbol reference => !reference.IsReadonly || ExposesWritableAccess(reference.ElementType, visited),
            ArrayTypeSymbol or InterfaceTypeSymbol => true,
            UniqueTypeSymbol or SharedTypeSymbol => true,
            FunctionValueTypeSymbol => true,
            IFieldStorageTypeSymbol structure => structure.AllInstanceFields.Any(field => ExposesWritableAccess(field.Type, visited)),
            _ => false,
        };
    }
}
