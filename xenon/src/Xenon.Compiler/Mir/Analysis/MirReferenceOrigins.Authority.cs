using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirReferenceOrigins
{
    internal TypeSymbol PlaceType(MirPlace place)
    {
        TypeSymbol type = _locals[place.Local].Type;
        foreach (var projection in place.Projections)
            type = projection switch
            {
                MirDerefProjection when type is ReferenceTypeSymbol reference => reference.ElementType,
                MirDerefProjection when Element(type) is { } element => element,
                MirFieldProjection field => field.Field.Type,
                MirLifetimeProjection when type is LifetimeModifierTypeSymbol modifier => modifier.ElementType,
                MirIndexProjection or MirLinearIndexProjection when type is PointerTypeSymbol pointer => pointer.ElementType,
                MirIndexProjection or MirLinearIndexProjection when type is ArrayTypeSymbol array => array.ElementType,
                _ => type,
            };
        return type;
    }
    private static MirReferenceValue Restrict(MirReferenceValue value, MirLifetimeAuthority authority) =>
        value with { Roots = value.Roots.Select(root => root with { Authority = authority }).ToImmutableHashSet() };

    private MirReferenceValue Borrow(MirBorrow borrow, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var value = new MirReferenceValue(Address(borrow.Place, state).Select(origin =>
            borrow.Kind == MirBorrowKind.Shared ? origin with { IsReadonly = true } : origin).ToImmutableHashSet(),
            MirReferenceValue.Empty.Fields) { Aliases = ThroughAliases(borrow.Place, state) };

        TypeSymbol type = _locals[borrow.Place.Local].Type;
        foreach (var projection in borrow.Place.Projections)
        {
            if (projection is MirLifetimeProjection && type is StorageTypeSymbol)
                value = Restrict(value, MirLifetimeAuthority.StorageValue);
            type = projection switch
            {
                MirDerefProjection when type is ReferenceTypeSymbol reference => reference.ElementType,
                MirDerefProjection when Element(type) is { } element => element,
                MirFieldProjection field => field.Field.Type,
                MirLifetimeProjection when type is LifetimeModifierTypeSymbol modifier => modifier.ElementType,
                MirIndexProjection or MirLinearIndexProjection when type is PointerTypeSymbol pointer => pointer.ElementType,
                MirIndexProjection or MirLinearIndexProjection when type is ArrayTypeSymbol array => array.ElementType,
                _ => type,
            };
        }
        return value;
    }
}