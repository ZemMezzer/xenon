using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private MirFunctionOperand Callee(FunctionSymbol function) =>
        new(function, _types.FunctionPointer(function.ReturnType, function.Parameters.Select(p => p.Type)));

    private MirOperand SetAccessor(BoundExpression receiver, FunctionSymbol setter, ImmutableArray<BoundExpression> arguments,
        BoundExpression value, bool pointer, InterfaceTypeSymbol? interfaceType, MirSourceInfo source)
    {
        MirOperand instance = pointer ? Snapshot(Value(receiver), source) : Address(Place(receiver), receiver.Type, source);
        ImmutableArray<MirOperand> inputs = Arguments(arguments.Add(value));
        _ = CallValues(Callee(setter), inputs, source, instance, interfaceType, setter.VTableSlot is not null);
        return inputs[^1];
    }

    private MirOperand CompoundAccessor(BoundCompoundAccessorAssignmentExpression expression, MirSourceInfo source)
    {
        MirOperand receiver = expression.IsPointerAccess ? Snapshot(Value(expression.Receiver), source) : Address(Place(expression.Receiver), expression.Receiver.Type, source);
        ImmutableArray<MirOperand> arguments = Arguments(expression.Arguments);
        var originalGuards = new List<TemporaryGuard>();
        var copyGuards = new List<TemporaryGuard>();
        foreach (MirOperand argument in arguments)
            if (GuardValue(argument, source) is { } guard) originalGuards.Add(guard);
        var copies = ImmutableArray.CreateBuilder<MirOperand>();
        foreach (MirOperand argument in arguments)
        {
            MirOperand copy = TypeFacts.RequiresDestruction(argument.Type)
                ? Intrinsic(MirIntrinsicKind.CloneValue, [argument], argument.Type, source)! : argument;
            copies.Add(copy);
            if (GuardValue(copy, source) is { } guard) copyGuards.Add(guard);
        }
        foreach (TemporaryGuard guard in copyGuards) EndValueGuard(guard, source);
        MirOperand current = CallValues(Callee(expression.Getter), copies.ToImmutable(), source, receiver, expression.InterfaceType, expression.Getter.VTableSlot is not null)!;
        MirPlace? previous = _capturedPlace;
        _capturedPlace = ((MirCopy)Snapshot(current, source)).Place;
        MirOperand next;
        try
        {
            MirOperand right = Value(expression.Value);
            next = expression.UsesUserOperator ? right : Save(new MirBinary(BinaryOperator(expression.OperatorKind), current, right, expression.Type), source);
        }
        finally { _capturedPlace = previous; }
        foreach (TemporaryGuard guard in originalGuards) EndValueGuard(guard, source);
        _ = CallValues(Callee(expression.Setter), arguments.Add(next), source, receiver, expression.InterfaceType, expression.Setter.VTableSlot is not null);
        return next;
    }
}
