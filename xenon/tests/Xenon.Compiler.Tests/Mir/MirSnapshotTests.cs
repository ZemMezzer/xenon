using System.Globalization;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirSnapshotTests
{
    [Theory]
    [InlineData("control", false)]
    [InlineData("control", true)]
    [InlineData("cleanup", false)]
    [InlineData("cleanup", true)]
    [InlineData("exceptions", false)]
    [InlineData("exceptions", true)]
    [InlineData("coroutine", false)]
    [InlineData("coroutine", true)]
    public void ProductionPipelineSnapshots(string name, bool lowered)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "Mir", "Snapshots");
        var compilation = Compilation.Create(SourceText.From(
            File.ReadAllText(Path.Combine(directory, name + ".xe")), name + ".xe"));
        Assert.Empty(compilation.Diagnostics);
        var function = compilation.GetMirFunctions(lowered).Single(function => function.Symbol.Name == "Run");
        string expected = File.ReadAllText(Path.Combine(directory, name + (lowered ? ".lowered.mir" : ".initial.mir")));
        Assert.Equal(expected.Replace("\r\n", "\n"), MirPrinter.Dump(function));
    }
    [Fact]
    public void ScalarLoweringSnapshot()
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace Example; int Main() { return 7; }"));
        MirFunction function = MirLowerer.Lower(compilation.SemanticModel.Functions.Single(), compilation.SemanticModel.TypeFactory);
        Assert.Equal("""
            fn Example.Main -> int {
              entry bb0

            bb0:
              return const 7: int

            bb1:
              resume.unwind
            }
            """.Replace("\r\n", "\n") + "\n", MirPrinter.Dump(function));
    }

    [Fact]
    public void FormattingIsCultureIndependentAndEscapesStrings()
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace Example; double Main() { return 1.5; }"));
        MirFunction function = MirLowerer.Lower(compilation.SemanticModel.Functions.Single(), compilation.SemanticModel.TypeFactory);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sr-Latn-RS");
            Assert.Contains("const 1.5: double", MirPrinter.Dump(function));
            Assert.Contains("\\n", MirPrinter.Operand(new MirConstant("a\nb", new TypeFactory().PointerTo(BuiltinTypes.Byte))));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void SourceMappingIsOptInAndStable()
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace Example; int Main() { return 7; }", "sample.xe"));
        MirFunction function = MirLowerer.Lower(compilation.SemanticModel.Functions.Single(), compilation.SemanticModel.TypeFactory);
        Assert.DoesNotContain("sample.xe", MirPrinter.Dump(function));
        Assert.Contains("\"sample.xe\"@", MirPrinter.Dump(function, includeSource: true));
        Assert.Equal(MirPrinter.Dump(function, true), MirPrinter.Dump(function, true));
    }
}
