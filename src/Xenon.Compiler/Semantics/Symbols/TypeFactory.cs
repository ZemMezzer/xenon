using System.Collections.Concurrent;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>
/// Owns derived types for one compilation. Builtins are global, declarations are nominal,
/// and every derived construction is canonical within this factory. No factory is global.
/// </summary>
public sealed class TypeFactory
{
    internal sealed record Snapshot(
        HashSet<(TypeSymbol Element, bool Readonly)> PointerKeys,
        HashSet<(TypeSymbol Element, bool Readonly)> ReferenceKeys,
        HashSet<(TypeSymbol Element, int Rank)> ArrayKeys,
        HashSet<TypeSymbol> AtomicKeys,
        HashSet<TypeSymbol> UniqueKeys,
        HashSet<TypeSymbol> SharedKeys,
        HashSet<TypeSymbol> WeakKeys,
        HashSet<TypeSymbol> StorageKeys,
        HashSet<TypeSymbol> PinKeys,
        HashSet<FunctionPointerTypeSymbol> FunctionPointers,
        HashSet<FunctionValueTypeSymbol> FunctionValues,
        Dictionary<TypeSymbol, FunctionSymbol?> Destructors);

    private readonly ConcurrentDictionary<(TypeSymbol Element, bool Readonly), PointerTypeSymbol> _pointers = new();
    private readonly ConcurrentDictionary<(TypeSymbol Element, bool Readonly), ReferenceTypeSymbol> _references = new();
    private readonly ConcurrentDictionary<(TypeSymbol Element, int Rank), ArrayTypeSymbol> _arrays = new();
    private readonly ConcurrentDictionary<TypeSymbol, AtomicTypeSymbol> _atomic = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, UniqueTypeSymbol> _unique = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, SharedTypeSymbol> _shared = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, WeakTypeSymbol> _weak = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, StorageTypeSymbol> _storage = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, PinTypeSymbol> _pin = new(TypeIdentity.Comparer);
    private readonly List<FunctionPointerTypeSymbol> _functionPointers = [];
    private readonly List<FunctionValueTypeSymbol> _functionValues = [];

    public PointerTypeSymbol PointerTo(TypeSymbol elementType, bool isReadonly = false) =>
        _pointers.GetOrAdd((Intern(elementType), isReadonly), static key => new PointerTypeSymbol(key.Element, key.Readonly));

    public ReferenceTypeSymbol ReferenceTo(TypeSymbol elementType, bool isReadonly = false) =>
        _references.GetOrAdd((Intern(elementType), isReadonly), static key => new ReferenceTypeSymbol(key.Element, key.Readonly));

    public FunctionPointerTypeSymbol FunctionPointer(TypeSymbol returnType, IEnumerable<TypeSymbol> parameterTypes)
    {
        TypeSymbol internedReturn = Intern(returnType);
        TypeSymbol[] internedParameters = parameterTypes.Select(Intern).ToArray();
        lock (_functionPointers)
        {
            FunctionPointerTypeSymbol? existing = _functionPointers.FirstOrDefault(candidate =>
                TypeIdentity.AreSame(candidate.ReturnType, internedReturn) &&
                candidate.ParameterTypes.Length == internedParameters.Length &&
                candidate.ParameterTypes.Zip(internedParameters).All(pair => TypeIdentity.AreSame(pair.First, pair.Second)));
            if (existing is not null) return existing;
            var created = new FunctionPointerTypeSymbol(internedReturn, [.. internedParameters]);
            _functionPointers.Add(created);
            return created;
        }
    }

    public FunctionValueTypeSymbol FunctionValue(TypeSymbol returnType, IEnumerable<TypeSymbol> parameterTypes)
    {
        TypeSymbol internedReturn = Intern(returnType);
        TypeSymbol[] internedParameters = parameterTypes.Select(Intern).ToArray();
        lock (_functionValues)
        {
            FunctionValueTypeSymbol? existing = _functionValues.FirstOrDefault(candidate =>
                TypeIdentity.AreSame(candidate.ReturnType, internedReturn) &&
                candidate.ParameterTypes.Length == internedParameters.Length &&
                candidate.ParameterTypes.Zip(internedParameters).All(pair => TypeIdentity.AreSame(pair.First, pair.Second)));
            if (existing is not null) return existing;
            var created = new FunctionValueTypeSymbol(internedReturn, [.. internedParameters]);
            _functionValues.Add(created);
            return created;
        }
    }

    public ArrayTypeSymbol ArrayOf(TypeSymbol elementType, int rank = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rank, 1);
        return _arrays.GetOrAdd((Intern(elementType), rank), static key => new ArrayTypeSymbol(key.Element, key.Rank));
    }

    public AtomicTypeSymbol AtomicOf(TypeSymbol elementType) =>
        _atomic.GetOrAdd(Intern(elementType), static value => new AtomicTypeSymbol(value));

    public UniqueTypeSymbol UniqueOf(TypeSymbol elementType)
    {
        TypeSymbol element = Intern(elementType);
        return _unique.GetOrAdd(element, value => new UniqueTypeSymbol(
            value,
            value is ArrayTypeSymbol ? value : PointerTo(value)));
    }

    public SharedTypeSymbol SharedOf(TypeSymbol elementType)
    {
        TypeSymbol element = Intern(elementType);
        return _shared.GetOrAdd(element, value => new SharedTypeSymbol(
            value, value is ArrayTypeSymbol ? value : PointerTo(value)));
    }

    public WeakTypeSymbol WeakOf(TypeSymbol elementType)
    {
        TypeSymbol element = Intern(elementType);
        return _weak.GetOrAdd(element, value => new WeakTypeSymbol(
            value, value is ArrayTypeSymbol ? value : PointerTo(value)));
    }

    public StorageTypeSymbol StorageOf(TypeSymbol elementType) =>
        _storage.GetOrAdd(Intern(elementType), static value => new StorageTypeSymbol(value));

    public PinTypeSymbol PinOf(TypeSymbol elementType) =>
        _pin.GetOrAdd(Intern(elementType), static value => new PinTypeSymbol(value));

    internal IReadOnlyCollection<OwnershipTypeSymbol> OwnershipTypes =>
        [.. _unique.Values, .. _shared.Values, .. _weak.Values];
    internal IReadOnlyCollection<StorageTypeSymbol> StorageTypes => [.. _storage.Values];
    internal IReadOnlyCollection<FunctionValueTypeSymbol> FunctionValueTypes => [.. _functionValues];

    internal Snapshot CaptureSnapshot()
    {
        FunctionPointerTypeSymbol[] functionPointers;
        FunctionValueTypeSymbol[] functionValues;
        lock (_functionPointers) functionPointers = [.. _functionPointers];
        lock (_functionValues) functionValues = [.. _functionValues];
        TypeSymbol[] destructible = _unique.Values.Cast<TypeSymbol>()
            .Concat(_shared.Values).Concat(_weak.Values).Concat(_storage.Values)
            .Concat(functionValues).ToArray();
        var destructors = new Dictionary<TypeSymbol, FunctionSymbol?>(ReferenceEqualityComparer.Instance);
        foreach (TypeSymbol type in destructible)
            destructors.TryAdd(type, GetDestructor(type));
        return new Snapshot(
            [.. _pointers.Keys], [.. _references.Keys], [.. _arrays.Keys],
            [.. _atomic.Keys], [.. _unique.Keys], [.. _shared.Keys], [.. _weak.Keys],
            [.. _storage.Keys], [.. _pin.Keys],
            new HashSet<FunctionPointerTypeSymbol>(functionPointers, ReferenceEqualityComparer.Instance),
            new HashSet<FunctionValueTypeSymbol>(functionValues, ReferenceEqualityComparer.Instance),
            destructors);
    }

    internal void Rollback(Snapshot snapshot)
    {
        RemoveNewKeys(_pointers, snapshot.PointerKeys);
        RemoveNewKeys(_references, snapshot.ReferenceKeys);
        RemoveNewKeys(_arrays, snapshot.ArrayKeys);
        RemoveNewKeys(_atomic, snapshot.AtomicKeys);
        RemoveNewKeys(_unique, snapshot.UniqueKeys);
        RemoveNewKeys(_shared, snapshot.SharedKeys);
        RemoveNewKeys(_weak, snapshot.WeakKeys);
        RemoveNewKeys(_storage, snapshot.StorageKeys);
        RemoveNewKeys(_pin, snapshot.PinKeys);
        lock (_functionPointers)
            _functionPointers.RemoveAll(type => !snapshot.FunctionPointers.Contains(type));
        lock (_functionValues)
            _functionValues.RemoveAll(type => !snapshot.FunctionValues.Contains(type));
        foreach ((TypeSymbol type, FunctionSymbol? destructor) in snapshot.Destructors)
            SetDestructor(type, destructor);
    }

    private static FunctionSymbol? GetDestructor(TypeSymbol type) => type switch
    {
        OwnershipTypeSymbol ownership => ownership.CompleteDestructor,
        StorageTypeSymbol storage => storage.CompleteDestructor,
        FunctionValueTypeSymbol function => function.CompleteDestructor,
        _ => null,
    };

    private static void SetDestructor(TypeSymbol type, FunctionSymbol? destructor)
    {
        switch (type)
        {
            case OwnershipTypeSymbol ownership:
                ownership.CompleteDestructor = destructor;
                break;
            case StorageTypeSymbol storage:
                storage.CompleteDestructor = destructor;
                break;
            case FunctionValueTypeSymbol function:
                function.CompleteDestructor = destructor;
                break;
        }
    }

    private static void RemoveNewKeys<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> dictionary,
        HashSet<TKey> originalKeys)
        where TKey : notnull
    {
        foreach (TKey key in dictionary.Keys)
            if (!originalKeys.Contains(key))
                dictionary.TryRemove(key, out _);
    }

    internal void EnsureOwnershipDestructor(
        OwnershipTypeSymbol type,
        NamespaceSymbol globalNamespace,
        SyntaxNode declaration)
    {
        if (type.CompleteDestructor is not null) return;
        lock (type)
        {
            type.CompleteDestructor ??= new FunctionSymbol(
                type,
                globalNamespace,
                PointerTo(type),
                declaration);
        }
    }

    internal void EnsureOwnershipDestructor(OwnershipTypeSymbol type,
        NamespaceSymbol globalNamespace, SymbolOrigin origin)
    {
        if (type.CompleteDestructor is not null) return;
        lock (type)
        {
            type.CompleteDestructor ??= new FunctionSymbol(type, globalNamespace, PointerTo(type), origin);
        }
    }

    internal void EnsureUniqueDestructor(UniqueTypeSymbol type, NamespaceSymbol globalNamespace, SyntaxNode declaration) =>
        EnsureOwnershipDestructor(type, globalNamespace, declaration);

    internal void EnsureUniqueDestructor(UniqueTypeSymbol type, NamespaceSymbol globalNamespace, SymbolOrigin origin) =>
        EnsureOwnershipDestructor(type, globalNamespace, origin);

    internal void EnsureStorageDestructor(
        StorageTypeSymbol type,
        NamespaceSymbol globalNamespace,
        SyntaxNode declaration)
    {
        if (type.CompleteDestructor is not null) return;
        lock (type)
        {
            type.CompleteDestructor ??= new FunctionSymbol(
                type,
                globalNamespace,
                PointerTo(type),
                declaration);
        }
    }

    internal void EnsureFunctionValueDestructor(FunctionValueTypeSymbol type,
        NamespaceSymbol globalNamespace, SyntaxNode declaration)
    {
        if (type.CompleteDestructor is not null) return;
        lock (type)
            type.CompleteDestructor ??= new FunctionSymbol(type, globalNamespace, PointerTo(type), declaration);
    }

    internal void EnsureFunctionValueDestructor(FunctionValueTypeSymbol type,
        NamespaceSymbol globalNamespace, SymbolOrigin origin)
    {
        if (type.CompleteDestructor is not null) return;
        lock (type)
            type.CompleteDestructor ??= new FunctionSymbol(type, globalNamespace, PointerTo(type), origin);
    }

    internal void EnsureStorageDestructor(StorageTypeSymbol type,
        NamespaceSymbol globalNamespace, SymbolOrigin origin)
    {
        if (type.CompleteDestructor is not null) return;
        lock (type)
        {
            type.CompleteDestructor ??= new FunctionSymbol(type, globalNamespace, PointerTo(type), origin);
        }
    }

    // Normalize incoming derived types, including those built with another factory.
    // Dictionary keys then use canonical element references, never display strings.
    public TypeSymbol Intern(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            PointerTypeSymbol pointer => PointerTo(pointer.ElementType, pointer.IsReadonly),
            FunctionPointerTypeSymbol function => FunctionPointer(function.ReturnType, function.ParameterTypes),
            FunctionValueTypeSymbol function => FunctionValue(function.ReturnType, function.ParameterTypes),
            ReferenceTypeSymbol reference => ReferenceTo(reference.ElementType, reference.IsReadonly),
            ArrayTypeSymbol array => ArrayOf(array.ElementType, array.Rank),
            AtomicTypeSymbol atomic => AtomicOf(atomic.ElementType),
            UniqueTypeSymbol unique => UniqueOf(unique.ElementType),
            SharedTypeSymbol shared => SharedOf(shared.ElementType),
            WeakTypeSymbol weak => WeakOf(weak.ElementType),
            StorageTypeSymbol storage => StorageOf(storage.ElementType),
            PinTypeSymbol pin => PinOf(pin.ElementType),
            _ => type,
        };
    }
}
