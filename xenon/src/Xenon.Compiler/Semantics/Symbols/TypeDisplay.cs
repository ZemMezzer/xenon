using System.Text;

namespace Xenon.Compiler.Semantics.Symbols;

public enum TypeDisplayFormat { Short, FullyQualified }

/// <summary>Formatting for the current type constructors. Types may override ToDisplayString to add new forms.</summary>
internal static class TypeDisplay
{
    public static string Pointer(PointerTypeSymbol type, TypeDisplayFormat format)
    {
        string element = type.ElementType.ToDisplayString(format);
        if (type.ElementType is ArrayTypeSymbol or ReferenceTypeSymbol ||
            type.IsReadonly && type.ElementType is PointerTypeSymbol)
            element = $"({element})";
        return $"{(type.IsReadonly ? "readonly " : "")}{element}*";
    }

    public static string Reference(ReferenceTypeSymbol type, TypeDisplayFormat format)
    {
        string element = type.ElementType.ToDisplayString(format);
        bool hasSourceReadonlyPointer = HasSourceReadonlyPointer(type.ElementType);
        // A source prefix qualifies both the innermost pointer and its reference.
        if (type.IsReadonly && hasSourceReadonlyPointer) return element + "&";
        // Some compiler-created shapes have no source spelling. Grouping makes
        // qualifier scope explicit instead of printing ambiguous repeated prefixes.
        if (type.ElementType is ArrayTypeSymbol or ReferenceTypeSymbol ||
            type.ElementType is PointerTypeSymbol && (type.IsReadonly || HasReadonlyPointer(type.ElementType)))
            element = $"({element})";
        return $"{(type.IsReadonly ? "readonly " : "")}{element}&";
    }

    public static string Array(ArrayTypeSymbol type, TypeDisplayFormat format)
    {
        var suffixes = new StringBuilder();
        TypeSymbol element = type;
        while (element is ArrayTypeSymbol array)
        {
            suffixes.Append('[').Append(',', array.Rank - 1).Append(']');
            element = array.ElementType;
        }
        return element.ToDisplayString(format) + suffixes;
    }

    private static bool HasReadonlyPointer(TypeSymbol type)
    {
        while (type is PointerTypeSymbol pointer)
        {
            if (pointer.IsReadonly) return true;
            type = pointer.ElementType;
        }
        return false;
    }

    private static bool HasSourceReadonlyPointer(TypeSymbol type)
    {
        while (type is PointerTypeSymbol pointer)
        {
            if (pointer.ElementType is not PointerTypeSymbol) return pointer.IsReadonly &&
                pointer.ElementType is not (ArrayTypeSymbol or ReferenceTypeSymbol);
            if (pointer.IsReadonly) return false;
            type = pointer.ElementType;
        }
        return false;
    }
}
