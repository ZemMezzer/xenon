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

public sealed record IfStatementSyntax(
    SyntaxToken IfKeyword,
    SyntaxToken OpenParenthesisToken,
    ExpressionSyntax Condition,
    SyntaxToken CloseParenthesisToken,
    StatementSyntax ThenStatement,
    SyntaxToken? ElseKeyword,
    StatementSyntax? ElseStatement) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.IfStatement;
}

public sealed record WhileStatementSyntax(
    SyntaxToken WhileKeyword,
    SyntaxToken OpenParenthesisToken,
    ExpressionSyntax Condition,
    SyntaxToken CloseParenthesisToken,
    StatementSyntax Body) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.WhileStatement;
}

public sealed record ForStatementSyntax(
    SyntaxToken ForKeyword,
    SyntaxToken OpenParenthesisToken,
    StatementSyntax? Initializer,
    SyntaxToken FirstSemicolonToken,
    ExpressionSyntax? Condition,
    SyntaxToken SecondSemicolonToken,
    ExpressionSyntax? Increment,
    SyntaxToken CloseParenthesisToken,
    StatementSyntax Body) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ForStatement;
}

public sealed record BreakStatementSyntax(
    SyntaxToken BreakKeyword,
    SyntaxToken SemicolonToken) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.BreakStatement;
}

public sealed record ContinueStatementSyntax(
    SyntaxToken ContinueKeyword,
    SyntaxToken SemicolonToken) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ContinueStatement;
}
