namespace Xenon.Compiler.Semantics.Symbols;

public abstract class TypeSymbol : Symbol
{
    protected TypeSymbol(string name, Symbol? containingSymbol = null)
        : base(name, SymbolKind.Type, containingSymbol)
    {
    }

    public Copyability Copyability => TypeFacts.GetCopyability(this);
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

/// <summary>
/// Core atomic storage for one value. Atomicity changes access semantics and may
/// require a backend-specific representation; it does not create ownership.
/// </summary>
public sealed class AtomicTypeSymbol : TypeSymbol
{
    internal AtomicTypeSymbol(TypeSymbol elementType)
        : base(string.Empty) => ElementType = elementType;

    public TypeSymbol ElementType { get; }
    public override string Name => ToDisplayString();
    public override string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short) =>
        $"atomic<{ElementType.ToDisplayString(format)}>";
}

/// <summary>
/// A single-owner handle for one fresh heap allocation. Its runtime value has the
/// same representation as <see cref="StorageType"/>, while its distinct semantic
/// identity prevents implicit raw-pointer adoption and ordinary copying.
/// </summary>
public abstract class OwnershipTypeSymbol : TypeSymbol
{
    public override string Name => ToDisplayString();

    protected OwnershipTypeSymbol(string ownershipKind, TypeSymbol elementType, TypeSymbol storageType)
        : base(string.Empty)
    {
        OwnershipKind = ownershipKind;
        ElementType = elementType;
        StorageType = storageType;
    }

    public string OwnershipKind { get; }
    public TypeSymbol ElementType { get; }
    public TypeSymbol StorageType { get; }
    public FunctionSymbol? CompleteDestructor { get; internal set; }

    public override string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short) =>
        $"{OwnershipKind}<{ElementType.ToDisplayString(format)}>";
}

public sealed class UniqueTypeSymbol : OwnershipTypeSymbol
{
    internal UniqueTypeSymbol(TypeSymbol elementType, TypeSymbol storageType)
        : base("unique", elementType, storageType) { }
}

public sealed class SharedTypeSymbol : OwnershipTypeSymbol
{
    internal SharedTypeSymbol(TypeSymbol elementType, TypeSymbol storageType)
        : base("shared", elementType, storageType) { }
}

public sealed class WeakTypeSymbol : OwnershipTypeSymbol
{
    internal WeakTypeSymbol(TypeSymbol elementType, TypeSymbol storageType)
        : base("weak", elementType, storageType) { }
}

public abstract class LifetimeModifierTypeSymbol : TypeSymbol
{
    protected LifetimeModifierTypeSymbol(string modifierKind, TypeSymbol elementType)
        : base(string.Empty)
    {
        ModifierKind = modifierKind;
        ElementType = elementType;
    }

    public string ModifierKind { get; }
    public TypeSymbol ElementType { get; }
    public override string Name => ToDisplayString();
    public override string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short) =>
        $"{ModifierKind}<{ElementType.ToDisplayString(format)}>";
}

/// <summary>Correctly aligned storage for T plus persistent runtime lifetime state.</summary>
public sealed class StorageTypeSymbol : LifetimeModifierTypeSymbol
{
    internal StorageTypeSymbol(TypeSymbol elementType) : base("storage", elementType) { }
    public FunctionSymbol? CompleteDestructor { get; internal set; }
}

/// <summary>A live T whose address must remain stable until destruction.</summary>
public sealed class PinTypeSymbol : LifetimeModifierTypeSymbol
{
    internal PinTypeSymbol(TypeSymbol elementType) : base("pin", elementType) { }
}

public sealed class ErrorTypeSymbol : TypeSymbol
{
    internal ErrorTypeSymbol() : base("<error>") { }
}

internal sealed class SpecialTypeSymbol : TypeSymbol
{
    public SpecialTypeSymbol(string name) : base(name) { }
}
