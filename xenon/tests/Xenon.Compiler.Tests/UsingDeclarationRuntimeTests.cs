using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class UsingDeclarationRuntimeTests
{
    [Theory]
    [InlineData("debug", false, "")]
    [InlineData("release", false, "")]
    [InlineData("debug", true, "")]
    [InlineData("release", true, "")]
    [InlineData("debug", false, "return;")]
    [InlineData("release", false, "return;")]
    [InlineData("debug", true, "return;")]
    [InlineData("release", true, "return;")]
    [InlineData("debug", false, "throw true;")]
    [InlineData("release", false, "throw true;")]
    [InlineData("debug", true, "throw true;")]
    [InlineData("release", true, "throw true;")]
    [InlineData("debug", false, "break;")]
    [InlineData("release", false, "break;")]
    [InlineData("debug", true, "break;")]
    [InlineData("release", true, "break;")]
    public Task CleanupExceptionPreservesConstructorReferenceBinding(string profile, bool automatic, string exit) =>
        ResumableRuntimeTests.Run(Resources + $$"""
        struct Bomb { public ~Bomb() { throw 8; } }
        struct Holder {
            public int& Value;
            public Holder(int& first, int& second, Bomb* pointer) {
                try { {{(exit == "break;" ? "while (true) {" : "")}}
                    {{(automatic ? "using" : "")}} var resource = pointer;
                    Value = first;
                    {{(automatic ? "" : "delete(resource);")}}
                    {{exit}}
                    {{(exit == "break;" ? "}" : "")}}
                } catch (readonly int& error) { Value = second; }
            }
        }
        int Main() {
            int first = 1; int second = 42;
            { Holder holder = Holder(first, second, new Bomb()); if (holder.Value != 42) return 1; }
            if (first != 42 || second != 42) return 2;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug", false)]
    [InlineData("release", false)]
    [InlineData("debug", true)]
    [InlineData("release", true)]
    public Task AsyncExceptionalFinallyRunsCleanupAfterSuspension(string profile, bool replace) =>
        ResumableRuntimeTests.Run(ResumableRuntimeTests.Protocol + $$"""
        struct Bomb {
            public ~Bomb() { Harness.Destroyed++; {{(replace ? "throw 8;" : "")}} }
        }
        async Result Work() {
            using var outer = new Resource(1);
            try { await Operation(); throw 7; }
            finally {
                using var inner = new Bomb();
                if (Harness.Destroyed != 0) throw 6;
            }
        }
        int Main() {
            Result result = Work();
            if (result.State->Status != 0 || Harness.Destroyed != 0) return 1;
            Harness.Ready = true; Harness.Next();
            if (result.State->Status != 2 || result.State->Value != {{(replace ? 8 : 7)}} || Harness.Destroyed != 2) return 2;
            Harness.Next();
            if (Harness.Destroyed != 2) return 3;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task AsyncCustomAllocatorAndNestedScopesSurviveRepeatedSuspension(string profile) =>
        ResumableRuntimeTests.Run(ResumableRuntimeTests.Protocol + """
        static struct Counts { public static int Allocated; public static int Freed; }
        allocator Heap {
            +Heap(nuint size, nuint alignment) { Counts.Allocated++; return malloc(size, alignment); }
            -Heap(void* block) { Counts.Freed++; free(block); }
        }
        async Result Work() {
            using var heap = new Heap();
            using var outer = new(heap) Resource(1);
            {
                using var inner = new(heap) Resource(2);
                await Operation();
                if (Harness.Destroyed != 0 || inner->Value != 2) return 3;
            }
            Harness.Ready = false;
            await Operation();
            return outer->Value + 41;
        }
        int Main() {
            Result result = Work();
            if (result.State->Status != 0 || Harness.Destroyed != 0 || Counts.Allocated != 2) return 1;
            Harness.Ready = true; Harness.Next();
            if (result.State->Status != 0 || Harness.Destroyed != 1 || Counts.Freed != 1) return 2;
            Harness.Ready = true; Harness.Next();
            if (result.State->Status != 1 || result.State->Value != 42 || Harness.Destroyed != 2 || Counts.Freed != 2) return 3;
            Harness.Next();
            if (Harness.Destroyed != 2 || Counts.Freed != 2) return 4;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task AsyncCleanupFailureReplacesSuccessfulReturn(string profile) =>
        ResumableRuntimeTests.Run(ResumableRuntimeTests.Protocol + """
        struct Bomb { public ~Bomb() { Harness.Destroyed++; throw 8; } }
        async Result Work() {
            using var outer = new Resource(1);
            using var inner = new Bomb();
            return await Operation();
        }
        int Main() {
            Result result = Work();
            if (result.State->Status != 0 || Harness.Destroyed != 0) return 1;
            Harness.Ready = true; Harness.Next();
            if (result.State->Status != 2 || result.State->Value != 8 || Harness.Destroyed != 2) return 2;
            Harness.Next();
            if (Harness.Destroyed != 2) return 3;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task CleanupFailureDestroysPendingOwnedReturn(string profile) => ResumableRuntimeTests.Run(Resources + """
        struct Bomb { public ~Bomb() { State.Dropped++; throw 8; } }
        unique<Item> Work() {
            using var outer = new Item(1);
            using var inner = new Bomb();
            return new Item(2);
        }
        int Main() {
            try { unique<Item> result = Work(); return 1; }
            catch (readonly int& error) {
                if (error != 8 || State.Dropped != 3 || State.Order != 21) return 2;
            }
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task CleanupInsideExceptionalFinallyReleasesAllReplacedExceptions(string profile) =>
        ResumableRuntimeTests.Run(Resources + """
        static struct Errors { public static int Original; public static int Inner; public static int Cleanup; }
        struct Fault {
            public int Code;
            public Fault(int code) { Code = code; }
            public ~Fault() { if (Code == 7) Errors.Original++; else if (Code == 6) Errors.Inner++; else Errors.Cleanup++; }
        }
        struct Bomb { public ~Bomb() { State.Dropped++; throw Fault(8); } }
        int Main() {
            try {
                try { throw Fault(7); }
                finally { using var bomb = new Bomb(); throw Fault(6); }
            } catch (readonly Fault& error) {
                if (error.Code != 8 || State.Dropped != 1 || Errors.Original != 1 || Errors.Inner != 1 || Errors.Cleanup != 0) return 1;
            }
            if (Errors.Cleanup != 1) return 2;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task ArrayDestructorFailureDuringUnwindFinishesAndFreesArray(string profile) => ResumableRuntimeTests.Run(Resources + """
        struct Entry {
            public int Id;
            public ~Entry() { State.Order = State.Order * 10 + Id; State.Dropped++; if (Id == 2) throw 8; }
        }
        int Main() {
            using var heap = new Heap();
            try {
                using var outer = new Item(4);
                using var entries = new(heap) Entry[3];
                entries[0].Id = 1; entries[1].Id = 2; entries[2].Id = 3;
                throw 7;
            } catch (readonly int& error) {
                if (error != 8 || State.Order != 3214 || State.Dropped != 4 || State.Freed != 1) return 1;
            }
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task AllocatorReleaseCanReplaceTheUnwindingException(string profile) => ResumableRuntimeTests.Run(Resources + """
        allocator FailingHeap {
            +FailingHeap(nuint size, nuint alignment) { return malloc(size, alignment); }
            -FailingHeap(void* block) { State.Freed++; free(block); throw 8; }
        }
        int Main() {
            using var heap = new FailingHeap();
            try {
                using var outer = new Item(1);
                using var inner = new(heap) Item(2);
                throw 7;
            } catch (readonly int& error) {
                if (error != 8 || State.Order != 21 || State.Dropped != 2 || State.Freed != 1) return 1;
            }
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task AsyncUnwindCleanupCanReplaceAwaitFailure(string profile) => ResumableRuntimeTests.Run(
        ResumableRuntimeTests.Protocol + """
        struct Bomb { public ~Bomb() { Harness.Destroyed++; throw 8; } }
        async Result Work() {
            using var first = new Resource(1);
            using var second = new Bomb();
            return await Operation();
        }
        int Main() {
            Result result = Work();
            if (result.State->Status != 0 || Harness.Destroyed != 0) return 1;
            Harness.Ready = true; Harness.Fail = true; Harness.Next();
            if (result.State->Status != 2 || result.State->Value != 8 || Harness.Destroyed != 2) return 2;
            Harness.Next();
            if (Harness.Destroyed != 2) return 3;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug", false, false)]
    [InlineData("release", false, false)]
    [InlineData("debug", true, false)]
    [InlineData("release", true, false)]
    [InlineData("debug", false, true)]
    [InlineData("release", false, true)]
    [InlineData("debug", true, true)]
    [InlineData("release", true, true)]
    public Task UnwindCleanupPreservesOrReplacesTheExceptionExactlyOnce(string profile, bool rethrow, bool replace) =>
        ResumableRuntimeTests.Run(Resources + $$"""
        static struct Errors { public static int Original; public static int Replacement; }
        struct Fault {
            public int Code;
            public Fault(int code) { Code = code; }
            public ~Fault() { if (Code == 7) Errors.Original++; else Errors.Replacement++; }
        }
        struct Bomb {
            public ~Bomb() { State.Dropped++; {{(replace ? "throw Fault(8);" : "")}} }
        }
        void Work() {
            {{(rethrow ? "try { throw Fault(7); } catch (readonly Fault& caught) {" : "")}}
            using var outer = new Item(1);
            using var bomb = new Bomb();
            {{(rethrow ? "throw; }" : "throw Fault(7);")}}
        }
        int Main() {
            try { Work(); return 1; }
            catch (readonly Fault& error) {
                if (error.Code != {{(replace ? 8 : 7)}} || State.Dropped != 2 || State.Order != 1) return 2;
                if (Errors.Original != {{(replace ? 1 : 0)}} || Errors.Replacement != 0) return 3;
            }
            if (Errors.Original != 1 || Errors.Replacement != {{(replace ? 1 : 0)}}) return 4;
            return 42;
        }
        """, profile);

    private const string Resources = """
        namespace UsingTests;
        static struct State {
            public static int Order; public static int Dropped;
            public static int Allocated; public static int Freed;
        }
        allocator Heap {
            +Heap(nuint size, nuint alignment) { State.Allocated++; return malloc(size, alignment); }
            -Heap(void* block) { State.Freed++; free(block); }
        }
        struct Item {
            public int Value;
            public Item() { Value = 1; }
            public Item(int value) { if (value == 9) throw 9; Value = value; }
            public ~Item() { State.Order = State.Order * 10 + Value; State.Dropped++; }
        }
        Item* Create(int value) { return new Item(value); }
        """;

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task ScopeOrderMutationAndReturn(string profile) => ResumableRuntimeTests.Run(Resources + """
        int Work() {
            using var first = Create(1);
            using Item second = new Item(2);
            { using Item* third = new Item(3); third->Value = 4; }
            if (State.Order != 4) return -1;
            return second->Value + first->Value;
        }
        int Main() {
            if (Work() != 3 || State.Order != 421 || State.Dropped != 3) return 1;
            State.Order = 0;
            { using var a = new Item(5); using var b = new Item(6); }
            if (State.Order != 65 || State.Dropped != 5) return 2;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task BreakContinueAndSwitch(string profile) => ResumableRuntimeTests.Run(Resources + """
        int Main() {
            {
                using var outer = new Item(4);
                for (int i = 1; i < 4; i++) {
                    using var item = new Item(i);
                    if (i == 1) continue;
                    { using var nested = new Item(5); if (i == 2) break; }
                }
                if (State.Order != 152 || State.Dropped != 3) return 1;
                switch (2) {
                    case 2: using var item = new Item(6); break;
                    default: return 2;
                }
                if (State.Order != 1526) return 3;
            }
            if (State.Order != 15264 || State.Dropped != 5) return 4;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task ExceptionsInitializationAndFinally(string profile) => ResumableRuntimeTests.Run(Resources + """
        int Main() {
            using var heap = new Heap();
            try {
                using var first = new(heap) Item(1);
                { using var second = new(heap) Item(2); using var failed = new(heap) Item(9); }
                return 1;
            } catch (readonly int& error) {
                if (error != 9 || State.Order != 21 || State.Dropped != 2 || State.Allocated != 3 || State.Freed != 3) return 2;
                using var caught = new Item(3);
            } finally {
                using var final = new Item(4);
            }
            if (State.Order != 2134 || State.Dropped != 4) return 3;
            State.Order = 0;
            try {
                using var a = new Item(5);
                { using var b = new Item(6); throw 7; }
            } catch (readonly int& error) {
                if (error != 7 || State.Order != 65 || State.Dropped != 6) return 4;
            }
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task ArraysAndCustomAllocatorsDeleteExactlyOnce(string profile) => ResumableRuntimeTests.Run(Resources + """
        int Main() {
            using var heap = new Heap();
            {
                using var scalar = new(heap) Item(2);
                using var values = new(heap) Item[3];
                using var numbers = new(heap) int[4];
                values[0].Value = 1; values[1].Value = 3; values[2].Value = 1; numbers[0] = 42;
                if (numbers[0] != 42) return 1;
            }
            if (State.Dropped != 4) return 100 + State.Dropped;
            if (State.Order != 1312) return 20;
            if (State.Allocated != 3 || State.Freed != 3) return 30;
            State.Order = 0;
            { using var values = new Item[2]; values[0].Value = 1; values[1].Value = 1; using var numbers = new int[3]; numbers[0] = 1; }
            if (State.Order != 11 || State.Dropped != 6) return 3;
            Item* manual = new(heap) Item(4); delete(manual);
            if (State.Dropped != 7 || State.Freed != 4) return 4;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug", false)]
    [InlineData("release", false)]
    [InlineData("debug", true)]
    [InlineData("release", true)]
    public Task AsyncSuspensionAndFailure(string profile, bool fail) => ResumableRuntimeTests.Run(
        ResumableRuntimeTests.Protocol + $$"""
        async Result Work() {
            using var resource = new Resource(0);
            resource->Value = 8;
            int value = await Operation();
            return value + resource->Value;
        }
        int Main() {
            Result result = Work();
            if (result.State->Status != 0 || Harness.Destroyed != 0) return 1;
            Harness.Fail = {{(fail ? "true" : "false")}};
            Harness.Ready = true;
            Harness.Next();
            if (result.State->Status != {{(fail ? 2 : 1)}} || result.State->Value != {{(fail ? 9 : 50)}} || Harness.Destroyed != 1) return 2;
            Harness.Next();
            if (Harness.Destroyed != 1) return 3;
            return 42;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task ThrowingCleanupStillDeletesOuterResources(string profile) => ResumableRuntimeTests.Run(Resources + """
        struct Failure {
            public ~Failure() { State.Dropped++; throw 8; }
        }
        int Main() {
            using var heap = new Heap();
            try {
                using var outer = new(heap) Item(1);
                using var failure = new(heap) Failure();
            } catch (readonly int& error) {
                if (error != 8 || State.Dropped != 2 || State.Order != 1 || State.Freed != 2) return 1;
                return 42;
            }
            return 2;
        }
        """, profile);

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public Task AsyncInitializerFailureCleansOnlyEarlierResources(string profile) => ResumableRuntimeTests.Run(
        ResumableRuntimeTests.Protocol + """
        Resource* Fail() { throw 7; }
        async Result Work() {
            using var first = new Resource(1);
            await Operation();
            using var second = Fail();
            return second->Value;
        }
        int Main() {
            Result result = Work();
            if (result.State->Status != 0 || Harness.Destroyed != 0) return 1;
            Harness.Ready = true;
            Harness.Next();
            if (result.State->Status != 2 || result.State->Value != 7 || Harness.Destroyed != 1) return 2;
            Harness.Next();
            if (Harness.Destroyed != 1) return 3;
            return 42;
        }
        """, profile);
}
