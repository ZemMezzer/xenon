using Xenon.Compiler.Semantics.Binding;

namespace Xenon.Compiler.Semantics;

internal enum ConstantFoldStatus
{
    Folded,
    TargetDependent,
    Invalid,
}

internal sealed class ConstantEvaluationContext(ITargetTypeLayout? targetLayout)
{
    public ITargetTypeLayout? TargetLayout { get; set; } = targetLayout;
    public bool RequiresTargetLayout { get; private set; }
    public void RequireTargetLayout() => RequiresTargetLayout = true;

    public ConstantFoldStatus Fold(BoundExpression expression, out object? value)
    {
        ConstantFoldStatus status = SemanticAnalyzer.FoldConstantExpression(expression, out value, TargetLayout);
        if (status == ConstantFoldStatus.TargetDependent) RequiresTargetLayout = true;
        return status;
    }

    public bool TryFold(BoundExpression expression, out object? value) => Fold(expression, out value) == ConstantFoldStatus.Folded;
}
