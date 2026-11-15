using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class BorrowHardeningDirectTests
{
    [Theory]
    [InlineData("A", true)]
    [InlineData("B", false)]
    public void ReturnedAggregateKeepsDistinctAllocationSites(string target, bool invalid)
    {
        Check("struct Pair { public int* A; public int* B; } Pair Make() { Pair value = Pair(); value.A = new int(); " +
            "value.B = new int(); return value; } void Test() { Pair value = Make(); readonly int& r = *value.A; *value." +
            target + " = 42; Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("q + i", true)]
    [InlineData("q", false)]
    public void RelativeRawOffsetsCanReachAnEarlierBorrowedElement(string expression, bool invalid)
    {
        Check("void Test(int i) { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 1; p[1] = 2; " +
            "int* q = &p[1]; readonly int& r = p[0]; q = " + expression + "; *q = 42; Observe(r); }",
            invalid, DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("x", true)]
    [InlineData("y", false)]
    public void RawStackAliasRespectsAnExclusiveLoan(string target, bool invalid)
    {
        Check("void Test() { int x = 0; int y = 0; int* p = &" + target +
            "; int& r = x; int value = *p; Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceAccess);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    public void HeapStoredAliasRespectsAnExclusiveLoan(string target, bool invalid)
    {
        Check("struct H { public int* P; } void Test() { int* p = new int(); int* q = new int(); H* h = new H(); h->P = " +
            target + "; int& r = *p; int value = *h->P; Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceAccess);
    }

    [Theory]
    [InlineData("x", true)]
    [InlineData("y", false)]
    public void RawStackAliasPreservesAnActiveCheckedLoan(string target, bool invalid)
    {
        Check("void Test() { int x = 0; int y = 0; int* p = &" + target +
            "; readonly int& r = x; *p = 42; Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    public void HeapStoredAliasPreservesAnActiveCheckedLoan(string target, bool invalid)
    {
        Check("struct H { public int* P; } void Test() { int* p = new int(); int* q = new int(); H* h = new H(); h->P = " +
            target + "; readonly int& r = *p; *h->P = 42; Observe(r); }", invalid, DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    public void HeapStoredAliasCannotFreeBorrowedStorage(string target, bool invalid)
    {
        Check("struct H { public int* P; } void Test() { int* p = new int(); int* q = new int(); H* h = new H(); h->P = " +
            target + "; readonly int& r = *p; delete(h->P); Observe(r); }", invalid, DiagnosticIds.FreeWhileBorrowed);
    }

    [Theory]
    [InlineData("p", false, true)]
    [InlineData("q", false, false)]
    [InlineData("p", true, true)]
    [InlineData("q", true, false)]
    public void PointerCastDoesNotChangeAllocationIdentity(string target, bool throughCall, bool invalid)
    {
        string action = throughCall ? "Write(bytes);" : "*bytes = cast<byte>(1);";
        Check("void Write(byte* p) { *p = cast<byte>(1); } void Test() { int* p = new int(); int* q = new int(); " +
            "byte* bytes = cast<byte*>(cast<void*>(" + target + ")); readonly int& r = *p; " + action + " Observe(r); }",
            invalid, DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData("p", true)]
    [InlineData("q", false)]
    public void VoidPointerCastCannotHideBorrowedStorageFromFree(string target, bool invalid)
    {
        Check("void Test() { int* p = new int(); int* q = new int(); readonly int& r = *p; free(cast<void*>(" + target +
            ")); Observe(r); }", invalid, DiagnosticIds.FreeWhileBorrowed);
    }

    [Fact]
    public void MutableReferenceKeepsItsOwnAccessAuthority()
    {
        Check("void Test() { int x = 0; int& r = x; int* p = &r; *p = 42; Observe(r); }", false,
            DiagnosticIds.BorrowedPlaceMutation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RawAliasCannotUseAnEarlierLoanDuringAnExclusiveReborrow(bool invalid)
    {
        Check("void Test(int& r) { int* p = &r; int& q = r; " +
            (invalid ? "*p = 42; Observe(q);" : "Observe(q); *p = 42;") + " }", invalid, DiagnosticIds.BorrowedPlaceMutation);
    }

    private static void Check(string body, bool invalid, string expected)
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; extern void Observe(readonly int& value); " + body,
            "borrow-hardening-direct.xe"));
        if (!invalid) Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
        else
        {
            var diagnostic = Assert.Single(compilation.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error));
            Assert.Equal(expected, diagnostic.Id);
        }
    }
}
