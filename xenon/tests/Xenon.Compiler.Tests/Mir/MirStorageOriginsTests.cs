using System.Collections.Immutable;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirStorageOriginsTests
{
    private static readonly MirSourceInfo Source = MirSourceInfo.Generated;
    private static readonly TypeFactory Types = new();
    private static readonly TypeSymbol Array = Types.ArrayOf(BuiltinTypes.Int);
    private static readonly TypeSymbol Reference = Types.ReferenceTo(Array, false);
    private static readonly Lazy<FunctionSymbol> Symbol = new(() =>
        Compilation.Create(SourceText.From("namespace Flow; void F() {}"))
            .SemanticModel.Functions.Single(function => function.Symbol.Name == "F").Symbol);
    private static MirPlace Place(int id) => new(new(id));
    private static MirCopy Read(int id) => new(Place(id), Array);
    private static MirAssign Store(int id, int value) => new(Place(id), new MirUse(Read(value)), Source);
    private static MirBasicBlock Block(int id, MirTerminator terminator, params MirStatement[] statements) =>
        new(new(id), [.. statements], terminator);
    private static MirFunction Body(params MirBasicBlock[] blocks) => new(Symbol.Value,
        [new(new(0), "first", Array, MirLocalKind.Parameter, Source),
         new(new(1), "second", Array, MirLocalKind.Parameter, Source),
         new(new(2), "local", Array, MirLocalKind.Variable, Source),
         new(new(3), "alias", Reference, MirLocalKind.Variable, Source)],
        [.. blocks], new(0), Source);

    private static MirDataflowResult<ImmutableDictionary<MirLocalId, MirStorageOrigin>> Analyze(MirFunction function) =>
        MirDataflow.Solve(new(function), new MirStorageOrigins(function));

    [Fact]
    public void FullOverwriteKillsOldStorageOrigins()
    {
        MirFunction function = Body(Block(0, new MirReturn(null, Source), Store(2, 0), Store(2, 1)));
        var state = Analyze(function).Output[new(0)];
        Assert.True(state[new(2)].Owners.SetEquals([new MirLocalId(1)]));
    }

    [Fact]
    public void ReferenceAssignmentUpdatesTheAddressedStorage()
    {
        MirFunction function = Body(Block(0, new MirReturn(null, Source), Store(2, 0),
            new MirAssign(Place(3), new MirBorrow(Place(2), MirBorrowKind.Exclusive, Reference), Source),
            new MirAssign(Place(3).Project(new MirDerefProjection()), new MirUse(Read(1)), Source)));
        var state = Analyze(function).Output[new(0)];
        Assert.True(state[new(2)].Owners.SetEquals([new MirLocalId(1)]));
        Assert.Contains(Place(2), state[new(3)].Addresses);
    }

    [Fact]
    public void BranchJoinPreservesEitherPossibleOwner()
    {
        MirFunction function = Body(
            Block(0, new MirSwitch(new MirConstant(true, BuiltinTypes.Bool),
                [new(new(true, BuiltinTypes.Bool), new(1))], new(2), Source)),
            Block(1, new MirGoto(new(3), Source), Store(2, 0)),
            Block(2, new MirGoto(new(3), Source), Store(2, 1)),
            Block(3, new MirReturn(null, Source)));
        Assert.True(Analyze(function).Input[new(3)][new(2)].Owners.SetEquals(
            [new MirLocalId(0), new(1)]));
    }

    [Fact]
    public void FrameRetainsCurrentAllocationAndReferencedLocalAfterOverwrite()
    {
        var callee = new MirRequirementOperand(Symbol.Value, MirGenericOperation.FunctionCall,
            Types.FunctionPointer(BuiltinTypes.Void, [Reference]), []);
        MirFunction function = Body(
            Block(0, new MirIntrinsicCall(MirIntrinsicKind.AllocateStackArray, [], Array,
                Place(2), new(1), new(5), Source with { OriginId = 10 })),
            Block(1, new MirIntrinsicCall(MirIntrinsicKind.AllocateStackArray, [], Array,
                Place(0), new(2), new(5), Source with { OriginId = 20 })),
            Block(2, new MirSuspend(null, new(3), Source), Store(2, 0),
                new MirAssign(Place(3), new MirBorrow(Place(2), MirBorrowKind.Exclusive, Reference), Source)),
            Block(3, new MirCall(callee, [new MirCopy(Place(3), Reference)], null, new(4), new(5), Source)),
            Block(4, new MirReturn(null, Source)), Block(5, new MirResumeUnwind(Source)));
        var frame = new MirFrameAnalysis(function);
        MirSuspensionState suspension = frame.Suspensions[new(2)];
        Assert.True(suspension.Allocations.SetEquals([20]));
        Assert.Contains(new MirLocalId(2), suspension.Live);
        Assert.Contains(new MirLocalId(3), suspension.Live);
        Assert.DoesNotContain(new MirLocalId(1), frame.LiveAcross);
    }
    [Theory]
    [InlineData(MirEdgeKind.Normal)]
    [InlineData(MirEdgeKind.Unwind)]
    public void CallsMayWriteThroughReferenceBeforeEitherExit(MirEdgeKind edge)
    {
        var callee = new MirRequirementOperand(Symbol.Value, MirGenericOperation.FunctionCall,
            Types.FunctionPointer(Array, [Reference, Array]), []);
        MirFunction function = Body(
            Block(0, new MirCall(callee, [new MirCopy(Place(3), Reference), Read(1)],
                Place(0), new(1), new(2), Source), Store(2, 0),
                new MirAssign(Place(3), new MirBorrow(Place(2), MirBorrowKind.Exclusive, Reference), Source)),
            Block(1, new MirReturn(null, Source)), Block(2, new MirResumeUnwind(Source)));
        var state = Analyze(function).Input[new(edge == MirEdgeKind.Normal ? 1 : 2)];
        Assert.Contains(new MirLocalId(1), state[new(2)].Owners);
        Assert.Equal(edge == MirEdgeKind.Normal, state[new(0)].Owners.Contains(new(1)));
    }
}