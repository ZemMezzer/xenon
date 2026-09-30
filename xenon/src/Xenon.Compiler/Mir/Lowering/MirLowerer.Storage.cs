using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private static MirPlace DestructorPlace(MirPlace place, TypeSymbol type) => type switch
    {
        PinTypeSymbol pin => DestructorPlace(place.Project(new MirLifetimeProjection()), pin.ElementType),
        AtomicTypeSymbol => place.Project(new MirAtomicStorageProjection()),
        _ => place,
    };

    private void EmptyMovedStorage(MirPlace place, TypeSymbol type, MirSourceInfo source)
    {
        switch (type)
        {
            case StorageTypeSymbol:
                _current.Statements.Add(new MirSetStorageState(place, false, source));
                break;
            case PinTypeSymbol pin:
                EmptyMovedStorage(place.Project(new MirLifetimeProjection()), pin.ElementType, source);
                break;
            case StructTypeSymbol structure:
                if (structure.BaseType is { } baseType)
                    EmptyMovedStorage(place.Project(new MirBaseProjection(baseType)), baseType, source);
                foreach (FieldSymbol field in structure.Fields)
                    EmptyMovedStorage(place.Project(new MirFieldProjection(field)), field.Type, source);
                break;
        }
    }

    private static bool IsStorage(TypeSymbol type) => type is StorageTypeSymbol ||
        type is PinTypeSymbol pin && IsStorage(pin.ElementType);

    private (MirPlace Place, TypeSymbol Type) Unpin(MirPlace place, TypeSymbol type)
    {
        while (type is PinTypeSymbol pin)
        {
            type = pin.ElementType;
            place = place.Project(new MirLifetimeProjection());
        }
        return (place, type);
    }

    private void ConstructStorage(BoundStorageConstructExpression construction, MirSourceInfo source)
    {
        MirPlace tracked = Place(construction.Storage);
        if (TypeFacts.IsPinned(construction.Storage.Type) && !IsStorage(construction.Storage.Type))
        {
            TypeSymbol pointerType = _types.PointerTo(construction.Storage.Type);
            MirPlace check = Temporary(pointerType, source);
            _current.Statements.Add(new MirAssign(check, new MirBorrow(tracked, MirBorrowKind.Raw, pointerType), source)
                { IsPinnedInitializationCheck = true });
        }
        (MirPlace target, TypeSymbol type) = Unpin(tracked, construction.Storage.Type);
        MirPlace? wrapper = type is StorageTypeSymbol ? target : null;
        if (wrapper is not null)
        {
            _ = Intrinsic(MirIntrinsicKind.CheckStorageEmpty, [Address(wrapper, type, source)], BuiltinTypes.Void, source);
            target = target.Project(new MirLifetimeProjection());
        }
        if (construction.Value is { } value)
        {
            MirOperand initialized = Value(value);
            _current.Statements.Add(new MirAssign(target, new MirUse(initialized), source));
        }
        else if (construction.Constructor is { } constructor)
        {
            _current.Statements.Add(new MirAssign(target, new MirDefault(construction.ValueType), source));
            _ = Call(Callee(constructor), construction.Arguments, source, Address(target, construction.ValueType, source));
        }
        else if (construction.ValueType is StructTypeSymbol structure)
            ConstructAggregateAt(new BoundStructConstructionExpression(structure, construction.Arguments)
                { IsDefaultInitialization = construction.IsDefaultInitialization }, target, source);
        else _current.Statements.Add(new MirAssign(target, new MirDefault(construction.ValueType), source));
        if (wrapper is not null)
            _current.Statements.Add(new MirSetStorageState(wrapper, true, source));
        Activate(tracked, true, source);
    }

    private MirOperand MoveStorage(MirPlace wrapper, StorageTypeSymbol type, MirSourceInfo source)
    {
        _ = Intrinsic(MirIntrinsicKind.CheckStorageInitialized, [Address(wrapper, type, source)], BuiltinTypes.Void, source);
        MirOperand value = Save(new MirUse(new MirMove(wrapper.Project(new MirLifetimeProjection()), type.ElementType)), source);
        _current.Statements.Add(new MirSetStorageState(wrapper, false, source));
        return value;
    }

    private void DestroyStorage(MirPlace wrapper, StorageTypeSymbol type, FunctionSymbol? destructor, MirSourceInfo source)
    {
        Block destroy = NewBlock(), after = NewBlock();
        MirOperand active = Save(new MirStorageState(wrapper), source);
        End(new MirSwitch(active, [new(new(true, BuiltinTypes.Bool), destroy.Id)], after.Id, source));
        _current = destroy;
        _current.Statements.Add(new MirSetStorageState(wrapper, false, source));
        if (destructor is not null)
            End(new MirDrop(wrapper.Project(new MirLifetimeProjection()), destructor, after.Id,
                GuardedUnwind(_unwindTarget, source).Id, source));
        else Jump(after);
        _current = after;
    }
}
