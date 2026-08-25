using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class XenonProjectLoaderTests
{
    [Fact]
    public void Loader_LoadsExplicitProjectSourcesAndProfiles()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("App.xeproj", """
            [project]
            name = "Example.App"
            type = "executable"
            version = "0.1.0"

            [source]
            root = "code"

            [profile.debug]
            optimization = 1
            debug-info = true
            checks = false

            [profile.release]
            optimization = 2
            """);
        directory.Write("code/Main.xe", "namespace Example; int Main() { return 0; }");
        directory.Write("code/IO/Loader.xe", "namespace Example.IO; int Load() { return 1; }");

        XenonProject project = XenonProjectLoader.LoadProjectFile(directory.PathOf("App.xeproj"));

        Assert.Equal("Example.App", project.Name);
        Assert.Equal(XenonProjectType.Executable, project.Type);
        Assert.Equal("0.1.0", project.Version);
        Assert.False(project.IsImplicit);
        Assert.Equal(2, project.SourceFiles.Length);
        Assert.Equal(1, project.DebugProfile.OptimizationLevel);
        Assert.True(project.DebugProfile.EmitDebugInformation);
        Assert.False(project.DebugProfile.EnableChecks);
        Assert.Equal(2, project.ReleaseProfile.OptimizationLevel);
        Assert.False(project.ReleaseProfile.EmitDebugInformation);
    }

    [Fact]
    public void Loader_TreatsDirectoryWithoutProjectFileAsImplicitExecutable()
    {
        using var directory = new TemporaryDirectory();
        string projectDirectory = directory.CreateDirectory("ImplicitApp");
        directory.Write("ImplicitApp/Main.xe", "namespace Example; int Main() { return 0; }");
        directory.Write("ImplicitApp/Nested/Math.xe", "namespace Example; int Add() { return 1; }");
        directory.Write("ImplicitApp/readme.txt", "not a source file");

        XenonProject project = XenonProjectLoader.LoadDirectory(projectDirectory);

        Assert.Equal("ImplicitApp", project.Name);
        Assert.Equal(XenonProjectType.Executable, project.Type);
        Assert.True(project.IsImplicit);
        Assert.Equal(Path.GetFullPath(projectDirectory), project.SourceRoot);
        Assert.Equal(2, project.SourceFiles.Length);
    }

    [Fact]
    public void Loader_PrefersExplicitProjectSourceRootOverImplicitDiscovery()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("Library.xeproj", """
            [project]
            name = "Library"
            type = "static-library"

            [source]
            root = "src"
            """);
        directory.Write("src/Library.xe", "namespace Library; export int Value() { return 1; }");
        directory.Write("unrelated.xe", "this file must not be included");

        XenonProject project = XenonProjectLoader.LoadDirectory(directory.Root);

        Assert.False(project.IsImplicit);
        Assert.Equal(XenonProjectType.StaticLibrary, project.Type);
        Assert.Equal("Library.xe", Path.GetFileName(Assert.Single(project.SourceFiles)));
    }

    [Fact]
    public void Loader_RejectsAmbiguousDirectory()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("First.xeproj", string.Empty);
        directory.Write("Second.xeproj", string.Empty);

        ProjectSystemException exception = Assert.Throws<ProjectSystemException>(
            () => XenonProjectLoader.LoadDirectory(directory.Root));

        Assert.Contains("multiple .xeproj files", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_ReportsUnknownSettingsWithSourceLine()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("Invalid.xeproj", """
            [project]
            name = "Invalid"
            type = "executable"
            unexpected = true
            """);

        ProjectSystemException exception = Assert.Throws<ProjectSystemException>(
            () => XenonProjectLoader.LoadProjectFile(directory.PathOf("Invalid.xeproj")));

        Assert.Contains("Invalid.xeproj(4)", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unknown project setting 'project.unexpected'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_RejectsProjectWithoutSources()
    {
        using var directory = new TemporaryDirectory();
        directory.CreateDirectory("Empty");

        ProjectSystemException exception = Assert.Throws<ProjectSystemException>(
            () => XenonProjectLoader.LoadDirectory(directory.PathOf("Empty")));

        Assert.Contains("no .xe source files", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildPaths_SeparatesObjectsByProfileAndTarget()
    {
        using var directory = new TemporaryDirectory();
        string projectDirectory = directory.CreateDirectory("ImplicitApp");
        directory.Write("ImplicitApp/Main.xe", "namespace Example; int Main() { return 0; }");
        XenonProject project = XenonProjectLoader.LoadDirectory(projectDirectory);

        string objectPath = XenonBuildPaths.GetObjectFilePath(
            project,
            "release",
            "x86_64-pc-windows-msvc",
            ".obj");

        Assert.Equal(
            Path.Combine(
                projectDirectory,
                ".xenon",
                "obj",
                "release",
                "x86_64-pc-windows-msvc",
                "ImplicitApp.obj"),
            objectPath);

        string executablePath = XenonBuildPaths.GetExecutablePath(
            projectDirectory,
            project.Name,
            "release",
            "x86_64-pc-windows-msvc");
        Assert.Equal(
            Path.Combine(
                projectDirectory,
                "build",
                "release",
                "x86_64-pc-windows-msvc",
                "ImplicitApp.exe"),
            executablePath);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "xenon-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string CreateDirectory(string relativePath)
        {
            string path = PathOf(relativePath);
            Directory.CreateDirectory(path);
            return path;
        }

        public string PathOf(string relativePath) => Path.Combine(Root, relativePath);

        public void Write(string relativePath, string content)
        {
            string path = PathOf(relativePath);
            string? parent = Path.GetDirectoryName(path);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(path, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
