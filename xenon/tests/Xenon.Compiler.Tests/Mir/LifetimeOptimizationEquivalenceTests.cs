using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class LifetimeOptimizationEquivalenceTests
{
    [Fact]
    public void TableInitializerSharesExitProofInsteadOfAllocatingOneWitnessPerRow()
    {
        string rows = string.Join(' ', Enumerable.Range(0, 32).Select(index => $"p[{index}] = Row({index});"));
        var compilation = Compilation.Create(SourceText.From("namespace Tables; " +
            "struct Row { public int X; public Row(int x) { X = x; } } void Fill(Row* p) { " + rows + " }"));
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        var bodies = compilation.SemanticModel.Functions.Select(function => MirLowerer.Lower(function, compilation.TypeFactory,
            compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true)).ToArray();
        var returns = MirPointerLifetimeAnalysis.BuildReturnSummaries(bodies, default);
        var summaries = MirPointerLifetimeAnalysis.BuildSummaries(bodies, returns, default);
        var effects = summaries.ToDictionary(pair => pair.Key, pair => pair.Value.Effects);
        var exits = summaries.Where(pair => pair.Value.Complete).ToDictionary(pair => pair.Key, pair => pair.Value.Exits);
        var body = bodies.Single(body => body.Symbol.Name == "Fill");
        var baseline = new MirPointerLifetimeAnalysis(body, returns, destructionSummaries: effects,
            symbolicParameters: true, lifetimeExits: exits);
        var optimized = new MirPointerLifetimeAnalysis(body, returns, destructionSummaries: effects,
            symbolicParameters: true, lifetimeExits: exits);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var expected = baseline.SummarizeEffects(default, summaries, sharePlacementExitProof: false);
        long baselineBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        var actual = optimized.SummarizeEffects(default, summaries);
        long optimizedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(expected.SetEquals(actual));
        Assert.True(optimizedBytes < baselineBytes / 2, $"shared: {optimizedBytes}; witnesses: {baselineBytes}");
    }

    [Theory]
    [InlineData("void End(int* p, int* q, bool c) { *p = 1; *q = 2; }")]
    [InlineData("struct Row { public int X; public Row(int x) { X = x; } } void End(Row* p) { p[0] = Row(1); p[1] = Row(2); p[2] = Row(3); }")]
    [InlineData("void Set(int* p) { *p = 1; } void End(int* p, int* q, bool c) { Set(p); Set(q); }")]
    [InlineData("void End(int* p, int* q, bool c) { *p = 1; if (c) *q = 2; }")]
    [InlineData("void End(int* p, int* q, bool c) { *p = 1; while (c) { *q = 2; } }")]
    [InlineData("void End(int* p, int* q, bool c) { *p = 1; delete(q); }")]
    [InlineData("void End(int* p, int* q, bool c) { *p = 1; throw 2; }")]
    [InlineData("extern void Unknown(int* p); void End(int* p, int* q, bool c) { *p = 1; Unknown(q); }")]
    [InlineData("void End(int* p, int* q, bool c) { delete(p); delete(q); }")]
    [InlineData("void End(int* p, int* q, bool c) { if(c) delete(p); else delete(q); }")]
    [InlineData("void End(int* p, int* q, bool c) { delete(p); if(c) return; delete(q); }")]
    [InlineData("void End(int* p, int* q, bool c) { if(c) return; delete(p); }")]
    [InlineData("void End(int* p, int* q, bool c) { delete(p); if(c) throw 1; }")]
    [InlineData("int End(int* p, int* q, bool c) { delete(p); if(c) return 1; return 2; }")]
    [InlineData("void End(int* p, int* q, bool c) { delete(p); while(c) { } }")]
    [InlineData("void End(int* p, int* q, bool c) { destruct(*p); if(c) *p=42; }")]
    [InlineData("void End(int* p, int* q, bool c) { int* a; if(c) a=p; else a=q; delete(a); }")]
    [InlineData("void End(int* p, int* q, bool c) { if(c) End(p,q,false); else delete(p); }")]
    [InlineData("extern void Unknown(int* p); void End(int* p, int* q, bool c) { delete(p); Unknown(q); }")]
    public void SharedStorageExitProofPreservesWitnessEffects(string source)
    {
        var compilation = Compilation.Create(SourceText.From("namespace Equivalence; " + source));
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        var bodies = compilation.SemanticModel.Functions.Select(function => MirLowerer.Lower(function, compilation.TypeFactory,
            compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true)).ToArray();
        var returns = MirPointerLifetimeAnalysis.BuildReturnSummaries(bodies, default);
        var summaries = MirPointerLifetimeAnalysis.BuildSummaries(bodies, returns, default);
        var effects = summaries.ToDictionary(pair => pair.Key, pair => pair.Value.Effects);
        var exits = summaries.Where(pair => pair.Value.Complete).ToDictionary(pair => pair.Key, pair => pair.Value.Exits);
        foreach (var body in bodies)
        {
            var previous = new MirPointerLifetimeAnalysis(body, returns, destructionSummaries: effects,
                symbolicParameters: true, lifetimeExits: exits).SummarizeEffects(default, summaries, shareStorageExitProof: false, sharePlacementExitProof: false);
            var current = new MirPointerLifetimeAnalysis(body, returns, destructionSummaries: effects,
                symbolicParameters: true, lifetimeExits: exits).SummarizeEffects(default, summaries, shareStorageExitProof: true);
            Assert.True(previous.SetEquals(current), body.Symbol.FullName + "\n" +
                string.Join('\n', previous.Except(current).Select(effect => "missing: " + effect)) + "\n" +
                string.Join('\n', current.Except(previous).Select(effect => "extra: " + effect)));
        }
    }
}
