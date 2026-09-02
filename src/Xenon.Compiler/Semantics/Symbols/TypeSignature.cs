using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>Stable signature encoding within a compilation, used for member keys and native names.
/// This is not semantic equality: same-named declarations from different compilations are distinct.</summary>
internal static class TypeSignature
{
    public static string Get(TypeSymbol type) => type switch
    {
        DeclaredTypeSymbol declared => $"{declared.DeclarationKind}({declared.FullName})",
        PointerTypeSymbol pointer => $"ptr{(pointer.IsReadonly ? "readonly" : "")}({Get(pointer.ElementType)})",
        ReferenceTypeSymbol reference => $"ref{(reference.IsReadonly ? "readonly" : "")}({Get(reference.ElementType)})",
        ArrayTypeSymbol array => $"array{array.Rank}({Get(array.ElementType)})",
        UniqueTypeSymbol unique => $"unique({Get(unique.ElementType)})",
        SharedTypeSymbol shared => $"shared({Get(shared.ElementType)})",
        WeakTypeSymbol weak => $"weak({Get(weak.ElementType)})",
        StorageTypeSymbol storage => $"storage({Get(storage.ElementType)})",
        PinTypeSymbol pin => $"pin({Get(pin.ElementType)})",
        _ => type.Name,
    };

    public static string Parameters(ImmutableArray<ParameterSymbol> parameters) =>
        string.Join(",", parameters.Select(parameter => Get(parameter.Type)));

    public static string Method(FunctionSymbol method) => $"{method.Name}({Parameters(method.Parameters)})";
}
