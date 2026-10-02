using System.Collections.Immutable;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    // Stable between lowering rounds: local ids and cleanup temporaries may change.
    private sealed record LifetimeOwner(VariableSymbol? Variable, bool Receiver, bool Indirect, string Path, TypeSymbol ValueType);
    private readonly IReadOnlyDictionary<BoundExpression, LifetimeOwner?>? _lifetimeOwners;
    private readonly Dictionary<BoundExpression, List<(MirLocation Location, MirPlace Place)>> _lifetimePoints =
        new(ReferenceEqualityComparer.Instance);

    private MirPlace? LifetimePlace(BoundExpression expression, MirPlace actual)
    {
        if (!_lifetimePoints.TryGetValue(expression, out var points)) _lifetimePoints.Add(expression, points = []);
        points.Add((new(_current.Id, _current.Statements.Count), actual));
        if (_lifetimeOwners?.GetValueOrDefault(expression) is not { } owner) return null;
        MirPlace place = owner.Receiver ? _receiver! : Variable(owner.Variable!);
        TypeSymbol type = _locals[place.Local.Value].Type;
        if (owner.Indirect)
        {
            place = place.Project(new MirDerefProjection());
            type = type is ReferenceTypeSymbol reference ? reference.ElementType : ((PointerTypeSymbol)type).ElementType;
        }
        foreach (string part in owner.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            while (type is LifetimeModifierTypeSymbol modifier)
            { place = place.Project(new MirLifetimeProjection()); type = modifier.ElementType; }
            FieldSymbol field = ((IFieldStorageTypeSymbol)type).AllInstanceFields.Single(field =>
                field.Ordinal == int.Parse(part, System.Globalization.CultureInfo.InvariantCulture));
            place = place.Project(new MirFieldProjection(field));
            type = field.Type;
        }
        while (!TypeIdentity.AreSame(type, owner.ValueType) && type is ReferenceTypeSymbol or LifetimeModifierTypeSymbol)
        {
            if (type is ReferenceTypeSymbol reference)
            { place = place.Project(new MirDerefProjection()); type = reference.ElementType; }
            else if (type is LifetimeModifierTypeSymbol modifier)
            { place = place.Project(new MirLifetimeProjection()); type = modifier.ElementType; }
        }
        return place;
    }

    private Dictionary<BoundExpression, LifetimeOwner?> ResolveLifetimeOwners(MirFunction mir)
    {
        var next = new Dictionary<BoundExpression, LifetimeOwner?>(ReferenceEqualityComparer.Instance);
        if (_lifetimePoints.Count == 0) return next;
        var origins = new MirReferenceOrigins(mir);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(mir, _cancellation), origins, _cancellation);
        foreach (var (expression, points) in _lifetimePoints)
        {
            var owners = points.Where(point => flow.Before.ContainsKey(point.Location))
                .SelectMany(point => origins.Address(point.Place, flow.Before[point.Location])
                    .Select(root => Owner(root, origins.PlaceType(point.Place)))).Distinct().ToArray();
            next[expression] = owners.Length == 1 ? owners[0] : null;
        }
        return next;

        LifetimeOwner? Owner(MirReferenceOrigin origin, TypeSymbol valueType)
        {
            if (origin.Path == "*") return null;
            MirLocal? local = origin.Kind switch
            {
                MirReferenceOriginKind.Local => mir.Locals.FirstOrDefault(local => local.Id.Value == origin.Ordinal),
                MirReferenceOriginKind.Receiver => mir.Locals.FirstOrDefault(local => local.Kind == MirLocalKind.Receiver),
                MirReferenceOriginKind.Parameter => mir.Locals.FirstOrDefault(local => local.Variable is ParameterSymbol parameter && parameter.Ordinal == origin.Ordinal),
                _ => null,
            };
            if (local is null || local.Variable is null && local.Kind != MirLocalKind.Receiver) return null;
            return new(local.Variable, local.Kind == MirLocalKind.Receiver,
                origin.Kind is MirReferenceOriginKind.Receiver or MirReferenceOriginKind.Parameter, origin.Path, valueType);
        }
    }
    private sealed record AssignmentState(MirPreviousValueState Previous, bool Initialize, bool RuntimeCheck);
    private readonly IReadOnlyDictionary<BoundAssignmentExpression, AssignmentState>? _ownershipStates;

    private AssignmentState Ownership(BoundAssignmentExpression expression) =>
        _ownershipStates?.GetValueOrDefault(expression) ??
            new(MirPreviousValueState.Live, false, false);

    private void RecordStore(BoundAssignmentExpression expression, MirPlace target)
    {
        if (!_storePoints.TryGetValue(expression, out var points)) _storePoints.Add(expression, points = []);
        points.Add((new(_current.Id, _current.Statements.Count), target));
    }
    private readonly Dictionary<BoundAssignmentExpression, List<(MirLocation Location, MirPlace Place)>> _storePoints =
        new(ReferenceEqualityComparer.Instance);

    public static MirFunction Lower(BoundFunction function, TypeFactory types,
        IReadOnlyDictionary<BoundExpression, TextLocation>? locations = null, CancellationToken cancellation = default,
        IReadOnlyDictionary<BoundExpression, int>? origins = null, bool diagnosticRecovery = false,
        IReadOnlySet<BoundExpression>? invalidExpressions = null)
    {
        MirFunction Finish(MirFunction mir)
        {
            mir = RetainArrayBacking(mir, cancellation);
            if (mir.Resumable is not null)
            {
                var graph = MirFlowFacts.Graph(mir, cancellation);
                if (!mir.Blocks.Any(block => graph.Reachable.Contains(block.Id) && block.Terminator is MirSuspend))
                    return Lower(function with { Body = function.Body with { RequiresSuspensionStateMachine = false } },
                        types, locations, cancellation, origins, diagnosticRecovery, invalidExpressions);
            }
            return mir;
        }
        IReadOnlyDictionary<BoundAssignmentExpression, AssignmentState>? previous = null;
        IReadOnlyDictionary<BoundExpression, LifetimeOwner?>? previousOwners = null;
        for (int round = 0; ; round++)
        {
            cancellation.ThrowIfCancellationRequested();
            MirFunction mir = LowerCore(function, types, locations, cancellation, origins, diagnosticRecovery,
                invalidExpressions, previous, previousOwners, out MirLowerer lowering);
            if (lowering._storePoints.Count == 0 && lowering._lifetimePoints.Count == 0) return Finish(mir);
            var nextOwners = lowering.ResolveLifetimeOwners(mir);
            var analysis = new MirMoveAnalysis(mir, cancellation);
            var flow = MirDataflow.Solve(MirFlowFacts.Graph(mir, cancellation), analysis, cancellation);
            var next = new Dictionary<BoundAssignmentExpression, AssignmentState>(ReferenceEqualityComparer.Instance);
            foreach (var (expression, points) in lowering._storePoints)
            {
                var states = points.Where(point => flow.Before.ContainsKey(point.Location))
                    .SelectMany(point => analysis.Resolve(point.Place, point.Location)
                        .Select(place => analysis.Status(place, flow.Before[point.Location]))).ToArray();
                MirPreviousValueState moved = states.Length != 0 && states.All(state => state == MirOwnershipStatus.Moved)
                    ? MirPreviousValueState.DefinitelyMoved
                    : states.Any(state => state is MirOwnershipStatus.Moved or MirOwnershipStatus.MaybeMoved)
                        ? MirPreviousValueState.MaybeMoved : MirPreviousValueState.Live;
                bool initialization = states.Length != 0 && states.All(state => state == MirOwnershipStatus.Uninitialized);
                bool conditional = states.Any(state => state == MirOwnershipStatus.MaybeInitialized) ||
                    states.Contains(MirOwnershipStatus.Uninitialized) && states.Any(state => state != MirOwnershipStatus.Uninitialized);
                next[expression] = new(moved, initialization, conditional);
            }
            if (previous is not null && previous.Count == next.Count &&
                next.All(pair => previous.TryGetValue(pair.Key, out var state) && state == pair.Value) && previousOwners is not null && previousOwners.Count == nextOwners.Count &&
                nextOwners.All(pair => previousOwners.TryGetValue(pair.Key, out var owner) && owner == pair.Value)) return Finish(mir);
            if (round > lowering._storePoints.Count + lowering._lifetimePoints.Count + 1)
                throw new InvalidOperationException("MIR ownership lowering did not stabilize.");
            previous = next;
            previousOwners = nextOwners;
        }
    }
    private static MirFunction RetainArrayBacking(MirFunction mir, CancellationToken cancellation)
    {
        if (!mir.Blocks.SelectMany(block => block.Statements).OfType<MirStackRestore>().Any() ||
            !mir.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>()
                .Any(assign => MirOperands.Of(assign.Value).Any(operand => operand is MirMove { Type: ArrayTypeSymbol }))) return mir;
        var analysis = new MirLifetimeAnalysis(mir, cancellation: cancellation);
        _ = analysis.Analyze();
        var retained = analysis.RetainedArrayScopes;
        if (retained.IsEmpty) return mir;
        mir = mir with { Blocks = [.. mir.Blocks.Select(block => block with
        {
            Statements = [.. block.Statements.Where(statement => statement is not MirStackRestore || !retained.Contains(statement.Source.Scope))],
        })] };
        MirVerifier.VerifyOrThrow(mir);
        return mir;
    }
}