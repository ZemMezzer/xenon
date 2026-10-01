using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private sealed record ExitTarget(MirBlockId Block, int Depth);
    private sealed record ExitAction(BoundBlockStatement? Finally, MirOperand? Record,
        bool Abandon, Block Unwind, Block Rethrow)
    {
        public LexicalCleanup? Cleanup { get; init; }
        public ExitTarget[] Breaks { get; init; } = [];
        public ExitTarget[] Continues { get; init; } = [];
    }
    private readonly List<ExitAction> _exits = [];
    private sealed record PendingException(MirOperand Record, MirPlace Active, int ProtectedDepth);
    private readonly List<PendingException> _pendingExceptions = [];
    private int _protectedDepth;

    private void AbandonPending(PendingException pending)
    {
        Block release = NewBlock(), after = NewBlock();
        End(new MirSwitch(new MirCopy(pending.Active, BuiltinTypes.Bool),
            [new(new(true, BuiltinTypes.Bool), release.Id)], after.Id, _functionSource));
        _current = release;
        SetFlag(pending.Active, false, _functionSource);
        _current.Statements.Add(new MirReleaseException(pending.Record, true, _functionSource));
        Jump(after);
        _current = after;
    }

    private void Transfer(ExitTarget target)
    {
        ExitActions(target.Depth);
        if (_current.Terminator is null) End(new MirGoto(target.Block, _functionSource));
    }

    private void ExitActions(int retainedDepth, List<MirReleaseException>? postponedRecords = null)
    {
        ExitAction[] saved = _exits.ToArray();
        Block unwind = _unwindTarget, rethrow = _rethrowTarget;
        try
        {
            for (int index = saved.Length - 1; index >= retainedDepth && _current.Terminator is null; index--)
            {
                ExitAction action = saved[index];
                _exits.RemoveAt(index);
                _unwindTarget = action.Unwind;
                _rethrowTarget = action.Rethrow;
                if (action.Cleanup is { } cleanup)
                {
                    Block after = NewBlock();
                    Jump(CleanupChain(cleanup.Guards, after, GuardedUnwind(action.Unwind, _functionSource), false, _functionSource));
                    _current = after;
                    foreach (MirLocalId local in cleanup.Locals)
                        _current.Statements.Add(new MirStorageDead(local, _functionSource));
                    if (cleanup.Stack is { } stack) _current.Statements.Add(new MirStackRestore(stack, _functionSource));
                }
                if (action.Record is { } record)
                {
                    var release = new MirReleaseException(record, action.Abandon, _functionSource)
                    {
                        RestoredRecord = action.Abandon ? null : saved.Take(index).LastOrDefault(exit => exit.Record is not null)?.Record,
                    };
                    if (postponedRecords is not null && !action.Abandon) postponedRecords.Add(release);
                    else _current.Statements.Add(release);
                }
                if (action.Finally is { } finalizer)
                {
                    ExitTarget[] breaks = _breaks.ToArray(), continues = _continues.ToArray();
                    Restore(_breaks, action.Breaks);
                    Restore(_continues, action.Continues);
                    try { Statement(finalizer); }
                    finally { Restore(_breaks, breaks); Restore(_continues, continues); }
                }
            }
        }
        finally
        {
            _exits.Clear();
            _exits.AddRange(saved);
            _unwindTarget = unwind;
            _rethrowTarget = rethrow;
        }
    }

    private static void Restore(Stack<ExitTarget> stack, ExitTarget[] values)
    {
        stack.Clear();
        foreach (ExitTarget value in values.Reverse()) stack.Push(value);
    }

    private MirOperand CurrentException() => Save(new MirCurrentException(_types.PointerTo(BuiltinTypes.Byte)), _functionSource);

    private bool _hasExceptionRegions;
    private void Try(BoundTryStatement region, bool isSourceRegion = true)
    {
        _hasExceptionRegions |= isSourceRegion;
        Block outerUnwind = _unwindTarget, outerRethrow = _rethrowTarget;
        int depth = _exits.Count;
        Block dispatch = NewBlock(), exceptionalExit = NewBlock(), end = NewBlock();
        if (region.FinallyBody is { } finalizer)
            _exits.Add(new(finalizer, null, false, outerUnwind, outerRethrow) { Breaks = _breaks.ToArray(), Continues = _continues.ToArray() });

        _unwindTarget = _rethrowTarget = dispatch;
        _protectedDepth++;
        try { Statement(region.Body); }
        finally { _protectedDepth--; }
        if (_current.Terminator is null) { ExitActions(depth); Jump(end); }

        // Handler bodies throw past sibling handlers; only the protected body
        // enters this dispatch. Match checks are explicit scalar branches.
        _current = dispatch;
        MirOperand record = CurrentException();
        foreach (BoundCatchClause handler in region.Catches)
        {
            Block body = NewBlock(), next = NewBlock();
            if (handler.Type is null) Jump(body);
            else
            {
                MirOperand matches = Save(new MirExceptionMatches(record, handler.Type), _functionSource);
                End(new MirSwitch(matches, [new(new(true, BuiltinTypes.Bool), body.Id)], next.Id, _functionSource));
            }
            Block abandoned = NewBlock();
            _current = abandoned;
            // A replacement escaping a finalizer suppresses its original record
            // before abandoning the nested catch record. Track the exact record
            // and flag so the outer exceptional exit cannot release it twice.
            foreach (PendingException pendingException in _pendingExceptions.AsEnumerable().Reverse())
                if (_protectedDepth <= pendingException.ProtectedDepth) AbandonPending(pendingException);
            _current.Statements.Add(new MirReleaseException(record, true, _functionSource));
            Jump(exceptionalExit);
            _current = body;
            if (handler.Variable is { } variable)
            {
                MirPlace destination = Variable(variable);
                _current.Statements.Add(new MirStorageLive(destination.Local, _functionSource));
                _current.Statements.Add(new MirAssign(destination,
                    new MirExceptionReference(record, (ReferenceTypeSymbol)variable.Type), _functionSource));
            }
            _unwindTarget = abandoned;
            _rethrowTarget = exceptionalExit;
            _exits.Add(new(null, record, false, exceptionalExit, exceptionalExit));
            Statement(handler.Body);
            if (_current.Terminator is null) { ExitActions(depth); Jump(end); }
            _exits.RemoveAt(_exits.Count - 1);
            _current = next;
        }
        Jump(exceptionalExit);
        if (region.FinallyBody is not null) _exits.RemoveAt(_exits.Count - 1);

        _current = exceptionalExit;
        _unwindTarget = outerUnwind;
        _rethrowTarget = outerRethrow;
        if (region.FinallyBody is { } exceptionalFinalizer)
        {
            MirOperand pending = CurrentException();
            var pendingScope = new PendingException(pending, Temporary(BuiltinTypes.Bool, _functionSource), _protectedDepth);
            SetFlag(pendingScope.Active, true, _functionSource);
            Block finalizerEntry = _current, replaced = NewBlock();
            _current = replaced;
            AbandonPending(pendingScope);
            Jump(outerUnwind);
            _current = finalizerEntry;
            _unwindTarget = replaced;
            _pendingExceptions.Add(pendingScope);
            // A return/break/continue suppresses the propagating exception. A
            // normal finalizer completion preserves it for the outer handler.
            _exits.Add(new(null, pending, true, outerUnwind, outerRethrow));
            _exceptionalFinalizerDepth++;
            try { Statement(exceptionalFinalizer); }
            finally { _exceptionalFinalizerDepth--; _pendingExceptions.RemoveAt(_pendingExceptions.Count - 1); }
            _exits.RemoveAt(_exits.Count - 1);
        }
        if (_current.Terminator is null) End(new MirGoto(outerUnwind.Id, _functionSource));
        _unwindTarget = outerUnwind;
        _rethrowTarget = outerRethrow;
        _current = end;
    }
}
