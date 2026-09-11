using System.Runtime.CompilerServices;
using System.Text.Json;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class RuntimeRobustnessTests
{
    [Fact]
    public async Task ProjectReloadPreservesOverlayAndRejectsMalformedReplacement()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "disk main\n");
        string project = directory.Write("App/App.xeproj", Project("App"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        using var log = new StringWriter();
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask, log,
            diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, project);
        await OpenAsync(session, uri, 7, "unsaved main\n");
        Workspace original = Assert.Single(session.Workspaces);

        string added = directory.Write("App/src/added.xe", "fn added() { }\n");
        await WatchedAsync(session, (added, 1));

        Workspace reloaded = Assert.Single(session.Workspaces);
        Assert.NotSame(original, reloaded);
        Assert.Equal(2, reloaded.CurrentSnapshot.Documents.Length);
        DocumentSnapshot overlay = Assert.Single(reloaded.CurrentSnapshot.Documents.Where(document =>
            document.PhysicalPath is not null && DocumentUri.PathComparer.Equals(
                DocumentUri.NormalizePath(document.PhysicalPath), DocumentUri.NormalizePath(main))));
        Assert.True(overlay.IsOpen);
        Assert.Equal("unsaved main\n", overlay.EffectiveText.Text);
        Assert.Equal(LspDocumentVersions.FromLsp(7), overlay.Version);

        directory.Write("App/App.xeproj", "this is not a project");
        await WatchedAsync(session, (project, 2));

        Assert.Same(reloaded, Assert.Single(session.Workspaces));
        Assert.Contains("keeping last valid state", log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkspaceMembershipReloadsAndMalformedWorkspaceKeepsLastValidState()
    {
        using var directory = new TestDirectory();
        directory.Write("A/src/a.xe", "fn a() { }\n");
        string a = directory.Write("A/A.xeproj", Project("A"));
        directory.Write("B/src/b.xe", "fn b() { }\n");
        string b = directory.Write("B/B.xeproj", Project("B"));
        string manifest = directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj"));
        using var log = new StringWriter();
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask, log);
        await InitializeAsync(session, manifest);
        Assert.Single(Assert.Single(session.Workspaces).CurrentSnapshot.Projects);

        directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj", "B/B.xeproj"));
        await WatchedAsync(session, (manifest, 2));
        Workspace valid = Assert.Single(session.Workspaces);
        Assert.Equal(2, valid.CurrentSnapshot.Projects.Length);
        Assert.Contains(valid.CurrentSnapshot.Projects,
            project => project.Configuration.ProjectFilePath is { } path &&
                DocumentUri.PathComparer.Equals(DocumentUri.NormalizePath(path),
                    DocumentUri.NormalizePath(b)));

        directory.Write("Root.xws", "[workspace]\nprojects = []\n");
        await WatchedAsync(session, (manifest, 2));
        Assert.Same(valid, Assert.Single(session.Workspaces));
        Assert.Equal(2, valid.CurrentSnapshot.Projects.Length);
        Assert.Contains("keeping last valid state", log.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public async Task ProjectReferenceAdditionAndRemovalRebuildsDependencyGraph()
    {
        using var directory = new TestDirectory();
        string appFile = directory.Write("App/src/main.xe", "namespace App; fn main() { }\n");
        string app = directory.Write("App/App.xeproj", Project("App"));
        directory.Write("Core/src/core.xe", "namespace Core; int Value() { return 1; }\n");
        directory.Write("Core/Core.xeproj", """
            [project]
            name = "Core"
            type = "static-library"
            [source]
            root = "src"
            """);
        string uri = DocumentUri.FromPath(appFile).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, app);
        await OpenAsync(session, uri, 4, "namespace App; fn main() { } // unsaved\n");
        Assert.Single(Assert.Single(session.Workspaces).CurrentSnapshot.Projects);

        directory.Write("App/App.xeproj", """
            [project]
            name = "App"
            type = "executable"
            [source]
            root = "src"
            [references]
            projects = ["../Core/Core.xeproj"]
            """);
        await WatchedAsync(session, (app, 2));
        Workspace withReference = Assert.Single(session.Workspaces);
        Assert.Equal(2, withReference.CurrentSnapshot.Projects.Length);
        Assert.Equal("namespace App; fn main() { } // unsaved\n",
            withReference.CurrentSnapshot.Documents.Single(document => document.IsOpen)
                .EffectiveText.Text);

        directory.Write("App/App.xeproj", Project("App"));
        await WatchedAsync(session, (app, 2));
        Workspace withoutReference = Assert.Single(session.Workspaces);
        Assert.Single(withoutReference.CurrentSnapshot.Projects);
        Assert.Equal(LspDocumentVersions.FromLsp(4),
            withoutReference.CurrentSnapshot.Documents.Single(document => document.IsOpen).Version);
    }

    [Fact]
    public async Task ExternalSharedBackingUpdatePreservesOpenOverlayAndEditorVersion()
    {
        using var directory = new TestDirectory();
        string shared = directory.Write("shared/common.xe", "disk one\n");
        directory.Write("A/A.xeproj", Project("A", "../shared"));
        directory.Write("B/B.xeproj", Project("B", "../shared"));
        string manifest = directory.Write("Root.xws",
            WorkspaceManifest("A/A.xeproj", "B/B.xeproj"));
        string uri = DocumentUri.FromPath(shared).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, manifest);
        Workspace workspace = Assert.Single(session.Workspaces);
        BackingVersion initialBacking = workspace.CurrentSnapshot.Documents[0].BackingVersion;

        directory.Write("shared/common.xe", "disk two\n");
        await WatchedAsync(session, (shared, 2));
        Assert.Equal(0, session.KnownUriCount);
        Assert.All(workspace.CurrentSnapshot.Documents, document =>
        {
            Assert.Equal("disk two\n", document.DiskText!.Text);
            Assert.Equal(DocumentVersion.Initial, document.Version);
            Assert.True(document.BackingVersion > initialBacking);
        });

        await OpenAsync(session, uri, 12, "unsaved overlay\n");
        Assert.Equal(1, session.KnownUriCount);
        BackingVersion beforeOpenChange = workspace.CurrentSnapshot.Documents[0].BackingVersion;
        directory.Write("shared/common.xe", "disk three\n");
        await WatchedAsync(session, (shared, 2));
        Assert.All(workspace.CurrentSnapshot.Documents, document =>
        {
            Assert.Equal("disk three\n", document.DiskText!.Text);
            Assert.Equal("unsaved overlay\n", document.EffectiveText.Text);
            Assert.Equal(LspDocumentVersions.FromLsp(12), document.Version);
            Assert.True(document.BackingVersion > beforeOpenChange);
        });
    }

    [Fact]
    public async Task DeletedOpenProjectDocumentBecomesDeterministicOrphanUntilClose()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "disk\n");
        string other = directory.Write("App/src/other.xe", "fn other() { }\n");
        string project = directory.Write("App/App.xeproj", Project("App"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, project);
        await OpenAsync(session, uri, 3, "unsaved deleted\n");

        File.Delete(main);
        await WatchedAsync(session, (main, 3));

        Assert.Equal(2, session.Workspaces.Count);
        Assert.Single(session.Workspaces[0].CurrentSnapshot.Documents);
        Assert.Equal(DocumentUri.NormalizePath(other), DocumentUri.NormalizePath(
            session.Workspaces[0].CurrentSnapshot.Documents[0].PhysicalPath!));
        DocumentSnapshot orphan = Assert.Single(session.Workspaces[1].CurrentSnapshot.Documents);
        Assert.True(orphan.IsOpen);
        Assert.Equal("unsaved deleted\n", orphan.EffectiveText.Text);

        await session.HandleNotificationAsync("textDocument/didClose", LspTestProtocol.Json(new
        {
            textDocument = new { uri },
        }), default);
        Assert.Single(session.Workspaces);
    }

    [Fact]
    public async Task ReadRequestsActuallyOverlapOnOneImmutableSnapshot()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "fn main() { }\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, file);
        int active = 0;
        int maximum = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object?>[] requests = Enumerable.Range(0, 24).Select(_ =>
            session.ExecuteSemanticRequestAsync(uri, async context =>
            {
                int current = Interlocked.Increment(ref active);
                InterlockedExtensions.Max(ref maximum, current);
                Assert.Same(session.Workspaces[0].CurrentSnapshot, context.Snapshot);
                await release.Task;
                Interlocked.Decrement(ref active);
                return context.Document.EffectiveText.Text;
            })).ToArray();

        await WaitForAsync(() => Volatile.Read(ref active) == requests.Length);
        release.TrySetResult();
        object?[] results = await Task.WhenAll(requests);
        Assert.True(maximum > 1);
        Assert.All(results, result => Assert.Equal("fn main() { }\n", result));
    }

    [Fact]
    public async Task ReplacedWorkspaceBecomesCollectible()
    {
        using var directory = new TestDirectory();
        directory.Write("A/src/a.xe", "fn a() { }\n");
        directory.Write("A/A.xeproj", Project("A"));
        directory.Write("B/src/b.xe", "fn b() { }\n");
        directory.Write("B/B.xeproj", Project("B"));
        string manifest = directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj"));
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, manifest);
        WeakReference oldWorkspace = CaptureWeakWorkspace(session);

        directory.Write("Root.xws", WorkspaceManifest("B/B.xeproj"));
        await WatchedAsync(session, (manifest, 2));
        await ForceCollectionAsync(oldWorkspace);

        Assert.False(oldWorkspace.IsAlive);
        Assert.Equal(0, session.KnownUriCount);
        Assert.Equal(0, session.PendingDiagnosticCount);
    }

    [Fact]
    public async Task HundredEditGenerationsAndCancelledRequestReleaseOldSnapshots()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "fn main() { }\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, file);
        await OpenAsync(session, uri, 1, "fn main() { }\n");
        WeakReference[] generations = await CreateGenerationsAsync(session, uri, 125);
        await WaitForAsync(() => session.PendingDiagnosticCount == 0);

        var requestStarted = new TaskCompletionSource<WeakReference>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<object?> request = session.ExecuteSemanticRequestAsync(uri, async context =>
        {
            requestStarted.TrySetResult(new WeakReference(context.Snapshot));
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            return null;
        });
        WeakReference cancelledSnapshot = await requestStarted.Task;
        await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 127 },
            contentChanges = new[] { new { text = "fn main() { } // current\n" } },
        }), default);
        await Assert.ThrowsAsync<Xenon.LanguageServer.Protocol.JsonRpcException>(() => request);
        await WaitForAsync(() => session.PendingDiagnosticCount == 0);

        foreach (WeakReference generation in generations) await ForceCollectionAsync(generation);
        await ForceCollectionAsync(cancelledSnapshot);
        Assert.All(generations, generation => Assert.False(generation.IsAlive));
        Assert.False(cancelledSnapshot.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> CreateGenerationsAsync(
        LanguageServerSession session, string uri, int count)
    {
        var references = new WeakReference[count];
        for (int index = 0; index < count; index++)
        {
            references[index] = new WeakReference(session.Workspaces[0].CurrentSnapshot);
            await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
            {
                textDocument = new { uri, version = index + 2 },
                contentChanges = new[] { new { text = $"fn main() {{ }} // {index}\n" } },
            }), default);
        }
        return references;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureWeakWorkspace(LanguageServerSession session) =>
        new(Assert.Single(session.Workspaces));

    private static async Task ForceCollectionAsync(WeakReference reference)
    {
        for (int attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(10);
        }
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

    private static string Project(string name, string sourceRoot = "src") => $$"""
        [project]
        name = "{{name}}"
        type = "executable"
        [source]
        root = "{{sourceRoot}}"
        """;

    private static string WorkspaceManifest(params string[] projects) => $$"""
        [workspace]
        projects = [{{string.Join(", ", projects.Select(project => $"\"{project}\""))}}]
        """;

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            int current = Volatile.Read(ref location);
            while (current < value)
            {
                int observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current) return;
                current = observed;
            }
        }
    }
}
