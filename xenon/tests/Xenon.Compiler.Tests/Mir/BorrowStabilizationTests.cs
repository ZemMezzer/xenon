using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowStabilizationTests
{
    [Theory]
    [InlineData("P", true)] [InlineData("Q", false)]
    public void LoadingAnOpaquePointerFieldKeepsTheCarrierType(string field, bool invalid)
    {
        var compilation = Compile("struct H { public int** P; public int** Q; } extern void ObservePointer(int*& r); " +
            "void End(H* h) { delete(h->" + field + "); } void Test(H* h) { int** p = h->P; int*& r = *p; End(h); ObservePointer(r); }");
        Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.FreeWhileBorrowed);
    }

    [Theory]
    [InlineData("0", true)] [InlineData("1", false)]
    public void LoadingAnIndexedPointerKeepsTheCarrierType(string index, bool invalid)
    {
        var compilation = Compile("extern void ObservePointer(int*& r); void End(int*** slots) { delete(slots[" + index + "]); } " +
            "void Test(int*** slots) { int** p = slots[0]; int*& r = *p; End(slots); ObservePointer(r); }");
        Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.FreeWhileBorrowed);
    }
    [Theory]
    [InlineData("p", true)] [InlineData("q", false)]
    public void IndirectUnknownCallCannotCertifyAnOverlappingLoan(string argument, bool invalid)
    {
        var compilation = Compile("void Test(function void(int*)* invoke) { int* p = new int(); int* q = new int(); readonly int& r = *p; invoke(" + argument + "); Observe(r); }");
        Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict);
    }
    [Fact]
    public void SummaryOrderingUsesQualifiedParameterTypes()
    {
        var compilation = Compilation.Create(SourceText.From("namespace First; public struct Item { }", "first.xe"),
            SourceText.From("namespace Second; public struct Item { }", "second.xe"),
            SourceText.From("namespace App; void End(First.Item* p) { delete(p); } void End(Second.Item* p) { delete(p); }", "app.xe"));
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        var keys = compilation.GetDeclaredFunctionSymbols().Where(function => function.Name == "End")
            .Select(MirPointerLifetimeAnalysis.FunctionKey).ToArray();
        Assert.Equal(2, keys.Distinct().Count());
        Assert.Contains(keys, key => key.Contains("First.Item"));
        Assert.Contains(keys, key => key.Contains("Second.Item"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExceptionalCompoundReconstructionIsCorrelated(bool restore)
    {
        var compilation = Compile("void Reset(int* p, bool a, bool b) { int* q = p; destruct(*q); if (a && b) *q = 42; throw 1; } " +
            "int Test() { int* p = new int(); try { Reset(p, true, " + restore.ToString().ToLowerInvariant() +
            "); } catch (readonly int& error) { return *p; } return 0; }");
        Assert.True(restore != compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (!restore) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.PointerUseAfterFree);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RecursiveThrowDoesNotInventNormalExit(bool useInCatch)
    {
        var compilation = Compile("void R(int* p, bool again) { if (again) R(p, false); else { destruct(*p); throw 1; } } " +
            "int Test() { int* p = new int(); try { R(p, true); return *p; } catch (readonly int& error) { return " + (useInCatch ? "*p" : "0") + "; } }");
        Assert.True(useInCatch == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (useInCatch) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.PointerUseAfterFree);
    }

    [Fact]
    public void RebuildingDoesNotRepairAnOldCheckedLoanOrCascadeDiagnostics()
    {
        var compilation = Compile("void Reset(int* p, bool a, bool b) { destruct(*p); if (a || b) *p = 42; } " +
            "void Test() { int* p = new int(); readonly int& r = *p; Reset(p, true, false); Observe(r); }");
        var error = Assert.Single(compilation.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error));
        Assert.Equal(DiagnosticIds.DestructWhileBorrowed, error.Id);
        Assert.Contains("Reset", error.Message);
        Assert.Contains("invalidates", error.Message);
    }
    [Theory]
    [InlineData("guard", false)] [InlineData("guard", true)]
    [InlineData("effect", false)] [InlineData("effect", true)]
    [InlineData("scc", false)] [InlineData("scc", true)]
    public void ExhaustedBudgetsRetainOnlyRelevantOperationCoverage(string budget, bool affected)
    {
        string body = budget == "scc" ? "if (c) End(unused, a, b, false); else delete(a);" :
            budget == "effect" ? "delete(a); delete(b);" : "int* p; if (c) p = a; else p = b; delete(p);";
        var compilation = Compile("void End(int* unused, int* a, int* b, bool c) { " + body + " } " +
            "void Test(bool c) { int* x = new int(); int* a = new int(); int* b = new int(); readonly int& r = *" + (affected ? "a" : "x") +
            "; End(x, a, b, c); Observe(r); }");
        var bodies = compilation.SemanticModel.Functions.Select(function => MirLowerer.Lower(function, compilation.TypeFactory,
            compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true)).ToArray();
        var returns = MirPointerLifetimeAnalysis.BuildReturnSummaries(bodies, default);
        var limits = budget switch { "guard" => new MirLifetimeBudgets(Contexts: 1), "effect" => new MirLifetimeBudgets(Effects: 1),
            _ => new MirLifetimeBudgets(ComponentRounds: 1) };
        var summaries = MirPointerLifetimeAnalysis.BuildSummaries(bodies, returns, default, limits);
        var end = summaries.Single(pair => pair.Key.Name == "End").Value;
        Assert.False(end.Complete);
        Assert.NotEmpty(end.Uncertainties!);
        Assert.DoesNotContain(end.Uncertainties!, item => item.Parameter == 0);
        var violations = new MirBorrowAnalysis(bodies.Single(body => body.Symbol.Name == "Test"), lifetimeSummaries: summaries, pointerReturns: returns).Check();
        Assert.Equal(affected, !violations.IsEmpty);
        var reversed = MirPointerLifetimeAnalysis.BuildSummaries(bodies.Reverse().ToArray(), returns, default, limits);
        foreach (var (symbol, summary) in summaries)
        {
            Assert.True(summary.Effects.SetEquals(reversed[symbol].Effects));
            Assert.True(summary.Uncertainties!.SetEquals(reversed[symbol].Uncertainties!));
        }
    }

    [Theory]
    [InlineData("a && b", "a && b", false)]
    [InlineData("a && b", "a && !b", true)]
    [InlineData("a || b", "a || b", false)]
    [InlineData("a || b", "!a && !b", true)]
    [InlineData("!a && b", "!a && b", false)]
    [InlineData("!a && b", "a || !b", true)]
    public void CompoundReconstructionPreservesLiveAndDeadPaths(string restore, string use, bool invalid)
    {
        var compilation = Compile("void Reset(int* p, bool a, bool b) { int* q = p; destruct(*q); if (" + restore + ") *q = 42; } " +
            "int Test(bool a, bool b) { int* p = new int(); Reset(p, a, b); if (" + use + ") return *p; return 0; }");
        Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.PointerUseAfterFree);
    }

    [Theory]
    [InlineData("if (again) R(p, false); else destruct(*p);", true)]
    [InlineData("if (again) R(p, false); destruct(*p);", true)]
    [InlineData("destruct(*p); if (again) R(new int(), false);", true)]
    [InlineData("if (again) { R(p, false); return; } destruct(*p);", true)]
    [InlineData("destruct(*p); R(new int(), false);", false)]
    public void RecursiveOrderingAndNonreturningExits(string body, bool invalid)
    {
        var compilation = Compile("void R(int* p, bool again) { " + body + " } int Test() { int* p = new int(); R(p, true); return *p; }");
        Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.PointerUseAfterFree);
    }
    [Theory]
    [InlineData("p")] [InlineData("&p[1]")]
    public void UnknownRelativeOffsetCanReachEarlierArrayElement(string pointer)
    {
        var compilation = Compile("void End(int* p, int i) { destruct(p[i]); } " +
            "void Test(int i) { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 1; p[1] = 2; int* q = " + pointer +
            "; readonly int& r = p[0]; End(q, i); Observe(r); free(p); }");
        Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict);
    }
    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(
        "namespace Stabilization; extern void Observe(readonly int& value); " + source));

    [Theory]
    [InlineData("Write(p);", true)]
    [InlineData("Observe(r); Write(p);", false)]
    public void UnknownCallResultStillHasAnEstablishedLoan(string call, bool invalid)
    {
        var compilation = Compile("extern int* Get(); void Write(int* p) { *p = 42; } void Test() { int* p = Get(); readonly int& r = *p; " +
            call + (invalid ? "Observe(r);" : "") + " }");
        Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("a", true)] [InlineData("x", false)]
    public void WildcardPointerLoadKeepsStoredPointees(string borrow, bool invalid)
    {
        var compilation = Compile($$"""
            void End(int** slots, int i) { delete(slots[i]); }
            void Test(int i) { int* a = new int(); int* b = new int(); int* x = new int();
                int** slots = cast<int**>(malloc(sizeof(int*) * cast<nuint>(2))); slots[0] = a; slots[1] = b;
                readonly int& r = *{{borrow}}; End(slots, i); Observe(r); free(slots); }
            """);
        Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict);
    }

    [Theory]
    [InlineData("void Mutate(int& v) { v = 42; }")]
    [InlineData("extern void Mutate(int& v);")]
    public void CheckedArgumentCanUseItsOwnAuthority(string declaration)
    {
        var compilation = Compile(declaration + "void Test() { int* p = new int(); delete(p); int x = 0; int& r = x; Mutate(r); Observe(r); }");
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
    }

    [Theory]
    [InlineData("int*", "a", "b", "a,b")]
    [InlineData("int*&", "a", "b", "a,b")]
    [InlineData("int**", "*a", "*b", "&a,&b")]
    public void IncompleteLoadedTargetCannotProveCheckedLoanSafe(string type, string a, string b, string args)
    {
        var compilation = Compile($$"""
            void End({{type}} a, {{type}} b, int n) { int* p; if (n == 0) p = {{a}}; else p = {{b}}; delete(p); }
            void Test(int n) { int* a = new int(); int* b = new int(); readonly int& r = *a; End({{args}}, n); Observe(r); }
            """);
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict && item.Message.Contains("End"));
    }

    [Theory]
    [InlineData("int*", "a", "b", "a,b")]
    [InlineData("int*&", "a", "b", "a,b")]
    [InlineData("int**", "*a", "*b", "&a,&b")]
    public void IncompleteLoadedTargetDoesNotAffectAnotherAllocation(string type, string a, string b, string args)
    {
        var compilation = Compile($$"""
            void End(int* unused, {{type}} a, {{type}} b, int n) { int* p; if (n == 0) p = {{a}}; else p = {{b}}; delete(p); }
            void Test(int n) { int* x = new int(); int* a = new int(); int* b = new int(); readonly int& r = *x; End(x, {{args}}, n); Observe(r); }
            """);
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void IncompleteEffectsRetainTheirGuard(bool enabled)
    {
        var compilation = Compile($$"""
            void End(int* a, int* b, int n, bool enabled) { if (enabled) { int* p; if (n == 0) p = a; else p = b; delete(p); } }
            void Test(int n) { int* a = new int(); int* b = new int(); readonly int& r = *a; End(a, b, n, {{enabled.ToString().ToLowerInvariant()}}); Observe(r); }
            """);
        Assert.Equal(enabled, compilation.HasErrors);
        if (enabled) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict);
    }

    [Theory]
    [InlineData("Safe", false)] [InlineData("Other", true)]
    public void IncompleteEffectsRetainTheirField(string field, bool invalid)
    {
        var compilation = Compile($$"""
            struct H { public int* Safe; public int* Other; }
            void End(H h, int i) { destruct(h.Other[i]); }
            void Test(int i) { int* x = new int(); int* y = new int(); H h = H(); h.Safe = x; h.Other = y;
                readonly int& r = *h.{{field}}; End(h, i); Observe(r); }
            """);
        Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics) + compilation.DumpLifetimeAnalysis() +
            string.Join('\n', compilation.GetDeclaredFunctionSymbols().SelectMany(f => f.MirLifetimeSummary?.Uncertainties?.Select(u => f.Name + ": " + u) ?? [])));
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict);
    }

    [Theory]
    [InlineData("Write(p);", true)]
    [InlineData("Observe(r); Write(p);", false)]
    public void OpaquePointerKeepsCheckedLoanIdentity(string call, bool invalid)
    {
        var compilation = Compile("struct H { public int* P; } void Write(int* p) { *p = 42; } " +
            "void Test(H* h) { int* p = h->P; readonly int& r = *p; " + call + (invalid ? "Observe(r);" : "") + " }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("void Unknown(int*& p);", "Unknown(p);", true)]
    [InlineData("void Unknown(int** p);", "Unknown(&p);", true)]
    [InlineData("void Unknown(int* p);", "Unknown(q);", false)]
    public void MissingContractsFollowLoadedHandles(string declaration, string call, bool invalid)
    {
        var compilation = Compile("extern " + declaration +
            "void Test() { int* p = new int(); int* q = new int(); readonly int& r = *p; " + call + "Observe(r); }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict);
    }
}
