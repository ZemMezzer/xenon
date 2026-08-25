namespace Xenon.Compiler.Semantics.Binding;

public enum BoundKind
{
    BlockStatement,
    VariableDeclarationStatement,
    ReturnStatement,
    ExpressionStatement,
    IfStatement,
    WhileStatement,
    ForStatement,
    BreakStatement,
    ContinueStatement,
    LiteralExpression,
    VariableExpression,
    UnaryExpression,
    BinaryExpression,
    AssignmentExpression,
    CallExpression,
    MemberAccessExpression,
    StructConstructionExpression,
    NewExpression,
    FreeExpression,
    ErrorExpression,
}
