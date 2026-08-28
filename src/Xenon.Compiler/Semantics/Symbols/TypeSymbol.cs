namespace Xenon.Compiler.Semantics.Symbols;

public abstract class TypeSymbol : Symbol
{
    protected TypeSymbol(string name, Symbol? containingSymbol = null)
        : base(name, SymbolKind.Type, containingSymbol)
    {
    }

    public virtual string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short) => Name;
    public override string ToString() => ToDisplayString();
}

public sealed class PrimitiveTypeSymbol : TypeSymbol
{
    internal PrimitiveTypeSymbol(
        string name,
        bool isInteger = false,
        bool isSigned = false,
        int? bitWidth = null,
        bool isFloatingPoint = false)
        : base(name)
    {
        IsInteger = isInteger;
        IsSigned = isSigned;
        BitWidth = bitWidth;
        IsFloatingPoint = isFloatingPoint;
    }

    public bool IsInteger { get; }

    public bool IsSigned { get; }

    public int? BitWidth { get; }

    public bool IsFloatingPoint { get; }
}

public sealed class PointerTypeSymbol : TypeSymbol
{
    public override string Name => ToDisplayString();
    public override string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short) => TypeDisplay.Pointer(this, format);

    internal PointerTypeSymbol(TypeSymbol elementType, bool isReadonly)
        : base(string.Empty)
    {
        ElementType = elementType;
        IsReadonly = isReadonly;
    }

    public TypeSymbol ElementType { get; }

    public bool IsReadonly { get; }
}

public sealed class ReferenceTypeSymbol : TypeSymbol
{
    public override string Name => ToDisplayString();
    public override string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short) => TypeDisplay.Reference(this, format);

    internal ReferenceTypeSymbol(TypeSymbol elementType, bool isReadonly)
        : base(string.Empty)
    {
        ElementType = elementType;
        IsReadonly = isReadonly;
    }

    public TypeSymbol ElementType { get; }

    public bool IsReadonly { get; }
}


public sealed class ArrayTypeSymbol : TypeSymbol
{
    public override string Name => ToDisplayString();
    public override string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short) => TypeDisplay.Array(this, format);

    internal ArrayTypeSymbol(TypeSymbol elementType, int rank)
        : base(string.Empty)
    {
        ElementType = elementType;
        Rank = rank;
    }

    public TypeSymbol ElementType { get; }
    public int Rank { get; }

}

internal sealed class SpecialTypeSymbol : TypeSymbol
{
    public SpecialTypeSymbol(string name)
        : base(name)
    {
    }
}
