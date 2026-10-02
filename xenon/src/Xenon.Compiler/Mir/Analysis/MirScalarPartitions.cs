using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirScalarPartition<T>(ImmutableDictionary<MirLocalId, long> Scalars, T Value);

/// <summary>
/// Finite scalar partitions preserve correlations between cleanup flags, registration
/// order and memory. Widening merges partitions conservatively; it never drops an edge.
/// </summary>
public sealed class MirScalarPartitions<T> : IMirDataflowAnalysis<ImmutableArray<MirScalarPartition<T>>>
{
    private readonly IMirDataflowAnalysis<T> _inner;
    private readonly MirDataflowResult<ImmutableHashSet<MirLocalId>> _live;
    private readonly Dictionary<MirStatement, MirLocation> _locations = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MirLocalId> _eligible;
    private const int Limit = 64;
    public MirScalarPartitions(MirControlFlow graph, IMirDataflowAnalysis<T> inner, CancellationToken cancellation = default)
    {
        if (inner.Direction != MirDataflowDirection.Forward) throw new ArgumentException("Scalar partitions require a forward analysis.", nameof(inner));
        _inner = inner;
        _live = MirDataflow.Solve(graph, new MirLiveness(), cancellation);
        _eligible = graph.Function.Locals.Where(local => local.IsCleanupControl).Select(local => local.Id).ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (MirAssign assign in graph.Function.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>())
            {
                if (!assign.Destination.Projections.IsEmpty ||
                    (!TypeIdentity.AreSame(assign.Value.Type, BuiltinTypes.Int) && !TypeIdentity.AreSame(assign.Value.Type, BuiltinTypes.Long) && !TypeIdentity.AreSame(assign.Value.Type, BuiltinTypes.Bool))) continue;
                MirOperand[] operands = MirOperands.Of(assign.Value).ToArray();
                if (operands.Any(operand => operand is MirCopy copy && _eligible.Contains(copy.Place.Local)) &&
                    operands.All(operand => operand is MirConstant || operand is MirCopy { Place.Projections.IsEmpty: true } copy && _eligible.Contains(copy.Place.Local)))
                    changed |= _eligible.Add(assign.Destination.Local);
            }
        } while (changed);
        foreach (MirBasicBlock block in graph.Function.Blocks)
            for (int index = 0; index < block.Statements.Length; index++)
            {
                var statement = block.Statements[index];
                _locations[statement] = new(block.Id, index);
                if (statement is MirAssign { Value: MirBorrow borrow }) _eligible.Remove(borrow.Place.Local);
            }
    }
    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public ImmutableArray<MirScalarPartition<T>> Bottom => [];
    public ImmutableArray<MirScalarPartition<T>> Boundary(MirControlFlow graph, MirBasicBlock block)
    {
        T value = _inner.Boundary(graph, block);
        return _inner.Same(value, _inner.Bottom) ? [] : [new(ImmutableDictionary<MirLocalId, long>.Empty, value)];
    }
    private static bool Covers(ImmutableDictionary<MirLocalId, long> general, ImmutableDictionary<MirLocalId, long> specific) =>
        general.All(pair => specific.TryGetValue(pair.Key, out long value) && value == pair.Value);
    private static bool Equal(ImmutableDictionary<MirLocalId, long> left, ImmutableDictionary<MirLocalId, long> right) => left.Count == right.Count && Covers(left, right);
    public ImmutableArray<MirScalarPartition<T>> Join(IEnumerable<ImmutableArray<MirScalarPartition<T>>> states)
    {
        var result = new List<MirScalarPartition<T>>();
        foreach (var next in states.SelectMany(state => state))
        {
            if (_inner.Same(next.Value, _inner.Bottom)) continue;
            int covering = result.FindIndex(previous => Covers(previous.Scalars, next.Scalars));
            if (covering >= 0)
            {
                result[covering] = result[covering] with { Value = _inner.Join([result[covering].Value, next.Value]) };
                continue;
            }
            var covered = result.Where(previous => Covers(next.Scalars, previous.Scalars)).ToArray();
            result.RemoveAll(previous => covered.Contains(previous));
            result.Add(next with { Value = _inner.Join(covered.Select(previous => previous.Value).Append(next.Value)) });
        }
        if (result.Count <= Limit) return [.. result];
        var common = result[0].Scalars.Where(pair => result.All(partition =>
            partition.Scalars.TryGetValue(pair.Key, out long value) && value == pair.Value)).ToImmutableDictionary();
        return [new(common, _inner.Join(result.Select(partition => partition.Value)))];
    }
    public bool Same(ImmutableArray<MirScalarPartition<T>> left, ImmutableArray<MirScalarPartition<T>> right) =>
        left.Length == right.Length && left.All(partition => right.Any(other => Equal(partition.Scalars, other.Scalars) && _inner.Same(partition.Value, other.Value)));
    private static long? Constant(object? value) => value switch
    {
        bool boolean => boolean ? 1 : 0,
        sbyte number => number, byte number => number, short number => number, ushort number => number,
        int number => number, long number => number,
        _ => null,
    };
    private static long? Operand(MirOperand operand, ImmutableDictionary<MirLocalId, long> values) => operand switch
    {
        MirConstant literal => Constant(literal.Value),
        MirCopy { Place.Projections.IsEmpty: true } copy when values.TryGetValue(copy.Place.Local, out long value) => value,
        _ => null,
    };
    private static long? Value(MirRValue value, ImmutableDictionary<MirLocalId, long> values)
    {
        if (value is MirUse use) return Operand(use.Operand, values);
        if (value is MirUnary { Operator: MirUnaryOperator.Not } unary && Operand(unary.Operand, values) is { } boolean) return boolean == 0 ? 1 : 0;
        if (value is not MirBinary binary || Operand(binary.Left, values) is not { } left || Operand(binary.Right, values) is not { } right) return null;
        if (left is < -256 or > 256 || right is < -256 or > 256) return null;
        return binary.Operator switch
        {
            MirBinaryOperator.Add => left + right, MirBinaryOperator.Subtract => left - right,
            MirBinaryOperator.Equal => left == right ? 1 : 0, MirBinaryOperator.NotEqual => left != right ? 1 : 0,
            MirBinaryOperator.Greater => left > right ? 1 : 0, MirBinaryOperator.GreaterOrEqual => left >= right ? 1 : 0,
            MirBinaryOperator.Less => left < right ? 1 : 0, MirBinaryOperator.LessOrEqual => left <= right ? 1 : 0,
            _ => null,
        };
    }
    public ImmutableArray<MirScalarPartition<T>> Statement(MirStatement statement, ImmutableArray<MirScalarPartition<T>> state) =>
        [.. state.Select(partition =>
        {
            var scalars = partition.Scalars;
            if (statement is MirAssign { Destination.Projections.IsEmpty: true } assign)
            {
                long? value = _eligible.Contains(assign.Destination.Local) ? Value(assign.Value, scalars) : null;
                scalars = scalars.Remove(assign.Destination.Local);
                if (value is >= -256 and <= 256) scalars = scalars.SetItem(assign.Destination.Local, value.Value);
            }
            if (statement is MirStorageLive live) scalars = scalars.Remove(live.Local);
            if (statement is MirStorageDead dead) scalars = scalars.Remove(dead.Local);
            if (_live.After.TryGetValue(_locations[statement], out var liveAfter))
                scalars = scalars.RemoveRange(scalars.Keys.Where(local => !liveAfter.Contains(local)));
            return new MirScalarPartition<T>(scalars, _inner.Statement(statement, partition.Value));
        })];
    public ImmutableArray<MirScalarPartition<T>> Terminator(MirTerminator terminator, ImmutableArray<MirScalarPartition<T>> state) =>
        [.. state.Select(partition => partition with { Value = _inner.Terminator(terminator, partition.Value) })];
    public ImmutableArray<MirScalarPartition<T>> Edge(MirBasicBlock source, MirEdge edge, ImmutableArray<MirScalarPartition<T>> state)
    {
        var result = ImmutableArray.CreateBuilder<MirScalarPartition<T>>();
        foreach (var partition in state)
        {
            if (source.Terminator is MirSwitch branch && Operand(branch.Value, partition.Scalars) is { } value)
            {
                var target = branch.Cases.FirstOrDefault(item => Constant(item.Value.Value) == value)?.Target ?? branch.Otherwise;
                if (target != edge.Target) continue;
            }
            var scalars = partition.Scalars;
            MirPlace? destination = source.Terminator switch { MirCall call => call.Destination, MirIntrinsicCall call => call.Destination, _ => null };
            if (edge.Kind == MirEdgeKind.Normal && destination is { Projections.IsEmpty: true }) scalars = scalars.Remove(destination.Local);
            if (_live.Input.TryGetValue(edge.Target, out var live)) scalars = scalars.RemoveRange(scalars.Keys.Where(local => !live.Contains(local)));
            T transferred = _inner.Edge(source, edge, partition.Value);
            if (!_inner.Same(transferred, _inner.Bottom)) result.Add(new(scalars, transferred));
        }
        return result.ToImmutable();
    }
}