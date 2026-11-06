using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Driver;

public sealed partial class NativeAllocatorAuditTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void NestedAllocatorContextsUseTheChosenParentAndConstructExactlyOnce(int optimization)
    {
        AssertSuccess("""
            static struct State {
                public static int order;
                public static int heapAlloc; public static int heapFree;
                public static int arenaAlloc; public static int arenaFree;
                public static int poolAlloc; public static int poolFree;
            }
            allocator Heap {
                +Heap(nuint size, nuint alignment) { State.heapAlloc++; return malloc(size, alignment); }
                -Heap(void* block) { State.heapFree++; free(block); }
            }
            allocator Arena : Heap {
                public allocator Heap* parent;
                public nuint capacity;
                public @Arena(Heap* provider) { parent = provider; State.order = State.order * 10 + 1; }
                public Arena(nuint size) {
                    if (parent == null) throw 11;
                    capacity = size; State.order = State.order * 10 + 2;
                }
                +Arena(nuint size, nuint alignment) { State.arenaAlloc++; return malloc(size, alignment); }
                -Arena(void* block) { State.arenaFree++; free(block); }
            }
            allocator Pool : Arena {
                public allocator Arena* arena;
                public @Pool(Arena* provider) { arena = provider; State.order = State.order * 10 + 3; }
                public Pool(nuint size) : base(size) {
                    Heap* parentArena = arena;
                    if (arena == null || parent != parentArena) throw 12;
                    State.order = State.order * 10 + 4;
                }
                +Pool(nuint size, nuint alignment) { State.poolAlloc++; return malloc(size, alignment); }
                -Pool(void* block) { State.poolFree++; free(block); }
            }
            int Main() {
                Heap* heap = new Heap();
                Arena* arena = new(heap) Arena(cast<nuint>(4096));
                Pool* pool = new(arena) Pool(cast<nuint>(64));
                if (arena->parent != heap || pool->arena != arena || arena->capacity != cast<nuint>(4096) || pool->capacity != cast<nuint>(64)) return 1;
                if (State.order != 121324 || State.heapAlloc != 1 || State.arenaAlloc != 1 || State.poolAlloc != 0) return 2;
                int* value = new(pool) int(42);
                if (*value != 42 || State.poolAlloc != 1) return 3;
                delete(value); delete(pool); delete(arena); delete(heap);
                if (State.heapFree != 1 || State.arenaFree != 1 || State.poolFree != 1) return 4;
                return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AllocatorContextInheritanceRollsBackEachPhaseWithoutLeaksOrDoubleDrops(int optimization)
    {
        AssertSuccess("""
            static struct State {
                public static int mode; public static int mask; public static int duplicate;
                public static int whole; public static int prepared; public static int ordinary;
            }
            struct Resource {
                public int id;
                public Resource(int number) { id = number; }
                public ~Resource() {
                    if ((State.mask & id) != 0) State.duplicate++;
                    State.mask |= id;
                }
            }
            allocator Heap {
                public int allocated; public int released; public void* raw;
                +Heap(nuint size, nuint alignment) { allocated++; raw = malloc(size, alignment); return raw; }
                -Heap(void* pointer) { if (pointer != raw) throw 99; released++; free(pointer); }
            }
            allocator Base : Heap {
                public Resource first = Resource(1);
                public @Base(Heap* parent) { State.prepared++; if (State.mode == 1) throw 1; }
                public Base() { State.ordinary++; if (State.mode == 3) throw 3; }
                public virtual ~Base() { State.whole++; }
            }
            allocator Child : Base {
                public Resource second = Resource(2);
                public @Child(Heap* parent) { State.prepared++; if (State.mode == 2) throw 2; }
                public Child() { State.ordinary++; if (State.mode == 4) throw 4; }
                public override ~Child() { State.whole += 10; }
            }
            int Main() {
                Heap* heap = new Heap();
                for (int phase = 1; phase <= 5; phase++) {
                    State.mode = phase; State.mask = 0; State.whole = 0;
                    State.prepared = 0; State.ordinary = 0;
                    int caught = 0;
                    try { Child* value = new(heap) Child(); delete(value); }
                    catch (readonly int& error) { caught = error; }
                    if (phase < 5 && caught != phase || phase == 5 && caught != 0) return 1;
                    if (heap->allocated != phase || heap->released != phase || State.duplicate != 0) return 2;
                    int expectedMask = 0; int expectedPrepared = 2;
                    if (phase == 1) expectedPrepared = 1;
                    if (phase == 3) expectedMask = 1;
                    if (phase >= 4) expectedMask = 3;
                    int expectedWhole = 0;
                    if (phase == 4) expectedWhole = 1;
                    if (phase == 5) expectedWhole = 11;
                    int expectedOrdinary = 0;
                    if (phase == 3) expectedOrdinary = 1;
                    if (phase >= 4) expectedOrdinary = 2;
                    if (State.mask != expectedMask) return 3;
                    if (State.prepared != expectedPrepared) return 4;
                    if (State.whole != expectedWhole) return 5;
                    if (State.ordinary != expectedOrdinary) return 6;
                }
                delete(heap); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void AllocatorContextOverloadsAndAllFourMembersSurviveSourceFreeXelib(int optimization)
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace ContextLibrary;
            public allocator Heap {
                public int freed;
                +Heap(nuint size, nuint alignment) { return malloc(size, alignment); }
                -Heap(void* block) { freed++; free(block); }
            }
            public allocator Arena : Heap {}
            public int Select(Arena* arena) { if (arena == null) return 1; return 2; }
            public allocator Pool : Heap {
                public allocator Heap* parent;
                public allocator Arena* arena;
                public int choice = Select(arena);
                public @Pool(Heap* provider) { parent = provider; }
                public @Pool(Arena* provider) { parent = provider; arena = provider; }
                public Pool(int increment) { choice += increment; }
                +Pool(nuint size, nuint alignment) { return malloc(size, alignment); }
                -Pool(void* block) { freed++; free(block); }
            }
            public template Constructible { Constructible(int value); }
            public T* Make<T>(Arena* provider) where T : Constructible { return new(provider) T(10); }
            """));
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("ContextLibrary")));
        Compilation consumer = Compilation.Create(new CompilationOptions(CompilationOutputKind.Executable), [reference], SourceText.From("""
            using ContextLibrary;
            namespace Consumer;
            int Main() {
                Arena* arena = new Arena(); Heap* basePointer = arena;
                Pool* exact = Make<Pool>(arena);
                Pool* baseSelected = new(basePointer) Pool(20);
                if (exact->choice != 12 || baseSelected->choice != 21) return 1;
                if (exact->parent != basePointer || baseSelected->parent != basePointer) return 2;
                int* value = new(exact) int(42); delete(value);
                if (exact->freed != 1) return 3;
                delete(exact); delete(baseSelected);
                if (arena->freed != 2) return 4;
                delete(arena); return 0;
            }
            """));
        Assert.Equal(0, Execute(consumer, optimization).ExitCode);
    }
}
