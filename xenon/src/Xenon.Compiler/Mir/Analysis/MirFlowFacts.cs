using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirFlowState(bool Reachable, ImmutableHashSet<TypeSymbol> Pending,
    ImmutableDictionary<MirLocalId, ImmutableHashSet<TypeSymbol>> ExceptionRecords,
    ImmutableDictionary<MirLocalId, byte> Booleans)
{
    public ImmutableDictionary<MirLocalId, MirExceptionTest> ExceptionTests { get; init; } = ImmutableDictionary<MirLocalId, MirExceptionTest>.Empty;
}
public sealed record MirExceptionTest(MirLocalId Record, TypeSymbol Type);

/// <summary>
/// Feasible control-flow edges from MIR boolean facts and exception records.
/// Unknown exceptions preserve both catch edges; explicit typed throws can
/// establish a definite match. Facts do not rely on a source control-flow tree.
/// </summary>
public sealed class MirFlowFacts(IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>>? effects = null,
    Func<MirTerminator, IEnumerable<TypeSymbol>?>? callEffects = null, bool externalCallsMayUnwind = false) : IMirDataflowAnalysis<MirFlowState>
{
    private static readonly ImmutableHashSet<TypeSymbol> Unknown = ImmutableHashSet.Create<TypeSymbol>(TypeIdentity.Comparer, BuiltinTypes.Error);
    private static readonly ImmutableHashSet<TypeSymbol> EmptyTypes = ImmutableHashSet.Create<TypeSymbol>(TypeIdentity.Comparer);
    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public MirFlowState Bottom { get; } = new(false, EmptyTypes,
        ImmutableDictionary<MirLocalId, ImmutableHashSet<TypeSymbol>>.Empty,
        ImmutableDictionary<MirLocalId, byte>.Empty);
    public MirFlowState Boundary(MirControlFlow graph, MirBasicBlock block) =>
        block.Id == graph.Function.Entry ? Bottom with { Reachable = true } : Bottom;

    public static MirControlFlow Graph(MirFunction function, CancellationToken cancellation = default,
        IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>>? effects = null, bool externalCallsMayUnwind = false)
    {
        var analysis = new MirFlowFacts(effects, externalCallsMayUnwind: externalCallsMayUnwind);
        var facts = MirDataflow.Solve(new MirControlFlow(function), analysis, cancellation);
        return new(function, (block, edge) => analysis.Edge(block, edge, facts.Output[block.Id]).Reachable);
    }

    public MirFlowState Join(IEnumerable<MirFlowState> states)
    {
        MirFlowState[] reachable = states.Where(state => state.Reachable).ToArray();
        if (reachable.Length == 0) return Bottom;
        var records = ImmutableDictionary.CreateBuilder<MirLocalId, ImmutableHashSet<TypeSymbol>>();
        foreach (MirLocalId local in reachable.SelectMany(state => state.ExceptionRecords.Keys).Distinct())
            records[local] = reachable.Select(state => state.ExceptionRecords.GetValueOrDefault(local, Unknown))
                .Aggregate(EmptyTypes, (result, types) => result.Union(types));
        var booleans = ImmutableDictionary.CreateBuilder<MirLocalId, byte>();
        foreach (MirLocalId local in reachable.SelectMany(state => state.Booleans.Keys).Distinct())
            booleans[local] = reachable.Aggregate((byte)0,
                (result, state) => (byte)(result | state.Booleans.GetValueOrDefault(local, (byte)3)));
        return new(true, reachable.Aggregate(EmptyTypes, (result, state) => result.Union(state.Pending)),
            records.ToImmutable(), booleans.ToImmutable())
        {
            ExceptionTests = reachable[0].ExceptionTests.Where(pair => reachable.All(state =>
                state.ExceptionTests.TryGetValue(pair.Key, out var predicate) && predicate == pair.Value)).ToImmutableDictionary(),
        };
    }

    public bool Same(MirFlowState left, MirFlowState right) => left.Reachable == right.Reachable &&
        left.Pending.SetEquals(right.Pending) &&
        left.ExceptionTests.Count == right.ExceptionTests.Count && left.ExceptionTests.All(pair =>
            right.ExceptionTests.TryGetValue(pair.Key, out var test) && test == pair.Value) &&
        left.ExceptionRecords.Count == right.ExceptionRecords.Count &&
        left.ExceptionRecords.All(pair => right.ExceptionRecords.TryGetValue(pair.Key, out var types) && pair.Value.SetEquals(types)) &&
        left.Booleans.Count == right.Booleans.Count &&
        left.Booleans.All(pair => right.Booleans.GetValueOrDefault(pair.Key) == pair.Value);

    private static byte Boolean(MirOperand operand, MirFlowState state) => operand switch
    {
        MirConstant { Value: bool value } => value ? (byte)1 : (byte)2,
        MirCopy { Place.Projections.IsEmpty: true } copy => state.Booleans.GetValueOrDefault(copy.Place.Local, (byte)3),
        _ => 3,
    };
    private static ImmutableHashSet<TypeSymbol> Record(MirOperand operand, MirFlowState state) =>
        operand is MirCopy { Place.Projections.IsEmpty: true } copy
            ? state.ExceptionRecords.GetValueOrDefault(copy.Place.Local, Unknown) : Unknown;
    private static bool Matches(TypeSymbol thrown, TypeSymbol caught)
    {
        for (TypeSymbol? type = thrown; type is not null; type = (type as StructTypeSymbol)?.BaseType)
            if (TypeIdentity.AreSame(type, caught)) return true;
        return false;
    }

    private static MirFlowState Kill(MirLocalId local, MirFlowState state) =>
        state with { ExceptionRecords = state.ExceptionRecords.Remove(local), Booleans = state.Booleans.Remove(local), ExceptionTests = state.ExceptionTests.Remove(local) };

    public MirFlowState Statement(MirStatement statement, MirFlowState state)
    {
        if (!state.Reachable) return state;
        if (statement is MirStorageLive live) return Kill(live.Local, state);
        if (statement is MirStorageDead dead) return Kill(dead.Local, state);
        if (statement is MirReleaseException release)
            return release.Abandon ? state : state with
            {
                Pending = release.RestoredRecord is { } restored ? Record(restored, state) : EmptyTypes,
            };
        if (statement is not MirAssign { Destination.Projections.IsEmpty: true } assign) return state;
        MirFlowState result = Kill(assign.Destination.Local, state);
        ImmutableHashSet<TypeSymbol>? record = assign.Value switch
        {
            MirCurrentException => state.Pending.IsEmpty ? Unknown : state.Pending,
            MirUse use => Record(use.Operand, state),
            _ => null,
        };
        if (record is not null && !record.SetEquals(Unknown))
            result = result with { ExceptionRecords = result.ExceptionRecords.SetItem(assign.Destination.Local, record) };
        MirExceptionTest? predicate = assign.Value switch
        {
            MirExceptionMatches { Record: MirCopy { Place.Projections.IsEmpty: true } tested } match => new(tested.Place.Local, match.ExceptionType),
            MirUse { Operand: MirCopy { Place.Projections.IsEmpty: true } copied } => state.ExceptionTests.GetValueOrDefault(copied.Place.Local),
            _ => null,
        };
        if (predicate is not null) result = result with { ExceptionTests = result.ExceptionTests.SetItem(assign.Destination.Local, predicate) };
        byte? boolean = assign.Value switch
        {
            MirUse use when TypeIdentity.AreSame(use.Type, BuiltinTypes.Bool) => Boolean(use.Operand, state),
            MirUnary { Operator: MirUnaryOperator.Not } unary => (byte)(((Boolean(unary.Operand, state) & 1) << 1) |
                ((Boolean(unary.Operand, state) & 2) >> 1)),
            MirExceptionMatches match => Record(match.Record, state).Aggregate((byte)0, (value, type) =>
                (byte)(value | (TypeIdentity.AreSame(type, BuiltinTypes.Error) ? 3 : Matches(type, match.ExceptionType) ? 1 : 2))),
            _ => null,
        };
        if (boolean is { } known && known != 3)
            result = result with { Booleans = result.Booleans.SetItem(assign.Destination.Local, known) };
        return result;
    }

    public MirFlowState Terminator(MirTerminator terminator, MirFlowState state) => state;

    public MirFlowState Edge(MirBasicBlock source, MirEdge edge, MirFlowState state)
    {
        if (!state.Reachable) return state;
        if (source.Terminator is MirSwitch branch && TypeIdentity.AreSame(branch.Value.Type, BuiltinTypes.Bool))
        {
            byte value = Boolean(branch.Value, state), accepted = 0, handled = 0;
            foreach (MirSwitchCase item in branch.Cases)
                if (item.Value.Value is bool boolean)
                {
                    byte mask = boolean ? (byte)1 : (byte)2;
                    handled |= mask;
                    if (item.Target == edge.Target) accepted |= mask;
                }
            if (branch.Otherwise == edge.Target) accepted |= (byte)(3 & ~handled);
            if ((value & accepted) == 0) return Bottom;
            if (branch.Value is MirCopy { Place.Projections.IsEmpty: true } condition &&
                state.ExceptionTests.TryGetValue(condition.Place.Local, out var predicate))
            {
                var possible = state.ExceptionRecords.GetValueOrDefault(predicate.Record, Unknown);
                var narrowed = possible.Where(type => TypeIdentity.AreSame(type, BuiltinTypes.Error) ||
                    ((Matches(type, predicate.Type) ? 1 : 2) & accepted) != 0).ToImmutableHashSet(TypeIdentity.Comparer);
                if (narrowed.IsEmpty) return Bottom;
                state = state with { Pending = narrowed, ExceptionRecords = state.ExceptionRecords.SetItem(predicate.Record, narrowed) };
            }
        }
        if (edge.Kind == MirEdgeKind.Unwind)
        {
            if (source.Terminator is MirIntrinsicCall primitive &&
                primitive.Intrinsic != MirIntrinsicKind.EnsureThreadLocal) return Bottom;
            if (!externalCallsMayUnwind && source.Terminator is MirCall { Callee: MirFunctionOperand { Function.IsExtern: true } }) return Bottom;
            if (callEffects?.Invoke(source.Terminator) is { } dynamicEffects)
            {
                var thrown = dynamicEffects.ToImmutableHashSet(TypeIdentity.Comparer);
                return thrown.IsEmpty ? Bottom : state with { Pending = thrown };
            }
            FunctionSymbol? invoked = source.Terminator switch
            {
                MirCall { Callee: MirFunctionOperand direct } => direct.Function,
                MirDrop drop => drop.Destructor,
                _ => null,
            };
            if (invoked is not null && effects is not null && effects.TryGetValue(invoked, out var errors))
                return errors.Count == 0 ? Bottom : state with { Pending = errors.ToImmutableHashSet(TypeIdentity.Comparer) };
            return source.Terminator switch
            {
                MirThrow { Exception: { } exception } => state with { Pending = EmptyTypes.Add(exception.Type) },
                MirThrow => state with { Pending = state.Pending.IsEmpty ? Unknown : state.Pending },
                _ => state with { Pending = Unknown },
            };
        }
        MirPlace? destination = source.Terminator switch
        {
            MirCall call => call.Destination,
            MirIntrinsicCall call => call.Destination,
            _ => null,
        };
        return destination is { Projections.IsEmpty: true } ? Kill(destination.Local, state) : state;
    }
}