using Xunit;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Tests.Driver;

public sealed partial class NativeAllocatorAuditTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(false, 3)]
    [InlineData(true, 0)] [InlineData(true, 3)]
    public void NestedArgumentAllocationReleasesInnerThenOuterBeforeDroppingCallerLocal(bool sourceFree, int optimization)
    {
        const string libraryText = """
            public static struct Log { public static int order; public static void Append(int n) { Log.order = Log.order * 10 + n; } }
            public allocator Heap {
                public int tag;
                +Heap(nuint bytes, nuint alignment) { return malloc(bytes, alignment); }
                -Heap(void* p) { Log.Append(tag); free(p); }
            }
            public struct Resource {
                public int tag;
                public Resource(int n) { tag = n; }
                public ~Resource() { Log.Append(tag); }
            }
            public struct Inner { public @Inner(Heap* h) { throw 7; } }
            public struct Parent { public allocator Heap* a; public @Parent(Heap* h) { a = h; Log.Append(1); } }
            public struct Middle : Parent { public allocator Heap* b; public @Middle(Heap* h) { b = h; Log.Append(2); } }
            public struct Outer<T> : Middle {
                public allocator Heap* c; public @Outer(Heap* h) { c = h; Log.Append(3); }
                public Resource ordinary = Resource(9);
                public Outer(Inner* arg) {}
            }
            public void Construct<T>(Heap* outer, Heap* inner) {
                Resource local = Resource(6);
                Outer<T>* p = new(outer) Outer<T>(new(inner) Inner());
                delete(p);
            }
            """;
        const string main = """
            int Main() {
                Heap* outer = new Heap(); outer->tag = 5;
                Heap* inner = new Heap(); inner->tag = 4;
                int caught = 0;
                try { Construct<int>(outer, inner); } catch (readonly int& e) { caught = e; }
                delete(inner); delete(outer);
                if (caught != 7) return 1;
                if (Log.order != 123456) return 2;
                return 0;
            }
            """;
        Compilation compilation;
        if (sourceFree)
        {
            var library = Compile(libraryText);
            Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
            var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("GuardOrdering")));
            compilation = Compilation.Create(new CompilationOptions(CompilationOutputKind.Executable), [reference],
                SourceText.From("using AllocatorAudit; namespace Consumer; " + main));
        }
        else compilation = Compile(libraryText + main);
        Assert.Equal(0, Execute(compilation, optimization).ExitCode);
    }

    [Theory]
    [InlineData("Resource", "")]
    [InlineData("Resource", "@Value() {}")]
    [InlineData("Resource", "@Value() { item = Resource(); }")]
    [InlineData("Resource", "@Value() { if (State.active) item = Resource(); }")]
    [InlineData("int&", "@Value() {}")]
    [InlineData("pin<int>", "@Value() {}")]
    public void AllocatorFieldTypeIsCheckedIndependentlyOfContextInitialization(string type, string phase)
    {
        var compilation = Compile($$"""
            static struct State { public static bool active; }
            struct Resource { public Resource() {} public ~Resource() {} }
            struct Value { public allocator {{type}} item; {{phase}} }
            int Main() { Value v = Value(); return 0; }
            """);
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.InvalidAllocatorFieldType &&
            d.Message.Contains("item") && d.Location.Span.Length > 0);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void StrictAllocatorPhasePreservesReferenceProvenanceAcrossOrdinaryInitialization(int optimization)
    {
        AssertSuccess(OverloadProviders + """
            static struct State { public static Heap* provider; public static int number; }
            struct Value {
                public allocator Heap* context;
                public int observed = context->requests;
                public int& number;
                public @Value() { context = State.provider; }
                public Value(int& source) { number = source; }
            }
            int& Get() {
                Value local = Value(State.number);
                local.number += local.observed;
                return local.number;
            }
            int Main() {
                State.provider = new Heap(); State.provider->requests = 8;
                int& result = Get(); result++;
                delete(State.provider);
                return State.number - 9;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void StrictAllocatorPhaseDefaultsOmittedProviderToNullWithoutADeclaredContext(int optimization)
    {
        AssertSuccess(OverloadProviders + """
            struct Value { public int ordinary; public allocator Heap* context; }
            int Main() {
                Value s = Value{41};
                Value* p = new Value{42};
                if (s.ordinary != 41 || s.context != null || p->ordinary != 42 || p->context != null) return 1;
                delete(p); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void StrictAllocatorPhaseUnwindsContextLocalsWithoutOwningProvider(int optimization)
    {
        AssertSuccess(OverloadProviders + """
            static struct Trace { public static int mode; public static int made; public static int dropped; }
            struct Resource {
                public Resource() { Trace.made++; }
                public ~Resource() { Trace.dropped++; }
            }
            struct Value {
                public allocator Heap* owner;
                public @Value(Heap* h) {
                    if (Trace.mode == 0) throw 10;
                    Resource first = Resource(); owner = h;
                    Resource second = Resource(); owner = null;
                    if (Trace.mode == 1) throw 11;
                }
                public Value() { if (Trace.mode == 2) throw 12; }
            }
            int Main() {
                Heap* h = new Heap();
                for (int mode = 0; mode < 4; mode++) {
                    Trace.mode = mode; int caught = 0;
                    try { Value* v = new(h) Value(); delete(v); }
                    catch (readonly int& error) { caught = error; }
                    if ((mode < 3 && caught != mode + 10) || (mode == 3 && caught != 0)) return 1;
                    if (Trace.made != 2 * mode || Trace.dropped != Trace.made) return 2;
                    if (h->releases != mode + 1) return 3;
                }
                delete(h); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void StrictAllocatorPhaseFinishesEveryContextBeforeTheFirstOrdinaryInitializer(int optimization)
    {
        AssertSuccess(OverloadProviders + """
            static struct Trace {
                public static int order;
                public static void Record(int digit) { Trace.order = Trace.order * 10 + digit; }
            }
            int Observe(Heap* h) { Trace.Record(3); if (h == null) return 0; return 1; }
            struct Base {
                public allocator Heap* provider;
                public int observed = Observe(provider);
                public @Base() { Trace.Record(1); }
                public @Base(Heap* h) { Trace.Record(1); provider = null; if (h != null) provider = h; }
                public Base() { Trace.Record(4); }
            }
            struct Derived<T> : Base {
                private allocator Heap* context;
                public Heap* captured = context;
                public @Derived() { context = null; Trace.Record(2); }
                public @Derived(Heap* h) { context = h; Trace.Record(2); }
                public Derived() { Trace.Record(5); }
                public Heap* Read() { return context; }
            }
            int Main() {
                Derived<int> local = Derived<int>();
                if (Trace.order != 12345 || local.observed != 0 || local.captured != null || local.Read() != null) return 1;
                Trace.order = 0;
                Derived<int>* system = new Derived<int>();
                if (Trace.order != 12345 || system->provider != null || system->observed != 0) return 2;
                delete(system);
                Trace.order = 0;
                Heap* h = new Heap();
                Derived<int>* selected = new(h) Derived<int>();
                if (Trace.order != 12345 || selected->provider != h || selected->captured != h || selected->Read() != h || selected->observed != 1) return 3;
                delete(selected);
                Trace.order = 0;
                Derived<int>[] elements = new(h) Derived<int>[2];
                if (Trace.order != 123123 || elements[1].observed != 1 || elements[1].captured != h) return 4;
                delete(elements);
                if (h->requests != 2 || h->releases != 2) return 5;
                delete(h); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void StrictAllocatorPhaseUnwindsConstructedResourcesAndReleasesWithOriginalProvider(int optimization)
    {
        AssertSuccess(OverloadProviders + """
            static struct Trace {
                public static int mode; public static int entered; public static int localDrops;
                public static int fieldStarts; public static int fieldDrops; public static int ordinary;
                public static int attempted; public static int whole;
            }
            struct Local { public ~Local() { Trace.localDrops++; } }
            struct Resource {
                public Resource() { Trace.fieldStarts++; }
                public ~Resource() { Trace.fieldDrops++; }
            }
            int Check() { Trace.attempted++; if (Trace.mode == 2) throw 72; return 1; }
            struct Parent {
                public Resource first = Resource();
                public @Parent(Heap* h) { Trace.entered++; }
            }
            struct Value : Parent {
                public allocator Heap* owner;
                public Resource second = Resource();
                public int last = Check();
                public @Value(Heap* h) {
                    Local local = Local(); owner = null;
                    if (Trace.mode == 1) throw 71;
                }
                public Value() { Trace.ordinary++; if (Trace.mode == 3) throw 73; }
                public ~Value() { Trace.whole++; }
            }
            int Main() {
                Heap* h = new Heap();
                for (int mode = 1; mode <= 3; mode++) {
                    Trace.mode = mode;
                    int caught = 0;
                    try { Value* value = new(h) Value(); delete(value); }
                    catch (readonly int& error) { caught = error; }
                    if (caught != 70 + mode || h->requests != mode || h->releases != mode) return 1;
                    if (Trace.entered != mode || Trace.localDrops != mode || Trace.whole != 0) return 2;
                    if (Trace.fieldStarts != (mode - 1) * 2 || Trace.fieldDrops != Trace.fieldStarts) return 3;
                    if (Trace.attempted != mode - 1) return 4;
                }
                if (Trace.ordinary != 1) return 5;
                delete(h); return 0;
            }
            """, optimization);
    }
}
