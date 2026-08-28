using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class EnumTypeSymbol : DeclaredTypeSymbol
{
    internal EnumTypeSymbol(string name, NamespaceSymbol containingNamespace, Syntax.EnumDeclarationSyntax declaration) : base(name, containingNamespace)
    {
        Declaration = declaration;
    }

    public override Syntax.EnumDeclarationSyntax Declaration { get; }
    public override string DeclarationKind => "enum";
    public override IEnumerable<Symbol> GetMembers() => Members;
    public PrimitiveTypeSymbol UnderlyingType { get; internal set; } = BuiltinTypes.Int;
    public ImmutableArray<ConstantSymbol> Members { get; internal set; } = [];
    internal ConstantSymbol? FindMember(string name) => Members.FirstOrDefault(member => member.Name == name);
}
