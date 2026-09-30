using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir;

/// <summary>Commits a deferred ownership transfer without reading or changing value bits.</summary>
public sealed record MirForget(MirPlace Place, MirSourceInfo Source) : MirStatement(Source);

/// <summary>An exception during unwind cleanup terminates the process.</summary>
public sealed record MirAbort(MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => [];
}

public sealed record MirStorageState(MirPlace Place) : MirRValue(Xenon.Compiler.Semantics.Symbols.BuiltinTypes.Bool);
public sealed record MirSetStorageState(MirPlace Place, bool Initialized, MirSourceInfo Source) : MirStatement(Source);
public sealed record MirStackSave(Xenon.Compiler.Semantics.Symbols.PointerTypeSymbol PointerType) : MirRValue(PointerType);
public sealed record MirStackRestore(MirOperand Token, MirSourceInfo Source) : MirStatement(Source);
public sealed record MirStackAllocation(Xenon.Compiler.Semantics.Symbols.PointerTypeSymbol PointerType) : MirRValue(PointerType);
