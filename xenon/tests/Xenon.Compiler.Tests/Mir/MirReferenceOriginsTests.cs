using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirReferenceOriginsTests
{
    private static MirReferenceOrigin[] Origins(string source)
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; " + source, "mir-references.xe"));
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        return new MirReferenceOrigins(MirLowerer.Lower(function, compilation.TypeFactory,
            compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true)).Returns().ToArray();
    }

    [Theory]
    [InlineData("int& Use(int& value) { return value; }")]
    [InlineData("int& Use(int& value) { int& alias = value; return alias; }")]
    [InlineData("int& Forward(int& value) { return value; } int& Use(int& value) { return Forward(value); }")]
    public void ReferenceParameterSurvivesCopiesAndCalls(string source)
    {
        Assert.Equal(new MirReferenceOrigin(MirReferenceOriginKind.Parameter, 0), Assert.Single(Origins(source)));
    }

    [Theory]
    [InlineData("int& Use(int* pointer) { return *pointer; }")]
    [InlineData("int& Use() { int value = 1; int* pointer = &value; return *pointer; }")]
    [InlineData("int& Use(unique<int> pointer) { return *pointer; }")]
    public void PointerPointeeLifetimeRemainsUnknown(string source)
    {
        Assert.Equal(ReferenceReturnOriginKind.Unknown, Assert.Single(Origins(source)).Contract.Kind);
    }

    [Fact]
    public void BranchJoinPreservesBothOrigins()
    {
        var origins = Origins("int& Use(int& left, int& right, bool branch) { if (branch) return left; return right; }");
        Assert.Equal(2, origins.Length);
        Assert.Contains(new MirReferenceOrigin(MirReferenceOriginKind.Parameter, 0), origins);
        Assert.Contains(new MirReferenceOrigin(MirReferenceOriginKind.Parameter, 1), origins);
    }

    [Fact]
    public void ConstantBranchExcludesUnreachableOrigin()
    {
        Assert.Equal(new MirReferenceOrigin(MirReferenceOriginKind.Parameter, 0), Assert.Single(Origins(
            "int& Use(int& left, int& right) { if (true) return left; return right; }")));
    }

    [Fact]
    public void LocalReturnRetainsStackOrigin()
    {
        Assert.Equal(MirReferenceOriginKind.Local, Assert.Single(Origins("int& Use() { int value = 1; return value; }")).Kind);
    }

    [Fact]
    public void FieldProjectionSurvivesReturn()
    {
        Assert.Equal(new MirReferenceOrigin(MirReferenceOriginKind.Parameter, 0, "0"), Assert.Single(Origins(
            "struct Pair { public int Value; } int& Use(Pair& pair) { return pair.Value; }")));
    }

    [Fact]
    public void StaticStorageHasNoStackOrigin()
    {
        Assert.Equal(MirReferenceOriginKind.Static, Assert.Single(Origins(
            "static struct State { public static int Value; } int& Use() { return State.Value; }")).Kind);
    }
    [Theory]
    [InlineData("Box Use(int& value) { return Box(value); }", "0")]
    [InlineData("Box Use(Box value) { return move value; }", "0")]
    [InlineData("struct Outer { public Box Inner; public Outer(int& value) { Inner = Box(value); } } Outer Use(int& value) { return Outer(value); }", "0/0")]
    public void AggregateResultExportsReferenceField(string source, string path)
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; struct Box { public int& Value; public Box(int& value) { Value = value; } } " + source));
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var origins = new MirReferenceOrigins(MirLowerer.Lower(function, compilation.TypeFactory,
            compilation.SemanticModel.ExpressionLocations)).ReferenceFields();
        var origin = Assert.Single(origins);
        Assert.Equal(path, string.Join('/', origin.FieldOrdinals));
        Assert.Equal(ReferenceReturnOriginKind.Parameter, origin.Origin.Kind);
        Assert.Equal(0, origin.Origin.ParameterOrdinal);
    }

    [Fact]
    public void ConstructorExportsStoredReference()
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; struct Box { public readonly int& Value; public Box(readonly int& value) { Value = value; } }"));
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.FunctionKind == FunctionKind.Constructor);
        var origins = new MirReferenceOrigins(MirLowerer.Lower(function, compilation.TypeFactory,
            compilation.SemanticModel.ExpressionLocations)).ReferenceFields();
        var origin = Assert.Single(origins);
        Assert.Equal("0", string.Join('/', origin.FieldOrdinals));
        Assert.Equal(ReferenceReturnOriginKind.Parameter, origin.Origin.Kind);
        Assert.Equal(0, origin.Origin.ParameterOrdinal);
        Assert.True(origin.IsReadonly);
    }

    [Fact]
    public void ReturnedReferenceFollowsReferenceFieldPayload()
    {
        Assert.Equal(new MirReferenceOrigin(MirReferenceOriginKind.Parameter, 0), Assert.Single(Origins(
            "struct Box { public int& Value; public Box(int& value) { Value = value; } } int& Read(Box value) { return value.Value; } int& Use(int& value) { return Read(Box(value)); }")));
    }
    [Fact]
    public void ClosureResultPreservesReadonlyBorrowInItsContract()
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; function int() Use(readonly int& value) { return [value]() => { return value; }; }"));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var origin = Assert.Single(function.Symbol.ReferenceFieldOrigins);
        Assert.Equal(ReferenceReturnOriginKind.Parameter, origin.Origin.Kind);
        Assert.Equal(0, origin.Origin.ParameterOrdinal);
        Assert.True(origin.IsReadonly);
    }
    [Theory]
    [InlineData("shared<int> Use() { return new int(); }", SharedReturnOriginKind.Fresh, -1)]
    [InlineData("shared<int> Use(shared<int> value) { return value; }", SharedReturnOriginKind.Parameter, 0)]
    [InlineData("shared<int> Use(weak<int> value) { return lock value; }", SharedReturnOriginKind.Parameter, 0)]
    [InlineData("shared<int> Create() { return new int(); } shared<int> Use() { return Create(); }", SharedReturnOriginKind.Fresh, -1)]
    public void SharedIdentityContractComesFromMir(string source, SharedReturnOriginKind kind, int parameter)
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; " + source));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        Assert.Equal(new SharedReturnOrigin(kind, parameter), Assert.Single(function.Symbol.SharedReturnOrigins));
    }
}