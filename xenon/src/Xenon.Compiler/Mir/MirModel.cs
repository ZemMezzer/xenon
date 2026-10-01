using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Mir;

public readonly record struct MirLocalId(int Value)
{
    public override string ToString() => $"_{Value}";
}

public readonly record struct MirBlockId(int Value)
{
    public override string ToString() => $"bb{Value}";
}

/// <summary>Source provenance survives lowering, cloning and transformations.</summary>
public sealed record MirSourceInfo(TextLocation Location, int Scope = 0, int? OriginId = null)
{
    public static MirSourceInfo Generated { get; } = new(TextLocation.None);
}

public sealed record MirScope(int Id, int? Parent);

public enum MirLocalKind { Temporary, Variable, Parameter, Receiver, Capture, Return }

public sealed record MirLocal(MirLocalId Id, string Name, TypeSymbol Type,
    MirLocalKind Kind, MirSourceInfo Source)
{
    public VariableSymbol? Variable { get; init; }
    public bool IsCleanupControl { get; init; }
}

public sealed record MirFunction(FunctionSymbol Symbol, ImmutableArray<MirLocal> Locals,
    ImmutableArray<MirBasicBlock> Blocks, MirBlockId Entry, MirSourceInfo Source)
{
    // The executable result can differ from a resumable function's public handle type.
    public TypeSymbol ReturnType { get; init; } = Symbol.ReturnType;
    public MirResumableBody? Resumable { get; init; }
    public Analysis.MirCoroutineLayout? Coroutine { get; init; }
    public bool HasDynamicCleanupOrder { get; init; }
    public bool HasExceptionRegions { get; init; }
    public MirBlockId? UnwindExit { get; init; }
    public MirBlockId? ResumableBodyEntry { get; init; }
    public ImmutableArray<MirScope> Scopes { get; init; } = [new(0, null)];
}

public sealed record MirResumableBody(MirFunction Initialization, MirLocalId Result);

/// <summary>Exactly one terminator; execution cannot implicitly fall through.</summary>
public sealed record MirBasicBlock(MirBlockId Id, ImmutableArray<MirStatement> Statements,
    MirTerminator Terminator);

public abstract record MirProjection;
public sealed record MirFieldProjection(FieldSymbol Field) : MirProjection;
public sealed record MirDerefProjection : MirProjection;
public sealed record MirLinearIndexProjection(MirOperand Index) : MirProjection;
public sealed record MirBaseProjection(StructTypeSymbol BaseType) : MirProjection;
public sealed record MirIndexProjection : MirProjection
{
    public MirIndexProjection(MirOperand index) : this([index]) { }
    public MirIndexProjection(ImmutableArray<MirOperand> indices) => Indices = indices.IsDefault ? [] : indices;
    public ImmutableArray<MirOperand> Indices { get; }
    public bool Equals(MirIndexProjection? other) => other is not null && Indices.SequenceEqual(other.Indices);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (MirOperand index in Indices) hash.Add(index);
        return hash.ToHashCode();
    }
}

/// <summary>
/// A storage location, never a value computation. Structural equality is essential
/// for projection-aware dataflow; ImmutableArray's backing-array equality is not.
/// The empty projection sequence denotes the local itself.
/// </summary>
public sealed class MirPlace : IEquatable<MirPlace>
{
    public MirPlace(MirLocalId local, ImmutableArray<MirProjection> projections = default)
    {
        Local = local;
        Projections = projections.IsDefault ? [] : projections;
    }

    public MirLocalId Local { get; }
    public ImmutableArray<MirProjection> Projections { get; }
    public MirPlace Project(MirProjection projection) => new(Local, Projections.Add(projection));
    public bool IsPrefixOf(MirPlace other) => Local == other.Local &&
        Projections.Length <= other.Projections.Length &&
        Projections.SequenceEqual(other.Projections.Take(Projections.Length));
    public bool Equals(MirPlace? other) => other is not null && Local == other.Local &&
        Projections.SequenceEqual(other.Projections);
    public override bool Equals(object? obj) => obj is MirPlace other && Equals(other);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Local);
        foreach (MirProjection projection in Projections) hash.Add(projection);
        return hash.ToHashCode();
    }
}

public abstract record MirOperand(TypeSymbol Type);
public sealed record MirConstant(object? Value, TypeSymbol ConstantType) : MirOperand(ConstantType);
public sealed record MirCopy(MirPlace Place, TypeSymbol ValueType) : MirOperand(ValueType);
public sealed record MirMove(MirPlace Place, TypeSymbol ValueType) : MirOperand(ValueType)
{
    // A reference-dereferenced value can transfer responsibility from a distinct
    // semantic place. Preserve the binder's ownership identity for drop elaboration.
    public MirPlace? OwnershipPlace { get; init; }
}
public sealed record MirDeferredConstant(TypeSymbol ConstantType) : MirOperand(ConstantType);
public sealed record MirFunctionOperand(FunctionSymbol Function, TypeSymbol CallableType) : MirOperand(CallableType);

/// <summary>A validated structural requirement in an open generic body.</summary>
public enum MirGenericOperation
{
    FunctionCall = 1, FieldGet, FieldSet, PropertyGet, PropertySet,
    IndexerGet, IndexerSet, Construction, Allocation, OperatorCall, MethodCall,
}
public sealed record MirRequirementOperand(Symbol Requirement, MirGenericOperation Operation,
    FunctionPointerTypeSymbol Signature, ImmutableArray<TypeSymbol> TypeArguments,
    int OperatorToken = 0, bool PointerAccess = false) : MirOperand(Signature);

public enum MirUnaryOperator { Negate, Not, BitwiseNot }
public enum MirBinaryOperator
{
    Add, Subtract, Multiply, Divide, Remainder, BitwiseAnd, BitwiseOr, BitwiseXor,
    ShiftLeft, ShiftRight, Equal, NotEqual, Less, LessOrEqual, Greater, GreaterOrEqual,
}
public enum MirBorrowKind { Shared, Exclusive, Raw }

/// <summary>Rvalues are shallow: evaluation order is explicit in statements.</summary>
public abstract record MirRValue(TypeSymbol Type);
public sealed record MirUse(MirOperand Operand) : MirRValue(Operand.Type);
public sealed record MirUnary(MirUnaryOperator Operator, MirOperand Operand, TypeSymbol ResultType) : MirRValue(ResultType);
public sealed record MirBinary(MirBinaryOperator Operator, MirOperand Left, MirOperand Right,
    TypeSymbol ResultType) : MirRValue(ResultType);
public sealed record MirCast(MirOperand Operand, TypeSymbol TargetType) : MirRValue(TargetType);
public sealed record MirBorrow(MirPlace Place, MirBorrowKind Kind, TypeSymbol BorrowType) : MirRValue(BorrowType);
public sealed record MirAggregate(TypeSymbol AggregateType, ImmutableArray<MirOperand> Fields) : MirRValue(AggregateType);
public sealed record MirDefault(TypeSymbol ValueType) : MirRValue(ValueType);

public abstract record MirStatement(MirSourceInfo Source);
public enum MirWriteKind { Initialize, Replace, RawPlacement }
public enum MirPreviousValueState { Live, DefinitelyMoved, MaybeMoved }
public sealed record MirAssign(MirPlace Destination, MirRValue Value, MirSourceInfo Source) : MirStatement(Source)
{
    // A source value read, as distinct from cleanup bookkeeping and ABI copies.
    public bool IsSemanticRead { get; init; }
    public bool IsSemanticWrite { get; init; }
    public bool IsDeclaration { get; init; }
    public bool IsAggregateInitialization { get; init; }
    public bool IsMoveRead { get; init; }
    // Destination of a direct source-level move assignment, before RHS cleanup.
    public MirPlace? TransferDestination { get; init; }
    public bool IsProjectionBaseRead { get; init; }
    // A replacement through an external place cannot test a local lifetime flag.
    public bool IsUntrackedReplacementCheck { get; init; }
    public bool IsPinnedInitializationCheck { get; init; }
    public bool IsArgumentReservationCheck { get; init; }
    public bool IsCompleteReceiverRead { get; init; }
    // Ownership is reserved while later arguments evaluate, then committed by
    // MirForget. An unwind before that commit cancels the reservation.
    public MirPlace? ReservedMove { get; init; }
    public MirWriteKind WriteKind { get; init; } = MirWriteKind.Initialize;
    public MirPreviousValueState PreviousValueState { get; init; }
    public FieldSymbol? ConstructorField { get; init; }
    public bool RequiresRuntimeInitializationCheck { get; init; }
}
/// <summary>A successful wait discharges the operation's borrowed-lifetime obligations.</summary>
public sealed record MirCompleteOperation(MirOperand Operation, MirPlace? Result, MirSourceInfo Source) : MirStatement(Source);
public sealed record MirStorageLive(MirLocalId Local, MirSourceInfo Source) : MirStatement(Source);
public sealed record MirStorageDead(MirLocalId Local, MirSourceInfo Source) : MirStatement(Source);

public enum MirEdgeKind { Normal, Unwind, Resume }
public readonly record struct MirEdge(MirBlockId Target, MirEdgeKind Kind);
public sealed record MirSwitchCase(MirConstant Value, MirBlockId Target);

public abstract record MirTerminator(MirSourceInfo Source)
{
    public abstract IEnumerable<MirEdge> Successors { get; }
}

public sealed record MirGoto(MirBlockId Target, MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => [new(Target, MirEdgeKind.Normal)];
}

public sealed record MirSwitch(MirOperand Value, ImmutableArray<MirSwitchCase> Cases,
    MirBlockId Otherwise, MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => (Cases.IsDefault ? [] : Cases).Select(item => new MirEdge(item.Target, MirEdgeKind.Normal))
        .Append(new(Otherwise, MirEdgeKind.Normal));
}

/// <summary>The destination is initialized only on the normal edge.</summary>
public sealed record MirCall(MirOperand Callee, ImmutableArray<MirOperand> Arguments,
    MirPlace? Destination, MirBlockId Normal, MirBlockId Unwind, MirSourceInfo Source) : MirTerminator(Source)
{
    public MirOperand? Receiver { get; init; }
    public InterfaceTypeSymbol? InterfaceType { get; init; }
    public bool IsIndirectReceiver { get; init; }
    public bool IsVirtual { get; init; }

    public override IEnumerable<MirEdge> Successors => [new(Normal, MirEdgeKind.Normal), new(Unwind, MirEdgeKind.Unwind)];
}

public sealed record MirReturn(MirOperand? Value, MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => [];
}

/// <summary>A null exception rethrows the active exception.</summary>
public sealed record MirThrow(MirOperand? Exception, MirBlockId Unwind, MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => [new(Unwind, MirEdgeKind.Unwind)];
}

public sealed record MirResumeUnwind(MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => [];
}

/// <summary>Generic suspension; the payload is not tied to await syntax.</summary>
public sealed record MirSuspend(MirOperand? Payload, MirBlockId Resume, MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => [new(Resume, MirEdgeKind.Resume)];
}

public sealed record MirUnreachable(MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => [];
}

/// <summary>Destruction may unwind, so a drop is a terminator rather than a hidden call.</summary>
public sealed record MirDrop(MirPlace Place, FunctionSymbol? Destructor,
    MirBlockId Normal, MirBlockId Unwind, MirSourceInfo Source) : MirTerminator(Source)
{
    public bool IsExplicit { get; init; }
    public bool IsVirtual { get; init; }
    public override IEnumerable<MirEdge> Successors => [new(Normal, MirEdgeKind.Normal), new(Unwind, MirEdgeKind.Unwind)];
}
