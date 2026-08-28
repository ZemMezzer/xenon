namespace Xenon.Compiler.Semantics.Symbols;

public static class NativeSymbolNames
{
    // Xenon currently exposes only non-variadic C calling convention functions.
    // Semantic readonly qualifiers do not affect their ABI.
    internal static string? GetAbiSignature(FunctionSymbol function, ITargetTypeLayout? layout)
    {
        string? result = AbiType(function.ReturnType, layout);
        string?[] parameters = function.Parameters.Select(parameter => AbiType(parameter.Type, layout)).ToArray();
        if (result is null || parameters.Any(parameter => parameter is null)) return null;
        return $"cdecl;fixed;{result}({(function.HasImplicitThis ? "ptr," : "")}{string.Join(",", parameters)})";
    }

    private static string? AbiType(TypeSymbol type, ITargetTypeLayout? layout) => type switch
    {
        PointerTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol => "ptr",
        EnumTypeSymbol enumeration => AbiType(enumeration.UnderlyingType, layout),
        PrimitiveTypeSymbol { IsInteger: true } integer =>
            (integer.BitWidth ?? layout?.GetIntegerBitWidth(integer)) is int width ? $"i{width}" : null,
        InterfaceTypeSymbol => "{ptr,ptr}",
        _ => TypeSignature.Get(type),
    };

    public static string Get(FunctionSymbol function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (function.IsExtern)
        {
            return function.Name;
        }

        return function.IsExport
            ? function.FullName.Replace('.', '_')
            : function.FullName;
    }
}
