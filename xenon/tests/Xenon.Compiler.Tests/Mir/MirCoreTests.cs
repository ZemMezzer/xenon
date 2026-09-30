using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirCoreTests
{
    private static readonly MirSourceInfo Source = new(new(SourceText.From("int F() { return 1; }", "mir.xe"), new(10, 6)));
    private static readonly Lazy<Compilation> Symbols = new(() => Compilation.Create(SourceText.From(
        "namespace MirTests; struct Pair { public int x; public int y; } int F() { return 1; } int G(int value) { return value; }")));
    private static FunctionSymbol Function(string name = "F") => Symbols.Value.SemanticModel.Functions.Single(f => f.Symbol.Name == name).Symbol;
    private static MirLocal Local(int id, TypeSymbol? type = null) => new(new(id), $"local{id}", type ?? BuiltinTypes.Int, MirLocalKind.Variable, Source);
    private static MirConstant Int(int value) => new(value, BuiltinTypes.Int);
    private static MirFunction Body(params MirBasicBlock[] blocks) => new(Function(), [Local(0)], [.. blocks], new(0), Source);
    private static MirBasicBlock Block(int id, MirTerminator terminator, params MirStatement[] statements) => new(new(id), [.. statements], terminator);
    private static MirReturn Return() => new(Int(1), Source);

    [Fact]
    public void ValidLoopAndSwitchHaveExplicitSuccessors()
    {
        MirFunction function = Body(
            Block(0, new MirGoto(new(1), Source), new MirStorageLive(new(0), Source), new MirAssign(new(new(0)), new MirUse(Int(0)), Source)),
            Block(1, new MirSwitch(new MirCopy(new(new(0)), BuiltinTypes.Int), [new(Int(4), new(2))], new(1), Source)),
            Block(2, Return(), new MirStorageDead(new(0), Source)));
        Assert.Empty(MirVerifier.Verify(function));
        Assert.Equal([new MirEdge(new(2), MirEdgeKind.Normal), new(new(1), MirEdgeKind.Normal)], function.Blocks[1].Terminator.Successors);
    }

    [Fact]
    public void CallsDropsThrowsAndSuspensionExposeDistinctEdges()
    {
        FunctionSymbol callee = Function("G");
        var target = new MirFunctionOperand(callee, new TypeFactory().FunctionPointer(BuiltinTypes.Int, [BuiltinTypes.Int]));
        MirFunction function = Body(
            Block(0, new MirCall(target, [Int(3)], new(new(0)), new(1), new(4), Source)),
            Block(1, new MirSuspend(new MirCopy(new(new(0)), BuiltinTypes.Int), new(2), Source)),
            Block(2, new MirDrop(new(new(0)), null, new(3), new(4), Source)),
            Block(3, new MirThrow(Int(1), new(4), Source)),
            Block(4, new MirResumeUnwind(Source)),
            Block(5, new MirUnreachable(Source)));
        Assert.Empty(MirVerifier.Verify(function));
        Assert.Equal([MirEdgeKind.Normal, MirEdgeKind.Unwind], function.Blocks[0].Terminator.Successors.Select(e => e.Kind));
        Assert.Equal(MirEdgeKind.Resume, Assert.Single(function.Blocks[1].Terminator.Successors).Kind);
        Assert.Equal(MirEdgeKind.Unwind, Assert.Single(function.Blocks[3].Terminator.Successors).Kind);
        Assert.Empty(function.Blocks[4].Terminator.Successors);
    }

    [Fact]
    public void PlaceIdentityIsStructuralAcrossIndependentlyBuiltProjections()
    {
        MirPlace first = new MirPlace(new(0)).Project(new MirDerefProjection()).Project(new MirIndexProjection(Int(2)));
        MirPlace second = new(new(0), [new MirDerefProjection(), new MirIndexProjection(Int(2))]);
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        Assert.True(new MirPlace(new(0)).IsPrefixOf(first));
        Assert.False(first.IsPrefixOf(new(new(0))));
        Assert.Single(new HashSet<MirPlace> { first, second });
        Assert.NotEqual(first, second.Project(new MirDerefProjection()));
        Assert.NotEqual(first, new MirPlace(new(1), second.Projections));
    }

    [Fact]
    public void FieldDerefAndIndexProjectionsAreTypeChecked()
    {
        StructTypeSymbol pair = Symbols.Value.SemanticModel.GlobalNamespace.Namespaces.Single().Structs.Single();
        FieldSymbol x = pair.Fields.Single(f => f.Name == "x");
        var types = new TypeFactory();
        MirPlace field = new MirPlace(new(0)).Project(new MirDerefProjection()).Project(new MirIndexProjection(Int(0))).Project(new MirFieldProjection(x));
        MirFunction function = Body(Block(0, Return(), new MirAssign(field, new MirUse(Int(2)), Source))) with
        { Locals = [Local(0, types.PointerTo(types.ArrayOf(pair)))] };
        Assert.Empty(MirVerifier.Verify(function));
        Assert.Contains(MirVerifier.Verify(function with { Locals = [Local(0)] }), error => error.Message.Contains("cannot dereference"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(50)]
    public void MissingSuccessorIsRejected(int target)
    {
        Assert.Contains(MirVerifier.Verify(Body(Block(0, new MirGoto(new(target), Source)))), e => e.Message.Contains("successor"));
    }

    [Fact]
    public void MissingEntryAndDuplicateBlockAreRejected()
    {
        Assert.Contains(MirVerifier.Verify(Body(Block(1, Return()))), e => e.Message.Contains("entry"));
        Assert.Contains(MirVerifier.Verify(Body(Block(0, Return()), Block(0, Return()))), e => e.Message.Contains("duplicate block"));
    }

    [Fact]
    public void MissingTerminatorAndUninitializedListsAreDiagnosed()
    {
        Assert.Contains(MirVerifier.Verify(Body(Block(0, null!))), e => e.Message.Contains("no terminator"));
        Assert.Contains(MirVerifier.Verify(Body(Block(0, Return())) with { Locals = default }), e => e.Message.Contains("tables"));
        Assert.Contains(MirVerifier.Verify(Body(new MirBasicBlock(new(0), default, Return()))), e => e.Message.Contains("statement list"));
        Assert.Contains(MirVerifier.Verify(Body(Block(0, new MirSwitch(Int(0), default, new(0), Source)))), e => e.Message.Contains("switch cases"));
    }

    [Fact]
    public void UnknownLocalsAndIncorrectOperandTypesAreRejected()
    {
        Assert.Contains(MirVerifier.Verify(Body(Block(0, new MirReturn(new MirCopy(new(new(20)), BuiltinTypes.Int), Source)))), e => e.Message.Contains("local _20"));
        Assert.Contains(MirVerifier.Verify(Body(Block(0, new MirReturn(new MirMove(new(new(0)), BuiltinTypes.Bool), Source)))), e => e.Message.Contains("move operand"));
        Assert.Contains(MirVerifier.Verify(Body(Block(0, Return())) with { Locals = [Local(0), Local(0)] }), e => e.Message.Contains("duplicate local"));
    }

    [Fact]
    public void AssignmentErrorRetainsFunctionBlockStatementAndSource()
    {
        MirFunction function = Body(Block(0, Return(), new MirAssign(new(new(0)), new MirUse(new MirConstant(true, BuiltinTypes.Bool)), Source)));
        MirVerificationException error = Assert.Throws<MirVerificationException>(() => MirVerifier.VerifyOrThrow(function));
        MirVerificationError diagnostic = Assert.Single(error.Errors);
        Assert.Equal("F", diagnostic.Function);
        Assert.Equal(new MirBlockId(0), diagnostic.Block);
        Assert.Equal(0, diagnostic.Statement);
        Assert.Same(Source, diagnostic.Source);
        Assert.Contains("F/bb0/statement 0", error.Message);
        Assert.Contains("mir.xe:1:11", error.Message);
    }

    [Fact]
    public void ReturnAndSwitchTypesAreValidated()
    {
        Assert.Contains(MirVerifier.Verify(Body(Block(0, new MirReturn(null, Source)))), e => e.Message.Contains("non-void return"));
        Assert.Contains(MirVerifier.Verify(Body(Block(0, new MirSwitch(Int(0), [new(Int(1), new(0)), new(Int(1), new(0))], new(0), Source)))), e => e.Message.Contains("duplicate switch"));
        Assert.Contains(MirVerifier.Verify(Body(Block(0, new MirSwitch(Int(0), [new(new(true, BuiltinTypes.Bool), new(0))], new(0), Source)))), e => e.Message.Contains("switch case:"));
    }

    [Fact]
    public void CallSignatureAndDestinationMustAgree()
    {
        FunctionSymbol callee = Function("G");
        var target = new MirFunctionOperand(callee, new TypeFactory().FunctionPointer(BuiltinTypes.Int, [BuiltinTypes.Int]));
        MirFunction function = Body(Block(0, new MirCall(target, [], new(new(0)), new(1), new(2), Source)), Block(1, Return()), Block(2, new MirResumeUnwind(Source)));
        Assert.Contains(MirVerifier.Verify(function), e => e.Message.Contains("call argument count"));
        Assert.Contains(MirVerifier.Verify(function with { Locals = [Local(0, BuiltinTypes.Bool)] }), e => e.Message.Contains("call destination"));
    }

    [Fact]
    public void BorrowMutabilityAndIndexEvaluationAreValidated()
    {
        var types = new TypeFactory();
        var borrow = new MirBorrow(new(new(0)), MirBorrowKind.Exclusive, types.ReferenceTo(BuiltinTypes.Int, isReadonly: true));
        MirFunction function = Body(Block(0, Return(), new MirAssign(new(new(1)), borrow, Source))) with
        { Locals = [Local(0), Local(1, borrow.Type)] };
        Assert.Contains(MirVerifier.Verify(function), e => e.Message.Contains("mutability"));
        MirPlace index = new MirPlace(new(0)).Project(new MirIndexProjection(new MirMove(new(new(1)), BuiltinTypes.Int)));
        function = Body(Block(0, new MirReturn(new MirCopy(index, BuiltinTypes.Int), Source))) with
        { Locals = [Local(0, types.ArrayOf(BuiltinTypes.Int)), Local(1)] };
        Assert.Contains(MirVerifier.Verify(function), e => e.Message.Contains("index must be a copy or constant"));
    }

    [Fact]
    public void MultidimensionalProjectionEqualityAndRankAreValidated()
    {
        MirPlace first = new MirPlace(new(0)).Project(new MirIndexProjection([Int(1), Int(2)]));
        MirPlace second = new MirPlace(new(0)).Project(new MirIndexProjection([Int(1), Int(2)]));
        Assert.Equal(first, second);
        Assert.Equal(first.GetHashCode(), second.GetHashCode());
        MirFunction function = Body(Block(0, new MirReturn(new MirCopy(first, BuiltinTypes.Int), Source))) with
        { Locals = [Local(0, new TypeFactory().ArrayOf(BuiltinTypes.Int, 2))] };
        Assert.Empty(MirVerifier.Verify(function));
        Assert.Contains(MirVerifier.Verify(function with { Locals = [Local(0, new TypeFactory().ArrayOf(BuiltinTypes.Int))] }), e => e.Message.Contains("rank mismatch"));
    }

    [Fact]
    public void IntrinsicsRejectMalformedSignatures()
    {
        MirFunction function = Body(Block(0, new MirIntrinsicCall(MirIntrinsicKind.CloneValue, [], BuiltinTypes.Int, new(new(0)), new(1), new(2), Source)),
            Block(1, Return()), Block(2, new MirResumeUnwind(Source)));
        Assert.Contains(MirVerifier.Verify(function), e => e.Message.Contains("argument count"));
        function = Body(Block(0, new MirIntrinsicCall(MirIntrinsicKind.ArrayLength, [Int(2)], BuiltinTypes.Int, new(new(0)), new(1), new(2), Source)),
            Block(1, Return()), Block(2, new MirResumeUnwind(Source)));
        Assert.Contains(MirVerifier.Verify(function), e => e.Message.Contains("requires an array"));
    }

    [Fact]
    public void ModelContainsNoExecutableBoundTreePayloads()
    {
        Type[] model = typeof(MirFunction).Assembly.GetTypes().Where(t => t.Namespace == typeof(MirFunction).Namespace).ToArray();
        foreach (Type type in model)
            foreach (var property in type.GetProperties())
                Assert.DoesNotContain("Semantics.Binding.Bound", property.PropertyType.ToString(), StringComparison.Ordinal);
    }
}
