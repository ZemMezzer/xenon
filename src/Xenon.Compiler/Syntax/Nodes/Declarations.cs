using System.Collections.Immutable;

namespace Xenon.Compiler.Syntax;

public sealed record CompilationUnitSyntax(
    NamespaceDeclarationSyntax Namespace,
    ImmutableArray<MemberDeclarationSyntax> Members,
    SyntaxToken EndOfFileToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.CompilationUnit;
}

public sealed record NamespaceDeclarationSyntax(
    SyntaxToken NamespaceKeyword,
    ImmutableArray<SyntaxToken> NameParts,
    ImmutableArray<SyntaxToken> DotTokens,
    SyntaxToken SemicolonToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.NamespaceDeclaration;

    public string Name => string.Join('.', NameParts.Select(part => part.Text));
}

public sealed record TypeSyntax(
    SyntaxToken? ConstKeyword,
    SyntaxToken NameToken,
    ImmutableArray<SyntaxToken> PointerTokens) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Type;

    public bool IsConst => ConstKeyword is not null;

    public int PointerDepth => PointerTokens.Length;
}

public sealed record ParameterSyntax(
    TypeSyntax Type,
    SyntaxToken IdentifierToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Parameter;
}

public sealed record FunctionDeclarationSyntax(
    SyntaxToken? ModifierToken,
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax? Body,
    SyntaxToken? SemicolonToken) : MemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.FunctionDeclaration;

    public bool IsExtern => ModifierToken?.Kind == SyntaxKind.ExternKeyword;

    public bool IsExport => ModifierToken?.Kind == SyntaxKind.ExportKeyword;
}
