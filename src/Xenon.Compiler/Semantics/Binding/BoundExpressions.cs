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

public sealed record BoundCompoundAccessorAssignmentExpression(
    BoundExpression Receiver,
    FunctionSymbol Getter,
    FunctionSymbol Setter,
    ImmutableArray<BoundExpression> Arguments,
    SyntaxKind OperatorKind,
    BoundExpression Value,
    bool IsPointerAccess,
    InterfaceTypeSymbol? InterfaceType) : BoundExpression(Getter.ReturnType)
{
    public override BoundKind Kind => BoundKind.CompoundAccessorAssignmentExpression;
}

public sealed record BoundMethodCallExpression(
    BoundExpression Receiver,
    FunctionSymbol Method,
    ImmutableArray<BoundExpression> Arguments,
    bool IsPointerAccess) : BoundExpression(Method.ReturnType)
{
    public override BoundKind Kind => BoundKind.MethodCallExpression;
}

public sealed record BoundPropertySetExpression(
    BoundExpression Receiver,
    PropertySymbol Property,
    BoundExpression Value,
    bool IsPointerAccess) : BoundExpression(Property.Type)
{
    public override BoundKind Kind => BoundKind.PropertySetExpression;
}

public sealed record BoundInterfacePropertySetExpression(
    BoundExpression Receiver,
    InterfaceTypeSymbol InterfaceType,
    InterfacePropertySymbol Property,
    BoundExpression Value,
    bool IsPointerAccess) : BoundExpression(Property.Type)
{
    public override BoundKind Kind => BoundKind.InterfacePropertySetExpression;
}

public sealed record BoundIndexerSetExpression(
    BoundExpression Receiver,
    IndexerSymbol Indexer,
    ImmutableArray<BoundExpression> Arguments,
    BoundExpression Value) : BoundExpression(Indexer.Type)
{
    public override BoundKind Kind => BoundKind.IndexerSetExpression;
}

public sealed record BoundInterfaceIndexerSetExpression(
    BoundExpression Receiver,
    InterfaceTypeSymbol InterfaceType,
    InterfaceIndexerSymbol Indexer,
    ImmutableArray<BoundExpression> Arguments,
    BoundExpression Value) : BoundExpression(Indexer.Type)
{
    public override BoundKind Kind => BoundKind.InterfaceIndexerSetExpression;
}

public sealed record BoundMemberAccessExpression(
    BoundExpression Receiver,
    FieldSymbol Field,
    bool IsPointerAccess) : BoundExpression(Field.Type)
{
    public override BoundKind Kind => BoundKind.MemberAccessExpression;
}

public sealed record BoundStaticFieldExpression(FieldSymbol Field) : BoundExpression(Field.Type)
{
    public override BoundKind Kind => BoundKind.StaticFieldExpression;
}

public sealed record BoundTypeLayoutExpression(
    SyntaxKind OperatorKind,
    TypeSymbol TargetType,
    FieldSymbol? Field) : BoundExpression(BuiltinTypes.NUInt)
{
    public override BoundKind Kind => BoundKind.TypeLayoutExpression;
}

public sealed record BoundCastExpression(
    BoundExpression Expression,
    TypeSymbol TargetType) : BoundExpression(TargetType)
{
    public override BoundKind Kind => BoundKind.CastExpression;
}

public sealed record BoundInterfaceConversionExpression(
    BoundExpression Source,
    StructTypeSymbol SourceType,
    InterfaceTypeSymbol InterfaceType) : BoundExpression(InterfaceType)
{
    public override BoundKind Kind => BoundKind.InterfaceConversionExpression;
}

public sealed record BoundReferenceConversionExpression(
    BoundExpression Source,
    ReferenceTypeSymbol ReferenceType) : BoundExpression(ReferenceType)
{
    public override BoundKind Kind => BoundKind.ReferenceConversionExpression;
}

public sealed record BoundReferenceDereferenceExpression(
    BoundExpression Reference,
    ReferenceTypeSymbol ReferenceType) : BoundExpression(ReferenceType.ElementType)
{
    public override BoundKind Kind => BoundKind.ReferenceDereferenceExpression;
}

public sealed record BoundInterfaceMethodCallExpression(
    BoundExpression Receiver,
    InterfaceTypeSymbol InterfaceType,
    FunctionSymbol Method,
    ImmutableArray<BoundExpression> Arguments,
    bool IsPointerAccess) : BoundExpression(Method.ReturnType)
{
    public override BoundKind Kind => BoundKind.InterfaceMethodCallExpression;
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

public sealed record BoundBaseLifecycleCallExpression(
    FunctionSymbol Function,
    ImmutableArray<BoundExpression> Arguments) : BoundExpression(BuiltinTypes.Void)
{
    public override BoundKind Kind => BoundKind.BaseLifecycleCallExpression;
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
