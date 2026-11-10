using Xenon.Compiler.Semantics.Binding;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private bool _hasScopeExitCleanup;

    private void ScopeExitCleanup(BoundBlockStatement block, BoundExpression cleanup)
    {
        _hasScopeExitCleanup = true;
        Block outerUnwind = _unwindTarget, outerRethrow = _rethrowTarget;
        Block failure = NewBlock();
        Block rethrow = ReferenceEquals(outerUnwind, outerRethrow) ? failure : NewBlock();
        int depth = _exits.Count;
        _exits.Add(new(null, null, false, outerUnwind, outerRethrow) { Expression = cleanup });
        _unwindTarget = failure;
        _rethrowTarget = rethrow;
        _protectedDepth++;
        try { ScopedBlock(block with { ExitCleanup = null }); }
        finally { _protectedDepth--; }
        if (_current.Terminator is null) ExitActions(depth);
        _exits.RemoveAt(_exits.Count - 1);

        // Normal/return/break/continue paths emit the ordinary delete expression
        // directly. Only unwind edges need exception-record bookkeeping.
        Block normal = _current;
        EmitUnwind(failure, outerUnwind);
        if (!ReferenceEquals(rethrow, failure)) EmitUnwind(rethrow, outerRethrow);
        _unwindTarget = outerUnwind;
        _rethrowTarget = outerRethrow;
        _current = normal;

        void EmitUnwind(Block entry, Block target)
        {
            if (!_blocks.Any(candidate => candidate.Terminator?.Successors.Any(edge => edge.Target == entry.Id) == true)) return;
            _current = entry;
            MirOperand pending = CurrentException();
            Block replaced = NewBlock();
            _unwindTarget = _rethrowTarget = replaced;
            _ = Expression(cleanup);
            Jump(target);
            _current = replaced;
            // A delete cannot return or transfer control out of a finally body.
            // Its one replacement edge abandons the original exception exactly once.
            _current.Statements.Add(new MirReleaseException(pending, true, Source(cleanup)));
            Jump(outerUnwind);
        }
    }
}
