using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private sealed record LexicalCleanup(List<TemporaryGuard> Guards, MirLocalId[] Locals, MirOperand? Stack);
    private readonly Dictionary<MirPlace, TemporaryGuard> _ownedPlaces = [];
    private readonly List<TemporaryGuard> _valueGuards = [];
    private bool _parametersRegistered;
    private bool _hasDynamicCleanupOrder;
    private List<TemporaryGuard> _pendingReturnGuards = [];

    private void Return(BoundReturnStatement statement)
    {
        MirSourceInfo source = statement.Expression is null ? _functionSource : Source(statement.Expression);
        MirOperand? returned = statement.Expression is null ? null :
            Snapshot(ConvertValue(Value(statement.Expression), _bound.Symbol.ReturnType, source), source);
        TemporaryGuard? guard = returned is null ? null : GuardValue(returned, source);
        List<TemporaryGuard> previous = _pendingReturnGuards;
        _pendingReturnGuards = guard is null ? [] : [guard];
        try
        {
            DiscardPending(previous, source);
            ExitActions(0);
            if (_current.Terminator is null)
            {
                EndValueGuard(guard, source);
                End(new MirReturn(returned, source));
            }
        }
        finally
        {
            if (guard is not null) _valueGuards.Remove(guard);
            _pendingReturnGuards = previous;
        }
    }

    private void DiscardPending(IReadOnlyList<TemporaryGuard> guards, MirSourceInfo source)
    {
        if (guards.Count == 0) return;
        Block after = NewBlock();
        Jump(CleanupChain(guards, after, GuardedUnwind(_unwindTarget, source), false, source));
        _current = after;
    }

    private void Embedded(BoundStatement statement) => Statement(statement is BoundBlockStatement ? statement : new BoundBlockStatement([statement]));

    private void ScopedBlock(BoundBlockStatement block)
    {
        int parent = _scope;
        _scope = _scopes.Count;
        _scopes.Add(new(_scope, parent));
        try { ScopedBlockCore(block); }
        finally { _scope = parent; }
    }

    private void ScopedBlockCore(BoundBlockStatement block)
    {
        Block outer = _unwindTarget, outerRethrow = _rethrowTarget;
        MirOperand? stack = _bound.Symbol.HasStackArrays && !_bound.Body.RequiresSuspensionStateMachine && !block.RetainsStackStorage ? Save(new MirStackSave(_types.PointerTo(BuiltinTypes.Byte)), _functionSource) : null;
        var previousPlaces = _ownedPlaces.ToArray();
        var previousArrays = _arrayCreations.ToArray();
        int depth = _exits.Count;
        var guards = new List<TemporaryGuard>();
        MirPlace? counter = null;
        if (!_parametersRegistered)
        {
            _parametersRegistered = true;
            foreach (ParameterSymbol parameter in _bound.Symbol.Parameters)
                Register(parameter, true);
        }
        var declarations = block.Statements.OfType<BoundVariableDeclarationStatement>()
            .Where(declaration => !_diagnosticRecovery || (!TypeIdentity.AreSame(declaration.Variable.Type, BuiltinTypes.Error) && !TypeIdentity.AreSame(declaration.Variable.Type, BuiltinTypes.Void))).ToArray();
        foreach (BoundVariableDeclarationStatement declaration in declarations)
            if (!_bound.Body.IsResumable || !ReferenceEquals(declaration, _bound.Body.Statements[0]))
                Register(declaration.Variable, false);
        foreach (BoundArrayCreationExpression creation in ScopeArrays(block))
        {
            TemporaryGuard guard = RegisterPlace(Temporary(creation.Type, Source(creation)), TypeFacts.GetCompleteDestructor(creation.ElementType)!, false, true);
            PrepareArrayHistory(guard, creation.ArrayType, Source(creation));
            _arrayCreations[creation] = guard;
        }
        var cleanup = new LexicalCleanup(guards, declarations.Where(d => !_bound.Body.IsResumable || !ReferenceEquals(d, _bound.Body.Statements[0])).Select(d => Variable(d.Variable).Local).ToArray(), stack);
        if (guards.Count == 0 && cleanup.Locals.Length == 0 && stack is null)
        {
            foreach (BoundStatement child in block.Statements) Statement(child);
            return;
        }
        Block dead = EndStorage(outer);
        Block rethrowDead = ReferenceEquals(outer, outerRethrow) ? dead : EndStorage(outerRethrow);
        Block failure = CleanupChain(guards, dead, dead, true, _functionSource);
        _exits.Add(new(null, null, false, dead, rethrowDead) { Cleanup = cleanup });
        _unwindTarget = guards.Count == 0 && stack is null ? outer : failure;
        _rethrowTarget = guards.Count == 0 && stack is null ? outerRethrow : CleanupChain(guards, rethrowDead, rethrowDead, true, _functionSource);
        foreach (BoundStatement child in block.Statements) Statement(child);
        if (_current.Terminator is null) ExitActions(depth);
        _exits.RemoveAt(_exits.Count - 1);
        _unwindTarget = outer;
        _rethrowTarget = outerRethrow;
        _ownedPlaces.Clear();
        foreach (var pair in previousPlaces) _ownedPlaces.Add(pair.Key, pair.Value);
        _arrayCreations.Clear();
        foreach (var pair in previousArrays) _arrayCreations.Add(pair.Key, pair.Value);

        Block EndStorage(Block target)
        {
            Block dead = NewBlock();
            foreach (MirLocalId local in cleanup.Locals) dead.Statements.Add(new MirStorageDead(local, _functionSource));
            if (stack is not null) dead.Statements.Add(new MirStackRestore(stack, _functionSource));
            dead.Terminator = new MirGoto(target.Id, _functionSource);
            return dead;
        }

        void Register(VariableSymbol variable, bool initialized)
        {
            foreach ((MirPlace place, FunctionSymbol destructor) in DestructionUnits(Variable(variable), variable.Type))
                RegisterPlace(place, destructor, initialized);
            if (variable is LocalVariableSymbol { RequiresArrayCleanupTransfer: true, Type: ArrayTypeSymbol array } &&
                TypeFacts.GetCompleteDestructor(array.ElementType) is { } elementDestructor)
                RegisterPlace(Variable(variable), elementDestructor, false, true);
        }

        TemporaryGuard RegisterPlace(MirPlace place, FunctionSymbol destructor, bool initialized, bool array = false)
        {
            if (counter is null)
            {
                counter = CleanupControl(BuiltinTypes.Long, _functionSource);
                _current.Statements.Add(new MirAssign(counter, new MirUse(new MirConstant(0L, BuiltinTypes.Long)), _functionSource));
            }
            var guard = new TemporaryGuard(place, Temporary(BuiltinTypes.Bool, _functionSource), destructor)
            {
                Order = CleanupControl(BuiltinTypes.Long, _functionSource), Counter = counter,
                Count = array ? Temporary(BuiltinTypes.Int, _functionSource) : null,
            };
            _ownedPlaces[place] = guard;
            guards.Add(guard);
            _current.Statements.Add(new MirAssign(guard.Order, new MirUse(new MirConstant(-1L, BuiltinTypes.Long)), _functionSource));
            if (guard.Count is { } count)
                _current.Statements.Add(new MirAssign(count, new MirUse(new MirConstant(0, BuiltinTypes.Int)), _functionSource));
            ActivateGuard(guard, initialized, _functionSource);
            return guard;
        }
    }
    private static IEnumerable<(MirPlace Place, FunctionSymbol Destructor)> DestructionUnits(MirPlace place, TypeSymbol type)
    {
        if (TypeFacts.GetCompleteDestructor(type) is not { } destructor) yield break;
        if (type is PinTypeSymbol pin)
        {
            foreach (var unit in DestructionUnits(place.Project(new MirLifetimeProjection()), pin.ElementType)) yield return unit;
        }
        else if (type is StructTypeSymbol structure && structure.FindDestructor() is null)
        {
            if (structure.BaseType is { } baseType)
                foreach (var unit in DestructionUnits(place, baseType)) yield return unit;
            foreach (FieldSymbol field in structure.Fields)
                foreach (var unit in DestructionUnits(place.Project(new MirFieldProjection(field)), field.Type)) yield return unit;
        }
        else yield return (DestructorPlace(place, type), destructor);
    }

    private IEnumerable<TemporaryGuard> OwnedUnits(MirPlace place) =>
        _ownedPlaces.Where(pair => place.IsPrefixOf(pair.Key)).Select(pair => pair.Value)
            .Concat(_lifecycleFields.Where(guard => place.IsPrefixOf(guard.Place)));

    private void Activate(MirPlace place, bool initialized, MirSourceInfo source)
    {
        foreach (TemporaryGuard guard in OwnedUnits(place)) ActivateGuard(guard, initialized, source);
    }

    private void ActivateGuard(TemporaryGuard guard, bool initialized, MirSourceInfo source)
    {
        if (initialized && guard.Order is { } order)
        {
            MirOperand first = Save(new MirBinary(MirBinaryOperator.Less, new MirCopy(order, BuiltinTypes.Long),
                new MirConstant(0L, BuiltinTypes.Long), BuiltinTypes.Bool), source);
            Block register = NewBlock(), after = NewBlock();
            End(new MirSwitch(first, [new(new(true, BuiltinTypes.Bool), register.Id)], after.Id, source));
            _current = register;
            _current.Statements.Add(new MirAssign(order, new MirUse(new MirCopy(guard.Counter!, BuiltinTypes.Long)), source));
            _current.Statements.Add(new MirAssign(guard.Counter!, new MirBinary(MirBinaryOperator.Add,
                new MirCopy(guard.Counter!, BuiltinTypes.Long), new MirConstant(1L, BuiltinTypes.Long), BuiltinTypes.Long), source));
            Jump(after);
            _current = after;
        }
        SetFlag(guard.Active, initialized, source);
    }

    // Registration order is determined by first successful initialization, which
    // may occur in either branch long after a declaration. Scalar units retain
    // their first registration; repeated arrays retain separate history entries.
    private Block OrderedCleanup(IReadOnlyList<TemporaryGuard> guards, Block normal, Block unwind,
        bool exceptional, MirSourceInfo source)
    {
        _hasDynamicCleanupOrder = true;
        Block saved = _current;
        Block start = NewBlock();
        MirPlace selected = CleanupControl(BuiltinTypes.Int, source), latest = CleanupControl(BuiltinTypes.Long, source);
        _current = start;
        _current.Statements.Add(new MirAssign(selected, new MirUse(new MirConstant(-1, BuiltinTypes.Int)), source));
        _current.Statements.Add(new MirAssign(latest, new MirUse(new MirConstant(-1L, BuiltinTypes.Long)), source));
        for (int index = 0; index < guards.Count; index++)
        {
            TemporaryGuard guard = guards[index];
            Block compare = NewBlock(), choose = NewBlock(), next = NewBlock();
            End(new MirSwitch(new MirCopy(guard.Active, BuiltinTypes.Bool), [new(new(true, BuiltinTypes.Bool), compare.Id)], next.Id, source));
            _current = compare;
            MirOperand order = new MirCopy(guard.Order!, BuiltinTypes.Long);
            MirOperand newer = Save(new MirBinary(MirBinaryOperator.Greater, order, new MirCopy(latest, BuiltinTypes.Long), BuiltinTypes.Bool), source);
            End(new MirSwitch(newer, [new(new(true, BuiltinTypes.Bool), choose.Id)], next.Id, source));
            _current = choose;
            _current.Statements.Add(new MirAssign(selected, new MirUse(new MirConstant(index, BuiltinTypes.Int)), source));
            _current.Statements.Add(new MirAssign(latest, new MirUse(order), source));
            Jump(next);
            _current = next;
        }
        Block dispatch = _current;
        Block failure;
        if (exceptional) { failure = NewBlock(); failure.Terminator = new MirAbort(source); }
        else failure = OrderedCleanup(guards, unwind, unwind, true, source);
        var cases = System.Collections.Immutable.ImmutableArray.CreateBuilder<MirSwitchCase>();
        for (int index = 0; index < guards.Count; index++)
        {
            TemporaryGuard guard = guards[index];
            Block drop = DropGuard(guard, start, failure, source);
            cases.Add(new(new(index, BuiltinTypes.Int), drop.Id));
        }
        dispatch.Terminator = new MirSwitch(new MirCopy(selected, BuiltinTypes.Int), cases.ToImmutable(), normal.Id, source);
        _current = saved;
        return start;
    }

    private void Forget(MirPlace place, MirSourceInfo source)
    {
        Activate(place, false, source);
        _current.Statements.Add(new MirForget(place, source));
    }

    private Block GuardedUnwind(Block target, MirSourceInfo source) =>
        CleanupChain(_valueGuards, target, target, true, source);

    private TemporaryGuard? GuardValue(MirOperand value, MirSourceInfo source)
    {
        if (TypeFacts.GetCompleteDestructor(value.Type) is not { } destructor) return null;
        MirPlace place = Temporary(value.Type, source);
        _current.Statements.Add(new MirAssign(place, new MirUse(value), source));
        var guard = new TemporaryGuard(place, Temporary(BuiltinTypes.Bool, source), destructor);
        SetFlag(guard.Active, true, source);
        _valueGuards.Add(guard);
        return guard;
    }

    private void EndValueGuard(TemporaryGuard? guard, MirSourceInfo source)
    {
        if (guard is null) return;
        SetFlag(guard.Active, false, source);
        _valueGuards.Remove(guard);
    }

    private static bool IsThisRooted(BoundExpression expression) => expression switch
    {
        BoundThisExpression => true,
        BoundMemberAccessExpression field => IsThisRooted(field.Receiver),
        BoundReferenceDereferenceExpression reference => IsThisRooted(reference.Reference),
        BoundLifetimeValueExpression lifetime => IsThisRooted(lifetime.Source),
        _ => false,
    };

    private void Store(MirAssign assign, bool strongReceiverReplacement = false, TemporaryGuard? incomingArray = null)
    {
        if ((assign.WriteKind == MirWriteKind.Replace || assign.RequiresRuntimeInitializationCheck) && assign.PreviousValueState != MirPreviousValueState.DefinitelyMoved)
        {
            if (strongReceiverReplacement && TypeFacts.GetCompleteDestructor(assign.Value.Type) is { } oldDestructor)
            {
                MirOperand previous = Save(new MirUse(new MirCopy(assign.Destination, assign.Value.Type)), assign.Source);
                _current.Statements.Add(assign);
                Activate(assign.Destination, true, assign.Source);
                Block after = NewBlock();
                End(new MirDrop(((MirCopy)previous).Place, oldDestructor, after.Id,
                    GuardedUnwind(_unwindTarget, assign.Source).Id, assign.Source));
                _current = after;
                return;
            }
            TemporaryGuard[] guards = OwnedUnits(assign.Destination).ToArray();
            if (guards.Length == 0 && assign.ConstructorField is { } field && _constructionFields.TryGetValue(field, out TemporaryGuard? fieldGuard))
                guards = [fieldGuard];
            if (guards.Length == 0 && TypeFacts.GetCompleteDestructor(assign.Value.Type) is { } destructor)
            {
                var guard = new TemporaryGuard(assign.Destination, Temporary(BuiltinTypes.Bool, assign.Source), destructor);
                SetFlag(guard.Active, true, assign.Source);
                guards = [guard];
            }
            if (guards.Length > 0)
            {
                TemporaryGuard? incoming = assign.Value is MirUse use ? GuardValue(use.Operand, assign.Source) : null;
                Block after = NewBlock();
                if (incomingArray is not null && guards is [var arrayGuard] && arrayGuard.Count is not null)
                {
                    Block outer = GuardedUnwind(_unwindTarget, assign.Source);
                    Block remaining = CleanupChain(guards, outer, outer, true, assign.Source);
                    Block discardNew = CleanupChain([incomingArray], remaining, remaining, true, assign.Source);
                    Block destroyOld = DropGuard(arrayGuard, after, discardNew, assign.Source);
                    End(new MirSwitch(new MirCopy(arrayGuard.Active, BuiltinTypes.Bool),
                        [new(new(true, BuiltinTypes.Bool), destroyOld.Id)], after.Id, assign.Source));
                }
                else Jump(CleanupChain(guards, after, GuardedUnwind(_unwindTarget, assign.Source), false, assign.Source));
                _current = after;
                EndValueGuard(incoming, assign.Source);
            }
        }
        _current.Statements.Add(assign);
        if (assign.WriteKind != MirWriteKind.RawPlacement) Activate(assign.Destination, true, assign.Source);
        if (assign.ConstructorField is { } initializedField && _constructionStates.TryGetValue(initializedField, out MirPlace? initialized))
            SetFlag(initialized, true, assign.Source);
    }
}
