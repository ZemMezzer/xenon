using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirLifetimeAnalysisTests
{
    private static MirLifetimeAnalysis Analyze(string source, string name = "Use")
    {
        Compilation compilation = Compilation.Create(SourceText.From(ResumableLifetimeTests.Protocol + source, "mir-lifetimes.xe"));
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == name);
        var effects = ResumableExceptionAnalyzer.InferEffects(compilation.SemanticModel.Functions, compilation.TypeFactory, default);
        var analysis = new MirLifetimeAnalysis(
            MirLowerer.Lower(function, compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations),
            effects);
        _ = analysis.Analyze();
        return analysis;
    }

    [Fact]
    public void SuspensionExportsItsLiveExternalBorrow()
    {
        var analysis = Analyze("", "Borrow");
        Assert.Contains(analysis.Returns, dependency => dependency.Kind == LifetimeDependencyKind.ParameterBorrow && dependency.Ordinal == 0);
    }

    [Theory]
    [InlineData("int value = 42; Result task = Borrow(value); return await task;")]
    [InlineData("int value = 42; Result task = Borrow(value); Result copy = task; return await copy;")]
    [InlineData("int[] destination; { int[] source = int[2]; destination = move source; } Result task = ArrayBorrow(destination); return await task;")]
    public void CompletedOperationAllowsOwnerCleanup(string body)
    {
        var analysis = Analyze("async Result Use() { " + body + " }");
        Assert.Empty(analysis.Diagnostics);
    }

    [Fact]
    public void ReturningDependentOperationDiagnosesItsLocalOwner()
    {
        var analysis = Analyze("Result Use() { int value = 42; return Borrow(value); }");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape &&
            diagnostic.Message.Contains("value", StringComparison.Ordinal));
    }

    [Fact]
    public void DiscardedOperationMustCompleteBeforeOwnerDies()
    {
        var analysis = Analyze("void Use() { int value = 42; Result task = Borrow(value); }");
        Assert.Contains(analysis.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PendingBorrowedOperation);
    }

    [Fact]
    public void ForwardingExternalOperationPreservesExportedContract()
    {
        var analysis = Analyze("Result Use(int& value) { return Borrow(value); }");
        Assert.Contains(analysis.Returns, dependency => dependency.Kind == LifetimeDependencyKind.ParameterBorrow && dependency.Ordinal == 0);
        Assert.Empty(analysis.Diagnostics);
    }
}