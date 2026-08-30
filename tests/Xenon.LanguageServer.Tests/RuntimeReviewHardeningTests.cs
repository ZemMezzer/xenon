using System.Runtime.CompilerServices;
using System.Text.Json;
using Xenon.LanguageServer.Protocol;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class RuntimeReviewHardeningTests
{
    [Fact]
    public async Task ReplacedWorkspaceDiagnosticWithSameVersionCannotPublish()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "namespace App; void Test() { missing; }");
        string project = directory.Write("App/App.xeproj", Project("App", "src"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        var oldAnalyzed = Signal();
        var releaseOld = Signal();
        var currentPublished = Signal();
        int analyses = 0;
        var publications = new List<JsonElement>();
        var hooks = new LanguageServerRuntimeHooks
        {
            AfterDiagnosticAnalysisAsync = async _ =>
            {
                if (Interlocked.Increment(ref analyses) != 1) return;
                oldAnalyzed.TrySetResult();
                await releaseOld.Task;
            },
        };
        await using var session = new LanguageServerSession((method, value) =>
        {
            if (method == "textDocument/publishDiagnostics")
            {
                JsonElement notification = JsonSerializer.SerializeToElement(value);
                lock (publications) publications.Add(notification);
                currentPublished.TrySetResult();
            }
            return Task.CompletedTask;
        }, diagnosticDebounce: TimeSpan.Zero, runtimeHooks: hooks);
        await InitializeAsync(session, project);
        await OpenAsync(session, uri, 10, "namespace App; void Test() { missing; }");
        await oldAnalyzed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        directory.Write("App/src/added.xe", "namespace App; void Added() {}\n");
        await WatchedAsync(session, (Path.Combine(directory.Path, "App/src/added.xe"), 1));
        await currentPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseOld.TrySetResult();
        await WaitForAsync(() => session.PendingDiagnosticCount == 0);

        Assert.True(analyses >= 2);
        Assert.Single(publications);
        Assert.Equal(10, publications[0].GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task FailedOrCancelledReloadLeavesPublishedWorkspaceUntouchedAndCandidatesCollectible()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "disk\n");
        directory.Write("App/other/other.xe", "other\n");
        string project = directory.Write("App/App.xeproj", Project("App", "src"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        var candidateReferences = new List<WeakReference>();
        var hooks = new LanguageServerRuntimeHooks
        {
            ReloadCandidatesPrepared = candidates => candidateReferences.AddRange(
                candidates.Select(candidate => new WeakReference(candidate))),
            BeforeReloadCommitAsync = _ => throw new InvalidOperationException("injected failure"),
        };
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            runtimeHooks: hooks);
        await InitializeAsync(session, project);
        await OpenAsync(session, uri, 5, "unsaved\n");
        Workspace original = Assert.Single(session.Workspaces);
        WorkspaceSnapshot originalSnapshot = original.CurrentSnapshot;

        directory.Write("App/App.xeproj", Project("App", "other"));
        await WatchedAsync(session, (project, 2));
        Assert.Same(original, Assert.Single(session.Workspaces));
        Assert.Same(originalSnapshot, original.CurrentSnapshot);
        Assert.Equal("unsaved\n", Assert.Single(originalSnapshot.Documents).EffectiveText.Text);
        await ForceCollectionAsync(candidateReferences);
        Assert.All(candidateReferences, reference => Assert.False(reference.IsAlive));

        var prepared = Signal();
        var cancelledCandidates = new List<WeakReference>();
        var cancellationHooks = new LanguageServerRuntimeHooks
        {
            ReloadCandidatesPrepared = candidates =>
            {
                cancelledCandidates.AddRange(candidates.Select(candidate => new WeakReference(candidate)));
                prepared.TrySetResult();
            },
            BeforeReloadCommitAsync = token => Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        await using var cancelledSession = new LanguageServerSession((_, _) => Task.CompletedTask,
            runtimeHooks: cancellationHooks);
        directory.Write("App/App.xeproj", Project("App", "src"));
        await InitializeAsync(cancelledSession, project);
        Workspace beforeCancellation = Assert.Single(cancelledSession.Workspaces);
        WorkspaceSnapshot beforeCancellationSnapshot = beforeCancellation.CurrentSnapshot;
        directory.Write("App/App.xeproj", Project("App", "other"));
        using var cancellation = new CancellationTokenSource();
        Task reload = WatchedAsync(cancelledSession, cancellation.Token, (project, 2));
        await prepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reload);
        Assert.Same(beforeCancellation, Assert.Single(cancelledSession.Workspaces));
        Assert.Same(beforeCancellationSnapshot, beforeCancellation.CurrentSnapshot);
        await ForceCollectionAsync(cancelledCandidates);
        Assert.All(cancelledCandidates, reference => Assert.False(reference.IsAlive));
    }

    [Fact]
    public async Task RemovedOpenOverlayIsReAdoptedWhenFileReturnsToPrimary()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "disk\n");
        directory.Write("App/other/other.xe", "other\n");
        string project = directory.Write("App/App.xeproj", Project("App", "src"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, project);
        await OpenAsync(session, uri, 10, "unsaved overlay\n");

        directory.Write("App/App.xeproj", Project("App", "other"));
        await WatchedAsync(session, (project, 2));
        Assert.Equal(2, session.Workspaces.Count);
        Workspace orphan = session.Workspaces[1];
        WeakReference orphanReference = new(orphan);
        Assert.Equal("unsaved overlay\n",
            Assert.Single(orphan.CurrentSnapshot.Documents).EffectiveText.Text);
        orphan = null!;

        directory.Write("App/App.xeproj", Project("App", "src"));
        await WatchedAsync(session, (project, 2));
        Workspace primary = Assert.Single(session.Workspaces);
        DocumentSnapshot adopted = Assert.Single(primary.CurrentSnapshot.Documents);
        Assert.True(adopted.IsOpen);
        Assert.Equal(LspDocumentVersions.FromLsp(10), adopted.Version);
        Assert.Equal("unsaved overlay\n", adopted.EffectiveText.Text);
        await WaitForAsync(() => session.PendingDiagnosticCount == 0);
        await ForceCollectionAsync([orphanReference]);
        Assert.False(orphanReference.IsAlive);

        await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 11 },
            contentChanges = new[] { new { text = "overlay v11\n" } },
        }), default);
        Assert.Equal("overlay v11\n", Assert.Single(primary.CurrentSnapshot.Documents).EffectiveText.Text);
        Assert.Equal("overlay v11\n", await session.ExecuteSemanticRequestAsync(uri,
            context => Task.FromResult<object?>(context.Document.EffectiveText.Text)));
    }

    [Fact]
    public async Task SharedOpenOverlayIsReAdoptedIntoEveryLogicalContext()
    {
        using var directory = new TestDirectory();
        string shared = directory.Write("shared/common.xe", "disk\n");
        directory.Write("A/A.xeproj", Project("A", "../shared"));
        directory.Write("B/B.xeproj", Project("B", "../shared"));
        directory.Write("C/src/c.xe", "c\n");
        directory.Write("C/C.xeproj", Project("C", "src"));
        string manifest = directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj", "B/B.xeproj"));
        string uri = DocumentUri.FromPath(shared).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, manifest);
        await OpenAsync(session, uri, 20, "shared overlay\n");

        directory.Write("Root.xws", WorkspaceManifest("C/C.xeproj"));
        await WatchedAsync(session, (manifest, 2));
        Assert.Equal(2, session.Workspaces.Count);
        directory.Write("Root.xws", WorkspaceManifest("A/A.xeproj", "B/B.xeproj"));
        await WatchedAsync(session, (manifest, 2));

        Workspace primary = Assert.Single(session.Workspaces);
        Assert.Equal(2, primary.CurrentSnapshot.Documents.Length);
        Assert.All(primary.CurrentSnapshot.Documents, document =>
        {
            Assert.True(document.IsOpen);
            Assert.Equal(LspDocumentVersions.FromLsp(20), document.Version);
            Assert.Equal("shared overlay\n", document.EffectiveText.Text);
        });
    }

    [Fact]
    public async Task CreatedAndDeletedMembershipRetriesAfterTransientReloadFailure()
    {
        using var directory = new TestDirectory();
        directory.Write("App/src/main.xe", "main\n");
        string project = directory.Write("App/App.xeproj", Project("App", "src"));
        int failNext = 1;
        var hooks = new LanguageServerRuntimeHooks
        {
            BeforeReloadCommitAsync = _ => Interlocked.Exchange(ref failNext, 0) == 1
                ? throw new IOException("transient watcher race") : Task.CompletedTask,
        };
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            runtimeHooks: hooks);
        await InitializeAsync(session, project);
        string added = directory.Write("App/src/added.xe", "added\n");

        await WatchedAsync(session, (added, 1));
        Assert.Single(Assert.Single(session.Workspaces).CurrentSnapshot.Documents);
        await WatchedAsync(session, (added, 2));
        Assert.Equal(2, Assert.Single(session.Workspaces).CurrentSnapshot.Documents.Length);
        await WatchedAsync(session, (added, 2), (added, 2));
        Assert.Equal(2, Assert.Single(session.Workspaces).CurrentSnapshot.Documents.Length);

        Volatile.Write(ref failNext, 1);
        File.Delete(added);
        await WatchedAsync(session, (added, 3));
        Assert.Equal(2, Assert.Single(session.Workspaces).CurrentSnapshot.Documents.Length);
        await WatchedAsync(session, (added, 2));
        Assert.Single(Assert.Single(session.Workspaces).CurrentSnapshot.Documents);
    }

    [Fact]
    public async Task AnalysisAcquisitionCannotRaceWorkspaceRetirement()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "main\n");
        string project = directory.Write("App/App.xeproj", Project("App", "src"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        using var entered = new ManualResetEventSlim();
        using var releaseAcquisition = new ManualResetEventSlim();
        var hooks = new LanguageServerRuntimeHooks
        {
            BeforeAnalysisAcquisition = () =>
            {
                entered.Set();
                releaseAcquisition.Wait();
            },
        };
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            runtimeHooks: hooks);
        await InitializeAsync(session, project);
        Task<object?> request = Task.Run(() => session.ExecuteSemanticRequestAsync(uri,
            async context =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                return null;
            }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        string added = directory.Write("App/src/added.xe", "added\n");
        Task reload = Task.Run(() => WatchedAsync(session, (added, 1)));
        releaseAcquisition.Set();

        await reload.WaitAsync(TimeSpan.FromSeconds(5));
        JsonRpcException cancellation = await Assert.ThrowsAsync<JsonRpcException>(() => request);
        Assert.Equal(LspErrorCodes.RequestCancelled, cancellation.Code);
    }

    [Fact]
    public async Task ActiveAnalysisRetainsOldWorkspaceOnlyUntilRequestReleasesIt()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "main\n");
        string project = directory.Write("App/App.xeproj", Project("App", "src"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, project);
        WeakReference oldWorkspace = CaptureWorkspace(session);
        var acquired = Signal();
        var release = Signal();
        Task<object?> request = session.ExecuteSemanticRequestAsync(uri, async _ =>
        {
            acquired.TrySetResult();
            await release.Task;
            return null;
        });
        await acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string added = directory.Write("App/src/added.xe", "added\n");
        await WatchedAsync(session, (added, 1));

        await ForceCollectionAsync([oldWorkspace]);
        Assert.True(oldWorkspace.IsAlive);
        release.TrySetResult();
        await Assert.ThrowsAsync<JsonRpcException>(() => request);
        await ForceCollectionAsync([oldWorkspace]);
        Assert.False(oldWorkspace.IsAlive);
    }

    [Fact]
    public async Task SerializedReloadOrderingKeepsNewestFilesystemState()
    {
        using var directory = new TestDirectory();
        directory.Write("App/initial/initial.xe", "initial\n");
        directory.Write("App/a/a.xe", "a\n");
        directory.Write("App/b/b.xe", "b\n");
        string project = directory.Write("App/App.xeproj", Project("App", "initial"));
        var firstPrepared = Signal();
        var releaseFirst = Signal();
        int attempts = 0;
        var hooks = new LanguageServerRuntimeHooks
        {
            BeforeReloadCommitAsync = async _ =>
            {
                if (Interlocked.Increment(ref attempts) != 1) return;
                firstPrepared.TrySetResult();
                await releaseFirst.Task;
            },
        };
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            runtimeHooks: hooks);
        await InitializeAsync(session, project);

        directory.Write("App/App.xeproj", Project("App", "a"));
        Task reloadA = WatchedAsync(session, (project, 2));
        await firstPrepared.Task.WaitAsync(TimeSpan.FromSeconds(5));
        directory.Write("App/App.xeproj", Project("App", "b"));
        Task reloadB = WatchedAsync(session, (project, 2));
        releaseFirst.TrySetResult();
        await Task.WhenAll(reloadA, reloadB).WaitAsync(TimeSpan.FromSeconds(5));

        DocumentSnapshot current = Assert.Single(Assert.Single(session.Workspaces)
            .CurrentSnapshot.Documents);
        Assert.EndsWith(Path.Combine("b", "b.xe"), current.PhysicalPath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManyAcquiredRequestsRemainSafeAcrossRepeatedWorkspaceReplacement()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "main\n");
        string project = directory.Write("App/App.xeproj", Project("App", "src"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, project);
        var retired = new List<WeakReference>();
        string extra = Path.Combine(directory.Path, "App/src/extra.xe");

        for (int iteration = 0; iteration < 8; iteration++)
        {
            retired.Add(CaptureWorkspace(session));
            var allAcquired = Signal();
            var release = Signal();
            int acquisitions = 0;
            Task<object?>[] requests = Enumerable.Range(0, 20).Select(_ =>
                session.ExecuteSemanticRequestAsync(uri, async _ =>
                {
                    if (Interlocked.Increment(ref acquisitions) == 20) allAcquired.TrySetResult();
                    await release.Task;
                    return null;
                })).ToArray();
            await allAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5));

            int eventType;
            if (iteration % 2 == 0)
            {
                directory.Write("App/src/extra.xe", "extra\n");
                eventType = 1;
            }
            else
            {
                File.Delete(extra);
                eventType = 3;
            }
            await WatchedAsync(session, (extra, eventType));
            release.TrySetResult();
            foreach (Task<object?> request in requests)
            {
                JsonRpcException error = await Assert.ThrowsAsync<JsonRpcException>(() => request);
                Assert.Equal(LspErrorCodes.RequestCancelled, error.Code);
            }
        }

        await WaitForAsync(() => session.PendingDiagnosticCount == 0);
        await ForceCollectionAsync(retired);
        Assert.All(retired, reference => Assert.False(reference.IsAlive));
    }

    [Fact]
    public async Task DeletedOrphanWorkspaceIsCollectibleAfterDidClose()
    {
        using var directory = new TestDirectory();
        string main = directory.Write("App/src/main.xe", "disk\n");
        directory.Write("App/src/other.xe", "other\n");
        string project = directory.Write("App/App.xeproj", Project("App", "src"));
        string uri = DocumentUri.FromPath(main).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, project);
        await OpenAsync(session, uri, 3, "deleted overlay\n");
        File.Delete(main);
        await WatchedAsync(session, (main, 3));
        WeakReference orphan = new(session.Workspaces.Single(workspace =>
            workspace.CurrentSnapshot.Documents.Any(document => document.IsOpen)));

        await session.HandleNotificationAsync("textDocument/didClose", LspTestProtocol.Json(new
        {
            textDocument = new { uri },
        }), default);
        await WaitForAsync(() => session.PendingDiagnosticCount == 0);
        await ForceCollectionAsync([orphan]);

        Assert.False(orphan.IsAlive);
        Assert.Single(session.Workspaces);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CaptureWorkspace(LanguageServerSession session) =>
        new(Assert.Single(session.Workspaces));

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
        params (string Path, int Type)[] changes) => WatchedAsync(session, default, changes);

    private static Task WatchedAsync(LanguageServerSession session, CancellationToken token,
        params (string Path, int Type)[] changes) => session.HandleNotificationAsync(
            "workspace/didChangeWatchedFiles", LspTestProtocol.Json(new
            {
                changes = changes.Select(change => new
                {
                    uri = DocumentUri.FromPath(change.Path).AbsoluteUri,
                    type = change.Type,
                }).ToArray(),
            }), token);

    private static string Project(string name, string sourceRoot) => $$"""
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

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static async Task ForceCollectionAsync(IEnumerable<WeakReference> references)
    {
        WeakReference[] items = references.ToArray();
        for (int attempt = 0; attempt < 12 && items.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Yield();
        }
    }
}
