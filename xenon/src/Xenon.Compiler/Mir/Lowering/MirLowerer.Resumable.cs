using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private BoundCallExpression? _completionExpression;
    private ImmutableArray<MirOperand> _completionArguments;
    private List<TemporaryGuard> _completionGuards = [];

    private void Complete(BoundReturnStatement statement)
    {
        if (statement.Expression is null || Unwrap(statement.Expression) is not BoundCallExpression call)
            throw new InvalidOperationException("An async return must invoke its bound completion operator.");
        BoundCallExpression? previous = _completionExpression;
        ImmutableArray<MirOperand> previousArguments = _completionArguments;
        List<TemporaryGuard> previousCompletionGuards = _completionGuards;
        List<TemporaryGuard> previousPendingGuards = _pendingReturnGuards;
        _completionExpression = call;
        _completionArguments = [];
        _completionGuards = [];
        _pendingReturnGuards = _completionGuards;
        try
        {
            _ = Expression(statement.Expression);
            ImmutableArray<MirOperand> arguments = _completionArguments;
            DiscardPending(previousPendingGuards, Source(call));
            List<MirReleaseException>? postponed = call.Function.OperatorKind == OperatorKind.Reject ? [] : null;
            ExitActions(0, postponed);
            if (_current.Terminator is not null) return;
            foreach (TemporaryGuard guard in _completionGuards) EndValueGuard(guard, Source(call));
            Block outer = _unwindTarget;
            Block failed = NewBlock();
            failed.Terminator = new MirAbort(Source(call));
            _unwindTarget = failed;
            _ = CallValues(Callee(call.Function), arguments, Source(call));
            if (postponed is not null) _current.Statements.AddRange(postponed);
            LocalVariableSymbol result = ((BoundVariableDeclarationStatement)_bound.Body.Statements[0]).Variable;
            if (_bound.Body.RequiresSuspensionStateMachine)
            {
                if (TypeFacts.GetCompleteDestructor(result.Type) is { } destructor)
                {
                    Block done = NewBlock();
                    End(new MirDrop(Variable(result), destructor, done.Id, failed.Id, Source(call)));
                    _current = done;
                }
                if (_bound.Symbol.IsCapturingLambda)
                    DestroyCallableControl(Intrinsic(MirIntrinsicKind.ClosureControl, [],
                        _types.PointerTo(BuiltinTypes.Byte), Source(call))!, Source(call));
                End(new MirReturn(null, Source(call)));
            }
            else End(new MirReturn(new MirCopy(Variable(result), result.Type), Source(call)));
            _unwindTarget = outer;
        }
        finally
        {
            foreach (TemporaryGuard guard in _completionGuards) _valueGuards.Remove(guard);
            _completionExpression = previous;
            _completionArguments = previousArguments;
            _completionGuards = previousCompletionGuards;
            _pendingReturnGuards = previousPendingGuards;
        }
    }

    private MirOperand? Await(BoundAwaitExpression expression)
    {
        MirSourceInfo source = Source(expression);
        MirPlace operand = Place(expression.Operand);
        Block outer = _unwindTarget;
        MirPlace? resultStorage = null;
        var storageGuards = new List<TemporaryGuard>();
        if (expression.ResultStorage is { } storage)
        {
            resultStorage = Variable(storage);
            _current.Statements.Add(new MirStorageLive(resultStorage.Local, source));
            _current.Statements.Add(new MirAssign(resultStorage, new MirDefault(storage.Type), source));
            if (TypeFacts.GetCompleteDestructor(storage.Type) is { } destructor)
            {
                var guard = new TemporaryGuard(resultStorage, Temporary(BuiltinTypes.Bool, source), destructor);
                storageGuards.Add(guard);
                SetFlag(guard.Active, true, source);
            }
        }
        Block storageDead = outer;
        if (resultStorage is not null)
        {
            storageDead = NewBlock();
            storageDead.Statements.Add(new MirStorageDead(resultStorage.Local, source));
            storageDead.Terminator = new MirGoto(outer.Id, source);
        }
        Block failure = CleanupChain(storageGuards, storageDead, storageDead, true, source);
        _unwindTarget = failure;
        Block retry = NewBlock(), pending = NewBlock(), ready = NewBlock();
        Jump(retry);
        _current = retry;
        MirPlace continuation = Variable(expression.Continuation);
        _current.Statements.Add(new MirStorageLive(continuation.Local, source));
        MirOperand callback = Intrinsic(MirIntrinsicKind.CreateContinuation, [], expression.Continuation.Type, source)!;
        _current.Statements.Add(new MirAssign(continuation, new MirUse(callback), source));
        var continuationGuard = new TemporaryGuard(continuation, Temporary(BuiltinTypes.Bool, source),
            TypeFacts.GetCompleteDestructor(expression.Continuation.Type)!);
        SetFlag(continuationGuard.Active, true, source);
        _unwindTarget = CleanupChain([continuationGuard], failure, failure, true, source);
        MirPlace? previous = _capturedPlace;
        _capturedPlace = operand;
        MirOperand complete;
        try { complete = Snapshot(Value(expression.Operation), source); }
        finally { _capturedPlace = previous; }
        Block check = NewBlock();
        Jump(CleanupChain([continuationGuard], check, failure, false, source));
        _current = check;
        _current.Statements.Add(new MirStorageDead(continuation.Local, source));
        End(new MirSwitch(complete, [new(new(true, BuiltinTypes.Bool), ready.Id)], pending.Id, source));
        _current = pending;
        _unwindTarget = failure;
        if (resultStorage is not null)
            _ = Intrinsic(MirIntrinsicKind.CheckStorageEmpty, [Address(resultStorage, expression.ResultStorage!.Type, source)], BuiltinTypes.Void, source);
        End(new MirSuspend(null, retry.Id, source));
        _current = ready;
        MirOperand? result = resultStorage is null ? null : MoveStorage(resultStorage, (StorageTypeSymbol)expression.ResultStorage!.Type, source);
        if (resultStorage is not null) _current.Statements.Add(new MirStorageDead(resultStorage.Local, source));
        foreach (TemporaryGuard guard in storageGuards) SetFlag(guard.Active, false, source);
        _unwindTarget = outer;
        return result;
    }
}
