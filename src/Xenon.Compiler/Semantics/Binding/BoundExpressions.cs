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

public sealed record BoundThisExpression(DeclaredTypeSymbol ContainingType, PointerTypeSymbol PointerType) : BoundExpression(PointerType)
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

public sealed record BoundMoveExpression(
    BoundExpression Source) : BoundExpression(Source.Type)
{
    // The semantic place is retained because a reference expression may have
    // been dereferenced in the bound tree.  Lowering uses this identity to
    // transfer the original place's drop responsibility.
    public VariableSymbol? TrackedVariable { get; init; }
    public ImmutableArray<FieldSymbol> TrackedPath { get; init; } = [];
    public override BoundKind Kind => BoundKind.MoveExpression;
}

/// <summary>A semantic by-value copy. Fresh temporaries and explicit moves bypass this node.</summary>
public sealed record BoundCopyExpression(
    BoundExpression Source) : BoundExpression(Source.Type)
{
    public override BoundKind Kind => BoundKind.CopyExpression;
}

/// <summary>Compiler-generated finalization after a user destructor body: own fields, then base.</summary>
public sealed record BoundDropFieldsExpression(
    StructTypeSymbol StructType) : BoundExpression(BuiltinTypes.Void)
{
    public override BoundKind Kind => BoundKind.DropFieldsExpression;
}

/// <summary>Compiler-generated drop of the unique handle passed to an ownership helper.</summary>
public sealed record BoundOwnershipDropExpression(
    OwnershipTypeSymbol OwnershipType,
    FunctionSymbol? ElementDrop) : BoundExpression(BuiltinTypes.Void)
{
    public override BoundKind Kind => BoundKind.OwnershipDropExpression;
}

/// <summary>A fresh heap result adopted directly into its first and only owner.</summary>
public sealed record BoundUniqueAdoptionExpression(
    BoundExpression Allocation,
    UniqueTypeSymbol UniqueType) : BoundExpression(UniqueType)
{
    public override BoundKind Kind => BoundKind.UniqueAdoptionExpression;
}

public sealed record BoundSharedAdoptionExpression(
    BoundExpression Allocation,
    SharedTypeSymbol SharedType) : BoundExpression(SharedType)
{
    public override BoundKind Kind => BoundKind.SharedAdoptionExpression;
}

public sealed record BoundWeakConversionExpression(
    BoundExpression Shared,
    WeakTypeSymbol WeakType) : BoundExpression(WeakType)
{
    public override BoundKind Kind => BoundKind.WeakConversionExpression;
}

public sealed record BoundWeakLockExpression(
    BoundExpression Weak,
    SharedTypeSymbol SharedType) : BoundExpression(SharedType)
{
    public override BoundKind Kind => BoundKind.WeakLockExpression;
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
    public bool IsInitialization { get; init; }
    public bool ReinitializesMovedPlace { get; init; }
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
    public ImmutableArray<BoundExpression> Indices { get; init; } = [Index];
}

public sealed record BoundStructConstructionExpression(
    StructTypeSymbol StructType,
    ImmutableArray<BoundExpression> Arguments) : BoundExpression(StructType)
{
    public override BoundKind Kind => BoundKind.StructConstructionExpression;
    public bool IsDefaultInitialization { get; init; }
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
    public ImmutableArray<BoundExpression> Dimensions { get; init; } = [Length];
}

public sealed record BoundArrayMetadataExpression(
    BoundExpression Receiver,
    string Member,
    BoundExpression? Dimension = null) : BoundExpression(BuiltinTypes.Int)
{
    public override BoundKind Kind => BoundKind.ArrayMetadataExpression;
}

public sealed record BoundNewExpression(
    TypeSymbol AllocatedType,
    FunctionSymbol? Constructor,
    ImmutableArray<BoundExpression> Arguments,
    bool IsPositionalInitialization,
    PointerTypeSymbol PointerType) : BoundExpression(PointerType)
{
    public override BoundKind Kind => BoundKind.NewExpression;
    public StructTypeSymbol? StructType => AllocatedType as StructTypeSymbol;
    public bool IsDefaultInitialization { get; init; }
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

// A nominal enum value whose numeric value must be recomputed once a target is selected.
// This node is never passed to LLVM emission.
public sealed record BoundDeferredConstantExpression(TypeSymbol ConstantType) : BoundExpression(ConstantType)
{
    public override BoundKind Kind => BoundKind.DeferredConstantExpression;
}
