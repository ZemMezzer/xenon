namespace Xenon.Compiler.Syntax;

public abstract record SyntaxNode
{
    public abstract SyntaxKind Kind { get; }
}

public abstract record MemberDeclarationSyntax : SyntaxNode;

public abstract record TypeDeclarationSyntax(SyntaxToken IdentifierToken) : MemberDeclarationSyntax;

public abstract record TypeMemberDeclarationSyntax : SyntaxNode;

public abstract record StatementSyntax : SyntaxNode;

public abstract record ExpressionSyntax : SyntaxNode;
