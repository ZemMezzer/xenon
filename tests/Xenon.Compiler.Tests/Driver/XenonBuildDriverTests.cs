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

        public void WriteProject(string name, string type, string source)
        {
            string sourceRoot = Path.Combine(Root, "src");
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllText(Path.Combine(sourceRoot, "main.xe"), source);
            File.WriteAllText(ProjectFile, $"""
                [project]
                name = "{name}"
                type = "{type}"

                [source]
                root = "src"
                """);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
