using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private static bool InvalidCall(BoundExpression expression)
    {
        (IEnumerable<TypeSymbol> Parameters, IEnumerable<BoundExpression> Arguments)? signature = expression switch
        {
            BoundCallExpression call => (call.Function.Parameters.Select(parameter => parameter.Type), call.Arguments),
            BoundMethodCallExpression call => (call.Method.Parameters.Select(parameter => parameter.Type), call.Arguments),
            BoundInterfaceMethodCallExpression call => (call.Method.Parameters.Select(parameter => parameter.Type), call.Arguments),
            BoundConstructorCallExpression call => (call.Constructor.Parameters.Select(parameter => parameter.Type), call.Arguments),
            BoundIndirectCallExpression call => (call.FunctionPointerType.ParameterTypes, call.Arguments),
            BoundFunctionValueCallExpression call => (((FunctionValueTypeSymbol)call.Target.Type).ParameterTypes, call.Arguments),
            _ => null,
        };
        return signature is { } value && (value.Parameters.Count() != value.Arguments.Count() ||
            !value.Parameters.Zip(value.Arguments).All(pair => TypeIdentity.AreSame(pair.First, pair.Second.Type)));
    }

    private void RecoverExpression(BoundExpression expression)
    {
        if (expression is BoundFullExpression full) { RecoverExpression(full.Expression); return; }
        IEnumerable<BoundExpression> arguments = expression switch
        {
            BoundErrorExpression error => error.RecoveryArguments,
            BoundCallExpression call => call.Arguments,
            BoundMethodCallExpression call => call.Arguments,
            BoundInterfaceMethodCallExpression call => call.Arguments,
            BoundConstructorCallExpression call => call.Arguments,
            BoundIndirectCallExpression call => call.Arguments,
            BoundFunctionValueCallExpression call => call.Arguments,
            _ => [],
        };
        foreach (BoundExpression argument in arguments)
        {
            if (HasBindingError(argument)) RecoverExpression(argument);
            else _ = Expression(argument);
        }
    }
}