using System.Runtime.CompilerServices;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class WorkspaceLifetimeTests
{
    [Fact]
    public void DisposeCancelsEveryStaleLeaseWhenOneConsumerCallbackThrows()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "namespace App; int Main() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        using WorkspaceAnalysisRequest throwing = workspace.CreateAnalysisRequest();
        using WorkspaceAnalysisRequest observed = workspace.CreateAnalysisRequest();
        using CancellationTokenRegistration throwingRegistration =
            throwing.CancellationToken.Register(() =>
                throw new InvalidOperationException("consumer callback failure"));
        int observedCancellation = 0;
        using CancellationTokenRegistration observedRegistration =
            observed.CancellationToken.Register(() =>
                Interlocked.Exchange(ref observedCancellation, 1));

        Exception? failure = Record.Exception(workspace.Dispose);

        Assert.Null(failure);
        Assert.True(throwing.CancellationToken.IsCancellationRequested);
        Assert.True(observed.CancellationToken.IsCancellationRequested);
        Assert.Equal(1, Volatile.Read(ref observedCancellation));
        Assert.Throws<ObjectDisposedException>(() => workspace.CreateAnalysisRequest());
    }

    [Fact]
    public void ObsoleteSnapshotProjectDocumentCompilationAndIndexesAreCollectible()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "namespace App; int Main() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        LifetimeReferences references = CreateObsoleteGeneration(workspace);

        Collect(references.All);

        Assert.All(references.All, reference => Assert.False(reference.IsAlive));
        Assert.Equal(1, workspace.CurrentSnapshot.Generation.Value);
    }

    [Fact]
    public void HundredEditsIndexesCancellationAndOverlayTransitionsDoNotCreateHistoryChain()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library", sources: [("library.xe",
            "namespace Library; public int Value() { return 0; }")]);
        directory.WriteProject("App", "executable", ["../Library/Library.xeproj"], ("main.xe",
            "using Library; namespace App; int Main() { return Value(); }"));
        using Workspace workspace = directory.CreateWorkspace();
        DocumentId library = workspace.CurrentSnapshot.Projects
            .Single(project => project.Configuration.Name == "Library").Documents.Single().Id;
        var oldSnapshots = new List<WeakReference>();
        for (int version = 1; version <= 120; version++)
        {
            WorkspaceSnapshot prior = workspace.CurrentSnapshot;
            using WorkspaceAnalysisRequest request = workspace.CreateAnalysisRequest(staleSensitive: true);
            WorkspaceSnapshot current = workspace.OpenDocument(library,
                $"namespace Library; public int Value() {{ return {version}; }}",
                new DocumentVersion(version));
            Assert.True(request.CancellationToken.IsCancellationRequested);
            _ = current.GetSymbolIndexAsync().GetAwaiter().GetResult();
            _ = current.GetReferenceIndexAsync().GetAwaiter().GetResult();
            if (version <= 110) oldSnapshots.Add(new WeakReference(prior));
        }

        Collect(oldSnapshots);

        Assert.True(oldSnapshots.Count(reference => reference.IsAlive) <= 1,
            "Current editor state retained an unbounded chain of obsolete Workspace snapshots.");
        Assert.Equal(120, workspace.CurrentSnapshot.Generation.Value);
    }

    [Fact]
    public void MultipleOpenDocumentsReferenceUpdatesCloseAndIndexWorkRemainCollectible()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library", sources: [("library.xe",
            "namespace Library; public int Value() { return 1; }")]);
        directory.WriteProject("App", sources: [("main.xe",
            "using Library; namespace App; int Main() { return Value(); }")]);
        directory.Write("Workspace.xws", """
            [workspace]
            projects = ["App/App.xeproj", "Library/Library.xeproj"]
            """);
        using Workspace workspace = Workspace.Create(directory.PathOf("Workspace.xws"));

        WeakReference[] obsolete = ExerciseMixedWorkload(workspace);
        Collect(obsolete);

        Assert.All(obsolete, reference => Assert.False(reference.IsAlive));
        Assert.All(workspace.CurrentSnapshot.Documents, document => Assert.False(document.IsOpen));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LifetimeReferences CreateObsoleteGeneration(Workspace workspace)
    {
        WorkspaceSnapshot old = workspace.CurrentSnapshot;
        ProjectSnapshot project = old.RootProject;
        DocumentSnapshot document = Assert.Single(project.Documents);
        object compilation = project.GetCompilationAsync().GetAwaiter().GetResult();
        object symbols = old.GetSymbolIndexAsync().GetAwaiter().GetResult();
        object references = old.GetReferenceIndexAsync().GetAwaiter().GetResult();
        workspace.OpenDocument(document.Id,
            "namespace App; int Renamed() { return 1; }", new DocumentVersion(1));
        return new LifetimeReferences(
        [
            new WeakReference(old),
            new WeakReference(project),
            new WeakReference(document),
            new WeakReference(compilation),
            new WeakReference(symbols),
            new WeakReference(references),
        ]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference[] ExerciseMixedWorkload(Workspace workspace)
    {
        ProjectSnapshot app = workspace.CurrentSnapshot.Projects.Single(project =>
            project.Configuration.Name == "App");
        ProjectSnapshot library = workspace.CurrentSnapshot.Projects.Single(project =>
            project.Configuration.Name == "Library");
        DocumentId appDocument = Assert.Single(app.Documents).Id;
        DocumentId libraryDocument = Assert.Single(library.Documents).Id;
        var obsolete = new List<WeakReference>();
        WorkspaceSnapshot snapshot = workspace.OpenDocument(appDocument,
            Assert.Single(app.Documents).EffectiveText.Text, new DocumentVersion(1));
        obsolete.Add(new WeakReference(snapshot));
        snapshot = workspace.OpenDocument(libraryDocument,
            Assert.Single(library.Documents).EffectiveText.Text, new DocumentVersion(1));
        obsolete.Add(new WeakReference(snapshot));
        _ = snapshot.GetSymbolIndexAsync().GetAwaiter().GetResult();
        _ = snapshot.GetReferenceIndexAsync().GetAwaiter().GetResult();
        XenonProject configuration = app.Configuration;
        var withReference = new XenonProject(configuration.Name, configuration.Type,
            configuration.Version, configuration.RootDirectory, configuration.SourceRoot,
            configuration.ProjectFilePath, configuration.SourceFiles, configuration.NativeLibraries,
            configuration.NativeLibraryPaths, [library.Configuration.Identity],
            configuration.DebugProfile, configuration.ReleaseProfile);
        using (WorkspaceAnalysisRequest request = workspace.CreateAnalysisRequest(staleSensitive: true))
        {
            snapshot = workspace.UpdateProject(app.Id, withReference);
            Assert.True(request.CancellationToken.IsCancellationRequested);
        }
        obsolete.Add(new WeakReference(snapshot));
        snapshot = workspace.ApplyDocumentChanges(appDocument, new DocumentVersion(1),
            new DocumentVersion(2), []);
        obsolete.Add(new WeakReference(snapshot));
        snapshot = workspace.CloseDocument(appDocument, new DocumentVersion(2));
        obsolete.Add(new WeakReference(snapshot));
        workspace.CloseDocument(libraryDocument, new DocumentVersion(1));
        return obsolete.ToArray();
    }

    private static void Collect(IEnumerable<WeakReference> references)
    {
        WeakReference[] values = references.ToArray();
        for (int attempt = 0; attempt < 15 && values.Any(reference => reference.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private sealed record LifetimeReferences(WeakReference[] All);
}
