using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Binding;

public sealed record BoundLiteralExpression(
    object? Value,
    TypeSymbol LiteralType) : BoundExpression(LiteralType)
{
    public override BoundKind Kind => BoundKind.LiteralExpression;
}

public sealed record BoundVariableExpression(
    VariableSymbol Variable) : BoundExpression(Variable.Type)
{
    public override BoundKind Kind => BoundKind.VariableExpression;
}

public sealed record BoundUnaryExpression(
    SyntaxKind OperatorKind,
    BoundExpression Operand,
    TypeSymbol ResultType,
    bool IsPostfix = false) : BoundExpression(ResultType)
{
    public override BoundKind Kind => BoundKind.UnaryExpression;
}

public sealed record BoundBinaryExpression(
    BoundExpression Left,
    SyntaxKind OperatorKind,
    BoundExpression Right,
    TypeSymbol ResultType) : BoundExpression(ResultType)
{
    public override BoundKind Kind => BoundKind.BinaryExpression;
}

public sealed record BoundAssignmentExpression(
    VariableSymbol Variable,
    SyntaxKind OperatorKind,
    BoundExpression Expression) : BoundExpression(Variable.Type)
{
    public override BoundKind Kind => BoundKind.AssignmentExpression;
}

public sealed record BoundCallExpression(
    FunctionSymbol Function,
    ImmutableArray<BoundExpression> Arguments) : BoundExpression(Function.ReturnType)
{
    public override BoundKind Kind => BoundKind.CallExpression;
}

public sealed record BoundErrorExpression() : BoundExpression(BuiltinTypes.Error)
{
    public override BoundKind Kind => BoundKind.ErrorExpression;
}
