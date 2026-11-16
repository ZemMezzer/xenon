using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public enum MirPointerLifetimeCheck { Use, Escape, DoubleFree }
public sealed record MirPointerLifetimeViolation(MirSourceInfo Source, MirReferenceOrigin Origin,
    MirPointerLifetimeCheck Check, string Name, bool Conditional);

/// <summary>
/// Invalidity is attached to reaching values before joins. This reuses the MIR
/// provenance lattice without imposing a validity obligation on unknown pointers.
/// Only a single exact destruction target invalidates aliases; may-alias is not
/// proof. Repeated allocation sites age previous instances to a finite summary.
/// </summary>
public sealed partial class MirPointerLifetimeAnalysis : IMirDataflowAnalysis<ImmutableDictionary<MirLocalId, MirReferenceValue>>
{
    private readonly MirFunction _function;
    private readonly MirReferenceOrigins _origins;
    private readonly Dictionary<MirLocalId, MirLocal> _locals;
    private readonly Dictionary<MirTerminator, ImmutableHashSet<MirReferenceOrigin>> _destructionTargets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MirStatement, ImmutableHashSet<MirReferenceOrigin>> _placementTargets = new(ReferenceEqualityComparer.Instance);
    private readonly MirControlFlow _graph;
    private readonly MirDataflowResult<ImmutableDictionary<MirLocalId, MirReferenceValue>> _provenance;
    private readonly MirPointerParameterGuard? _entryGuard;
    private readonly IReadOnlyDictionary<FunctionSymbol, ImmutableHashSet<MirLifetimeExit>>? _lifetimeExits;
    private readonly Dictionary<object, MirLocation> _locations = new(ReferenceEqualityComparer.Instance);
    public MirPointerLifetimeAnalysis(MirFunction function,
        IReadOnlyDictionary<FunctionSymbol, MirReferenceValue>? summaries = null, CancellationToken cancellation = default,
        IReadOnlyDictionary<FunctionSymbol, ImmutableHashSet<MirLifetimeEffect>>? destructionSummaries = null,
        bool symbolicParameters = false, MirPointerParameterGuard? entryGuard = null,
        IReadOnlyDictionary<FunctionSymbol, ImmutableHashSet<MirLifetimeExit>>? lifetimeExits = null)
    {
        _function = function;
        _entryGuard = entryGuard;
        _lifetimeExits = lifetimeExits;
        _origins = new(function, pointerLifetimes: true, pointerReturns: summaries, symbolicPointerParameters: symbolicParameters);
        _locals = function.Locals.ToDictionary(local => local.Id);
        foreach (var block in function.Blocks)
        {
            for (int index = 0; index < block.Statements.Length; index++) _locations[block.Statements[index]] = new(block.Id, index);
            _locations[block.Terminator] = new(block.Id, block.Statements.Length);
        }
        _graph = MirFlowFacts.Graph(function, cancellation, precisePointerFacts: true, entryGuard: entryGuard, lifetimeExits: lifetimeExits);
        // Must-target decisions come from a completed provenance fixed point.
        // Recomputing singleton-ness during the invalidity solve is non-monotone.
        var provenance = _provenance = MirDataflow.Solve(_graph, _origins, cancellation);
        foreach (var block in function.Blocks.Where(block => _graph.Reachable.Contains(block.Id)))
        {
            for (int index = 0; index < block.Statements.Length; index++)
                if (block.Statements[index] is MirAssign { WriteKind: MirWriteKind.RawPlacement } placement)
                    _placementTargets[placement] = _origins.Address(placement.Destination, provenance.Before[new(block.Id, index)]);
            var state = provenance.Output[block.Id];
            if (block.Terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Free, Arguments: [var pointer, ..] } free)
                _destructionTargets[free] = _origins.Operand(pointer, state).Roots;
            if (block.Terminator is MirDrop drop)
                _destructionTargets[drop] = PlaceType(drop.Place) is UniqueTypeSymbol
                    ? _origins.Read(drop.Place, state).Roots : _origins.Address(drop.Place, state);
        }
        CollectCallEffects(provenance, destructionSummaries);
    }
    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Bottom => _origins.Bottom;
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Boundary(MirControlFlow graph, MirBasicBlock block) => _origins.Boundary(graph, block);
    public ImmutableDictionary<MirLocalId, MirReferenceValue> Join(IEnumerable<ImmutableDictionary<MirLocalId, MirReferenceValue>> states) => _origins.Join(states);
    public bool Same(ImmutableDictionary<MirLocalId, MirReferenceValue> left, ImmutableDictionary<MirLocalId, MirReferenceValue> right) => _origins.Same(left, right);

    private static bool SameObject(MirReferenceOrigin left, MirReferenceOrigin right, bool storage = false) =>
        !left.PriorInstance && !right.PriorInstance && left.Kind == right.Kind && left.Ordinal == right.Ordinal &&
        left.AllocationSite == right.AllocationSite && left.AllocationContext == right.AllocationContext &&
        left.HandleIdentity == right.HandleIdentity && left.Kind is not (MirReferenceOriginKind.Unknown or MirReferenceOriginKind.Static) &&
        !right.Path.Contains('*') && (!left.Path.Contains('*') || storage && right.Path.Length == 0) &&
        (storage ? left.Path == right.Path || right.Path.Length == 0 || left.Path.StartsWith(right.Path + "/", StringComparison.Ordinal)
            : !left.UnknownObjectOffset && !right.UnknownObjectOffset && ContainsObject(left.Path, right.Path));

    private static bool ContainsObject(string left, string right)
    {
        // *p and p[0] designate the first object, not every object in p's allocation.
        static string FirstObject(string path) => path == "[0]" ? "" : path.StartsWith("[0]/", StringComparison.Ordinal) ? path[4..] : path;
        left = FirstObject(left);
        right = FirstObject(right);
        return left == right || (right.Length == 0 ? !left.StartsWith('[') : left.StartsWith(right + "/", StringComparison.Ordinal));
    }

    private static ImmutableDictionary<MirLocalId, MirReferenceValue> Transform(
        ImmutableDictionary<MirLocalId, MirReferenceValue> state, Func<MirReferenceOrigin, MirReferenceOrigin> transform)
    {
        var result = state.ToBuilder();
        foreach (var (key, value) in state)
        {
            var roots = Map(value.Roots);
            var fields = value.Fields.ToBuilder();
            foreach (var (path, field) in value.Fields) fields[path] = Map(field);
            var mappedFields = fields.ToImmutable();
            if (!ReferenceEquals(roots, value.Roots) || !ReferenceEquals(mappedFields, value.Fields))
                result[key] = value with { Roots = roots, Fields = mappedFields };
        }
        return result.ToImmutable();

        ImmutableHashSet<MirReferenceOrigin> Map(ImmutableHashSet<MirReferenceOrigin> roots)
        {
            var mapped = roots.ToBuilder();
            foreach (var root in roots)
            {
                var replacement = transform(root);
                if (replacement == root) continue;
                mapped.Remove(root);
                mapped.Add(replacement);
            }
            return mapped.ToImmutable();
        }
    }

    private static ImmutableDictionary<MirLocalId, MirReferenceValue> Destroy(
        ImmutableDictionary<MirLocalId, MirReferenceValue> state, IEnumerable<MirReferenceOrigin> roots, MirSourceInfo source, MirLocation location, bool freesStorage = true, bool automatic = false,
        MirPointerCallDeath? callDeath = null)
    {
        var targets = roots.Select(root => root with { DestroyedAt = null, DestroyedLocation = null, CallDeath = null, StorageFreed = false, AutomaticLifetimeEnd = false, PointeeType = null, IsReadonly = false,
            Authority = MirLifetimeAuthority.Owner }).Distinct().ToArray();
        if (targets.Length != 1 || targets[0].PriorInstance || targets[0].Path.Contains('*')) return state;
        var target = targets[0];
        return Transform(state, root => SameObject(root, target, storage: freesStorage) && (root.DestroyedAt is null || freesStorage && !root.StorageFreed)
            ? root with { DestroyedAt = source, DestroyedLocation = location, CallDeath = callDeath, StorageFreed = freesStorage, AutomaticLifetimeEnd = automatic || source.IsAutomaticCleanup } : root);
    }

    public ImmutableDictionary<MirLocalId, MirReferenceValue> Statement(MirStatement statement,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        if (statement is MirStorageDead dead)
            state = Destroy(state, [new(MirReferenceOriginKind.Local, dead.Local.Value)], dead.Source, _locations[statement], automatic: true);
        if (statement is MirStorageLive live)
            state = Transform(state, root => root.Kind == MirReferenceOriginKind.Local && root.Ordinal == live.Local.Value
                ? root with { PriorInstance = true } : root);
        if (statement is MirAssign { WriteKind: MirWriteKind.RawPlacement } placement)
        {
            // A placement into an imprecise target invalidates a death proof for
            // any possible object target. It never revives freed allocation bytes.
            var targets = _placementTargets.GetValueOrDefault(placement, []);
            state = Transform(state, root => !root.StorageFreed && targets.Any(target =>
                target.UnknownObjectOffset || target.Path.Contains('*')
                    ? SameObject(root with { Path = "" }, target with { Path = "" }, storage: true)
                    : SameObject(root, target))
                ? root with { DestroyedAt = null, DestroyedLocation = null, CallDeath = null, AutomaticLifetimeEnd = false } : root);
        }
        return _origins.Statement(statement, state);
    }

    public ImmutableDictionary<MirLocalId, MirReferenceValue> Terminator(MirTerminator terminator,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        if (terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Free, Arguments: [var pointer, ..] } free)
            return Destroy(state, _destructionTargets.GetValueOrDefault(free, []), free.Source, _locations[terminator]);
        if (terminator is MirDrop drop)
        {
            TypeSymbol type = PlaceType(drop.Place);
            if (type is UniqueTypeSymbol)
                return Destroy(state, _destructionTargets.GetValueOrDefault(drop, []), drop.Source, _locations[terminator], automatic: true);
            // A shared/weak drop does not prove that the last strong owner died.
            // storage<T> payload state is owned by MirMoveAnalysis. Ending its
            // payload does not destroy the still-live wrapper or allocation.
            if (type is not OwnershipTypeSymbol && drop.IsExplicit &&
                !drop.Place.Projections.Any(projection => projection is MirLifetimeProjection))
                return Destroy(state, _destructionTargets.GetValueOrDefault(drop, []), drop.Source, _locations[terminator], freesStorage: false);
        }
        return state;
    }

    public ImmutableDictionary<MirLocalId, MirReferenceValue> Edge(MirBasicBlock source, MirEdge edge,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        if (source.Terminator is MirCall effected && _callEffects.TryGetValue(effected, out var effects))
        {
            // Keep each guarded death as a separate reaching alternative. Mapping
            // was frozen before mutable-call clobber, and return aliases observe
            // the updated input when MirReferenceOrigins processes this edge.
            var alternatives = state;
            foreach (var effect in effects.Where(effect => effect.Effect.Phase == MirLifetimeEffectPhase.Exit && effect.Effect.InvalidatesBorrow && effect.Effect.Exit == edge.Kind))
                alternatives = Join([alternatives, Destroy(state, effect.Targets, effected.Source, _locations[effected],
                    effect.Effect.Storage, callDeath: effect.Death)]);
            foreach (var effect in effects.Where(effect => effect.Effect.Phase == MirLifetimeEffectPhase.Exit &&
                effect.Effect.Kind == MirLifetimeTransition.PlacementReconstruction && effect.Effect.Exit == edge.Kind && effect.Targets.Count == 1))
                alternatives = Join([alternatives, Transform(state, root => !root.StorageFreed && SameObject(root, effect.Targets.Single())
                    ? root with { DestroyedAt = null, DestroyedLocation = null, CallDeath = null, AutomaticLifetimeEnd = false } : root)]);
            state = alternatives;
        }
        if (edge.Kind == MirEdgeKind.Normal && source.Terminator is MirCall { Destination: { } callDestination })
            state = Transform(state, root => root.Ordinal == callDestination.Local.Value &&
                root.HandleIdentity == $"call:{callDestination.Local.Value}"
                ? root with { PriorInstance = true } : root);
        if (edge.Kind == MirEdgeKind.Normal && source.Terminator is MirIntrinsicCall
            { Intrinsic: MirIntrinsicKind.Allocate or MirIntrinsicKind.AllocateHeapArray or MirIntrinsicKind.AllocateStackArray or
                MirIntrinsicKind.Malloc or MirIntrinsicKind.Calloc or MirIntrinsicKind.AlignedMalloc, Destination: { } destination })
            state = Transform(state, root => root.Ordinal == destination.Local.Value &&
                (root.HandleIdentity == "allocation:" + destination.Local.Value || root.Kind == MirReferenceOriginKind.Local)
                ? root with { PriorInstance = true } : root);
        return _origins.Edge(source, edge, state);
    }

    private TypeSymbol PlaceType(MirPlace place)
    {
        TypeSymbol type = _locals[place.Local].Type;
        foreach (var projection in place.Projections)
            type = projection switch
            {
                MirDerefProjection when type is PointerTypeSymbol pointer => pointer.ElementType,
                MirDerefProjection when type is ReferenceTypeSymbol reference => reference.ElementType,
                MirDerefProjection when type is OwnershipTypeSymbol owner => owner.ElementType,
                MirFieldProjection field => field.Field.Type,
                MirLifetimeProjection when type is LifetimeModifierTypeSymbol modifier => modifier.ElementType,
                MirIndexProjection or MirLinearIndexProjection when type is ArrayTypeSymbol array => array.ElementType,
                MirIndexProjection or MirLinearIndexProjection when type is PointerTypeSymbol pointer => pointer.ElementType,
                _ => type,
            };
        return type;
    }

    public ImmutableArray<MirPointerLifetimeViolation> Check(CancellationToken cancellation = default)
    {
        var flow = MirDataflow.Solve(_graph, this, cancellation);
        var facts = new MirFlowFacts(precisePointerFacts: true, lifetimeExits: _lifetimeExits);
        MirDataflowResult<MirFlowState>? feasible = null;
        var witnesses = new Dictionary<(MirLocation, MirReferenceOrigin), ImmutableArray<MirDataflowResult<MirFlowState>>>();
        var definitions = _function.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>()
            .Where(assign => assign.Destination.Projections.IsEmpty).GroupBy(assign => assign.Destination.Local)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.First().Value);
        MirLocation currentLocation = default;
        var violations = new List<MirPointerLifetimeViolation>();
        foreach (var block in _function.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
        {
            cancellation.ThrowIfCancellationRequested();
            for (int index = 0; index < block.Statements.Length; index++)
            {
                if (block.Statements[index] is not MirAssign assign) continue;
                currentLocation = new(block.Id, index);
                var state = flow.Before[new(block.Id, index)];
                foreach (var place in MirOperands.Of(assign.Value).SelectMany(MirOperands.Places))
                    Access(place, state, assign.Source);
                Access(assign.Destination, state, assign.Source, placement: assign.WriteKind == MirWriteKind.RawPlacement);
                if (assign.Value is MirBorrow borrow && borrow.Kind != MirBorrowKind.Raw)
                    Access(borrow.Place, state, assign.Source);
            }
            var before = flow.Before[new(block.Id, block.Statements.Length)];
            currentLocation = new(block.Id, block.Statements.Length);
            if (block.Terminator is MirCall { Callee.Type: FunctionValueTypeSymbol } callable)
                CheckRoots(_origins.Operand(callable.Callee, before).Roots, callable.Source, MirPointerLifetimeCheck.Use, Name(callable.Callee));
            if (block.Terminator is MirCall destructive && _callEffects.TryGetValue(destructive, out var effects))
                foreach (var effect in effects.Where(effect => effect.Effect.InvalidatesBorrow))
                {
                    var roots = _origins.MapPointerEffect(effect.Argument, effect.Effect, before).Roots;
                    CheckRoots(roots.Where(root => !effect.Effect.Storage || root.DestroyedAt is null || root.StorageFreed),
                        destructive.Source, MirPointerLifetimeCheck.DoubleFree, effect.Argument is MirCopy { Place.Local.Value: < 0 } ? "captured pointer" : Name(effect.Argument), guard: effect.Death.Guard);
                }
            if (_function.Symbol.IsAsync && block.Terminator is MirCall { Arguments: [_, var completion] } resolved &&
                (resolved.Callee is MirFunctionOperand { Function.OperatorKind: OperatorKind.Resolve } or
                    MirRequirementOperand { Requirement: FunctionSymbol { OperatorKind: OperatorKind.Resolve } }) && CarriesPointer(completion.Type))
            {
                var retained = _origins.Operand(completion, before);
                CheckRoots(retained.Roots.Concat(retained.Fields.Values.SelectMany(roots => roots)), resolved.Source,
                    MirPointerLifetimeCheck.Escape, Name(completion), escape: true);
            }
            if (block.Terminator is MirReturn or MirResumeUnwind)
                foreach (var stored in _origins.ExternalValues(before))
                    CheckRoots(stored.Roots.Where(root => root.AutomaticLifetimeEnd || root.Kind == MirReferenceOriginKind.Local), block.Terminator.Source,
                        MirPointerLifetimeCheck.Escape, "externally stored pointer", escape: true);
            foreach (var place in MirOperands.Of(block.Terminator).SelectMany(MirOperands.Places)) Access(place, before, block.Terminator.Source);
            switch (block.Terminator)
            {
                case MirCall { Receiver: { } receiver } call:
                    CheckRoots(_origins.Operand(receiver, before).Roots, call.Source, MirPointerLifetimeCheck.Use, Name(receiver));
                    break;
                case MirDrop drop:
                    CheckRoots(_origins.Address(drop.Place, before), drop.Source, MirPointerLifetimeCheck.DoubleFree, Friendly(drop.Place.Local));
                    break;
                case MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Free, Arguments: [var pointer, ..] } free:
                    CheckRoots(_origins.Operand(pointer, before).Roots.Where(root => root.DestroyedAt is null || root.StorageFreed), free.Source, MirPointerLifetimeCheck.DoubleFree, Name(pointer));
                    break;
                case MirReturn { Value: { } returned } ret when returned.Type is not ReferenceTypeSymbol && CarriesPointer(returned.Type):
                    var value = _origins.Operand(returned, before);
                    CheckRoots(value.Roots.Where(root => returned.Type is not ArrayTypeSymbol || root.Kind != MirReferenceOriginKind.Local)
                        .Concat(value.Fields.Values.SelectMany(roots => roots)), ret.Source, MirPointerLifetimeCheck.Escape, Name(returned), escape: true);
                    break;
            }
        }
        return violations.DistinctBy(v => (v.Source.Location, v.Check, v.Origin.Ordinal, v.Origin.HandleIdentity)).ToImmutableArray();

        string Name(MirOperand operand) => MirOperands.Places(operand).Select(place => Friendly(place.Local)).FirstOrDefault() ?? "pointer";
        string Friendly(MirLocalId id)
        {
            var seen = new HashSet<MirLocalId>();
            while (_locals[id].Kind == MirLocalKind.Temporary && seen.Add(id) && definitions.TryGetValue(id, out var value))
            {
                var source = value switch { MirUse use => use.Operand, MirCast cast => cast.Operand, _ => null };
                if (source is not MirCopy copy) break;
                id = copy.Place.Local;
            }
            return _locals[id].Variable?.Name ?? "pointer";
        }
        void Access(MirPlace place, ImmutableDictionary<MirLocalId, MirReferenceValue> state, MirSourceInfo source, bool placement = false)
        {
            var prefix = new MirPlace(place.Local);
            foreach (var projection in place.Projections)
            {
                if (projection is MirDerefProjection || projection is MirIndexProjection or MirLinearIndexProjection && PlaceType(prefix) is PointerTypeSymbol or ArrayTypeSymbol)
                    CheckRoots(_origins.Read(prefix, state).Roots.Select(root => projection is MirIndexProjection or MirLinearIndexProjection ? _origins.Index(root, projection) : root)
                        .Where(root => !placement || !ReferenceEquals(projection, place.Projections[^1]) ||
                        root.DestroyedAt is null || root.StorageFreed), source, MirPointerLifetimeCheck.Use, Friendly(place.Local));
                prefix = prefix.Project(projection);
            }
        }
        void CheckRoots(IEnumerable<MirReferenceOrigin> roots, MirSourceInfo source, MirPointerLifetimeCheck check, string name, bool escape = false, MirPointerCondition? guard = null)
        {
            var all = roots.ToArray();
            foreach (var root in all.Where(root => root.DestroyedAt is not null || escape && root.Kind == MirReferenceOriginKind.Local))
            {
                if (root.DestroyedLocation is { } death && !Reaches(death, currentLocation, root, guard)) continue;
                violations.Add(new(source, root, check, name, all.Any(other => other.DestroyedAt is null && (!escape || other.Kind != MirReferenceOriginKind.Local))));
            }
        }
        bool Reaches(MirLocation death, MirLocation use, MirReferenceOrigin instance, MirPointerCondition? guard)
        {
            var callDeath = instance.CallDeath;
            if (callDeath is null && guard is null && death.Block == use.Block && death.Statement < use.Statement) return true;
            if (!witnesses.TryGetValue((death, instance), out var alternatives))
            {
                var block = _graph.Blocks[death.Block];
                var entry = new MirBlockId(_function.Blocks.Max(block => block.Id.Value) + 1);
                var tail = new MirBasicBlock(entry, [.. block.Statements.Skip(death.Statement + 1)], block.Terminator);
                // Statement-level witness facts are needed only for an actual
                // dead-pointer candidate, not every valid function.
                feasible ??= MirDataflow.Solve(_graph, facts, cancellation);
                var seed = feasible.After[death];
                MirEdge? initialEdge = null;
                if (callDeath is not null)
                {
                    seed = Assume(facts, seed, callDeath.Guard);
                    var edge = block.Terminator.Successors.First(edge => edge.Kind == callDeath.Exit);
                    initialEdge = edge;
                    tail = new(entry, [], new MirGoto(edge.Target, block.Terminator.Source));
                }
                var afterDeath = _function with { Entry = entry, Blocks = _function.Blocks.Add(tail) };
                List<ImmutableArray<(MirCall Call, MirEdgeKind Exit, MirPointerCondition? Exclude)>> policies = [[]];
                if (!instance.StorageFreed)
                {
                    // Missing or incomplete contracts cannot establish absence
                    // of reconstruction. Forget an earlier object-death proof
                    // across such a call on the same storage; never revive freed
                    // allocation storage or erase this call's own final proof.
                    foreach (var node in _function.Blocks)
                        if (node.Terminator is MirCall opaque && !ReferenceEquals(opaque, callDeath is null ? null : block.Terminator) &&
                            (DirectCallee(opaque)?.MirLifetimeSummary is not { Complete: true }) &&
                            opaque.Arguments.Where(argument => CarriesPointer(argument.Type)).Any(argument =>
                            {
                                var value = _origins.Operand(argument, flow.Output[node.Id]);
                                return value.Roots.Concat(value.Fields.Values.SelectMany(roots => roots)).Any(target =>
                                    instance.PriorInstance == target.PriorInstance &&
                                    SameObject(instance with { Path = "", PriorInstance = false }, target with { Path = "", PriorInstance = false }, storage: true));
                            }))
                            policies[0] = policies[0].Add((opaque, MirEdgeKind.Normal, null)).Add((opaque, MirEdgeKind.Unwind, null));
                    foreach (var (call, effects) in _callEffects.OrderBy(pair => _locations[pair.Key].Block.Value))
                        foreach (var effect in effects.Where(effect => effect.Effect.Kind == MirLifetimeTransition.PlacementReconstruction &&
                            (effect.Effect.Phase == MirLifetimeEffectPhase.Exit && effect.Targets.Count == 1 && SameObject(instance, effect.Targets.Single()) ||
                            effect.Effect.Phase == MirLifetimeEffectPhase.Operation && !ReferenceEquals(call, callDeath is null ? null : block.Terminator) &&
                                _origins.MapPointerEffect(effect.Argument, effect.Effect, flow.Output[_locations[call].Block]).Roots
                                .Any(target => instance.PriorInstance == target.PriorInstance && SameObject(instance with { Path = "", PriorInstance = false },
                                    target with { Path = "", PriorInstance = false }, storage: true))))
                            .OrderBy(effect => EffectKey(effect.Effect), StringComparer.Ordinal))
                        {
                            var choices = new List<MirPointerCondition?>();
                            for (var condition = effect.Death.Guard; condition is not null; condition = condition.Next)
                                choices.Add(condition with { Value = !condition.Value, Next = null });
                            if (choices.Count == 0) choices.Add(null);
                            if (policies.Count * choices.Count > 64 && _function.Symbol.MirLifetimeSummary is { } summary)
                                _function.Symbol.MirLifetimeSummary = summary with { IncompleteReasons = summary.IncompleteReasons.Add("restoration-witness-budget") };
                            policies = policies.SelectMany(policy => choices.Select(choice => effect.Effect.Phase == MirLifetimeEffectPhase.Operation
                                ? policy.Add((call, MirEdgeKind.Normal, choice)).Add((call, MirEdgeKind.Unwind, choice))
                                : policy.Add((call, effect.Effect.Exit, choice)))).Take(64).ToList();
                        }
                }
                var solved = ImmutableArray.CreateBuilder<MirDataflowResult<MirFlowState>>();
                foreach (var policy in policies)
                {
                    MirFlowState Restrict(MirBasicBlock source, MirEdge edge, MirFlowState state)
                    {
                        foreach (var exclusion in policy)
                            if (ReferenceEquals(source.Terminator, exclusion.Call) && edge.Kind == exclusion.Exit)
                                state = exclusion.Exclude is null ? facts.Bottom : Assume(facts, state, exclusion.Exclude);
                        return state;
                    }
                    var initial = seed;
                    if (initialEdge is { } edge)
                    {
                        initial = facts.Edge(block, edge, Restrict(block, edge, initial));
                        if (callDeath?.ExceptionType is { } error) initial = initial with { Pending = [error] };
                        if (callDeath?.Result is { } returned && block.Terminator is MirCall { Destination: { } destination })
                            initial = facts.Statement(new MirAssign(destination, new MirUse(returned), block.Terminator.Source), initial);
                    }
                    solved.Add(MirDataflow.Solve(new MirControlFlow(afterDeath),
                        new DeathWitness(facts, initial, entry, _function, instance, Restrict), cancellation));
                }
                alternatives = solved.ToImmutable();
                witnesses.Add((death, instance), alternatives);
            }
            return alternatives.Any(witness => witness.Before.TryGetValue(use, out var state) && Assume(facts, state, guard).Reachable);
        }
    }

    public static bool CarriesPointer(TypeSymbol type) => CarriesPointer(type, []);
    private static bool CarriesPointer(TypeSymbol type, HashSet<TypeSymbol> seen) =>
        type is PointerTypeSymbol or ArrayTypeSymbol or OwnershipTypeSymbol or FunctionValueTypeSymbol ||
        type is LifetimeModifierTypeSymbol modifier && CarriesPointer(modifier.ElementType, seen) ||
        type is AtomicTypeSymbol atomic && CarriesPointer(atomic.ElementType, seen) ||
        type is IFieldStorageTypeSymbol structure && seen.Add(type) && structure.AllInstanceFields.Any(field => CarriesPointer(field.Type, seen));

    // Validate each death witness with its actual cleanup selector facts. A global
    // graph join must not combine a death under flag=false with an unrelated
    // destructor unwind under flag=true. Cached per site, no path enumeration.
    private sealed class DeathWitness(MirFlowFacts facts, MirFlowState seed, MirBlockId entry,
        MirFunction? function = null, MirReferenceOrigin? instance = null,
        Func<MirBasicBlock, MirEdge, MirFlowState, MirFlowState>? restrict = null) : IMirDataflowAnalysis<MirFlowState>
    {
        public MirDataflowDirection Direction => MirDataflowDirection.Forward;
        public MirFlowState Bottom => facts.Bottom;
        public MirFlowState Boundary(MirControlFlow graph, MirBasicBlock block) => block.Id == entry ? seed : Bottom;
        public MirFlowState Join(IEnumerable<MirFlowState> states) => facts.Join(states);
        public bool Same(MirFlowState left, MirFlowState right) => facts.Same(left, right);
        public MirFlowState Statement(MirStatement statement, MirFlowState state) =>
            instance is { PriorInstance: false, Kind: MirReferenceOriginKind.Local } &&
            statement is MirStorageLive live && live.Local.Value == instance.Ordinal ? Bottom : facts.Statement(statement, state);
        public MirFlowState Terminator(MirTerminator terminator, MirFlowState state) => facts.Terminator(terminator, state);
        public MirFlowState Edge(MirBasicBlock source, MirEdge edge, MirFlowState state)
        {
            // A current-instance witness cannot cross a fresh execution of its
            // allocation site. Retained older aliases explicitly carry PriorInstance
            // and keep their death proof across subsequent loop iterations.
            if (instance is { PriorInstance: false } && edge.Kind == MirEdgeKind.Normal && function is not null)
            {
                if (source.Terminator is MirCall { Destination: { } callResult } &&
                    instance.AllocationContext?.CallSite == new MirAllocationSite(function.Symbol, callResult.Local)) return Bottom;
                if (source.Terminator is MirIntrinsicCall { Destination: { } allocated,
                    Intrinsic: MirIntrinsicKind.Allocate or MirIntrinsicKind.AllocateHeapArray or MirIntrinsicKind.AllocateStackArray or
                        MirIntrinsicKind.Malloc or MirIntrinsicKind.Calloc or MirIntrinsicKind.AlignedMalloc } &&
                    instance.AllocationSite == new MirAllocationSite(function.Symbol, allocated.Local)) return Bottom;
            }
            return facts.Edge(source, edge, restrict is null ? state : restrict(source, edge, state));
        }
    }
}
