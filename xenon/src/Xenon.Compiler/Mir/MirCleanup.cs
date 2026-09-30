using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir;

/// <summary>Commits a deferred ownership transfer without reading or changing value bits.</summary>
public sealed record MirForget(MirPlace Place, MirSourceInfo Source) : MirStatement(Source);

/// <summary>An exception during unwind cleanup terminates the process.</summary>
public sealed record MirAbort(MirSourceInfo Source) : MirTerminator(Source)
{
    public override IEnumerable<MirEdge> Successors => [];
}
