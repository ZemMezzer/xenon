using Xenon.CodeGen.LLVM;
using Xenon.Driver;
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
    public async Task ProjectReferenceGenericFunctionAndStructCompileLinkAndRunInConsumer()
    {
        using var directory = new TemporaryProject();
        string libraryProject = directory.WriteDependencyProject("GenericLibrary", "static-library", """
            namespace GenericLibrary;
            public T Identity<T>(T value) { return move value; }
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
                Box<int> box = Box<int>(40);
                return box.Offset + Identity<int>(box.Get());
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

        public string WriteDependencyProject(string name, string type, string source)
        {
            string projectDirectory = Path.Combine(Root, name);
            string sourceRoot = Path.Combine(projectDirectory, "src");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(Path.Combine(sourceRoot, "main.xe"), source);
            string projectFile = Path.Combine(projectDirectory, $"{name}.xeproj");
            File.WriteAllText(projectFile, $"""
                [project]
                name = "{name}"
                type = "{type}"

                [source]
                root = "src"
                """);
            return Path.GetRelativePath(Root, projectFile).Replace('\\', '/');
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
