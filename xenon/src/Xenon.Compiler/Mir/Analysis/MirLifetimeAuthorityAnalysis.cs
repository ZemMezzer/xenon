using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirLifetimeAuthorityViolation(MirLocation Location, MirSourceInfo Source,
    MirLifetimeAuthority? Authority, ImmutableHashSet<MirReferenceOrigin> Origins, bool IsReceiverEffect = false,
    StructTypeSymbol? PartialDestructorOwner = null, bool IsDestruction = false, bool IsPartialStorage = false, bool IsIndirectReceiver = false);

/// <summary>Lifetime operations require one authoritative storage owner.</summary>
public static class MirLifetimeAuthorityAnalysis
{
    public static ImmutableArray<MirLifetimeAuthorityViolation> Check(MirFunction function,
        CancellationToken cancellation = default)
    {
        var origins = new MirReferenceOrigins(function);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(function, cancellation), origins, cancellation);
        var violations = ImmutableArray.CreateBuilder<MirLifetimeAuthorityViolation>();
        foreach (var block in function.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
        {
            for (int index = 0; index < block.Statements.Length; index++)
            {
                if (block.Statements[index] is not MirAssign { IsSemanticRead: true, IsMoveRead: true } move) continue;
                var location = new MirLocation(block.Id, index);
                var state = flow.Before[location];
                foreach (var place in MirOperands.Of(move.Value).SelectMany(MirOperands.Places))
                    CheckPlace(place, state, move.Source, location);
            }
            var end = new MirLocation(block.Id, block.Statements.Length);
            var output = flow.Output[block.Id];
            if (block.Terminator is MirDrop { IsExplicit: true } drop)
                CheckPlace(drop.Place, output, drop.Source, end, destruction: true);
            if (block.Terminator is MirCall { Receiver: { } receiver, Callee: MirFunctionOperand { Function: { } method } } call)
            {
                var effects = method.ReceiverMoveEffects.IsEmpty && method.GenericDefinition is { } definition
                    ? definition.ReceiverMoveEffects : method.ReceiverMoveEffects;
                if (!effects.IsEmpty)
                {
                    var roots = origins.Operand(receiver, output).Roots;
                    // Raw-pointer calls that move only plain values have no caller
                    // cleanup ownership to transfer (including specialized T = int).
                    bool plainIndirect = (call.IsIndirectReceiver || roots.Any(root => root.Kind == MirReferenceOriginKind.RawPointee)) &&
                        effects.All(effect => PlainEffect(method.ContainingType, effect));
                    if (!plainIndirect || roots.Any(root => root.Authority != MirLifetimeAuthority.Owner))
                        CheckOrigins(roots, call.Source, end, receiverEffect: true, indirectReceiver: call.IsIndirectReceiver);
                }
            }
        }
        return violations.ToImmutable();
        void CheckPlace(MirPlace place, ImmutableDictionary<MirLocalId, MirReferenceValue> state,
            MirSourceInfo source, MirLocation location, bool destruction = false)
        {
            var roots = origins.Address(place, state);
            bool wholeStorage = false;
            TypeSymbol type = function.Locals.Single(local => local.Id == place.Local).Type;
            for (int index = 0; index < place.Projections.Length; index++)
            {
                var projection = place.Projections[index];
                if (projection is MirLifetimeProjection && type is StorageTypeSymbol && index + 1 < place.Projections.Length)
                {
                    violations.Add(new(location, source, null, roots, IsDestruction: destruction, IsPartialStorage: true));
                    return;
                }
                if (projection is MirLifetimeProjection && type is StorageTypeSymbol && index + 1 == place.Projections.Length) wholeStorage = true;
                type = projection switch
                {
                    MirDerefProjection when type is ReferenceTypeSymbol reference => reference.ElementType,
                    MirDerefProjection when type is PointerTypeSymbol pointer => pointer.ElementType,
                    MirDerefProjection when type is OwnershipTypeSymbol ownership => ownership.ElementType,
                    MirLifetimeProjection when type is LifetimeModifierTypeSymbol modifier => modifier.ElementType,
                    MirFieldProjection field => field.Field.Type,
                    MirIndexProjection or MirLinearIndexProjection when type is ArrayTypeSymbol array => array.ElementType,
                    MirIndexProjection or MirLinearIndexProjection when type is PointerTypeSymbol pointer => pointer.ElementType,
                    _ => type,
                };
            }
            CheckOrigins(roots, source, location, destruction: destruction, wholeStorage: wholeStorage);
        }
        void CheckOrigins(ImmutableHashSet<MirReferenceOrigin> roots, MirSourceInfo source, MirLocation location, bool receiverEffect = false, bool destruction = false, bool indirectReceiver = false, bool wholeStorage = false)
        {
            if (!receiverEffect && !roots.IsEmpty && roots.All(root => root.Kind == MirReferenceOriginKind.RawPointee && root.Authority == MirLifetimeAuthority.Owner)) return;
            var restricted = roots.FirstOrDefault(root => root.Authority != MirLifetimeAuthority.Owner);
            if (restricted is not null)
                violations.Add(new(location, source, restricted.Authority, roots, receiverEffect));
            else if (receiverEffect && (indirectReceiver || roots.Any(root => root.Kind == MirReferenceOriginKind.RawPointee)))
                violations.Add(new(location, source, null, roots, IsReceiverEffect: true, IsIndirectReceiver: true));
            else if (roots.Count == 0 || roots.Any(root => root.Kind == MirReferenceOriginKind.Unknown) ||
                     roots.DistinctBy(root => (root.Kind, root.Ordinal, root.Path, root.HandleIdentity)).Count() > 1)
                violations.Add(new(location, source, null, roots, receiverEffect));
            else if (!receiverEffect && !wholeStorage && PartialOwner(roots.First()) is { } owner)
                violations.Add(new(location, source, null, roots, PartialDestructorOwner: owner, IsDestruction: destruction));
        }
        static bool PlainEffect(TypeSymbol? type, ReceiverMoveEffect effect)
        {
            foreach (int ordinal in effect.FieldOrdinals)
            {
                if (type is not IFieldStorageTypeSymbol structure ||
                    structure.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal) is not { } field) return false;
                type = field.Type;
            }
            return type is not null && !TypeFacts.RequiresDestruction(type) &&
                !TypeFacts.ContainsReferenceStorage(type) && !TypeFacts.IsPinned(type);
        }
        StructTypeSymbol? PartialOwner(MirReferenceOrigin origin)
        {
            TypeSymbol? type = origin.Kind switch
            {
                MirReferenceOriginKind.Local => function.Locals.FirstOrDefault(local => local.Id.Value == origin.Ordinal)?.Type,
                MirReferenceOriginKind.Receiver => function.Symbol.ContainingType,
                MirReferenceOriginKind.Parameter => function.Symbol.Parameters.FirstOrDefault(parameter => parameter.Ordinal == origin.Ordinal)?.Type,
                _ => null,
            };
            if (origin.Kind == MirReferenceOriginKind.Parameter && type is ReferenceTypeSymbol reference) type = reference.ElementType;
            foreach (string part in origin.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (type is StructTypeSymbol aggregate && aggregate.FindDestructor() is not null) return aggregate;
                if (type is not IFieldStorageTypeSymbol structure || !int.TryParse(part, out int ordinal)) return null;
                type = structure.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal)?.Type;
            }
            return null;
        }
    }
}