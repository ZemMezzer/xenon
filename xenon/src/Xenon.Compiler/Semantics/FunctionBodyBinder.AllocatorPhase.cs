using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed partial class FunctionBodyBinder
{
    private bool CanWriteAllocatorField(BoundMemberAccessExpression member) => CanWriteAllocatorField(_function, member);

    private static bool CanWriteAllocatorField(FunctionSymbol function, BoundMemberAccessExpression member) =>
        function.FunctionKind == FunctionKind.AllocationConstructor && member.Field.IsAllocatorField &&
        member.Receiver is BoundThisExpression && TypeIdentity.AreSame(member.Field.ContainingType, function.ContainingType);

    // Check resolved receivers, including implicit ones synthesized for calls, accessors and
    // captures. The only legal use of the incomplete object is as a direct store destination.
    internal static void ValidateAllocatorPhase(FunctionSymbol function, BoundNode node, DiagnosticBag diagnostics,
        TextLocation location, IReadOnlyDictionary<BoundExpression, TextLocation>? locations = null)
    {
        if (node is BoundExpression expression && locations is not null && locations.TryGetValue(expression, out var source))
            location = source;
        switch (node)
        {
            case BoundAssignmentExpression { OperatorKind: SyntaxKind.EqualsToken, CapturesTarget: false,
                Target: BoundMemberAccessExpression target } store when CanWriteAllocatorField(function, target):
                Visit(store.Expression);
                return;
            case BoundStorageConstructExpression { Storage: BoundMemberAccessExpression target } construct
                when CanWriteAllocatorField(function, target):
                if (construct.Value is not null) Visit(construct.Value);
                foreach (var argument in construct.Arguments) Visit(argument);
                return;
            case BoundMemberAccessExpression { Receiver: BoundThisExpression } field:
                diagnostics.Report(location, field.Field.IsAllocatorField
                    ? $"allocator field '{field.Field.Name}' is write-only during allocator initialization"
                    : $"instance field '{field.Field.Name}' is unavailable during allocator initialization",
                    DiagnosticIds.InvalidAllocatorPhaseAccess);
                return;
            case BoundThisExpression or BoundBaseLifecycleCallExpression:
                diagnostics.Report(location, "the object being initialized cannot be used as a receiver or value in @Type",
                    DiagnosticIds.InvalidAllocatorPhaseAccess);
                return;
        }
        foreach (var child in BoundTree.Children(node)) Visit(child);

        void Visit(BoundNode child) => ValidateAllocatorPhase(function, child, diagnostics, location, locations);
    }
}
