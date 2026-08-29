using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class XenonWorkspaceLoaderTests
{
    [Fact]
    public async Task RealisticFixtureLoadsThroughConfigurationSnapshotsAndSemanticReferences()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Workspaces", "GameWorkspace", "Game.xws");
        WorkspaceConfiguration configuration = XenonWorkspaceLoader.Load(path);
        using Workspace workspace = Workspace.Create(configuration);
        ProjectSnapshot game = workspace.CurrentSnapshot.Projects.Single(project =>
            project.Configuration.Name == "Game");

        var compilation = await game.GetCompilationAsync();

        Assert.False(compilation.HasErrors);
        Assert.Equal(3, workspace.CurrentSnapshot.Projects.Length);
        Assert.Equal(configuration.Id, workspace.CurrentSnapshot.Id);
        Assert.Equal("Core", Assert.Single(game.ProjectReferences).Configuration.Name);
        Assert.Empty(workspace.CurrentSnapshot.Projects.Single(project =>
            project.Configuration.Name == "Tools").ProjectReferences);
        WorkspaceSnapshot next = workspace.OpenDocument(Assert.Single(game.Documents).Id,
            Assert.Single(game.Documents).EffectiveText.Text, new DocumentVersion(1));
        Assert.Equal(workspace.CurrentSnapshot.Id, next.Id);
        Assert.Equal(game.Id, next.Projects.Single(project => project.Configuration.Name == "Game").Id);
        Assert.Equal(Assert.Single(game.Documents).Id,
            Assert.Single(next.Projects.Single(project => project.Configuration.Name == "Game").Documents).Id);
    }

    [Fact]
    public void LoaderBuildsRootReferencedAndIndependentProjects()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library");
        directory.WriteProject("App", "executable", ["../Library/Library.xeproj"]);
        directory.WriteProject("Tooling", "static-library");
        directory.Write("Xenon.xws", """
            [workspace]
            name = "Xenon"
            projects = [
                "App/App.xeproj",
                "Library/Library.xeproj",
                "Tooling/Tooling.xeproj",
            ]
            """);

        WorkspaceConfiguration definition = XenonWorkspaceLoader.Load(directory.PathOf("Xenon.xws"));
        XenonProjectGraph graph = XenonProjectGraph.Load(directory.PathOf("Xenon.xws"));
        using Workspace workspace = Workspace.Create(directory.PathOf("Xenon.xws"));

        Assert.Equal("Xenon", definition.Name);
        Assert.Equal("App", definition.Graph.Root.Name);
        Assert.Equal(["Library", "App", "Tooling"], graph.BuildOrder.Select(project => project.Name));
        Assert.Equal(3, workspace.CurrentSnapshot.Projects.Length);
        Assert.Equal(definition.Id, workspace.Id);
        Assert.All(definition.Projects, project => Assert.True(project.IsExplicitMember));
    }

    [Theory]
    [InlineData("[workspace]\nprojects = [\"App/App.txt\"]", ".xeproj")]
    [InlineData("[workspace]\nprojects = []", "at least one")]
    [InlineData("[workspace]\nprojects = [\"App/App.xeproj\"]\nunknown = true", "unknown workspace setting")]
    public void LoaderRejectsInvalidManifestsWithUsefulDiagnostics(string manifest, string expected)
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App");
        directory.Write("Invalid.xws", manifest.Replace("\\n", Environment.NewLine,
            StringComparison.Ordinal));

        ProjectSystemException exception = Assert.Throws<ProjectSystemException>(() =>
            XenonWorkspaceLoader.Load(directory.PathOf("Invalid.xws")));

        Assert.Contains(expected, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EquivalentDuplicatePathsAreRejectedAfterNormalization()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App");
        directory.Write("Duplicate.xws", """
            [workspace]
            projects = ["App/App.xeproj", "./App/../App/App.xeproj"]
            """);

        ProjectSystemException exception = Assert.Throws<ProjectSystemException>(() =>
            XenonWorkspaceLoader.Load(directory.PathOf("Duplicate.xws")));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransitiveDependencyIsOneImplicitMemberAndMembershipAddsNoEdges()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Engine", "static-library");
        directory.WriteProject("Game", "executable", ["../Engine/Engine.xeproj"]);
        directory.WriteProject("Tools", "static-library");
        directory.Write("Game.xws", """
            [workspace]
            projects = ["Game/Game.xeproj", "Tools/Tools.xeproj"]
            """);

        WorkspaceConfiguration configuration = XenonWorkspaceLoader.Load(directory.PathOf("Game.xws"));
        WorkspaceProjectEntry engine = configuration.Projects.Single(project => project.Project.Name == "Engine");
        XenonProject tools = configuration.Projects.Single(project => project.Project.Name == "Tools").Project;

        Assert.False(engine.IsExplicitMember);
        Assert.Equal(3, configuration.Projects.Length);
        Assert.Empty(configuration.Graph.GetDirectDependencies(tools));
        Assert.Single(configuration.Projects.Where(project => project.Project.Name == "Engine"));
    }

    [Fact]
    public void RelativePathsAreBasedOnWorkspaceDirectoryAndExplicitOrderIsPreserved()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("One", "static-library");
        directory.WriteProject("Two", "static-library");
        directory.Write("config/Ordered.xws", """
            [workspace]
            projects = ["../Two/Two.xeproj", "../One/One.xeproj"]
            """);

        WorkspaceConfiguration configuration = XenonWorkspaceLoader.Load(
            directory.PathOf("config/Ordered.xws"));

        Assert.Equal(["Two", "One"], configuration.ExplicitProjects.Select(entry => entry.Project.Name));
        Assert.Equal(Path.GetFullPath(directory.PathOf("config")), configuration.RootDirectory);
        Assert.Equal(Path.GetFullPath(directory.PathOf("Two/Two.xeproj")),
            configuration.ExplicitProjects[0].ProjectPath);
        Assert.Equal(configuration.Id,
            XenonWorkspaceLoader.Load(directory.PathOf("config/Ordered.xws")).Id);
    }

    [Fact]
    public void MissingInvalidAndCyclicProjectsUseFocusedAndExistingGraphErrors()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.Write("Missing.xws", "[workspace]\nprojects = [\"Nope/Nope.xeproj\"]");
        Assert.Contains("does not exist", Assert.Throws<ProjectSystemException>(() =>
            XenonWorkspaceLoader.Load(directory.PathOf("Missing.xws"))).Message,
            StringComparison.OrdinalIgnoreCase);

        directory.Write("Broken/Broken.xeproj", "[project]\nname = \"Broken\"");
        directory.Write("Broken.xws", "[workspace]\nprojects = [\"Broken/Broken.xeproj\"]");
        Assert.Contains("project.type", Assert.Throws<ProjectSystemException>(() =>
            XenonWorkspaceLoader.Load(directory.PathOf("Broken.xws"))).Message,
            StringComparison.OrdinalIgnoreCase);

        directory.WriteProject("A", "static-library", ["../B/B.xeproj"]);
        directory.WriteProject("B", "static-library", ["../A/A.xeproj"]);
        directory.Write("Cycle.xws", "[workspace]\nprojects = [\"A/A.xeproj\", \"B/B.xeproj\"]");
        Assert.Contains("cycle", Assert.Throws<ProjectSystemException>(() =>
            XenonWorkspaceLoader.Load(directory.PathOf("Cycle.xws"))).Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalExtensionRejectsCompetingWorkspaceSuffix()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App");
        directory.Write("App.xeworkspace", "[workspace]\nprojects = [\"App/App.xeproj\"]");

        ProjectSystemException exception = Assert.Throws<ProjectSystemException>(() =>
            XenonWorkspaceLoader.Load(directory.PathOf("App.xeworkspace")));

        Assert.Contains(".xws extension", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
