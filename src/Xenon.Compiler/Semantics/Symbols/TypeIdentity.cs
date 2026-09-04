using System.Runtime.CompilerServices;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>
/// Semantic type identity, independent of interning and conversion rules.
/// Builtins and declared types are nominal; constructed types compare recursively.
/// In particular, equal names or equal ABI layouts do not imply equal types.
/// </summary>
public static class TypeIdentity
{
    public static IEqualityComparer<TypeSymbol> Comparer { get; } = new TypeComparer();

    public static bool AreSame(TypeSymbol? left, TypeSymbol? right)
    {
        if (ReferenceEquals(left, right)) return true;
        return (left, right) switch
        {
            (PointerTypeSymbol a, PointerTypeSymbol b) => a.IsReadonly == b.IsReadonly && AreSame(a.ElementType, b.ElementType),
            (ReferenceTypeSymbol a, ReferenceTypeSymbol b) => a.IsReadonly == b.IsReadonly && AreSame(a.ElementType, b.ElementType),
            (ArrayTypeSymbol a, ArrayTypeSymbol b) => a.Rank == b.Rank && AreSame(a.ElementType, b.ElementType),
            (AtomicTypeSymbol a, AtomicTypeSymbol b) => AreSame(a.ElementType, b.ElementType),
            (UniqueTypeSymbol a, UniqueTypeSymbol b) => AreSame(a.ElementType, b.ElementType),
            (SharedTypeSymbol a, SharedTypeSymbol b) => AreSame(a.ElementType, b.ElementType),
            (WeakTypeSymbol a, WeakTypeSymbol b) => AreSame(a.ElementType, b.ElementType),
            (StorageTypeSymbol a, StorageTypeSymbol b) => AreSame(a.ElementType, b.ElementType),
            (PinTypeSymbol a, PinTypeSymbol b) => AreSame(a.ElementType, b.ElementType),
            _ => false,
        };
    }

    public static int GetHashCode(TypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type switch
        {
            PointerTypeSymbol pointer => HashCode.Combine(1, GetHashCode(pointer.ElementType), pointer.IsReadonly),
            ReferenceTypeSymbol reference => HashCode.Combine(2, GetHashCode(reference.ElementType), reference.IsReadonly),
            ArrayTypeSymbol array => HashCode.Combine(3, GetHashCode(array.ElementType), array.Rank),
            AtomicTypeSymbol atomic => HashCode.Combine(10, GetHashCode(atomic.ElementType)),
            UniqueTypeSymbol unique => HashCode.Combine(4, GetHashCode(unique.ElementType)),
            SharedTypeSymbol shared => HashCode.Combine(5, GetHashCode(shared.ElementType)),
            WeakTypeSymbol weak => HashCode.Combine(6, GetHashCode(weak.ElementType)),
            StorageTypeSymbol storage => HashCode.Combine(7, GetHashCode(storage.ElementType)),
            PinTypeSymbol pin => HashCode.Combine(9, GetHashCode(pin.ElementType)),
            _ => RuntimeHelpers.GetHashCode(type),
        };
    }

    private sealed class TypeComparer : IEqualityComparer<TypeSymbol>
    {
        public bool Equals(TypeSymbol? x, TypeSymbol? y) => AreSame(x, y);
        public int GetHashCode(TypeSymbol obj) => TypeIdentity.GetHashCode(obj);
    }
}
