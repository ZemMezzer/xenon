using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class EnumTypeSymbol : DeclaredTypeSymbol
{
    internal EnumTypeSymbol(string name, NamespaceSymbol containingNamespace, Syntax.EnumDeclarationSyntax declaration)
        : base(name, containingNamespace, "enum", origin: SymbolOrigin.Source(declaration),
            documentation: SymbolDocumentation.FromDeclaration(declaration),
            accessibility: AccessibilityFacts.FromSyntax(declaration.AccessModifierToken,
                declaration.SecondaryAccessModifierToken, Accessibility.Public))
    {
        Declaration = declaration;
        SetImplementation(new SourceSymbolImplementation(declaration));
    }

    internal EnumTypeSymbol(string name, NamespaceSymbol containingNamespace,
        SymbolOrigin? origin = null, SymbolDocumentation? documentation = null,
        Accessibility accessibility = Accessibility.Public)
        : base(name, containingNamespace, "enum", origin: origin, documentation: documentation,
            accessibility: accessibility) { }

    internal Syntax.EnumDeclarationSyntax Declaration { get; } = null!;
    public override IEnumerable<Symbol> GetMembers() => Members.Cast<Symbol>().Concat(StaticFields);
    public PrimitiveTypeSymbol UnderlyingType { get; internal set; } = BuiltinTypes.Int;
    public ImmutableArray<ConstantSymbol> Members { get; internal set; } = [];
    public ImmutableArray<FieldSymbol> StaticFields { get; internal set; } = [];
    internal ConstantSymbol? FindMember(string name) => Members.FirstOrDefault(member => member.Name == name);
}
