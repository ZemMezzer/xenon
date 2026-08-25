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
    ImmutableArray<SyntaxToken> PointerTokens,
    SyntaxToken? OpenBracketToken,
    SyntaxToken? CloseBracketToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Type;

    public bool IsConst => ConstKeyword is not null;

    public int PointerDepth => PointerTokens.Length;

    public bool IsArray => OpenBracketToken is not null;

    public bool IsUnsizedArray => IsArray;
}

public sealed record ParameterSyntax(
    TypeSyntax Type,
    SyntaxToken IdentifierToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Parameter;
}

public sealed record FieldDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken SemicolonToken) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.FieldDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
}

public sealed record MethodDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax Body) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.MethodDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
}

public sealed record ConstructorDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax Body) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ConstructorDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
}

public sealed record DestructorDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken TildeToken,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax Body) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.DestructorDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
}

public sealed record StructDeclarationSyntax(
    SyntaxToken StructKeyword,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenBraceToken,
    ImmutableArray<StructMemberDeclarationSyntax> Members,
    SyntaxToken CloseBraceToken) : MemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.StructDeclaration;

    public ImmutableArray<FieldDeclarationSyntax> Fields =>
        Members.OfType<FieldDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<MethodDeclarationSyntax> Methods =>
        Members.OfType<MethodDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<ConstructorDeclarationSyntax> Constructors =>
        Members.OfType<ConstructorDeclarationSyntax>().ToImmutableArray();

    public DestructorDeclarationSyntax? Destructor =>
        Members.OfType<DestructorDeclarationSyntax>().FirstOrDefault();
}

public sealed record FunctionDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? AbiModifierToken,
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

    public bool IsExtern => AbiModifierToken?.Kind == SyntaxKind.ExternKeyword;

    public bool IsExport => AbiModifierToken?.Kind == SyntaxKind.ExportKeyword;

    public bool IsPublic => IsExport || AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
}
