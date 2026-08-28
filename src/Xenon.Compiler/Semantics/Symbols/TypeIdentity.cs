using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>Structural identity for internal signatures; display names are not symbol keys.</summary>
internal static class TypeIdentity
{
    public static string Get(TypeSymbol type) => type switch
    {
        StructTypeSymbol structure => $"struct({structure.FullName})",
        InterfaceTypeSymbol @interface => $"interface({@interface.FullName})",
        EnumTypeSymbol enumeration => $"enum({enumeration.FullName})",
        PointerTypeSymbol pointer => $"ptr{(pointer.IsReadonly ? "readonly" : "")}({Get(pointer.ElementType)})",
        ReferenceTypeSymbol reference => $"ref{(reference.IsReadonly ? "readonly" : "")}({Get(reference.ElementType)})",
        ArrayTypeSymbol array => $"array{array.Rank}({Get(array.ElementType)})",
        _ => type.Name,
    };

    public static string Parameters(ImmutableArray<ParameterSymbol> parameters) =>
        string.Join(",", parameters.Select(parameter => Get(parameter.Type)));

    public static string Method(FunctionSymbol method) => $"{method.Name}({Parameters(method.Parameters)})";
}
