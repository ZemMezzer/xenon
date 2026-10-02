using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

/// <summary>Checks retained reference lifetimes at stores and lexical storage exits.</summary>
public static class MirReferenceSafety
{
    public static IEnumerable<MirBorrowViolation> Check(MirFunction function,
        MirDataflowResult<ImmutableDictionary<MirLocalId, MirReferenceValue>> flow,
        MirReferenceOrigins origins)
    {
        var locals = function.Locals.ToDictionary(local => local.Id);
        var scopes = function.Scopes.ToDictionary(scope => scope.Id);
        foreach (var block in function.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
            for (int index = 0; index < block.Statements.Length; index++)
            {
                var location = new MirLocation(block.Id, index);
                var state = flow.Before[location];
                if (block.Statements[index] is MirStorageDead dead && locals[dead.Local].Variable is LocalVariableSymbol expiring)
                {
                    foreach (var (id, value) in state)
                    {
                        if (!locals.TryGetValue(id, out var carrier) || carrier.Variable is not LocalVariableSymbol dependent ||
                            !Storage(carrier.Type) || Outlives(locals[dead.Local], carrier)) continue;
                        foreach (var root in Roots(value, carrier.Type).Where(root => root.Kind == MirReferenceOriginKind.Local && root.Ordinal == dead.Local.Value))
                            yield return new(location, dead.Source, MirBorrowCheck.DestructionOrder, root, id);
                    }
                }
                if (block.Statements[index] is not MirAssign assign) continue;
                var roots = Roots(origins.Value(assign.Value, state), assign.Value.Type).ToArray();
                if (roots.Length == 0) continue;
                foreach (var destination in origins.Address(assign.Destination, state))
                {
                    if (destination.Kind == MirReferenceOriginKind.Local && locals.TryGetValue(new(destination.Ordinal), out var carrier) &&
                        carrier.Variable is LocalVariableSymbol && !Storage(carrier.Type) && carrier.Type is not ReferenceTypeSymbol)
                    {
                        foreach (var root in roots.Where(root => root.Kind == MirReferenceOriginKind.Local &&
                            locals.TryGetValue(new(root.Ordinal), out var referenced) && referenced.Variable is LocalVariableSymbol &&
                            !Outlives(referenced, carrier)))
                            yield return new(location, assign.Source, MirBorrowCheck.DestructionOrder, root, carrier.Id);
                    }
                    else if (assign.IsSemanticWrite && assign.Value.Type is not ReferenceTypeSymbol &&
                             destination.Kind is not (MirReferenceOriginKind.Local or MirReferenceOriginKind.Receiver))
                        foreach (var root in roots.Where(root => root.Kind != MirReferenceOriginKind.Static && !CallablePayload(root)))
                            yield return new(location, assign.Source, MirBorrowCheck.AggregateEscape, root, assign.Destination.Local);
                }
            }
        bool CallablePayload(MirReferenceOrigin origin)
        {
            var local = origin.Kind == MirReferenceOriginKind.Parameter
                ? function.Locals.FirstOrDefault(local => local.Variable is ParameterSymbol parameter && parameter.Ordinal == origin.Ordinal)
                : origin.Kind == MirReferenceOriginKind.Local ? locals.GetValueOrDefault(new(origin.Ordinal)) : null;
            if (local is null) return false;
            TypeSymbol type = local.Type;
            foreach (string component in origin.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
                if (type is ReferenceTypeSymbol reference) type = reference.ElementType;
                if (type is not IFieldStorageTypeSymbol structure || !int.TryParse(component, out int ordinal) ||
                    structure.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal) is not { } field) return false;
                type = field.Type;
            }
            while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
            return type is FunctionValueTypeSymbol;
        }
        bool Outlives(MirLocal referenced, MirLocal carrier)
        {
            if (referenced.Variable!.Locations.IsDefaultOrEmpty || carrier.Variable!.Locations.IsDefaultOrEmpty) return true;
            if (referenced.Variable.Locations[0].Span.Start >= carrier.Variable.Locations[0].Span.Start) return false;
            int? scope = carrier.Source.Scope;
            while (scope is { } id)
            {
                if (id == referenced.Source.Scope) return true;
                scope = scopes[id].Parent;
            }
            return false;
        }
    }
    private static bool Storage(TypeSymbol type) => type is StorageTypeSymbol || type is PinTypeSymbol pin && Storage(pin.ElementType);
    private static IEnumerable<MirReferenceOrigin> Roots(MirReferenceValue value, TypeSymbol type) =>
        MirReferenceOrigins.ReferenceLeaves(type).SelectMany(leaf => value.Project(string.Join('/', leaf.Path)).Roots).Distinct();
}