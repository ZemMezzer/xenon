using System.Collections.Immutable;

namespace Xenon.Compiler.Syntax;

public sealed record CompilationUnitSyntax(
    ImmutableArray<UsingDirectiveSyntax> Usings,
    NamespaceDeclarationSyntax Namespace,
    ImmutableArray<MemberDeclarationSyntax> Members,
    SyntaxToken EndOfFileToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.CompilationUnit;
}

public sealed record UsingDirectiveSyntax(
    SyntaxToken UsingKeyword,
    SyntaxToken? AliasToken,
    SyntaxToken? EqualsToken,
    ImmutableArray<SyntaxToken> NameParts,
    ImmutableArray<SyntaxToken> DotTokens,
    SyntaxToken SemicolonToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.UsingDirective;

    public bool HasAlias => AliasToken is not null;

    public string Name => string.Join('.', NameParts.Select(part => part.Text));
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
    ImmutableArray<SyntaxToken> NameParts,
    ImmutableArray<SyntaxToken> DotTokens,
    ImmutableArray<SyntaxToken> PointerTokens,
    SyntaxToken? ReferenceToken,
    SyntaxToken? OpenBracketToken,
    SyntaxToken? CloseBracketToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Type;

    public SyntaxToken NameToken => NameParts[^1];

    public string Name => string.Join('.', NameParts.Select(part => part.Text));

    public bool IsQualifiedName => NameParts.Length > 1;

    public bool IsConst => ConstKeyword is not null;

    public int PointerDepth => PointerTokens.Length;

    public bool IsReference => ReferenceToken is not null;

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
    SyntaxToken? StaticKeyword,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken? EqualsToken,
    ExpressionSyntax? Initializer,
    SyntaxToken SemicolonToken) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.FieldDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
    public bool IsStatic => StaticKeyword is not null;
}

public sealed record MethodDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? StaticKeyword,
    SyntaxToken? VirtualKeyword,
    SyntaxToken? OverrideKeyword,
    SyntaxToken? AbstractKeyword,
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax? Body,
    SyntaxToken? SemicolonToken) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.MethodDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
    public bool IsStatic => StaticKeyword is not null;
    public bool IsVirtual => VirtualKeyword is not null;
    public bool IsOverride => OverrideKeyword is not null;
    public bool IsAbstract => AbstractKeyword is not null;
}

public sealed record ConstructorDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    SyntaxToken? ColonToken,
    SyntaxToken? BaseKeyword,
    SyntaxToken? BaseOpenParenthesisToken,
    ImmutableArray<ExpressionSyntax> BaseArguments,
    ImmutableArray<SyntaxToken> BaseCommaTokens,
    SyntaxToken? BaseCloseParenthesisToken,
    BlockStatementSyntax Body) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ConstructorDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
}

public sealed record DestructorDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? VirtualKeyword,
    SyntaxToken TildeToken,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax Body) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.DestructorDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;

    public bool IsVirtual => VirtualKeyword is not null;
}

public sealed record StructDeclarationSyntax(
    SyntaxToken StructKeyword,
    SyntaxToken IdentifierToken,
    SyntaxToken? ColonToken,
    ImmutableArray<TypeSyntax> BaseTypes,
    ImmutableArray<SyntaxToken> BaseCommaTokens,
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

public sealed record InterfaceMethodDeclarationSyntax(
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    SyntaxToken SemicolonToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.InterfaceMethodDeclaration;
}

public sealed record InterfaceDeclarationSyntax(
    SyntaxToken InterfaceKeyword,
    SyntaxToken IdentifierToken,
    SyntaxToken? ColonToken,
    ImmutableArray<TypeSyntax> BaseInterfaces,
    ImmutableArray<SyntaxToken> BaseCommaTokens,
    SyntaxToken OpenBraceToken,
    ImmutableArray<InterfaceMethodDeclarationSyntax> Methods,
    SyntaxToken CloseBraceToken) : MemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.InterfaceDeclaration;
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
