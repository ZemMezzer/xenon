using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

/// <summary>Canonical semantic identity for one constructed generic declaration.</summary>
internal sealed class GenericInstantiationKey : IEquatable<GenericInstantiationKey>
{
    public GenericInstantiationKey(Symbol definition, ImmutableArray<TypeSymbol> typeArguments)
    {
        Definition = definition;
        TypeArguments = typeArguments;
    }

    public Symbol Definition { get; }
    public ImmutableArray<TypeSymbol> TypeArguments { get; }

    public bool Equals(GenericInstantiationKey? other) =>
        other is not null &&
        ReferenceEquals(Definition, other.Definition) &&
        TypeArguments.Length == other.TypeArguments.Length &&
        TypeArguments.Zip(other.TypeArguments).All(pair => TypeIdentity.AreSame(pair.First, pair.Second));

    public override bool Equals(object? obj) => Equals(obj as GenericInstantiationKey);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(RuntimeHelpers.GetHashCode(Definition));
        foreach (TypeSymbol argument in TypeArguments)
            hash.Add(TypeIdentity.GetHashCode(argument));
        return hash.ToHashCode();
    }
}

internal static class GenericTypeFacts
{
    public static bool ContainsGenericParameter(TypeSymbol type) => type switch
    {
        GenericParameterSymbol => true,
        TemplateSelfTypeSymbol => true,
        StructTypeSymbol { GenericDefinition: not null } structure =>
            structure.TypeArguments.Any(ContainsGenericParameter),
        PointerTypeSymbol pointer => ContainsGenericParameter(pointer.ElementType),
        ReferenceTypeSymbol reference => ContainsGenericParameter(reference.ElementType),
        ArrayTypeSymbol array => ContainsGenericParameter(array.ElementType),
        _ => false,
    };
}
