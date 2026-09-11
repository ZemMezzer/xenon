using System.Diagnostics.CodeAnalysis;

namespace Xenon.Compiler.Syntax;

[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
public abstract record SyntaxNode
{
    public abstract SyntaxKind Kind { get; }
}

public abstract record MemberDeclarationSyntax : SyntaxNode
{
    public SyntaxToken? SecondaryAccessModifierToken { get; init; }
}

public abstract record TypeDeclarationSyntax(SyntaxToken IdentifierToken) : MemberDeclarationSyntax
{
    public SyntaxToken? AccessModifierToken { get; init; }
    public bool IsPublic => AccessModifierToken is null or { Kind: SyntaxKind.PublicKeyword };
    public bool IsInternal => AccessModifierToken?.Kind == SyntaxKind.InternalKeyword;
}

public abstract record TypeMemberDeclarationSyntax : SyntaxNode
{
    public SyntaxToken? SecondaryAccessModifierToken { get; init; }
}

public abstract record StatementSyntax : SyntaxNode;

public abstract record ExpressionSyntax : SyntaxNode;
