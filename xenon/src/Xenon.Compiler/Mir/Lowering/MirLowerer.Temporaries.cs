using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private sealed class TemporaryGuard(MirPlace place, MirPlace active, FunctionSymbol destructor)
    {
        public MirPlace Place { get; } = place;
        public MirPlace Active { get; } = active;
        public FunctionSymbol Destructor { get; } = destructor;
        public bool Emitted { get; set; }
    }
    private Dictionary<BoundExpression, TemporaryGuard> _temporaryGuards = new(ReferenceEqualityComparer.Instance);
    private HashSet<BoundMoveExpression> _deferredArgumentMoves = new(ReferenceEqualityComparer.Instance);
    private List<MirPlace> _pendingMoves = [];

    private MirOperand? Expression(BoundExpression expression)
    {
        if (!_temporaryGuards.TryGetValue(expression, out TemporaryGuard? guard)) return ExpressionCore(expression);
        if (!guard.Emitted)
        {
            MirOperand value = ExpressionCore(expression) ?? throw new InvalidOperationException("A void expression cannot own a temporary.");
            _current.Statements.Add(new MirAssign(guard.Place, new MirUse(value), Source(expression)));
            // A directly transferred move remains owned by its source until all
            // arguments succeed. Its uncommitted temporary must not be destroyed.
            if (expression is not BoundMoveExpression move || !_deferredArgumentMoves.Contains(move))
                SetFlag(guard.Active, true, Source(expression));
            guard.Emitted = true;
        }
        return new MirCopy(guard.Place, expression.Type);
    }

    private void SetFlag(MirPlace flag, bool value, MirSourceInfo source) =>
        _current.Statements.Add(new MirAssign(flag, new MirUse(new MirConstant(value, BuiltinTypes.Bool)), source));

    private MirOperand? FullExpression(BoundFullExpression expression)
    {
        MirSourceInfo source = Source(expression);
        Dictionary<BoundExpression, TemporaryGuard> previous = _temporaryGuards;
        Block outerUnwind = _unwindTarget;
        _temporaryGuards = new(previous, ReferenceEqualityComparer.Instance);
        var guards = new List<TemporaryGuard>();
        foreach (BoundFullExpressionTemporary temporary in expression.Temporaries)
        {
            var guard = new TemporaryGuard(Temporary(temporary.Value.Type, source), Temporary(BuiltinTypes.Bool, source), temporary.Destructor);
            guards.Add(guard);
            _temporaryGuards[temporary.Value] = guard;
            _current.Statements.Add(new MirStorageLive(guard.Place.Local, source));
            SetFlag(guard.Active, false, source);
        }
        _unwindTarget = CleanupChain(guards, outerUnwind, outerUnwind, exceptional: true, source);
        try
        {
            MirOperand? result = Expression(expression.Expression);
            Block after = NewBlock();
            Jump(CleanupChain(guards, after, outerUnwind, exceptional: false, source));
            _current = after;
            foreach (TemporaryGuard guard in guards) _current.Statements.Add(new MirStorageDead(guard.Place.Local, source));
            return result;
        }
        finally { _temporaryGuards = previous; _unwindTarget = outerUnwind; }
    }

    // Build from earliest construction toward latest so execution destroys in
    // reverse construction order. The flags are cleared before invoking drops.
    private Block CleanupChain(IReadOnlyList<TemporaryGuard> guards, Block normal, Block unwind,
        bool exceptional, MirSourceInfo source)
    {
        Block next = normal;
        Block? abort = null;
        if (exceptional && guards.Count != 0)
        {
            abort = NewBlock();
            abort.Terminator = new MirAbort(source);
        }
        for (int index = 0; index < guards.Count; index++)
        {
            TemporaryGuard guard = guards[index];
            Block test = NewBlock(), drop = NewBlock();
            test.Terminator = new MirSwitch(new MirCopy(guard.Active, BuiltinTypes.Bool),
                [new(new(true, BuiltinTypes.Bool), drop.Id)], next.Id, source);
            drop.Statements.Add(new MirAssign(guard.Active, new MirUse(new MirConstant(false, BuiltinTypes.Bool)), source));
            Block failure = exceptional ? abort! : CleanupChain(guards.Take(index).ToArray(), unwind, unwind, true, source);
            drop.Terminator = new MirDrop(guard.Place, guard.Destructor, next.Id, failure.Id, source);
            next = test;
        }
        return next;
    }

    private ImmutableArray<MirOperand> Arguments(ImmutableArray<BoundExpression> arguments)
    {
        HashSet<BoundMoveExpression> previousMoves = _deferredArgumentMoves;
        List<MirPlace> previousPending = _pendingMoves;
        _deferredArgumentMoves = new(ReferenceEqualityComparer.Instance);
        _pendingMoves = [];
        foreach (BoundExpression argument in arguments)
            if (Unwrap(argument) is BoundMoveExpression move) _deferredArgumentMoves.Add(move);
        try
        {
            ImmutableArray<MirOperand> values = [.. arguments.Select(argument => Snapshot(Value(argument), Source(argument)))];
            foreach (MirPlace place in _pendingMoves) _current.Statements.Add(new MirForget(place, _functionSource));
            foreach (BoundExpression argument in arguments)
                if (_temporaryGuards.TryGetValue(Unwrap(argument), out TemporaryGuard? guard))
                    SetFlag(guard.Active, false, Source(argument));
            return values;
        }
        finally { _deferredArgumentMoves = previousMoves; _pendingMoves = previousPending; }
    }

    private static BoundExpression Unwrap(BoundExpression expression) => expression is BoundFullExpression full ? Unwrap(full.Expression) : expression;
}
