using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>Stable signature encoding within a compilation, used for member keys and native names.
/// This is not semantic equality: same-named declarations from different compilations are distinct.</summary>
internal static class TypeSignature
{
    public static string Get(TypeSymbol type) => Get(type, null);

    private static string Get(TypeSymbol type,
        IReadOnlyDictionary<GenericParameterSymbol, int>? genericPositions) => type switch
    {
        GenericParameterSymbol generic when genericPositions is not null &&
            genericPositions.TryGetValue(generic, out int position) => $"generic({position})",
        GenericParameterSymbol generic =>
            $"generic({generic.ContainingSymbol?.QualifiedName}:{generic.Ordinal})",
        StructTypeSymbol { GenericDefinition: { } definition } specialization =>
            $"struct({definition.FullName}<{string.Join(",", specialization.TypeArguments.Select(argument => Get(argument, genericPositions)))}>)",
        DeclaredTypeSymbol declared => $"{declared.DeclarationKind}({declared.FullName})",
        PointerTypeSymbol pointer => $"ptr{(pointer.IsReadonly ? "readonly" : "")}({Get(pointer.ElementType, genericPositions)})",
        FunctionPointerTypeSymbol function => $"fn({Get(function.ReturnType, genericPositions)};{string.Join(",", function.ParameterTypes.Select(parameter => Get(parameter, genericPositions)))})",
        FunctionValueTypeSymbol function => $"function({Get(function.ReturnType, genericPositions)};{string.Join(",", function.ParameterTypes.Select(parameter => Get(parameter, genericPositions)))})",
        ReferenceTypeSymbol reference => $"ref{(reference.IsReadonly ? "readonly" : "")}({Get(reference.ElementType, genericPositions)})",
        ArrayTypeSymbol array => $"array{array.Rank}({Get(array.ElementType, genericPositions)})",
        AtomicTypeSymbol atomic => $"atomic({Get(atomic.ElementType, genericPositions)})",
        UniqueTypeSymbol unique => $"unique({Get(unique.ElementType, genericPositions)})",
        SharedTypeSymbol shared => $"shared({Get(shared.ElementType, genericPositions)})",
        WeakTypeSymbol weak => $"weak({Get(weak.ElementType, genericPositions)})",
        StorageTypeSymbol storage => $"storage({Get(storage.ElementType, genericPositions)})",
        PinTypeSymbol pin => $"pin({Get(pin.ElementType, genericPositions)})",
        _ => type.Name,
    };

    public static string Parameters(ImmutableArray<ParameterSymbol> parameters) =>
        string.Join(",", parameters.Select(parameter => Get(parameter.Type)));

    public static string Parameters(FunctionSymbol function)
    {
        IReadOnlyDictionary<GenericParameterSymbol, int> positions = function.TypeParameters
            .Select((parameter, index) => (parameter, index))
            .ToDictionary(pair => pair.parameter, pair => pair.index);
        return string.Join(",", function.Parameters.Select(parameter => Get(parameter.Type, positions)));
    }

    public static string Callable(FunctionSymbol function) =>
        $"{(function.OperatorKind is { } kind && kind != OperatorKind.Invalid ? "operator:" + OperatorFacts.GetNativeName(kind) : function.Name)}`{function.TypeParameters.Length}({Parameters(function)})" +
        (function.IsConversionOperator ? $"->{Get(function.ReturnType)}" : "");

    public static string Method(FunctionSymbol method) => Callable(method);
}
