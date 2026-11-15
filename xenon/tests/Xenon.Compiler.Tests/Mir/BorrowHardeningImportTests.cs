using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowHardeningImportTests
{
    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    public void ImportedMutationPreservesAllocationIdentityAcrossCasts(string target, bool invalid)
    {
        Check("public void Write(byte* p) { *p = cast<byte>(1); }",
            "void Test() { int* p = new int(); int* q = new int(); readonly int& r = *p; Write(cast<byte*>(cast<void*>(" + target +
            "))); Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    public void ImportedDeletionFollowsIndirectAliases(string target, bool invalid)
    {
        Check("public void End(int** p) { delete(*p); }",
            "void Test() { int* p = new int(); int* q = new int(); readonly int& r = *p; End(&" + target +
            "); Observe(r); }", invalid, DiagnosticIds.FreeWhileBorrowed);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    public void ImportedDeletionFollowsHeapStoredAliases(string target, bool invalid)
    {
        Check("public struct H { public int* P; } public void End(H* h) { delete(h->P); }",
            "void Test() { int* p = new int(); int* q = new int(); H* h = new H(); h->P = " + target +
            "; readonly int& r = *p; End(h); Observe(r); }", invalid, DiagnosticIds.FreeWhileBorrowed);
    }

    private static void Check(string library, string body, bool invalid, string expected)
    {
        var source = Compilation.Create(SourceText.From("namespace Lib; " + library, "library.xe"));
        Assert.False(source.HasErrors, string.Join('\n', source.Diagnostics));
        var imported = XelibReader.Read(XelibWriter.Write(source, new XelibWriteOptions("BorrowHardening")), metadataOnly: true);
        var compilation = Compilation.Create(new CompilationOptions(), [imported],
            SourceText.From("using Lib; namespace Test; extern void Observe(readonly int& value); " + body, "consumer.xe"));
        if (!invalid) Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        else Assert.Equal(expected, Assert.Single(compilation.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)).Id);
    }
}
