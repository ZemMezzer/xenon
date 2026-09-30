using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
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
        IReadOnlyDictionary<BoundAssignmentExpression, AssignmentState>? previous = null;
        for (int round = 0; ; round++)
        {
            cancellation.ThrowIfCancellationRequested();
            MirFunction mir = LowerCore(function, types, locations, cancellation, origins, diagnosticRecovery,
                invalidExpressions, previous, out MirLowerer lowering);
            if (lowering._storePoints.Count == 0) return mir;
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
                next.All(pair => previous.TryGetValue(pair.Key, out var state) && state == pair.Value)) return mir;
            if (round > lowering._storePoints.Count + 1)
                throw new InvalidOperationException("MIR assignment ownership did not stabilize.");
            previous = next;
        }
    }
}