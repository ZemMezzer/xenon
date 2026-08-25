namespace Xenon.Compiler.Semantics.Binding;

public enum BoundKind
{
    BlockStatement,
    VariableDeclarationStatement,
    ReturnStatement,
    ExpressionStatement,
    LiteralExpression,
    VariableExpression,
    UnaryExpression,
    BinaryExpression,
    AssignmentExpression,
    CallExpression,
    ErrorExpression,
}
