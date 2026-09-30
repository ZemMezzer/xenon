using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private sealed record ExitTarget(MirBlockId Block, int Depth);
    private sealed record ExitAction(BoundBlockStatement? Finally, MirOperand? Record,
        bool Abandon, Block Unwind, Block Rethrow)
    {
        public ExitTarget[] Breaks { get; init; } = [];
        public ExitTarget[] Continues { get; init; } = [];
    }
    private readonly List<ExitAction> _exits = [];

    private void Transfer(ExitTarget target)
    {
        ExitActions(target.Depth);
        if (_current.Terminator is null) End(new MirGoto(target.Block, _functionSource));
    }

    private void ExitActions(int retainedDepth)
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
                if (action.Record is { } record)
                    _current.Statements.Add(new MirReleaseException(record, action.Abandon, _functionSource));
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

    private void Try(BoundTryStatement region)
    {
        Block outerUnwind = _unwindTarget, outerRethrow = _rethrowTarget;
        int depth = _exits.Count;
        Block dispatch = NewBlock(), exceptionalExit = NewBlock(), end = NewBlock();
        if (region.FinallyBody is { } finalizer)
            _exits.Add(new(finalizer, null, false, outerUnwind, outerRethrow) { Breaks = _breaks.ToArray(), Continues = _continues.ToArray() });

        _unwindTarget = _rethrowTarget = dispatch;
        Statement(region.Body);
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
            abandoned.Statements.Add(new MirReleaseException(record, true, _functionSource));
            abandoned.Terminator = new MirGoto(exceptionalExit.Id, _functionSource);
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
            Block replaced = NewBlock();
            replaced.Statements.Add(new MirReleaseException(pending, true, _functionSource));
            replaced.Terminator = new MirGoto(outerUnwind.Id, _functionSource);
            _unwindTarget = replaced;
            // A return/break/continue suppresses the propagating exception. A
            // normal finalizer completion preserves it for the outer handler.
            _exits.Add(new(null, pending, true, outerUnwind, outerRethrow));
            Statement(exceptionalFinalizer);
            _exits.RemoveAt(_exits.Count - 1);
        }
        if (_current.Terminator is null) End(new MirGoto(outerUnwind.Id, _functionSource));
        _unwindTarget = outerUnwind;
        _rethrowTarget = outerRethrow;
        _current = end;
    }
}
