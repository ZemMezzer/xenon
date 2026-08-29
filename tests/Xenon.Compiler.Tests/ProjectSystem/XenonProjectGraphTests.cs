using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class XenonProjectGraphTests
{
    [Fact]
    public void GraphBuildOrderIsDeterministicAndDependenciesFirst()
    {
        using var directory = new TemporaryDirectory();
        WriteProject(directory, "Core", "static-library");
        WriteProject(directory, "Rendering", "static-library", "../Core/Core.xeproj");
        WriteProject(directory, "Physics", "static-library", "../Core/Core.xeproj");
        WriteProject(directory, "Game", "executable", "../Physics/Physics.xeproj", "../Rendering/Rendering.xeproj");

        XenonProjectGraph graph = XenonProjectGraph.Load(directory.PathOf("Game/Game.xeproj"));

        Assert.Equal(["Core", "Physics", "Rendering", "Game"], graph.BuildOrder.Select(project => project.Name));
        Assert.Equal(["Physics", "Rendering"], graph.GetDirectDependencies(graph.Root).Select(project => project.Name));
        Assert.Equal(["Rendering", "Physics", "Core"],
            graph.GetNativeLinkOrder(graph.Root).Select(project => project.Name));
        Assert.Equal(4, graph.Projects.Length);

        XenonProjectGraph toolingGraph = XenonProjectGraph.Create(graph.Root, graph.Projects);
        Assert.Equal(graph.BuildOrder.Select(project => project.Identity),
            toolingGraph.BuildOrder.Select(project => project.Identity));
    }

    [Fact]
    public void GraphReportsCyclesAndMissingReferences()
    {
        using var directory = new TemporaryDirectory();
        WriteProject(directory, "A", "static-library", "../B/B.xeproj");
        WriteProject(directory, "B", "static-library", "../A/A.xeproj");
        ProjectSystemException cycle = Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Load(directory.PathOf("A/A.xeproj")));
        Assert.Contains("cycle", cycle.Message, StringComparison.OrdinalIgnoreCase);
        XenonProject a = XenonProjectLoader.LoadProjectFile(directory.PathOf("A/A.xeproj"));
        XenonProject b = XenonProjectLoader.LoadProjectFile(directory.PathOf("B/B.xeproj"));
        Assert.Contains("cycle", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Create(a, [a, b])).Message, StringComparison.OrdinalIgnoreCase);

        WriteProject(directory, "Missing", "executable", "../Unknown/Unknown.xeproj");
        ProjectSystemException missing = Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Load(directory.PathOf("Missing/Missing.xeproj")));
        Assert.Contains("missing project", missing.Message, StringComparison.OrdinalIgnoreCase);
        XenonProject missingRoot = XenonProjectLoader.LoadProjectFile(directory.PathOf("Missing/Missing.xeproj"));
        Assert.Contains("missing project", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Create(missingRoot, [missingRoot])).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeLinkOrderIsDependentFirstForChainsAndMixedDiamonds()
    {
        using var directory = new TemporaryDirectory();
        WriteProject(directory, "Core", "static-library");
        WriteProject(directory, "Middle", "static-library", "../Core/Core.xeproj");
        WriteProject(directory, "ChainApp", "executable", "../Middle/Middle.xeproj");
        XenonProjectGraph chain = XenonProjectGraph.Load(directory.PathOf("ChainApp/ChainApp.xeproj"));
        Assert.Equal(["Middle", "Core"],
            chain.GetNativeLinkOrder(chain.Root).Select(project => project.Name));

        WriteProject(directory, "Shared", "shared-library", "../Core/Core.xeproj");
        WriteProject(directory, "Utility", "static-library", "../Core/Core.xeproj");
        WriteProject(directory, "MixedApp", "executable",
            "../Shared/Shared.xeproj", "../Utility/Utility.xeproj");
        XenonProjectGraph mixed = XenonProjectGraph.Load(directory.PathOf("MixedApp/MixedApp.xeproj"));
        Assert.Equal(["Utility", "Shared", "Core"],
            mixed.GetNativeLinkOrder(mixed.Root).Select(project => project.Name));
        Assert.Equal(mixed.GetNativeLinkOrder(mixed.Root).Select(project => project.Identity),
            XenonProjectGraph.Load(directory.PathOf("MixedApp/MixedApp.xeproj"))
                .GetNativeLinkOrder(mixed.Root).Select(project => project.Identity));
    }

    [Fact]
    public void LoadAndCreateRejectExecutableAndDuplicateReferenceEdgesEqually()
    {
        using var directory = new TemporaryDirectory();
        WriteProject(directory, "Tool", "executable");
        WriteProject(directory, "App", "executable", "../Tool/Tool.xeproj");
        Assert.Contains("cannot reference executable", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Load(directory.PathOf("App/App.xeproj"))).Message,
            StringComparison.OrdinalIgnoreCase);
        XenonProject app = XenonProjectLoader.LoadProjectFile(directory.PathOf("App/App.xeproj"));
        XenonProject tool = XenonProjectLoader.LoadProjectFile(directory.PathOf("Tool/Tool.xeproj"));
        Assert.Contains("cannot reference executable", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Create(app, [app, tool])).Message,
            StringComparison.OrdinalIgnoreCase);

        WriteProject(directory, "Core", "static-library");
        WriteProject(directory, "Duplicate", "executable",
            "../Core/Core.xeproj", "../Core/Core.xeproj");
        Assert.Contains("duplicate project references", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Load(directory.PathOf("Duplicate/Duplicate.xeproj"))).Message,
            StringComparison.OrdinalIgnoreCase);
        XenonProject duplicate = XenonProjectLoader.LoadProjectFile(directory.PathOf("Duplicate/Duplicate.xeproj"));
        XenonProject core = XenonProjectLoader.LoadProjectFile(directory.PathOf("Core/Core.xeproj"));
        Assert.Contains("duplicate project references", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Create(duplicate, [duplicate, core])).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAndCreateRejectDuplicateArtifactNamesEqually()
    {
        using var directory = new TemporaryDirectory();
        WriteProject(directory, "First", "static-library");
        WriteProject(directory, "Second", "static-library");
        WriteProject(directory, "App", "executable",
            "../First/First.xeproj", "../Second/Second.xeproj");
        directory.Write("Second/Second.xeproj", """
            [project]
            name = "First"
            type = "static-library"

            [source]
            root = "src"
            """);

        Assert.Contains("duplicate project name", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Load(directory.PathOf("App/App.xeproj"))).Message,
            StringComparison.OrdinalIgnoreCase);
        XenonProject app = XenonProjectLoader.LoadProjectFile(directory.PathOf("App/App.xeproj"));
        XenonProject first = XenonProjectLoader.LoadProjectFile(directory.PathOf("First/First.xeproj"));
        XenonProject second = XenonProjectLoader.LoadProjectFile(directory.PathOf("Second/Second.xeproj"));
        Assert.Contains("duplicate project name", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Create(app, [app, first, second])).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateRejectsInvalidKindsAndConflictingIdentitySnapshots()
    {
        using var directory = new TemporaryDirectory();
        WriteProject(directory, "Core", "static-library");
        XenonProject core = XenonProjectLoader.LoadProjectFile(directory.PathOf("Core/Core.xeproj"));
        XenonProject invalid = Copy(core, type: (XenonProjectType)999);
        Assert.Contains("invalid project type", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Create(invalid, [invalid])).Message,
            StringComparison.OrdinalIgnoreCase);

        XenonProject conflict = Copy(core, name: "OtherCore");
        Assert.Contains("conflicting configuration snapshots", Assert.Throws<ProjectSystemException>(
            () => XenonProjectGraph.Create(core, [core, conflict])).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private static XenonProject Copy(XenonProject project, string? name = null, XenonProjectType? type = null) =>
        new(name ?? project.Name, type ?? project.Type, project.Version, project.RootDirectory,
            project.SourceRoot, project.ProjectFilePath, project.SourceFiles, project.NativeLibraries,
            project.NativeLibraryPaths, project.ProjectReferences, project.DebugProfile, project.ReleaseProfile);

    private static void WriteProject(TemporaryDirectory directory, string name, string type,
        params string[] references)
    {
        string referencesText = references.Length == 0 ? string.Empty : $"""

            [references]
            projects = [{string.Join(", ", references.Select(reference => $"\"{reference}\""))}]
            """;
        directory.Write($"{name}/{name}.xeproj", $"""
            [project]
            name = "{name}"
            type = "{type}"

            [source]
            root = "src"
            {referencesText}
            """);
        directory.Write($"{name}/src/main.xe", $"namespace {name}; int Value() {{ return 1; }}");
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Root = Path.Combine(Path.GetTempPath(), "xenon-project-graph-tests", Guid.NewGuid().ToString("N"));
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
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
