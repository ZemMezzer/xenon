using System.Collections.Immutable;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirDataflowTests
{
    private static readonly MirSourceInfo Source = MirSourceInfo.Generated;
    private static readonly Lazy<Compilation> Symbols = new(() => Compilation.Create(SourceText.From(
        "namespace Flow; struct Pair { public int x; public int y; } int F() { return 0; } int G() { return 1; }")));
    private static MirConstant Int(int value) => new(value, BuiltinTypes.Int);
    private static MirCopy Read(int id) => new(new(new(id)), BuiltinTypes.Int);
    private static MirAssign Assign(int id, int value) => new(new(new(id)), new MirUse(Int(value)), Source);
    private static MirBasicBlock Block(int id, MirTerminator terminator, params MirStatement[] statements) =>
        new(new(id), [.. statements], terminator);
    private static MirFunction Body(params MirBasicBlock[] blocks) => new(
        Symbols.Value.SemanticModel.Functions.Single(f => f.Symbol.Name == "F").Symbol,
        [.. Enumerable.Range(0, 4).Select(id => new MirLocal(new(id), $"local{id}", BuiltinTypes.Int, MirLocalKind.Variable, Source))],
        [.. blocks], new(0), Source);

    [Fact]
    public void ForwardJoinConvergesThroughBackedgeAndStoresStatementStates()
    {
        MirFunction function = Body(
            Block(0, new MirGoto(new(1), Source), Assign(0, 0)),
            Block(1, new MirSwitch(Read(0), [new(Int(0), new(2))], new(3), Source)),
            Block(2, new MirGoto(new(1), Source), Assign(1, 1)),
            Block(3, new MirReturn(Read(1), Source)),
            Block(4, new MirReturn(Int(0), Source), Assign(3, 3)));
        var result = MirDataflow.Solve(new MirControlFlow(function), new Definitions());
        Assert.True(result.Input[new(1)].SetEquals([new MirLocalId(0), new(1), new(2)]));
        Assert.DoesNotContain(new MirBlockId(4), result.Graph.Reachable);
        Assert.Empty(result.Input[new(4)]);
        Assert.DoesNotContain(new MirLocation(new(4), 0), result.Before.Keys);
        Assert.DoesNotContain(new MirLocalId(0), result.Before[new(new(0), 0)]);
        Assert.Contains(new MirLocalId(0), result.After[new(new(0), 0)]);
    }

    [Fact]
    public void BackwardLivenessConvergesThroughLoopAndKillsFullAssignments()
    {
        MirFunction function = Body(
            Block(0, new MirGoto(new(1), Source), Assign(0, 0), Assign(1, 7)),
            Block(1, new MirSwitch(Read(0), [new(Int(0), new(2))], new(3), Source)),
            Block(2, new MirGoto(new(1), Source),
                new MirAssign(new(new(0)), new MirBinary(MirBinaryOperator.Add, Read(0), Int(1), BuiltinTypes.Int), Source)),
            Block(3, new MirReturn(Read(1), Source)));
        var result = MirLiveness.Analyze(function);
        Assert.Empty(result.Input[new(0)]);
        Assert.True(result.Input[new(1)].SetEquals([new MirLocalId(0), new(1)]));
        Assert.DoesNotContain(new MirLocalId(1), result.Before[new(new(0), 1)]);
        Assert.Contains(new MirLocalId(1), result.After[new(new(0), 1)]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CallDestinationIsDefinedOnlyOnItsNormalEdge(bool unwindReadsPrevious)
    {
        FunctionSymbol target = Symbols.Value.SemanticModel.Functions.Single(f => f.Symbol.Name == "G").Symbol;
        var callee = new MirFunctionOperand(target, new TypeFactory().FunctionPointer(BuiltinTypes.Int, []));
        MirFunction function = Body(
            Block(0, new MirCall(callee, [], new(new(0)), new(1), new(2), Source)),
            Block(1, new MirReturn(Read(0), Source)),
            Block(2, new MirReturn(unwindReadsPrevious ? Read(0) : Int(0), Source)));
        Assert.Empty(MirVerifier.Verify(function));
        var result = MirLiveness.Analyze(function);
        Assert.Equal(unwindReadsPrevious, result.Input[new(0)].Contains(new(0)));
        Assert.Equal(2, result.Graph.Successors[new(0)].Length);
    }

    [Fact]
    public void ProjectedWritesKeepAggregateAddressAndIndexLive()
    {
        var types = new TypeFactory();
        MirPlace indexed = new(new(0), [new MirIndexProjection(Read(1))]);
        MirFunction function = Body(Block(0, new MirReturn(Read(2), Source),
            new MirAssign(indexed, new MirUse(Int(9)), Source))) with
        {
            Locals = [
                new(new(0), "array", types.ArrayOf(BuiltinTypes.Int), MirLocalKind.Parameter, Source),
                new(new(1), "index", BuiltinTypes.Int, MirLocalKind.Parameter, Source),
                new(new(2), "result", BuiltinTypes.Int, MirLocalKind.Parameter, Source)],
        };
        Assert.True(MirLiveness.Analyze(function).Input[new(0)].SetEquals(
            [new MirLocalId(0), new(1), new(2)]));
    }

    [Fact]
    public void SuspensionUsesResumeSuccessorAndPayload()
    {
        MirFunction function = Body(
            Block(0, new MirSuspend(Read(0), new(1), Source)),
            Block(1, new MirReturn(Read(1), Source)));
        var result = MirLiveness.Analyze(function);
        Assert.True(result.Input[new(0)].SetEquals([new MirLocalId(0), new(1)]));
        Assert.True(result.Output[new(0)].SetEquals([new MirLocalId(1)]));
        Assert.Equal(MirEdgeKind.Resume, Assert.Single(result.Graph.Successors[new(0)]).Kind);
    }

    [Fact]
    public void SolverHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            MirLiveness.Analyze(Body(Block(0, new MirReturn(Int(0), Source))), cancellation.Token));
    }

    [Theory]
    [InlineData(MirDataflowDirection.Forward)]
    [InlineData(MirDataflowDirection.Backward)]
    public void ReversedCleanupChainPropagatesInLinearTransfers(MirDataflowDirection direction)
    {
        const int count = 512;
        var blocks = new List<MirBasicBlock> { Block(0, new MirGoto(new(count - 1), Source)) };
        blocks.Add(Block(1, new MirReturn(Int(0), Source)));
        for (int id = 2; id < count; id++) blocks.Add(Block(id, new MirGoto(new(id - 1), Source)));
        var analysis = new ReachabilityCounter(direction);
        var result = MirDataflow.Solve(new MirControlFlow(Body([.. blocks])), analysis);
        Assert.All(result.Input.Values, value => Assert.True(value));
        Assert.All(result.Output.Values, value => Assert.True(value));
        Assert.InRange(analysis.Transfers, count, 3 * count);
        Assert.Equal(0, analysis.Joins);
    }

    private sealed class ReachabilityCounter(MirDataflowDirection direction) : IMirDataflowAnalysis<bool>
    {
        public int Transfers { get; private set; }
        public int Joins { get; private set; }
        public MirDataflowDirection Direction => direction;
        public bool Bottom => false;
        public bool Boundary(MirControlFlow graph, MirBasicBlock block) => direction == MirDataflowDirection.Forward
            ? block.Id == graph.Function.Entry : block.Terminator is MirReturn;
        public bool Join(IEnumerable<bool> states) { Joins++; return states.Any(value => value); }
        public bool Same(bool left, bool right) => left == right;
        public bool Statement(MirStatement statement, bool state) => state;
        public bool Terminator(MirTerminator terminator, bool state) { Transfers++; return state; }
        public bool Edge(MirBasicBlock source, MirEdge edge, bool state) => state;
    }
    private sealed class Definitions : IMirDataflowAnalysis<ImmutableHashSet<MirLocalId>>
    {
        public MirDataflowDirection Direction => MirDataflowDirection.Forward;
        public ImmutableHashSet<MirLocalId> Bottom => [];
        public ImmutableHashSet<MirLocalId> Boundary(MirControlFlow graph, MirBasicBlock block) =>
            block.Id == graph.Function.Entry ? [new(2)] : [];
        public ImmutableHashSet<MirLocalId> Join(IEnumerable<ImmutableHashSet<MirLocalId>> states) =>
            states.Aggregate(Bottom, (result, state) => result.Union(state));
        public bool Same(ImmutableHashSet<MirLocalId> a, ImmutableHashSet<MirLocalId> b) => a.SetEquals(b);
        public ImmutableHashSet<MirLocalId> Statement(MirStatement statement, ImmutableHashSet<MirLocalId> state) =>
            statement is MirAssign assign ? state.Add(assign.Destination.Local) : state;
        public ImmutableHashSet<MirLocalId> Terminator(MirTerminator terminator, ImmutableHashSet<MirLocalId> state) => state;
        public ImmutableHashSet<MirLocalId> Edge(MirBasicBlock source, MirEdge edge, ImmutableHashSet<MirLocalId> state) => state;
    }
}
