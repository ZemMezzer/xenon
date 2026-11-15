using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirPointerCondition(MirOperand Operand, bool Value, MirPointerCondition? Next = null,
    MirBinaryOperator? Operator = null, object? Constant = null);
public sealed record MirPointerParameterGuard(int Parameter, bool Value, MirPointerParameterGuard? Next = null,
    MirBinaryOperator? Operator = null, object? Constant = null);
public sealed record MirPointerCallDeath(MirPointerCondition? Guard, MirEdgeKind Exit, MirSourceInfo Source, MirConstant? Result = null, TypeSymbol? ExceptionType = null);
internal sealed record MirLifetimeBudgets(int Contexts = 256, int Effects = 4096, int ComponentRounds = 128);

public sealed partial class MirPointerLifetimeAnalysis
{
    private sealed record CallEffect(MirLifetimeEffect Effect, ImmutableHashSet<MirReferenceOrigin> Targets, MirPointerCallDeath Death, MirOperand Argument);
    private readonly Dictionary<MirCall, ImmutableArray<CallEffect>> _callEffects = new(ReferenceEqualityComparer.Instance);
    private bool _needsGuardRefinement;
    private bool _hasOpaqueEffects;
    private readonly HashSet<MirLifetimeUncertainty> _uncertainties = [];
    private readonly HashSet<MirLifetimeExit> _summaryExits = [];
    internal static FunctionSymbol? DirectCallee(MirCall call) => call is { IsVirtual: false, InterfaceType: null } &&
        call.Callee is MirFunctionOperand { Function: { IsVirtual: false, IsOverride: false } function } ? function : null;

    private static MirFlowState Assume(MirFlowFacts facts, MirFlowState state, MirPointerCondition? guard)
    {
        for (; guard is not null && state.Reachable; guard = guard.Next)
            state = guard.Operator is { } op ? facts.AssumeComparison(guard.Operand, op, guard.Constant!, guard.Value, state) : facts.Assume(guard.Operand, guard.Value, state);
        return state;
    }

    private void CollectCallEffects(MirDataflowResult<ImmutableDictionary<MirLocalId, MirReferenceValue>> provenance,
        IReadOnlyDictionary<FunctionSymbol, ImmutableHashSet<MirLifetimeEffect>>? summaries)
    {
        if (summaries is null) return;
        foreach (var block in _function.Blocks.Where(block => _graph.Reachable.Contains(block.Id)))
        {
            if (block.Terminator is not MirCall originalCall) continue;
            foreach (var call in _origins.BoundCalls(originalCall, provenance.Output[block.Id]))
            {
            if (DirectCallee(call) is not { } callee || !summaries.TryGetValue(callee, out var effects))
            {
                var opaque = ImmutableArray.CreateBuilder<CallEffect>();
                for (int index = 0; index < call.Arguments.Length; index++)
                    if (call.Arguments[index].Type is PointerTypeSymbol { IsReadonly: false })
                    {
                        var targets = _origins.Operand(call.Arguments[index], provenance.Output[block.Id]).Roots;
                        _hasOpaqueEffects |= targets.Any(root => root.HandleParameter is not null || root.Kind == MirReferenceOriginKind.Parameter);
                        opaque.Add(new(new(index, "", false, false, null, MirEdgeKind.Normal, call.Source,
                            Phase: MirLifetimeEffectPhase.Operation, Transition: MirLifetimeTransition.PlacementReconstruction),
                            targets, new(null, MirEdgeKind.Normal, call.Source), call.Arguments[index]));
                    }
                if (opaque.Count != 0) _callEffects[originalCall] = _callEffects.GetValueOrDefault(originalCall, []).AddRange(opaque);
                continue;
            }
            var mapped = ImmutableArray.CreateBuilder<CallEffect>();
            foreach (var effect in effects)
            {
                if (effect.Parameter < 0 || effect.Parameter >= call.Arguments.Length) continue;
                MirPointerCondition? guard = null;
                for (var condition = effect.Guard; condition is not null; condition = condition.Next)
                    guard = new(call.Arguments[condition.Parameter], condition.Value, guard, condition.Operator, condition.Constant);
                var targets = _origins.MapPointerEffect(call.Arguments[effect.Parameter], effect, provenance.Output[block.Id]).Roots;
                mapped.Add(new(effect, targets, new(guard, effect.Exit, effect.Source, effect.Result, effect.ExceptionType), call.Arguments[effect.Parameter]));
            }
            _callEffects[originalCall] = _callEffects.GetValueOrDefault(originalCall, []).AddRange(mapped);
            }
        }
    }

    // Summaries describe witnessed exits, not merely a reachable free. A free
    // followed only by a throw cannot invalidate the caller's normal successor.
    internal ImmutableHashSet<MirLifetimeEffect> SummarizeEffects(CancellationToken cancellation,
        IReadOnlyDictionary<FunctionSymbol, MirLifetimeSummary>? summaries = null, bool shareStorageExitProof = true)
    {
        using var profile = MirAnalysisProfile.Start("SummarizeEffects", _function);
        var facts = new MirFlowFacts(precisePointerFacts: true, entryGuard: _entryGuard, lifetimeExits: _lifetimeExits);
        var feasible = MirDataflow.Solve(_graph, facts, cancellation);
        // One structural proof serves every storage-death event in this function.
        // An acyclic body whose feasible continuations all return void cannot
        // change an established free into a different exit/result. Entry guards
        // still come from each event's own supported-path proof. Object deaths,
        // loops, calls, unwind and value-returning bodies retain DeathWitness.
        bool normalVoidExit = shareStorageExitProof && HasOnlyFiniteVoidExits();
        // Edge feasibility depends only on the frozen flow result. All targets
        // in this summary share it; do not rebuild immutable predicate maps for
        // each destruction candidate during reverse reachability.
        var feasibleEdges = new Dictionary<MirBlockId, MirEdge[]>();
        var parameters = _function.Locals.Where(local => local.Variable is ParameterSymbol &&
            (TypeIdentity.AreSame(local.Type, BuiltinTypes.Bool) || TypeFacts.IsInteger(local.Type) || TypeFacts.IsCharacter(local.Type)))
            .ToDictionary(local => local.Id, local => ((ParameterSymbol)local.Variable!).Ordinal);
        var writes = _function.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>().ToArray();
        foreach (var local in writes.Select(assign => assign.Destination.Local).Concat(writes.Select(assign => assign.Value).OfType<MirBorrow>().Select(borrow => borrow.Place.Local)))
            parameters.Remove(local);
        var result = ImmutableHashSet.CreateBuilder<MirLifetimeEffect>();
        foreach (var block in _function.Blocks.Where(block => _graph.Reachable.Contains(block.Id)))
        {
            cancellation.ThrowIfCancellationRequested();
            for (int index = 0; index < block.Statements.Length; index++)
            {
                if (block.Statements[index] is MirAssign placement && MirLifetimeOperations.Of(placement) == MirLifetimeTransition.PlacementReconstruction &&
                    _placementTargets.TryGetValue(placement, out var destinations))
                    Placement(block, index, placement.Source, destinations);
                if (block.Statements[index] is MirAssign read && !read.IsMoveRead &&
                    (read.IsSemanticRead || read.Value is MirBorrow { Kind: MirBorrowKind.Shared }))
                {
                    var place = read.Value is MirBorrow borrow ? borrow.Place :
                        MirOperands.Of(read.Value).SelectMany(MirOperands.Places).FirstOrDefault();
                    if (place is not null) ReadOperation(block, index, read.Source,
                        _origins.Address(place, _provenance.Before[new(block.Id, index)]));
                }
            }
            var location = new MirLocation(block.Id, block.Statements.Length);
            if (block.Terminator is MirCall uncertainCall && summaries is not null)
            {
                if (DirectCallee(uncertainCall) is null &&
                    _origins.Operand(uncertainCall.Callee, _provenance.Output[block.Id]).CallableTargets.Count == 0)
                    Uncertain(_origins.Operand(uncertainCall.Callee, _provenance.Output[block.Id]).Roots,
                        feasible.Output[block.Id], MirLifetimeTransition.BorrowInvalidation, "callable-invocation");
                foreach (var boundCall in _origins.BoundCalls(uncertainCall, _provenance.Output[block.Id]))
                {
                MirLifetimeSummary? contract = null;
                if (DirectCallee(boundCall) is { } dependency) summaries.TryGetValue(dependency, out contract);
                foreach (var uncertainty in MirLifetimeOperations.Coverage(boundCall, contract))
                {
                    MirPointerCondition? guard = null;
                    for (var item = uncertainty.Guard; item is not null; item = item.Next)
                        guard = new(boundCall.Arguments[item.Parameter], item.Value, guard, item.Operator, item.Constant);
                    var seed = Assume(facts, feasible.Output[block.Id], guard);
                    if (!seed.Reachable) continue;
                    var uncertainValue = _origins.MapPointerParameter(boundCall.Arguments[uncertainty.Parameter], uncertainty.Path, _provenance.Output[block.Id]);
                    if (uncertainValue.CallableTargets.GetValueOrDefault("", []).Count != 0)
                        ExpandCallable(uncertainValue, boundCall.Arguments[uncertainty.Parameter], []);
                    else Uncertain(uncertainValue.Roots, seed, uncertainty.Transition, uncertainty.Reason, uncertainty.UnknownOffset);
                }
                }
            }
            if (_destructionTargets.TryGetValue(block.Terminator, out var targets))
            {
                bool storage = block.Terminator is MirIntrinsicCall || block.Terminator is MirDrop drop && PlaceType(drop.Place) is UniqueTypeSymbol;
                if (block.Terminator is not MirDrop dropped || storage || dropped.IsExplicit && PlaceType(dropped.Place) is not OwnershipTypeSymbol)
                    Add(targets, storage, block.Terminator.Source, null, null);
            }
            if (block.Terminator is MirCall call && _callEffects.TryGetValue(call, out var effects))
            {
                foreach (var effect in effects.Where(effect => effect.Effect.InvalidatesBorrow)) Add(effect.Targets, effect.Effect.Storage, effect.Effect.Source,
                    effect.Death.Guard, effect.Effect.Exit, effect.Effect.Result, effect.Effect.ExceptionType, effect.Effect.Phase == MirLifetimeEffectPhase.Operation);
                foreach (var effect in effects.Where(effect => effect.Effect.Kind == MirLifetimeTransition.PlacementReconstruction))
                    Placement(block, block.Statements.Length, effect.Effect.Source, effect.Targets, effect);
                foreach (var effect in effects.Where(effect => effect.Effect.Kind == MirLifetimeTransition.StorageRead))
                    ReadOperation(block, block.Statements.Length, effect.Effect.Source, effect.Targets, effect);
            }

            void ExpandCallable(MirReferenceValue callable, MirOperand operand, HashSet<FunctionSymbol> visiting)
            {
                var state = _provenance.Output[block.Id];
                var context = _origins.DeferredInvocation((MirCall)block.Terminator, operand);
                foreach (var bound in _origins.BoundCalls(context, state, callable))
                {
                    var callee = DirectCallee(bound);
                    if (callee is null || !visiting.Add(callee))
                    {
                        Uncertain(_origins.CallablePayload(callable, state), feasible.Output[block.Id],
                            MirLifetimeTransition.BorrowInvalidation, "recursive-callable");
                        continue;
                    }
                    MirLifetimeSummary? contract = null;
                    summaries?.TryGetValue(callee, out contract);
                    foreach (var effect in contract?.Effects.Where(effect => effect.Phase == MirLifetimeEffectPhase.Operation) ?? [])
                    {
                        MirPointerCondition? guard = null;
                        for (var item = effect.Guard; item is not null; item = item.Next)
                            guard = new(bound.Arguments[item.Parameter], item.Value, guard, item.Operator, item.Constant);
                        var targets = _origins.MapPointerEffect(bound.Arguments[effect.Parameter], effect, state).Roots;
                        var mapped = new CallEffect(effect, targets, new(guard, effect.Exit, effect.Source), bound.Arguments[effect.Parameter]);
                        if (effect.InvalidatesBorrow) Add(targets, effect.Storage, effect.Source, guard, null, operationOnly: true);
                        else if (effect.Kind == MirLifetimeTransition.PlacementReconstruction) Placement(block, block.Statements.Length, effect.Source, targets, mapped);
                        else if (effect.Kind == MirLifetimeTransition.StorageRead) ReadOperation(block, block.Statements.Length, effect.Source, targets, mapped);
                    }
                    foreach (var uncertainty in MirLifetimeOperations.Coverage(bound, contract))
                    {
                        MirPointerCondition? guard = null;
                        for (var item = uncertainty.Guard; item is not null; item = item.Next)
                            guard = new(bound.Arguments[item.Parameter], item.Value, guard, item.Operator, item.Constant);
                        var seed = Assume(facts, feasible.Output[block.Id], guard);
                        if (!seed.Reachable) continue;
                        var value = _origins.MapPointerParameter(bound.Arguments[uncertainty.Parameter], uncertainty.Path, state);
                        if (value.CallableTargets.GetValueOrDefault("", []).Count != 0)
                            ExpandCallable(value, bound.Arguments[uncertainty.Parameter], visiting);
                        else Uncertain(value.Roots, seed, uncertainty.Transition, uncertainty.Reason, uncertainty.UnknownOffset);
                    }
                    visiting.Remove(callee);
                }
            }

            void Add(ImmutableHashSet<MirReferenceOrigin> roots, bool storage, MirSourceInfo source, MirPointerCondition? guard, MirEdgeKind? exit,
                MirConstant? resultValue = null, TypeSymbol? exceptionType = null, bool operationOnly = false)
            {
                var seed = Assume(facts, feasible.After[location], guard);
                if (!seed.Reachable) return;
                if (roots.Count != 1)
                {
                    _needsGuardRefinement |= roots.Count > 1;
                    Uncertain(roots, seed, storage ? MirLifetimeTransition.StorageDeallocation : MirLifetimeTransition.ObjectDestruction);
                    return;
                }
                var root = roots.Single();
                int? parameter = root.HandleParameter ?? (root.Kind == MirReferenceOriginKind.Parameter ? root.Ordinal : null);
                if (parameter is null || root.PriorInstance || root.Path.Contains('*') || !storage && root.UnknownObjectOffset)
                {
                    _needsGuardRefinement |= parameter is not null || root.Kind == MirReferenceOriginKind.Unknown;
                    Uncertain(roots, seed, storage ? MirLifetimeTransition.StorageDeallocation : MirLifetimeTransition.ObjectDestruction);
                    return;
                }
                if (!SupportedPath(_graph, feasible, block.Id, seed, root))
                {
                    _needsGuardRefinement = true;
                    Uncertain(roots, seed, storage ? MirLifetimeTransition.StorageDeallocation : MirLifetimeTransition.ObjectDestruction);
                    return;
                }
                result.Add(new(parameter.Value, root.Path, root.UnknownObjectOffset, storage, Conditions(seed), MirEdgeKind.Normal, source,
                    Phase: MirLifetimeEffectPhase.Operation));
                if (operationOnly) return;
                if (storage && normalVoidExit)
                {
                    result.Add(new(parameter.Value, root.Path, root.UnknownObjectOffset, true, Conditions(seed), MirEdgeKind.Normal, source));
                    return;
                }
                var entry = new MirBlockId(_function.Blocks.Max(b => b.Id.Value) + 1);
                MirTerminator tail = block.Terminator;
                if (exit is { } kind)
                {
                    var edge = block.Terminator.Successors.First(edge => edge.Kind == kind);
                    seed = facts.Edge(block, edge, seed);
                    if (exceptionType is not null) seed = seed with { Pending = [exceptionType] };
                    if (resultValue is { } returned && block.Terminator is MirCall { Destination: { } destination })
                        seed = facts.Statement(new MirAssign(destination, new MirUse(returned), block.Terminator.Source), seed);
                    tail = new MirGoto(edge.Target, block.Terminator.Source);
                }
                var function = _function with { Entry = entry, Blocks = _function.Blocks.Add(new(entry, [], tail)) };
                var graph = new MirControlFlow(function);
                var witness = MirDataflow.Solve(graph, new DeathWitness(facts, seed, entry), cancellation,
                    captureLocations: !storage && (_placementTargets.Count != 0 || _callEffects.Count != 0));
                // Placement can restore object lifetime. Until summaries describe
                // its guarded ordering too, a possible restoration invalidates an
                // object-end proof, while it never restores released storage.
                if (!storage && _placementTargets.Any(placement =>
                    witness.Before.TryGetValue(_locations[placement.Key], out var point) && point.Reachable &&
                    placement.Value.Any(target => target.Kind == MirReferenceOriginKind.Unknown ||
                        SameObject(root, target) || (target.UnknownObjectOffset || target.Path.Contains('*')) &&
                        SameObject(root with { Path = "" }, target with { Path = "" }, storage: true))))
                {
                    _needsGuardRefinement = true;
                    return;
                }
                if (!storage && _callEffects.Any(pair => witness.Before.TryGetValue(_locations[pair.Key], out var point) && point.Reachable &&
                    pair.Value.Any(effect => effect.Effect.Kind == MirLifetimeTransition.PlacementReconstruction &&
                        Assume(facts, point, effect.Death.Guard).Reachable && effect.Targets.Any(target => SameObject(root, target)))))
                { _needsGuardRefinement = true; return; }
                foreach (var end in graph.Function.Blocks.Where(b => b.Terminator is MirReturn or MirResumeUnwind))
                {
                    var state = witness.Output[end.Id];
                    if (!state.Reachable || !SupportedPath(graph, witness, end.Id, state, root)) continue;
                    var conditions = Conditions(state);
                    if (end.Terminator is MirReturn returned)
                        result.Add(new(parameter.Value, root.Path, root.UnknownObjectOffset, storage, conditions, MirEdgeKind.Normal, source,
                            returned.Value is { } returnedValue ? MirFlowFacts.KnownConstant(returnedValue, state) : null));
                    else foreach (var error in state.Pending.DefaultIfEmpty(BuiltinTypes.Error))
                        result.Add(new(parameter.Value, root.Path, root.UnknownObjectOffset, storage, conditions, MirEdgeKind.Unwind, source,
                            ExceptionType: TypeIdentity.AreSame(error, BuiltinTypes.Error) ? null : error));
                }
            }
        }
        foreach (var block in _function.Blocks.Where(block => feasible.Output[block.Id].Reachable))
        {
            var state = feasible.Output[block.Id];
            if (block.Terminator is MirReturn returned)
                _summaryExits.Add(new(Conditions(state), MirEdgeKind.Normal,
                    returned.Value is { } operand ? MirFlowFacts.KnownConstant(operand, state) : null));
            else if (block.Terminator is MirResumeUnwind)
                foreach (var error in state.Pending.DefaultIfEmpty(BuiltinTypes.Error))
                    _summaryExits.Add(new(Conditions(state), MirEdgeKind.Unwind, ExceptionType: TypeIdentity.AreSame(error, BuiltinTypes.Error) ? null : error));
        }
        return result.ToImmutable();

        bool HasOnlyFiniteVoidExits()
        {
            var nodes = _function.Blocks.Where(node => feasible.Output[node.Id].Reachable).ToDictionary(node => node.Id);
            if (nodes.Count == 0 || nodes.Values.Count(node => node.Terminator is MirReturn { Value: null }) != 1) return false;
            var incoming = nodes.Keys.ToDictionary(id => id, _ => 0);
            foreach (var node in nodes.Values)
            {
                if (node.Terminator is not (MirReturn { Value: null } or MirGoto or MirSwitch or MirDrop or
                    MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Free })) return false;
                if (node.Terminator is not MirReturn && !_graph.Successors[node.Id].Any(edge => nodes.ContainsKey(edge.Target))) return false;
                foreach (var edge in _graph.Successors[node.Id])
                {
                    if (!nodes.ContainsKey(edge.Target)) continue;
                    if (edge.Kind != MirEdgeKind.Normal) return false;
                    incoming[edge.Target]++;
                }
            }
            var ready = new Queue<MirBlockId>(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
            int visited = 0;
            while (ready.TryDequeue(out var id))
            {
                cancellation.ThrowIfCancellationRequested();
                visited++;
                foreach (var edge in _graph.Successors[id])
                    if (incoming.ContainsKey(edge.Target) && --incoming[edge.Target] == 0) ready.Enqueue(edge.Target);
            }
            return visited == nodes.Count;
        }

        void Uncertain(IEnumerable<MirReferenceOrigin> roots, MirFlowState state, MirLifetimeTransition transition,
            string reason = "unresolved-target", bool unknownOffset = false)
        {
            foreach (var root in roots)
                if ((root.HandleParameter ?? (root.Kind == MirReferenceOriginKind.Parameter ? root.Ordinal : null)) is { } parameter)
                    _uncertainties.Add(new(parameter, root.Path, root.UnknownObjectOffset || unknownOffset, Conditions(state), transition, reason));
                else if (root.Kind == MirReferenceOriginKind.Unknown)
                    foreach (var fallback in MirLifetimeOperations.Coverage(_function.Symbol, null))
                        _uncertainties.Add(fallback with { Guard = Conditions(state), Reason = reason, Transition = transition });
        }

        void ReadOperation(MirBasicBlock block, int index, MirSourceInfo source, ImmutableHashSet<MirReferenceOrigin> roots, CallEffect? callEffect = null)
        {
            var possible = Assume(facts, feasible.After[new(block.Id, index)], callEffect?.Death.Guard);
            if (!possible.Reachable) return;
            foreach (var root in roots)
                if ((root.HandleParameter ?? (root.Kind == MirReferenceOriginKind.Parameter ? root.Ordinal : null)) is { } ordinal)
                    result.Add(new(ordinal, root.Path, root.UnknownObjectOffset, false, Conditions(possible), MirEdgeKind.Normal, source,
                        Phase: MirLifetimeEffectPhase.Operation, Transition: MirLifetimeTransition.StorageRead));
                else if (root.Kind == MirReferenceOriginKind.Unknown)
                    Uncertain([root], possible, MirLifetimeTransition.StorageRead);
        }

        void Placement(MirBasicBlock block, int index, MirSourceInfo source, ImmutableHashSet<MirReferenceOrigin> roots, CallEffect? callEffect = null)
        {
            // Possible writes invalidate strict loans and prevent a stale raw
            // death proof. Only an exact target may export an exit-state rebuild.
            var possible = Assume(facts, feasible.After[new(block.Id, index)], callEffect?.Death.Guard);
            if (!possible.Reachable) return;
            foreach (var target in roots)
                if ((target.HandleParameter ?? (target.Kind == MirReferenceOriginKind.Parameter ? target.Ordinal : null)) is { } ordinal)
                    result.Add(new(ordinal, target.Path, target.UnknownObjectOffset, false, Conditions(possible), MirEdgeKind.Normal, source,
                        Phase: MirLifetimeEffectPhase.Operation, Transition: MirLifetimeTransition.PlacementReconstruction));
            if (roots.Count != 1) { _needsGuardRefinement |= roots.Count > 1; return; }
            var root = roots.Single();
            int? parameter = root.HandleParameter ?? (root.Kind == MirReferenceOriginKind.Parameter ? root.Ordinal : null);
            if (parameter is null || root.PriorInstance || root.UnknownObjectOffset || root.Path.Contains('*'))
            { _needsGuardRefinement |= parameter is not null; return; }
            var location = new MirLocation(block.Id, index);
            var seed = Assume(facts, feasible.After[location], callEffect?.Death.Guard);
            if (!seed.Reachable) return;
            if (!SupportedPath(_graph, feasible, block.Id, seed, root)) { _needsGuardRefinement = true; return; }
            result.Add(new(parameter.Value, root.Path, false, false, Conditions(seed), MirEdgeKind.Normal, source,
                Phase: MirLifetimeEffectPhase.Operation, Transition: MirLifetimeTransition.PlacementReconstruction));
            if (callEffect?.Effect.Phase == MirLifetimeEffectPhase.Operation) return;
            var entry = new MirBlockId(_function.Blocks.Max(node => node.Id.Value) + 1);
            var tail = new MirBasicBlock(entry, [.. block.Statements.Skip(index + 1)], block.Terminator);
            if (callEffect is not null)
            {
                var edge = block.Terminator.Successors.First(edge => edge.Kind == callEffect.Effect.Exit);
                seed = facts.Edge(block, edge, seed);
                tail = new(entry, [], new MirGoto(edge.Target, source));
            }
            var graph = new MirControlFlow(_function with { Entry = entry,
                Blocks = _function.Blocks.Add(tail) });
            var witness = MirDataflow.Solve(graph, new DeathWitness(facts, seed, entry), cancellation);
            bool MayEnd(ImmutableHashSet<MirReferenceOrigin> targets) => targets.Any(target => target.Kind == MirReferenceOriginKind.Unknown ||
                SameObject(root, target) || SameObject(root, target, storage: true));
            if (_destructionTargets.Any(pair => witness.Before.TryGetValue(_locations[pair.Key], out var point) && point.Reachable && MayEnd(pair.Value)) ||
                _callEffects.Any(pair => witness.Before.TryGetValue(_locations[pair.Key], out var point) && point.Reachable &&
                    pair.Value.Any(effect => effect.Effect.InvalidatesBorrow && MayEnd(effect.Targets))))
            { _needsGuardRefinement = true; return; }
            foreach (var end in graph.Function.Blocks.Where(node => node.Terminator is MirReturn or MirResumeUnwind))
            {
                var state = witness.Output[end.Id];
                if (!state.Reachable || !SupportedPath(graph, witness, end.Id, state, root)) continue;
                if (end.Terminator is MirReturn returned)
                    result.Add(new(parameter.Value, root.Path, false, false, Conditions(state), MirEdgeKind.Normal, source,
                        returned.Value is { } value ? MirFlowFacts.KnownConstant(value, state) : null, Transition: MirLifetimeTransition.PlacementReconstruction));
                else foreach (var error in state.Pending.DefaultIfEmpty(BuiltinTypes.Error))
                    result.Add(new(parameter.Value, root.Path, false, false, Conditions(state), MirEdgeKind.Unwind, source,
                        ExceptionType: TypeIdentity.AreSame(error, BuiltinTypes.Error) ? null : error, Transition: MirLifetimeTransition.PlacementReconstruction));
            }
        }

        MirPointerParameterGuard? Conditions(MirFlowState state)
        {
            MirPointerParameterGuard? conditions = null;
            foreach (var (predicate, value) in state.PredicateValues.OrderByDescending(pair => MirPrinter.Operand(pair.Key.Left), StringComparer.Ordinal)
                .ThenByDescending(pair => pair.Key.Operator).ThenByDescending(pair => MirPrinter.Operand(pair.Key.Right), StringComparer.Ordinal))
                if (value is 1 or 2 && predicate.Left is MirCopy { Place.Projections.IsEmpty: true } copy &&
                    parameters.TryGetValue(copy.Place.Local, out var ordinal) && predicate.Right is MirConstant { Value: not null } constant)
                    conditions = new(ordinal, value == 1, conditions, predicate.Operator, constant.Value);
            foreach (var (local, ordinal) in parameters.OrderByDescending(pair => pair.Value))
            {
                byte value = state.Booleans.GetValueOrDefault(local, (byte)3);
                if (value is 1 or 2) conditions = new(ordinal, value == 1, conditions);
            }
            return conditions;
        }

        bool SupportedPath(MirControlFlow graph, MirDataflowResult<MirFlowState> flow, MirBlockId destination, MirFlowState atDestination, MirReferenceOrigin target)
        {
            using var pathProfile = MirAnalysisProfile.Start("SupportedPath", graph.Function);
            // A single conjunction of unchanged bool formals is deliberately
            // bounded. If a restricting branch cannot be expressed, omit the
            // effect instead of widening its guard to unconditional destruction.
            var availableEdges = ReferenceEquals(flow, feasible) ? feasibleEdges : new Dictionary<MirBlockId, MirEdge[]>();
            MirEdge[] Edges(MirBasicBlock node)
            {
                if (!availableEdges.TryGetValue(node.Id, out var edges))
                    availableEdges[node.Id] = edges = flow.Output[node.Id].Reachable
                        ? node.Terminator.Successors.Where(edge => facts.Edge(node, edge, flow.Output[node.Id]).Reachable).Distinct().ToArray() : [];
                return edges;
            }
            var reaching = new HashSet<MirBlockId> { destination };
            var pending = new Queue<MirBlockId>();
            pending.Enqueue(destination);
            while (pending.TryDequeue(out var current))
            {
                foreach (var predecessor in graph.Predecessors[current])
                {
                    var node = graph.Blocks[predecessor.Source];
                    if (!reaching.Contains(node.Id) && flow.Output[node.Id].Reachable &&
                        Edges(node).Any(edge => edge.Target == current && edge.Kind == predecessor.Kind) && reaching.Add(node.Id))
                        pending.Enqueue(node.Id);
                }
            }
            foreach (var node in graph.Function.Blocks.Where(node => reaching.Contains(node.Id) && node.Terminator is MirSwitch))
            {
                var edges = Edges(node);
                if (edges.Length < 2 || edges.All(edge => reaching.Contains(edge.Target))) continue;
                var branch = (MirSwitch)node.Terminator;
                if (branch.Value is not MirCopy { Place.Projections.IsEmpty: true } copy) return false;
                var alias = flow.Output[node.Id].BooleanAliases.GetValueOrDefault(copy.Place.Local, new(copy.Place.Local, false));
                // Lowering guards delete with a null test. A singleton allocation
                // target already identifies the non-null alternative; do not lose
                // every delete summary merely because this implicit guard exists.
                if (flow.Output[node.Id].Predicates.TryGetValue(alias.Root, out var comparison) &&
                    comparison.Predicate is { Operator: MirBinaryOperator.Equal, Right: MirConstant { Value: null } } predicate &&
                    atDestination.PredicateValues.GetValueOrDefault(predicate) == 2 &&
                    _provenance.Output.TryGetValue(node.Id, out var origins) && _origins.Operand(predicate.Left, origins).Roots.SetEquals([target])) continue;
                if (flow.Output[node.Id].Predicates.TryGetValue(alias.Root, out var scalar) &&
                    scalar.Predicate.Left is MirCopy { Place.Projections.IsEmpty: true } parameter && parameters.ContainsKey(parameter.Place.Local) &&
                    scalar.Predicate.Right is MirConstant { Value: not null } && atDestination.PredicateValues.GetValueOrDefault(scalar.Predicate) is 1 or 2) continue;
                if (!parameters.ContainsKey(alias.Root) || atDestination.Booleans.GetValueOrDefault(alias.Root, (byte)3) is not (1 or 2)) return false;
            }
            return true;
        }
    }

    internal static IReadOnlyDictionary<FunctionSymbol, MirLifetimeSummary> BuildSummaries(
        MirFunction[] bodies, IReadOnlyDictionary<FunctionSymbol, MirReferenceValue> returns, CancellationToken cancellation,
        MirLifetimeBudgets? budgets = null)
    {
        bodies = WithCaptureViews(bodies);
        budgets ??= new();
        int contextBudget = budgets.Contexts, effectBudget = budgets.Effects, componentBudget = budgets.ComponentRounds;
        if (contextBudget < 1 || effectBudget < 1 || componentBudget < 1) throw new ArgumentOutOfRangeException(nameof(budgets));
        var available = bodies.Where(body => !body.Symbol.IsAsync).ToDictionary(body => body.Symbol);
        var summaries = available.Keys.ToDictionary(symbol => symbol, _ => MirLifetimeSummary.Empty);
        var effects = available.Keys.ToDictionary(symbol => symbol, _ => ImmutableHashSet<MirLifetimeEffect>.Empty);
        var exits = available.Keys.ToDictionary(symbol => symbol, _ => ImmutableHashSet<MirLifetimeExit>.Empty);
        foreach (var callee in bodies.SelectMany(body => body.Blocks).Select(block => block.Terminator).OfType<MirCall>()
            .Select(DirectCallee).OfType<FunctionSymbol>().Distinct().Where(symbol => !available.ContainsKey(symbol)))
            if (callee.MirLifetimeSummary is { } imported)
            {
                summaries[callee] = imported;
                effects[callee] = imported.Effects;
                if (imported.Complete) exits[callee] = imported.Exits;
            }
        IEnumerable<FunctionSymbol> Dependencies(MirFunction body) => body.Blocks.Select(block => block.Terminator).OfType<MirCall>()
            .Select(DirectCallee).OfType<FunctionSymbol>().Concat(body.Blocks.Select(block => block.Terminator)
                .OfType<MirIntrinsicCall>().Where(call => call.Intrinsic == MirIntrinsicKind.MakeCallable)
                .Select(call => call.Function?.MirCaptureFunction ?? call.Function).OfType<FunctionSymbol>());
        var calls = available.ToDictionary(pair => pair.Key, pair => Dependencies(pair.Value)
            .Where(available.ContainsKey).Distinct().OrderBy(FunctionKey, StringComparer.Ordinal).ToArray());
        foreach (var component in Components())
        {
            bool stable = false;
            for (int round = 0; round < componentBudget; round++)
            {
                bool changed = false;
                foreach (var symbol in component)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var body = available[symbol];
                    var found = ImmutableHashSet<MirLifetimeEffect>.Empty;
                    var returned = ImmutableHashSet<MirLifetimeExit>.Empty;
                    var reasons = ImmutableSortedSet.CreateBuilder<string>(StringComparer.Ordinal);
                    var uncertain = ImmutableHashSet.CreateBuilder<MirLifetimeUncertainty>();
                    var contexts = new Queue<(MirPointerParameterGuard? Guard, ImmutableHashSet<MirLifetimeUncertainty> Coverage)>();
                    contexts.Enqueue((null, []));
                    int analyzed = 0;
                    while (analyzed < contextBudget && contexts.TryDequeue(out var work))
                    {
                        analyzed++;
                        var context = work.Guard;
                        var analysis = new MirPointerLifetimeAnalysis(body, returns, cancellation, effects,
                            symbolicParameters: true, entryGuard: context, lifetimeExits: exits);
                        found = found.Union(analysis.SummarizeEffects(cancellation, summaries));
                        returned = returned.Union(analysis._summaryExits);
                        if (analysis._hasOpaqueEffects) reasons.Add("opaque-call");
                        if (!analysis._needsGuardRefinement) { uncertain.UnionWith(analysis._uncertainties); continue; }
                        var bound = new HashSet<int>();
                        for (var item = context; item is not null; item = item.Next) bound.Add(item.Parameter);
                        var next = body.Symbol.Parameters.FirstOrDefault(parameter => TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Bool) && !bound.Contains(parameter.Ordinal));
                        if (next is null) { reasons.Add("unresolved-target-or-restoration"); uncertain.UnionWith(analysis._uncertainties); continue; }
                        contexts.Enqueue((new(next.Ordinal, false, context), analysis._uncertainties.ToImmutableHashSet()));
                        contexts.Enqueue((new(next.Ordinal, true, context), analysis._uncertainties.ToImmutableHashSet()));
                    }
                    if (contexts.Count != 0)
                    {
                        reasons.Add("guard-context-budget");
                        foreach (var work in contexts)
                            uncertain.UnionWith(work.Coverage.Select(item => item with { Reason = "guard-context-budget" }));
                    }
                    if (found.Count > effectBudget) reasons.Add("effect-budget");
                    returned = summaries[symbol].Exits.Union(returned);
                    if (returned.Count > effectBudget) reasons.Add("exit-budget");
                    foreach (var dropped in found.OrderBy(EffectKey, StringComparer.Ordinal).Skip(effectBudget).Where(effect => effect.Phase == MirLifetimeEffectPhase.Operation))
                        uncertain.Add(new(dropped.Parameter, dropped.Path, dropped.UnknownOffset, dropped.Guard, dropped.Kind, "effect-budget"));
                    found = found.OrderBy(EffectKey, StringComparer.Ordinal).Take(effectBudget).ToImmutableHashSet();
                    returned = returned.OrderBy(ExitKey, StringComparer.Ordinal).Take(effectBudget).ToImmutableHashSet();
                    foreach (var dependency in Dependencies(body).Distinct())
                        if (summaries.TryGetValue(dependency, out var dependencySummary) && !dependencySummary.Complete) reasons.Add("incomplete-callee");
                    var previous = summaries[symbol];
                    foreach (var item in uncertain) reasons.Add(item.Reason);
                    changed |= !previous.Effects.SetEquals(found) || !previous.Exits.SetEquals(returned) || !previous.IncompleteReasons.SetEquals(reasons) ||
                        previous.Uncertainties is null || !previous.Uncertainties.SetEquals(uncertain);
                    summaries[symbol] = new(found, returned, reasons.ToImmutable(), analyzed) { Uncertainties = uncertain.ToImmutable() };
                    effects[symbol] = found;
                    if (reasons.Count == 0) exits[symbol] = returned;
                    else exits.Remove(symbol); // A partial set cannot prove an edge unreachable.
                }
                if (!changed || component.Length == 1 && !calls[component[0]].Contains(component[0])) { stable = true; break; }
            }
            if (!stable)
            {
                var coverage = ComponentCoverage(component, available, returns, summaries, cancellation);
                foreach (var symbol in component)
                {
                    var previous = summaries[symbol];
                    // Unstable exit-state proofs must not leak out of a recursive
                    // component. Established operation effects remain useful to
                    // the strict checked-borrow consumer.
                    effects[symbol] = previous.Effects.Where(effect => effect.Phase == MirLifetimeEffectPhase.Operation).ToImmutableHashSet();
                    exits.Remove(symbol);
                    summaries[symbol] = previous with { Effects = effects[symbol], IncompleteReasons = previous.IncompleteReasons.Add("recursive-fixed-point-budget"),
                        Uncertainties = coverage[symbol] };
                }
            }
        }
        return summaries;

        IEnumerable<FunctionSymbol[]> Components()
        {
            int nextIndex = 0;
            var index = new Dictionary<FunctionSymbol, int>();
            var low = new Dictionary<FunctionSymbol, int>();
            var pending = new Stack<FunctionSymbol>();
            var active = new HashSet<FunctionSymbol>();
            var components = new List<FunctionSymbol[]>();
            foreach (var symbol in available.Keys.OrderBy(FunctionKey, StringComparer.Ordinal)) if (!index.ContainsKey(symbol)) Visit(symbol);
            return components;
            void Visit(FunctionSymbol symbol)
            {
                cancellation.ThrowIfCancellationRequested();
                index[symbol] = low[symbol] = nextIndex++;
                pending.Push(symbol); active.Add(symbol);
                foreach (var callee in calls[symbol])
                {
                    if (!index.ContainsKey(callee)) { Visit(callee); low[symbol] = Math.Min(low[symbol], low[callee]); }
                    else if (active.Contains(callee)) low[symbol] = Math.Min(low[symbol], index[callee]);
                }
                if (low[symbol] != index[symbol]) return;
                var members = new List<FunctionSymbol>();
                FunctionSymbol member;
                do { member = pending.Pop(); active.Remove(member); members.Add(member); } while (member != symbol);
                components.Add(members.OrderBy(FunctionKey, StringComparer.Ordinal).ToArray());
            }
        }
    }

    internal static string FunctionKey(FunctionSymbol function) => function.FullName + "(" + string.Join(',', function.Parameters.Select(parameter => parameter.Type.ToDisplayString(TypeDisplayFormat.FullyQualified))) + ")";
    internal static string GuardKey(MirPointerParameterGuard? guard) => guard is null ? "" :
        $"{guard.Parameter}:{guard.Operator}:{Convert.ToString(guard.Constant, System.Globalization.CultureInfo.InvariantCulture)}:{guard.Value};" + GuardKey(guard.Next);
    internal static string EffectKey(MirLifetimeEffect effect) => $"{effect.Parameter:D6}:{effect.Path}:{effect.Kind}:{effect.Phase}:{effect.UnknownOffset}:" +
        GuardKey(effect.Guard) + $":{effect.Exit}:{effect.Result}:{effect.ExceptionType}:{effect.Source.Location.Path}:{effect.Source.Location.Span.Start}";
    internal static string ExitKey(MirLifetimeExit exit) => GuardKey(exit.Guard) + $":{exit.Exit}:{exit.Result}:{exit.ExceptionType}";
}
