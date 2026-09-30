using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private MirOperand ConvertValue(MirOperand value, TypeSymbol type, MirSourceInfo source) =>
        TypeIdentity.AreSame(value.Type, type) ? value :
        Save(type is AtomicTypeSymbol atomic ? new MirAtomicValue(value, atomic) : new MirCast(value, type), source);

    private MirOperand Address(MirPlace place, TypeSymbol type, MirSourceInfo source) =>
        Save(new MirBorrow(place, MirBorrowKind.Raw, _types.PointerTo(type)), source);

    private MirOperand? Intrinsic(MirIntrinsicKind intrinsic, ImmutableArray<MirOperand> arguments,
        TypeSymbol type, MirSourceInfo source, FunctionSymbol? function = null, TypeSymbol? subjectType = null,
        ImmutableArray<CaptureVariableSymbol> captures = default, MirBinaryOperator? op = null, bool returnsOldValue = false, ulong? fixedArrayLength = null, FieldSymbol? field = null)
    {
        MirPlace? result = TypeIdentity.AreSame(type, BuiltinTypes.Void) ? null : Temporary(type, source);
        Block next = NewBlock();
        End(new MirIntrinsicCall(intrinsic, arguments, type, result, next.Id, GuardedUnwind(_unwindTarget, source).Id, source)
        { Function = function, SubjectType = subjectType, Captures = captures.IsDefault ? [] : captures, Operator = op, ReturnsOldValue = returnsOldValue, FixedArrayLength = fixedArrayLength, Field = field });
        _current = next;
        return result is null ? null : new MirCopy(result, type);
    }

    private MirOperand? MethodCall(FunctionSymbol method, BoundExpression receiver,
        ImmutableArray<BoundExpression> arguments, bool pointerAccess, InterfaceTypeSymbol? interfaceType, MirSourceInfo source)
    {
        MirOperand instance = pointerAccess ? Snapshot(Value(receiver), source) :
            Save(new MirBorrow(Place(receiver), MirBorrowKind.Raw, _types.PointerTo(receiver.Type)), source, semanticRead: true);
        var callee = new MirFunctionOperand(method, _types.FunctionPointer(method.ReturnType, method.Parameters.Select(p => p.Type)));
        MirOperand? result = Call(callee, arguments, source, instance, interfaceType, method.VTableSlot is not null);
        ApplyReceiverMoves(receiver, method, source);
        return result;
    }

    private void ApplyReceiverMoves(BoundExpression receiver, FunctionSymbol method, MirSourceInfo source)
    {
        ImmutableArray<ReceiverMoveEffect> effects = method.ReceiverMoveEffects.IsEmpty &&
            method.GenericDefinition is { } definition ? definition.ReceiverMoveEffects : method.ReceiverMoveEffects;
        MirPlace? receiverPlace = TrackedReceiver(receiver);
        if (receiverPlace is null) return;
        foreach (ReceiverMoveEffect effect in effects)
        {
            MirPlace place = receiverPlace;
            TypeSymbol type = receiver.Type;
            bool valid = true;
            foreach (int ordinal in effect.FieldOrdinals)
            {
                if (type is not StructTypeSymbol structure ||
                    structure.Fields.FirstOrDefault(field => field.Ordinal == ordinal) is not { } field)
                { valid = false; break; }
                place = place.Project(new MirFieldProjection(field));
                type = field.Type;
            }
            if (valid) Forget(place, source);
        }
    }

    private MirPlace? TrackedReceiver(BoundExpression receiver) => receiver switch
    {
        BoundVariableExpression variable => Variable(variable.Variable),
        BoundMemberAccessExpression { IsPointerAccess: false } member =>
            TrackedReceiver(member.Receiver)?.Project(new MirFieldProjection(member.Field)),
        BoundLifetimeValueExpression lifetime => TrackedReceiver(lifetime.Source)?.Project(new MirLifetimeProjection()),
        _ => null,
    };
    private MirOperand ConstructAggregate(BoundStructConstructionExpression construction, MirSourceInfo source)
    {
        MirPlace result = Temporary(construction.Type, source);
        _current.Statements.Add(new MirStorageLive(result.Local, source));
        ConstructAggregateAt(construction, result, source);
        return new MirCopy(result, construction.Type);
    }

    private void ConstructAggregateAt(BoundStructConstructionExpression construction, MirPlace result, MirSourceInfo source)
    {
        if (construction.IsDefaultInitialization)
            _current.Statements.Add(new MirAssign(result, new MirDefault(construction.Type), source));
        if (construction.StructType.HasVirtualDispatch)
            _current.Statements.Add(new MirInitializeDispatch(result, construction.StructType, source));
        Initialize(construction.StructType);
        MirOperand[] values = Arguments(construction.Arguments).ToArray();
        for (int index = 0; index < values.Length; index++)
            _current.Statements.Add(new MirAssign(result.Project(new MirFieldProjection(construction.StructType.AllInstanceFields[index])), new MirUse(ConvertValue(values[index], construction.StructType.AllInstanceFields[index].Type, source)), source));
        void Initialize(StructTypeSymbol type)
        {
            if (type.BaseType is { } baseType) Initialize(baseType);
            if (type.InstanceInitializer is { } initializer)
                _ = Call(new MirFunctionOperand(initializer, _types.FunctionPointer(BuiltinTypes.Void, [])), [], source,
                    Address(result, construction.Type, source));
        }
    }

    private MirOperand Construct(BoundConstructorCallExpression construction, MirSourceInfo source)
    {
        MirPlace result = Temporary(construction.Type, source);
        _current.Statements.Add(new MirStorageLive(result.Local, source));
        _current.Statements.Add(new MirAssign(result, new MirDefault(construction.Type), source));
        var callee = new MirFunctionOperand(construction.Constructor,
            _types.FunctionPointer(BuiltinTypes.Void, construction.Constructor.Parameters.Select(p => p.Type)));
        _ = Call(callee, construction.Arguments, source, Address(result, construction.Type, source));
        return new MirCopy(result, construction.Type);
    }
}
