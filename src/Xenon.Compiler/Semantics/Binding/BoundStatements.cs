using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics.Binding;

public sealed record BoundBlockStatement(
    ImmutableArray<BoundStatement> Statements) : BoundStatement
{
    public override BoundKind Kind => BoundKind.BlockStatement;
}

public sealed record BoundVariableDeclarationStatement(
    LocalVariableSymbol Variable,
    BoundExpression? Initializer) : BoundStatement
{
    public override BoundKind Kind => BoundKind.VariableDeclarationStatement;
}

public sealed record BoundReturnStatement(
    BoundExpression? Expression) : BoundStatement
{
    public override BoundKind Kind => BoundKind.ReturnStatement;
}

public sealed record BoundExpressionStatement(
    BoundExpression Expression) : BoundStatement
{
    public override BoundKind Kind => BoundKind.ExpressionStatement;
}

public sealed record BoundIfStatement(
    BoundExpression Condition,
    BoundStatement ThenStatement,
    BoundStatement? ElseStatement) : BoundStatement
{
    public override BoundKind Kind => BoundKind.IfStatement;
}

public sealed record BoundWhileStatement(
    BoundExpression Condition,
    BoundStatement Body) : BoundStatement
{
    public override BoundKind Kind => BoundKind.WhileStatement;
}

public sealed record BoundForStatement(
    BoundStatement? Initializer,
    BoundExpression? Condition,
    BoundExpression? Increment,
    BoundStatement Body) : BoundStatement
{
    public override BoundKind Kind => BoundKind.ForStatement;
}

public sealed record BoundBreakStatement() : BoundStatement
{
    public override BoundKind Kind => BoundKind.BreakStatement;
}

public sealed record BoundContinueStatement() : BoundStatement
{
    public override BoundKind Kind => BoundKind.ContinueStatement;
}
