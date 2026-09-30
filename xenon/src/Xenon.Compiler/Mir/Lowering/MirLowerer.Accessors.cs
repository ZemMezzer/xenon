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
        _ = CallValues(Callee(setter), inputs, source, instance, interfaceType, setter.IsVirtual);
        return inputs[^1];
    }

    private MirOperand CompoundAccessor(BoundCompoundAccessorAssignmentExpression expression, MirSourceInfo source)
    {
        MirOperand receiver = expression.IsPointerAccess ? Snapshot(Value(expression.Receiver), source) : Address(Place(expression.Receiver), expression.Receiver.Type, source);
        ImmutableArray<MirOperand> arguments = Arguments(expression.Arguments);
        MirOperand current = CallValues(Callee(expression.Getter), arguments, source, receiver, expression.InterfaceType, expression.Getter.IsVirtual)!;
        MirPlace? previous = _capturedPlace;
        _capturedPlace = ((MirCopy)Snapshot(current, source)).Place;
        MirOperand next;
        try
        {
            MirOperand right = Value(expression.Value);
            next = expression.UsesUserOperator ? right : Save(new MirBinary(BinaryOperator(expression.OperatorKind), current, right, expression.Type), source);
        }
        finally { _capturedPlace = previous; }
        _ = CallValues(Callee(expression.Setter), arguments.Add(next), source, receiver, expression.InterfaceType, expression.Setter.IsVirtual);
        return next;
    }
}
