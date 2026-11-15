using System.Collections.Immutable;
using System.Text;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xunit;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowHardeningReadImportTests
{
    [Theory]
    [InlineData(false, false, "p", true)]
    [InlineData(false, true, "p", false)]
    [InlineData(false, false, "q", false)]
    [InlineData(true, false, "p", true)]
    [InlineData(true, true, "p", false)]
    [InlineData(true, false, "q", false)]
    public void ImportedReadsRespectExclusiveButNotSharedLoans(bool legacy, bool shared, string target, bool invalid)
    {
        var source = Compilation.Create(SourceText.From("namespace Lib; public int Read(int* p) { return *p; }"));
        Assert.False(source.HasErrors, string.Join('\n', source.Diagnostics));
        var image = XelibWriter.Write(source, new("Reads"));
        if (legacy) image = Rewrite(image, record => record with
        {
            ReadCoverageComplete = false,
            Effects = [.. record.Effects.Where(effect => effect.Transition != "StorageRead")],
        });
        var symbols = Encoding.UTF8.GetString(XelibContainer.Read(image).GetRequiredSection(XelibSectionKind.Symbols).AsSpan());
        Assert.Equal(!legacy, symbols.Contains("\"readCoverageComplete\":true", StringComparison.Ordinal));
        var app = Compilation.Create(new CompilationOptions(), [XelibReader.Read(image, metadataOnly: true)],
            SourceText.From("using Lib; namespace App; extern void Observe(readonly int& r); int Test() { int* p=new int(); int* q=new int(); " +
                (shared ? "readonly " : "") + "int& r=*p; int value=Read(" + target + "); Observe(r); return value; }"));
        if (invalid) Assert.Contains(app.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowedPlaceAccess);
        else Assert.False(app.HasErrors, string.Join('\n', app.Diagnostics));
    }

    [Fact]
    public void ReadEffectsCannotClaimAnExitLifetimeTransition()
    {
        var source = Compilation.Create(SourceText.From("namespace Lib; public int Read(int* p) { return *p; }"));
        var image = Rewrite(XelibWriter.Write(source, new("Reads")), record => record with
        { Effects = [.. record.Effects.Select(effect => effect.Transition != "StorageRead" ? effect : effect with { Phase = "Exit" })] });
        Assert.Throws<XelibFormatException>(() => XelibReader.Read(image, metadataOnly: true));
    }

    private static byte[] Rewrite(byte[] image, Func<XelibLifetimeSummaryRecord, XelibLifetimeSummaryRecord> change)
    {
        var container = XelibContainer.Read(image);
        var manifest = XelibJson.Deserialize<XelibManifest>(container.GetRequiredSection(XelibSectionKind.Manifest).AsSpan(), null);
        var records = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(container.GetRequiredSection(XelibSectionKind.Symbols).AsSpan(), null);
        var sections = container.Sections.Where(pair => pair.Key != (uint)XelibSectionKind.Manifest)
            .ToDictionary(pair => (XelibSectionKind)pair.Key, pair => pair.Value.ToArray());
        sections[XelibSectionKind.Symbols] = XelibJson.Serialize(records.Select(record => record.Name != "Read" ? record :
            record with { LifetimeSummary = change(record.LifetimeSummary!) }).ToImmutableArray());
        sections[XelibSectionKind.Manifest] = XelibJson.Serialize(manifest with { ContentIdentity = XelibWriter.ComputeContentIdentity(manifest.Name,
            manifest.Version, sections.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)pair.Value))) });
        return XelibContainer.Write(sections.Select(pair => new XelibSection(pair.Key, XelibSectionFlags.Required, pair.Value)));
    }
}
