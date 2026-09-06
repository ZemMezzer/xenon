using Xenon.CodeGen.LLVM;
using Xenon.Driver;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
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
        Assert.True(library.Success, library.Failure ?? string.Join(Environment.NewLine, library.Diagnostics));

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
                return box.Offset + box.Get();
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
        Assert.True(app.Success, app.Failure ?? string.Join(Environment.NewLine, app.Diagnostics));
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
        Assert.True(app.Success, app.Failure ?? string.Join(Environment.NewLine, app.Diagnostics));
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
        Assert.True(app.Success, app.Failure ?? string.Join(Environment.NewLine, app.Diagnostics));
        NativeProcessResult process = await new NativeProcessRunner().RunAsync(new NativeProcessRequest(
            app.ArtifactPath!, [], directory.Root, TimeSpan.FromSeconds(15)));
        Assert.Null(process.StartError);
        Assert.False(process.TimedOut);
        Assert.Equal(42, process.ExitCode);
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
