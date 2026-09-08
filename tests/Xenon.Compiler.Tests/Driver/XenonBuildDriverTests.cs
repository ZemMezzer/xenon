using Xenon.CodeGen.LLVM;
using Xenon.Driver;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.Driver;

public sealed class XenonBuildDriverTests
{
    [Theory]
    [InlineData(XenonProjectType.Executable, "x86_64-pc-linux-gnu", true)]
    [InlineData(XenonProjectType.StaticLibrary, "x86_64-pc-linux-gnu", true)]
    [InlineData(XenonProjectType.SharedLibrary, "x86_64-pc-linux-gnu", true)]
    [InlineData(XenonProjectType.Executable, "x86_64-pc-windows-msvc", false)]
    [InlineData(XenonProjectType.StaticLibrary, "x86_64-pc-windows-msvc", false)]
    [InlineData(XenonProjectType.SharedLibrary, "x86_64-pc-windows-msvc", true)]
    public void PositionIndependentCodePolicySupportsUnixPieAndSharedLibraries(
        XenonProjectType projectType,
        string triple,
        bool expected)
    {
        Assert.Equal(expected,
            XenonBuildDriver.RequiresPositionIndependentCode(projectType, triple));
    }

    [Fact]
    public void HostExecutableBuildEmitsLinksAndIsRunnable()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("HostApp", "executable", "namespace HostApp; int Main() { return 42; }");

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(result.Success, result.Failure);
        Assert.False(result.NativeLinkSkipped);
        Assert.True(result.IsRunnable);
        Assert.True(File.Exists(result.ObjectPath));
        Assert.True(File.Exists(result.ArtifactPath));
    }

    [Fact]
    public async Task NativeExceptionsUnwindDestructorsAndRunFinallyAcrossCalls()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ExceptionApp", "executable", """
            namespace ExceptionApp;
            static struct State { public static int Trace; }
            struct Guard
            {
                public int Digit;
                public Guard(int digit) { Digit = digit; }
                public ~Guard() { State.Trace = State.Trace * 10 + Digit; }
            }
            void Fail()
            {
                Guard inner = Guard(2);
                throw 40;
            }
            int Run()
            {
                Guard outer = Guard(1);
                try { Fail(); }
                catch (readonly int& value)
                {
                    State.Trace = State.Trace * 10 + 3;
                    return value;
                }
                finally { State.Trace = State.Trace * 10 + 4; }
                return 0;
            }
            int Main()
            {
                int value = Run();
                if (value == 40 && State.Trace == 2341) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(result.Success, string.Join(Environment.NewLine,
            new[] { result.Failure }.Concat(result.Diagnostics.Select(diagnostic => diagnostic.ToString()))));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.True(process.ExitCode == 42,
            $"exit={process.ExitCode}; stdout={process.Stdout}; stderr={process.Stderr}");
    }

    [Fact]
    public async Task SharedLibraryExceptionUsesProcessRuntimeAndUnwindsCallerCleanup()
    {
        using var directory = new TemporaryProject();
        string engineProject = directory.WriteDependencyProject("ExceptionEngine", "shared-library", """
            namespace ExceptionEngine;
            public struct MyError
            {
                public int Code;
                public MyError(int code) { Code = code; }
            }
            public void Fail() { throw MyError(40); }
            """);
        directory.WriteProject("SharedExceptionApp", "executable", """
            using ExceptionEngine;
            namespace SharedExceptionApp;
            static struct State { public static int Destructions; }
            struct Resource { public ~Resource() { State.Destructions++; } }
            int Run()
            {
                Resource resource = Resource();
                try { Fail(); }
                catch (readonly MyError& error) { return error.Code; }
                return 0;
            }
            int Main()
            {
                int result = Run();
                if (result == 40 && State.Destructions == 1) return 42;
                return 1;
            }
            """, engineProject);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, string.Join(Environment.NewLine,
            new[] { result.Failure }.Concat(result.Diagnostics.Select(diagnostic => diagnostic.ToString()))));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.True(process.ExitCode == 42,
            $"exit={process.ExitCode}; stdout={process.Stdout}; stderr={process.Stderr}");
    }

    [Fact]
    public async Task ExceptionCrossesTwoSharedModulesWithOneActiveRecord()
    {
        using var directory = new TemporaryProject();
        string sourceProject = directory.WriteDependencyProject("ExceptionSource", "shared-library", """
            namespace ExceptionSource;
            public struct ModuleError
            {
                public int Code;
                public ModuleError(int code) { Code = code; }
            }
            public void Fail() { throw ModuleError(41); }
            """);
        string bridgeProject = directory.WriteDependencyProject("ExceptionBridge", "shared-library", """
            using ExceptionSource;
            namespace ExceptionBridge;
            public int Translate()
            {
                try { Fail(); }
                catch (readonly ModuleError& error) { return error.Code + 1; }
                return 1;
            }
            """, $"../{sourceProject}");
        directory.WriteProject("SharedExceptionChain", "executable", """
            using ExceptionBridge;
            namespace SharedExceptionChain;
            int Main() { return Translate(); }
            """, bridgeProject);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, string.Join(Environment.NewLine,
            new[] { result.Failure }.Concat(result.Diagnostics.Select(diagnostic => diagnostic.ToString()))));
        string runtimePath = XenonBuildPaths.GetSharedLibraryPath(
            directory.OutputRoot, "xenon-eh-runtime", "debug", LlvmTargetPlatform.HostTriple);
        Assert.True(File.Exists(runtimePath), runtimePath);

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.True(process.ExitCode == 42,
            $"exit={process.ExitCode}; stdout={process.Stdout}; stderr={process.Stderr}");
    }

    [Fact]
    public async Task DestructorThrowDuringNormalScopeExitPropagates()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("NormalDestructorThrow", "executable", """
            namespace NormalDestructorThrow;
            struct Resource { public ~Resource() { throw 42; } }
            int Main()
            {
                try { { Resource resource = Resource(); } }
                catch (readonly int& error) { return error; }
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task VirtualDestructorThrowDuringNormalFreePropagates()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("VirtualDestructorThrow", "executable", """
            namespace VirtualDestructorThrow;
            struct Base { public virtual ~Base() { } }
            struct Derived : Base { public override ~Derived() { throw 42; } }
            int Main()
            {
                try
                {
                    Base* value = new Derived();
                    free(value);
                }
                catch (readonly int& error) { return error; }
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task VirtualDestructorThrowDuringUnwindTerminates()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("VirtualDestructorDoubleThrow", "executable", """
            namespace VirtualDestructorDoubleThrow;
            struct First { }
            struct Second { }
            struct Base { public virtual ~Base() { } }
            struct Derived : Base { public override ~Derived() { throw Second(); } }
            int Main()
            {
                unique<Derived> value = new Derived();
                throw First();
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, string.Join(Environment.NewLine,
            new[] { result.Failure }.Concat(result.Diagnostics.Select(diagnostic => diagnostic.ToString()))));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Unhandled exception", process.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArrayDestructorThrowPropagatesAndRemainingElementsUnwindOnce()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ArrayDestructorThrow", "executable", """
            namespace ArrayDestructorThrow;
            static struct State
            {
                public static int NextId;
                public static int Trace;
                public static int Next() { State.NextId++; return State.NextId; }
            }
            struct Item
            {
                public int Id = State.Next();
                public ~Item()
                {
                    State.Trace = State.Trace * 10 + Id;
                    if (Id == 2) throw 42;
                }
            }
            int Main()
            {
                try { Item[] values = Item[3]; }
                catch (readonly int& error) { }
                if (State.Trace != 321) return 1;
                State.NextId = 0;
                State.Trace = 0;
                try
                {
                    Item[] values = new Item[3];
                    free(values);
                }
                catch (readonly int& error) { }
                if (State.Trace == 321) return 42;
                return 2;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, string.Join(Environment.NewLine,
            new[] { result.Failure }.Concat(result.Diagnostics.Select(diagnostic => diagnostic.ToString()))));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ThrowingReplacementDestructorDoesNotDestroyOldValueTwice()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ReplacementDestructorThrow", "executable", """
            namespace ReplacementDestructorThrow;
            static struct State { public static int Trace; }
            struct Item
            {
                public int Id;
                public Item(int id) { Id = id; }
                public ~Item()
                {
                    State.Trace = State.Trace * 10 + Id;
                    if (Id == 1) throw 7;
                }
            }
            int Main()
            {
                try
                {
                    Item value = Item(1);
                    value = Item(2);
                }
                catch (readonly int& error) { }
                if (State.Trace == 12) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ThrowingFieldReplacementInsideDestructorCleansNewValueOnlyOnce()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("DestructorFieldReplacement", "executable", """
            namespace DestructorFieldReplacement;
            static struct State { public static int Trace; }
            struct Field
            {
                public int Id;
                public Field(int id) { Id = id; }
                public ~Field()
                {
                    State.Trace = State.Trace * 10 + Id;
                    if (Id == 1) throw 7;
                }
            }
            struct Owner
            {
                public Field Value = Field(1);
                public ~Owner() { this.Value = Field(2); }
            }
            int Main()
            {
                try { Owner value = Owner(); }
                catch (readonly int& error) { }
                if (State.Trace == 12) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task FinallyReturnValueSurvivesThrowFromSupersededReturnDestructor()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("FinallyReturnDestructorThrow", "executable", """
            namespace FinallyReturnDestructorThrow;
            static struct State { public static int Trace; }
            struct Item
            {
                public int Id;
                public Item(int id) { Id = id; }
                public ~Item()
                {
                    State.Trace = State.Trace * 10 + Id;
                    if (Id == 1) throw 7;
                }
            }
            Item Make()
            {
                try { return Item(1); }
                finally { return Item(2); }
            }
            int Main()
            {
                try { Item value = Make(); }
                catch (readonly int& error) { }
                if (State.Trace == 12) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ExceptionLeavingCatchDestroysCaughtValueBeforeFinally()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("CatchExceptionLifetime", "executable", """
            namespace CatchExceptionLifetime;
            static struct State { public static int Trace; }
            struct Error
            {
                public ~Error() { State.Trace = State.Trace * 10 + 1; }
            }
            int Main()
            {
                try
                {
                    try { throw Error(); }
                    catch (readonly Error& error) { throw 7; }
                    finally { State.Trace = State.Trace * 10 + 2; }
                }
                catch (readonly int& error) { }
                if (State.Trace == 12) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ThrowFromDestructorBodyStillDestroysFieldsAndBaseOnce()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("DestructorBodyThrow", "executable", """
            namespace DestructorBodyThrow;
            static struct State { public static int Trace; }
            struct Base { public ~Base() { State.Trace = State.Trace * 10 + 3; } }
            struct Field
            {
                public int Id;
                public Field(int id) { Id = id; }
                public ~Field() { State.Trace = State.Trace * 10 + Id; }
            }
            struct Owner : Base
            {
                public Field First = Field(1);
                public Field Second = Field(2);
                public ~Owner()
                {
                    State.Trace = State.Trace * 10 + 9;
                    throw 7;
                }
            }
            int Main()
            {
                try { Owner value = Owner(); }
                catch (readonly int& error) { }
                if (State.Trace == 9213) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ThrowFromFieldDestructorContinuesWithRemainingFieldAndBase()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("FieldDestructorThrow", "executable", """
            namespace FieldDestructorThrow;
            static struct State { public static int Trace; }
            struct Base { public ~Base() { State.Trace = State.Trace * 10 + 3; } }
            struct Field
            {
                public int Id;
                public Field(int id) { Id = id; }
                public ~Field()
                {
                    State.Trace = State.Trace * 10 + Id;
                    if (Id == 2) throw 7;
                }
            }
            struct Owner : Base
            {
                public Field First = Field(1);
                public Field Second = Field(2);
                public ~Owner() { }
            }
            int Main()
            {
                try { Owner value = Owner(); }
                catch (readonly int& error) { }
                if (State.Trace == 213) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibExceptionTypeIdentityMatchesWithoutLibrarySource()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("ExceptionLibrary", "xenon-library", """
            namespace ExceptionLibrary;
            public struct LibraryError<T> { public ~LibraryError() { } }
            public void Fail<T>() { throw LibraryError<T>(); }
            """);

        XenonBuildResult result = await BuildXelibConsumerAsync(
            directory,
            libraryProject,
            "ExceptionConsumer",
            """
            using ExceptionLibrary;
            namespace ExceptionConsumer;
            int Main()
            {
                try { Fail<int>(); }
                catch (readonly LibraryError<int>& error) { return 42; }
                return 1;
            }
            """);

        Assert.True(result.Success, string.Join(Environment.NewLine,
            new[] { result.Failure }.Concat(result.Diagnostics.Select(diagnostic => diagnostic.ToString()))));
    }

    [Fact]
    public async Task FinallyRunsOnceForLoopControlTransfers()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("FinallyBranches", "executable", """
            namespace FinallyBranches;
            static struct State { public static int Trace; }
            int Main()
            {
                for (int index = 0; index < 2; index++)
                {
                    try
                    {
                        if (index == 0) continue;
                        break;
                    }
                    finally { State.Trace = State.Trace * 10 + index + 1; }
                }
                if (State.Trace == 12) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task UnhandledExceptionTerminatesAtExecutableBoundary()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("UnhandledException", "executable", """
            namespace UnhandledException;
            int Main() { throw 42; }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("Unhandled exception of type 'int'", process.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThrownTemporaryIsDestroyedExactlyOnceAfterItsHandler()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ExceptionLifetime", "executable", """
            namespace ExceptionLifetime;
            static struct State { public static int Destructions; }
            struct Error
            {
                public ~Error() { State.Destructions++; }
            }
            int Main()
            {
                try { throw Error(); }
                catch (readonly Error& error) { }
                if (State.Destructions == 1) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ConstructorFailureDestroysOnlyInitializedFields()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ConstructorFailure", "executable", """
            namespace ConstructorFailure;
            static struct State { public static int Trace; }
            struct Guard
            {
                public int Digit;
                public Guard(int digit) { Digit = digit; }
                public ~Guard() { State.Trace = State.Trace * 10 + Digit; }
            }
            struct Container
            {
                public Guard First;
                public Guard Second;
                public Container()
                {
                    First = Guard(1);
                    throw 7;
                }
            }
            int Main()
            {
                try { Container value = Container(); }
                catch (readonly int& error) { }
                if (State.Trace == 1) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task RethrowPreservesObjectAndFinallyReplacementDestroysBothValues()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ExceptionReplacement", "executable", """
            namespace ExceptionReplacement;
            static struct State { public static int Trace; }
            struct First { public ~First() { State.Trace = State.Trace * 10 + 1; } }
            struct Second { public ~Second() { State.Trace = State.Trace * 10 + 2; } }
            void ThrowSecond() { throw Second(); }
            void Rethrow()
            {
                try { throw First(); }
                catch (readonly First& error) { throw; }
            }
            int Main()
            {
                try { Rethrow(); }
                catch (readonly First& error) { }
                try
                {
                    try { throw First(); }
                    finally { ThrowSecond(); }
                }
                catch (readonly Second& error) { }
                if (State.Trace == 112) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task NestedHandledExceptionKeepsOuterCatchObjectAlive()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("NestedCatchLifetime", "executable", """
            namespace NestedCatchLifetime;
            static struct State { public static int Trace; }
            struct First
            {
                public int Code;
                public First(int code) { Code = code; }
                public ~First() { State.Trace = State.Trace * 10 + 1; }
            }
            struct Second { public ~Second() { State.Trace = State.Trace * 10 + 2; } }
            int Main()
            {
                try { throw First(40); }
                catch (readonly First& outer)
                {
                    try { throw Second(); }
                    catch (readonly Second& inner) { }
                    if (outer.Code == 40) State.Trace = State.Trace * 10 + 3;
                }
                if (State.Trace == 231) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task NestedBareRethrowAbandonsCrossedOuterCatchRecord()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("NestedBareRethrow", "executable", """
            namespace NestedBareRethrow;
            static struct State { public static int Trace; }
            struct First { public ~First() { State.Trace = State.Trace * 10 + 1; } }
            struct Second { public ~Second() { State.Trace = State.Trace * 10 + 2; } }
            int Main()
            {
                try
                {
                    try { throw First(); }
                    catch (readonly First& first)
                    {
                        try { throw Second(); }
                        catch (readonly Second& second) { throw; }
                    }
                }
                catch (readonly Second& error) { }
                if (State.Trace == 12) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task UnmatchedCatchCleansFunctionScopeBeforeCallerHandlesException()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("UnhandledCatchCleanup", "executable", """
            namespace UnhandledCatchCleanup;
            static struct State { public static int Trace; }
            struct Guard { public ~Guard() { State.Trace++; } }
            struct First { }
            struct Second { }
            void Fail()
            {
                Guard guard = Guard();
                try { throw First(); }
                catch (readonly Second& wrong) { }
            }
            int Main()
            {
                try { Fail(); }
                catch (readonly First& error) { }
                if (State.Trace == 1) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task UnmatchedInnerCatchRunsItsFinallyExactlyOnce()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("UnhandledCatchFinally", "executable", """
            namespace UnhandledCatchFinally;
            static struct State { public static int Trace; }
            struct First { }
            struct Second { }
            int Main()
            {
                try
                {
                    try { throw First(); }
                    catch (readonly Second& wrong) { }
                    finally { State.Trace++; }
                }
                catch (readonly First& error) { }
                if (State.Trace == 1) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ExceptionCaughtInsideExceptionalFinallyPreservesOriginalException()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("CaughtFinallyException", "executable", """
            namespace CaughtFinallyException;
            static struct State { public static int Trace; }
            struct First { public ~First() { State.Trace = State.Trace * 10 + 1; } }
            struct Second { public ~Second() { State.Trace = State.Trace * 10 + 2; } }
            int Main()
            {
                try
                {
                    try { throw First(); }
                    finally
                    {
                        try { throw Second(); }
                        catch (readonly Second& handled) { }
                    }
                }
                catch (readonly First& original) { }
                if (State.Trace == 21) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task BareRethrowEscapingExceptionalFinallyReplacesOriginalException()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("FinallyBareRethrow", "executable", """
            namespace FinallyBareRethrow;
            static struct State { public static int Trace; }
            struct First { public ~First() { State.Trace = State.Trace * 10 + 1; } }
            struct Second { public ~Second() { State.Trace = State.Trace * 10 + 2; } }
            int Main()
            {
                try
                {
                    try { throw First(); }
                    finally
                    {
                        try { throw Second(); }
                        catch (readonly Second& replacement) { throw; }
                    }
                }
                catch (readonly Second& error) { }
                if (State.Trace == 12) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ExceptionEscapingCatchInsideFinallySuppressesExactOriginalRecord()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("FinallyCatchReplacement", "executable", """
            namespace FinallyCatchReplacement;
            static struct State { public static int Trace; }
            struct First { public ~First() { State.Trace = State.Trace * 10 + 1; } }
            struct Second { public ~Second() { State.Trace = State.Trace * 10 + 2; } }
            struct Third { public ~Third() { State.Trace = State.Trace * 10 + 3; } }
            int Main()
            {
                try
                {
                    try { throw First(); }
                    finally
                    {
                        try { throw Second(); }
                        catch (readonly Second& active) { throw Third(); }
                    }
                }
                catch (readonly Third& error) { }
                if (State.Trace == 123) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task CompareExchangeCleansExpectedWhenDesiredEvaluationThrows()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("CompareExchangeCleanup", "executable", """
            namespace CompareExchangeCleanup;
            static struct State { public static int Trace; }
            struct Item
            {
                public int Id;
                public Item(int id) { Id = id; }
                public ~Item() { State.Trace = State.Trace * 10 + Id; }
            }
            Item Fail() { throw 7; }
            int Main()
            {
                atomic<Item> value = Item(0);
                try { value : Item(1) --> Fail(); }
                catch (readonly int& error) { }
                if (State.Trace == 1) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ArrayReplacementThrowCleansNewHeapArrayAndRemainingOldElements()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ArrayReplacementCleanup", "executable", """
            namespace ArrayReplacementCleanup;
            static struct State
            {
                public static int NextId;
                public static int Trace;
                public static int Next() { State.NextId++; return State.NextId; }
            }
            struct Item
            {
                public int Id = State.Next();
                public ~Item()
                {
                    State.Trace = State.Trace * 10 + Id;
                    if (Id == 2) throw 7;
                }
            }
            int Main()
            {
                try
                {
                    Item[] values = Item[3];
                    values = new Item[3];
                }
                catch (readonly int& error) { }
                if (State.Trace == 326541) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task UnmatchedNestedTryEscapesCatchAndAbandonsItsOldException()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("NestedUnmatchedCatch", "executable", """
            namespace NestedUnmatchedCatch;
            static struct State { public static int Trace; }
            struct First { public ~First() { State.Trace = State.Trace * 10 + 1; } }
            struct Second { public ~Second() { State.Trace = State.Trace * 10 + 2; } }
            int Main()
            {
                try
                {
                    try { throw First(); }
                    catch (readonly First& outer)
                    {
                        try { throw Second(); }
                        catch (readonly First& wrong) { }
                    }
                }
                catch (readonly Second& error) { }
                if (State.Trace == 12) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ExceptionFromFinallyDestroysAbandonedReturnValue()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("FinallyReturnCleanup", "executable", """
            namespace FinallyReturnCleanup;
            struct Item
            {
                public static int Destructions;
                public ~Item() { Item.Destructions++; }
            }
            shared<Item> Make()
            {
                try { return new Item(); }
                finally { throw 7; }
            }
            int Main()
            {
                try { shared<Item> value = Make(); }
                catch (readonly int& error) { }
                if (Item.Destructions == 1) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ReturnFromExceptionalFinallySuppressesAndDestroysOriginalException()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("FinallySuppressesException", "executable", """
            namespace FinallySuppressesException;
            static struct State { public static int Destructions; }
            struct Error { public ~Error() { State.Destructions++; } }
            int Run()
            {
                try { throw Error(); }
                finally { return 42; }
            }
            int SuppressRethrow()
            {
                try { throw Error(); }
                catch (readonly Error& error)
                {
                    try { throw; }
                    finally { return 7; }
                }
            }
            int Main()
            {
                int result = Run();
                int rethrow = SuppressRethrow();
                if (result != 42) return 2;
                if (rethrow != 7) return 3;
                if (State.Destructions != 2) return 4;
                return 42;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.True(process.ExitCode == 42,
            $"exit={process.ExitCode}; stdout={process.Stdout}; stderr={process.Stderr}");
    }

    [Fact]
    public async Task ThrowingLaterArgumentKeepsEarlierOwnershipGuarded()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ArgumentExceptionCleanup", "executable", """
            namespace ArgumentExceptionCleanup;
            static struct State { public static int Destructions; }
            struct Item { public ~Item() { State.Destructions++; } }
            void Accept(shared<Item> first, int second) { }
            int ThrowLater() { throw 7; }
            int Main()
            {
                {
                    shared<Item> value = new Item();
                    try { Accept(move value, ThrowLater()); }
                    catch (readonly int& error) { }
                }
                try { Accept(new Item(), ThrowLater()); }
                catch (readonly int& error) { }
                if (State.Destructions == 2) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ReturnFromExceptionalFinallyCleansLocalsBeforeSuppressedException()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ExceptionalFinallyReturnOrder", "executable", """
            namespace ExceptionalFinallyReturnOrder;
            static struct State { public static int Trace; }
            struct Error { public ~Error() { State.Trace = State.Trace * 10 + 1; } }
            struct Local { public ~Local() { State.Trace = State.Trace * 10 + 2; } }
            void Run()
            {
                try { throw Error(); }
                finally
                {
                    Local local = Local();
                    return;
                }
            }
            int Main()
            {
                Run();
                if (State.Trace == 21) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ReceiverFieldReplacementKeepsNewValueLiveWhenOldDestructorThrows()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ReceiverReplacementException", "executable", """
            namespace ReceiverReplacementException;
            static struct State { public static int Trace; }
            struct Field
            {
                public int Id;
                public Field(int id) { Id = id; }
                public ~Field()
                {
                    State.Trace = State.Trace * 10 + Id;
                    if (Id == 1) throw 7;
                }
            }
            struct Container
            {
                public Field Value;
                public Container(int id) { Value = Field(id); }
                public void Replace() { Value = Field(2); }
            }
            int Main()
            {
                State.Trace = 0;
                {
                    Container value = Container(1);
                    try { value.Replace(); }
                    catch (readonly int& error)
                    {
                        if (error != 7 || value.Value.Id != 2) return 1;
                    }
                }
                if (State.Trace != 12) return 2;
                return 42;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task PartiallyInitializedArraysDestroyOnlyCompletedElements()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("PartialArrayCleanup", "executable", """
            namespace PartialArrayCleanup;
            static struct State
            {
                public static int Created;
                public static int Destroyed;
                public static int Next()
                {
                    State.Created++;
                    if (State.Created == 3) throw 7;
                    return State.Created;
                }
            }
            struct Item
            {
                public int Id = State.Next();
                public ~Item() { State.Destroyed++; }
            }
            int Main()
            {
                try { Item[] stack = Item[5]; }
                catch (readonly int& error) { }
                if (State.Created != 3 || State.Destroyed != 2) return 1;
                State.Created = 0;
                try { Item[] heap = new Item[5]; }
                catch (readonly int& error) { }
                if (State.Created == 3 && State.Destroyed == 4) return 42;
                return 2;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, string.Join(Environment.NewLine,
            new[] { result.Failure }.Concat(result.Diagnostics.Select(diagnostic => diagnostic.ToString()))));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task FailedThreadLocalInitializationCanBeCaughtAndRetried()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ThreadLocalException", "executable", """
            namespace ThreadLocalException;
            static struct State
            {
                public static int Attempts;
                public static threadlocal int Value = State.Initialize();
                public static int Initialize()
                {
                    State.Attempts++;
                    if (State.Attempts == 1) throw 7;
                    return 42;
                }
            }
            int Main()
            {
                try { int first = State.Value; }
                catch (readonly int& error) { }
                int second = State.Value;
                if (State.Attempts == 2 && second == 42) return 42;
                return 1;
            }
            """);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(result.Success, string.Join(Environment.NewLine,
            new[] { result.Failure }.Concat(result.Diagnostics.Select(diagnostic => diagnostic.ToString()))));

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Theory]
    [InlineData("static-library")]
    [InlineData("shared-library")]
    public async Task ProjectReferenceGenericImplementationAbiCompilesLinksAndRunsInConsumer(
        string projectType)
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("GenericLibrary", projectType, """
            namespace GenericLibrary;
            internal static struct GenericRuntime
            {
                internal static int Bonus = 1;
            }
            internal int Offset() { return 1; }
            public T Identity<T>(T value) { return move value; }
            public int ReadOffset<T>() { return Offset() + GenericRuntime.Bonus; }
            struct Box<T>
            {
                T value;
                int offset = 2;
                public Box(T item) { value = move item; }
                public T Get() { return move value; }
                public int Offset { get { return offset; } }
            }
            """);
        directory.WriteProject("GenericApp", "executable", """
            using GenericLibrary;
            namespace GenericApp;
            int Main()
            {
                Box<int> box = Box<int>(38);
                return box.Offset + Identity<int>(box.Get()) + ReadOffset<int>();
            }
            """, libraryProject);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        Assert.True(result.IsRunnable);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ProjectReferenceVirtualMethodsPropertiesAndIndexersDispatchInConsumer()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("VirtualLibrary", "static-library", """
            namespace VirtualLibrary;
            struct Base
            {
                public virtual int Read() { return 1; }
                public virtual int Value { get { return 2; } set { } }
                public virtual int this[int index] { get { return index + 2; } set { } }
            }
            """);
        directory.WriteProject("VirtualApp", "executable", """
            using VirtualLibrary;
            namespace VirtualApp;
            struct Derived : Base
            {
                public override int Read() { return 40; }
                public override int Value { get { return 1; } set { } }
                public override int this[int index] { get { return index; } set { } }
            }
            int Dispatch(Base& value)
            {
                value.Value = 0;
                value[1] = 0;
                return value.Read() + value.Value + value[1];
            }
            int Main()
            {
                Derived value = Derived();
                return Dispatch(value);
            }
            """, libraryProject);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(result.Success, result.Failure ?? string.Join(Environment.NewLine, result.Diagnostics));
        Assert.True(result.IsRunnable);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            result.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public void ForeignExecutableBuildEmitsObjectWithoutInvokingHostLinker()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ForeignApp", "executable", "namespace ForeignApp; int Main() { return 42; }");
        const string Triple = "aarch64-unknown-linux-gnu";

        var runner = new RejectingProcessRunner();
        XenonBuildResult result = new XenonBuildDriver(runner).Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot, TargetTriple: Triple));

        Assert.True(result.Success, result.Failure);
        Assert.Equal(BuildStage.Complete, result.Stage);
        Assert.True(result.NativeLinkSkipped);
        Assert.False(result.IsRunnable);
        Assert.True(File.Exists(result.ObjectPath));
        Assert.Null(result.ArtifactPath);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public void XenonLibraryBuildWritesPortableArtifactWithoutNativePipeline()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("Portable", "xenon-library",
            "namespace Portable; public int Value() { return 42; }");
        var runner = new RejectingProcessRunner();

        XenonBuildResult first = new XenonBuildDriver(runner).Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot,
            TargetTriple: "aarch64-unknown-linux-gnu"));
        byte[] firstBytes = File.ReadAllBytes(first.ArtifactPath!);
        XenonBuildResult second = new XenonBuildDriver(runner).Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot,
            TargetTriple: "x86_64-pc-windows-msvc"));

        Assert.True(first.Success, first.Failure);
        Assert.True(second.Success, second.Failure);
        Assert.Equal(firstBytes, File.ReadAllBytes(second.ArtifactPath!));
        Assert.EndsWith(Path.Combine("build", "debug", "Portable.xelib"), first.ArtifactPath,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(first.ObjectPath);
        Assert.Null(first.LlvmIrPath);
        Assert.Equal(0, runner.CallCount);
        Assert.Equal("Portable", XelibMetadataReader.ReadFile(first.ArtifactPath!).Manifest.Name);
    }

    [Fact]
    public void InvalidXelibIsReportedAndNeverFallsBackToTheNativeLinker()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("InvalidXelibApp", "executable",
            "namespace InvalidXelibApp; int Main() { return 42; }");
        File.WriteAllBytes(Path.Combine(directory.Root, "broken.xelib"), "not a xelib"u8.ToArray());
        File.AppendAllText(directory.ProjectFile, """

            [libraries]
            libraries = ["broken.xelib"]
            """);
        var runner = new RejectingProcessRunner();

        XenonBuildResult result = new XenonBuildDriver(runner).Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.False(result.Success);
        Assert.Equal(BuildStage.Compilation, result.Stage);
        Assert.Contains("XELIB", result.Failure, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public void UnreferencedEnvironmentXelibDoesNotChangeNativeBuildOutput()
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("EnvironmentIsolation", "executable",
            "namespace EnvironmentIsolation; int Main() { return 42; }");
        var driver = new XenonBuildDriver();
        XenonBuildResult first = driver.Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(first.Success, first.Failure ?? string.Join(Environment.NewLine, first.Diagnostics));
        byte[] firstHash = System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(first.ArtifactPath!));

        File.WriteAllBytes(Path.Combine(directory.Root, "Unrelated.xelib"), "not a xelib"u8.ToArray());
        XenonBuildResult second = driver.Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(second.Success, second.Failure ?? string.Join(Environment.NewLine, second.Diagnostics));
        byte[] secondHash = System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(second.ArtifactPath!));

        Assert.Equal(firstHash, secondHash);
    }

    [Fact]
    public async Task ExplicitXelibBuildsAndRunsWithoutLibrarySources()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("PortableLibrary", "xenon-library", """
            namespace PortableLibrary;
            public int Add(int left, int right) { return left + right; }
            """);
        string libraryProjectPath = Path.GetFullPath(Path.Combine(directory.Root, libraryProject));
        XenonBuildResult library = new XenonBuildDriver().Build(new XenonBuildRequest(
            libraryProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(library.Success, string.Join(Environment.NewLine, library.Diagnostics) +
            Environment.NewLine + library.Failure);

        Directory.Delete(Path.Combine(Path.GetDirectoryName(libraryProjectPath)!, "src"), recursive: true);
        directory.WriteProject("PortableApp", "executable", """
            using PortableLibrary;
            namespace PortableApp;
            int Main() { return Add(40, 2); }
            """);
        string relativeLibrary = Path.GetRelativePath(directory.Root, library.ArtifactPath!).Replace('\\', '/');
        File.AppendAllText(directory.ProjectFile, $"""

            [libraries]
            libraries = ["{relativeLibrary}"]
            """);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibGenericFunctionSpecializesWithoutLibrarySources()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("GenericXelib", "xenon-library", """
            namespace GenericXelib;
            public T Identity<T>(T value) { return move value; }
            """);
        string libraryProjectPath = Path.GetFullPath(Path.Combine(directory.Root, libraryProject));
        XenonBuildResult library = new XenonBuildDriver().Build(new XenonBuildRequest(
            libraryProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(library.Success, library.Failure ?? string.Join(Environment.NewLine, library.Diagnostics));

        Directory.Delete(Path.Combine(Path.GetDirectoryName(libraryProjectPath)!, "src"), recursive: true);
        directory.WriteProject("GenericXelibApp", "executable", """
            using GenericXelib;
            namespace GenericXelibApp;
            int Main() { return Identity<int>(42); }
            """);
        string relativeLibrary = Path.GetRelativePath(directory.Root, library.ArtifactPath!).Replace('\\', '/');
        File.AppendAllText(directory.ProjectFile, $"""

            [libraries]
            libraries = ["{relativeLibrary}"]
            """);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibGenericStructSpecializesWithoutLibrarySources()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("GenericStructXelib", "xenon-library", """
            namespace GenericStructXelib;
            struct Box<T>
            {
                const int ConstantOffset = 2;
                T value;
                int offset = ConstantOffset;
                public static int State = 7;
                public static int Adjustment() { return 0; }
                public Box(T item) { value = move item; }
                public T Get() { return move value; }
                public int Offset { get { return offset; } }
            }
            """);
        string libraryProjectPath = Path.GetFullPath(Path.Combine(directory.Root, libraryProject));
        XenonBuildResult library = new XenonBuildDriver().Build(new XenonBuildRequest(
            libraryProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(library.Success, library.Failure ?? string.Join(Environment.NewLine, library.Diagnostics));

        Directory.Delete(Path.Combine(Path.GetDirectoryName(libraryProjectPath)!, "src"), recursive: true);
        directory.WriteProject("GenericStructXelibApp", "executable", """
            using GenericStructXelib;
            namespace GenericStructXelibApp;
            int Main()
            {
                Box<int> box = Box<int>(40);
                return box.Offset + box.Get() + GenericStructXelib.Box<int>.State - 7 +
                    GenericStructXelib.Box<int>.Adjustment();
            }
            """);
        string relativeLibrary = Path.GetRelativePath(directory.Root, library.ArtifactPath!).Replace('\\', '/');
        File.AppendAllText(directory.ProjectFile, $"""

            [libraries]
            libraries = ["{relativeLibrary}"]
            """);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        StructTypeSymbol boxType = app.Compilation!.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "GenericStructXelib").Structs
            .Single(type => type.GenericDefinition?.Name == "Box");
        Assert.Equal(2, boxType.Constants.Single(constant => constant.Name == "ConstantOffset").Value);
        Assert.Equal(7, boxType.StaticFields.Single(field => field.Name == "State").ConstantValue);
        Assert.NotNull(boxType.InstanceInitializer);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibGenericTargetLayoutConstantAndThreadLocalInitializerRemainPortable()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("GenericPortableState", "xenon-library", """
            namespace GenericPortableState;
            struct State<T>
            {
                const nuint Width = sizeof(T);
                const nuint Alignment = alignof(T);
                public static threadlocal int Offset = 30;
                public int GetWidth() { return cast<int>(Width); }
                public int Read() { return GetWidth() + State.Offset; }
            }
            """);
        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "GenericPortableStateApp", """
            using GenericPortableState;
            namespace GenericPortableStateApp;
            int Main()
            {
                GenericPortableState.State<int> value = State<int>();
                return cast<int>(GenericPortableState.State<int>.Width) +
                    cast<int>(GenericPortableState.State<int>.Alignment) + value.Read();
            }
            """);
        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibDestructorReachabilityCoversAssignmentAndNonGenericTls()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("DestructorClosureXelib", "xenon-library", """
            namespace DestructorClosureXelib;
            struct Counters { public static int Assignment; public static int Tls; }
            void NoteTlsCleanup() { Counters.Tls += 1; }
            struct AssignmentResource
            {
                public int Value = 1;
                public ~AssignmentResource() { Counters.Assignment += Value; }
            }
            struct TlsResource
            {
                public int Value = 41;
                public ~TlsResource() { NoteTlsCleanup(); }
            }
            struct State
            {
                public static threadlocal TlsResource Current = TlsResource();
            }
            public int RunAssignment()
            {
                Counters.Assignment = 0;
                { AssignmentResource value; value = AssignmentResource(); }
                return Counters.Assignment;
            }
            public int ReadTls() { return State.Current.Value; }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "DestructorClosureXelibApp", """
            using DestructorClosureXelib;
            namespace DestructorClosureXelibApp;
            int Main() { return RunAssignment() + ReadTls(); }
            """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibStackAtomicSharedArraySelectsAndRunsElementCleanup()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("StackArrayClosureXelib", "xenon-library", """
            namespace StackArrayClosureXelib;
            struct Counters { public static int Destroyed; }
            struct Resource
            {
                public int Value;
                public Resource(int value) { Value = value; }
                public ~Resource() { Counters.Destroyed++; }
            }
            public int Run(int count)
            {
                Counters.Destroyed = 0;
                {
                    atomic<shared<Resource>>[] values = atomic<shared<Resource>>[count];
                    for (int index = 0; index < count; index++)
                        values[index] = new Resource(index);
                }
                return 40 + Counters.Destroyed;
            }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "StackArrayClosureXelibApp", """
            using StackArrayClosureXelib;
            namespace StackArrayClosureXelibApp;
            int Main() { return Run(2); }
            """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task XelibDependenciesResolveByContentIdentityWithoutSources()
    {
        using var directory = new TemporaryProject();
        string contractProject = directory.WriteDependencyProject("ContractXelib", "xenon-library", """
            namespace ContractXelib;
            public int BaseValue() { return 40; }
            """);
        string engineProject = directory.WriteDependencyProject("EngineXelib", "xenon-library", """
            using ContractXelib;
            namespace EngineXelib;
            public int Value() { return BaseValue() + 2; }
            """);
        string engineProjectPath = Path.GetFullPath(Path.Combine(directory.Root, engineProject));
        string engineDirectory = Path.GetDirectoryName(engineProjectPath)!;
        string contractFromEngine = Path.GetRelativePath(engineDirectory,
            Path.GetFullPath(Path.Combine(directory.Root, contractProject))).Replace('\\', '/');
        File.AppendAllText(engineProjectPath, $"""

            [references]
            projects = ["{contractFromEngine}"]
            """);

        XenonBuildResult engine = new XenonBuildDriver().Build(new XenonBuildRequest(
            engineProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(engine.Success, engine.Failure ?? string.Join(Environment.NewLine, engine.Diagnostics));
        string contractArtifact = XenonBuildPaths.GetXenonLibraryPath(
            directory.OutputRoot, "ContractXelib", "debug");
        Assert.True(File.Exists(contractArtifact));

        Directory.Delete(Path.Combine(directory.Root, "ContractXelib", "src"), recursive: true);
        Directory.Delete(Path.Combine(directory.Root, "EngineXelib", "src"), recursive: true);
        directory.WriteProject("DependencyXelibApp", "executable", """
            using EngineXelib;
            namespace DependencyXelibApp;
            int Main() { return Value(); }
            """);
        string relativeContract = Path.GetRelativePath(directory.Root, contractArtifact).Replace('\\', '/');
        string relativeEngine = Path.GetRelativePath(directory.Root, engine.ArtifactPath!).Replace('\\', '/');
        File.AppendAllText(directory.ProjectFile, $"""

            [libraries]
            libraries = ["{relativeContract}", "{relativeEngine}"]
            """);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibTemplateConstraintAndGenericBodyWorkWithConsumerType()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("TemplateXelib", "xenon-library", """
            namespace TemplateXelib;
            template Runnable { int Run(); }
            public int Execute<T>(T value) where T : Runnable { return value.Run(); }
            """);
        string libraryProjectPath = Path.GetFullPath(Path.Combine(directory.Root, libraryProject));
        XenonBuildResult library = new XenonBuildDriver().Build(new XenonBuildRequest(
            libraryProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(library.Success, library.Failure ?? string.Join(Environment.NewLine, library.Diagnostics));

        Directory.Delete(Path.Combine(Path.GetDirectoryName(libraryProjectPath)!, "src"), recursive: true);
        directory.WriteProject("TemplateXelibApp", "executable", """
            using TemplateXelib;
            namespace TemplateXelibApp;
            struct Worker { public int Run() { return 42; } }
            int Main() { Worker worker = Worker(); return Execute<Worker>(worker); }
            """);
        string relativeLibrary = Path.GetRelativePath(directory.Root, library.ArtifactPath!).Replace('\\', '/');
        File.AppendAllText(directory.ProjectFile, $"""

            [libraries]
            libraries = ["{relativeLibrary}"]
            """);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibVirtualPropertiesAndIndexersDispatchInConsumer()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("VirtualXelib", "xenon-library", """
            namespace VirtualXelib;
            struct Base
            {
                public virtual int Read() { return 1; }
                public virtual int Value { get { return 2; } set { } }
                public virtual int this[int index] { get { return index + 2; } set { } }
            }
            """);
        string libraryProjectPath = Path.GetFullPath(Path.Combine(directory.Root, libraryProject));
        XenonBuildResult library = new XenonBuildDriver().Build(new XenonBuildRequest(
            libraryProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(library.Success, library.Failure ?? string.Join(Environment.NewLine, library.Diagnostics));
        Directory.Delete(Path.Combine(Path.GetDirectoryName(libraryProjectPath)!, "src"), recursive: true);

        directory.WriteProject("VirtualXelibApp", "executable", """
            using VirtualXelib;
            namespace VirtualXelibApp;
            struct Derived : Base
            {
                public override int Read() { return 40; }
                public override int Value { get { return 1; } set { } }
                public override int this[int index] { get { return index; } set { } }
            }
            int Dispatch(Base& value)
            {
                value.Value = 0;
                value[1] = 0;
                return value.Read() + value.Value + value[1];
            }
            int Main() { Derived value = Derived(); return Dispatch(value); }
            """);
        string relativeLibrary = Path.GetRelativePath(directory.Root, library.ArtifactPath!).Replace('\\', '/');
        File.AppendAllText(directory.ProjectFile, $"""

            [libraries]
            libraries = ["{relativeLibrary}"]
            """);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibVirtualDestructorDispatchesToSourceOverrideAndRunsBaseChain()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("VirtualDestructorXelib",
            "xenon-library", """
            namespace VirtualDestructorXelib;
            struct Base
            {
                int* trace;
                public Base(int* trace) { this.trace = trace; }
                public virtual ~Base() { *trace = *trace * 10 + 1; }
            }
            public void Destroy(Base* value) { free(value); }
            """);
        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "VirtualDestructorXelibApp", """
            using VirtualDestructorXelib;
            namespace VirtualDestructorXelibApp;
            struct Derived : Base
            {
                int* trace;
                public Derived(int* trace) : base(trace) { this.trace = trace; }
                public override ~Derived() { *trace = *trace * 10 + 2; }
            }
            int Main()
            {
                int trace = 0;
                Derived* value = new Derived(&trace);
                Destroy(value);
                if (trace != 21) return 1;
                return 42;
            }
            """);
        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task XelibInterfacePropertiesAndIndexersDispatchToConsumerType()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("InterfaceAccessorXelib",
            "xenon-library", """
            namespace InterfaceAccessorXelib;
            interface IValue
            {
                int Value { get; set; }
                int this[int index] { get; set; }
            }
            public int Update(IValue& value)
            {
                value.Value = 20;
                value[2] = 22;
                return value.Value + value[2];
            }
            """);
        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "InterfaceAccessorXelibApp", """
            using InterfaceAccessorXelib;
            namespace InterfaceAccessorXelibApp;
            struct Value : IValue
            {
                int stored;
                public int Value { get { return stored; } set { stored = value; } }
                public int this[int index]
                {
                    get { return stored + index; }
                    set { stored = value - index; }
                }
            }
            int Main() { Value value = Value(); return Update(value); }
            """);
        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task XelibPreservesOwnershipDestructionAndTargetLayoutOperations()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("OwnershipXelib", "xenon-library", """
            namespace OwnershipXelib;
            struct State { public static int Trace; }
            struct Resource
            {
                public int Value;
                public Resource(int value) { Value = value; }
                public ~Resource() { State.Trace = State.Trace * 10 + Value; }
            }
            public void Reset() { State.Trace = 0; }
            public int Trace() { return State.Trace; }
            public unique<Resource> MakeUnique(int value) { return new Resource(value); }
            public shared<Resource> MakeShared(int value) { return new Resource(value); }
            public int Read(unique<Resource>& value) { return value->Value; }
            public int ReadShared(shared<Resource> value) { return value->Value; }
            public int ReadWeak(weak<Resource> value)
            {
                shared<Resource> locked = lock value;
                return locked->Value;
            }
            public int SharedLifetime()
            {
                Reset();
                { shared<Resource> value = MakeShared(3); }
                return Trace();
            }
            public bool PortableLayout()
            {
                return sizeof(unique<Resource>) == sizeof(nint)
                    && alignof(shared<Resource>) == alignof(nint);
            }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject, "OwnershipXelibApp", """
            using OwnershipXelib;
            namespace OwnershipXelibApp;
            int Main()
            {
                if (!PortableLayout()) return 1;
                if (SharedLifetime() != 3) return 7;
                Reset();
                {
                    unique<Resource> first = MakeUnique(4);
                    if (Read(first) != 4) return 2;
                    unique<Resource> moved = move first;
                    if (Read(moved) != 4) return 3;
                }
                if (Trace() != 4) return 4;
                Reset();
                {
                    shared<Resource> strong = MakeShared(2);
                    weak<Resource> observer = strong;
                    if (ReadShared(strong) + ReadWeak(observer) != 4) return 5;
                }
                if (Trace() != 2) return 6;
                return 42;
            }
            """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task XelibPreservesFunctionPointersArraysReferencesAndIndirectCalls()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("CallableXelib", "xenon-library", """
            namespace CallableXelib;
            struct Api { public function int(int)* Transform; }
            int Twice(int value) { return value * 2; }
            public function int(int)* GetCallback() { return &Twice; }
            public Api GetApi() { return Api { &Twice }; }
            public int Invoke(function int(int)* callback, int value) { return callback(value); }
            public int ReadFirst(readonly int* pointer) { return *pointer; }
            public int Sum(int[] values) { return values[0] + values[1]; }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject, "CallableXelibApp", """
            using CallableXelib;
            namespace CallableXelibApp;
            int Main()
            {
                function int(int)* callback = GetCallback();
                Api api = GetApi();
                int value = 10;
                int[] values = new int[2];
                values[0] = Invoke(callback, value);
                values[1] = api.Transform(ReadFirst(&value));
                int result = Sum(values) + 2;
                free(values);
                return result;
            }
            """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task XelibPreservesAtomicStorageAndPinSemantics()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("LifetimeXelib", "xenon-library", """
            namespace LifetimeXelib;
            struct Resource
            {
                public int Value;
                public Resource(int value) { Value = value; }
            }
            public void Increment(atomic<int>& value) { value++; }
            public int UseStorage(int value)
            {
                storage<Resource> slot = Resource(value);
                int result = slot.Value;
                destruct(slot);
                slot = Resource(result + 1);
                return slot.Value;
            }
            public int UsePin(int value)
            {
                pin<Resource> fixedValue = Resource(value);
                return fixedValue.Value;
            }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject, "LifetimeXelibApp", """
            using LifetimeXelib;
            namespace LifetimeXelibApp;
            int Main()
            {
                atomic<int> value = 39;
                Increment(value);
                return value + UseStorage(0) + UsePin(1);
            }
            """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task XelibSpecializesAllStructuralGenericOperationsWithoutSources()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("StructuralXelib", "xenon-library", """
            namespace StructuralXelib;
            template ValueContract
            {
                ValueContract(int value);
                int Value { get; set; }
                int this[int index] { get; set; }
            }
            public T Identity<T>(T value) { return move value; }
            public T Relay<T>(T value) { return Identity<T>(move value); }
            public int Mutate<T>(T value) where T : ValueContract
            {
                value.Value = 20;
                value[2] = 22;
                return value.Value + value[2];
            }
            public int Compound<T>(T value) where T : ValueContract
            {
                value.Value = 18;
                value.Value += 2;
                value[2] = 20;
                value[2] += 2;
                return value.Value + value[2];
            }
            public T Construct<T>(int value) where T : ValueContract { return T(value); }
            public T* Allocate<T>(int value) where T : ValueContract { return new T(value); }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject, "StructuralXelibApp", """
            using StructuralXelib;
            namespace StructuralXelibApp;
            struct Value
            {
                public int stored;
                public Value(int value) { stored = value; }
                public int Value { get { return stored; } set { stored = value; } }
                public int this[int index]
                {
                    get { return stored + index; }
                    set { stored = value - index; }
                }
            }
            int Main()
            {
                Value first = Construct<Value>(1);
                if (Mutate<Value>(first) != 42) return 1;
                if (Compound<Value>(first) != 42) return 2;
                Value relayed = Relay<Value>(first);
                Value* allocated = Allocate<Value>(42);
                int result = relayed.Value + allocated->Value - 1;
                free(allocated);
                return result;
            }
            """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task XelibExternAndCCompatibleStructResolveInFinalConsumer()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("CAbiXelib", "xenon-library", """
            namespace CAbiXelib;
            struct Pair { public int Left; public int Right; }
            extern int NativeBridge_Sum(Pair value);
            public int CallNative(int left, int right)
            {
                return NativeBridge_Sum(Pair { left, right });
            }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject, "CAbiXelibApp", """
            using CAbiXelib;
            namespace NativeBridge;
            export int Sum(Pair value) { return value.Left + value.Right; }
            int Main() { return CallNative(40, 2); }
            """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task XelibFinalAcceptanceScenarioRunsWithoutAnyLibrarySources()
    {
        using var directory = new TemporaryProject();
        string contractProject = directory.WriteDependencyProject("EpicContract", "xenon-library", """
            namespace EpicContract;
            template EntityContract { int Run(); }
            interface IEntity { int InterfaceValue(); }
            """);
        string engineProject = directory.WriteDependencyProject("EpicEngine", "xenon-library", """
            using EpicContract;
            namespace EpicEngine;
            struct EngineBase { public virtual int Bonus() { return 1; } }
            struct Container<T> where T : EntityContract
            {
                unique<T> value;
                public Container(unique<T> input) { value = move input; }
                public int Execute() { return value->Run() + 1; }
            }
            public int ReadEntity(IEntity& value) { return value.InterfaceValue(); }
            public int ReadBase(EngineBase& value) { return value.Bonus(); }
            """);
        string engineProjectPath = Path.GetFullPath(Path.Combine(directory.Root, engineProject));
        string engineDirectory = Path.GetDirectoryName(engineProjectPath)!;
        string contractFromEngine = Path.GetRelativePath(engineDirectory,
            Path.GetFullPath(Path.Combine(directory.Root, contractProject))).Replace('\\', '/');
        File.AppendAllText(engineProjectPath, $"""

            [references]
            projects = ["{contractFromEngine}"]
            """);

        XenonBuildResult engine = new XenonBuildDriver().Build(new XenonBuildRequest(
            engineProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(engine.Success, string.Join(Environment.NewLine, engine.Diagnostics) + Environment.NewLine + engine.Failure);
        string contractArtifact = XenonBuildPaths.GetXenonLibraryPath(
            directory.OutputRoot, "EpicContract", "debug");
        Directory.Delete(Path.Combine(directory.Root, "EpicContract", "src"), recursive: true);
        Directory.Delete(Path.Combine(directory.Root, "EpicEngine", "src"), recursive: true);

        directory.WriteProject("EpicApp", "executable", """
            using EpicContract;
            using EpicEngine;
            namespace EpicApp;
            struct State { public static int Destructed; }
            struct Player : IEntity
            {
                int score;
                public Player(int score) { this.score = score; }
                public int Run() { return score; }
                public int InterfaceValue() { return 1; }
                public ~Player() { State.Destructed++; }
            }
            struct Derived : EngineBase { public override int Bonus() { return 0; } }
            int Main()
            {
                State.Destructed = 0;
                int result = 0;
                {
                    unique<Player> player = new Player(40);
                    Container<Player> container = Container<Player>(move player);
                    result = container.Execute();
                }
                if (State.Destructed != 1) return 2;
                Player interfaceValue = Player(0);
                Derived derived = Derived();
                return result + ReadEntity(interfaceValue) + ReadBase(derived);
            }
            """);
        string relativeContract = Path.GetRelativePath(directory.Root, contractArtifact).Replace('\\', '/');
        string relativeEngine = Path.GetRelativePath(directory.Root, engine.ArtifactPath!).Replace('\\', '/');
        File.AppendAllText(directory.ProjectFile, $"""

            [libraries]
            libraries = ["{relativeContract}", "{relativeEngine}"]
            """);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        File.Delete(contractArtifact);
        File.Delete(engine.ArtifactPath!);
        Assert.False(File.Exists(contractArtifact));
        Assert.False(File.Exists(engine.ArtifactPath));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
        Assert.True(File.Exists(app.ArtifactPath));
    }

    [Fact]
    public async Task XenonLibraryProjectReferencesBringTransitiveDiamondClosureExactlyOnce()
    {
        using var directory = new TemporaryProject();
        string contractProject = directory.WriteDependencyProject("GraphContract", "xenon-library", """
            namespace GraphContract;
            public int BaseValue() { return 20; }
            """);
        string leftProject = directory.WriteDependencyProject("GraphLeft", "xenon-library", """
            using GraphContract;
            namespace GraphLeft;
            public int LeftValue() { return BaseValue(); }
            """);
        string rightProject = directory.WriteDependencyProject("GraphRight", "xenon-library", """
            using GraphContract;
            namespace GraphRight;
            public int RightValue() { return BaseValue() + 2; }
            """);
        AddProjectReference(leftProject, contractProject);
        AddProjectReference(rightProject, contractProject);
        directory.WriteProject("GraphApp", "executable", """
            using GraphLeft;
            using GraphRight;
            namespace GraphApp;
            int Main() { return LeftValue() + RightValue(); }
            """, leftProject, rightProject);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        LibraryCompilationReference[] references = app.Compilation!.References
            .OfType<LibraryCompilationReference>().ToArray();
        Assert.Equal(3, references.Length);
        Assert.Single(references, reference => reference.LibraryIdentity.Name == "GraphContract");
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);

        void AddProjectReference(string ownerProject, string dependencyProject)
        {
            string ownerPath = Path.GetFullPath(Path.Combine(directory.Root, ownerProject));
            string dependencyPath = Path.GetFullPath(Path.Combine(directory.Root, dependencyProject));
            string relative = Path.GetRelativePath(Path.GetDirectoryName(ownerPath)!, dependencyPath)
                .Replace('\\', '/');
            File.AppendAllText(ownerPath, $"""

                [references]
                projects = ["{relative}"]
                """);
        }
    }

    [Fact]
    public async Task AppProjectReferenceReceivesTransitiveXenonLibraryDependency()
    {
        using var directory = new TemporaryProject();
        string contractProject = directory.WriteDependencyProject("ChainContract", "xenon-library", """
            namespace ChainContract;
            public int BaseValue() { return 40; }
            """);
        string engineProject = directory.WriteDependencyProject("ChainEngine", "xenon-library", """
            using ChainContract;
            namespace ChainEngine;
            public int Value() { return BaseValue() + 2; }
            """);
        string enginePath = Path.GetFullPath(Path.Combine(directory.Root, engineProject));
        string contractPath = Path.GetFullPath(Path.Combine(directory.Root, contractProject));
        string relativeContract = Path.GetRelativePath(Path.GetDirectoryName(enginePath)!, contractPath)
            .Replace('\\', '/');
        File.AppendAllText(enginePath, $"""

            [references]
            projects = ["{relativeContract}"]
            """);
        directory.WriteProject("ChainApp", "executable", """
            using ChainEngine;
            namespace ChainApp;
            int Main() { return Value(); }
            """, engineProject);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        Assert.Equal(["ChainEngine", "ChainContract"], app.Compilation!.References
            .OfType<LibraryCompilationReference>().Select(reference => reference.LibraryIdentity.Name).ToArray());
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task OrdinaryXelibFieldInitializersRunForDirectHeapAndStorageConstruction()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("InitializerXelib", "xenon-library", """
            namespace InitializerXelib;
            int Seed() { return 42; }
            struct Value { public int Number = Seed(); }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "InitializerApp", """
                using InitializerXelib;
                namespace InitializerApp;
                int Main()
                {
                    Value direct = Value();
                    Value* heap = new Value();
                    storage<Value> stored = Value();
                    int result = direct.Number + heap->Number + stored.Number - 84;
                    free(heap);
                    return result;
                }
                """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
    }

    [Fact]
    public async Task StdShapedAccessibilityAndTypeModifiersWorkWithoutLibrarySources()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("StdShape", "xenon-library", """
            namespace Xenon.IO;

            internal struct ConsoleWriterState
            {
                public static int Seed = 40;
            }

            internal int Offset() { return 1; }

            public enum ConsoleColor
            {
                Black,
                White,
                public static int Writes;
                public static threadlocal int ThreadWrites = 1;
                public static ConsoleColor Default;
            }

            public static struct Console
            {
                public static ConsoleWriter Out;
                public static ConsoleWriter Error;
                public static ConsoleReader In;
                public static int Read()
                {
                    ConsoleColor.Default = ConsoleColor.White;
                    ConsoleColor.Writes = ConsoleWriterState.Seed - 2;
                    ConsoleColor.ThreadWrites += 1;
                    if (ConsoleColor.Default != ConsoleColor.White) return 0;
                    return ConsoleColor.Writes + ConsoleColor.ThreadWrites;
                }
            }

            public sealed struct ConsoleWriter { public int Value; }
            public struct ConsoleReader { public int Value; }
            public struct ConsoleReaderBase
            {
                protected int Value;
                protected ConsoleReaderBase(int value) { Value = value; }
            }
            public readonly struct ConsoleKey
            {
                int Code;
                public ConsoleKey(int code) { Code = code; }
                public int Read() { return Code; }
            }

            public int GenericOffset<T>() { return Offset(); }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "StdShapeApp", """
                using Xenon.IO;
                namespace StdShapeApp;
                struct DerivedReader : ConsoleReaderBase
                {
                    public DerivedReader(int value) : base(value) {}
                    public int Read() { return Value; }
                }
                int Main()
                {
                    ConsoleKey key = ConsoleKey(0);
                    DerivedReader reader = DerivedReader(1);
                    return Console.Read() + GenericOffset<int>() + reader.Read() + key.Read();
                }
                """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) + Environment.NewLine + app.Failure);
        Compilation hiddenConsumer = Compilation.Create(new CompilationOptions(), app.Compilation!.References,
            SourceText.From("""
                using Xenon.IO;
                namespace HiddenConsumer;
                int Run() { ConsoleWriterState state; return Offset(); }
                """, "hidden-consumer.xe"));
        Assert.True(hiddenConsumer.HasErrors);
    }

    [Fact]
    public void InconsistentAccessibilityFailsTheDependencyBeforeXelibEmission()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("InvalidApi", "xenon-library", """
            namespace InvalidApi;
            internal struct Hidden {}
            public Hidden Leak() { return Hidden(); }
            """);
        directory.WriteProject("InvalidApiApp", "executable",
            "namespace InvalidApiApp; int Main() { return 42; }", libraryProject);

        XenonBuildResult result = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.False(result.Success);
        Assert.Equal(BuildStage.Compilation, result.Stage);
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.InconsistentAccessibility);
        Assert.Null(result.ArtifactPath);
    }

    [Fact]
    public async Task XelibProtectedInternalOverrideReusesVirtualSlotAndRunsWithoutSources()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("ProtectedOverride", "xenon-library", """
            namespace ProtectedOverride;
            public struct Base
            {
                protected internal virtual int Read() { return 1; }
            }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "ProtectedOverrideApp", """
                using ProtectedOverride;
                namespace ProtectedOverrideApp;
                public struct Derived : Base
                {
                    protected override int Read() { return 42; }
                    public int Invoke() { return Read(); }
                }
                int Main()
                {
                    Derived value = Derived();
                    return value.Invoke();
                }
                """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
        StructTypeSymbol derived = app.Compilation!.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "ProtectedOverrideApp").Structs.Single();
        FunctionSymbol overriding = derived.Methods.Single(method => method.Name == "Read");
        FunctionSymbol inherited = derived.BaseType!.VirtualMethods.Single(method => method.Name == "Read");
        Assert.Equal(inherited.VTableSlot, overriding.VTableSlot);
    }

    [Theory]
    [InlineData("static-library")]
    [InlineData("shared-library")]
    public async Task ProjectReferencePreservesInaccessibleInternalVirtualSlotForBaseDispatch(
        string projectType)
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("InternalVirtual", projectType, """
            namespace InternalVirtual;
            public struct Base
            {
                internal virtual int Read() { return 1; }
            }
            public int CallInternal(Base& value) { return value.Read(); }
            """);
        directory.WriteProject("InternalVirtualApp", "executable", """
            using InternalVirtual;
            namespace InternalVirtualApp;
            public struct Derived : Base
            {
                public virtual int Read() { return 41; }
            }
            int Main()
            {
                Derived value = Derived();
                return CallInternal(value) + value.Read();
            }
            """, libraryProject);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task XelibInaccessibleInternalVirtualKeepsBaseDispatchAndGetsIndependentDerivedSlot()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("InternalVirtualXelib", "xenon-library", """
            namespace InternalVirtualXelib;
            public struct Base
            {
                internal virtual int Read() { return 1; }
            }
            public int CallInternal(Base& value) { return value.Read(); }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "InternalVirtualXelibApp", """
                using InternalVirtualXelib;
                namespace InternalVirtualXelibApp;
                public struct Derived : Base
                {
                    public virtual int Read() { return 41; }
                }
                int Main()
                {
                    Derived value = Derived();
                    return CallInternal(value) + value.Read();
                }
                """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
        StructTypeSymbol derived = app.Compilation!.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "InternalVirtualXelibApp").Structs.Single();
        FunctionSymbol inherited = derived.BaseType!.Methods.Single(method => method.Name == "Read");
        FunctionSymbol independent = derived.Methods.Single(method => method.Name == "Read");
        Assert.NotEqual(inherited.VTableSlot, independent.VTableSlot);
        Assert.Same(inherited, derived.VirtualMethods[inherited.VTableSlot!.Value]);
        Assert.Same(independent, derived.VirtualMethods[independent.VTableSlot!.Value]);
    }

    [Theory]
    [InlineData("static-library")]
    [InlineData("shared-library")]
    public async Task XelibVirtualAbiRootSurvivesIntermediateNativeLibrary(string engineType)
    {
        using var directory = new TemporaryProject();
        string baseProject = directory.WriteDependencyProject("VirtualAbiBase", "xenon-library", """
            namespace VirtualAbiBase;
            public struct Base
            {
                internal virtual int Read() { return 40; }
                internal virtual int Value { get { return 1; } set {} }
                internal virtual int this[int index] { get { return index; } set {} }
                public virtual ~Base() {}
            }
            public int CallInternal(Base& value) { return value.Read(); }
            """);
        string baseProjectPath = Path.GetFullPath(Path.Combine(directory.Root, baseProject));
        XenonBuildResult baseLibrary = new XenonBuildDriver().Build(new XenonBuildRequest(
            baseProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(baseLibrary.Success,
            baseLibrary.Failure ?? string.Join(Environment.NewLine, baseLibrary.Diagnostics));
        Directory.Delete(Path.Combine(Path.GetDirectoryName(baseProjectPath)!, "src"), recursive: true);

        string engineProject = directory.WriteDependencyProject("VirtualAbiEngine", engineType, """
            using VirtualAbiBase;
            namespace VirtualAbiEngine;
            public struct Derived : Base {}
            public int Dispatch(Derived& value) { return CallInternal(value) + 1; }
            """);
        string engineProjectPath = Path.GetFullPath(Path.Combine(directory.Root, engineProject));
        string engineDirectory = Path.GetDirectoryName(engineProjectPath)!;
        string relativeLibrary = Path.GetRelativePath(engineDirectory, baseLibrary.ArtifactPath!)
            .Replace('\\', '/');
        File.AppendAllText(engineProjectPath, $"""

            [libraries]
            libraries = ["{relativeLibrary}"]
            """);

        directory.WriteProject("VirtualAbiApp", "executable", """
            using VirtualAbiEngine;
            namespace VirtualAbiApp;
            public struct FinalDerived : Derived
            {
                public virtual int Read() { return 1; }
            }
            int Main()
            {
                FinalDerived value = FinalDerived();
                return Dispatch(value) + value.Read();
            }
            """, engineProject);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
        StructTypeSymbol finalDerived = app.Compilation!.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "VirtualAbiApp").Structs.Single();
        StructTypeSymbol derived = finalDerived.BaseType!;
        FunctionSymbol inherited = derived.BaseType!.Methods.Single(method => method.Name == "Read");
        FunctionSymbol independent = finalDerived.Methods.Single(method => method.Name == "Read");
        Assert.Same(inherited, derived.VirtualMethods[inherited.VTableSlot!.Value]);
        Assert.Same(inherited, finalDerived.VirtualMethods[inherited.VTableSlot.Value]);
        Assert.NotEqual(inherited.VTableSlot, independent.VTableSlot);
        Assert.Same(independent, finalDerived.VirtualMethods[independent.VTableSlot!.Value]);

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Theory]
    [InlineData("static-library")]
    [InlineData("shared-library")]
    public async Task SourceVirtualAbiControlSurvivesIntermediateNativeLibrary(string libraryType)
    {
        using var directory = new TemporaryProject();
        string baseProject = directory.WriteDependencyProject("SourceAbiBase", libraryType, """
            namespace SourceAbiBase;
            public struct Base
            {
                internal virtual int Read() { return 40; }
            }
            public int CallInternal(Base& value) { return value.Read(); }
            """);
        string engineProject = directory.WriteDependencyProject("SourceAbiEngine", libraryType, """
            using SourceAbiBase;
            namespace SourceAbiEngine;
            public struct Derived : Base {}
            public int Dispatch(Derived& value) { return CallInternal(value) + 1; }
            """);
        string engineProjectPath = Path.GetFullPath(Path.Combine(directory.Root, engineProject));
        string engineDirectory = Path.GetDirectoryName(engineProjectPath)!;
        string baseFromEngine = Path.GetRelativePath(engineDirectory,
            Path.GetFullPath(Path.Combine(directory.Root, baseProject))).Replace('\\', '/');
        File.AppendAllText(engineProjectPath, $"""

            [references]
            projects = ["{baseFromEngine}"]
            """);

        directory.WriteProject("SourceAbiApp", "executable", """
            using SourceAbiEngine;
            namespace SourceAbiApp;
            public struct FinalDerived : Derived
            {
                public virtual int Read() { return 1; }
            }
            int Main()
            {
                FinalDerived value = FinalDerived();
                return Dispatch(value) + value.Read();
            }
            """, engineProject);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
    }

    [Fact]
    public async Task ClosedSourceVirtualLayoutsRetainXelibBodiesWithoutExportingThem()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("ClosedVirtualXelib", "xenon-library", """
            namespace ClosedVirtualXelib;
            public struct Base
            {
                internal virtual int Read() { return 1; }
                internal virtual int Value { get { return 2; } set {} }
                internal virtual int this[int index] { get { return index; } set {} }
                public virtual ~Base() {}
            }
            """);

        XenonBuildResult app = await BuildXelibConsumerAsync(directory, libraryProject,
            "ClosedVirtualXelibApp", """
                using ClosedVirtualXelib;
                namespace ClosedVirtualXelibApp;
                public sealed struct SealedDerived : Base {}
                internal struct InternalDerived : Base {}
                int Main() { return 42; }
                """);

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
        Assert.DoesNotContain(app.Compilation!.GetImplementationNativeAbiRoots().OfType<FunctionSymbol>(),
            function => function.Origin.LibraryContentIdentity is not null);
    }

    [Fact]
    public void SharedProjectReferenceExportsInheritedInaccessibleAbstractSlotStub()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("AbstractInternalVirtual", "shared-library", """
            namespace AbstractInternalVirtual;
            public abstract struct Base
            {
                internal abstract int Read();
            }
            """);
        directory.WriteProject("AbstractInternalVirtualApp", "executable", """
            using AbstractInternalVirtual;
            namespace AbstractInternalVirtualApp;
            public abstract struct Derived : Base {}
            int Main() { return 42; }
            """, libraryProject);

        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));

        Assert.True(app.Success, string.Join(Environment.NewLine, app.Diagnostics) +
            Environment.NewLine + app.Failure);
    }

    [Theory]
    [InlineData("static-library")]
    [InlineData("shared-library")]
    public void ForeignLibraryBuildHasDefinedObjectOnlyResult(string projectType)
    {
        using var directory = new TemporaryProject();
        directory.WriteProject("ForeignLibrary", projectType,
            "namespace ForeignLibrary; public int Value() { return 42; }");
        var runner = new RejectingProcessRunner();

        XenonBuildResult result = new XenonBuildDriver(runner).Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot,
            TargetTriple: "aarch64-unknown-linux-gnu"));

        Assert.True(result.Success, result.Failure);
        Assert.True(result.NativeLinkSkipped);
        Assert.False(result.IsRunnable);
        Assert.True(File.Exists(result.LlvmIrPath));
        Assert.True(File.Exists(result.ObjectPath));
        Assert.Null(result.ArtifactPath);
        Assert.Null(result.ImportLibraryPath);
        Assert.Equal(0, runner.CallCount);
    }

    private sealed class RejectingProcessRunner : INativeProcessRunner
    {
        public int CallCount { get; private set; }

        public Task<NativeProcessResult> RunAsync(
            NativeProcessRequest command, CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("The host native linker must not run for a foreign target.");
        }
    }

    private static async Task<XenonBuildResult> BuildXelibConsumerAsync(
        TemporaryProject directory, string libraryProject, string applicationName, string applicationSource)
    {
        string libraryProjectPath = Path.GetFullPath(Path.Combine(directory.Root, libraryProject));
        XenonBuildResult library = new XenonBuildDriver().Build(new XenonBuildRequest(
            libraryProjectPath, OutputRoot: directory.OutputRoot));
        Assert.True(library.Success, string.Join(Environment.NewLine,
            new[] { library.Failure }.Concat(library.Diagnostics.Select(diagnostic => diagnostic.ToString()))));
        Directory.Delete(Path.Combine(Path.GetDirectoryName(libraryProjectPath)!, "src"), recursive: true);

        directory.WriteProject(applicationName, "executable", applicationSource);
        string relativeLibrary = Path.GetRelativePath(directory.Root, library.ArtifactPath!).Replace('\\', '/');
        File.AppendAllText(directory.ProjectFile, $"""

            [libraries]
            libraries = ["{relativeLibrary}"]
            """);
        XenonBuildResult app = new XenonBuildDriver().Build(new XenonBuildRequest(
            directory.ProjectFile, OutputRoot: directory.OutputRoot));
        if (!app.Success) return app;

        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
        return app;
    }

    private sealed class TemporaryProject : IDisposable
    {
        public TemporaryProject()
        {
            Root = Path.Combine(Path.GetTempPath(), "xenon-driver-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            OutputRoot = Path.Combine(Root, "output");
            ProjectFile = Path.Combine(Root, "project.xeproj");
        }

        public string Root { get; }
        public string OutputRoot { get; }
        public string ProjectFile { get; }

        public void WriteProject(string name, string type, string source,
            params string[] projectReferences)
        {
            string sourceRoot = Path.Combine(Root, "src");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(Path.Combine(sourceRoot, "main.xe"), source);
            string references = projectReferences.Length == 0 ? string.Empty : $"""

                [references]
                projects = [{string.Join(", ", projectReferences.Select(path => $"\"{path}\""))}]
                """;
            File.WriteAllText(ProjectFile, $"""
                [project]
                name = "{name}"
                type = "{type}"

                [source]
                root = "src"
                {references}
                """);
        }

        public string WriteDependencyProject(string name, string type, string source,
            params string[] projectReferences)
        {
            string projectDirectory = Path.Combine(Root, name);
            string sourceRoot = Path.Combine(projectDirectory, "src");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(Path.Combine(sourceRoot, "main.xe"), source);
            string projectFile = Path.Combine(projectDirectory, $"{name}.xeproj");
            string references = projectReferences.Length == 0 ? string.Empty : $"""

                [references]
                projects = [{string.Join(", ", projectReferences.Select(path => $"\"{path}\""))}]
                """;
            File.WriteAllText(projectFile, $"""
                [project]
                name = "{name}"
                type = "{type}"

                [source]
                root = "src"
                {references}
                """);
            return Path.GetRelativePath(Root, projectFile).Replace('\\', '/');
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
