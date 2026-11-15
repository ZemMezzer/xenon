using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Libraries;

public sealed class XelibLifetimeSummaryTests
{
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void ImportedRawAsyncRetentionKeepsKnownOwnerDependencies(bool owned)
    {
        var image = Image(ResumableLifetimeTests.Protocol.Replace("namespace Example;", "namespace Lifetimes;") +
            "public async Result Read(int* p) { await Operation(); return *p; }");
        var app = Import(image, owned
            ? "Result Make() { unique<int> p=new int(); return Read(&*p); }"
            : "Result Make() { return Read(new int()); }");
        if (owned) Assert.Contains(app.Diagnostics, d => d.Id == DiagnosticIds.ValueLifetimeEscape);
        else Assert.False(app.HasErrors, string.Join('\n', app.Diagnostics));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LegacyDeepWildcardContractRetainsOpaqueBorrow(bool missing)
    {
        string types = "public struct S0 { public int* P; } " + string.Join(' ', Enumerable.Range(1, 10)
            .Select(i => $"public struct S{i} {{ public S{i - 1} Next; }}"));
        string path = string.Concat(Enumerable.Repeat("Next.", 10));
        var image = Rewrite(Image("namespace Lifetimes; " + types + "public void End(S10* h) { delete(h->" + path + "P); }"), records =>
            [.. records.Select(record => record.Name != "End" ? record : record with { LifetimeSummary = missing ? null :
                record.LifetimeSummary! with { Effects = [], IncompleteReasons = ["unresolved-target"],
                    Uncertainties = [record.LifetimeSummary!.Uncertainties!.Value[0] with { Path = "*/^" }] } })]);
        var app = Import(image, "extern void Observe(readonly int& value); void Test(S10* h) { int* p=h->" + path + "P; readonly int& r=*p; End(h); Observe(r); }");
        Assert.Contains(app.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict);
    }

    private const string Library = """
        namespace Lifetimes;
        public struct Item { public int Value; }
        public struct Holder { public Item* Value; }
        public void Destroy(Item* p) { delete(p); }
        public void Reset(Item* p, bool restore) { destruct(*p); if (restore) *p = Item(); }
        public void Rebuild(Item* p) { *p = Item(); }
        public void Choose(Item* a, Item* b, bool chooseA) { Item* p; if (chooseA) p = a; else p = b; delete(p); }
        public Item* Same(Item* p) { return p; }
        public Item* FromHolder(Holder value) { return value.Value; }
        """;

    private static byte[] Image(string source = Library)
    {
        var compilation = Compilation.Create(SourceText.From(source, "library.xe"));
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        return XelibWriter.Write(compilation, new("Lifetimes"));
    }
    private static Compilation Import(byte[] image, string source) => Compilation.Create(new CompilationOptions(),
        [XelibReader.Read(image, metadataOnly: true)], SourceText.From("using Lifetimes; namespace App; " + source, "app.xe"));

    [Theory]
    [InlineData("Destroy(p);", false)]
    [InlineData("Reset(p, false);", false)]
    [InlineData("Reset(p, true);", true)]
    [InlineData("destruct(*p); Rebuild(p);", true)]
    [InlineData("Choose(p, new Item(), true);", false)]
    [InlineData("Choose(new Item(), p, true);", true)]
    [InlineData("Item* q = Same(p); delete(q);", false)]
    [InlineData("Holder h = Holder(); h.Value = p; Item* q = FromHolder(h); delete(q);", false)]
    public void EffectsAndReturnAliasesSurviveWithoutBodies(string operation, bool valid)
    {
        var compilation = Import(Image(), "int Test() { Item* p = new Item(); " + operation + " return p->Value; }");
        Assert.Equal(valid, !compilation.HasErrors);
        if (!valid) Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == DiagnosticIds.PointerUseAfterFree);
    }

    [Fact]
    public void OldLibraryWithoutOptionalSummaryKeepsRawUnknownSemantics()
    {
        var image = Rewrite(Image(), records => [.. records.Select(record => record with { LifetimeSummary = null })]);
        var compilation = Import(image, "int Test() { Item* p = new Item(); Destroy(p); return p->Value; }");
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void MalformedOptionalSummaryIsRejected(int mutation)
    {
        var image = Rewrite(Image(), records => [.. records.Select(record => record.Name != "Reset" || record.LifetimeSummary is not { } summary ? record :
            record with { LifetimeSummary = mutation switch
            {
                0 => summary with { Version = 999 },
                1 => summary with { Effects = [summary.Effects[0] with { Parameter = 999 }] },
                2 => summary with { Effects = [summary.Effects[0] with { Path = "bad/path" }] },
                3 => summary with { Effects = [summary.Effects[0] with { Transition = "MadeUp" }] },
                4 => summary with { Effects = [summary.Effects[0] with { When = summary.Effects[0].When with { Guards = [new(0, true, null, null)] } }] },
                5 => summary with { Exits = [new([], "MadeUp", null, 0, 0)] },
                6 => summary with { Effects = [summary.Effects[0] with { Path = "999" }] },
                _ => summary with { Effects = [summary.Effects[0] with { Path = "[00]" }] },
            } })]);
        Assert.Throws<XelibFormatException>(() => XelibReader.Read(image, metadataOnly: true));
    }

    [Fact]
    public void RawAsyncRetentionContractSurvivesSeparateCompilation()
    {
        var image = Image(ResumableLifetimeTests.Protocol.Replace("namespace Example;", "namespace Lifetimes;") +
            "public async Result Read(int* p) { await Operation(); return *p; }");
        var compilation = Import(image, "Result Make() { int value = 42; return Read(&value); }");
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }

    [Fact]
    public void CharacterResultAndGuardSurviveSeparateCompilation()
    {
        var image = Image("namespace Lifetimes; public char Destroy(int* p, char c) { if (c == 'a') delete(p); return 'a'; }");
        var compilation = Import(image, "int Test() { int* p = new int(); char c = Destroy(p, 'a'); return *p; }");
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PointerUseAfterFree);
    }

    [Fact]
    public void CharacterResultPreservesTheLiveBranch()
    {
        var image = Image("namespace Lifetimes; public char Maybe(int* p, bool c) { if (c) { delete(p); return 'd'; } return 'a'; }");
        var compilation = Import(image, "int Test(bool c) { int* p = new int(); char status = Maybe(p, c); if (status == 'a') return *p; return 0; }");
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
    }

    [Theory]
    [InlineData("int", XelibConstantKind.UnsignedInteger, "18446744073709551615")]
    [InlineData("uint", XelibConstantKind.SignedInteger, "-1")]
    [InlineData("int", XelibConstantKind.SignedInteger, "2147483648")]
    public void GuardConstantsMustMatchTheirDeclaredType(string type, XelibConstantKind kind, string value)
    {
        var image = Rewrite(Image("namespace Lifetimes; public void Maybe(int* p, " + type + " n) { delete(p); }"),
            records => [.. records.Select(record => record.Name != "Maybe" || record.LifetimeSummary is not { } summary ? record : record with
            { LifetimeSummary = summary with { Effects = [summary.Effects[0] with { When = summary.Effects[0].When with
                { Guards = [new(1, true, "Equal", new(kind, value))] } }] } })]);
        Assert.Throws<XelibFormatException>(() => XelibReader.Read(image, metadataOnly: true));
    }

    private static byte[] Rewrite(byte[] image, Func<ImmutableArray<XelibSymbolRecord>, ImmutableArray<XelibSymbolRecord>> change)
    {
        var container = XelibContainer.Read(image);
        var manifest = XelibJson.Deserialize<XelibManifest>(container.GetRequiredSection(XelibSectionKind.Manifest).AsSpan(), null);
        var records = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(container.GetRequiredSection(XelibSectionKind.Symbols).AsSpan(), null);
        var sections = container.Sections.Where(pair => pair.Key != (uint)XelibSectionKind.Manifest)
            .ToDictionary(pair => (XelibSectionKind)pair.Key, pair => pair.Value.ToArray());
        sections[XelibSectionKind.Symbols] = XelibJson.Serialize(change(records));
        sections[XelibSectionKind.Manifest] = XelibJson.Serialize(manifest with { ContentIdentity = XelibWriter.ComputeContentIdentity(manifest.Name,
            manifest.Version, sections.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)pair.Value))) });
        return XelibContainer.Write(sections.Select(pair => new XelibSection(pair.Key, XelibSectionFlags.Required, pair.Value)));
    }

    [Theory]
    [InlineData("int*", "a", "b", "a,b")]
    [InlineData("int*&", "a", "b", "a,b")]
    [InlineData("int**", "*a", "*b", "&a,&b")]
    public void ScopedIncompletenessSurvivesTwoMetadataOnlyImports(string type, string a, string b, string args)
    {
        var imageA = Image($$"""
            namespace Lifetimes;
            public void End(int* unused, {{type}} a, {{type}} b, int n, bool enabled) {
                if (enabled) { int* p; if (n == 0) p = {{a}}; else p = {{b}}; delete(p); }
            }
            """);
        var referenceA = XelibReader.Read(imageA, metadataOnly: true);
        var libraryB = Compilation.Create(new CompilationOptions(), [referenceA],
            SourceText.From($$"""
                namespace Middle;
                public void Forward(int* unused, {{type}} a, {{type}} b, int n, bool enabled) { Lifetimes.End(unused, a, b, n, enabled); }
                """));
        Assert.False(libraryB.HasErrors, string.Join('\n', libraryB.Diagnostics));
        var imageB = XelibWriter.Write(libraryB, new("Middle", Dependencies: [new(referenceA, referenceA.LibraryIdentity)]));
        foreach (var (borrow, enabled, invalid) in new[] { ("a", "true", true), ("x", "true", false), ("a", "false", false) })
        {
            var compilation = Compilation.Create(new CompilationOptions(),
                [referenceA, XelibReader.Read(imageB, [referenceA], metadataOnly: true)], SourceText.From($$"""
                    namespace App; extern void Observe(readonly int& value);
                    void Test(int n) { int* x = new int(); int* a = new int(); int* b = new int();
                        readonly int& r = *{{borrow}}; Middle.Forward(x, {{args}}, n, {{enabled}}); Observe(r); }
                    """));
            Assert.True(invalid == compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
            if (invalid) Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict && item.Message.Contains("Forward"));
        }
    }

    [Theory]
    [InlineData("false", true)] [InlineData("true", false)]
    public void ConditionalEffectsAndAliasesSurviveTwoImports(string restore, bool invalid)
    {
        var imageA = Image();
        var referenceA = XelibReader.Read(imageA, metadataOnly: true);
        var middle = Compilation.Create(new CompilationOptions(), [referenceA], SourceText.From("""
            using Lifetimes; namespace Middle;
            public void Forward(Item* p, bool restore) { Reset(p, restore); }
            public Item* Alias(Item* p) { return Same(p); }
            """));
        Assert.False(middle.HasErrors, string.Join('\n', middle.Diagnostics));
        var imageB = XelibWriter.Write(middle, new("Middle", Dependencies: [new(referenceA, referenceA.LibraryIdentity)]));
        var app = Compilation.Create(new CompilationOptions(),
            [referenceA, XelibReader.Read(imageB, [referenceA], metadataOnly: true)], SourceText.From(
                "using Lifetimes; namespace App; int Test() { Item* p = new Item(); Item* q = Middle.Alias(p); Middle.Forward(p, " + restore + "); return q->Value; }"));
        Assert.True(invalid == app.HasErrors, string.Join('\n', app.Diagnostics));
        if (invalid) Assert.Contains(app.Diagnostics, item => item.Id == DiagnosticIds.PointerUseAfterFree);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void LegacyIncompleteAndMissingMetadataDoNotLoseLoadedLoans(bool missing)
    {
        var image = Rewrite(Image("namespace Lifetimes; public void End(int** p) { delete(*p); }"), records =>
            [.. records.Select(record => record.Name != "End" ? record : record with { LifetimeSummary = missing ? null :
                record.LifetimeSummary! with { Effects = [], IncompleteReasons = ["effect-budget"], Uncertainties = null } })]);
        var app = Import(image, "extern void Observe(readonly int& value); void Test() { int* p = new int(); readonly int& r = *p; End(&p); Observe(r); }");
        Assert.True(app.HasErrors);
        Assert.Contains(app.Diagnostics, item => item.Id == DiagnosticIds.BorrowConflict);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void MalformedUncertaintyIsRejected(int mutation)
    {
        var image = Rewrite(Image("namespace Lifetimes; public void End(int* a, int* b, int n) { int* p; if (n == 0) p = a; else p = b; delete(p); }"),
            records => [.. records.Select(record => record.Name != "End" ? record : record with { LifetimeSummary = record.LifetimeSummary! with
            { Uncertainties = [mutation switch {
                0 => record.LifetimeSummary!.Uncertainties!.Value[0] with { Parameter = 999 },
                1 => record.LifetimeSummary!.Uncertainties!.Value[0] with { Path = "999" },
                2 => record.LifetimeSummary!.Uncertainties!.Value[0] with { Transition = "MadeUp" },
                3 => record.LifetimeSummary!.Uncertainties!.Value[0] with { Reason = "" },
                _ => record.LifetimeSummary!.Uncertainties!.Value[0] with { Transition = "1" },
            }] } })]);
        Assert.Throws<XelibFormatException>(() => XelibReader.Read(image, metadataOnly: true));
    }
}
