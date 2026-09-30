using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private MirRequirementOperand GenericCallee(BoundDeferredGenericOperationExpression expression,
        ImmutableArray<MirOperand> arguments) => new(expression.Requirement,
        (MirGenericOperation)expression.Operation,
        _types.FunctionPointer(expression.Type, arguments.Select(argument => argument.Type)),
        expression.TypeArguments.IsDefault ? [] : expression.TypeArguments,
        (int)expression.OperatorKind, expression.IsPointerAccess);

    private MirOperand? GenericOperation(BoundDeferredGenericOperationExpression expression)
    {
        MirSourceInfo source = Source(expression);
        MirOperand? receiver = expression.Receiver is null ? null : expression.IsPointerAccess
            ? Snapshot(Value(expression.Receiver), source)
            : Address(Place(expression.Receiver), expression.Receiver.Type, source);
        ImmutableArray<BoundExpression> boundArguments = expression.Value is null
            ? expression.Arguments : expression.Arguments.Add(expression.Value);
        ImmutableArray<MirOperand> arguments = Arguments(boundArguments);
        return CallValues(GenericCallee(expression, arguments), arguments, source, receiver);
    }
}