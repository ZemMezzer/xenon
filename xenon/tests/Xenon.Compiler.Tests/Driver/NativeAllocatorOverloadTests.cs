using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Driver;

public sealed partial class NativeAllocatorAuditTests
{
    private const string OverloadProviders = """
        allocator Heap {
            public int requests; public int releases;
            +Heap(nuint size, nuint alignment) { requests++; return malloc(size, alignment); }
            -Heap(void* block) { releases++; free(block); }
        }
        allocator Arena : Heap {
            +Arena(nuint size, nuint alignment) { requests += 10; return malloc(size, alignment); }
            -Arena(void* block) { releases += 10; free(block); }
        }
        allocator Pool : Arena {
            +Pool(nuint size, nuint alignment) { requests += 100; return malloc(size, alignment); }
            -Pool(void* block) { releases += 100; free(block); }
        }
        allocator Leaf : Pool {}
        """;

    [Theory]
    [InlineData("struct", 0)] [InlineData("struct", 3)]
    [InlineData("allocator", 0)] [InlineData("allocator", 3)]
    public void ContextOverloadsChooseOncePerLevelForScalarsAndArrays(string kind, int optimization)
    {
        AssertSuccess(OverloadProviders + $$"""
            static struct Calls { public static int parent; public static int child; public static int parentDigit; public static int childDigit; }
            {{kind}} Parent{{(kind == "allocator" ? " : Heap" : "")}} {
                public allocator Heap* parentProvider; public int baseTicket = Calls.parent;
                public @Parent(Heap* h) { parentProvider = h; Calls.parentDigit = 10; Calls.parent++; }
                public @Parent(Arena* h) { parentProvider = h; Calls.parentDigit = 20; Calls.parent++; }
            }
            {{kind}} Child : Parent {
                public allocator Heap* childProvider; public int ticket = Calls.child;
                public int order = Calls.parentDigit + Calls.childDigit;
                public @Child(Pool* h) { childProvider = h; Calls.childDigit = 3; Calls.child++; }
                public @Child(Heap* h) { childProvider = h; Calls.childDigit = 1; Calls.child++; }
                public @Child(Arena* h) { childProvider = h; Calls.childDigit = 2; Calls.child++; }
                public Child(int suffix) { order = order * 10 + suffix; }
            }
            int Main() {
                Heap* heap = new Heap(); Arena* arena = new Arena();
                Pool* pool = new Pool(); Leaf* leaf = new Leaf(); Heap* erased = pool;
                Child* a = new(heap) Child(9);
                Child* b = new(arena) Child(9);
                Child* c = new(pool) Child(9);
                Child* d = new(erased) Child(9);
                Child* e = new(leaf) Child(9);
                if (a->order != 119 || b->order != 229 || c->order != 239 || d->order != 119 || e->order != 239) return 1;
                if (a->parentProvider != heap || b->childProvider != arena || c->parentProvider != pool || d->childProvider != erased || e->childProvider != leaf) return 9;
                if (a->ticket != 1 || b->ticket != 2 || c->ticket != 3 || d->ticket != 4 || e->ticket != 5) return 2;
                if (a->baseTicket != 1 || b->baseTicket != 2 || c->baseTicket != 3 || d->baseTicket != 4 || e->baseTicket != 5) return 3;
                Child[] exact = new(pool) Child[3];
                Child[] baseSelected = new(erased) Child[2];
                Child*[] pointers = new(heap) Child*[4];
                for (int i = 0; i < 3; i++)
                    if (exact[i].order != 23 || exact[i].ticket != i + 6 || exact[i].baseTicket != i + 6) return 4;
                for (int i = 0; i < 2; i++)
                    if (baseSelected[i].order != 11 || baseSelected[i].ticket != i + 9 || baseSelected[i].baseTicket != i + 9) return 5;
                if (pointers[0] != null || pointers[3] != null) return 6;
                if (heap->requests != 2 || arena->requests != 10 || pool->requests != 400 || leaf->requests != 100) return 7;
                delete(a); delete(b); delete(c); delete(d); delete(e);
                delete(exact); delete(baseSelected); delete(pointers);
                if (heap->releases != 2 || arena->releases != 10 || pool->releases != 400 || leaf->releases != 100) return 8;
                delete(heap); delete(arena); delete(pool); delete(leaf); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData("struct", 0)] [InlineData("struct", 3)]
    [InlineData("allocator", 0)] [InlineData("allocator", 3)]
    public void SelectedOverloadFailureCleansPartialArraysWithoutRunningOtherOverloads(string kind, int optimization)
    {
        AssertSuccess(OverloadProviders + $$"""
            static struct State {
                public static int made; public static int dropped; public static int duplicate;
                public static int mask; public static int wrong; public static int baseCalls;
                public static int selected; public static int whole;
            }
            struct Resource {
                public int id;
                public Resource() { id = ++State.made; }
                public ~Resource() {
                    int bit = 1 << id;
                    if ((State.mask & bit) != 0) State.duplicate++;
                    State.mask |= bit; State.dropped++;
                }
            }
            {{kind}} Parent{{(kind == "allocator" ? " : Heap" : "")}} {
                public Resource first = Resource();
                public @Parent(Heap* h) { State.wrong++; }
                public @Parent(Arena* h) { State.baseCalls++; }
            }
            {{kind}} Child : Parent {
                public Resource second = Resource();
                public @Child(Heap* h) { State.wrong++; }
                public @Child(Arena* h) { State.wrong++; }
                public @Child(Pool* h) { if (++State.selected == 3) throw 7; }
                public ~Child() { State.whole++; }
            }
            int Main() {
                Pool* pool = new Pool();
                int caught = 0;
                try { Child[] values = new(pool) Child[5]; delete(values); }
                catch (readonly int& error) { caught = error; }
                if (caught != 7 || State.wrong != 0 || State.selected != 3 || State.baseCalls != 3) return 1;
                if (State.made != 4 || State.dropped != 4 || State.duplicate != 0 || State.whole != 2) return 2;
                if (pool->requests != 100 || pool->releases != 100) return 3;
                delete(pool); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void GenericContextOverloadsSelectCorrectBodiesFromSourceFreeXelib(int optimization)
    {
        Compilation library = Compile(OverloadProviders + """
            public allocator Provider<T> : Arena {}
            public static struct Selection { public static int digit; }
            public struct Value<T> {
                public allocator Heap* owner;
                public allocator Provider<T>* specialized;
                public int selected = Selection.digit;
                public @Value(Heap* h) { owner = h; Selection.digit = 1; }
                public @Value(Provider<T>* h) { specialized = h; owner = h; Selection.digit = 2; }
                public @Value(Provider<int>* h) { owner = h; Selection.digit = 3; }
                public Value() { selected += 10; }
            }
            public template Constructible { Constructible(); }
            public T* Make<T>(Heap* h) where T : Constructible { return new(h) T(); }
            """);
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Overloads")));
        Compilation consumer = Compilation.Create(new CompilationOptions(CompilationOutputKind.Executable), [reference], SourceText.From("""
            using AllocatorAudit;
            namespace Consumer;
            int Main() {
                Provider<byte>* first = new Provider<byte>();
                Provider<int>* second = new Provider<int>();
                Value<byte>* a = new(first) Value<byte>();
                Value<byte>* b = new(second) Value<byte>();
                Value<byte>* c = Make<Value<byte>>(first);
                Value<byte>[] values = new(second) Value<byte>[2];
                if (a->specialized != first || b->specialized != null || c->specialized != null) return 4;
                if (a->owner != first || b->owner != second || c->owner != first || values[1].owner != second) return 3;
                if (a->selected != 12 || b->selected != 13 || c->selected != 11 || values[1].selected != 3) return 1;
                delete(a); delete(b); delete(c); delete(values);
                if (first->releases != 20 || second->releases != 20) return 2;
                delete(first); delete(second); return 0;
            }
            """));
        Assert.Equal(0, Execute(consumer, optimization).ExitCode);
    }
}
