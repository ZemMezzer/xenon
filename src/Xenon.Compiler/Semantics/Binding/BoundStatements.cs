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
