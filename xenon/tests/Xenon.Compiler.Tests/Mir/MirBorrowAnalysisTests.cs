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
    [Fact]
    public void IncrementChecksBothReadAndWrite()
    {
        var violations = Check("int value = 1; int& alias = value; value++; alias = 3;");
        Assert.Contains(violations, violation => violation.Check == MirBorrowCheck.Read);
        Assert.Contains(violations, violation => violation.Check == MirBorrowCheck.Mutation);
    }
    [Fact]
    public void PointerIndexBorrowsShareTheirPointeeOrigin()
    {
        Assert.Contains(Check("int& first = pointer[index]; int& second = pointer[index]; first = 1; second = 2;",
            "int* pointer, int index"), violation => violation.Check == MirBorrowCheck.Borrow);
    }
    [Fact]
    public void ExplicitDestructionChecksLiveLoans()
    {
        Assert.Contains(Check("storage<int> value = 1; int& alias = value; destruct(value); alias = 2;"),
            violation => violation.Check == MirBorrowCheck.Destruct);
    }
    [Fact]
    public void FreeChecksLivePointeeLoans()
    {
        Assert.Contains(Check("int* value = new int(); int& alias = *value; free value; alias = 2;"),
            violation => violation.Check == MirBorrowCheck.Free);
    }    [Fact]
    public void DirectReferenceArgumentPermitsLaterArgumentReadBeforeCall()
    {
        Assert.Empty(Check("int value = 1; action(value, value);", "function void(int&, int)* action"));
    }
    [Fact]
    public void DirectReferenceArgumentsConflictWhenActivatedTogether()
    {
        Assert.Contains(Check("int value = 1; action(value, value);", "function void(int&, int&)* action"),
            violation => violation.Check == MirBorrowCheck.Borrow);
    }}