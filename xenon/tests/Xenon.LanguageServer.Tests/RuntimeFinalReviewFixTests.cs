using System.Text.Json;
using Xenon.Compiler;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class RuntimeFinalReviewFixTests
{
    [Fact]
    public async Task ProjectCreatedAfterEmptyFolderInitializationIsIndexedWithoutRestart()
    {
        using var directory = new TestDirectory();
        using var log = new StringWriter();
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask, log);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            rootUri = DocumentUri.FromPath(directory.Path).AbsoluteUri,
        }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);

        Assert.Empty(session.Workspaces);
        Assert.True(session.PendingConfigurationReconciliation);

        string project = directory.Write("Created.xeproj", Project("Created"));
        await WatchedAsync(session, (project, 1));
        Assert.Empty(session.Workspaces);
        Assert.True(session.PendingConfigurationReconciliation);

        string source = directory.Write("src/main.xe",
            "namespace Created; public int CreatedAfterStart() { return 42; }");
        await WatchedAsync(session, (source, 1));

        Workspace workspace = Assert.Single(session.Workspaces);
        Assert.False(session.PendingConfigurationReconciliation);
        Assert.Equal(Path.GetFullPath(project),
            workspace.CurrentSnapshot.RootProject.Configuration.ProjectFilePath);
        JsonElement symbols = await WorkspaceSymbolsAsync(session, "CreatedAfterStart");
        Assert.Contains(symbols.EnumerateArray(), symbol =>
            symbol.GetProperty("name").GetString() == "CreatedAfterStart");
    }

    [Fact]
    public async Task WorkspaceCreatedAfterEmptyFolderInitializationIsIndexedWithoutRestart()
    {
        using var directory = new TestDirectory();
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            rootUri = DocumentUri.FromPath(directory.Path).AbsoluteUri,
        }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);

        string manifest = directory.Write("Created.xws",
            WorkspaceManifest("App/App.xeproj", "Library/Library.xeproj"));
        await WatchedAsync(session, (manifest, 1));
        Assert.Empty(session.Workspaces);

        string appProject = directory.Write("App/App.xeproj", Project("App"));
        string appSource = directory.Write("App/src/main.xe",
            "using Library; namespace App; int Main() { return AddedWorkspaceSymbol(); }");
        await WatchedAsync(session, (appProject, 1), (appSource, 1));
        Assert.Empty(session.Workspaces);

        string libraryProject = directory.Write("Library/Library.xeproj", Project("Library"));
        string librarySource = directory.Write("Library/src/library.xe",
            "namespace Library; public int AddedWorkspaceSymbol() { return 42; }");
        await WatchedAsync(session, (libraryProject, 1), (librarySource, 1));

        Workspace workspace = Assert.Single(session.Workspaces);
        Assert.False(session.PendingConfigurationReconciliation);
        Assert.Equal(2, workspace.CurrentSnapshot.Projects.Length);
        JsonElement symbols = await WorkspaceSymbolsAsync(session, "AddedWorkspaceSymbol");
        Assert.Contains(symbols.EnumerateArray(), symbol =>
            symbol.GetProperty("name").GetString() == "AddedWorkspaceSymbol");
    }

    [Fact]
    public async Task InvalidLibraryProjectAtInitializeKeepsServerAliveAndRecoversWhenSourceAppears()
    {
        using var directory = new TestDirectory();
        string libraryPath = directory.PathOf("lib/Lib.xelib");
        Directory.CreateDirectory(Path.GetDirectoryName(libraryPath)!);
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Lib; public int Value() { return 42; }", "library.xe"));
        File.WriteAllBytes(libraryPath,
            XelibWriter.Write(library, new XelibWriteOptions("Lib")));
        string project = directory.Write("Test.xeproj", """
            [project]
            name = "Test"
            type = "executable"
            [source]
            root = "src"
            [libraries]
            libraries = ["lib/Lib.xelib"]
            """);
        using var log = new StringWriter();
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask, log);

        object? initialize = await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            rootUri = DocumentUri.FromPath(directory.Path).AbsoluteUri,
        }), default);

        Assert.NotNull(initialize);
        Assert.Equal(LanguageServerLifecycleState.InitializeResponded, session.State);
        Assert.Empty(session.Workspaces);
        Assert.True(session.PendingConfigurationReconciliation);
        Assert.Contains("initial workspace discovery failed", log.ToString(), StringComparison.Ordinal);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);

        string source = directory.Write("src/main.xe",
            "using Lib; namespace Test; int Main() { return Value(); }");
        await WatchedAsync(session, (project, 2), (source, 1));

        Workspace recovered = Assert.Single(session.Workspaces);
        Assert.False(session.PendingConfigurationReconciliation);
        Assert.Equal(LanguageServerLifecycleState.Initialized, session.State);
        Compilation compilation = await recovered.CurrentSnapshot.RootProject.GetCompilationAsync();
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.IsType<LibraryCompilationReference>(Assert.Single(compilation.References));
    }

    [Fact]
    public async Task MissingReferencedProjectCreatedLaterRetriesRootAndPreservesOverlay()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("A/src/main.xe", "disk\n");
        directory.Write("A/A.xeproj", Project("A"));
        directory.Write("B/src/b.xe", "b\n");
        string missingProject = Path.Combine(directory.Path, "B/B.xeproj");
        string manifest = directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, manifest);
        await OpenAsync(session, uri, 7, "unsaved overlay\n");
        Workspace original = Assert.Single(session.Workspaces);

        directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj", "B/B.xeproj"));
        await WatchedAsync(session, (manifest, 2));

        Assert.True(session.PendingConfigurationReconciliation);
        Assert.Same(original, Assert.Single(session.Workspaces));

        directory.Write("B/B.xeproj", Project("B"));
        await WatchedAsync(session, (missingProject, 1));

        Workspace recovered = Assert.Single(session.Workspaces);
        Assert.NotSame(original, recovered);
        Assert.False(session.PendingConfigurationReconciliation);
        Assert.Equal(2, recovered.CurrentSnapshot.Projects.Length);
        Assert.Equal(2, recovered.CurrentSnapshot.Projects
            .Select(project => project.Configuration.ProjectFilePath)
            .Distinct(DocumentUri.PathComparer).Count());
        DocumentSnapshot adopted = Assert.Single(recovered.CurrentSnapshot.Documents.Where(document =>
            document.PhysicalPath is not null && DocumentUri.PathComparer.Equals(
                DocumentUri.NormalizePath(document.PhysicalPath),
                DocumentUri.NormalizePath(main))));
        Assert.True(adopted.IsOpen);
        Assert.Equal(LspDocumentVersions.FromLsp(7), adopted.Version);
        Assert.Equal("unsaved overlay\n", adopted.EffectiveText.Text);
    }

    [Fact]
    public async Task InvalidReferencedProjectChangedToValidRetriesRootConfiguration()
    {
        using var directory = new TestDirectory();
        directory.Write("A/src/a.xe", "a\n");
        directory.Write("A/A.xeproj", Project("A"));
        directory.Write("B/src/b.xe", "b\n");
        string projectB = directory.Write("B/B.xeproj", "not a valid project");
        string manifest = directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj"));
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, manifest);

        directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj", "B/B.xeproj"));
        await WatchedAsync(session, (manifest, 2));
        Assert.True(session.PendingConfigurationReconciliation);
        Assert.Single(Assert.Single(session.Workspaces).CurrentSnapshot.Projects);

        directory.Write("B/B.xeproj", Project("B"));
        await WatchedAsync(session, (projectB, 2));

        Assert.False(session.PendingConfigurationReconciliation);
        Assert.Equal(2, Assert.Single(session.Workspaces).CurrentSnapshot.Projects.Length);
    }

    [Fact]
    public async Task RepeatedInvalidConfigurationEventsKeepExactPublishedGraphAndPendingRetry()
    {
        using var directory = new TestDirectory();
        directory.Write("A/src/a.xe", "a\n");
        directory.Write("A/A.xeproj", Project("A"));
        directory.Write("B/src/b.xe", "b\n");
        string projectB = directory.Write("B/B.xeproj", "invalid");
        string manifest = directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj"));
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, manifest);
        Workspace original = Assert.Single(session.Workspaces);
        WorkspaceSnapshot snapshot = original.CurrentSnapshot;

        directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj", "B/B.xeproj"));
        await WatchedAsync(session, (manifest, 2));
        await WatchedAsync(session, (projectB, 1), (projectB, 2));
        await WatchedAsync(session, (projectB, 2));

        Assert.True(session.PendingConfigurationReconciliation);
        Assert.Same(original, Assert.Single(session.Workspaces));
        Assert.Same(snapshot, original.CurrentSnapshot);
        Assert.Single(snapshot.Projects);

        directory.Write("B/B.xeproj", Project("B"));
        await WatchedAsync(session, (projectB, 2));
        Workspace recovered = Assert.Single(session.Workspaces);
        Assert.False(session.PendingConfigurationReconciliation);
        Assert.Equal(2, recovered.CurrentSnapshot.Projects.Length);
        Assert.Equal(2, recovered.CurrentSnapshot.Projects
            .Select(project => project.Configuration.ProjectFilePath)
            .Distinct(DocumentUri.PathComparer).Count());
    }

    [Fact]
    public async Task ReloadRetiresEveryWorkspaceWhenOneStaleCallbackThrows()
    {
        using var directory = new TestDirectory();
        directory.Write("App/src/main.xe", "main\n");
        string project = directory.Write("App/App.xeproj", Project("App"));
        string loose = directory.Write("loose.xe", "loose\n");
        string looseUri = DocumentUri.FromPath(loose).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, project);
        await OpenAsync(session, looseUri, 3, "loose overlay\n");
        Assert.Equal(2, session.Workspaces.Count);
        Workspace throwingWorkspace = session.Workspaces[0];
        Workspace observedWorkspace = session.Workspaces[1];
        using WorkspaceAnalysisRequest throwingRequest = throwingWorkspace.CreateAnalysisRequest();
        using WorkspaceAnalysisRequest observedRequest = observedWorkspace.CreateAnalysisRequest();
        using CancellationTokenRegistration throwingRegistration =
            throwingRequest.CancellationToken.Register(() =>
                throw new InvalidOperationException("throwing stale callback"));
        int observedCancellation = 0;
        using CancellationTokenRegistration observedRegistration =
            observedRequest.CancellationToken.Register(() =>
                Interlocked.Exchange(ref observedCancellation, 1));

        directory.Write("App/App.xeproj", Project("App"));
        await WatchedAsync(session, (project, 2));

        Assert.Equal(1, Volatile.Read(ref observedCancellation));
        Assert.DoesNotContain(throwingWorkspace, session.Workspaces);
        Assert.DoesNotContain(observedWorkspace, session.Workspaces);
        Assert.Equal(2, session.Workspaces.Count);
        Assert.Throws<ObjectDisposedException>(() => throwingWorkspace.CreateAnalysisRequest());
        Assert.Throws<ObjectDisposedException>(() => observedWorkspace.CreateAnalysisRequest());
    }

    private static async Task InitializeAsync(LanguageServerSession session, string path)
    {
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            initializationOptions = new { workspacePath = path },
        }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
    }

    private static Task OpenAsync(LanguageServerSession session, string uri, int version, string text) =>
        session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version, text },
        }), default);

    private static Task WatchedAsync(LanguageServerSession session,
        params (string Path, int Type)[] changes) => session.HandleNotificationAsync(
            "workspace/didChangeWatchedFiles", LspTestProtocol.Json(new
            {
                changes = changes.Select(change => new
                {
                    uri = DocumentUri.FromPath(change.Path).AbsoluteUri,
                    type = change.Type,
                }).ToArray(),
            }), default);

    private static async Task<JsonElement> WorkspaceSymbolsAsync(LanguageServerSession session,
        string query)
    {
        object? result = await session.HandleRequestAsync("workspace/symbol",
            LspTestProtocol.Json(new { query }), default);
        return JsonSerializer.SerializeToElement(result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static string Project(string name) => $$"""
        [project]
        name = "{{name}}"
        type = "executable"
        [source]
        root = "src"
        """;

    private static string WorkspaceManifest(params string[] projects) => $$"""
        [workspace]
        projects = [{{string.Join(", ", projects.Select(project => $"\"{project}\""))}}]
        """;
}
