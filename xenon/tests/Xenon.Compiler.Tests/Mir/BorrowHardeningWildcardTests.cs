using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowHardeningWildcardTests
{
    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    public void CopyOfWildcardStoredCallableKeepsItsKnownCapture(string captured, bool invalid)
    {
        var compilation = Compile("struct H { public function void() F; } void Test(int i) { int* p=new int(); int* q=new int(); " +
            "H[] slots=H[2]; slots[i].F=[" + captured + "]()=>{*" + captured + "=42;}; H copy=slots[0]; " +
            "readonly int& r=*p; copy.F(); Observe(r); }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("other", false)]
    public void LegacyWidenedAggregateLoadKeepsExplicitIndexedPayloads(string borrowed, bool invalid)
    {
        var compilation = Compile("""
            struct Nested { public int* P; }
            struct H { public Nested* Value; }
            void End(H* slots, int i) { delete(slots[i].Value->P); }
            void Test(int i) { int* p=new int(); int* other=new int(); Nested* inner=new Nested(); inner->P=p;
                H* slots=cast<H*>(malloc(sizeof(H)*cast<nuint>(2))); slots[0].Value=inner; slots[1].Value=inner;
            """ + "readonly int& r=*" + borrowed + "; End(slots,i); Observe(r); }");
        var bodies = compilation.SemanticModel.Functions.Select(function => MirLowerer.Lower(function,
            compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true)).ToArray();
        var returns = MirPointerLifetimeAnalysis.BuildReturnSummaries(bodies, default);
        var summaries = MirPointerLifetimeAnalysis.BuildSummaries(bodies, returns, default).ToDictionary(pair => pair.Key, pair => pair.Value);
        var end = summaries.Single(pair => pair.Key.Name == "End");
        summaries[end.Key] = end.Value with
        {
            Effects = [], IncompleteReasons = end.Value.IncompleteReasons.Add("unresolved-target"),
            Uncertainties = [new(0, "*/^", false, null, MirLifetimeTransition.StorageDeallocation, "unresolved-target")],
            IsImported = true,
        };
        var violations = new MirBorrowAnalysis(bodies.Single(body => body.Symbol.Name == "Test"),
            lifetimeSummaries: summaries, pointerReturns: returns).Check();
        Assert.Equal(invalid, !violations.IsEmpty);
        if (invalid) Assert.Contains(violations, violation => violation.Check == MirBorrowCheck.Borrow);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    [InlineData("other", false)]
    public void IndexedAggregateCallPreservesStoredPointeesAndFieldSelection(string borrowed, bool invalid)
    {
        var compilation = Compile("""
            struct H { public int* P; public int* Q; }
            void End(H* slots, int i) { delete(slots[i].P); }
            void Test(int i) { int* p=new int(); int* q=new int(); int* other=new int();
                H* slots=cast<H*>(malloc(sizeof(H)*cast<nuint>(2)));
                slots[0].P=p; slots[1].P=p; slots[0].Q=q; slots[1].Q=q;
            """ + "readonly int& r=*" + borrowed + "; End(slots,i); Observe(r); }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.BorrowConflict || d.Id == DiagnosticIds.FreeWhileBorrowed);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("other", false)]
    public void CopyOfWildcardStoredAggregateKeepsItsPointerPayload(string borrowed, bool invalid)
    {
        var compilation = Compile("""
            struct H { public int* P; }
            void Test(int i) { int* p=new int(); int* other=new int();
                H* slots=cast<H*>(malloc(sizeof(H)*cast<nuint>(2)));
                slots[i].P=p; H copy=slots[0];
            """ + "readonly int& r=*" + borrowed + "; *copy.P=42; Observe(r); }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(
        "namespace Wildcards; extern void Observe(readonly int& r); " + source));
}
