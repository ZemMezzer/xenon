using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowHardeningBoundaryTests
{
    private const string Protocol = """
        namespace Test;
        struct Result {
            public static void operator resolve(Result& value, int result) {}
            public static void operator reject(Result& value, int error) {}
            public static bool operator await(readonly Result& value, storage<int>& result, function void() next) { result = 42; return true; }
        }
        struct Operation {
            public static bool operator await(readonly Operation& value, storage<int>& result, function void() next) { result = 1; return true; }
        }
        """;

    [Theory]
    [InlineData("Result Make() { unique<int> p = new int(); return Read(&*p); }")]
    [InlineData("Result Make() { unique<int> p = new int(); int* alias = &*p; return Read(alias); }")]
    [InlineData("Result Make() { unique<int> p = new int(); int* alias = &*p; unique<int> q = move p; return Read(alias); }")]
    [InlineData("Result Make(unique<int> p) { return Read(&*p); }")]
    [InlineData("Result Make() { using var p = new int(); return Read(p); }")]
    [InlineData("struct H { public unique<int> P; } Result Make() { H h = H(); h.P = new int(); return Read(&*h.P); }")]
    public void RawAsyncBorrowCannotOutliveItsKnownUniqueOwner(string source)
    {
        var compilation = Compile("async Result Read(int* p) { await Operation(); return *p; } " + source);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PendingBorrowedOperation);
    }

    [Theory]
    [InlineData("async Result Read(int* p) { await Operation(); return *p; } Result Make() { return Read(new int()); }")]
    [InlineData("async Result Read(int* p) { await Operation(); return *p; } Result Make(int* p) { return Read(p); }")]
    [InlineData("async Result Read(int* p) { await Operation(); return *p; } async Result Make() { unique<int> p = new int(); return await Read(&*p); }")]
    [InlineData("async Result Read(unique<int> p) { await Operation(); return *p; } Result Make() { unique<int> p = new int(); return Read(move p); }")]
    [InlineData("async Result Read(unique<int> p, int* alias) { await Operation(); return *alias; } Result Make() { unique<int> p = new int(); int* alias = &*p; return Read(move p, alias); }")]
    [InlineData("async Result Make() { unique<int> p = new int(); int* alias = &*p; await Operation(); return *alias; }")]
    [InlineData("async Result Read(int* p) { int value = *p; await Operation(); return value; } Result Make() { unique<int> p = new int(); return Read(&*p); }")]
    [InlineData("async Result Read(int* p) { await Operation(); return *p; } async Result Make() { using var p = new int(); return await Read(p); }")]
    [InlineData("struct H { public unique<int> P; } async Result Read(int* p) { await Operation(); return *p; } async Result Make() { H h = H(); h.P = new int(); return await Read(&*h.P); }")]
    public void RawAsyncBorrowPreservesValidHeapAndOwnershipTransfers(string source)
    {
        var compilation = Compile(source);
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RawAsyncBorrowTracksTheCurrentOwnerAfterMove(bool transferToOuterScope)
    {
        string transfer = transferToOuterScope ? "q = move p;" : "";
        var compilation = Compile("async Result Read(int* p) { await Operation(); return *p; } " +
            "async Result Make() { unique<int> q; Result task; { unique<int> p = new int(); int* alias = &*p; " +
            transfer + " task = Read(alias); } return await task; }");
        if (transferToOuterScope) Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        else
        {
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PendingBorrowedOperation);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RawAsyncBorrowTracksMovedAggregateOwnerFields(bool transferToOuterScope)
    {
        var compilation = Compile("struct H { public unique<int> P; } async Result Read(int* p) { await Operation(); return *p; } " +
            "async Result Make() { unique<int> q; Result task; { H h = H(); h.P = new int(); int* alias = &*h.P; " +
            (transferToOuterScope ? "q = move h.P;" : "") + " task = Read(alias); } return await task; }");
        if (transferToOuterScope) Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        else Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(Protocol + source, "borrow-hardening-boundary.xe"));
}
