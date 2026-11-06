using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Driver;

public sealed partial class NativeAllocatorAuditTests
{
    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void ParameterlessContextsInitializeStorageAndPinAtTheirFinalDestination(int optimization)
    {
        AssertSuccess(OverloadProviders + """
            static struct State { public static Heap* provider; public static int calls; public static int drops; }
            struct Value {
                public allocator Heap* seed;
                public int n = seed->requests;
                public @Value() { seed = State.provider; State.calls++; }
                public Value() { n++; }
                public ~Value() { State.drops++; }
            }
            struct Aggregate { public allocator Heap* owner; public @Aggregate() { owner = State.provider; State.calls++; } }
            int Main() {
                State.provider = new Heap(); State.provider->requests = 10;
                { storage<Value> stored = Value(); pin<Value> pinned = Value(); pin<storage<Value>> both = Value();
                  storage<Aggregate> aggregate = Aggregate(); pin<Aggregate> pinnedAggregate = Aggregate();
                  if (stored.n != 11 || pinned.n != 11 || both.n != 11 || aggregate.owner != State.provider || pinnedAggregate.owner != State.provider || State.calls != 5) return 1;
                  destruct(stored); stored = Value();
                  if (stored.n != 11 || State.calls != 6 || State.drops != 1) return 2; }
                delete(State.provider);
                if (State.drops != 4) return 3;
                return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(3, 1)]
    [InlineData(0, 2)] [InlineData(3, 2)]
    [InlineData(0, 3)] [InlineData(3, 3)]
    public void ParameterlessStorageFailureLeavesEmptyStorageForReinitialization(int optimization, int failure)
    {
        AssertSuccess($$"""
            static struct State { public static int mode; public static int calls; public static int made; public static int dropped; public static int whole; }
            struct Resource { public Resource() { State.made++; } public ~Resource() { State.dropped++; } }
            struct Value {
                public Resource resource = Resource();
                public @Value() { State.calls++; if (State.mode == 1) throw 7; }
                public Value(int value) { if (State.mode == 3) throw 7; }
                public ~Value() { State.whole++; }
            }
            int Argument() { if (State.mode == 2) throw 7; return 0; }
            int Main() {
                storage<Value> slot;
                State.mode = {{failure}};
                int caught = 0;
                int expectedMade = {{(failure == 3 ? 1 : 0)}};
                try { slot = Value(Argument()); } catch (readonly int& error) { caught = error; }
                if (caught != 7 || State.calls != 1 || State.made != expectedMade || State.dropped != expectedMade || State.whole != 0) return 1;
                State.mode = 0;
                slot = Value(Argument());
                destruct(slot);
                if (State.calls != 2 || State.made != expectedMade + 1 || State.dropped != expectedMade + 1 || State.whole != 1) return 2;
                return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void ParameterlessContextsAreNotRepeatedByDelegationOrCopying(int optimization)
    {
        AssertSuccess(OverloadProviders + """
            static struct State { public static int calls; public static Heap* provider; }
            struct Parent {
                public allocator Heap* owner;
                public int value = 1 + owner->requests;
                public @Parent() { owner = State.provider; State.calls++; }
            }
            struct Child : Parent {
                public int extra = 4;
                public Child() { value += extra; }
                public Child(int suffix) : this() { value += suffix; }
            }
            struct Container { public Child child = Child(5); }
            int Main() {
                State.provider = new Heap(); State.provider->requests = 2;
                Child local = Child(5);
                Child* heap = new Child(5);
                Child copy = local;
                Container embedded = Container();
                if (local.value != 12 || heap->value != 12 || copy.value != 12 || embedded.child.value != 12 || State.calls != 3) return 1;
                delete(heap); delete(State.provider); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData("struct", 0)] [InlineData("struct", 3)]
    [InlineData("allocator", 0)] [InlineData("allocator", 3)]
    public void ParameterlessContextsSelectOncePerLevelBeforeOrdinaryConstruction(string kind, int optimization)
    {
        AssertSuccess(OverloadProviders + $$"""
            static struct State { public static int trace; public static int calls; public static int ordinary; public static int drops; }
            {{kind}} Parent{{(kind == "allocator" ? " : Heap" : "")}} {
                public int order = State.trace;
                public @Parent() { State.trace = 12; State.calls++; }
                public @Parent(Heap* h) { State.trace = 13; State.calls++; }
                public Parent() { order = order * 10 + 6; State.ordinary++; }
            }
            {{kind}} Child : Parent {
                public @Child() { State.trace = State.trace * 10 + 4; State.calls++; }
                public @Child(Heap* h) { State.trace = State.trace * 10 + 5; State.calls++; }
                public Child() { order = order * 10 + 7; State.ordinary++; }
                public ~Child() { State.drops++; }
            }
            struct Aggregate {
                public allocator Heap* owner;
                public Heap* observed = owner;
                public @Aggregate() { State.calls++; }
                public @Aggregate(Heap* h) { owner = h; State.calls++; }
            }
            int Main() {
                Heap* heap = new Heap();
                Child* system = new Child(); Child* custom = new(heap) Child();
                if (system->order != 12467 || custom->order != 13567) return 1;
                Child[] defaults = new Child[2]; Child[] explicitValues = new(heap) Child[2];
                if (defaults[0].order != 124 || defaults[1].order != 124 || explicitValues[1].order != 135) return 2;
                int beforePointers = State.calls;
                Child*[] pointers = new Child*[2];
                if (pointers[0] != null || State.calls != beforePointers) return 3;
                delete(pointers);
                {{(kind == "struct" ? """
                { Child stack = Child(); if (stack.order != 12467) return 4;
                  Child copy = stack; if (State.calls != beforePointers + 2) return 5; }
                { Child[] stackValues = Child[2]; if (stackValues[1].order != 124) return 6; }
                if (State.calls != 18 || State.ordinary != 6 || State.drops != 4) return 7;
                """ : "if (State.calls != 12 || State.ordinary != 4 || State.drops != 0) return 7;")}}
                int beforeAggregates = State.calls;
                Aggregate local = Aggregate(); Aggregate* a = new Aggregate();
                Aggregate* b = new(heap) Aggregate();
                if (State.calls != beforeAggregates + 3 || local.observed != null || a->observed != null || b->observed != heap) return 8;
                delete(a); delete(b); delete(system); delete(custom); delete(defaults); delete(explicitValues);
                if (State.drops != {{(kind == "struct" ? 10 : 6)}} || heap->requests != 3 || heap->releases != 3) return 9;
                delete(heap); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0, "stack")] [InlineData(3, "stack")]
    [InlineData(0, "heap")] [InlineData(3, "heap")]
    [InlineData(0, "array")] [InlineData(3, "array")]
    [InlineData(0, "stack-array")] [InlineData(3, "stack-array")]
    [InlineData(0, "arguments")] [InlineData(3, "arguments")]
    [InlineData(0, "ordinary")] [InlineData(3, "ordinary")]
    public void ParameterlessContextFailureCleansInitializedFieldsExactlyOnce(int optimization, string mode)
    {
        string creation = mode switch {
            "stack" => "Child c = Child(0);",
            "heap" => "Child* c = new Child(0); delete(c);",
            "array" => "Child[] c = new Child[4]; delete(c);",
            "stack-array" => "Child[] c = Child[4];",
            "arguments" => "Child c = Child(Fail());",
            _ => "Child c = Child(1);",
        };
        bool array = mode is "array" or "stack-array";
        AssertSuccess($$"""
            static struct State {
                public static int made; public static int dropped; public static int mask;
                public static int duplicate; public static int calls; public static int whole;
            }
            struct Resource {
                public int id;
                public Resource() { id = ++State.made; }
                public ~Resource() { int bit = 1 << id; if ((State.mask & bit) != 0) State.duplicate++; State.mask |= bit; State.dropped++; }
            }
            struct Parent { public Resource first = Resource(); public @Parent() {} }
            struct Child : Parent {
                public Resource second = Resource();
                public @Child() { State.calls++; {{(mode is "arguments" or "ordinary" ? "" : $"if (State.calls == {(array ? 3 : 1)}) throw 7;")}} }
                public Child(int fail) { if (fail != 0) throw 7; }
                public ~Child() { State.whole++; }
            }
            int Fail() { throw 7; }
            int Main() {
                int caught = 0;
                try { {{creation}} } catch (readonly int& error) { caught = error; }
                if (caught != 7 || State.calls != {{(array ? 3 : 1)}}) return 1;
                if (State.made != {{(array ? 4 : mode == "ordinary" ? 2 : 0)}} || State.dropped != State.made || State.duplicate != 0) return 2;
                if (State.whole != {{(array ? 2 : 0)}}) return 3;
                return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void ParameterlessGenericContextsSurviveSourceFreeXelib(int optimization)
    {
        var library = Compile(OverloadProviders + """
            public static struct DefaultState { public static int calls; }
            public int Digit(Heap* h, int fallback) { if (h == null) return fallback; return fallback + 1; }
            public struct Value<T> {
                public allocator Heap* context;
                public int selected = Digit(context, 1);
                public @Value() { context = null; DefaultState.calls++; }
                public @Value(Heap* h) { context = h; }
                public Value() { selected += 10; }
            }
            public allocator Provider<T> : Heap {
                public allocator Heap* context;
                public int selected = Digit(context, 3);
                public @Provider() { context = null; }
                public @Provider(Heap* h) { context = h; }
                public Provider() { selected += 10; }
            }
            public template Constructible { Constructible(); }
            public T MakeStack<T>() where T : Constructible { return T(); }
            public T* MakeHeap<T>() where T : Constructible { return new T(); }
            public T[] MakeArray<T>() { return new T[2]; }
            public int Store<T>() where T : Constructible { storage<T> v = T(); return 0; }
            """);
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Defaults")));
        var consumer = Compilation.Create(new CompilationOptions(CompilationOutputKind.Executable), [reference], SourceText.From("""
            using AllocatorAudit; namespace Consumer;
            int Main() {
                Heap* h = new Heap();
                Value<int> stack = MakeStack<Value<int>>();
                Value<int>* a = MakeHeap<Value<int>>();
                Value<int>* b = new(h) Value<int>();
                Value<int>[] values = MakeArray<Value<int>>();
                Provider<int>* p = MakeHeap<Provider<int>>();
                Provider<int>* q = new(h) Provider<int>();
                Store<Value<int>>();
                if (DefaultState.calls != 5) return 3;
                if (stack.selected != 11 || a->selected != 11 || b->selected != 12 || values[1].selected != 1 || p->selected != 13 || q->selected != 14) return 1;
                delete(a); delete(b); delete(values); delete(p); delete(q);
                if (h->releases != 2) return 2;
                delete(h); return 0;
            }
            """));
        Assert.Equal(0, Execute(consumer, optimization).ExitCode);
    }
}
