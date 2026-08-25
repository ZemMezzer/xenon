using System.Collections.Immutable;

namespace Xenon.Compiler.Syntax;

public sealed record LiteralExpressionSyntax(SyntaxToken LiteralToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.LiteralExpression;
}

public sealed record NameExpressionSyntax(SyntaxToken IdentifierToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.NameExpression;
}

public sealed record UnaryExpressionSyntax(
    SyntaxToken OperatorToken,
    ExpressionSyntax Operand) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.UnaryExpression;
}

public sealed record PostfixUnaryExpressionSyntax(
    ExpressionSyntax Operand,
    SyntaxToken OperatorToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.PostfixUnaryExpression;
}

public sealed record BinaryExpressionSyntax(
    ExpressionSyntax Left,
    SyntaxToken OperatorToken,
    ExpressionSyntax Right) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.BinaryExpression;
}

public sealed record AssignmentExpressionSyntax(
    ExpressionSyntax Target,
    SyntaxToken OperatorToken,
    ExpressionSyntax Expression) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.AssignmentExpression;
}

public sealed record CallExpressionSyntax(
    ExpressionSyntax Target,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ExpressionSyntax> Arguments,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.CallExpression;
}

public sealed record MemberAccessExpressionSyntax(
    ExpressionSyntax Receiver,
    SyntaxToken OperatorToken,
    SyntaxToken MemberToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.MemberAccessExpression;
}

public sealed record IndexExpressionSyntax(
    ExpressionSyntax Receiver,
    SyntaxToken OpenBracketToken,
    ExpressionSyntax Index,
    SyntaxToken CloseBracketToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.IndexExpression;
}

public sealed record StructPositionalConstructionExpressionSyntax(
    SyntaxToken TypeNameToken,
    SyntaxToken OpenBraceToken,
    ImmutableArray<ExpressionSyntax> Arguments,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseBraceToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.StructPositionalConstructionExpression;
}

public sealed record StackArrayCreationExpressionSyntax(
    TypeSyntax ElementType,
    SyntaxToken OpenBracketToken,
    ExpressionSyntax Length,
    SyntaxToken CloseBracketToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.StackArrayCreationExpression;
}

public sealed record NewExpressionSyntax(
    SyntaxToken NewKeyword,
    TypeSyntax Type,
    SyntaxToken OpenDelimiterToken,
    ImmutableArray<ExpressionSyntax> Arguments,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseDelimiterToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.NewExpression;

    public bool IsArrayAllocation => OpenDelimiterToken.Kind == SyntaxKind.OpenBracketToken;

    public bool IsPositionalInitialization => OpenDelimiterToken.Kind == SyntaxKind.OpenBraceToken;

    public bool IsConstructorCall => OpenDelimiterToken.Kind == SyntaxKind.OpenParenthesisToken;
}

public sealed record FreeExpressionSyntax(
    SyntaxToken FreeKeyword,
    SyntaxToken OpenParenthesisToken,
    ExpressionSyntax Pointer,
    SyntaxToken CloseParenthesisToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.FreeExpression;
}

public sealed record ParenthesizedExpressionSyntax(
    SyntaxToken OpenParenthesisToken,
    ExpressionSyntax Expression,
    SyntaxToken CloseParenthesisToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ParenthesizedExpression;
}
