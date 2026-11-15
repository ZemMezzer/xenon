using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowHardeningBudgetTests
{
    [Theory]
    [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(8, false)] [InlineData(8, true)]
    public void WidenedFactoryContextDoesNotCertifyAnActiveBorrow(int depth, bool invalid)
    {
        var factories = string.Join(' ', Enumerable.Range(0, depth).Select(i =>
            $"int* F{i}() {{ return " + (i == depth - 1 ? "new int()" : $"F{i + 1}()") + "; }"));
        var compilation = Compile(factories + "void Write(int* p) { *p=42; } void Test() { int* p=F0(); int* q=F0(); readonly int& r=*p; Write(" +
            (invalid ? "p" : "q") + "); Observe(r); }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Fact]
    public void ExitBudgetDoesNotCreateMissingOperationsOrPruneCallerBranches()
    {
        var compilation = Compile("int Choose(bool c) { if(c) return 1; return 2; } " +
            "void Test(bool c) { int* p=new int(); readonly int& r=*p; Choose(c); Observe(r); }");
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        var bodies = compilation.SemanticModel.Functions.Select(function => MirLowerer.Lower(function, compilation.TypeFactory,
            compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true)).ToArray();
        var returns = MirPointerLifetimeAnalysis.BuildReturnSummaries(bodies, default);
        var summaries = MirPointerLifetimeAnalysis.BuildSummaries(bodies, returns, default, new(Effects: 1));
        var choose = summaries.Single(pair => pair.Key.Name == "Choose").Value;
        Assert.Contains("exit-budget", choose.IncompleteReasons);
        Assert.False(choose.Complete);
        Assert.Empty(choose.Uncertainties!);
        Assert.Empty(new MirBorrowAnalysis(bodies.Single(body => body.Symbol.Name == "Test"),
            lifetimeSummaries: summaries, pointerReturns: returns).Check());
    }

    [Fact]
    public void RestorationAlternativeBudgetIsExplicitWithoutInventingSafety()
    {
        var parameters = string.Join(',', Enumerable.Range(0, 14).Select(i => $"bool c{i}"));
        var calls = string.Join(' ', Enumerable.Range(0, 7).Select(i => $"Restore(p,c{i * 2},c{i * 2 + 1});"));
        var compilation = Compile("void Restore(int* p,bool a,bool b) { if(a && b) *p=42; } " +
            "int Test(" + parameters + ") { int* p=new int(); destruct(*p); " + calls + "return *p; }");
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.PointerUseAfterFree);
        Assert.Contains("restoration-witness-budget", compilation.DumpLifetimeAnalysis());
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(
        "namespace Budgets; extern void Observe(readonly int& r); " + source));
}
