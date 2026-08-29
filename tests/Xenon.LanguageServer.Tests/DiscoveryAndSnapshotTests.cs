using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class DiscoveryAndSnapshotTests
{
    [Fact]
    public void DiscoveryPrefersWorkspaceOverNearerProject()
    {
        using var directory = new TestDirectory();
        directory.Write("App/src/main.xe", "fn main() {}\n");
        string project = directory.Write("App/App.xeproj", """
            [project]
            name = "App"
            type = "executable"
            [source]
            root = "src"
            """);
        string workspace = directory.Write("Root.xws", """
            [workspace]
            projects = ["App/App.xeproj"]
            """);

        WorkspaceDiscoveryResult result = WorkspaceDiscovery.Discover(null,
            DocumentUri.FromPath(System.IO.Path.GetDirectoryName(project)!).AbsoluteUri, null);

        using (result.Workspace)
        {
            Assert.Equal(System.IO.Path.GetFullPath(workspace), result.ConfigurationPath);
            Assert.NotNull(result.Workspace!.Configuration);
        }
    }

    [Fact]
    public void DiscoveryRejectsMultipleCandidatesAndMissingExplicitPath()
    {
        using var directory = new TestDirectory();
        directory.Write("a.xws", "[workspace]\nprojects=[]");
        directory.Write("b.xws", "[workspace]\nprojects=[]");
        Assert.Throws<ProjectSystemException>(() => WorkspaceDiscovery.Discover(null, null,
            directory.Path));
        Assert.Throws<ProjectSystemException>(() => WorkspaceDiscovery.Discover(
            System.IO.Path.Combine(directory.Path, "missing.xeproj"), null, null));
    }

    [Fact]
    public void AnalysisContextPinsSnapshotAndCancelsWhenSuperseded()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "fn main() {}\n");
        using var workspace = WorkspaceDiscovery.CreateLooseFile(file);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var factory = new LanguageServerAnalysisContextFactory(new DocumentContextResolver());
        using LanguageServerAnalysisContext context = factory.Create(workspace, uri);
        WorkspaceGeneration captured = context.Snapshot.Generation;
        DocumentId id = context.Document.Id;

        workspace.OpenDocument(id, "fn main() { }\n", new DocumentVersion(1));

        Assert.Equal(captured, context.Snapshot.Generation);
        Assert.True(context.CancellationToken.IsCancellationRequested);
        using LanguageServerAnalysisContext next = factory.Create(workspace, uri);
        Assert.True(next.Snapshot.Generation.Value > captured.Value);
        Assert.Equal(new DocumentVersion(1), next.Document.Version);
    }
}
