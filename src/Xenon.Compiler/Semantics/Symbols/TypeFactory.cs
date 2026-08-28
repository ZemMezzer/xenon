using System.Collections.Concurrent;

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

    public PointerTypeSymbol PointerTo(TypeSymbol elementType, bool isReadonly = false) =>
        _pointers.GetOrAdd((Intern(elementType), isReadonly), static key => new PointerTypeSymbol(key.Element, key.Readonly));

    public ReferenceTypeSymbol ReferenceTo(TypeSymbol elementType, bool isReadonly = false) =>
        _references.GetOrAdd((Intern(elementType), isReadonly), static key => new ReferenceTypeSymbol(key.Element, key.Readonly));

    public ArrayTypeSymbol ArrayOf(TypeSymbol elementType, int rank = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rank, 1);
        return _arrays.GetOrAdd((Intern(elementType), rank), static key => new ArrayTypeSymbol(key.Element, key.Rank));
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
            _ => type,
        };
    }
}
