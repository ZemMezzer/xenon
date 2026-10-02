using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private MirOperand ThisAddress(TypeSymbol type) => _bound.Symbol.FunctionKind is FunctionKind.OwnershipDestructor or FunctionKind.StorageDestructor or FunctionKind.FunctionValueDestructor
        ? new MirCopy(Variable(_bound.Symbol.Parameters[0], MirLocalKind.Parameter), _bound.Symbol.Parameters[0].Type)
        : new MirCopy(_receiver ??= Receiver(_types.PointerTo(type)), _types.PointerTo(type));

    private MirOperand? ExtendedExpression(BoundExpression expression)
    {
        MirSourceInfo source = Source(expression);
        switch (expression)
        {
            case BoundUniqueAdoptionExpression adoption:
                return Intrinsic(MirIntrinsicKind.AdoptUnique, [Value(adoption.Allocation)], adoption.Type, source);
            case BoundSharedAdoptionExpression adoption:
                return Intrinsic(MirIntrinsicKind.AdoptShared, [Value(adoption.Allocation)], adoption.Type, source);
            case BoundWeakConversionExpression weak:
                return Intrinsic(MirIntrinsicKind.ConvertWeak, [Value(weak.Shared)], weak.Type, source);
            case BoundLockExpression locked:
                return Intrinsic(MirIntrinsicKind.LockWeak, [Value(locked.Weak)], locked.Type, source);
            case BoundDestroyFieldsExpression destruction:
                DestroyFields(source);
                return null;
            case BoundOwnershipDestructionExpression { OwnershipType: UniqueTypeSymbol } destruction:
                MirPlace owner = ((MirCopy)ThisAddress(destruction.OwnershipType)).Place.Project(new MirDerefProjection()).Project(new MirOwnerStorageProjection());
                DeleteValue(Save(new MirUse(new MirCopy(owner, destruction.OwnershipType.StorageType)), source), destruction.ElementDestructor, source);
                return null;
            case BoundOwnershipDestructionExpression destruction:
                DestroySharedOrWeak(destruction, source);
                return null;
            case BoundStorageDestructionExpression destruction:
                MirPlace wrapper = ((MirCopy)ThisAddress(destruction.StorageType)).Place.Project(new MirDerefProjection());
                DestroyStorage(wrapper, destruction.StorageType, destruction.ElementDestructor, source);
                return null;
            case BoundFunctionValueDestructionExpression destruction:
                MirOperand callableValue = Save(new MirUse(new MirCopy(
                    ((MirCopy)ThisAddress(destruction.FunctionValueType)).Place.Project(new MirDerefProjection()),
                    destruction.FunctionValueType)), source);
                DestroyCallableControl(Intrinsic(MirIntrinsicKind.CallableControl, [callableValue],
                    _types.PointerTo(BuiltinTypes.Byte), source)!, source);
                return null;
            case BoundInterfaceConversionExpression conversion:
                return Save(new MirInterfaceView(Address(Place(conversion.Source), conversion.Source.Type, source), conversion.SourceType, conversion.InterfaceType), source);
            case BoundTypeLayoutExpression layout:
                return Save(new MirTypeLayout(layout.OperatorKind switch
                {
                    SyntaxKind.SizeOfKeyword => MirLayoutQuery.Size,
                    SyntaxKind.AlignOfKeyword => MirLayoutQuery.Alignment,
                    SyntaxKind.OffsetOfKeyword => MirLayoutQuery.FieldOffset,
                    _ => throw Unsupported(layout),
                }, layout.TargetType, layout.Field), source);
            case BoundRawAllocationExpression allocation:
                return Intrinsic(allocation.AllocationKind switch
                {
                    RawAllocationKind.Malloc => MirIntrinsicKind.Malloc,
                    RawAllocationKind.AlignedMalloc => MirIntrinsicKind.AlignedMalloc,
                    RawAllocationKind.Calloc => MirIntrinsicKind.Calloc,
                    _ => throw Unsupported(allocation),
                }, Arguments(allocation.Arguments), allocation.Type, source);
            case BoundFreeExpression free:
                return Intrinsic(MirIntrinsicKind.Free, [Value(free.Pointer)], BuiltinTypes.Void, source);
            case BoundDeleteExpression deletion:
                DeleteValue(Snapshot(Value(deletion.Pointer), source), deletion.Destructor, source);
                return null;
            case BoundFunctionValueExpression callable:
                return Intrinsic(MirIntrinsicKind.MakeCallable, Arguments([.. callable.Captures.Select(c => c.Initializer)]), callable.Type, source,
                    callable.InvokeFunction, captures: [.. callable.Captures.Select(c => c.Variable)]);
            case BoundExplicitDestructExpression destruction:
            {
                MirPlace tracked = Place(destruction.Target);
                MirPlace? ownership = LifetimePlace(destruction, tracked);
                (MirPlace place, TypeSymbol type) = Unpin(tracked, destruction.Target.Type);
                if (type is StorageTypeSymbol)
                {
                    _ = Intrinsic(MirIntrinsicKind.CheckStorageInitialized, [Address(place, type, source)], BuiltinTypes.Void, source, storageCheck: MirStorageCheckPurpose.Destruct);
                    _current.Statements.Add(new MirSetStorageState(place, false, source));
                    place = place.Project(new MirLifetimeProjection());
                }
                // Destroying T empties storage<T>; the wrapper remains live and
                // can be initialized again through an alias.
                if (type is not StorageTypeSymbol)
                    Forget(ownership ?? tracked, source);
                Block after = NewBlock();
                End(new MirDrop(place, destruction.Destructor, after.Id, GuardedUnwind(_unwindTarget, source).Id, source) { IsExplicit = true });
                _current = after;
                return null;
            }
            case BoundNewExpression allocation: return Allocate(allocation, source);
            case BoundPropertySetExpression setter:
                return SetAccessor(setter.Receiver, setter.Property.Setter!, [], setter.Value, setter.IsPointerAccess, null, source);
            case BoundInterfacePropertySetExpression setter:
                return SetAccessor(setter.Receiver, setter.Property.Setter!, [], setter.Value, setter.IsPointerAccess, setter.InterfaceType, source);
            case BoundIndexerSetExpression setter:
                return SetAccessor(setter.Receiver, setter.Indexer.Setter!, setter.Arguments, setter.Value, false, null, source);
            case BoundInterfaceIndexerSetExpression setter:
                return SetAccessor(setter.Receiver, setter.Indexer.Setter!, setter.Arguments, setter.Value, false, setter.InterfaceType, source);
            case BoundCompoundAccessorAssignmentExpression compound: return CompoundAccessor(compound, source);
            case BoundCompareExchangeExpression exchange:
                return CompareExchange(exchange, source);
            case BoundSwapExpression swap:
                return Intrinsic(MirIntrinsicKind.Swap, [Address(Place(swap.Left), swap.Left.Type, source), Address(Place(swap.Right), swap.Right.Type, source)],
                    BuiltinTypes.Void, source);
            default: throw Unsupported(expression);
        }
    }

    private MirOperand Allocate(BoundNewExpression allocation, MirSourceInfo source)
    {
        MirOperand pointer = Intrinsic(MirIntrinsicKind.Allocate, [], allocation.Type, source, subjectType: allocation.AllocatedType)!;
        Block outer = _unwindTarget, failed = NewBlock();
        _unwindTarget = failed;
        MirPlace place = ((MirCopy)pointer).Place.Project(new MirDerefProjection());
        if (allocation.Constructor is { } constructor)
        {
            _current.Statements.Add(new MirAssign(place, new MirDefault(allocation.AllocatedType), source));
            _ = Call(new MirFunctionOperand(constructor, _types.FunctionPointer(BuiltinTypes.Void, constructor.Parameters.Select(p => p.Type))),
                allocation.Arguments, source, pointer);
        }
        else
        {
            MirOperand value = allocation.StructType is { } structure
                ? ConstructAggregate(new BoundStructConstructionExpression(structure, allocation.Arguments) { IsDefaultInitialization = allocation.IsDefaultInitialization }, source)
                : allocation.Arguments.IsEmpty ? Save(new MirDefault(allocation.AllocatedType), source) : Arguments(allocation.Arguments)[0];
            _current.Statements.Add(new MirAssign(place, new MirUse(value), source));
        }
        Block normal = _current;
        _current = failed;
        _unwindTarget = outer;
        _ = Intrinsic(MirIntrinsicKind.Free, [pointer], BuiltinTypes.Void, source);
        Jump(outer);
        _current = normal;
        return pointer;
    }
}
