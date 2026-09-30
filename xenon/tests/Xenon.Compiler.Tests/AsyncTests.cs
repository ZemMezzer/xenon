using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class AsyncTests
{
    private const string Protocol = """
        namespace Example;
        public struct AppState { public int Status; public int Value; public bool HasNext; public function void() Next; }
        public struct AppResult
        {
            public shared<AppState> State;
            public AppResult() { State = new AppState(); }
            public static void operator resolve(AppResult& result, int value)
            { result.State->Value = value; result.State->Status = 1; if (result.State->HasNext) result.State->Next(); }
            public static void operator reject(AppResult& result, int error)
            { result.State->Value = error; result.State->Status = 2; if (result.State->HasNext) result.State->Next(); }
            public static bool operator await(readonly AppResult& result, storage<int>& value, function void() next)
            {
                if (result.State->Status == 2) throw result.State->Value;
                if (result.State->Status == 1) { value = result.State->Value; return true; }
                result.State->Next = next; result.State->HasNext = true; return false;
            }
        }
        """;

    [Theory]
    [InlineData("async public AppResult Value() { return 42; }")]
    [InlineData("public async AppResult Value() { return 42; }")]
    [InlineData("struct Methods { public static async AppResult Value() { return 42; } }")]
    [InlineData("struct Methods { async public static AppResult Value() { return 42; } }")]
    public void ModifierCombinationsAndSynchronousLowering(string source)
    {
        var compilation = Compile(source);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var function = compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == "Value");
        Assert.True(function.Symbol.IsAsync);
        Assert.True(function.Body.IsResumable);
        Assert.False(function.Body.RequiresSuspensionStateMachine);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.DoesNotContain("llvm.coro", ir);
        Assert.DoesNotContain("__xenon_resume_create", ir);
        Assert.Contains(BoundTree.DescendantsAndSelf(function.Body).OfType<BoundCallExpression>(), call => call.Function.OperatorKind == OperatorKind.Resolve);
    }

    [Theory]
    [InlineData("AppResult Foo() { return await AppResult(); }", "'await' may only be used inside an async function")]
    [InlineData("AppResult Foo() { return 42; }", "cannot")]
    [InlineData("async int Foo() { return 42; }", "completion protocol")]
    [InlineData("async AppResult Foo() {}", "not all code paths produce a value")]
    [InlineData("struct Bad { public Bad(int value) {} public static void operator resolve(Bad& value) {} } async Bad Foo() {}", "accessible parameterless constructor")]
    [InlineData("struct Plain {} async Plain Foo() {}", "operator resolve")]
    [InlineData("struct OnlyReject { public static void operator reject(OnlyReject& r, int error) {} } async OnlyReject Foo() { throw 1; }", "operator resolve")]
    [InlineData("struct Good { public static void operator resolve(Good& value) {} } async Good Foo() { throw 12; }", "escaping exception 'int'")]
    public void InvalidAsyncUsageIsDiagnosed(string source, string expected)
    {
        var compilation = Compile(source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains(expected));
    }

    [Fact]
    public void NothrowCompletionDoesNotRequireReject()
    {
        var compilation = Compile("struct Ready { public static void operator resolve(Ready& r) {} } async Ready Foo() {}");
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void OrdinaryForwarderRemainsOrdinary()
    {
        var compilation = Compile("AppResult Foo() { return AppResult(); }");
        var function = compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == "Foo");
        Assert.False(function.Symbol.IsAsync);
        Assert.False(function.Body.IsResumable);
    }

    [Fact]
    public void LibraryAndGenericSpecializationPreserveAsync()
    {
        var library = Compile("public async AppResult Value<T>(T ignored) { return 42; }");
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Async")));
        var consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Example; namespace App; AppResult Foo() { return Value<int>(1); }"));
        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        var imported = consumer.GetStaticImplementationFunctions().Single(f => f.Symbol.Name.StartsWith("Value"));
        Assert.True(imported.Symbol.IsAsync);
        Assert.False(imported.Body.RequiresSuspensionStateMachine);
        _ = new LlvmIrGenerator().GenerateForTarget(consumer, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public async Task SynchronousMainReturnsLogicalExitCode(string profile)
    {
        await ResumableRuntimeTests.Run(Protocol + "async AppResult Main() { return 42; }", profile);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public async Task NestedAsyncMainUsesProtocol(string profile)
    {
        await ResumableRuntimeTests.Run(Protocol + """
            async AppResult GetValue() { return 21; }
            async AppResult Calculate() { int value = await GetValue(); return value * 2; }
            async AppResult Main() { return await Calculate(); }
            """, profile);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public async Task NativeAwaitableCompletesSuspendedMain(string profile)
    {
        await ResumableRuntimeTests.Run(Protocol + """
            extern void test_schedule_callback(function void(void*)* callback, void* context);
            static struct Pending { public static atomic<bool> Ready; public static function void() Next; }
            export void Complete(void* ignored) {
                function void() next = Pending.Next;
                Pending.Ready = true;
                next();
            }
            struct Operation
            {
                public static bool operator await(readonly Operation& op, storage<int>& value, function void() next)
                {
                    if (Pending.Ready) { value = 21; Pending.Ready = false; return true; }
                    Pending.Next = next;
                    test_schedule_callback(&Complete, null);
                    return false;
                }
            }
            async AppResult Main() { int a = await Operation(); int b = await Operation(); return a + b; }
            """, profile, nativeScheduler: true);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public async Task RejectedMainUsesUnhandledEntryPolicy(string profile)
    {
        await ResumableRuntimeTests.Run(Protocol + "async AppResult Main() { throw 17; }", profile,
            expectedExit: 1, expectedError: "Unhandled exception of type 'int'");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VoidLikeMainCompletesWithZero(bool explicitReturn)
    {
        await ResumableRuntimeTests.Run("""
            namespace Example;
            struct Done
            {
                public bool Ready;
                public static void operator resolve(Done& result) { result.Ready = true; }
                public static bool operator await(readonly Done& result, function void() next) { return result.Ready; }
            }
            """ + "async Done Main() { " + (explicitReturn ? "return;" : "") + " }", expectedExit: 0);
    }

    [Theory]
    [InlineData("debug")]
    [InlineData("release")]
    public async Task SynchronousCompletionHonorsCatchFinallyAndDestruction(string profile)
    {
        await ResumableRuntimeTests.Run(Protocol + """
            static struct State { public static int Destroyed; }
            struct Resource { public ~Resource() { State.Destroyed++; } }
            async AppResult Value()
            {
                Resource resource = Resource();
                try { throw 12; }
                catch (readonly int& error) { return error; }
                finally { State.Destroyed += 10; }
            }
            async AppResult Rejected() { throw 9; }
            int Main()
            {
                AppResult value = Value();
                if (value.State->Status != 1 || value.State->Value != 12 || State.Destroyed != 11) return 1;
                AppResult error = Rejected();
                if (error.State->Status != 2 || error.State->Value != 9) return 2;
                return 42;
            }
            """, profile);
    }

    [Theory]
    [InlineData("struct Bad { async int Field; }")]
    [InlineData("async protected AppResult Foo() { return 1; }")]
    [InlineData("interface Bad { async int Value { get; } }")]
    [InlineData("struct Bad { public async Bad() {} }")]
    [InlineData("async extern AppResult Foo();")]
    [InlineData("async async AppResult Foo() { return 1; }")]
    public void InvalidModifierPlacementIsDiagnosed(string source)
    {
        Assert.True(Compile(source).HasErrors);
    }

    [Fact]
    public void OverrideKeepsOrdinaryDispatchSignature()
    {
        var compilation = Compile("""
            abstract struct Base { public abstract AppResult Value(); }
            struct Derived : Base { public override async AppResult Value() { return 42; } }
            interface IValue { async AppResult Value(); }
            struct Concrete : IValue { public AppResult Value() { return AppResult(); } }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootContinuationWakesForDirectOrWorkerNotification(bool worker)
    {
        if (worker && !OperatingSystem.IsWindows()) return;
        string scheduling = worker
            ? "void* thread = CreateThread(null, cast<nuint>(0), &Notify, null, cast<uint>(0), null); if (thread == null) throw 1; CloseHandle(thread);"
            : "Signal.Ready = true; next(); next();";
        await ResumableRuntimeTests.Run("""
            namespace Example;
            extern void* CreateThread(void* attributes, nuint stackSize, function uint(void*)* start, void* argument, uint flags, uint* id);
            extern bool CloseHandle(void* handle);
            static struct Signal { public static atomic<bool> Ready; public static function void() Next; }
            uint Notify(void* context) { Signal.Ready = true; Signal.Next(); return cast<uint>(0); }
            struct AppResult
            {
                public int Value;
                public static void operator resolve(AppResult& result, int value) { result.Value = value; }
                public static bool operator await(readonly AppResult& result, storage<int>& value, function void() next)
                {
                    if (Signal.Ready) { value = result.Value; return true; }
                    Signal.Next = next;
            """ + scheduling + """
                    return false;
                }
            }
            async AppResult Main() { return 42; }
            """, "release");
    }

    [Fact]
    public void GenericStructMethodAndFunctionValuesKeepAsyncSemantics()
    {
        var compilation = Compile("""
            struct Factory<T> { public static async AppResult Value(T input) { return 42; } }
            AppResult Use() { function AppResult() work = async () => { return 42; }; return work(); }
            AppResult Call() { return Factory<int>.Value(1); }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.Contains(compilation.SemanticModel.Functions, f => f.Symbol.IsAsync && f.Symbol.Name == "Value");
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData("struct Result { public static void operator resolve(Result& r) {} }", "operator await")]
    [InlineData("struct Result { public static void operator resolve(Result& r) {} public static bool operator await(readonly Result& r, storage<bool>& value, function void() next) { value = true; return true; } }", "int or void")]
    public void InvalidEntryAwaitProtocolIsASemanticDiagnostic(string protocol, string message)
    {
        var compilation = Compilation.Create(new CompilationOptions(CompilationOutputKind.Executable), [],
            SourceText.From("namespace Entry; " + protocol + " async Result Main() {}"));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains(message));
    }

    [Theory]
    [InlineData("export async")]
    [InlineData("async export public")]
    [InlineData("public async export")]
    public void ExportModifierOrderParses(string modifiers)
    {
        var tree = SyntaxTree.Parse(SourceText.From("namespace Example; " + modifiers + " Result Foo() { return 42; }"));
        Assert.Empty(tree.Diagnostics);
        var function = Assert.Single(tree.Root.Members.OfType<FunctionDeclarationSyntax>());
        Assert.True(function.IsAsync);
        Assert.True(function.IsExport);
    }

    [Fact]
    public void ImportedGenericAsyncLambdaRetainsModifier()
    {
        var library = Compile("public function AppResult() Make<T>(T value) { return async [move value]() => { return 42; }; }");
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("AsyncLambda")));
        var consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Example; namespace App; AppResult Use() { function AppResult() value = Make<int>(1); return value(); }"));
        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        Assert.Contains(consumer.GetStaticImplementationFunctions(), f => f.Symbol.IsLambda && f.Symbol.IsAsync);
        _ = new LlvmIrGenerator().GenerateForTarget(consumer, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void AsyncMainRootsImportedAwaitImplementation()
    {
        var library = Compile("");
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("AsyncEntry")));
        var consumer = Compilation.Create(new CompilationOptions(CompilationOutputKind.Executable), [reference],
            SourceText.From("using Example; namespace App; async AppResult Main() { return 42; }"));
        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        Assert.Contains(consumer.GetStaticImplementationFunctions(), function => function.Symbol.OperatorKind == OperatorKind.Await);
        string ir = new LlvmIrGenerator().GenerateForTarget(consumer, LlvmTargetOptions.CreateHost());
        Assert.Contains("async.root.retry", ir);
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(Protocol + source));
}
