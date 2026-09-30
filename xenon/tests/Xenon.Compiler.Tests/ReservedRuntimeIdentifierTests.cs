using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xenon.Driver;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class ReservedRuntimeIdentifierTests
{
    [Theory]
    [InlineData("extern void __xenon_test();")]
    [InlineData("void __xenon_test() {}")]
    [InlineData("void __xenon_() {}")]
    [InlineData("int __xenon_test;")]
    [InlineData("const int __xenon_test = 0;")]
    [InlineData("struct S { public void __xenon_test() {} }")]
    [InlineData("struct S { public const int __xenon_test = 0; }")]
    [InlineData("struct __xenon_test {}")]
    [InlineData("interface __xenon_test {}")]
    [InlineData("enum __xenon_test { A }")]
    [InlineData("enum E { __xenon_test }")]
    [InlineData("struct S { public int __xenon_test; }")]
    [InlineData("struct S { public int __xenon_test { get; set; } }")]
    [InlineData("void F(int __xenon_test) {}")]
    [InlineData("void F() { int __xenon_test = 0; }")]
    [InlineData("void F() { __xenon_test(); }")]
    [InlineData("void F() { function void()* f = &__xenon_test; }")]
    [InlineData("void F<T>(T x) { x.__xenon_test(); }")]
    [InlineData("void F<__xenon_test>() {}")]
    [InlineData("namespace __xenon_test;")]
    [InlineData("using __xenon_test = Example;")]
    [InlineData("extern void* __xenon_async_root_current();")]
    [InlineData("extern void __xenon_async_root_post();")]
    [InlineData("extern void __xenon_free(void* value);")]
    [InlineData("extern void __xenon_eh_throw(void* value);")]
    [InlineData("extern void* __xenon_resume_create();")]
    [InlineData("extern int __xenon_target_os();")]
    [InlineData("extern int __xenon_target_arch();")]
    [InlineData("extern int __xenon_future_unknown_name();")]
    public void EverySourceIdentifierIsReservedWithPreciseLocation(string fragment)
    {
        string text = "namespace Example; " + fragment;
        var source = SourceText.From(text, "reserved.xe");
        var compilation = Compilation.Create(source);
        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics.Where(d => d.Id == DiagnosticIds.ReservedRuntimeIdentifier));
        Match identifier = Regex.Match(text, "__xenon_[A-Za-z_]*");
        Assert.Equal(identifier.Index, diagnostic.Location.Span.Start);
        Assert.Equal(identifier.Length, diagnostic.Location.Span.Length);
        Assert.Contains(identifier.Value, diagnostic.Message);
        Assert.Contains("reserved compiler/runtime prefix '__xenon_'", diagnostic.Message);
    }

    [Fact]
    public void OrdinaryFfiAndPrefixInMiddleStringsAndCommentsRemainAllowed()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Example;
            // __xenon_comment
            extern int puts(readonly byte* text);
            extern int my__xenon_test();
            extern int __Xenon_test();
            int Main() { puts("__xenon_literal"); return my__xenon_test() + __Xenon_test(); }
            """));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.Contains("@puts", ir);
        Assert.Contains("@my__xenon_test", ir);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedSymbolCannotMasqueradeAsRuntime(bool metadataOnly)
    {
        var library = Compilation.Create(SourceText.From("namespace Library; public extern void External();"));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("Library"));
        var container = XelibContainer.Read(bytes);
        var symbols = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(
            container.GetRequiredSection(XelibSectionKind.Symbols).AsSpan(), null);
        foreach (string name in new[] { "External", "Library" })
        {
            byte[] forged = Rewrite(bytes, XelibSectionKind.Symbols, XelibJson.Serialize(symbols
                .Select(symbol => symbol.Name == name ? symbol with { Name = "__xenon_forged" } : symbol).ToImmutableArray()));
            var error = Assert.Throws<XelibFormatException>(() => XelibReader.Read(forged, metadataOnly: metadataOnly));
            Assert.Contains("reserved compiler/runtime prefix", error.Message);
        }
    }

    [Fact]
    public void ImportedGenericLocalCannotUseReservedPrefix()
    {
        var library = Compilation.Create(SourceText.From(
            "namespace Library; public T Identity<T>(T value) { T local = move value; return move local; }"));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("Library"));
        var container = XelibContainer.Read(bytes);
        string bodies = System.Text.Encoding.UTF8.GetString(container.GetRequiredSection(XelibSectionKind.Bodies).AsSpan());
        Assert.Contains("local", bodies);
        byte[] forged = Rewrite(bytes, XelibSectionKind.Bodies,
            System.Text.Encoding.UTF8.GetBytes(bodies.Replace("\"local\"", "\"__xenon_local\"")));
        foreach (bool metadataOnly in new[] { false, true })
        {
            var error = Assert.Throws<XelibFormatException>(() => XelibReader.Read(forged, metadataOnly: metadataOnly));
            Assert.Contains("reserved compiler/runtime prefix", error.Message);
        }
    }

    [Fact]
    public void NativeRuntimeExportsMatchDefinitionsAndExcludeRemovedQueueApi()
    {
        string[] definitions = Regex.Matches(NativeExceptionRuntime.Source,
            "extern \"C\"[^\n]*?(__xenon_[a-z_]+)\\(")
            .Select(match => match.Groups[1].Value).Distinct().Order().ToArray();
        Assert.Equal(NativeExceptionRuntime.ExportedSymbols.Order(), definitions);
        Assert.DoesNotContain("__xenon_async_root_current", NativeExceptionRuntime.Source);
        Assert.DoesNotContain("__xenon_async_root_post", NativeExceptionRuntime.Source);
    }

    [Fact]
    public void GeneratedHelpersRemainUsableAndModuleLocal()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Example;
            struct Result { public static void operator resolve(Result& result, int value) {} }
            struct Operation {
                public static bool operator await(readonly Operation& op, storage<int>& value, function void() next)
                { value = 42; return true; }
            }
            async Result Work() { return await Operation(); }
            int Main() { Work(); return 0; }
            """));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.Contains("define internal ptr @__xenon_resume_create", ir);
        Assert.Contains("define internal void @__xenon_resume_request", ir);
        Assert.Contains("define internal noalias ptr @__xenon_malloc", ir);
        Assert.DoesNotContain("define linkonce_odr void @__xenon_resume_", ir);
    }

    [Fact]
    public void NativeExportKeepsOnlyItsWrapperExternallyVisible()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Example;
            export int Probe() { try { throw 42; } catch (readonly int& value) { return value; } return 0; }
            int Main() { return Probe(); }
            """));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.Matches(@"define internal [^\r\n]*@__xenon_export_implementation_", ir);
        Assert.Matches(@"define (?!internal)[^\r\n]*@Example_Probe\(", ir);
    }

    private static byte[] Rewrite(byte[] bytes, XelibSectionKind kind, byte[] payload)
    {
        var container = XelibContainer.Read(bytes);
        var manifest = XelibMetadataReader.Read(bytes).Manifest;
        var sections = container.Sections.Where(pair => pair.Key != (uint)XelibSectionKind.Manifest)
            .ToDictionary(pair => (XelibSectionKind)pair.Key, pair => pair.Value.ToArray());
        sections[kind] = payload;
        string identity = XelibWriter.ComputeContentIdentity(manifest.Name, manifest.Version,
            sections.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
        sections[XelibSectionKind.Manifest] = XelibJson.Serialize(manifest with { ContentIdentity = identity });
        return XelibContainer.Write(sections.Select(pair => new XelibSection(pair.Key, XelibSectionFlags.Required, pair.Value)));
    }
}
