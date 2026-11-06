using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

internal static class AllocatorFieldStorage
{
    // Projecting into an embedded value preserves its storage. Following a pointer
    // or reference instead begins a new place, with the pointee's own mutability.
    internal static FieldSymbol? Find(BoundExpression place) => place switch
    {
        BoundMemberAccessExpression { Field.IsAllocatorField: true } field => field.Field,
        BoundMemberAccessExpression { IsPointerAccess: false } field => Find(field.Receiver),
        BoundDeferredGenericOperationExpression { Operation: BoundDeferredGenericOperationKind.FieldGet,
            Requirement: FieldSymbol { IsAllocatorField: true } field } => field,
        BoundLifetimeValueExpression value => Find(value.Source),
        _ => null,
    };
}
