using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirPointerLifetimeTests
{
    private const string Prelude = """
        namespace PointerLifetime;
        struct Item { public int Value; public int Read() { return Value; } }
        struct Holder { public Item* Value; }
        struct Outer { public Holder Inner; }
        """;

    [Theory]
    [InlineData("void Reset(Item* p, bool c) { destruct(*p); if (c) *p = Item(); } int Test() { Item* p = new Item(); Reset(p, false); return p->Read(); }")]
    [InlineData("void Reset(Item* p, bool c) { destruct(*p); if (c) *p = Item(); } int Test(bool c) { Item* p = new Item(); Reset(p, c); if (!c) return p->Read(); return 0; }")]
    [InlineData("void Destroy(Item* a, Item* b, bool c) { Item* p; if (c) p = a; else p = b; delete(p); } int Test() { Item* a = new Item(); Item* b = new Item(); Destroy(a, b, true); return a->Read(); }")]
    [InlineData("void Destroy(Item* a, Item* b, bool c) { Item* p; if (c) p = a; else p = b; delete(p); } int Test(bool c) { Item* a = new Item(); Item* b = new Item(); Destroy(a, b, c); if (!c) return b->Read(); return 0; }")]
    [InlineData("void Recursive(Item* p, bool again) { delete(p); if (again) Recursive(new Item(), false); } int Test() { Item* p = new Item(); Recursive(p, true); return p->Read(); }")]
    [InlineData("void A(Item* p, bool c) { if (c) B(p, false); else delete(p); } void B(Item* p, bool c) { A(p, c); } int Test() { Item* p = new Item(); B(p, true); return p->Read(); }")]
    [InlineData("void Recursive(Item* p, int n) { delete(p); if (n > 0) Recursive(new Item(), n - 1); } int Test() { Item* p = new Item(); Recursive(p, 2); return p->Read(); }")]
    [InlineData("void Rebuild(Item* p, bool c) { if (c) *p = Item(); } int Test(bool c) { Item* p = new Item(); destruct(*p); Rebuild(p, c); if (!c) return p->Read(); return 0; }")]
    [InlineData("void Set(int* p) { *p = 42; } int Test() { int* saved = null; bool first = true; bool again = true; while (again) { int* p = new int(); if (first) { destruct(*p); saved = p; first = false; } else { Set(p); again = false; } } return *saved; }")]
    public void CorrelatedLifetimeViolationsAreErrors(string source) => ProvenViolationsAreErrors(source);

    [Theory]
    [InlineData("void Reset(Item* p, bool c) { destruct(*p); if (c) *p = Item(); } int Test() { Item* p = new Item(); Reset(p, true); return p->Read(); }")]
    [InlineData("void Destroy(Item* a, Item* b, bool c) { Item* p; if (c) p = a; else p = b; delete(p); } int Test() { Item* a = new Item(); Item* b = new Item(); Destroy(a, b, true); return b->Read(); }")]
    [InlineData("void Destroy(Item* a, Item* b, bool c) { Item* p; if (c) p = a; else p = b; delete(p); } int Test(bool c) { Item* a = new Item(); Item* b = new Item(); Destroy(a, b, c); if (!c) return a->Read(); return b->Read(); }")]
    [InlineData("void Forever(Item* p) { delete(p); Forever(new Item()); } int Test() { Item* p = new Item(); Forever(p); return p->Read(); }")]
    [InlineData("void Forever(Item* p, int n) { delete(p); if (n > 0) Forever(new Item(), n); } int Test() { Item* p = new Item(); Forever(p, 2); return p->Read(); }")]
    [InlineData("void Rebuild(Item* p, bool c) { if (c) *p = Item(); } int Test(bool c) { Item* p = new Item(); destruct(*p); Rebuild(p, c); if (c) return p->Read(); return 0; }")]
    [InlineData("void Rebuild(Item* p, bool c, bool d) { if (c) { if (d) *p = Item(); } } int Test(bool c, bool d) { Item* p = new Item(); destruct(*p); Rebuild(p, c, d); if (c) { if (d) return p->Read(); } return 0; }")]
    [InlineData("void Rebuild(Item* p) { *p = Item(); } void Wrapper(Item* p) { Rebuild(p); } int Test() { Item* p = new Item(); destruct(*p); Wrapper(p); return p->Read(); }")]
    [InlineData("void Set(int* p, int i) { p[i] = 42; } int Test(int i) { if (i != 0) return 0; int* p = new int(); destruct(*p); Set(p, i); int x = *p; free(p); return x; }")]
    [InlineData("void Set(int* p, int i) { *(p + i) = 42; } int Test() { int* p = new int(); destruct(*p); Set(p, 0); int x = *p; free(p); return x; }")]
    [InlineData("extern void MayRebuild(int* p); int Test() { int* p = new int(); destruct(*p); MayRebuild(p); return *p; }")]
    [InlineData("extern void MayRebuild(int* p); void Wrapper(int* p) { MayRebuild(p); } int Test() { int* p = new int(); destruct(*p); Wrapper(p); return *p; }")]
    [InlineData("extern void MayRebuild(int* p); void Wrapper(int* p) { destruct(*p); MayRebuild(p); } int Test() { int* p = new int(); Wrapper(p); return *p; }")]
    [InlineData("void Set(int* p) { *p = 42; } int Test() { int* saved = null; bool first = true; bool again = true; while (again) { int* p = new int(); if (first) { destruct(*p); saved = p; first = false; } else { Set(saved); again = false; } } return *saved; }")]
    [InlineData("void Set(int* p, int i) { p[i] = 42; throw 1; } int Test(int i) { if (i != 0) return 0; int* p = new int(); destruct(*p); try { Set(p, i); } catch (readonly int& error) { return *p; } return 0; }")]
    public void CorrelatedLifetimeValidProgramsAreAccepted(string source) => ValidUnsafeProgramsRemainAccepted(source);

    [Fact]
    public void ReconstructingCallStillInvalidatesAnActiveCheckedBorrow()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace CheckedLifetimeCall;
            extern void Observe(readonly int& value);
            void Reset(int* p) { destruct(*p); *p = 42; }
            void Test() { int* p = new int(); readonly int& borrow = *p; Reset(p); Observe(borrow); delete(p); }
            """));
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == DiagnosticIds.DestructWhileBorrowed);
    }

    [Fact]
    public void DeepSummariesAreIndependentOfDeclarationOrder()
    {
        var functions = Enumerable.Range(0, 70).Select(index => $"void F{index:D3}(Item* p) {{ " +
            (index == 69 ? "delete(p);" : $"F{index + 1:D3}(p);") + " }").ToArray();
        const string test = "int Test() { Item* p = new Item(); F000(p); return p->Read(); }";
        var forward = Compilation.Create(SourceText.From(Prelude + string.Join('\n', functions) + test));
        var reverse = Compilation.Create(SourceText.From(Prelude + string.Join('\n', functions.Reverse()) + test));
        Assert.True(forward.HasErrors);
        Assert.True(reverse.HasErrors);
        Assert.Contains(forward.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PointerUseAfterFree && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal(forward.Diagnostics.Select(diagnostic => (diagnostic.Id, diagnostic.Message)), reverse.Diagnostics.Select(diagnostic => (diagnostic.Id, diagnostic.Message)));
        Assert.Equal(forward.DumpLifetimeAnalysis(), reverse.DumpLifetimeAnalysis());
    }

    [Theory]
    [InlineData("void Write(int* p) { *p = 42; }", "Write(a);", DiagnosticIds.BorrowedPlaceMutation)]
    [InlineData("void Destroy(int* a, int* b, int n) { int* p; if (n == 0) p = a; else p = b; delete(p); }", "Destroy(a, b, 0);", DiagnosticIds.BorrowConflict)]
    [InlineData("void End(int* p, int i) { destruct(p[i]); }", "End(a, i);", DiagnosticIds.BorrowConflict)]
    public void IndirectOperationsCannotBypassCheckedLoans(string function, string call, string diagnostic)
    {
        var compilation = Compilation.Create(SourceText.From("namespace CheckedCalls; extern void Observe(readonly int& r); " + function +
            "void Test(int i) { int* a = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); int* b = new int(); readonly int& r = a[0]; " + call + " Observe(r); free(a); delete(b); }"));
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, item => item.Severity == DiagnosticSeverity.Error && item.Id == diagnostic);
    }

    [Theory]
    [InlineData("void End(int* p) { destruct(p[1]); }", "End(a);")]
    [InlineData("void Write(int* p) { *p = 42; }", "Write(b);")]
    public void DisjointOperationsPreserveCheckedLoans(string function, string call)
    {
        var compilation = Compilation.Create(SourceText.From("namespace DisjointCalls; extern void Observe(readonly int& r); " + function +
            "void Test() { int* a = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); int* b = new int(); readonly int& r = a[0]; " + call + " Observe(r); free(a); delete(b); }"));
        Assert.False(compilation.HasErrors, string.Join('\n', compilation.Diagnostics));
    }

    [Fact]
    public void MoreThan128IndependentDestructionEffectsSurvive()
    {
        const int count = 130;
        var source = Prelude + "void DestroyAll(" + string.Join(',', Enumerable.Range(0, count).Select(index => $"Item* p{index}")) + ") { " +
            string.Join(' ', Enumerable.Range(0, count).Select(index => $"delete(p{index});")) + " } int Test() { Item* p = new Item(); DestroyAll(" +
            string.Join(',', Enumerable.Range(0, count).Select(index => index == count - 1 ? "p" : "null")) + "); return p->Read(); }";
        var compilation = Compilation.Create(SourceText.From(source));
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PointerUseAfterFree && diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain("effect-budget", compilation.DumpLifetimeAnalysis());
    }

    [Fact]
    public void GuardBudgetIsExplicitAndDoesNotEraseLaterLocalProofs()
    {
        var parameters = string.Join(',', Enumerable.Range(0, 9).Select(index => $"bool c{index}"));
        var selection = string.Join(' ', Enumerable.Range(0, 9).Select(index => $"if (c{index}) p = a; else p = b;"));
        var compilation = Compilation.Create(SourceText.From(Prelude + "void Many(Item* a, Item* b, " + parameters +
            ") { Item* p = a; " + selection + " delete(p); } int Test(" + parameters +
            ") { Item* p = new Item(); Many(new Item(), new Item(), " + string.Join(',', Enumerable.Range(0, 9).Select(index => $"c{index}")) +
            "); delete(p); return p->Read(); }"));
        Assert.Contains("guard-context-budget", compilation.DumpLifetimeAnalysis());
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PointerUseAfterFree && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [InlineData("int Test() { int* p = new int(); int* q = p + 0; destruct(*p); return *q; }")]
    [InlineData("int Test() { int* p = new int(); int* q = p - 0; destruct(*p); return *q; }")]
    [InlineData("int Test() { int* p = new int(); int* q = p + (2 - 2); destruct(*p); return *q; }")]
    [InlineData("void Destroy(Item* p) { delete(p); } int Test() { Item* p = new Item(); Destroy(p); return p->Read(); }")]
    [InlineData("void Destroy(Item* p) { Item* q = p; delete(q); } int Test() { Item* p = new Item(); Destroy(p); return p->Read(); }")]
    [InlineData("void Destroy(Item* p, bool c) { if (c) delete(p); } int Test(bool c) { Item* p = new Item(); Destroy(p, c); return p->Read(); }")]
    [InlineData("void Destroy(Item* p, bool c) { if (c) delete(p); } int Test() { Item* p = new Item(); Destroy(p, true); return p->Read(); }")]
    [InlineData("void A(Item* p) { delete(p); } void B(Item* p) { A(p); } int Test() { Item* p = new Item(); B(p); return p->Read(); }")]
    [InlineData("void Destroy(Item* p) { delete(p); } void Test() { Item* p = new Item(); Destroy(p); Destroy(p); }")]
    [InlineData("void Destroy(Item* p) { delete(p); } void Test() { using var p = new Item(); Destroy(p); }")]
    [InlineData("void Destroy(Item*& p) { delete(p); } int Test() { Item* p = new Item(); Item* q = p; Destroy(p); return q->Read(); }")]
    [InlineData("void Destroy(Item** p) { delete(*p); } int Test() { Item* p = new Item(); Item* q = p; Destroy(&p); return q->Read(); }")]
    [InlineData("void Destroy(int[] p) { delete(p); } int Test() { int[] p = new int[2]; Destroy(p); return p[0]; }")]
    [InlineData("void Destroy(Item* p) { delete(p); throw 1; } int Test() { Item* p = new Item(); try { Destroy(p); } catch (readonly int& e) { return p->Read(); } return 0; }")]
    [InlineData("int Test() { int* p = new int(); destruct(*p); int* q = p + (3 - 3); return *q; }")]
    [InlineData("int* Same(int* p) { return p + 0; } int Test() { int* p = new int(); int* q = Same(p); destruct(*p); return *q; }")]
    [InlineData("void End(Item* p) { destruct(*p); } int Test() { Item* p = new Item(); End(p); return p->Read(); }")]
    [InlineData("void Destroy(Item* p, bool c) { if (c) delete(p); } int Test(bool c) { Item* p = new Item(); Destroy(p, c); c = false; return p->Read(); }")]
    [InlineData("void A(Item* p, bool c) { if (c) delete(p); } void B(Item* p, bool c) { A(p, !c); } int Test(bool c) { Item* p = new Item(); B(p, c); if (!c) return p->Read(); return 0; }")]
    [InlineData("void Destroy(Item*& p) { Item* q = p; p = new Item(); delete(q); } int Test() { Item* p = new Item(); Item* q = p; Destroy(p); return q->Read(); }")]
    [InlineData("void Destroy(Holder h) { delete(h.Value); } int Test() { Item* p = new Item(); Holder h = Holder(); h.Value = p; Destroy(h); return p->Read(); }")]
    [InlineData("void Destroy(Holder* h) { delete(h->Value); } int Test() { Item* p = new Item(); Holder h = Holder(); h.Value = p; Destroy(&h); return p->Read(); }")]
    [InlineData("void Release(void* p) { free(p); } int Test(int i) { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(4))); int* q = p + i; Release(cast<void*>(p)); return *q; }")]
    [InlineData("void Destroy(Item* p) { delete(p); } int Test(int n) { Item* saved = null; while (n > 0) { Item* p = new Item(); saved = p; Destroy(p); n -= 1; } return saved->Read(); }")]
    [InlineData("void Destroy(Item* p, bool c) { if (c) delete(p); } int Test(bool c) { Item* p = new Item(); for (int i = 0; i < 2; i++) { Destroy(p, c); } return p->Read(); }")]
    [InlineData("void Destroy(Item* p) { delete(p); } int Test() { Item* p = new Item(); try { throw 1; } catch (readonly int& e) {} finally { Destroy(p); } return p->Read(); }")]
    [InlineData("bool Maybe(Item* p, bool c) { if (c) delete(p); return c; } int Test(bool c) { Item* p = new Item(); bool dead = Maybe(p, c); if (dead) return p->Read(); return 0; }")]
    [InlineData("void End(int* p) { destruct(p[0]); } void Test() { int* p = new int(); destruct(*p); End(p); }")]
    [InlineData("int Maybe(Item* p, bool c) { if (c) { delete(p); return 1; } return 0; } int Test(bool c) { Item* p = new Item(); int status = Maybe(p, c); if (status == 1) return p->Read(); return 0; }")]
    [InlineData("Item* Get(Item*& p) { return p; } Item* Test() { using var p = new Item(); Item* q = p; return Get(q); }")]
    [InlineData("Item* Get(Holder* h) { return h->Value; } Item* Test() { using var p = new Item(); Holder h = Holder(); h.Value = p; return Get(&h); }")]
    public void PrecisionAndCallViolationsAreErrors(string source) => ProvenViolationsAreErrors(source);

    [Theory]
    [InlineData("void Destroy(Item* p, bool c) { if (c) delete(p); } int Test() { Item* p = new Item(); Destroy(p, false); return p->Read(); }")]
    [InlineData("void Destroy(Item* p, bool c) { if (c) delete(p); } int Test(bool c) { Item* p = new Item(); Destroy(p, c); if (!c) return p->Read(); return 0; }")]
    [InlineData("void Destroy(Item* p) { delete(p); } void Test(int n) { while (n > 0) { Item* p = new Item(); p->Read(); Destroy(p); n -= 1; } }")]
    [InlineData("void Destroy(Item* p) { throw 1; } int Test() { Item* p = new Item(); try { Destroy(p); } catch (readonly int& e) { return p->Read(); } return 0; }")]
    [InlineData("void End(int* p) { destruct(*p); } void Test() { int* p = new int(); End(p); free(p); }")]
    [InlineData("void Destroy(Item* p) { p = new Item(); delete(p); } int Test() { Item* p = new Item(); Destroy(p); return p->Read(); }")]
    [InlineData("int Test() { int* p = new int(); *p = 42; int* q = p + (2 - 2); int result = *q; delete(p); return result; }")]
    [InlineData("void Reset(Item* p) { destruct(*p); *p = Item(); } int Test() { Item* p = new Item(); Reset(p); int value = p->Read(); delete(p); return value; }")]
    [InlineData("void Reset(Item* p, bool c) { destruct(*p); if (c) *p = Item(); } int Test(bool c) { Item* p = new Item(); Reset(p, c); if (c) return p->Read(); return 0; }")]
    [InlineData("bool Maybe(Item* p, bool c) { if (c) delete(p); return c; } int Test(bool c) { Item* p = new Item(); bool dead = Maybe(p, c); if (!dead) { int value = p->Read(); delete(p); return value; } return 0; }")]
    [InlineData("bool A(Item* p, bool c) { if (c) delete(p); return c; } bool B(Item* p, bool c) { return A(p, c); } int Test(bool c) { Item* p = new Item(); if (!B(p, c)) return p->Read(); return 0; }")]
    [InlineData("void End(int* p) { destruct(p[1]); } void Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 1; p[1] = 2; destruct(*p); End(p); free(p); }")]
    [InlineData("void Destroy(Item* p, bool c) { if (c) delete(p); } int Test(bool c) { Item* p = new Item(); bool d = c; Destroy(p, d); if (!c) return p->Read(); return 0; }")]
    [InlineData("void A(Item* p, bool c) { if (c) delete(p); } void B(Item* p, bool c) { A(p, !c); } int Test(bool c) { Item* p = new Item(); B(p, c); if (c) return p->Read(); return 0; }")]
    [InlineData("void Destroy(Item*& p) { Item* q = p; p = new Item(); delete(q); } int Test() { Item* p = new Item(); Destroy(p); return p->Read(); }")]
    [InlineData("void Borrow(Item* p) { p->Read(); } void Test() { using var p = new Item(); Borrow(p); }")]
    [InlineData("extern void Unknown(Item* p); int Test(Item* p) { Unknown(p); return p->Read(); }")]
    [InlineData("void A(Item* p, bool c) { if (c) B(p, false); } void B(Item* p, bool c) { if (c) A(p, false); } int Test(Item* p) { A(p, true); return p->Read(); }")]
    [InlineData("void Drop(shared<Item> p) {} int Test() { shared<Item> p = new Item(); shared<Item> q = p; Drop(q); return p->Read(); }")]
    [InlineData("unique<Item> Pass(unique<Item> p) { return move p; } int Test() { unique<Item> p = new Item(); Item* alias = &*p; unique<Item> q = Pass(move p); return alias->Read(); }")]
    [InlineData("void Destroy(Item* p) { delete(p); } void Test(int n) { while (n > 0) { for (int j = 0; j < 2; j++) { Item* p = new Item(); p->Read(); Destroy(p); } n -= 1; } }")]
    [InlineData("void Destroy(Item* p) { delete(p); } int Test(bool c) { Item* a = new Item(); Item* b = new Item(); Item* p; if (c) p = a; else p = b; Destroy(p); if (c) return b->Read(); return a->Read(); }")]
    [InlineData("void D(Item* p) { delete(p); throw 1; } int Test() { Item* p = new Item(); try { D(p); } catch (readonly bool& e) { return p->Read(); } return 0; }")]
    [InlineData("void D(Item* p) { delete(p); throw 1; } void B(Item* p) { D(p); } int Test() { Item* p = new Item(); try { B(p); } catch (readonly bool& e) { return p->Read(); } return 0; }")]
    [InlineData("int Maybe(Item* p, bool c) { if (c) { delete(p); return 1; } return 0; } int Test(bool c) { Item* p = new Item(); int status = Maybe(p, c); if (status == 0) { int value = p->Read(); delete(p); return value; } return 0; }")]
    [InlineData("Item* Get(Holder* h) { return h->Value; } Item* Test() { Holder h = Holder(); h.Value = new Item(); return Get(&h); }")]
    [InlineData("int Maybe(Item* p, bool c) { if (c) { delete(p); return 1; } return 0; } int Test() { int sum = 0; for (int i = 0; i < 4; i++) { Item* p = new Item(); int status = Maybe(p, i == 3); if (status == 0) { sum += p->Read(); delete(p); } } return sum; }")]
    public void PrecisionAndCallValidProgramsAreAccepted(string source) => ValidUnsafeProgramsRemainAccepted(source);

    [Theory]
    [InlineData("Item* Test() { using var p = new Item(); return p; }")]
    [InlineData("Item* Test() { using var p = new Item(); Item* q = p; return q; }")]
    [InlineData("Holder Test() { using var p = new Item(); Holder h = Holder(); h.Value = p; return h; }")]
    [InlineData("Outer Test() { using var p = new Item(); Outer h = Outer(); h.Inner.Value = p; return h; }")]
    [InlineData("int* Test() { int value = 42; return &value; }")]
    [InlineData("int* Test() { int value = 42; int* p = &value; return p; }")]
    [InlineData("int Test() { Item* p = new Item(); Item* q = p; delete(p); return q->Read(); }")]
    [InlineData("int Test(bool c) { Item* p = new Item(); if (c) delete(p); return p->Value; }")]
    [InlineData("int Test() { Item* p; { using var q = new Item(); p = q; } return p->Value; }")]
    [InlineData("void Test() { Item* p = new Item(); Item* q = p; delete(p); delete(q); }")]
    [InlineData("void Test() { using var p = new Item(); delete(p); }")]
    [InlineData("int Test() { Item* p = new Item(); Holder h = Holder(); h.Value = p; delete(p); return h.Value->Read(); }")]
    [InlineData("int Test() { Item* p = new Item(); void* b = cast<void*>(p); Item* q = cast<Item*>(b); delete(p); return q->Value; }")]
    [InlineData("Item* Identity(Item* p) { return p; } Item* Test() { using var p = new Item(); return Identity(p); }")]
    [InlineData("int Test() { Item* p; while (true) { using var q = new Item(); p = q; break; } return p->Value; }")]
    [InlineData("int Test() { Item* p = new Item(); try { delete(p); throw 1; } catch (readonly int& e) { return p->Value; } }")]
    [InlineData("int Test() { int* p = cast<int*>(malloc(sizeof(int))); int* q = p; free(p); return *q; }")]
    [InlineData("void Test() { void* p = malloc(sizeof(int)); void* q = p; free(p); free(q); }")]
    [InlineData("int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); int* q = p + 1; free(p); return *q; }")]
    [InlineData("int Test(int i) { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); int* q = &p[i]; free(p); return *q; }")]
    [InlineData("int* Next(int* p) { return p + 1; } int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); int* q = Next(p); free(p); return *q; }")]
    [InlineData("int* First(int* p) { return &p[0]; } int Test() { int* p = new int(); destruct(*p); int* q = First(p); return *q; }")]
    [InlineData("int Test() { int* p = new int(); destruct(*p); return p[0]; }")]
    [InlineData("int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); int* q = &p[1]; destruct(*q); p[0] = 42; return *q; }")]
    [InlineData("int Test() { int[] a = new int[2]; int[] b = a; delete(a); return b[0]; }")]
    [InlineData("int Test() { Item* p = new Item(); Holder[] a = new Holder[2]; a[0].Value = p; delete(p); return a[0].Value->Read(); }")]
    [InlineData("int Test() { Item* p; { unique<Item> owner = new Item(); p = &*owner; } return p->Read(); }")]
    [InlineData("Item* Test(bool c) { using var p = new Item(); if (c) return p; return new Item(); }")]
    [InlineData("int Test(bool c) { Item* p = new Item(); while (c) { delete(p); c = false; } return p->Read(); }")]
    [InlineData("int Test(bool c) { Item* p = new Item(); try { if (c) throw 1; } catch (readonly int& e) {} finally { delete(p); } return p->Read(); }")]
    [InlineData("Holder Identity(Holder h) { return h; } Item* Test() { using var p = new Item(); Holder h = Holder(); h.Value = p; return Identity(h).Value; }")]
    [InlineData("struct Box { public Item* Value; public Box(Item* p) { Value = p; } } Box Test() { using var p = new Item(); return Box(p); }")]
    [InlineData("function int() Test() { using var p = new Item(); return [p]() => { return p->Read(); }; }")]
    [InlineData("int Test() { Item* p = new Item(); Holder* h = new Holder(); h->Value = p; delete(p); return h->Value->Read(); }")]
    [InlineData("int Test() { Item* a = new Item(); Item* b = new Item(); Item* p = a; a <-> b; delete(b); return p->Value; }")]
    [InlineData("int Test() { int* p = new int(); atomic<int*> slot = p; delete(p); int* q = slot; return *q; }")]
    [InlineData("void Test(Holder& h) { using var p = new Item(); h.Value = p; }")]
    [InlineData("void Test(Holder* h) { using var p = new Item(); h->Value = p; }")]
    [InlineData("void Test(Holder[] h) { using var p = new Item(); h[0].Value = p; }")]
    [InlineData("void Test(shared<Holder> h) { using var p = new Item(); h->Value = p; }")]
    [InlineData("struct Pair { public Item* A; public Item* B; } Pair Make() { Pair p = Pair(); p.A = new Item(); p.B = p.A; return p; } int Test() { Pair p = Make(); delete(p.A); return p.B->Read(); }")]
    [InlineData("static struct Saved { public static Item* Value; } void Test() { using var p = new Item(); Saved.Value = p; }")]
    [InlineData("static struct Saved { public static int* Value; } void Test() { int[] local = int[1]; int* alias = &local[0]; Saved.Value = alias; }")]
    public void ProvenViolationsAreErrors(string source)
    {
        var compilation = Compilation.Create(SourceText.From(Prelude + source, "pointer-lifetime.xe"));
        Assert.True(compilation.HasErrors, compilation.HasErrors ? null : compilation.DumpMir());
        Assert.Contains(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error &&
            d.Id is "XE2291" or "XE2292" or "XE2293");
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error &&
            d.Id is not ("XE2291" or "XE2292" or "XE2293"));
    }

    [Theory]
    [InlineData("Item* Test() { return new Item(); }")]
    [InlineData("int Test(Item* p) { return p->Read(); }")]
    [InlineData("void Test(Item* p) { delete(p); }")]
    [InlineData("int Test() { Item* p = new Item(); Item* q = p; int n = q->Read(); delete(p); return n; }")]
    [InlineData("int Test() { using var p = new Item(); Item* q = p; return q->Read(); }")]
    [InlineData("void Test(int n) { while (n > 0) { Item* p = new Item(); p->Read(); delete(p); n -= 1; } }")]
    [InlineData("int Test(bool c) { Item* a = new Item(); Item* b = new Item(); Item* p; if (c) { delete(a); p = b; } else { p = a; } return p->Read(); }")]
    [InlineData("int Test() { Item* p = new Item(); if (false) delete(p); int n = p->Read(); delete(p); return n; }")]
    [InlineData("int Test() { unique<Item> p = new Item(); unique<Item> q = move p; return q->Read(); }")]
    [InlineData("int Test() { shared<Item> p = new Item(); shared<Item> q = p; { shared<Item> r = q; } return p->Read(); }")]
    [InlineData("int Test(bool c) { Item* a = new Item(); Item* b = new Item(); Item* p; Item* q; if (c) { p = a; q = b; } else { p = null; q = a; } delete(p); return q->Read(); }")]
    [InlineData("struct Pair { public Item* A; public Item* B; } Pair Make() { Pair h = Pair(); h.A = new Item(); h.B = new Item(); return h; } int Test() { Pair h = Make(); delete(h.A); return h.B->Read(); }")]
    [InlineData("extern Item* Create(); int Test() { Item* p = Create(); return p->Read(); }")]
    [InlineData("extern void Use(Item* p); void Test(Item* p) { Use(p); delete(p); }")]
    [InlineData("int Test() { unique<Item> p = new Item(); Item* alias = &*p; unique<Item> q = move p; return alias->Read(); }")]
    [InlineData("int Test() { Item* p = new Item(); Holder[] a = new Holder[2]; a[0].Value = p; a[1].Value = new Item(); delete(p); return a[1].Value->Read(); }")]
    [InlineData("int Test() { int* p = cast<int*>(malloc(sizeof(int))); *p = 42; int n = *p; free(p); return n; }")]
    [InlineData("Item* Make() { return new Item(); } void Test(int n) { Item* keep = null; while (n > 0) { Item* current = Make(); if (keep != null) { delete(current); keep->Read(); } else { keep = current; } n -= 1; } }")]
    [InlineData("extern void Reset(Item*& p); int Test() { Item* p = new Item(); delete(p); Reset(p); return p->Read(); }")]
    [InlineData("void Replace(Item*& p) { p = new Item(); } int Test() { Item* original = new Item(); Item* p = original; Replace(p); delete(original); return p->Read(); }")]
    [InlineData("int Test() { Item* a = new Item(); Item* b = new Item(); Item* p = a; a <-> b; delete(a); return p->Value; }")]
    [InlineData("extern void Replace(Item** p); int Test() { Item* original = new Item(); Item* p = original; Replace(&p); delete(original); return p->Read(); }")]
    [InlineData("int* Test() { atomic<int*> slot = new int(); return slot; }")]
    [InlineData("int* Test() { atomic<int*> slot = null; return slot = new int(); }")]
    [InlineData("void Test(Holder& h) { using var p = new Item(); h.Value = p; h.Value = null; }")]
    [InlineData("void Test(Holder[] h) { using var p = new Item(); h[0].Value = p; h[0].Value = null; }")]
    [InlineData("static struct Saved { public static Item* Value; } void Test() { Saved.Value = new Item(); delete(Saved.Value); }")]
    [InlineData("static struct Saved { public static int* Value; } int Test() { int[] local = int[1]; local[0] = 42; int* alias = &local[0]; Saved.Value = alias; try { return *Saved.Value; } finally { Saved.Value = null; } }")]
    [InlineData("void Test() { Item* p = new Item(); destruct(*p); free(p); }")]
    [InlineData("struct Resource { public ~Resource() {} } void Test() { Resource[] values = Resource[1]; values = new Resource[1]; delete(values); }")]
    [InlineData("int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 10; p[1] = 20; int* q = p + 1; destruct(*q); int value = p[0]; free(p); return value; }")]
    [InlineData("int* Next(int* p) { return p + 1; } int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 10; p[1] = 20; int* q = Next(p); destruct(*q); int value = p[0]; free(p); return value; }")]
    [InlineData("int* Next(int* p) { return &p[1]; } int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 10; p[1] = 20; destruct(*p); int* q = Next(p); int value = *q; free(p); return value; }")]
    [InlineData("int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 10; p[1] = 20; int* q = &p[1]; destruct(*p); int value = *q; free(p); return value; }")]
    [InlineData("int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 10; p[1] = 20; destruct(*p); int value = p[1]; free(p); return value; }")]
    [InlineData("int Test() { int* p = cast<int*>(malloc(sizeof(int) * cast<nuint>(2))); p[0] = 10; p[1] = 20; destruct(*p); int* q = p + 1; int value = *q; free(p); return value; }")]
    [InlineData("void Test() { Item* p = new Item(); destruct(*p); *p = Item(); p->Read(); delete(p); }")]
    [InlineData("int Test() { int* p = new int(); destruct(*p); p[0] = 42; int value = *p; free(p); return value; }")]
    [InlineData("int Test(int i) { if (i != 0) return 0; int* p = new int(); destruct(*p); p[i] = 42; int value = *p; free(p); return value; }")]
    [InlineData("int Test(bool c) { Item* p = new Item(); if (c) delete(p); if (!c) return p->Read(); return 0; }")]
    [InlineData("struct Pair { public Holder* A; public Holder* B; } Pair Make() { Pair p = Pair(); p.A = new Holder(); p.B = new Holder(); p.A->Value = new Item(); p.B->Value = new Item(); return p; } int Test() { Pair p = Make(); Item* a = p.A->Value; Item* b = p.B->Value; delete(a); return b->Read(); }")]
    [InlineData("int Test(bool c) { Item* p = new Item(); if (c) delete(p); bool& r = c; r = false; if (c) return p->Read(); return 0; }")]
    [InlineData("int Test(int n) { Item* p = new Item(); if (n == 1) delete(p); if (n != 1) return p->Read(); return 0; }")]
    [InlineData("int Test(bool c) { Item* p = new Item(); if (c) delete(p); bool b = false; c <-> b; if (c) return p->Read(); return 0; }")]
    [InlineData("struct Pair { public Item* A; public Item* B; } Item* Allocate() { return new Item(); } Pair Make() { Pair p = Pair(); p.A = Allocate(); p.B = Allocate(); return p; } int Test() { Pair p = Make(); delete(p.A); return p.B->Read(); }")]
    [InlineData("int Test(bool c) { Item* a = new Item(); Item* b = new Item(); Holder h = Holder(); Item* q; if (c) { h.Value = a; q = b; } else { q = a; } delete(h.Value); return q->Value; }")]
    public void ValidUnsafeProgramsRemainAccepted(string source)
    {
        var compilation = Compilation.Create(SourceText.From(Prelude + source, "pointer-lifetime.xe"));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics) + "\n" + compilation.DumpLifetimeAnalysis() + "\n" + string.Join("\n", compilation.GetDeclaredFunctionSymbols().SelectMany(f => f.MirLifetimeSummary?.Effects.Select(e => f.Name + ": " + e) ?? [])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsyncCompletionRejectsPointerAfterCleanup(bool suspend)
    {
        var compilation = Compilation.Create(SourceText.From(Prelude + $$"""
            struct Result {
                public Item* Value;
                public static void operator resolve(Result& target, Item* value) { target.Value = value; }
                public static void operator reject(Result& target, int error) {}
            }
            struct Operation {
                public static bool operator await(readonly Operation& value, function void() next) { return true; }
            }
            async Result Test() { using var p = new Item(); {{(suspend ? "await Operation();" : "")}} return p; }
            """, "pointer-async.xe"));
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.DanglingPointerEscape && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Id != DiagnosticIds.DanglingPointerEscape);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AsyncCompletionRejectsPointerDestroyedByCall(bool suspend)
    {
        var compilation = Compilation.Create(SourceText.From(Prelude + $$"""
            struct Result {
                public Item* Value;
                public static void operator resolve(Result& target, Item* value) { target.Value = value; }
                public static void operator reject(Result& target, int error) {}
            }
            struct Operation {
                public static bool operator await(readonly Operation& value, function void() next) { return true; }
            }
            void Destroy(Item* p) { delete(p); }
            async Result Test() { Item* p = new Item(); {{(suspend ? "await Operation();" : "")}} Destroy(p); return p; }
            """, "pointer-call-async.xe"));
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.DanglingPointerEscape && d.Severity == DiagnosticSeverity.Error);
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Severity == DiagnosticSeverity.Error && d.Id != DiagnosticIds.DanglingPointerEscape);
    }

    [Fact]
    public void CallDiagnosticIncludesCallerAndDestructionWithoutDuplicates()
    {
        var compilation = Compilation.Create(SourceText.From(Prelude + """
            void Destroy(Item* p) { delete(p); }
            void Forward(Item* p) { Destroy(p); }
            int Test() { Item* p = new Item(); Forward(p); return p->Read(); }
            """, "pointer-call-locations.xe"));
        Assert.True(compilation.HasErrors);
        var diagnostic = Assert.Single(compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(DiagnosticIds.PointerUseAfterFree, diagnostic.Id);
        Assert.Contains(diagnostic.RelatedLocations, related => related.Message?.Contains("called function") == true);
        Assert.True(diagnostic.RelatedLocations.Select(related => related.Location.Span).Distinct().Count() >= 3);
    }

    [Fact]
    public void DiagnosticNamesAliasAndLocatesLifetimeEndWithoutDuplicates()
    {
        var compilation = Compilation.Create(SourceText.From(Prelude + """

            int Test() {
                Item* allocation = new Item();
                Item* alias = allocation;
                delete(allocation);
                return alias->Read();
            }
            """, "located-pointer.xe"));
        Assert.True(compilation.HasErrors);
        var diagnostic = Assert.Single(compilation.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Equal(DiagnosticIds.PointerUseAfterFree, diagnostic.Id);
        Assert.Contains("alias", diagnostic.Message);
        Assert.Equal(2, diagnostic.RelatedLocations.Length);
        Assert.All(diagnostic.RelatedLocations, related => Assert.Equal("located-pointer.xe", related.Location.Path));
    }
}





