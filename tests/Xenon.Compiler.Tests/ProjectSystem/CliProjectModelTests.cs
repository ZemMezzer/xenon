using Xenon.Cli;
using Xenon.Compiler;
using Xenon.Driver;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class CliProjectModelTests
{
    [Fact]
    public void CliRejectsRunningForeignTargetAfterObjectOnlyDriverBuild()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("App/App.xeproj", """
            [project]
            name = "ForeignCliApp"
            type = "executable"

            [source]
            root = "src"
            """);
        directory.Write("App/src/main.xe", "namespace ForeignCliApp; int Main() { return 42; }");

        int exitCode = Program.Main([
            "run",
            directory.PathOf("App/App.xeproj"),
            "--target",
            "aarch64-unknown-linux-gnu",
        ]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void ProjectShapedCliRouteUsesTheSameGraphAndCompilationModel()
    {
        using var directory = new TemporaryDirectory();
        directory.Write("Core/Core.xeproj", """
            [project]
            name = "Core"
            type = "static-library"

            [source]
            root = "src"
            """);
        directory.Write("Core/src/core.xe", "namespace Cli.Core; public int Value() { return 42; }");
        directory.Write("App/App.xeproj", """
            [project]
            name = "App"
            type = "executable"

            [source]
            root = "src"

            [native]
            libraries = ["user32"]
            library-paths = ["native"]

            [references]
            projects = ["../Core/Core.xeproj"]
            """);
        directory.Write("App/src/main.xe",
            "using Cli.Core; namespace Cli.App; int Main() { return Value(); }");
        string projectFile = directory.PathOf("App/App.xeproj");

        Assert.True(Program.IsProjectShapedInput([projectFile]));
        Assert.True(Program.IsProjectShapedInput([directory.PathOf("App")]));
        Assert.False(Program.IsProjectShapedInput([directory.PathOf("App/src/main.xe")]));

        XenonBuildRequest cliRequest = Program.CreateProjectBuildRequest(
            projectFile, "release", targetTriple: null, compileOnly: true, skipLink: false);
        XenonBuildResult driverResult = new XenonBuildDriver().Build(cliRequest);
        XenonProjectGraph graph = XenonProjectGraph.Load(projectFile);
        var compilations = new Dictionary<string, Compilation>(StringComparer.OrdinalIgnoreCase);
        foreach (XenonProject project in graph.BuildOrder)
            compilations[project.Identity] = XenonProjectCompilationFactory.Create(
                project, "release", compilations);
        Compilation apiCompilation = compilations[graph.Root.Identity];

        Assert.True(driverResult.Success, driverResult.Failure);
        Assert.Equal(graph.Projects.Select(project => project.Identity),
            driverResult.ProjectGraph!.Projects.Select(project => project.Identity));
        Assert.Equal(graph.Root.SourceFiles.ToArray(), driverResult.Project!.SourceFiles.ToArray());
        Assert.Equal(graph.Root.ProjectReferences.ToArray(), driverResult.Project.ProjectReferences.ToArray());
        Assert.Equal(graph.Root.Type, driverResult.Project.Type);
        Assert.Equal(graph.Root.NativeLibraries.ToArray(), driverResult.Project.NativeLibraries.ToArray());
        Assert.Equal(graph.Root.NativeLibraryPaths.ToArray(), driverResult.Project.NativeLibraryPaths.ToArray());
        Assert.Equal(apiCompilation.Options, driverResult.Compilation!.Options);
        Assert.Equal(apiCompilation.SyntaxTrees.Select(tree => tree.Source.Path),
            driverResult.Compilation.SyntaxTrees.Select(tree => tree.Source.Path));
        Assert.Equal(apiCompilation.References.Length, driverResult.Compilation.References.Length);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "xenon-cli-model-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }
        public string PathOf(string path) => Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

        public void Write(string path, string content)
        {
            string fullPath = PathOf(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
