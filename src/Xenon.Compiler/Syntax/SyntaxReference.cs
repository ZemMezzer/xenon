using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

/// <summary>
/// An explicit declaration and its identifying token. Coordinates and file identity
/// are derived from the syntax's source snapshot, never copied from a binder.
/// The span selects the declared name (or an accessor/indexer keyword), not its body.
/// </summary>
public sealed class SyntaxReference
{
    public SyntaxReference(SyntaxNode declaration, int namespacePartIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (declaration is NamespaceDeclarationSyntax ns)
        {
            if (namespacePartIndex == -1) namespacePartIndex = ns.NameParts.Length - 1;
            ArgumentOutOfRangeException.ThrowIfNegative(namespacePartIndex);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(namespacePartIndex, ns.NameParts.Length);
        }
        else if (namespacePartIndex != -1)
        {
            throw new ArgumentException("Only namespace declarations have name parts.", nameof(namespacePartIndex));
        }
        Declaration = declaration;
        NamespacePartIndex = namespacePartIndex;
        _ = IdentifierToken; // Reject nodes that do not declare a symbol.
    }

    public SyntaxNode Declaration { get; }
    public int NamespacePartIndex { get; }
    public SourceText Source => Location.Source;
    public TextSpan Span => Location.Span;
    public string Path => Location.Path;
    public TextLocation Location => IdentifierToken.Location;

    public SyntaxToken IdentifierToken => Declaration switch
    {
        NamespaceDeclarationSyntax syntax => syntax.NameParts[NamespacePartIndex],
        TypeDeclarationSyntax syntax => syntax.IdentifierToken,
        FunctionDeclarationSyntax syntax => syntax.IdentifierToken,
        GenericParameterSyntax syntax => syntax.IdentifierToken,
        MethodDeclarationSyntax syntax => syntax.IdentifierToken,
        InterfaceMethodDeclarationSyntax syntax => syntax.IdentifierToken,
        ConstructorDeclarationSyntax syntax => syntax.IdentifierToken,
        TemplateConstructorDeclarationSyntax syntax => syntax.IdentifierToken,
        DestructorDeclarationSyntax syntax => syntax.IdentifierToken,
        FieldDeclarationSyntax syntax => syntax.IdentifierToken,
        PropertyDeclarationSyntax syntax => syntax.IdentifierToken,
        InterfacePropertyDeclarationSyntax syntax => syntax.IdentifierToken,
        IndexerDeclarationSyntax syntax => syntax.ThisKeyword,
        InterfaceIndexerDeclarationSyntax syntax => syntax.ThisKeyword,
        PropertyAccessorDeclarationSyntax syntax => syntax.KeywordToken,
        ModuleConstantDeclarationSyntax syntax => syntax.IdentifierToken,
        TypeConstantDeclarationSyntax syntax => syntax.IdentifierToken,
        EnumMemberDeclarationSyntax syntax => syntax.IdentifierToken,
        ParameterSyntax syntax => syntax.IdentifierToken,
        VariableDeclarationStatementSyntax syntax => syntax.IdentifierToken,
        _ => throw new ArgumentException($"Syntax kind '{Declaration.Kind}' is not a symbol declaration."),
    };
}
