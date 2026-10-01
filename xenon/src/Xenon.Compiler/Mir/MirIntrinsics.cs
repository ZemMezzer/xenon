using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir;

/// <summary>Unwraps the value storage, preserving its identity and lifetime state.</summary>
public sealed record MirLifetimeProjection : MirProjection;
public sealed record MirOwnerStorageProjection : MirProjection;
public sealed record MirAtomicStorageProjection : MirProjection;

public sealed record MirStaticFieldAddress(FieldSymbol Field, PointerTypeSymbol PointerType) : MirRValue(PointerType);

public sealed record MirInitializeDispatch(MirPlace Place, StructTypeSymbol Type, MirSourceInfo Source) : MirStatement(Source);

public enum MirStorageCheckPurpose { None, Read, Move, Destruct, Initialize }

public enum MirIntrinsicKind
{
    CoroutineCreate, CoroutineSuspend, CoroutineFree, CoroutineEnd,
    AsyncRootCreate, AsyncRootContinuation, AsyncRootPump, AsyncRootClose,
    CloneValue,
    AllocateStackArray,
    AllocateHeapArray,
    ArrayLength,
    ArrayRank,
    ArrayDimension,
    CreateContinuation, EnsureThreadLocal,
    ReleaseStrong, SharedPayload, ReleaseWeak, CallableControl, ClosureControl,
    ReleaseCallableCount, CallableEnvironment, CallableDestructor,
    CheckStorageEmpty,
    CheckStorageInitialized,
    AdoptUnique, AdoptShared, ConvertWeak, LockWeak,
    Allocate, Malloc, AlignedMalloc, Calloc, Free,
    CompareExchangeOwned, AtomicExchange, MakeCallable, AtomicLoad, AtomicInitialize, AtomicStore, AtomicUpdate, CompareExchange, Swap,
}

/// <summary>
/// A primitive runtime operation with fully evaluated inputs and explicit
/// exceptional control flow. It cannot contain an expression or statement tree.
/// </summary>
public sealed record MirIntrinsicCall(MirIntrinsicKind Intrinsic, ImmutableArray<MirOperand> Arguments,
    TypeSymbol ResultType, MirPlace? Destination, MirBlockId Normal, MirBlockId Unwind,
    MirSourceInfo Source) : MirTerminator(Source)
{
    public MirStorageCheckPurpose StorageCheck { get; init; }
    public FunctionSymbol? Function { get; init; }
    public TypeSymbol? SubjectType { get; init; }
    public FieldSymbol? Field { get; init; }
    public ImmutableArray<CaptureVariableSymbol> Captures { get; init; } = [];
    public MirBinaryOperator? Operator { get; init; }
    public bool ReturnsOldValue { get; init; }
    public ulong? FixedArrayLength { get; init; }
    public bool RetainedInFrame { get; init; }
    public override IEnumerable<MirEdge> Successors => [new(Normal, MirEdgeKind.Normal), new(Unwind, MirEdgeKind.Unwind)];
}

public enum MirLayoutQuery { Size, Alignment, FieldOffset }
public sealed record MirTypeLayout(MirLayoutQuery Query, TypeSymbol SubjectType, FieldSymbol? Field) : MirRValue(BuiltinTypes.NUInt);
public sealed record MirInterfaceView(MirOperand Address, StructTypeSymbol SourceType, InterfaceTypeSymbol InterfaceType) : MirRValue(InterfaceType);

public sealed record MirAtomicValue(MirOperand Value, AtomicTypeSymbol AtomicType) : MirRValue(AtomicType);
