using System.Collections.Immutable;

namespace Xenon.Compiler.Syntax;

public sealed record BlockStatementSyntax(
    SyntaxToken OpenBraceToken,
    ImmutableArray<StatementSyntax> Statements,
    SyntaxToken CloseBraceToken) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.BlockStatement;
}

public sealed record VariableDeclarationStatementSyntax(
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken? EqualsToken,
    ExpressionSyntax? Initializer,
    SyntaxToken SemicolonToken) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.VariableDeclarationStatement;
}

public sealed record ReturnStatementSyntax(
    SyntaxToken ReturnKeyword,
    ExpressionSyntax? Expression,
    SyntaxToken SemicolonToken) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ReturnStatement;
}

public sealed record ExpressionStatementSyntax(
    ExpressionSyntax Expression,
    SyntaxToken SemicolonToken) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ExpressionStatement;
}
