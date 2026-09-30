using Xenon.Compiler.Tests.ProjectSystem;
using Xenon.Driver;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class ResumableRuntimeTests
{
    private const string Protocol = """
        namespace Example;
        struct ResultState { public int Status; public int Value; }
        struct Result
        {
            public shared<ResultState> State;
            public Result() { State = new ResultState(); }
            public static void operator resolve(Result& target, int value)
            { target.State->Value = value; target.State->Status = 1; }
            public static void operator reject(Result& target, int error)
            { target.State->Value = error; target.State->Status = 2; }
        }
        static struct Harness
        {
            public static bool Ready;
            public static bool Fail;
            public static bool Reentrant;
            public static int Calls;
            public static int Destroyed;
            public static function void() Next;
        }
        struct Operation
        {
            public static bool operator await(readonly Operation& value, storage<int>& result, function void() continuation)
            {
                Harness.Calls++;
                if (Harness.Ready)
                {
                    if (Harness.Fail) throw 9;
                    result = 42;
                    return true;
                }
                Harness.Next = continuation;
                if (Harness.Reentrant) { Harness.Ready = true; continuation(); }
                return false;
            }
        }
        struct Resource
        {
            public int Value;
            public Resource(int value) { Value = value; }
            public ~Resource() { Harness.Destroyed++; }
        }
        """;

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public async Task PendingCompletionPreservesLocalsAndIgnoresDuplicateContinuation(string profile)
    {
        await Run(Protocol + """
            async Result Use()
            {
                Resource resource = Resource(8);
                int value = await Operation();
                return value + resource.Value;
            }
            int Main()
            {
                Result result = Use();
                if (result.State->Status != 0 || Harness.Calls != 1 || Harness.Destroyed != 0) return 1;
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Status != 1 || result.State->Value != 50 || Harness.Calls != 2 || Harness.Destroyed != 1) return 2;
                Harness.Next();
                if (Harness.Calls != 2 || Harness.Destroyed != 1) return 3;
                return 42;
            }
            """, profile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SynchronousAndReentrantCompletion(bool reentrant)
    {
        await Run(Protocol + $$"""
            async Result Use() { int value = await Operation(); return value; }
            int Main()
            {
                Harness.Ready = {{(!reentrant).ToString().ToLowerInvariant()}};
                Harness.Reentrant = {{reentrant.ToString().ToLowerInvariant()}};
                Result result = Use();
                if (result.State->Status != 1 || result.State->Value != 42) return 1;
                return 42;
            }
            """);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedExceptionUsesOriginalCatchOrReject(bool handled)
    {
        string body = handled
            ? "try { int value = await Operation(); return value; } catch (readonly int& error) { return error + 1; }"
            : "int value = await Operation(); return value;";
        await Run(Protocol + $$"""
            async Result Use() { Resource resource = Resource(8); {{body}} }
            int Main()
            {
                Result result = Use();
                Harness.Ready = true;
                Harness.Fail = true;
                Harness.Next();
                if (result.State->Status != {{(handled ? 1 : 2)}} || result.State->Value != {{(handled ? 10 : 9)}} || Harness.Destroyed != 1) return 1;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task MultipleAwaitsInLoopsPreserveEvaluationAndBorrowedLocal()
    {
        await Run(Protocol + """
            async Result Use()
            {
                Resource owner = Resource(3);
                Resource& reference = owner;
                int total = 0;
                for (int i = 0; i < 3; i++)
                {
                    if (i < 2) { total += await Operation(); }
                    else { switch (i) { case 2: total += await Operation(); break; default: break; } }
                    Harness.Ready = false;
                }
                return total + reference.Value;
            }
            int Main()
            {
                Result result = Use();
                for (int i = 0; i < 3; i++)
                {
                    if (result.State->Status != 0) return 1;
                    Harness.Ready = true;
                    Harness.Next();
                }
                if (result.State->Value != 129 || Harness.Calls != 6 || Harness.Destroyed != 1) return 2;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task AwaitedNonDefaultConstructibleMoveOnlyValueHasOneDestructor()
    {
        await Run("""
            namespace Example;
            static struct Harness { public static bool Ready; public static function void() Next; public static int Destroyed; }
            struct Resource
            {
                public int Value;
                public Resource(int value) { Value = value; }
                public ~Resource() { Harness.Destroyed++; }
            }
            struct State<T> { public storage<T> Value; public int Status; }
            struct TestTask<T>
            {
                public shared<State<T>> State;
                public TestTask() { State = new State<T>(); }
                public static void operator resolve(TestTask<T>& target, T value)
                { target.State->Value = move value; target.State->Status = 1; }
                public static void operator reject(TestTask<T>& target, int error) { target.State->Status = 2; }
            }
            struct Operation
            {
                public static bool operator await(readonly Operation& value, storage<unique<Resource>>& result, function void() continuation)
                {
                    if (!Harness.Ready) { Harness.Next = continuation; return false; }
                    result = new Resource(42);
                    return true;
                }
            }
            async TestTask<unique<Resource>> Use()
            {
                unique<Resource> value = await Operation();
                return move value;
            }
            int Main()
            {
                {
                    TestTask<unique<Resource>> result = Use();
                    if (result.State->Status != 0) return 1;
                    Harness.Ready = true;
                    Harness.Next();
                    if (result.State->Status != 1) return 2;
                    unique<Resource> value = move result.State->Value;
                    if (value->Value != 42 || Harness.Destroyed != 0) return 3;
                }
                if (Harness.Destroyed != 1) return 4;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task CleanupFailureRejectsBeforeAnySuccessfulCompletion()
    {
        await Run(Protocol + """
            struct FailingResource { public ~FailingResource() { throw 17; } }
            async Result Use()
            {
                FailingResource resource = FailingResource();
                int value = await Operation();
                return value;
            }
            int Main()
            {
                Result result = Use();
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Status != 2 || result.State->Value != 17) return 1;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task FinallyAndTemporaryLifetimesSurviveSuspension()
    {
        await Run(Protocol + """
            struct TemporaryOperation
            {
                public ~TemporaryOperation() { Harness.Destroyed++; }
                public static bool operator await(readonly TemporaryOperation& operation, function void() next)
                {
                    if (Harness.Ready) return true;
                    Harness.Next = next;
                    return false;
                }
            }
            async Result Use()
            {
                try { await TemporaryOperation(); }
                finally { Harness.Calls += 10; }
                return Harness.Destroyed;
            }
            int Main()
            {
                Result result = Use();
                if (Harness.Destroyed != 0 || Harness.Calls != 0) return 1;
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Value != 1 || Harness.Destroyed != 1 || Harness.Calls != 10) return 2;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task ResumableLambdaOwnsCapturesAfterCallableDestruction()
    {
        await Run(Protocol + """
            Result Start()
            {
                shared<Resource> resource = new Resource(8);
                function Result() work = async [resource]() => { int value = await Operation(); return value + resource->Value; };
                return work();
            }
            int Main()
            {
                Result result = Start();
                if (Harness.Destroyed != 0) return 1;
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Value != 50 || Harness.Destroyed != 1) return 2;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task NestedExpressionPreservesFirstArgumentAcrossSecondAwait()
    {
        await Run(Protocol + """
            Operation NextOperation() { Harness.Ready = false; return Operation(); }
            int Add(int first, int second) { return first + second; }
            async Result Use() { return Add(await NextOperation(), await NextOperation()); }
            int Main()
            {
                Result result = Use();
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Status != 0) return 1;
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Status != 1 || result.State->Value != 84 || Harness.Calls != 4) return 2;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task CrossThreadDuplicateCallbacksSerializeFrameExecution()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Run(Protocol + """
            extern void* CreateThread(void* attributes, nuint stackSize, function uint(void*)* start, void* argument, uint flags, uint* id);
            extern uint WaitForSingleObject(void* handle, uint milliseconds);
            extern bool CloseHandle(void* handle);
            uint Notify(void* argument) { Harness.Next(); return cast<uint>(0); }
            async Result Use() { Resource resource = Resource(1); int value = await Operation(); return value; }
            int Main()
            {
                for (int i = 0; i < 20; i++)
                {
                    Harness.Ready = false;
                    Result result = Use();
                    Harness.Ready = true;
                    void* first = CreateThread(null, cast<nuint>(0), &Notify, null, cast<uint>(0), null);
                    void* second = CreateThread(null, cast<nuint>(0), &Notify, null, cast<uint>(0), null);
                    if (first == null || second == null) return 1;
                    WaitForSingleObject(first, cast<uint>(10000));
                    WaitForSingleObject(second, cast<uint>(10000));
                    CloseHandle(first);
                    CloseHandle(second);
                    if (result.State->Status != 1 || result.State->Value != 42 || Harness.Calls != (i + 1) * 2 || Harness.Destroyed != i + 1) return 2;
                }
                return 42;
            }
            """, "release");
    }

    [Fact]
    public async Task PartiallyMovedLocalsAndStorageCleanUpOnce()
    {
        await Run(Protocol + """
            struct Pair { public unique<Resource> First; public unique<Resource> Second; }
            async Result Use()
            {
                Pair pair = Pair();
                pair.First = new Resource(1);
                pair.Second = new Resource(2);
                storage<unique<Resource>> slot = move pair.First;
                int value = await Operation();
                unique<Resource> moved = move slot;
                return value + moved->Value + pair.Second->Value;
            }
            int Main()
            {
                Result result = Use();
                if (Harness.Destroyed != 0) return 1;
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Value != 45 || Harness.Destroyed != 2) return 2;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task FinallyReturnReplacesAndDestroysPendingCompletionValue()
    {
        await Run(Protocol + """
            struct Handle
            {
                public shared<ResultState> State;
                public Handle() { State = new ResultState(); }
                public static void operator resolve(Handle& target, unique<Resource> value)
                { target.State->Value = value->Value; target.State->Status++; }
                public static void operator reject(Handle& target, int error) { target.State->Status = 2; }
            }
            async Handle Use()
            {
                try { await Operation(); return new Resource(1); }
                finally { return new Resource(2); }
            }
            int Main()
            {
                Handle result = Use();
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Status != 1 || result.State->Value != 2 || Harness.Destroyed != 2) return 1;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task BorrowedParameterAndReceiverUsedOnlyBeforeAwait()
    {
        await Run(Protocol + """
            struct Reader
            {
                public int Value;
                public async Result Read() { int value = this.Value; await Operation(); return value; }
            }
            async Result Read(Resource& resource) { int value = resource.Value; await Operation(); return value; }
            int Main()
            {
                Result first;
                { Resource resource = Resource(21); first = Read(resource); }
                Harness.Ready = true;
                Harness.Next();
                if (first.State->Value != 21) return 1;
                Harness.Ready = false;
                Result second;
                { Reader reader = Reader(); reader.Value = 42; second = reader.Read(); }
                Harness.Ready = true;
                Harness.Next();
                if (second.State->Value != 42) return 2;
                return 42;
            }
            """);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public async Task InlineArrayAndSelfBorrowHaveStableFrameAddresses(string profile)
    {
        await Run(Protocol + """
            async Result Use()
            {
                Resource owner = Resource(9);
                Resource* before = &owner;
                Resource& reference = owner;
                int[] values = int[16];
                values[3] = 33;
                int* arrayBefore = &values[3];
                int[]& view = values;
                await Operation();
                if (before != &reference || arrayBefore != &view[3]) return 1;
                return reference.Value + view[3];
            }
            int Main()
            {
                Result result = Use();
                if (Harness.Destroyed != 0) return 1;
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Value != 42 || Harness.Destroyed != 1) return 2;
                return 42;
            }
            """, profile);
    }

    [Fact]
    public async Task BorrowCaptureBeforeAwaitAndOwnedMoveCaptureAcrossAwait()
    {
        await Run(Protocol + """
            Result ReadBefore()
            {
                Resource resource = Resource(21);
                function Result() work = async [&resource]() => { int value = resource.Value; await Operation(); return value; };
                return work();
            }
            Result ReadOwned()
            {
                unique<Resource> resource = new Resource(42);
                function Result() work = async [move resource]() => { await Operation(); return resource->Value; };
                return work();
            }
            int Main()
            {
                Result first = ReadBefore();
                Harness.Ready = true;
                Harness.Next();
                if (first.State->Value != 21 || Harness.Destroyed != 1) return 1;
                Harness.Ready = false;
                Result second = ReadOwned();
                if (Harness.Destroyed != 1) return 2;
                Harness.Ready = true;
                Harness.Next();
                if (second.State->Value != 42 || Harness.Destroyed != 2) return 3;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task DynamicArrayScopeFinishesBeforeSuspension()
    {
        await Run(Protocol + """
            async Result Use(int count)
            {
                int value;
                { int[] values = int[count]; values[0] = 42; value = values[0]; }
                await Operation();
                return value;
            }
            int Main()
            {
                Result result = Use(16);
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Value != 42) return 1;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task ArrayCleanupAndPinnedValueRespectSuspensionRegions()
    {
        await Run(Protocol + """
            struct Cell { public int Value; public ~Cell() { Harness.Destroyed++; } }
            struct SelfReference
            {
                public SelfReference* Self;
                public SelfReference() { Self = this; }
            }
            async Result Use()
            {
                { Cell[] early = Cell[2]; }
                pin<SelfReference> stable = SelfReference();
                SelfReference* before = stable.Self;
                {
                    Cell[] live = Cell[3];
                    live[1].Value = 42;
                    await Operation();
                    if (Harness.Destroyed != 2 || before != &stable || stable.Self != &stable) return 1;
                    if (live[1].Value != 42) return 2;
                }
                Harness.Ready = false;
                await Operation();
                return Harness.Destroyed;
            }
            int Main()
            {
                Result result = Use();
                if (Harness.Destroyed != 2) return 1;
                Harness.Ready = true;
                Harness.Next();
                if (Harness.Destroyed != 5 || result.State->Status != 0) return 2;
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Value != 5 || Harness.Destroyed != 5) return 3;
                return 42;
            }
            """);
    }

    [Fact]
    public async Task StaticBorrowsAcrossAwaitKeepOriginalStorage()
    {
        await Run(Protocol + """
            static struct Data { public static int Value; public static int[] Values; }
            async Result Use()
            {
                int& value = Data.Value;
                int[]& array = Data.Values;
                await Operation();
                array[0] = value;
                return array[0];
            }
            int Main()
            {
                Data.Value = 42;
                Data.Values = new int[16];
                Result result = Use();
                Harness.Ready = true;
                Harness.Next();
                if (result.State->Value != 42 || Data.Values[0] != 42) return 1;
                free(Data.Values);
                return 42;
            }
            """);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public async Task ExternalBorrowsArraysAndReceiverSurviveNestedSuspension(string profile)
    {
        string protocol = Protocol.Replace("public int Status; public int Value;", "public int Status; public int Value; public bool HasNext; public function void() Next;")
            .Replace("target.State->Status = 1;", "target.State->Status = 1; if (target.State->HasNext) target.State->Next();")
            .Replace("public shared<ResultState> State;", """
                public shared<ResultState> State;
                public static bool operator await(readonly Result& target, storage<int>& value, function void() next)
                {
                    if (target.State->Status != 0) { value = target.State->Value; return true; }
                    target.State->Next = next; target.State->HasNext = true;
                    return false;
                }
                """);
        await Run(protocol + """
            struct Reader
            {
                public int Value;
                public async Result Read(Resource& resource, int[]& values)
                {
                    await Operation();
                    values[0] = resource.Value + this.Value;
                    return values[0];
                }
            }
            T Identity<T>(T value) { return move value; }
            async Result Parent()
            {
                Resource resource = Resource(20);
                Reader reader = Reader(); reader.Value = 22;
                int[] values = int[16];
                int* original = &values[0];
                int[]& view = values;
                Result task = Identity<Result>(reader.Read(resource, view));
                Result copy = task;
                int result = await copy;
                if (&view[0] != original || view[0] != 42) return 1;
                return result;
            }
            int Main()
            {
                Result parent = Parent();
                if (parent.State->Status != 0 || Harness.Destroyed != 0) return 1;
                Harness.Ready = true;
                Harness.Next();
                if (parent.State->Status != 1 || parent.State->Value != 42 || Harness.Destroyed != 1) return 2;
                return 42;
            }
            """, profile);
    }

    internal static async Task Run(string source, string profile = "debug", int expectedExit = 42, string? expectedError = null, bool nativeScheduler = false)
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("AwaitApp", sources: [("main.xe", source)]);
        if (nativeScheduler)
        {
            // Ordinary FFI fixture: scheduling is owned by this library, never the async root.
            directory.Write("native/scheduler.cpp", """
                #include <thread>
                #include <chrono>
                extern "C" void test_schedule_callback(void (*callback)(void*), void* context) {
                    std::thread([=] {
                        std::this_thread::sleep_for(std::chrono::milliseconds(10));
                        callback(context);
                    }).detach();
                }
                """);
            directory.Write("native/CMakeLists.txt", """
                cmake_minimum_required(VERSION 3.24)
                project(XenonTestScheduler LANGUAGES CXX)
                set(CMAKE_CXX_STANDARD 17)
                set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded")
                add_library(test_scheduler STATIC scheduler.cpp)
                """);
            string nativeBuild = directory.PathOf("native/build");
            foreach (string[] arguments in new[] {
                new[] { "-S", directory.PathOf("native"), "-B", nativeBuild },
                new[] { "--build", nativeBuild, "--config", "Release" } })
            {
                NativeProcessResult tool = await new NativeProcessRunner().RunAsync(
                    new NativeProcessRequest("cmake", arguments, directory.Root, TimeSpan.FromMinutes(3)));
                Assert.True(tool.StartError is null && !tool.TimedOut && tool.ExitCode == 0,
                    $"start={tool.StartError}; stdout={tool.Stdout}; stderr={tool.Stderr}");
            }
            string library = Path.Combine(nativeBuild, OperatingSystem.IsWindows()
                ? "Release/test_scheduler.lib" : "libtest_scheduler.a").Replace('\\', '/');
            File.AppendAllText(directory.PathOf("AwaitApp/AwaitApp.xeproj"),
                $"\n[libraries]\nlibraries = [\"{library}\"]\n");
        }
        XenonBuildResult build = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.PathOf("AwaitApp/AwaitApp.xeproj"), profile, directory.PathOf("build")));
        Assert.True(build.Success, string.Join(Environment.NewLine,
            new[] { build.Failure }.Concat(build.Diagnostics.Select(diagnostic => diagnostic.ToString()))));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            build.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.True(process.StartError is null && !process.TimedOut && process.ExitCode == expectedExit,
            $"exit={process.ExitCode}; start={process.StartError}; stdout={process.Stdout}; stderr={process.Stderr}");
        if (expectedError is not null) Assert.Contains(expectedError, process.Stderr);
    }
}
