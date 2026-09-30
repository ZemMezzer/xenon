using System.Collections.Immutable;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirMoveAnalysisTests
{
    private static readonly MirSourceInfo Source = MirSourceInfo.Generated;
    private static readonly Lazy<Compilation> Symbols = new(() => Compilation.Create(SourceText.From(
        "namespace Flow; struct Pair { public int x; public int y; } void F(Pair value) {}")));
    private static FunctionSymbol Symbol => Symbols.Value.SemanticModel.Functions.Single(f => f.Symbol.Name == "F").Symbol;
    private static MirPlace Place(int id) => new(new(id));
    private static MirPlace Field(string name) => Place(0).Project(new MirFieldProjection(
        ((StructTypeSymbol)Symbol.Parameters[0].Type).AllInstanceFields.Single(field => field.Name == name)));
    private static MirAssign Int(MirPlace place, int value) =>
        new(place, new MirUse(new MirConstant(value, BuiltinTypes.Int)), Source);
    private static MirBasicBlock Block(int id, MirTerminator terminator, params MirStatement[] statements) =>
        new(new(id), [.. statements], terminator);
    private static MirFunction Body(params MirBasicBlock[] blocks) => new(Symbol,
        [new(new(0), "value", Symbol.Parameters[0].Type, MirLocalKind.Parameter, Source),
         new(new(1), "local", BuiltinTypes.Int, MirLocalKind.Variable, Source)],
        [.. blocks], new(0), Source);
    private static MirGoto Go(int id) => new(new(id), Source);
    private static MirReturn Return() => new(null, Source);
    private static MirSwitch Branch(int first, int second) =>
        new(new MirConstant(true, BuiltinTypes.Bool), [new(new(true, BuiltinTypes.Bool), new(first))], new(second), Source);
    private static MirForget Move(MirPlace place) => new(place, Source);

    [Fact]
    public void ParameterStartsInitializedAndLocalStartsUninitialized()
    {
        MirFunction function = Body(Block(0, Return()));
        var states = MirMoveAnalysis.Analyze(function);
        var analysis = new MirMoveAnalysis(function);
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(Place(0), states.Input[new(0)]));
        Assert.Equal(MirOwnershipStatus.Uninitialized, analysis.Status(Place(1), states.Input[new(0)]));
    }

    [Fact]
    public void FieldMoveLeavesItsSiblingUsableAndReinitializationRepairsAggregate()
    {
        MirFunction function = Body(Block(0, Return(), Move(Field("x")), Int(Field("x"), 42)));
        var states = MirMoveAnalysis.Analyze(function);
        var analysis = new MirMoveAnalysis(function);
        var moved = states.After[new(new(0), 0)];
        Assert.Equal(MirOwnershipStatus.PartiallyMoved, analysis.Status(Place(0), moved));
        Assert.Equal(MirOwnershipStatus.Moved, analysis.Status(Field("x"), moved));
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(Field("y"), moved));
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(Place(0), states.Output[new(0)]));
    }

    [Fact]
    public void ConditionalWholeMoveIsDifferentFromPartialMove()
    {
        MirFunction function = Body(
            Block(0, Branch(1, 2)), Block(1, Go(2), Move(Place(0))), Block(2, Return()));
        var states = MirMoveAnalysis.Analyze(function);
        Assert.Equal(MirOwnershipStatus.MaybeMoved,
            new MirMoveAnalysis(function).Status(Place(0), states.Input[new(2)]));
    }

    [Fact]
    public void MovingDifferentFieldsOnDifferentBranchesStillLeavesAPartialValue()
    {
        MirFunction function = Body(
            Block(0, Branch(1, 2)), Block(1, Go(3), Move(Field("x"))),
            Block(2, Go(3), Move(Field("y"))), Block(3, Return()));
        Assert.Equal(MirOwnershipStatus.PartiallyMoved,
            new MirMoveAnalysis(function).Status(Place(0), MirMoveAnalysis.Analyze(function).Input[new(3)]));
    }

    [Fact]
    public void ConditionalAssignmentDoesNotDefinitelyInitializeLocal()
    {
        MirFunction function = Body(
            Block(0, Branch(1, 2)), Block(1, Go(2), Int(Place(1), 7)), Block(2, Return()));
        Assert.Equal(MirOwnershipStatus.MaybeInitialized,
            new MirMoveAnalysis(function).Status(Place(1), MirMoveAnalysis.Analyze(function).Input[new(2)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BackedgeCarriesMovesUnlessLoopReinitializesTheValue(bool reinitialize)
    {
        MirStatement[] body = reinitialize
            ? [Move(Field("x")), Int(Field("x"), 1)] : [Move(Field("x"))];
        MirFunction function = Body(Block(0, Go(1)), Block(1, Branch(2, 3)),
            Block(2, Go(1), body), Block(3, Return()));
        var states = MirMoveAnalysis.Analyze(function);
        Assert.Equal(reinitialize ? MirOwnershipStatus.Initialized : MirOwnershipStatus.MaybeMoved,
            new MirMoveAnalysis(function).Status(Field("x"), states.Input[new(1)]));
    }

    [Fact]
    public void CallResultInitializesOnlyItsNormalEdge()
    {
        var callee = new MirRequirementOperand(Symbol, MirGenericOperation.FunctionCall,
            new TypeFactory().FunctionPointer(BuiltinTypes.Int, []), []);
        MirFunction function = Body(
            Block(0, new MirCall(callee, [], Place(1), new(1), new(2), Source)),
            Block(1, Return()), Block(2, new MirResumeUnwind(Source)));
        var states = MirMoveAnalysis.Analyze(function);
        var analysis = new MirMoveAnalysis(function);
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(Place(1), states.Input[new(1)]));
        Assert.Equal(MirOwnershipStatus.Uninitialized, analysis.Status(Place(1), states.Input[new(2)]));
    }
    [Fact]
    public void EmptyStorageWrapperIsInitializedWhileItsPayloadIsNot()
    {
        StorageTypeSymbol storage = new TypeFactory().StorageOf(BuiltinTypes.Int);
        MirFunction function = Body(Block(0, Return(),
            new MirAssign(Place(1), new MirDefault(storage), Source),
            new MirSetStorageState(Place(1), true, Source),
            new MirSetStorageState(Place(1), false, Source))) with
        {
            Locals = [new(new(1), "storage", storage, MirLocalKind.Variable, Source)],
        };
        var analysis = new MirMoveAnalysis(function);
        var states = MirMoveAnalysis.Analyze(function);
        MirPlace payload = Place(1).Project(new MirLifetimeProjection());
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(Place(1), states.After[new(new(0), 0)]));
        Assert.Equal(MirOwnershipStatus.Uninitialized, analysis.Status(payload, states.After[new(new(0), 0)]));
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(payload, states.After[new(new(0), 1)]));
        Assert.Equal(MirOwnershipStatus.Uninitialized, analysis.Status(payload, states.After[new(new(0), 2)]));
    }

    [Fact]
    public void ReservedFieldMovePreventsAggregateUseAndRollsBackOnUnwind()
    {
        var callee = new MirRequirementOperand(Symbol, MirGenericOperation.FunctionCall,
            new TypeFactory().FunctionPointer(BuiltinTypes.Int, []), []);
        MirFunction function = Body(
            Block(0, new MirCall(callee, [], null, new(1), new(2), Source),
                new MirAssign(Place(1), new MirUse(new MirCopy(Field("x"), BuiltinTypes.Int)), Source)
                    { ReservedMove = Field("x") }),
            Block(1, Return(), Move(Field("x"))),
            Block(2, new MirResumeUnwind(Source)));
        var analysis = new MirMoveAnalysis(function);
        var states = MirMoveAnalysis.Analyze(function);
        Assert.Equal(MirOwnershipStatus.PartiallyMoved, analysis.Status(Place(0), states.Output[new(0)]));
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(Field("y"), states.Output[new(0)]));
        Assert.Equal(MirOwnershipStatus.PartiallyMoved, analysis.Status(Place(0), states.Output[new(1)]));
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(Place(0), states.Input[new(2)]));
        Assert.Equal(MirOwnershipStatus.Initialized, analysis.Status(Field("x"), states.Input[new(2)]));
    }
}