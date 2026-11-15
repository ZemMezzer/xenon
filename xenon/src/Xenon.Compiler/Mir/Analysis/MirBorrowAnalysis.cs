using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public enum MirBorrowCheck { Read, Mutation, Borrow, Move, Destruct, Free, DestructionOrder, AggregateEscape }
public sealed record MirBorrowViolation(MirLocation Location, MirSourceInfo Source, MirBorrowCheck Check,
    MirReferenceOrigin Place, MirLocalId Alias, string? Detail = null);

/// <summary>Loan conflicts at MIR accesses, using reaching origins and backward storage liveness.</summary>
public sealed class MirBorrowAnalysis(MirFunction function, CancellationToken cancellation = default,
    IReadOnlyDictionary<FunctionSymbol, MirLifetimeSummary>? lifetimeSummaries = null,
    IReadOnlyDictionary<FunctionSymbol, MirReferenceValue>? pointerReturns = null)
{
    private sealed record Loan(MirLocalId Alias, MirReferenceOrigin Place, bool IsReadonly);
    private readonly Dictionary<MirLocalId, MirLocal> _locals = function.Locals.ToDictionary(local => local.Id);
    public ImmutableArray<MirBorrowViolation> Check()
    {
        var graph = MirFlowFacts.Graph(function, cancellation, precisePointerFacts: lifetimeSummaries is not null);
        var origins = new MirReferenceOrigins(function, pointerLifetimes: lifetimeSummaries is not null, pointerReturns: pointerReturns,
            checkedPointerIdentities: lifetimeSummaries is not null);
        var flow = MirDataflow.Solve(graph, origins, cancellation);
        var live = MirDataflow.Solve(graph, new MirLiveness(), cancellation);
        var violations = new HashSet<MirBorrowViolation>();
        var facts = lifetimeSummaries is null ? null : new MirFlowFacts(precisePointerFacts: true);
        var feasible = facts is null ? null : MirDataflow.Solve(graph, facts, cancellation);
        foreach (MirBasicBlock block in function.Blocks.Where(block => graph.Reachable.Contains(block.Id)))
        {
            cancellation.ThrowIfCancellationRequested();
            if (lifetimeSummaries is not null && block.Terminator is MirCall effectCall)
            {
                var location = new MirLocation(block.Id, block.Statements.Length);
                CheckCall(effectCall, flow.Output[block.Id], location, Active(flow.Output[block.Id], live.Before[location]).ToArray(), []);
            }
            for (int index = 0; index < block.Statements.Length; index++)
            {
                var location = new MirLocation(block.Id, index);
                if (block.Statements[index] is not MirAssign assign) continue;
                var state = flow.Before[location];
                var loans = Active(state, live.Before[location]).ToArray();
                if (MirLifetimeOperations.Of(assign) == MirLifetimeTransition.BorrowCreation && assign.Value is MirBorrow borrow)
                    Access(origins.Address(borrow.Place, state), origins.ThroughAliases(borrow.Place, state),
                        borrow.Kind == MirBorrowKind.Shared, MirBorrowCheck.Borrow, assign.Source, location, loans);
                else if (assign.IsSemanticRead)
                {
                    MirPlace? read = assign.Value is MirBorrow address ? address.Place :
                        MirOperands.Of(assign.Value).SelectMany(MirOperands.Places).FirstOrDefault();
                    if (read is not null)
                    {
                        var places = origins.Address(read, state);
                        if (assign.IsMoveRead && assign.Value.Type is OwnershipTypeSymbol)
                            places = places.Union(origins.Read(read, state).Roots);
                        Access(places, origins.ThroughAliases(read, state), !assign.IsMoveRead,
                            assign.IsMoveRead ? MirBorrowCheck.Move : MirBorrowCheck.Read, assign.Source, location, loans);
                    }
                }
                if (assign.IsSemanticWrite)
                    Access(origins.Address(assign.Destination, state), origins.ThroughAliases(assign.Destination, state),
                        false, MirBorrowCheck.Mutation, assign.Source, location, loans);
                if (assign.IsDeclaration && _locals[assign.Destination.Local].Type is ReferenceTypeSymbol reference)
                {
                    var value = origins.Value(assign.Value, state);
                    Access(value.Roots, value.Aliases.Union(value.Lineage).Union(MirOperands.Of(assign.Value).SelectMany(MirOperands.Places).Select(place => place.Local)), reference.IsReadonly,
                        MirBorrowCheck.Borrow, assign.Source, location, loans);
                }
            }
            if (block.Terminator is MirDrop { IsExplicit: true } drop)
            {
                var location = new MirLocation(block.Id, block.Statements.Length);
                var state = flow.Output[block.Id];
                Access(origins.Address(drop.Place, state), origins.ThroughAliases(drop.Place, state),
                    false, MirBorrowCheck.Destruct, drop.Source, location, Active(state, live.Before[location]).ToArray());
            }
            if (block.Terminator is MirIntrinsicCall intrinsic)
            {
                var location = new MirLocation(block.Id, block.Statements.Length);
                var state = flow.Output[block.Id];
                var loans = Active(state, live.Before[location]).ToArray();
                int writes = intrinsic.Intrinsic switch
                {
                    MirIntrinsicKind.Swap => 2,
                    MirIntrinsicKind.Free or MirIntrinsicKind.AtomicStore or MirIntrinsicKind.AtomicUpdate or
                        MirIntrinsicKind.AtomicExchange or MirIntrinsicKind.AtomicInitialize or
                        MirIntrinsicKind.CompareExchange or MirIntrinsicKind.CompareExchangeOwned => 1,
                    _ => 0,
                };
                foreach (var argument in intrinsic.Arguments.Take(writes))
                {
                    var value = origins.Operand(argument, state);
                    Access(value.Roots, value.Aliases.Union(value.Lineage), false,
                        intrinsic.Intrinsic is MirIntrinsicKind.Free ? MirBorrowCheck.Free : MirBorrowCheck.Mutation,
                        intrinsic.Source, location, loans);
                }
            }
            if (block.Terminator is MirCall call)
            {
                var location = new MirLocation(block.Id, block.Statements.Length);
                var state = flow.Output[block.Id];
                var loans = Active(state, live.Before[location]).ToArray();
                if (call.Receiver is { } receiver && call.Callee is MirFunctionOperand { Function: { } method } &&
                    method.FunctionKind is not (FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer))
                {
                    var value = origins.Operand(receiver, state);
                    Access(value.Roots, value.Aliases.Union(value.Lineage), method.IsReadonly,
                        method.IsReadonly ? MirBorrowCheck.Read : MirBorrowCheck.Mutation, call.Source, location, loans);
                }
                // The established reference-argument checks remain in their
                // original pass; receiver accesses also need precise raw aliases.
                if (lifetimeSummaries is not null) continue;
                var arguments = call.Arguments.SelectMany((operand, index) =>
                {
                    var value = origins.Operand(operand, state);
                    return MirReferenceOrigins.ReferenceLeaves(operand.Type)
                        .Where(leaf => !IsCallableLeaf(operand.Type, leaf.Path) ||
                            !value.Aliases.Any(alias => _locals[alias].Variable is not null &&
                                MirReferenceOrigins.ReferenceLeaves(_locals[alias].Type).Any(item => IsCallableLeaf(_locals[alias].Type, item.Path))))
                        .Select(leaf => (Index: index, Operand: operand, Value: value.Project(string.Join('/', leaf.Path)), Readonly: leaf.IsReadonly));
                }).ToArray();
                foreach (var argument in arguments)
                    Access(argument.Value.Roots, argument.Value.Aliases.Union(argument.Value.Lineage), argument.Readonly,
                        MirBorrowCheck.Borrow, call.Source, location, loans, arguments.SelectMany(argument => argument.Value.Roots).ToHashSet());
                for (int index = 0; index < arguments.Length; index++)
                    foreach (var prior in arguments.Take(index).Where(prior => prior.Index != arguments[index].Index))
                        foreach (var place in arguments[index].Value.Roots.Where(Tracked))
                            if (prior.Value.Roots.Any(root => Overlaps(place, root) && !(prior.Readonly && arguments[index].Readonly)))
                            {
                                MirLocalId? alias = MirOperands.Places(prior.Operand).Select(place => (MirLocalId?)place.Local).FirstOrDefault();
                                if (alias is { } id) violations.Add(new(location, call.Source, MirBorrowCheck.Borrow, place with { Authority = MirLifetimeAuthority.Owner, IsReadonly = false }, id));
                            }
            }
        }
        if (lifetimeSummaries is null) violations.UnionWith(MirReferenceSafety.Check(function, flow, origins));
        return [.. violations];

        void CheckCall(MirCall context, ImmutableDictionary<MirLocalId, MirReferenceValue> state, MirLocation location,
            Loan[] loans, HashSet<FunctionSymbol> visiting, MirReferenceValue? callable = null)
        {
            foreach (var call in origins.BoundCalls(context, state, callable))
            {
                var callee = MirPointerLifetimeAnalysis.DirectCallee(call);
                if (callee is not null && !visiting.Add(callee))
                {
                    Access(origins.CallablePayload(callable ?? origins.Operand(context.Callee, state), state), [], false,
                        MirBorrowCheck.Borrow, context.Source, location, loans, detail: "recursive callable coverage");
                    continue;
                }
                MirLifetimeSummary? summary = null;
                if (callee is not null) lifetimeSummaries!.TryGetValue(callee, out summary);
                string name = callee?.FullName ?? "indirect call";
                foreach (var effect in (summary?.Effects ?? []).Where(effect => effect.Phase == MirLifetimeEffectPhase.Operation &&
                    (effect.InvalidatesBorrow || effect.Kind is MirLifetimeTransition.PlacementReconstruction or MirLifetimeTransition.StorageRead))
                    .OrderBy(MirPointerLifetimeAnalysis.EffectKey, StringComparer.Ordinal))
                {
                    if (!Possible(effect.Guard)) continue;
                    var value = origins.MapPointerEffect(call.Arguments[effect.Parameter], effect, state);
                    var argument = origins.Operand(call.Arguments[effect.Parameter], state);
                    bool read = effect.Kind == MirLifetimeTransition.StorageRead;
                    Access(value.Roots, call.Arguments[effect.Parameter].Type is ReferenceTypeSymbol ? argument.Aliases.Union(argument.Lineage) : [], read,
                        read ? MirBorrowCheck.Read : effect.Storage ? MirBorrowCheck.Free :
                        effect.Kind == MirLifetimeTransition.PlacementReconstruction ? MirBorrowCheck.Mutation : MirBorrowCheck.Destruct,
                        context.Source, location, loans, detail: name);
                }
                foreach (var uncertainty in MirLifetimeOperations.Coverage(call, summary))
                {
                    if (!Possible(uncertainty.Guard)) continue;
                    var operand = call.Arguments[uncertainty.Parameter];
                    var value = origins.MapPointerParameter(operand, uncertainty.Path, state);
                    if (uncertainty.Reason == "callable-invocation" || value.CallableTargets.GetValueOrDefault("", []).Count != 0)
                    {
                        if (value.CallableTargets.Count != 0)
                            CheckCall(origins.DeferredInvocation(context, operand), state, location, loans, visiting, value);
                        else Access(origins.CallablePayload(value, state), [], false, MirBorrowCheck.Borrow,
                            context.Source, location, loans, detail: name + ": unknown callable payload");
                        continue;
                    }
                    var argument = origins.Operand(operand, state);
                    IEnumerable<MirReferenceOrigin> targets = value.CallableTargets.Count != 0 ? origins.CallablePayload(value, state) :
                        value.Roots.Concat(value.Fields.Values.SelectMany(roots => roots));
                    if (uncertainty.UnknownOffset) targets = targets.Select(root => root with { UnknownObjectOffset = true });
                    bool read = uncertainty.Transition == MirLifetimeTransition.StorageRead;
                    Access(targets, operand.Type is ReferenceTypeSymbol ? argument.Aliases.Union(argument.Lineage) : [], read,
                        read ? MirBorrowCheck.Read : MirBorrowCheck.Borrow, context.Source, location, loans,
                        detail: $"{name}: {(summary?.IsImported == true ? "imported contract, " : "")}{uncertainty.Reason} (parameter {uncertainty.Parameter}, path '{uncertainty.Path}')");
                }
                if (callee is not null) visiting.Remove(callee);

                bool Possible(MirPointerParameterGuard? guard)
                {
                    var possible = feasible!.Output[location.Block];
                    for (; guard is not null && possible.Reachable; guard = guard.Next)
                        possible = guard.Operator is { } op ? facts!.AssumeComparison(call.Arguments[guard.Parameter], op, guard.Constant!, guard.Value, possible) :
                            facts!.Assume(call.Arguments[guard.Parameter], guard.Value, possible);
                    return possible.Reachable;
                }
            }
        }

        IEnumerable<Loan> Active(ImmutableDictionary<MirLocalId, MirReferenceValue> state, ImmutableHashSet<MirLocalId> alive)
        {
            foreach (MirLocalId local in alive)
            {
                if (_locals[local].Kind is MirLocalKind.Parameter or MirLocalKind.Receiver or MirLocalKind.Capture || !state.TryGetValue(local, out var value)) continue;
                var leaves = MirReferenceOrigins.ReferenceLeaves(_locals[local].Type).ToArray();
                if (leaves.Length == 0) continue;
                var aliases = _locals[local].Variable is LocalVariableSymbol ? ImmutableHashSet.Create(local) : value.Aliases;
                if (aliases.IsEmpty) aliases = [local];
                // A direct reference argument is reserved while later operands are
                // evaluated and activated by the call's pairwise loan check.
                if (_locals[local].Kind == MirLocalKind.Temporary && _locals[local].Type is ReferenceTypeSymbol &&
                    aliases.All(alias => _locals[alias].Kind == MirLocalKind.Temporary)) continue;
                foreach (var leaf in leaves)
                    foreach (var origin in value.Project(string.Join('/', leaf.Path)).Roots.Where(Tracked))
                        foreach (var alias in aliases)
                            yield return new(alias, origin, leaf.IsReadonly || origin.IsReadonly);
            }
        }
        void Access(IEnumerable<MirReferenceOrigin> places, ImmutableHashSet<MirLocalId> through, bool shared,
            MirBorrowCheck check, MirSourceInfo source, MirLocation location, Loan[] loans, HashSet<MirReferenceOrigin>? arguments = null, string? detail = null)
        {
            foreach (var place in places.Where(Tracked))
            {
                var conflict = loans.FirstOrDefault(loan => !through.Contains(loan.Alias) &&
                    !(shared && loan.IsReadonly) && Overlaps(place, loan.Place) &&
                    // The argument's own ephemeral borrow is consumed by this call.
                    !(arguments is not null && _locals[loan.Alias].Kind == MirLocalKind.Temporary && arguments.Contains(loan.Place)));
                if (conflict is not null) violations.Add(new(location, source, check, place with { Authority = MirLifetimeAuthority.Owner, IsReadonly = false }, conflict.Alias, detail));
            }
        }
    }
    private static bool IsCallableLeaf(TypeSymbol type, ImmutableArray<int> path)
    {
        foreach (int ordinal in path)
        {
            while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
            if (type is not IFieldStorageTypeSymbol structure) return false;
            type = structure.AllInstanceFields.Single(field => field.Ordinal == ordinal).Type;
        }
        while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
        return type is FunctionValueTypeSymbol;
    }
    private bool Tracked(MirReferenceOrigin origin)
    {
        if (origin.Kind is MirReferenceOriginKind.Static or MirReferenceOriginKind.Unknown) return false;
        MirLocal? local = origin.Kind == MirReferenceOriginKind.Parameter
            ? function.Locals.FirstOrDefault(local => local.Variable is ParameterSymbol parameter && parameter.Ordinal == origin.Ordinal)
            : origin.Kind == MirReferenceOriginKind.Local ? _locals.GetValueOrDefault(new(origin.Ordinal)) : null;
        if (local?.Variable is null) return true;
        TypeSymbol? type = local.Type;
        foreach (string component in origin.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
            if (type is ReferenceTypeSymbol reference) type = reference.ElementType;
            if (type is not IFieldStorageTypeSymbol structure || !int.TryParse(component, out int ordinal)) return true;
            type = structure.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal)?.Type;
        }
        while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
        // An input callable represents its copied payload, not a borrow of the
        // parameter's local carrier storage. Concrete captured loans still retain
        // their own non-callable source origins.
        return type is not FunctionValueTypeSymbol;
    }
    public static bool Overlaps(MirReferenceOrigin left, MirReferenceOrigin right)
    {
        if (left.Kind != right.Kind) return false;
        if (left.Kind is MirReferenceOriginKind.RawPointee or MirReferenceOriginKind.UniquePointee or MirReferenceOriginKind.SharedPointee)
        {
            // One aggregate factory result can contain several allocations with
            // the same caller carrier identity. Concrete allocation sites and
            // contexts distinguish them; opaque handles retain may-alias rules.
            if (left.AllocationSite is not null && right.AllocationSite is not null &&
                (left.AllocationSite != right.AllocationSite || left.AllocationContext != right.AllocationContext)) return false;
            // A cast changes the access type, not the allocation identity.
            if (left.HandleIdentity != right.HandleIdentity && left.PointeeType is not null && right.PointeeType is not null &&
                !TypeIdentity.AreSame(left.PointeeType, right.PointeeType)) return false;
            if (left.Kind == MirReferenceOriginKind.UniquePointee &&
                left.SharedOwner is { } leftOwner && right.SharedOwner is { } rightOwner)
            {
                if (leftOwner.Type is not null && rightOwner.Type is not null &&
                    !TypeIdentity.AreSame(leftOwner.Type, rightOwner.Type)) return false;
                if (leftOwner.Identity != rightOwner.Identity && leftOwner.IsFresh && rightOwner.IsFresh) return false;
                if (!leftOwner.Path.Split('/').Zip(rightOwner.Path.Split('/')).All(pair =>
                    pair.First == "*" || pair.Second == "*" || pair.First == pair.Second)) return false;
            }
            else if (left.HandleIdentity != right.HandleIdentity &&
                !(left.Kind == MirReferenceOriginKind.SharedPointee && (!left.IsFresh || !right.IsFresh))) return false;
        }
        else if (left.Ordinal != right.Ordinal) return false;
        // Relative raw arithmetic may reach another subobject (including one
        // before the current pointer). It retains allocation identity, but no
        // field/index path can prove disjointness within that allocation.
        if (left.UnknownObjectOffset || right.UnknownObjectOffset) return true;
        string[] a = left.Path.Split('/', StringSplitOptions.RemoveEmptyEntries), b = right.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return a.Zip(b).All(pair => pair.First == "*" || pair.Second == "*" || pair.First == pair.Second);
    }
}
