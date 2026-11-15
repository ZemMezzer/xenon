using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowHardeningCallableTests
{
    [Theory]
    [InlineData("p", true)] [InlineData("q", false)]
    public void InvokedRawCaptureChecksMutation(string captured, bool invalid) => Check(
        "void Test() { int* p = new int(); int* q = new int(); function void() f = [" + captured + "]() => { *" + captured +
        " = 42; }; readonly int& r = *p; f(); Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceMutation);

    [Fact]
    public void UnusedRawCaptureDoesNotCreateACheckedLoan() => Check(
        "void Test() { int* p = new int(); function void() f = [p]() => { *p = 42; }; readonly int& r = *p; Observe(r); }",
        false, DiagnosticIds.BorrowedPlaceMutation);

    [Theory]
    [InlineData("readonly int&", false)] [InlineData("int&", true)]
    public void CapturedReadRespectsExclusiveLoans(string reference, bool invalid) => Check(
        "void Test() { int* p = new int(); function int() f = [p]() => { return *p; }; " + reference +
        " r = *p; int value = f(); Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceAccess);

    [Theory]
    [InlineData("readonly int&", false)] [InlineData("int&", true)]
    public void OrdinaryReadRespectsExclusiveLoans(string reference, bool invalid) => Check(
        "int Read(int* p) { return *p; } void Test() { int* p = new int(); " + reference +
        " r = *p; int value = Read(p); Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceAccess);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ClosureEffectsSurviveOrdinaryWrappers(bool indirect) => Check(
        "void Invoke(function void() f) { f(); } void Run(int* p) { function void() f = [p]() => { *p = 42; }; " +
        (indirect ? "Invoke(f);" : "f();") + " } void Test() { int* p = new int(); readonly int& r = *p; Run(p); Observe(r); }",
        true, DiagnosticIds.BorrowedPlaceMutation);

    [Theory]
    [InlineData("readonly int&", false)] [InlineData("int&", true)]
    public void ReaderPreservesItsCategoryThroughCallableWrappers(string reference, bool invalid) => Check(
        "int Invoke(function int() f) { return f(); } int Run(int* p) { function int() f = [p]() => { return *p; }; return Invoke(f); } " +
        "void Test() { int* p = new int(); " + reference + " r = *p; int value = Run(p); Observe(r); }",
        invalid, DiagnosticIds.BorrowedPlaceAccess);

    [Fact]
    public void NestedCapturedCallableStillChecksMutation() => Check(
        "void Test() { int* p = new int(); function void() f = [p]() => { *p = 42; }; function void() g = [f]() => { f(); }; " +
        "readonly int& r = *p; g(); Observe(r); }", true, DiagnosticIds.BorrowedPlaceMutation);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void UnknownInvokerUsesTheKnownCaptureContract(bool writes)
    {
        var source = Compilation.Create(SourceText.From(
            "namespace Lib; public void Invoke(function void() f) { f(); }", "unknown-invoker-lib.xe"));
        Assert.False(source.HasErrors, string.Join('\n', source.Diagnostics));
        var imported = XelibReader.Read(XelibWriter.Write(source, new XelibWriteOptions("UnknownInvoker")), metadataOnly: true);
        imported.GlobalNamespace.Namespaces.Single().Functions.Single(function => function.Name == "Invoke").MirLifetimeSummary = null;
        var compilation = Compilation.Create(new CompilationOptions(), [imported], SourceText.From(
            "using Lib; namespace Test; extern void Observe(readonly int& value); " +
            "void Test() { int* p = new int(); function void() f = [p]() => { " +
            (writes ? "*p = 42;" : "int value = *p;") + " }; readonly int& r = *p; Invoke(f); Observe(r); }", "unknown-invoker-client.xe"));
        if (!writes) Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        else Assert.Equal(DiagnosticIds.BorrowedPlaceMutation,
            Assert.Single(compilation.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)).Id);
    }

    [Fact]
    public void CaptureSlotsDoNotOverwriteReceiverStorage() => Check(
        "struct Holder { public int* P; public void Test() { int* p = this.P; function void() f = [p]() => { *p = 42; }; " +
        "readonly int& r = *this.P; f(); Observe(r); } }", true, DiagnosticIds.BorrowedPlaceMutation);

    [Theory]
    [InlineData("x", true)] [InlineData("y", false)]
    public void CaptureSlotsDoNotOverwriteReferenceParameterCarriers(string target, bool invalid) => Check(
        "void Test(int& x, int& y) { int* p = &" + target + "; function void() f = [p]() => { *p = 42; }; " +
        "readonly int& r = x; f(); Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceMutation);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExportedWrapperUsesOrdinaryParameterEffects(bool read)
    {
        string library = read ? "public int Run(int* p) { function int() f = [p]() => { return *p; }; return f(); }" :
            "public void Run(int* p) { function void() f = [p]() => { *p = 42; }; f(); }";
        var source = Compilation.Create(SourceText.From("namespace Lib; " + library, "closure-lib.xe"));
        Assert.False(source.HasErrors, string.Join('\n', source.Diagnostics));
        var imported = XelibReader.Read(XelibWriter.Write(source, new XelibWriteOptions("ClosureEffects")), metadataOnly: true);
        var compilation = Compilation.Create(new CompilationOptions(), [imported], SourceText.From(
            "using Lib; namespace Test; extern void Observe(readonly int& value); void Test() { int* p = new int(); " +
            (read ? "int&" : "readonly int&") + " r = *p; Run(p); Observe(r); }", "closure-client.xe"));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id ==
            (read ? DiagnosticIds.BorrowedPlaceAccess : DiagnosticIds.BorrowedPlaceMutation));
    }

    private static void Check(string body, bool invalid, string expected)
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; extern void Observe(readonly int& value); " + body, "callable-borrow.xe"));
        if (!invalid) Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        else Assert.Equal(expected, Assert.Single(compilation.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)).Id);
    }
}
