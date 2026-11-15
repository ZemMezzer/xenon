using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirFlowState(bool Reachable, ImmutableHashSet<TypeSymbol> Pending,
    ImmutableDictionary<MirLocalId, ImmutableHashSet<TypeSymbol>> ExceptionRecords,
    ImmutableDictionary<MirLocalId, byte> Booleans)
{
    public ImmutableDictionary<MirLocalId, MirBooleanAlias> BooleanAliases { get; init; } = ImmutableDictionary<MirLocalId, MirBooleanAlias>.Empty;
    public ImmutableDictionary<MirLocalId, MirLocalId> AddressAliases { get; init; } = ImmutableDictionary<MirLocalId, MirLocalId>.Empty;
    public ImmutableDictionary<MirLocalId, MirOperand> Copies { get; init; } = ImmutableDictionary<MirLocalId, MirOperand>.Empty;
    public ImmutableDictionary<MirLocalId, MirPredicateAlias> Predicates { get; init; } = ImmutableDictionary<MirLocalId, MirPredicateAlias>.Empty;
    public ImmutableDictionary<MirPredicate, byte> PredicateValues { get; init; } = ImmutableDictionary<MirPredicate, byte>.Empty;
    public ImmutableDictionary<MirLocalId, ImmutableHashSet<object>> Constants { get; init; } = ImmutableDictionary<MirLocalId, ImmutableHashSet<object>>.Empty;
    public ImmutableDictionary<MirLocalId, MirExceptionTest> ExceptionTests { get; init; } = ImmutableDictionary<MirLocalId, MirExceptionTest>.Empty;
}
public sealed record MirExceptionTest(MirLocalId Record, TypeSymbol Type);
public sealed record MirBooleanAlias(MirLocalId Root, bool Negated);
public sealed record MirPredicate(MirBinaryOperator Operator, MirOperand Left, MirOperand Right);
public sealed record MirPredicateAlias(MirPredicate Predicate, bool Negated);

/// <summary>
/// Feasible control-flow edges from MIR boolean facts and exception records.
/// Unknown exceptions preserve both catch edges; explicit typed throws can
/// establish a definite match. Facts do not rely on a source control-flow tree.
/// </summary>
public sealed class MirFlowFacts(IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>>? effects = null,
    Func<MirTerminator, IEnumerable<TypeSymbol>?>? callEffects = null, bool externalCallsMayUnwind = false,
    bool precisePointerFacts = false, MirPointerParameterGuard? entryGuard = null,
    IReadOnlyDictionary<FunctionSymbol, ImmutableHashSet<MirLifetimeExit>>? lifetimeExits = null) : IMirDataflowAnalysis<MirFlowState>
{
    private HashSet<MirLocalId>? _constantLocals;
    private HashSet<MirLocalId>? _addressedLocals;
    private static readonly ImmutableHashSet<TypeSymbol> Unknown = ImmutableHashSet.Create<TypeSymbol>(TypeIdentity.Comparer, BuiltinTypes.Error);
    private static readonly ImmutableHashSet<TypeSymbol> EmptyTypes = ImmutableHashSet.Create<TypeSymbol>(TypeIdentity.Comparer);
    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public MirFlowState Bottom { get; } = new(false, EmptyTypes,
        ImmutableDictionary<MirLocalId, ImmutableHashSet<TypeSymbol>>.Empty,
        ImmutableDictionary<MirLocalId, byte>.Empty);
    public MirFlowState Boundary(MirControlFlow graph, MirBasicBlock block)
    {
        if (!precisePointerFacts) return block.Id == graph.Function.Entry ? Bottom with { Reachable = true } : Bottom;
        // Exact scalar facts are restricted to temporaries whose address cannot
        // escape. User variables may be changed through a call's writable alias.
        _addressedLocals ??= graph.Function.Blocks.SelectMany(block => block.Statements)
            .OfType<MirAssign>().Select(assign => assign.Value).OfType<MirBorrow>().Select(borrow => borrow.Place.Local).ToHashSet();
        _constantLocals ??= graph.Function.Locals.Where(local => local.Kind == MirLocalKind.Temporary)
            .Select(local => local.Id).Except(_addressedLocals).ToHashSet();
        if (block.Id != graph.Function.Entry) return Bottom;
        var boundary = Bottom with { Reachable = true };
        for (var guard = entryGuard; guard is not null; guard = guard.Next)
        {
            var parameter = graph.Function.Locals.FirstOrDefault(local => local.Variable is ParameterSymbol symbol && symbol.Ordinal == guard.Parameter);
            if (parameter is not null)
            {
                var operand = new MirCopy(new(parameter.Id), parameter.Type);
                boundary = guard.Operator is { } op ? AssumeComparison(operand, op, guard.Constant!, guard.Value, boundary) : Assume(operand, guard.Value, boundary);
            }
        }
        return boundary;
    }

    public static MirControlFlow Graph(MirFunction function, CancellationToken cancellation = default,
        IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>>? effects = null, bool externalCallsMayUnwind = false,
        bool precisePointerFacts = false, MirPointerParameterGuard? entryGuard = null,
        IReadOnlyDictionary<FunctionSymbol, ImmutableHashSet<MirLifetimeExit>>? lifetimeExits = null)
    {
        var analysis = new MirFlowFacts(effects, externalCallsMayUnwind: externalCallsMayUnwind, precisePointerFacts: precisePointerFacts, entryGuard: entryGuard, lifetimeExits: lifetimeExits);
        var facts = MirDataflow.Solve(new MirControlFlow(function), analysis, cancellation);
        return new(function, (block, edge) => analysis.Edge(block, edge, facts.Output[block.Id]).Reachable);
    }

    public MirFlowState Join(IEnumerable<MirFlowState> states)
    {
        MirFlowState[] reachable = states.Where(state => state.Reachable).ToArray();
        if (reachable.Length == 0) return Bottom;
        if (reachable.Length == 1) return reachable[0];
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
            BooleanAliases = reachable[0].BooleanAliases.Where(pair => reachable.All(state =>
                state.BooleanAliases.TryGetValue(pair.Key, out var value) && pair.Value == value)).ToImmutableDictionary(),
            AddressAliases = reachable[0].AddressAliases.Where(pair => reachable.All(state =>
                state.AddressAliases.TryGetValue(pair.Key, out var value) && pair.Value == value)).ToImmutableDictionary(),
            Copies = reachable[0].Copies.Where(pair => reachable.All(state =>
                state.Copies.TryGetValue(pair.Key, out var value) && pair.Value == value)).ToImmutableDictionary(),
            Predicates = reachable[0].Predicates.Where(pair => reachable.All(state =>
                state.Predicates.TryGetValue(pair.Key, out var value) && pair.Value == value)).ToImmutableDictionary(),
            PredicateValues = reachable[0].PredicateValues.Where(pair => reachable.All(state =>
                state.PredicateValues.TryGetValue(pair.Key, out var value) && pair.Value == value)).ToImmutableDictionary(),
            Constants = reachable[0].Constants.Keys.Where(key => reachable.All(state => state.Constants.ContainsKey(key)))
                .ToImmutableDictionary(key => key, key => reachable.Select(state => state.Constants[key])
                    .Aggregate(ImmutableHashSet<object>.Empty, (values, next) => values.Union(next))),
            ExceptionTests = reachable[0].ExceptionTests.Where(pair => reachable.All(state =>
                state.ExceptionTests.TryGetValue(pair.Key, out var predicate) && predicate == pair.Value)).ToImmutableDictionary(),
        };
    }

    public bool Same(MirFlowState left, MirFlowState right) => left.Reachable == right.Reachable &&
        left.BooleanAliases.Count == right.BooleanAliases.Count && left.BooleanAliases.All(pair =>
            right.BooleanAliases.TryGetValue(pair.Key, out var value) && pair.Value == value) &&
        left.AddressAliases.Count == right.AddressAliases.Count && left.AddressAliases.All(pair =>
            right.AddressAliases.TryGetValue(pair.Key, out var value) && pair.Value == value) &&
        left.Copies.Count == right.Copies.Count && left.Copies.All(pair => right.Copies.TryGetValue(pair.Key, out var value) && pair.Value == value) &&
        left.Predicates.Count == right.Predicates.Count && left.Predicates.All(pair => right.Predicates.TryGetValue(pair.Key, out var value) && pair.Value == value) &&
        left.PredicateValues.Count == right.PredicateValues.Count && left.PredicateValues.All(pair => right.PredicateValues.TryGetValue(pair.Key, out var value) && pair.Value == value) &&
        left.Constants.Count == right.Constants.Count && left.Constants.All(pair =>
            right.Constants.TryGetValue(pair.Key, out var value) && pair.Value.SetEquals(value)) &&
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
    internal static bool? KnownBoolean(MirOperand operand, MirFlowState state) => Boolean(operand, state) switch
    { 1 => true, 2 => false, _ => null };
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
        state with { BooleanAliases = state.BooleanAliases.RemoveRange(state.BooleanAliases.Where(pair => pair.Key == local || pair.Value.Root == local).Select(pair => pair.Key)),
            AddressAliases = state.AddressAliases.Remove(local),
            Copies = state.Copies.RemoveRange(state.Copies.Where(pair => pair.Key == local || Uses(pair.Value, local)).Select(pair => pair.Key)),
            Predicates = state.Predicates.RemoveRange(state.Predicates.Where(pair => pair.Key == local || Uses(pair.Value.Predicate, local)).Select(pair => pair.Key)),
            PredicateValues = state.PredicateValues.RemoveRange(state.PredicateValues.Keys.Where(predicate => Uses(predicate, local))),
            Constants = state.Constants.Remove(local), ExceptionRecords = state.ExceptionRecords.Remove(local), Booleans = state.Booleans.Remove(local), ExceptionTests = state.ExceptionTests.Remove(local) };

    private static bool Uses(MirOperand operand, MirLocalId local) => operand is MirCopy copy && copy.Place.Local == local;
    private static bool Uses(MirPredicate predicate, MirLocalId local) => Uses(predicate.Left, local) || Uses(predicate.Right, local);
    private static MirOperand Copied(MirOperand operand, MirFlowState state) => operand is MirCopy { Place.Projections.IsEmpty: true } copy
        ? state.Copies.GetValueOrDefault(copy.Place.Local, operand) : operand;

    private static ImmutableHashSet<object>? ConstantValues(MirOperand operand, MirFlowState state) => operand switch
    {
        MirConstant { Value: { } value } => ImmutableHashSet.Create(value),
        MirCopy { Place.Projections.IsEmpty: true } copy => state.Constants.GetValueOrDefault(copy.Place.Local) ??
            (state.Copies.GetValueOrDefault(copy.Place.Local) is MirConstant { Value: { } constant } ? ImmutableHashSet.Create(constant) : null),
        _ => null,
    };
    private static object? Constant(MirOperand operand, MirFlowState state) =>
        ConstantValues(operand, state) is { Count: 1 } values ? values.Single() : null;
    internal static MirConstant? KnownConstant(MirOperand operand, MirFlowState state) =>
        KnownBoolean(operand, state) is { } boolean ? new(boolean, operand.Type) :
        Constant(operand, state) is { } value ? new(value, operand.Type) : null;

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
        if (statement is not MirAssign assign) return state;
        if (assign.Destination.Projections is [MirDerefProjection] && state.AddressAliases.TryGetValue(assign.Destination.Local, out var addressed))
            assign = assign with { Destination = new(addressed) };
        if (!assign.Destination.Projections.IsEmpty) return state;
        MirFlowState result = Kill(assign.Destination.Local, state);
        if (precisePointerFacts && assign.Value is MirUse copyValue && copyValue.Type is PrimitiveTypeSymbol or PointerTypeSymbol &&
            Copied(copyValue.Operand, state) is { } copiedValue &&
            copiedValue is MirConstant or MirCopy { Place.Projections.IsEmpty: true } && !Uses(copiedValue, assign.Destination.Local))
            result = result with { Copies = result.Copies.SetItem(assign.Destination.Local, copiedValue) };
        MirLocalId? addressAlias = !precisePointerFacts ? null : assign.Value switch
        {
            MirBorrow { Place.Projections.IsEmpty: true } address => address.Place.Local,
            MirUse { Operand: MirCopy { Place.Projections.IsEmpty: true } copy } when state.AddressAliases.TryGetValue(copy.Place.Local, out var root) => root,
            MirCast { Operand: MirCopy { Place.Projections.IsEmpty: true } copy } when state.AddressAliases.TryGetValue(copy.Place.Local, out var root) => root,
            _ => null,
        };
        if (addressAlias is { } addressRoot) result = result with { AddressAliases = result.AddressAliases.SetItem(assign.Destination.Local, addressRoot) };
        MirCopy? booleanCopy = !precisePointerFacts ? null : assign.Value switch
        {
            MirUse { Operand: MirCopy copy } when TypeIdentity.AreSame(assign.Value.Type, BuiltinTypes.Bool) => copy,
            MirUnary { Operator: MirUnaryOperator.Not, Operand: MirCopy copy } => copy,
            _ => null,
        };
        if (booleanCopy is { Place.Projections.IsEmpty: true })
        {
            var alias = state.BooleanAliases.GetValueOrDefault(booleanCopy.Place.Local, new(booleanCopy.Place.Local, false));
            if (alias.Root != assign.Destination.Local)
                result = result with { BooleanAliases = result.BooleanAliases.SetItem(assign.Destination.Local,
                    alias with { Negated = alias.Negated ^ assign.Value is MirUnary }) };
        }
        if (_constantLocals?.Contains(assign.Destination.Local) == true && assign.Value is MirUse constantUse && ConstantValues(constantUse.Operand, state) is { } constant)
            result = result with { Constants = result.Constants.SetItem(assign.Destination.Local, constant) };
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
            MirBinary binary when precisePointerFacts && Constant(binary.Left, state) is { } left && Constant(binary.Right, state) is { } right &&
                MirPointerConstants.Binary(binary, left, right) is bool truth => truth ? (byte)1 : (byte)2,
            MirExceptionMatches match => Record(match.Record, state).Aggregate((byte)0, (value, type) =>
                (byte)(value | (TypeIdentity.AreSame(type, BuiltinTypes.Error) ? 3 : Matches(type, match.ExceptionType) ? 1 : 2))),
            _ => null,
        };
        if (boolean is { } known && known != 3)
            result = result with { Booleans = result.Booleans.SetItem(assign.Destination.Local, known) };
        if (precisePointerFacts && assign.Value is MirBinary { Operator: MirBinaryOperator.Equal or MirBinaryOperator.NotEqual or MirBinaryOperator.Less or
            MirBinaryOperator.GreaterOrEqual or MirBinaryOperator.Greater or MirBinaryOperator.LessOrEqual } comparison &&
            (comparison.Operator is MirBinaryOperator.Equal or MirBinaryOperator.NotEqual || TypeFacts.IsInteger(comparison.Left.Type)))
        {
            var op = comparison.Operator switch
            {
                MirBinaryOperator.NotEqual => MirBinaryOperator.Equal,
                MirBinaryOperator.GreaterOrEqual => MirBinaryOperator.Less,
                MirBinaryOperator.LessOrEqual => MirBinaryOperator.Greater,
                _ => comparison.Operator,
            };
            var comparisonPredicate = new MirPredicate(op, Copied(comparison.Left, state), Copied(comparison.Right, state));
            // A projected load may change without a local assignment. Only exact
            // local snapshots and constants participate in repeated predicates.
            if (comparisonPredicate.Left is MirConstant or MirCopy { Place.Projections.IsEmpty: true } &&
                comparisonPredicate.Right is MirConstant or MirCopy { Place.Projections.IsEmpty: true })
            {
                bool negated = op != comparison.Operator;
                result = result with { Predicates = result.Predicates.SetItem(assign.Destination.Local, new(comparisonPredicate, negated)) };
                if (state.PredicateValues.TryGetValue(comparisonPredicate, out byte truth))
                    result = result with { Booleans = result.Booleans.SetItem(assign.Destination.Local, negated ? (byte)(3 ^ truth) : truth) };
            }
        }
        return result;
    }

    public MirFlowState Terminator(MirTerminator terminator, MirFlowState state) => state;

    internal MirFlowState Assume(MirOperand condition, bool value, MirFlowState state)
    {
        var yes = new MirBlockId(-1001);
        var no = new MirBlockId(-1002);
        var branch = new MirSwitch(condition, [new(new MirConstant(true, BuiltinTypes.Bool), yes)], no, default!);
        return Edge(new(new(-1000), [], branch), new(value ? yes : no, MirEdgeKind.Normal), state);
    }

    internal MirFlowState AssumeComparison(MirOperand left, MirBinaryOperator op, object right, bool value, MirFlowState state)
    {
        var local = new MirLocalId(-1000000);
        state = Statement(new MirAssign(new(local), new MirBinary(op, left, new MirConstant(right, left.Type), BuiltinTypes.Bool), default!), state);
        return Assume(new MirCopy(new(local), BuiltinTypes.Bool), value, state);
    }

    public MirFlowState Edge(MirBasicBlock source, MirEdge edge, MirFlowState state)
    {
        if (!state.Reachable) return state;
        ImmutableHashSet<TypeSymbol>? lifetimeErrors = null;
        if (source.Terminator is MirCall { IsVirtual: false, InterfaceType: null, Callee: MirFunctionOperand { Function: { IsVirtual: false, IsOverride: false } target } } contractedCall &&
            lifetimeExits is not null && lifetimeExits.TryGetValue(target, out var exits))
        {
            var alternatives = new List<MirFlowState>();
            var errors = ImmutableHashSet.CreateBuilder<TypeSymbol>(TypeIdentity.Comparer);
            foreach (var contract in exits.Where(contract => contract.Exit == edge.Kind))
            {
                var candidate = state;
                for (var guard = contract.Guard; guard is not null && candidate.Reachable; guard = guard.Next)
                    candidate = guard.Operator is { } op ? AssumeComparison(contractedCall.Arguments[guard.Parameter], op, guard.Constant!, guard.Value, candidate) :
                        Assume(contractedCall.Arguments[guard.Parameter], guard.Value, candidate);
                if (!candidate.Reachable) continue;
                if (contract.Result is { } result && contractedCall.Destination is { } resultDestination)
                    candidate = Statement(new MirAssign(resultDestination, new MirUse(result), contractedCall.Source), candidate);
                alternatives.Add(candidate);
                errors.Add(contract.ExceptionType ?? BuiltinTypes.Error);
            }
            if (alternatives.Count == 0) return Bottom;
            state = Join(alternatives);
            lifetimeErrors = errors.ToImmutable();
        }
        // Cleanup dispatchers use exact integer selectors as well as booleans.
        // Sets preserve alternatives across cleanup selector joins. Only source
        // literals/copies enter the sets: arithmetic never grows the lattice.
        if (source.Terminator is MirSwitch constantBranch && ConstantValues(constantBranch.Value, state) is { } constants)
        {
            if (!constants.Any(constant => (constantBranch.Cases.FirstOrDefault(item => Equals(item.Value.Value, constant))?.Target ?? constantBranch.Otherwise) == edge.Target)) return Bottom;
        }
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
            if (precisePointerFacts && branch.Value is MirCopy { Place.Projections.IsEmpty: true } tested && accepted is 1 or 2)
            {
                var alias = state.BooleanAliases.GetValueOrDefault(tested.Place.Local, new(tested.Place.Local, false));
                byte rootValue = alias.Negated ? (byte)(3 ^ accepted) : accepted;
                var booleans = state.Booleans.SetItem(tested.Place.Local, accepted).SetItem(alias.Root, rootValue);
                foreach (var (local, related) in state.BooleanAliases.Where(pair => pair.Value.Root == alias.Root))
                    booleans = booleans.SetItem(local, related.Negated ? (byte)(3 ^ rootValue) : rootValue);
                state = state with { Booleans = booleans };
                if (state.Predicates.TryGetValue(alias.Root, out var comparisonFact))
                    state = state with { PredicateValues = state.PredicateValues.SetItem(comparisonFact.Predicate,
                        comparisonFact.Negated ? (byte)(3 ^ rootValue) : rootValue) };
            }
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
        if (source.Terminator is MirCall && _addressedLocals is not null)
            foreach (var local in _addressedLocals) state = Kill(local, state);
        if (precisePointerFacts && edge.Kind == MirEdgeKind.Normal && source.Terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Swap,
            Arguments: [MirCopy leftAddress, MirCopy rightAddress] } swap)
        {
            if (state.AddressAliases.TryGetValue(leftAddress.Place.Local, out var left) &&
                state.AddressAliases.TryGetValue(rightAddress.Place.Local, out var right) &&
                leftAddress.Type is PointerTypeSymbol leftPointer && rightAddress.Type is PointerTypeSymbol rightPointer)
            {
                var leftValue = Snapshot(left, leftPointer.ElementType);
                var rightValue = Snapshot(right, rightPointer.ElementType);
                state = Statement(new MirAssign(new(left), new MirUse(rightValue), swap.Source), state);
                state = Statement(new MirAssign(new(right), new MirUse(leftValue), swap.Source), state);
            }
            else if (_addressedLocals is not null)
                foreach (var local in _addressedLocals) state = Kill(local, state);
        }
        if (edge.Kind == MirEdgeKind.Unwind)
        {
            if (lifetimeErrors is not null) return state with { Pending = lifetimeErrors };
            if (source.Terminator is MirIntrinsicCall primitive &&
                primitive.Intrinsic != MirIntrinsicKind.EnsureThreadLocal && primitive.AllocationReleases.IsEmpty && primitive.Function?.AllocationOperation != AllocationOperation.Allocate) return Bottom;
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

        MirOperand Snapshot(MirLocalId local, TypeSymbol type)
        {
            var copy = new MirCopy(new(local), type);
            if (TypeIdentity.AreSame(type, BuiltinTypes.Bool) && Boolean(copy, state) is 1 or 2)
                return new MirConstant(Boolean(copy, state) == 1, type);
            if (Constant(copy, state) is { } constant) return new MirConstant(constant, type);
            return Copied(copy, state) is MirConstant literal ? literal : new MirDeferredConstant(type);
        }
    }
}
