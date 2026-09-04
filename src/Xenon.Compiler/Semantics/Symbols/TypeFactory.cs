using System.Collections.Concurrent;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>
/// Owns derived types for one compilation. Builtins are global, declarations are nominal,
/// and every derived construction is canonical within this factory. No factory is global.
/// </summary>
public sealed class TypeFactory
{
    private readonly ConcurrentDictionary<(TypeSymbol Element, bool Readonly), PointerTypeSymbol> _pointers = new();
    private readonly ConcurrentDictionary<(TypeSymbol Element, bool Readonly), ReferenceTypeSymbol> _references = new();
    private readonly ConcurrentDictionary<(TypeSymbol Element, int Rank), ArrayTypeSymbol> _arrays = new();
    private readonly ConcurrentDictionary<TypeSymbol, AtomicTypeSymbol> _atomic = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, UniqueTypeSymbol> _unique = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, SharedTypeSymbol> _shared = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, WeakTypeSymbol> _weak = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, StorageTypeSymbol> _storage = new(TypeIdentity.Comparer);
    private readonly ConcurrentDictionary<TypeSymbol, PinTypeSymbol> _pin = new(TypeIdentity.Comparer);

    public PointerTypeSymbol PointerTo(TypeSymbol elementType, bool isReadonly = false) =>
        _pointers.GetOrAdd((Intern(elementType), isReadonly), static key => new PointerTypeSymbol(key.Element, key.Readonly));

    public ReferenceTypeSymbol ReferenceTo(TypeSymbol elementType, bool isReadonly = false) =>
        _references.GetOrAdd((Intern(elementType), isReadonly), static key => new ReferenceTypeSymbol(key.Element, key.Readonly));

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

    internal void EnsureUniqueDestructor(UniqueTypeSymbol type, NamespaceSymbol globalNamespace, SyntaxNode declaration) =>
        EnsureOwnershipDestructor(type, globalNamespace, declaration);

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

    // Normalize incoming derived types, including those built with another factory.
    // Dictionary keys then use canonical element references, never display strings.
    public TypeSymbol Intern(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            PointerTypeSymbol pointer => PointerTo(pointer.ElementType, pointer.IsReadonly),
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
