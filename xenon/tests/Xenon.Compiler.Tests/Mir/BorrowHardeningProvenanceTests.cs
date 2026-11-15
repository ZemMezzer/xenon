using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowHardeningProvenanceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownOffsetWithinKnownAllocationCanReachSiblingStorage(bool invalid)
    {
        var compilation = Compile("struct S { public int A; public int B; } void Test(int i) { S* h=new S(); int* p=&h->A; readonly int& r=h->B; " +
            (invalid ? "p=p+i;" : "") + "*p=42; Observe(r); }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UncertainCallOffsetCanReachSiblingStorage(bool invalid)
    {
        var compilation = Compile("struct S { public int A; public int B; } void End(int* p,int i) { destruct(p[i]); } " +
            "void Test(int i) { S* h=new S(); S* other=new S(); readonly int& r=h->B; End(&" +
            (invalid ? "h" : "other") + "->A,i); Observe(r); }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.BorrowConflict);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RenamingAndUnrelatedAllocationsPreserveBorrowDiagnostics(bool invalid)
    {
        foreach (string name in new[] { "p", "renamed" })
        foreach (bool reverse in new[] { false, true })
        foreach (bool wrapper in new[] { false, true })
        {
            string p = "int* " + name + "=new int();";
            string q = "int* unrelated=new int();";
            string target = invalid ? name : "unrelated";
            var compilation = Compile("void Write(int* value) { *value=42; } void Test() { " +
                (reverse ? q + p : p + q) + "readonly int& r=*" + name + ";" +
                (wrapper ? "Write(" + target + ");" : "*" + target + "=42;") + "Observe(r); }");
            var errors = compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (invalid) Assert.Equal(DiagnosticIds.BorrowedPlaceMutation, Assert.Single(errors).Id);
            else Assert.Empty(errors);
        }
    }

    [Theory]
    [InlineData("P", true)] [InlineData("Q", false)]
    public void HeapAliasReceiverRetainsTheActiveLoan(string field, bool invalid)
    {
        var compilation = Compile("struct Item { public int X; public void Write() { this.X=42; } } " +
            "struct H { public Item* P; public Item* Q; } extern void ObserveItem(readonly Item& r); " +
            "void Test() { Item* p=new Item(); Item* q=new Item(); H* h=new H(); h->P=p; h->Q=q; readonly Item& r=*p; h->" +
            field + "->Write(); ObserveItem(r); }");
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void DeepByValueReturnAliasRetainsStoredRoots(bool invalid)
    {
        const int depth = 10;
        string path = string.Concat(Enumerable.Repeat("Next.", depth));
        var compilation = Compile(Types(depth) + $$"""
            int* Get(Top value) { return value.{{(invalid ? "A" : "B")}}.{{path}}P; }
            void Write(int* p) { *p = 42; }
            void Test() { Top value = Top(); int* p = new int(); int* q = new int();
                value.A.{{path}}P = p; value.B.{{path}}P = q;
                readonly int& r = *p; Write(Get(value)); Observe(r); }
            """);
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.BorrowedPlaceMutation || d.Id == DiagnosticIds.BorrowConflict);
    }

    [Theory]
    [InlineData(7, true)] [InlineData(7, false)]
    [InlineData(8, true)] [InlineData(8, false)]
    [InlineData(10, true)] [InlineData(10, false)]
    [InlineData(12, true)] [InlineData(12, false)]
    public void DeepOpaqueHandleKeepsItsBorrowDependencyAndOuterField(int depth, bool invalid)
    {
        string path = string.Concat(Enumerable.Repeat("Next.", depth));
        var compilation = Compile(Types(depth) + $$"""
            void End(Top* h) { delete(h->{{(invalid ? "A" : "B")}}.{{path}}P); }
            void Test(Top* h) { int* p = h->A.{{path}}P; readonly int& r = *p; End(h); Observe(r); }
            """);
        Assert.Equal(invalid, compilation.HasErrors);
        if (invalid) Assert.Contains(compilation.Diagnostics, d => d.Id is DiagnosticIds.BorrowConflict or DiagnosticIds.FreeWhileBorrowed);
    }

    [Theory]
    [InlineData(8)] [InlineData(12)] [InlineData(20)]
    public void DeepSubobjectRetainsAllocationDeath(int depth)
    {
        string path = string.Concat(Enumerable.Repeat("Next.", depth));
        var compilation = Compile(Types(depth) + $$"""
            int Test() { Top* h = new Top(); int** p = &h->A.{{path}}P; delete(h); return **p; }
            """);
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.PointerUseAfterFree);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DeepExplicitlyStoredAliasesSurviveWidening(bool unrelated)
    {
        const int depth = 10;
        string path = string.Concat(Enumerable.Repeat("Next.", depth));
        var compilation = Compile(Types(depth) + $$"""
            void End(Top* h) { delete(h->A.{{path}}P); }
            void Test() { Top* h = new Top(); int* p = new int(); int* q = new int();
                h->A.{{path}}P = p; readonly int& r = *{{(unrelated ? "q" : "p")}}; End(h); Observe(r); }
            """);
        Assert.Equal(!unrelated, compilation.HasErrors);
        if (!unrelated) Assert.Contains(compilation.Diagnostics, d => d.Id is DiagnosticIds.BorrowConflict or DiagnosticIds.FreeWhileBorrowed);
    }

    [Fact]
    public void BoundedPathsKeepTheirPrefixAndNeverBecomeExactAgain()
    {
        var root = new MirReferenceOrigin(MirReferenceOriginKind.RawPointee, 1);
        foreach (string field in Enumerable.Range(0, 40).Select(i => i.ToString())) root = root.Project(field);
        Assert.Equal("0/1/2/3/4/5/6/*", root.Path);
        Assert.Equal(root, root.Project("^").Project("0"));
        var a = MirReferenceValue.Of(new(MirReferenceOriginKind.Local, 1));
        var b = MirReferenceValue.Of(new(MirReferenceOriginKind.Local, 2));
        var stored = MirReferenceValue.Empty.Store(root.Path, a, true).Store(root.Path, b, true);
        Assert.True(a.Roots.Union(b.Roots).IsSubsetOf(stored.Project(root.Path).Roots));
        Assert.Contains(stored.Project(root.Path).Roots, item => item.Kind == MirReferenceOriginKind.Unknown);
    }

    private static string Types(int depth) => "struct S0 { public int* P; } " +
        string.Join(' ', Enumerable.Range(1, depth).Select(i => $"struct S{i} {{ public S{i - 1} Next; }}")) +
        $"struct Top {{ public S{depth} A; public S{depth} B; }} ";
    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(
        "namespace Hardening; extern void Observe(readonly int& r); " + source));
}
