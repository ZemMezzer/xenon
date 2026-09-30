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
                return Intrinsic(MirIntrinsicKind.DestroyFields, [ThisAddress(destruction.StructType)], BuiltinTypes.Void, source, subjectType: destruction.StructType);
            case BoundOwnershipDestructionExpression destruction:
                return Intrinsic(MirIntrinsicKind.DestroyOwner, [ThisAddress(destruction.OwnershipType)], BuiltinTypes.Void, source,
                    destruction.ElementDestructor, destruction.OwnershipType);
            case BoundStorageDestructionExpression destruction:
                return Intrinsic(MirIntrinsicKind.DestroyStorage, [ThisAddress(destruction.StorageType)], BuiltinTypes.Void, source,
                    destruction.ElementDestructor, destruction.StorageType);
            case BoundFunctionValueDestructionExpression destruction:
                return Intrinsic(MirIntrinsicKind.DestroyCallable, [ThisAddress(destruction.FunctionValueType)], BuiltinTypes.Void, source,
                    subjectType: destruction.FunctionValueType);
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
                return Intrinsic(MirIntrinsicKind.Delete, [Value(deletion.Pointer)], BuiltinTypes.Void, source, deletion.Destructor);
            case BoundFunctionValueExpression callable:
                return Intrinsic(MirIntrinsicKind.MakeCallable, Arguments([.. callable.Captures.Select(c => c.Initializer)]), callable.Type, source,
                    callable.InvokeFunction, captures: [.. callable.Captures.Select(c => c.Variable)]);
            case BoundExplicitDestructExpression destruction:
            {
                MirPlace place = Place(destruction.Target);
                Block after = NewBlock();
                End(new MirDrop(place, destruction.Destructor, after.Id, _unwindTarget.Id, source));
                _current = after;
                _current.Statements.Add(new MirForget(place, source));
                return null;
            }
            case BoundStorageConstructExpression storage:
            {
                MirPlace target = Place(storage.Storage);
                MirOperand address = Address(target, storage.Storage.Type, source);
                _ = Intrinsic(MirIntrinsicKind.CheckStorageEmpty, [address], BuiltinTypes.Void, source);
                MirPlace element = target.Project(new MirLifetimeProjection());
                _current.Statements.Add(new MirAssign(element, new MirDefault(storage.ValueType), source));
                _ = Call(Callee(storage.Constructor!), storage.Arguments, source, Address(element, storage.ValueType, source));
                _ = Intrinsic(MirIntrinsicKind.MarkStorageInitialized, [address], BuiltinTypes.Void, source);
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
                return Intrinsic(MirIntrinsicKind.CompareExchange,
                    [Address(Place(exchange.Target), exchange.Target.Type, source), Snapshot(Value(exchange.Expected), source), Value(exchange.Desired)],
                    BuiltinTypes.Bool, source);
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
