using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirHandleLifetimeTests
{
    [Theory]
    [InlineData("shared<State>", "readonly Box&")]
    [InlineData("shared<State>", "Box&")]
    [InlineData("unique<State>", "Box&")]
    [InlineData("State*", "Box&")]
    public void BorrowedHandleFieldCanMoveCompletePointeeStorage(string handle, string parameter)
    {
        var compilation = Compilation.Create(SourceText.From($$"""
            namespace Regression;
            struct State { public storage<int> Value; }
            struct Box { public {{handle}} State; }
            void Take({{parameter}} box, storage<int>& result) {
                result = move box.State->Value;
            }
            """, "handle-storage.xe"));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        foreach (var function in compilation.GetMirFunctions()) Assert.Empty(MirVerifier.Verify(function));
    }

    [Fact]
    public void MovingTheBorrowedHandleItselfStillRequiresLifetimeAuthority()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Regression;
            struct Box { public shared<int> State; }
            shared<int> Take(Box& box) { return move box.State; }
            """));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ReferenceParameterLifetimeMutation);
    }

    [Fact]
    public void GenericTaskCanMoveItsSharedStateStorage()
    {
        var compilation = Compilation.Create(SourceText.From(TaskProtocol + """

            async Task<int> Main() {
                Task<int> task = Task<int>();
                resolve(task, 42);
                return await task;
            }
            """, "Task.xe"));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        foreach (var function in compilation.GetMirFunctions()) Assert.Empty(MirVerifier.Verify(function));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData("shared<State>")]
    [InlineData("unique<State>")]
    [InlineData("State*")]
    public void ReferenceToHandleCanMovePointeeStorage(string handle)
    {
        var compilation = Compilation.Create(SourceText.From($$"""
            namespace Regression;
            struct State { public storage<int> Value; }
            void Take({{handle}}& state, storage<int>& result) { result = move state->Value; }
            """));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Fact]
    public void NestedHandlesCrossDistinctStorageBoundaries()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Regression;
            struct State { public storage<int> Value; }
            struct Inner { public shared<State> State; }
            struct Outer { public shared<Inner> Inner; }
            void Take(readonly Outer& outer, storage<int>& result) {
                shared<State>& alias = outer.Inner->State;
                result = move alias->Value;
            }
            """));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Fact]
    public void GenericTaskStorageMoveSurvivesLibraryRoundTrip()
    {
        var library = Compilation.Create(SourceText.From(TaskProtocol
            .Replace("struct TaskState<T>", "public struct TaskState<T>")
            .Replace("struct Task<T>", "public struct Task<T>")));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Tasks")));
        var compilation = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Program;
            namespace Consumer;
            async Task<int> Main() {
                Task<int> task = Task<int>();
                resolve(task, 42);
                return await task;
            }
            """));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData("debug", false)]
    [InlineData("release", false)]
    [InlineData("debug", true)]
    [InlineData("release", true)]
    public async Task AwaitMovesStoredResourceAndDestroysItOnce(string profile, bool suspended)
    {
        await ResumableRuntimeTests.Run(TaskProtocol + $$"""

            static struct Counts { public static int Destroyed; }
            struct Resource {
                public int Value;
                public Resource(int value) { Value = value; }
                public ~Resource() { Counts.Destroyed += 1; }
            }
            async Task<int> Consume(Task<Resource> task) {
                Resource value = await task;
                return value.Value;
            }
            int Main() {
                Task<Resource> task = Task<Resource>();
                {{(suspended ? "Task<int> result = Consume(task);" : "")}}
                resolve(task, Resource(42));
                {{(suspended ? "" : "Task<int> result = Consume(task);")}}
                if (result.State->Status != 1 || Counts.Destroyed != 1) return 1;
                int value = move result.State->Value;
                return value;
            }
            """, profile);
    }

    [Fact]
    public void TraversingRecursiveHandleFieldsConverges()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Regression;
            struct Node { public shared<Node> Next; public int Value; }
            int Read(shared<Node> node, int count) {
                while (count > 0) { node = node->Next; count -= 1; }
                return node->Value;
            }
            """));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    private const string TaskProtocol = """
        namespace Program;
        extern void Sleep(uint milliseconds);
        extern void* CreateThread(void* threadAttributes, nuint stackSize,
            function uint(void*)* startAddress, void* parameter, uint creationFlags, uint* threadId);
        extern bool CloseHandle(void* handle);
        struct TaskState<T> {
            public int Status;
            public storage<T> Value;
            public int Error;
            public bool HasContinuation;
            public function void() Continuation;
        }
        struct Task<T> {
            public shared<TaskState<T>> State;
            public Task() { State = new TaskState<T>(); }
            public static void operator resolve(Task<T>& task, T value) {
                task.State->Value = move value;
                task.State->Status = 1;
                if (task.State->HasContinuation) {
                    task.State->HasContinuation = false;
                    task.State->Continuation();
                }
            }
            public static void operator reject(Task<T>& task, int error) {
                task.State->Error = error;
                task.State->Status = 2;
                if (task.State->HasContinuation) {
                    task.State->HasContinuation = false;
                    task.State->Continuation();
                }
            }
            public static bool operator await(readonly Task<T>& task, storage<T>& result, function void() continuation) {
                if (task.State->Status == 1) {
                    result = move task.State->Value;
                    return true;
                }
                if (task.State->Status == 2) { throw task.State->Error; }
                task.State->Continuation = continuation;
                task.State->HasContinuation = true;
                return false;
            }
            public static Task<int> Yield() {
                Task<int> task = Task<int>();
                TaskScheduler.PendingYield = task;
                void* thread = CreateThread(null, cast<nuint>(0), &YieldThread, null, cast<uint>(0), null);
                if (thread != null) { CloseHandle(thread); }
                return task;
            }
            public static Task<int> WaitForSeconds(float seconds) {
                Task<int> task = Task<int>();
                TaskScheduler.PendingDelay = task;
                TaskScheduler.DelayMilliseconds = cast<uint>(seconds * 1000.0f);
                void* thread = CreateThread(null, cast<nuint>(0), &DelayThread, null, cast<uint>(0), null);
                if (thread != null) { CloseHandle(thread); }
                return task;
            }
        }
        static struct TaskScheduler {
            public static Task<int> PendingYield;
            public static Task<int> PendingDelay;
            public static uint DelayMilliseconds;
        }
        uint YieldThread(void* context) {
            Sleep(cast<uint>(1));
            resolve(TaskScheduler.PendingYield, 0);
            return cast<uint>(0);
        }
        uint DelayThread(void* context) {
            Sleep(TaskScheduler.DelayMilliseconds);
            resolve(TaskScheduler.PendingDelay, 0);
            return cast<uint>(0);
        }
        """;
}