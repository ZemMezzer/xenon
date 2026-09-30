using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirBorrowAnalysisTests
{
    private static MirBorrowViolation[] Check(string body, string parameters = "")
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; void Use(" + parameters + ") { " + body + " }", "mir-borrow.xe"));
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        return new MirBorrowAnalysis(MirLowerer.Lower(function, compilation.TypeFactory,
            compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true)).Check().ToArray();
    }
    [Fact]
    public void DirectReadConflictsWithLiveExclusiveBorrow()
    {
        Assert.Contains(Check("int value = 1; int& alias = value; int copy = value; alias = 2;"), violation => violation.Check == MirBorrowCheck.Read);
    }
    [Fact]
    public void MutationConflictsWithLiveReadonlyBorrow()
    {
        Assert.Contains(Check("int value = 1; readonly int& alias = value; value = 2; int copy = alias;"), violation => violation.Check == MirBorrowCheck.Mutation);
    }
    [Fact]
    public void OverlappingExclusiveBorrowsConflict()
    {
        Assert.Contains(Check("int value = 1; int& first = value; int& second = value; first = 2; second = 3;"), violation => violation.Check == MirBorrowCheck.Borrow);
    }
    [Theory]
    [InlineData("int value = 1; int& alias = value; alias = 2; value = 3;")]
    [InlineData("int value = 1; readonly int& first = value; readonly int& second = value; int sum = first + second;")]
    [InlineData("int value = 1; int& parent = value; int& child = parent; child = 2;")]
    public void CompatibleAndExpiredBorrowsAllowAccess(string body) => Assert.Empty(Check(body));
    [Fact]
    public void ReferenceParameterIsAnInputCapability()
    {
        Assert.Empty(Check("value = 2; int& alias = value; alias = 3;", "int& value"));
    }
}