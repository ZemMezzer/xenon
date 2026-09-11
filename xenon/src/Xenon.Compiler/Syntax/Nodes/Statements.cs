using System.Collections.Immutable;

namespace Xenon.Compiler.Syntax;

public sealed record TryStatementSyntax(
    SyntaxToken TryKeyword,
    BlockStatementSyntax Body,
    ImmutableArray<CatchClauseSyntax> Catches,
    SyntaxToken? FinallyKeyword,
    BlockStatementSyntax? FinallyBody) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.TryStatement;
}

public sealed record CatchClauseSyntax(
    SyntaxToken CatchKeyword,
    SyntaxToken OpenParenthesisToken,
    TypeSyntax? Type,
    SyntaxToken? IdentifierToken,
    ImmutableArray<SyntaxToken> EllipsisTokens,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax Body) : SyntaxNode
{
    public bool IsCatchAll => Type is null;
    public override SyntaxKind Kind => SyntaxKind.CatchClause;
}

public sealed record ThrowStatementSyntax(
    SyntaxToken ThrowKeyword,
    ExpressionSyntax? Expression,
    SyntaxToken SemicolonToken) : StatementSyntax
{
    public bool IsRethrow => Expression is null;
    public override SyntaxKind Kind => SyntaxKind.ThrowStatement;
}

public sealed record SwitchStatementSyntax(
    SyntaxToken SwitchKeyword,
    ExpressionSyntax Expression,
    ImmutableArray<SwitchSectionSyntax> Sections) : StatementSyntax
{
    public override SyntaxKind Kind => SyntaxKind.SwitchStatement;
    public SyntaxToken? OpenBraceToken { get; init; }
    public SyntaxToken? CloseBraceToken { get; init; }
}

public sealed record SwitchSectionSyntax(
    SyntaxToken Label,
    ExpressionSyntax? Value,
    ImmutableArray<StatementSyntax> Statements) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.SwitchSection;
}

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
