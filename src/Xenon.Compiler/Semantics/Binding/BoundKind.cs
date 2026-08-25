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
    ThisExpression,
    UnaryExpression,
    BinaryExpression,
    AssignmentExpression,
    CallExpression,
    MemberAccessExpression,
    IndexExpression,
    StructConstructionExpression,
    ConstructorCallExpression,
    ArrayCreationExpression,
    NewExpression,
    FreeExpression,
    ErrorExpression,
}
