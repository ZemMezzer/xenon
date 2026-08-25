using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Binding;

public sealed record BoundLiteralExpression(object? Value, TypeSymbol LiteralType) : BoundExpression(LiteralType)
{
    public override BoundKind Kind => BoundKind.LiteralExpression;
}

public sealed record BoundVariableExpression(VariableSymbol Variable) : BoundExpression(Variable.Type)
{
    public override BoundKind Kind => BoundKind.VariableExpression;
}

public sealed record BoundThisExpression(StructTypeSymbol StructType, PointerTypeSymbol PointerType) : BoundExpression(PointerType)
{
    public override BoundKind Kind => BoundKind.ThisExpression;
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
    BoundExpression Target,
    SyntaxKind OperatorKind,
    BoundExpression Expression) : BoundExpression(Target.Type)
{
    public override BoundKind Kind => BoundKind.AssignmentExpression;
}

public sealed record BoundMemberAccessExpression(
    BoundExpression Receiver,
    FieldSymbol Field,
    bool IsPointerAccess) : BoundExpression(Field.Type)
{
    public override BoundKind Kind => BoundKind.MemberAccessExpression;
}

public sealed record BoundIndexExpression(
    BoundExpression Receiver,
    BoundExpression Index,
    TypeSymbol ElementType) : BoundExpression(ElementType)
{
    public override BoundKind Kind => BoundKind.IndexExpression;
}

public sealed record BoundStructConstructionExpression(
    StructTypeSymbol StructType,
    ImmutableArray<BoundExpression> Arguments) : BoundExpression(StructType)
{
    public override BoundKind Kind => BoundKind.StructConstructionExpression;
}

public sealed record BoundConstructorCallExpression(
    StructTypeSymbol StructType,
    FunctionSymbol Constructor,
    ImmutableArray<BoundExpression> Arguments) : BoundExpression(StructType)
{
    public override BoundKind Kind => BoundKind.ConstructorCallExpression;
}

public sealed record BoundArrayCreationExpression(
    TypeSymbol ElementType,
    BoundExpression Length,
    ArrayTypeSymbol ArrayType,
    ArrayStorageKind Storage) : BoundExpression(ArrayType)
{
    public override BoundKind Kind => BoundKind.ArrayCreationExpression;
}

public sealed record BoundNewExpression(
    StructTypeSymbol StructType,
    FunctionSymbol? Constructor,
    ImmutableArray<BoundExpression> Arguments,
    bool IsPositionalInitialization,
    PointerTypeSymbol PointerType) : BoundExpression(PointerType)
{
    public override BoundKind Kind => BoundKind.NewExpression;
}

public sealed record BoundFreeExpression(
    BoundExpression Pointer,
    FunctionSymbol? Destructor) : BoundExpression(BuiltinTypes.Void)
{
    public override BoundKind Kind => BoundKind.FreeExpression;
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
