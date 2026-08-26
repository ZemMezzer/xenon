namespace Xenon.Compiler.Semantics.Symbols;

public abstract class TypeSymbol : Symbol
{
    protected TypeSymbol(string name)
        : base(name, SymbolKind.Type)
    {
    }
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
    internal PointerTypeSymbol(TypeSymbol elementType, bool isConst)
        : base($"{(isConst ? "const " : string.Empty)}{elementType.Name}*")
    {
        ElementType = elementType;
        IsConst = isConst;
    }

    public TypeSymbol ElementType { get; }

    public bool IsConst { get; }
}

public sealed class ReferenceTypeSymbol : TypeSymbol
{
    internal ReferenceTypeSymbol(TypeSymbol elementType, bool isConst)
        : base($"{(isConst ? "const " : string.Empty)}{elementType.Name}&")
    {
        ElementType = elementType;
        IsConst = isConst;
    }

    public TypeSymbol ElementType { get; }

    public bool IsConst { get; }
}


public sealed class ArrayTypeSymbol : TypeSymbol
{
    internal ArrayTypeSymbol(TypeSymbol elementType)
        : base($"{elementType.Name}[]")
    {
        ElementType = elementType;
    }

    public TypeSymbol ElementType { get; }
}

internal sealed class SpecialTypeSymbol : TypeSymbol
{
    public SpecialTypeSymbol(string name)
        : base(name)
    {
    }
}
