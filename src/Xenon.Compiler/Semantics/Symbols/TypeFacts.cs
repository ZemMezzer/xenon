namespace Xenon.Compiler.Semantics.Symbols;

internal static class TypeFacts
{
    public static bool IsNumeric(TypeSymbol type) =>
        type is PrimitiveTypeSymbol { IsInteger: true } or PrimitiveTypeSymbol { IsFloatingPoint: true };

    public static bool IsInteger(TypeSymbol type) => type is PrimitiveTypeSymbol { IsInteger: true };

    public static bool CanAssign(TypeSymbol destination, TypeSymbol source)
    {
        if (ReferenceEquals(destination, BuiltinTypes.Error) || ReferenceEquals(source, BuiltinTypes.Error))
        {
            return true;
        }

        if (ReferenceEquals(destination, source))
        {
            return true;
        }

        return destination is PointerTypeSymbol && ReferenceEquals(source, BuiltinTypes.Null);
    }
}
