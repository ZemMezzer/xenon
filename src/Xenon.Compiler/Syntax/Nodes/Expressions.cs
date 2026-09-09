using System.Collections.Immutable;

namespace Xenon.Compiler.Syntax;

public enum LambdaCaptureKind
{
    Value,
    MutableBorrow,
    ReadonlyBorrow,
    Move,
}

public sealed record LambdaCaptureSyntax(
    SyntaxToken? ReadonlyKeyword,
    SyntaxToken? AmpersandToken,
    SyntaxToken? MoveKeyword,
    SyntaxToken IdentifierToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.LambdaCapture;
    public LambdaCaptureKind CaptureKind => MoveKeyword is not null ? LambdaCaptureKind.Move :
        AmpersandToken is not null && ReadonlyKeyword is not null ? LambdaCaptureKind.ReadonlyBorrow :
        AmpersandToken is not null ? LambdaCaptureKind.MutableBorrow : LambdaCaptureKind.Value;
}

public sealed record LambdaExpressionSyntax(
    SyntaxToken? OpenBracketToken,
    ImmutableArray<LambdaCaptureSyntax> Captures,
    ImmutableArray<SyntaxToken> CaptureCommaTokens,
    SyntaxToken? CloseBracketToken,
    SyntaxToken? FunctionKeyword,
    TypeSyntax? ExplicitReturnType,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    SyntaxToken? FatArrowToken,
    BlockStatementSyntax Body) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.LambdaExpression;
    public SyntaxToken IntroducerToken => FunctionKeyword ?? OpenBracketToken ?? OpenParenthesisToken;
}

public sealed record MissingExpressionSyntax(SyntaxToken MissingToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.MissingExpression;
}

public sealed record LiteralExpressionSyntax(SyntaxToken LiteralToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.LiteralExpression;
}

public sealed record NameExpressionSyntax(SyntaxToken IdentifierToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.NameExpression;
}

public sealed record ThisExpressionSyntax(SyntaxToken ThisKeyword) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ThisExpression;
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

public sealed record CompareExchangeExpressionSyntax(
    ExpressionSyntax Target,
    SyntaxToken ColonToken,
    ExpressionSyntax Expected,
    SyntaxToken ArrowToken,
    ExpressionSyntax Desired) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.CompareExchangeExpression;
}

public sealed record SwapExpressionSyntax(
    ExpressionSyntax Left,
    SyntaxToken OperatorToken,
    ExpressionSyntax Right) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.SwapExpression;
}

public sealed record CallExpressionSyntax(
    ExpressionSyntax Target,
    TypeArgumentListSyntax? TypeArguments,
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
    public TypeArgumentListSyntax? ReceiverTypeArguments { get; init; }
}

public sealed record IndexExpressionSyntax(
    ExpressionSyntax Receiver,
    SyntaxToken OpenBracketToken,
    ImmutableArray<ExpressionSyntax> Arguments,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseBracketToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.IndexExpression;

    public ExpressionSyntax Index => Arguments[0];
}

public sealed record StructPositionalConstructionExpressionSyntax(
    TypeSyntax Type,
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
    ImmutableArray<ExpressionSyntax> Dimensions,
    ImmutableArray<SyntaxToken> CommaTokens,
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

public sealed record MoveExpressionSyntax(
    SyntaxToken MoveKeyword,
    ExpressionSyntax Operand) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.MoveExpression;
}

public sealed record LockExpressionSyntax(
    SyntaxToken LockKeyword,
    ExpressionSyntax Operand) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.LockExpression;
}

public sealed record ParenthesizedExpressionSyntax(
    SyntaxToken OpenParenthesisToken,
    ExpressionSyntax Expression,
    SyntaxToken CloseParenthesisToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ParenthesizedExpression;
}

public sealed record TypeLayoutExpressionSyntax(
    SyntaxToken Keyword,
    SyntaxToken OpenParenthesisToken,
    TypeSyntax Type,
    SyntaxToken? CommaToken,
    SyntaxToken? FieldToken,
    SyntaxToken CloseParenthesisToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.TypeLayoutExpression;
}

public sealed record CastExpressionSyntax(
    SyntaxToken CastKeyword,
    SyntaxToken LessToken,
    TypeSyntax Type,
    SyntaxToken GreaterToken,
    SyntaxToken OpenParenthesisToken,
    ExpressionSyntax Expression,
    SyntaxToken CloseParenthesisToken) : ExpressionSyntax
{
    public override SyntaxKind Kind => SyntaxKind.CastExpression;
}
