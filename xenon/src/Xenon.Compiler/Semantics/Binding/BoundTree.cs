namespace Xenon.Compiler.Semantics.Binding;

internal static class BoundTree
{
    public static IEnumerable<BoundNode> DescendantsAndSelf(BoundNode root)
    {
        yield return root;
        foreach (BoundNode child in Children(root))
            foreach (BoundNode descendant in DescendantsAndSelf(child))
                yield return descendant;
    }

    public static IEnumerable<BoundNode> Children(BoundNode node) => node switch
    {
        BoundBlockStatement value => value.Statements.Cast<BoundNode>().Concat(Optional(value.ExitCleanup)),
        BoundVariableDeclarationStatement value => Optional(value.Initializer),
        BoundReturnStatement value => Optional(value.Expression),
        BoundExpressionStatement value => [value.Expression],
        BoundIfStatement value => new BoundNode?[]
            { value.Condition, value.ThenStatement, value.ElseStatement }.OfType<BoundNode>(),
        BoundWhileStatement value => [value.Condition, value.Body],
        BoundForStatement value => new BoundNode?[]
            { value.Initializer, value.Condition, value.Increment, value.Body }.OfType<BoundNode>(),
        BoundSwitchStatement value => new[] { value.Expression }.Concat(value.Sections.SelectMany(section =>
            new BoundNode?[] { section.Value, section.Body }.OfType<BoundNode>())),
        BoundTryStatement value => new BoundNode[] { value.Body }
            .Concat(value.Catches.Select(handler => handler.Body)).Concat(Optional(value.FinallyBody)),
        BoundThrowStatement value => Optional(value.Expression),
        BoundUnaryExpression value => [value.Operand],
        BoundMoveExpression value => [value.Source],
        BoundCopyExpression value => [value.Source],
        BoundUniqueAdoptionExpression value => [value.Allocation],
        BoundSharedAdoptionExpression value => [value.Allocation],
        BoundWeakConversionExpression value => [value.Shared],
        BoundLockExpression value => [value.Weak],
        BoundFullExpression value => new[] { value.Expression }.Concat(value.Temporaries.Select(item => item.Value)),
        BoundBinaryExpression value => [value.Left, value.Right],
        BoundAssignmentExpression value => [value.Target, value.Expression],
        BoundCompareExchangeExpression value => [value.Target, value.Expected, value.Desired],
        BoundSwapExpression value => [value.Left, value.Right],
        BoundCompoundAccessorAssignmentExpression value => new[] { value.Receiver }.Concat(value.Arguments).Append(value.Value),
        BoundMethodCallExpression value => new[] { value.Receiver }.Concat(value.Arguments),
        BoundDeferredGenericMethodCallExpression value => new[] { value.Receiver }.Concat(value.Arguments),
        BoundDeferredGenericOperationExpression value => Optional(value.Receiver)
            .Concat(value.Arguments).Concat(Optional(value.Value)),
        BoundPropertySetExpression value => [value.Receiver, value.Value],
        BoundInterfacePropertySetExpression value => [value.Receiver, value.Value],
        BoundIndexerSetExpression value => new[] { value.Receiver }.Concat(value.Arguments).Append(value.Value),
        BoundInterfaceIndexerSetExpression value => new[] { value.Receiver }.Concat(value.Arguments).Append(value.Value),
        BoundMemberAccessExpression value => [value.Receiver],
        BoundCastExpression value => [value.Expression],
        BoundInterfaceConversionExpression value => [value.Source],
        BoundReferenceConversionExpression value => [value.Source],
        BoundReferenceDereferenceExpression value => [value.Reference],
        BoundLifetimeValueExpression value => [value.Source],
        BoundStorageConstructExpression value => new[] { value.Storage }.Concat(Optional(value.Value)).Concat(value.Arguments),
        BoundExplicitDestructExpression value => [value.Target],
        BoundStorageMoveExpression value => [value.Storage],
        BoundInterfaceMethodCallExpression value => new[] { value.Receiver }.Concat(value.Arguments),
        BoundIndexExpression value => new[] { value.Receiver }.Concat(value.Indices),
        BoundStructConstructionExpression value => value.Arguments,
        BoundConstructorCallExpression value => value.Arguments,
        BoundBaseLifecycleCallExpression value => value.Arguments,
        BoundArrayCreationExpression value => value.Dimensions,
        BoundArrayMetadataExpression value => new[] { value.Receiver }.Concat(Optional(value.Dimension)),
        BoundNewExpression value => value.Arguments,
        BoundFreeExpression value => [value.Pointer],
        BoundCallExpression value => value.Arguments,
        BoundIndirectCallExpression value => new[] { value.Target }.Concat(value.Arguments),
        BoundFunctionValueExpression value => value.Captures.Select(capture => (BoundNode)capture.Initializer),
        BoundFunctionValueCallExpression value => new[] { value.Target }.Concat(value.Arguments),
        _ => [],
    };

    private static IEnumerable<BoundNode> Optional(BoundNode? value) => value is null ? [] : [value];
}
