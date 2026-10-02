using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir;

/// <summary>Read the current runtime exception record, independently of its payload type.</summary>
public sealed record MirCurrentException(PointerTypeSymbol RecordType) : MirRValue(RecordType);
public sealed record MirExceptionMatches(MirOperand Record, TypeSymbol ExceptionType) : MirRValue(BuiltinTypes.Bool);
public sealed record MirExceptionReference(MirOperand Record, ReferenceTypeSymbol ReferenceType) : MirRValue(ReferenceType);

/// <summary>
/// Handle consumes a caught exception on normal exit. Abandon removes an older
/// record when another exception or an abrupt finalizer exit supersedes it.
/// Neither operation hides a source-level control-flow region.
/// </summary>
public sealed record MirReleaseException(MirOperand Record, bool Abandon, MirSourceInfo Source) : MirStatement(Source)
{
    public MirOperand? RestoredRecord { get; init; }
}
