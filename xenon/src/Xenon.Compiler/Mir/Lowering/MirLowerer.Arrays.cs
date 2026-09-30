using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private readonly Dictionary<BoundArrayCreationExpression, TemporaryGuard> _arrayCreations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BoundMoveExpression, MirOperand> _arrayMoveStates = new(ReferenceEqualityComparer.Instance);

    private static IEnumerable<BoundArrayCreationExpression> ScopeArrays(BoundBlockStatement block)
    {
        foreach (BoundStatement statement in block.Statements)
        {
            IEnumerable<BoundNode> roots = statement switch
            {
                BoundVariableDeclarationStatement { Initializer: { } value } => [value],
                BoundExpressionStatement value => [value.Expression],
                BoundReturnStatement { Expression: { } value } => [value],
                BoundThrowStatement { Expression: { } value } => [value],
                BoundIfStatement value => [value.Condition],
                BoundWhileStatement value => [value.Condition],
                BoundForStatement value => new BoundNode?[] { value.Condition, value.Increment }.OfType<BoundNode>(),
                BoundSwitchStatement value => [value.Expression],
                _ => [],
            };
            foreach (BoundArrayCreationExpression creation in roots.SelectMany(BoundTree.DescendantsAndSelf)
                         .OfType<BoundArrayCreationExpression>().Distinct<BoundArrayCreationExpression>(ReferenceEqualityComparer.Instance))
                if (creation.Storage == ArrayStorageKind.Stack && TypeFacts.GetCompleteDestructor(creation.ElementType) is not null)
                    yield return creation;
        }
    }

    private MirOperand CreateArray(BoundArrayCreationExpression creation, MirSourceInfo source)
    {
        TemporaryGuard? guard = _arrayCreations.GetValueOrDefault(creation);
        if (guard is not null) PushArrayHistory(guard, source);
        MirOperand array = Intrinsic(creation.Storage == ArrayStorageKind.Stack ? MirIntrinsicKind.AllocateStackArray : MirIntrinsicKind.AllocateHeapArray,
            [.. creation.Dimensions.Select(d => Snapshot(Value(d), Source(d)))], creation.Type, source)!;
        if (creation.Storage == ArrayStorageKind.Heap)
        {
            guard = new(((MirCopy)array).Place, Temporary(BuiltinTypes.Bool, source), TypeFacts.GetCompleteDestructor(creation.ElementType))
            { Count = Temporary(BuiltinTypes.Int, source), FreeAllocation = true, Allocation = array };
            _current.Statements.Add(new MirAssign(guard.Count, new MirUse(new MirConstant(0, BuiltinTypes.Int)), source));
            SetFlag(guard.Active, true, source);
            _valueGuards.Add(guard);
        }
        MirOperand length = Intrinsic(MirIntrinsicKind.ArrayLength, [array], BuiltinTypes.Int, source)!;
        if (guard is not null)
        {
            _current.Statements.Add(new MirAssign(guard.Place, new MirUse(array), source));
            _current.Statements.Add(new MirAssign(guard.Count!, new MirUse(new MirConstant(0, BuiltinTypes.Int)), source));
            if (guard.Order is { } order)
            {
                _current.Statements.Add(new MirAssign(order, new MirUse(new MirCopy(guard.Counter!, BuiltinTypes.Long)), source));
                _current.Statements.Add(new MirAssign(guard.Counter!, new MirBinary(MirBinaryOperator.Add,
                    new MirCopy(guard.Counter!, BuiltinTypes.Long), new MirConstant(1L, BuiltinTypes.Long), BuiltinTypes.Long), source));
            }
            SetFlag(guard.Active, true, source);
        }
        MirPlace count = guard?.Count ?? Temporary(BuiltinTypes.Int, source);
        if (guard is null) _current.Statements.Add(new MirAssign(count, new MirUse(new MirConstant(0, BuiltinTypes.Int)), source));
        Block test = NewBlock(), initialize = NewBlock(), after = NewBlock();
        Jump(test);
        _current = test;
        MirOperand more = Save(new MirBinary(MirBinaryOperator.Less, new MirCopy(count, BuiltinTypes.Int), length, BuiltinTypes.Bool), source);
        End(new MirSwitch(more, [new(new(true, BuiltinTypes.Bool), initialize.Id)], after.Id, source));
        _current = initialize;
        MirPlace element = ((MirCopy)array).Place.Project(new MirLinearIndexProjection(new MirCopy(count, BuiltinTypes.Int)));
        _current.Statements.Add(new MirAssign(element, new MirDefault(creation.ElementType), source));
        StructTypeSymbol? structure = creation.ElementType is AtomicTypeSymbol atomic ? atomic.ElementType as StructTypeSymbol : creation.ElementType as StructTypeSymbol;
        if (structure is not null)
            ConstructAggregateAt(new BoundStructConstructionExpression(structure, []) { IsDefaultInitialization = true },
                creation.ElementType is AtomicTypeSymbol ? element.Project(new MirAtomicStorageProjection()) : element, source);
        _current.Statements.Add(new MirAssign(count, new MirBinary(MirBinaryOperator.Add,
            new MirCopy(count, BuiltinTypes.Int), new MirConstant(1, BuiltinTypes.Int), BuiltinTypes.Int), source));
        Jump(test);
        _current = after;
        if (creation.Storage == ArrayStorageKind.Heap) EndValueGuard(guard, source);
        return array;
    }

    private (TemporaryGuard? Guard, MirOperand? Active) ArrayOwnership(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundFullExpression full: return ArrayOwnership(full.Expression);
            case BoundCastExpression cast: return ArrayOwnership(cast.Expression);
            case BoundArrayCreationExpression creation:
                TemporaryGuard? guard = _arrayCreations.GetValueOrDefault(creation);
                return (guard, guard is null ? null : new MirCopy(guard.Active, BuiltinTypes.Bool));
            case BoundMoveExpression move:
                var source = ArrayOwnership(move.Source);
                return (source.Guard, _arrayMoveStates.GetValueOrDefault(move));
            case BoundVariableExpression variable:
                TemporaryGuard? owner = _ownedPlaces.GetValueOrDefault(Variable(variable.Variable));
                return (owner?.Count is null ? null : owner, owner?.Count is null ? null : new MirCopy(owner.Active, BuiltinTypes.Bool));
            default: return (null, null);
        }
    }

    private void TransferArray(MirPlace destination, BoundExpression source, MirSourceInfo location)
    {
        if (!_ownedPlaces.TryGetValue(destination, out TemporaryGuard? target) || target.Count is null) return;
        (TemporaryGuard? origin, MirOperand? active) = source is BoundArrayCreationExpression or BoundMoveExpression or BoundFullExpression or BoundCastExpression
            ? ArrayOwnership(source) : (null, null);
        if (origin is not null && active is not null)
        {
            _current.Statements.Add(new MirAssign(target.Count, new MirUse(new MirCopy(origin.Count!, BuiltinTypes.Int)), location));
            MirOperand snapshot = Snapshot(active, location);
            Block transferred = NewBlock();
            Jump(PopArrayHistory(origin, transferred, location));
            _current = transferred;
            ActivateGuard(target, true, location);
            _current.Statements.Add(new MirAssign(target.Active, new MirUse(snapshot), location));
        }
        else SetFlag(target.Active, false, location);
    }

    private void DeleteValue(MirOperand pointer, FunctionSymbol? destructor, MirSourceInfo source)
    {
        MirOperand value = pointer is MirCopy ? pointer : Save(new MirUse(pointer), source);
        Block destroy = NewBlock(), after = NewBlock();
        MirOperand empty = Save(new MirBinary(MirBinaryOperator.Equal, value, new MirConstant(null, value.Type), BuiltinTypes.Bool), source);
        End(new MirSwitch(empty, [new(new(true, BuiltinTypes.Bool), after.Id)], destroy.Id, source));
        _current = destroy;
        bool array = value.Type is ArrayTypeSymbol;
        var guard = new TemporaryGuard(array ? ((MirCopy)value).Place : DestructorPlace(((MirCopy)value).Place.Project(new MirDerefProjection()), ((PointerTypeSymbol)value.Type).ElementType),
            Temporary(BuiltinTypes.Bool, source), destructor)
        { FreeAllocation = true, Allocation = value, Count = array ? Temporary(BuiltinTypes.Int, source) : null };
        SetFlag(guard.Active, true, source);
        if (guard.Count is { } count)
        {
            MirOperand length = Intrinsic(MirIntrinsicKind.ArrayLength, [value], BuiltinTypes.Int, source)!;
            _current.Statements.Add(new MirAssign(count, new MirUse(length), source));
        }
        Jump(CleanupChain([guard], after, GuardedUnwind(_unwindTarget, source), false, source));
        _current = after;
    }

    private Block DropGuard(TemporaryGuard guard, Block normal, Block unwind, MirSourceInfo source)
    {
        Block drop = NewBlock();
        if (guard.Count is null || guard.Destructor is null)
        {
            drop.Statements.Add(new MirAssign(guard.Active, new MirUse(new MirConstant(false, BuiltinTypes.Bool)), source));
            if (guard.Destructor is null) drop.Terminator = guard.FreeAllocation ? FreeBuffer(normal, unwind) : new MirGoto(normal.Id, source);
            else if (!guard.FreeAllocation) drop.Terminator = new MirDrop(guard.Place, guard.Destructor, normal.Id, unwind.Id, source);
            else
            {
                Block freed = NewBlock(), exceptionalFree = NewBlock(), abort = NewBlock();
                abort.Terminator = new MirAbort(source);
                freed.Terminator = FreeBuffer(normal, unwind);
                exceptionalFree.Terminator = FreeBuffer(unwind, abort);
                drop.Terminator = new MirDrop(guard.Place, guard.Destructor, freed.Id, exceptionalFree.Id, source);
            }
            return drop;
        }
        Block element = NewBlock(), end = NewBlock();
        MirPlace more = Temporary(BuiltinTypes.Bool, source);
        drop.Statements.Add(new MirAssign(more, new MirBinary(MirBinaryOperator.Greater, new MirCopy(guard.Count, BuiltinTypes.Int),
            new MirConstant(0, BuiltinTypes.Int), BuiltinTypes.Bool), source));
        drop.Terminator = new MirSwitch(new MirCopy(more, BuiltinTypes.Bool), [new(new(true, BuiltinTypes.Bool), element.Id)], end.Id, source);
        element.Statements.Add(new MirAssign(guard.Count, new MirBinary(MirBinaryOperator.Subtract,
            new MirCopy(guard.Count, BuiltinTypes.Int), new MirConstant(1, BuiltinTypes.Int), BuiltinTypes.Int), source));
        TypeSymbol elementType = ((ArrayTypeSymbol)_locals[guard.Place.Local.Value].Type).ElementType;
        element.Terminator = new MirDrop(DestructorPlace(guard.Place.Project(new MirLinearIndexProjection(new MirCopy(guard.Count, BuiltinTypes.Int))), elementType),
            guard.Destructor, drop.Id, unwind.Id, source);
        if (guard.FreeAllocation)
        {
            end.Statements.Add(new MirAssign(guard.Active, new MirUse(new MirConstant(false, BuiltinTypes.Bool)), source));
            end.Terminator = FreeBuffer(normal, unwind);
        }
        else end.Terminator = new MirGoto(PopArrayHistory(guard, normal, source).Id, source);
        return drop;

        MirIntrinsicCall FreeBuffer(Block next, Block failure) => new(MirIntrinsicKind.Free,
            [guard.Allocation ?? new MirCopy(guard.Place, _locals[guard.Place.Local.Value].Type)], BuiltinTypes.Void, null, next.Id, failure.Id, source);
    }
}
