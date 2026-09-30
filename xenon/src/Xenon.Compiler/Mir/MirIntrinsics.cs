using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir;

/// <summary>Unwraps the value storage, preserving its identity and lifetime state.</summary>
public sealed record MirLifetimeProjection : MirProjection;
public sealed record MirOwnerStorageProjection : MirProjection;

public sealed record MirStaticFieldAddress(FieldSymbol Field, PointerTypeSymbol PointerType) : MirRValue(PointerType);

public sealed record MirInitializeDispatch(MirPlace Place, StructTypeSymbol Type, MirSourceInfo Source) : MirStatement(Source);

public enum MirIntrinsicKind
{
    CloneValue,
    CreateStackArray,
    CreateHeapArray,
    ArrayLength,
    ArrayRank,
    ArrayDimension,
    MoveStorage,
    ConstructStorage,
    DestroyStorage,
    CreateContinuation,
    CheckStorageEmpty,
    CheckStorageInitialized,
    AdoptUnique, AdoptShared, ConvertWeak, LockWeak,
    DestroyFields, DestroyOwner, DestroyCallable,
    Allocate, Malloc, AlignedMalloc, Calloc, Free, Delete,
    MakeCallable, AtomicLoad, AtomicStore, AtomicUpdate, CompareExchange, Swap,
    MarkStorageInitialized,
}

/// <summary>
/// A primitive runtime operation with fully evaluated inputs and explicit
/// exceptional control flow. It cannot contain an expression or statement tree.
/// </summary>
public sealed record MirIntrinsicCall(MirIntrinsicKind Intrinsic, ImmutableArray<MirOperand> Arguments,
    TypeSymbol ResultType, MirPlace? Destination, MirBlockId Normal, MirBlockId Unwind,
    MirSourceInfo Source) : MirTerminator(Source)
{
    public FunctionSymbol? Function { get; init; }
    public TypeSymbol? SubjectType { get; init; }
    public ImmutableArray<CaptureVariableSymbol> Captures { get; init; } = [];
    public MirBinaryOperator? Operator { get; init; }
    public bool ReturnsOldValue { get; init; }
    public override IEnumerable<MirEdge> Successors => [new(Normal, MirEdgeKind.Normal), new(Unwind, MirEdgeKind.Unwind)];
}

public enum MirLayoutQuery { Size, Alignment, FieldOffset }
public sealed record MirTypeLayout(MirLayoutQuery Query, TypeSymbol SubjectType, FieldSymbol? Field) : MirRValue(BuiltinTypes.NUInt);
public sealed record MirInterfaceView(MirOperand Address, StructTypeSymbol SourceType, InterfaceTypeSymbol InterfaceType) : MirRValue(InterfaceType);public sealed record MirAtomicValue(MirOperand Value, AtomicTypeSymbol AtomicType) : MirRValue(AtomicType);
