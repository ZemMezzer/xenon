using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class EnumTypeSymbol : TypeSymbol
{
    internal EnumTypeSymbol(string name, NamespaceSymbol containingNamespace) : base(name)
    {
        ContainingNamespace = containingNamespace;
    }

    public NamespaceSymbol ContainingNamespace { get; }
    public string FullName => $"{ContainingNamespace.FullName}.{Name}";
    public PrimitiveTypeSymbol UnderlyingType { get; internal set; } = BuiltinTypes.Int;
    public ImmutableArray<ConstantSymbol> Members { get; internal set; } = [];
    internal ConstantSymbol? FindMember(string name) => Members.FirstOrDefault(member => member.Name == name);
}
