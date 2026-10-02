using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private void Condition(BoundExpression expression, Block yes, Block no)
    {
        if (_diagnosticRecovery && HasBindingError(expression))
        {
            End(new MirSwitch(new MirDeferredConstant(BuiltinTypes.Bool),
                [new(new(true, BuiltinTypes.Bool), yes.Id)], no.Id, Source(expression)));
            return;
        }
        if (expression is BoundFullExpression full)
        {
            if (full.Temporaries.IsEmpty) Condition(full.Expression, yes, no);
            else FullExpression(full, yes, no);
            return;
        }
        if (expression is BoundBinaryExpression { OperatorKind: SyntaxKind.AmpersandAmpersandToken } and)
        {
            Block right = NewBlock();
            Condition(and.Left, right, no);
            _current = right;
            Condition(and.Right, yes, no);
            return;
        }
        if (expression is BoundBinaryExpression { OperatorKind: SyntaxKind.PipePipeToken } or)
        {
            Block right = NewBlock();
            Condition(or.Left, yes, right);
            _current = right;
            Condition(or.Right, yes, no);
            return;
        }
        if (expression is BoundUnaryExpression { OperatorKind: SyntaxKind.BangToken } not)
        {
            Condition(not.Operand, no, yes);
            return;
        }
        End(new MirSwitch(Value(expression), [new(new(true, BuiltinTypes.Bool), yes.Id)], no.Id, Source(expression)));
    }
}