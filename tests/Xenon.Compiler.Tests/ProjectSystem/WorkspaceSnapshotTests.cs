using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class WorkspaceSnapshotTests
{
    [Fact]
    public async Task OverlayVersionsAreIsolatedAndSaveCloseTransitionsAreExplicit()
    {
        using var directory = new WorkspaceTestDirectory();
        const string disk = "namespace App; int Old() { return 1; }";
        const string overlay = "namespace App; int New() { return 2; }";
        directory.WriteProject("App", sources: [("main.xe", disk)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot s1 = workspace.CurrentSnapshot;
        DocumentSnapshot d1 = Assert.Single(s1.Documents);

        WorkspaceSnapshot s2 = workspace.OpenDocument(d1.Id, overlay, new DocumentVersion(42));
        DocumentSnapshot d2 = s2.GetDocument(d1.Id);

        Assert.Equal(d1.Id, d2.Id);
        Assert.Equal(disk, d1.EffectiveText.Text);
        Assert.Equal(overlay, d2.EffectiveText.Text);
        Assert.Equal(disk, File.ReadAllText(d1.PhysicalPath!));
        Assert.True(d2.IsOpen);
        Assert.True(d2.IsUnsaved);
        Assert.NotSame(d1.SyntaxTree, d2.SyntaxTree);
        Assert.Contains((await s1.RootProject.GetCompilationAsync()).SemanticModel.GlobalNamespace
            .Namespaces.Single().Functions, function => function.Name == "Old");
        Assert.Contains((await s2.RootProject.GetCompilationAsync()).SemanticModel.GlobalNamespace
            .Namespaces.Single().Functions, function => function.Name == "New");

        WorkspaceSnapshot saved = workspace.SaveDocument(d1.Id, new DocumentVersion(42));
        Assert.Equal(overlay, File.ReadAllText(d1.PhysicalPath!));
        Assert.True(saved.GetDocument(d1.Id).IsOpen);
        Assert.False(saved.GetDocument(d1.Id).IsUnsaved);
        WorkspaceSnapshot closed = workspace.CloseDocument(d1.Id, new DocumentVersion(44));
        Assert.False(closed.GetDocument(d1.Id).IsOpen);
        Assert.Equal(overlay, closed.GetDocument(d1.Id).EffectiveText.Text);
        Assert.Same(saved.GetDocument(d1.Id).SyntaxTree, closed.GetDocument(d1.Id).SyntaxTree);
    }

    [Fact]
    public void CloseWithoutSaveRevertsAndStaleVersionsNeverPublish()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "namespace App; int Value() { return 1; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        DocumentSnapshot original = Assert.Single(workspace.CurrentSnapshot.Documents);
        WorkspaceSnapshot opened = workspace.OpenDocument(original.Id,
            "namespace App; int Value() { return 2; }", new DocumentVersion(10));
        int valuePosition = opened.GetDocument(original.Id).EffectiveText.Text.IndexOf('2');
        WorkspaceSnapshot edited = workspace.ApplyDocumentChanges(original.Id,
            new DocumentVersion(10), new DocumentVersion(11),
            [new DocumentTextChange(new TextSpan(valuePosition, 1), "3")]);
        WorkspaceSnapshot beforeStale = workspace.CurrentSnapshot;

        Assert.Throws<StaleDocumentVersionException>(() => workspace.ApplyDocumentChanges(original.Id,
            new DocumentVersion(10), new DocumentVersion(12), []));
        Assert.Same(beforeStale, workspace.CurrentSnapshot);
        WorkspaceSnapshot closed = workspace.CloseDocument(original.Id, new DocumentVersion(12));
        Assert.Equal(original.EffectiveText.Text, closed.GetDocument(original.Id).EffectiveText.Text);
        Assert.Equal("namespace App; int Value() { return 3; }", edited.GetDocument(original.Id).EffectiveText.Text);
        Assert.Equal("namespace App; int Value() { return 2; }", opened.GetDocument(original.Id).EffectiveText.Text);
    }

    [Fact]
    public void IncrementalChangesValidateRangesAndHandleMultipleUnicodeEdits()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "namespace App; int Value() { return 1; } // λ")]);
        using Workspace workspace = directory.CreateWorkspace();
        DocumentSnapshot document = Assert.Single(workspace.CurrentSnapshot.Documents);
        workspace.OpenDocument(document.Id, document.EffectiveText.Text, new DocumentVersion(1));
        string oldText = document.EffectiveText.Text;
        int returnStart = oldText.IndexOf("return 1", StringComparison.Ordinal);
        int unicodeStart = oldText.IndexOf('λ');
        WorkspaceSnapshot changed = workspace.ApplyDocumentChanges(document.Id,
            new DocumentVersion(1), new DocumentVersion(2),
            [
                new DocumentTextChange(new TextSpan(15, 0), "public "),
                new DocumentTextChange(new TextSpan(returnStart, "return 1".Length), "return 22"),
                new DocumentTextChange(new TextSpan(unicodeStart, 1), "Ω"),
            ]);

        Assert.Equal("namespace App; public int Value() { return 22; } // Ω",
            changed.GetDocument(document.Id).EffectiveText.Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => workspace.ApplyDocumentChanges(document.Id,
            new DocumentVersion(2), new DocumentVersion(3),
            [new DocumentTextChange(new TextSpan(999, 1), "x")]));
        Assert.Equal(new DocumentVersion(2), workspace.CurrentSnapshot.GetDocument(document.Id).Version);
    }

    [Fact]
    public void StableIdsAreProjectScopedAndRemoveReaddRequiresNewIdentity()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.Write("shared/main.xe", "namespace Shared; int Value() { return 1; }");
        XenonProject first = CreateProject(directory, "One", directory.PathOf("shared/main.xe"));
        XenonProject second = CreateProject(directory, "Two", directory.PathOf("shared/main.xe"));
        XenonProjectGraph graph = XenonProjectGraph.Create(first, [first, second]);
        using Workspace workspace = Workspace.Create(graph);
        DocumentSnapshot[] documents = workspace.CurrentSnapshot.Documents.ToArray();
        Assert.Equal(2, documents.Length);
        Assert.Equal(documents[0].PhysicalPath, documents[1].PhysicalPath);
        Assert.NotEqual(documents[0].Id, documents[1].Id);
        Assert.NotEqual(documents[0].SourceFileId, documents[1].SourceFileId);

        DocumentSnapshot removed = documents[0];
        workspace.RemoveDocument(removed.Id);
        DocumentId replacementId = DocumentId.CreateNew(removed.ProjectId);
        WorkspaceSnapshot replacement = workspace.AddDocument(replacementId,
            removed.EffectiveText.Text, new DocumentVersion(1), isOpen: true);
        Assert.NotEqual(removed.Id, replacement.GetDocument(replacementId).Id);
    }

    [Fact]
    public async Task UnsavedNewDocumentParticipatesInCrossFileSemanticsWithoutDiskFile()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe",
            "namespace App; int Main() { return Added(); }")]);
        using Workspace workspace = directory.CreateWorkspace();
        ProjectId projectId = workspace.CurrentSnapshot.RootProjectId;
        DocumentId addedId = DocumentId.CreateNew(projectId);
        WorkspaceSnapshot snapshot = workspace.AddDocument(addedId,
            "namespace App; int Added() { return 7; }", new DocumentVersion(1));
        var compilation = await snapshot.RootProject.GetCompilationAsync();

        Assert.False(compilation.HasErrors);
        Assert.False(snapshot.GetDocument(addedId).HasPhysicalFile);
        Assert.True(snapshot.GetDocument(addedId).IsUnsaved);
        Assert.Contains(compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Functions,
            function => function.Name == "Added");
    }

    [Fact]
    public void ReloadSaveAsAndUntitledClosePreserveExplicitBackingContracts()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "namespace App; int Main() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        DocumentSnapshot disk = Assert.Single(workspace.CurrentSnapshot.Documents);
        SourceFileId sourceId = disk.SourceFileId;
        File.WriteAllText(disk.PhysicalPath!, "namespace App; int Main() { return 1; }");
        WorkspaceSnapshot reloaded = workspace.ReloadFromDisk(disk.Id, DocumentVersion.Initial);
        Assert.Contains("return 1", reloaded.GetDocument(disk.Id).EffectiveText.Text);
        Assert.Equal(sourceId, reloaded.GetDocument(disk.Id).SourceFileId);
        Assert.Equal(DocumentVersion.Initial, reloaded.GetDocument(disk.Id).Version);
        Assert.Equal(new BackingVersion(1), reloaded.GetDocument(disk.Id).BackingVersion);

        DocumentId untitledId = DocumentId.CreateNew(reloaded.RootProjectId);
        workspace.AddDocument(untitledId, "namespace App; int Extra() { return 2; }",
            new DocumentVersion(1));
        string savedPath = directory.PathOf("App/src/extra.xe");
        WorkspaceSnapshot saved = workspace.SaveDocumentAs(untitledId, savedPath,
            new DocumentVersion(1));
        Assert.Equal(Path.GetFullPath(savedPath), saved.GetDocument(untitledId).PhysicalPath);
        Assert.Equal(saved.GetDocument(untitledId).EffectiveText.Text, File.ReadAllText(savedPath));

        DocumentId discardedId = DocumentId.CreateNew(reloaded.RootProjectId);
        workspace.AddDocument(discardedId, "namespace App; int Discarded() { return 3; }",
            new DocumentVersion(1));
        WorkspaceSnapshot closed = workspace.CloseDocument(discardedId, new DocumentVersion(2));
        Assert.False(closed.TryGetDocument(discardedId, out _));
    }

    [Fact]
    public async Task UpdateProjectChangesReferenceGraphWithoutChangingMembershipOrStableIds()
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
        ProjectSnapshot app = workspace.CurrentSnapshot.Projects.Single(project =>
            project.Configuration.Name == "App");
        ProjectSnapshot library = workspace.CurrentSnapshot.Projects.Single(project =>
            project.Configuration.Name == "Library");
        Assert.True((await app.GetCompilationAsync()).HasErrors);
        XenonProject old = app.Configuration;
        var updated = new XenonProject(old.Name, old.Type, old.Version, old.RootDirectory,
            old.SourceRoot, old.ProjectFilePath, old.SourceFiles, old.NativeLibraries,
            old.NativeLibraryPaths, [library.Configuration.Identity], old.DebugProfile, old.ReleaseProfile);

        WorkspaceSnapshot next = workspace.UpdateProject(app.Id, updated);
        ProjectSnapshot nextApp = next.GetProject(app.Id);

        Assert.False((await nextApp.GetCompilationAsync()).HasErrors);
        Assert.Equal(app.Id, nextApp.Id);
        Assert.Equal(Assert.Single(app.Documents).Id, Assert.Single(nextApp.Documents).Id);
        Assert.Same(next.GetProject(library.Id), Assert.Single(nextApp.ProjectReferences));
    }

    [Fact]
    public void AddDocumentRejectsEquivalentPhysicalPathWithoutChangingWorkspaceOrDisk()
    {
        using var directory = new WorkspaceTestDirectory();
        const string originalText = "namespace App; int Main() { return 1; }";
        directory.WriteProject("App", sources: [("main.xe", originalText)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot before = workspace.CurrentSnapshot;
        DocumentSnapshot existing = Assert.Single(before.Documents);
        string equivalentPath = directory.PathOf("App/src/../src/main.xe");

        Assert.Throws<ProjectSystemException>(() => workspace.AddDocument(
            DocumentId.CreateNew(existing.ProjectId), "namespace App; int Other() { return 2; }",
            new DocumentVersion(1), equivalentPath));

        Assert.Same(before, workspace.CurrentSnapshot);
        Assert.Equal(originalText, File.ReadAllText(existing.PhysicalPath!));
        Assert.Single(workspace.CurrentSnapshot.Documents);
    }

    [Fact]
    public void SaveAsCollisionIsRejectedBeforeWriteAndKeepsUntitledState()
    {
        using var directory = new WorkspaceTestDirectory();
        const string originalText = "namespace App; int Main() { return 1; }";
        directory.WriteProject("App", sources: [("main.xe", originalText)]);
        var fileSystem = new ObservingWorkspaceFileSystem();
        using Workspace workspace = directory.CreateWorkspace(fileSystem);
        DocumentSnapshot existing = Assert.Single(workspace.CurrentSnapshot.Documents);
        DocumentId untitledId = DocumentId.CreateNew(existing.ProjectId);
        WorkspaceSnapshot before = workspace.AddDocument(untitledId,
            "namespace App; int Other() { return 2; }", new DocumentVersion(1));
        string equivalentPath = directory.PathOf("App/src/../src/main.xe");

        Assert.Throws<ProjectSystemException>(() => workspace.SaveDocumentAs(
            untitledId, equivalentPath, new DocumentVersion(1)));

        Assert.Same(before, workspace.CurrentSnapshot);
        Assert.Equal(0, fileSystem.WriteCount);
        Assert.Equal(originalText, File.ReadAllText(existing.PhysicalPath!));
        DocumentSnapshot untitled = workspace.CurrentSnapshot.GetDocument(untitledId);
        Assert.Null(untitled.PhysicalPath);
        Assert.Equal(new DocumentVersion(1), untitled.Version);
        Assert.True(untitled.IsUnsaved);
    }

    [Fact]
    public void UpdateProjectRejectsDuplicateNormalizedPhysicalSourcesBeforePublication()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe",
            "namespace App; int Main() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot before = workspace.CurrentSnapshot;
        ProjectSnapshot project = before.RootProject;
        XenonProject old = project.Configuration;
        string main = Assert.Single(old.SourceFiles);
        string equivalent = directory.PathOf("App/src/../src/main.xe");
        var duplicate = new XenonProject(old.Name, old.Type, old.Version, old.RootDirectory,
            old.SourceRoot, old.ProjectFilePath, [main, equivalent], old.NativeLibraries,
            old.NativeLibraryPaths, old.ProjectReferences, old.DebugProfile, old.ReleaseProfile);

        Assert.Throws<ProjectSystemException>(() => workspace.UpdateProject(project.Id, duplicate));
        Assert.Same(before, workspace.CurrentSnapshot);
        Assert.Single(workspace.CurrentSnapshot.RootProject.Documents);
    }

    [Fact]
    public void SaveCancellationBeforeCommitDoesNotWriteOrPublish()
    {
        using var directory = new WorkspaceTestDirectory();
        const string diskText = "namespace App; int Main() { return 0; }";
        const string overlayText = "namespace App; int Main() { return 1; }";
        directory.WriteProject("App", sources: [("main.xe", diskText)]);
        var fileSystem = new ObservingWorkspaceFileSystem();
        using Workspace workspace = directory.CreateWorkspace(fileSystem);
        DocumentSnapshot document = Assert.Single(workspace.CurrentSnapshot.Documents);
        WorkspaceSnapshot before = workspace.OpenDocument(document.Id, overlayText,
            new DocumentVersion(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => workspace.SaveDocument(document.Id,
            new DocumentVersion(1), cancellation.Token));

        Assert.Same(before, workspace.CurrentSnapshot);
        Assert.Equal(0, fileSystem.WriteCount);
        Assert.Equal(diskText, File.ReadAllText(document.PhysicalPath!));
        Assert.True(workspace.CurrentSnapshot.GetDocument(document.Id).IsUnsaved);
    }

    [Fact]
    public void SaveCancellationAfterCandidatePreparationDoesNotWriteOrPublish()
    {
        using var directory = new WorkspaceTestDirectory();
        const string diskText = "namespace App; int Main() { return 0; }";
        const string overlayText = "namespace App; int Main() { return 1; }";
        directory.WriteProject("App", sources: [("main.xe", diskText)]);
        using var cancellation = new CancellationTokenSource();
        var observer = new CallbackSaveObserver(cancellation.Cancel);
        var fileSystem = new ObservingWorkspaceFileSystem();
        using Workspace workspace = directory.CreateWorkspace(fileSystem, observer);
        DocumentSnapshot document = Assert.Single(workspace.CurrentSnapshot.Documents);
        WorkspaceSnapshot before = workspace.OpenDocument(document.Id, overlayText,
            new DocumentVersion(1));

        Assert.Throws<OperationCanceledException>(() => workspace.SaveDocument(document.Id,
            new DocumentVersion(1), cancellation.Token));

        Assert.Same(before, workspace.CurrentSnapshot);
        Assert.Equal(1, observer.CandidateCount);
        Assert.Equal(0, fileSystem.WriteCount);
        Assert.Equal(diskText, File.ReadAllText(document.PhysicalPath!));
    }

    [Fact]
    public void SaveCancellationAfterDiskCommitStillPublishesMatchingSnapshot()
    {
        using var directory = new WorkspaceTestDirectory();
        const string overlayText = "namespace App; int Main() { return 1; }";
        directory.WriteProject("App", sources: [("main.xe",
            "namespace App; int Main() { return 0; }")]);
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new ObservingWorkspaceFileSystem { AfterWrite = cancellation.Cancel };
        using Workspace workspace = directory.CreateWorkspace(fileSystem);
        DocumentSnapshot document = Assert.Single(workspace.CurrentSnapshot.Documents);
        WorkspaceSnapshot before = workspace.OpenDocument(document.Id, overlayText,
            new DocumentVersion(1));

        WorkspaceSnapshot saved = workspace.SaveDocument(document.Id,
            new DocumentVersion(1), cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.NotSame(before, saved);
        Assert.Same(saved, workspace.CurrentSnapshot);
        Assert.Equal(overlayText, File.ReadAllText(document.PhysicalPath!));
        Assert.Equal(overlayText, saved.GetDocument(document.Id).DiskText!.Text);
        Assert.False(saved.GetDocument(document.Id).IsUnsaved);
    }

    [Fact]
    public void SaveWriteFailureDoesNotPublishCandidateSnapshot()
    {
        using var directory = new WorkspaceTestDirectory();
        const string diskText = "namespace App; int Main() { return 0; }";
        directory.WriteProject("App", sources: [("main.xe", diskText)]);
        var fileSystem = new ObservingWorkspaceFileSystem
        {
            BeforeWrite = () => throw new IOException("injected write failure"),
        };
        using Workspace workspace = directory.CreateWorkspace(fileSystem);
        DocumentSnapshot document = Assert.Single(workspace.CurrentSnapshot.Documents);
        WorkspaceSnapshot before = workspace.OpenDocument(document.Id,
            "namespace App; int Main() { return 1; }", new DocumentVersion(1));

        Assert.Throws<IOException>(() => workspace.SaveDocument(document.Id,
            new DocumentVersion(1)));

        Assert.Same(before, workspace.CurrentSnapshot);
        Assert.Equal(diskText, File.ReadAllText(document.PhysicalPath!));
        Assert.True(workspace.CurrentSnapshot.GetDocument(document.Id).IsUnsaved);
    }

    [Fact]
    public void SaveAsCancellationBeforeCommitAndAfterCommitObeyOneCommitBoundary()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe",
            "namespace App; int Main() { return 0; }")]);
        using var preCommitCancellation = new CancellationTokenSource();
        var observer = new CallbackSaveObserver(preCommitCancellation.Cancel);
        var fileSystem = new ObservingWorkspaceFileSystem();
        using Workspace workspace = directory.CreateWorkspace(fileSystem, observer);
        ProjectId projectId = workspace.CurrentSnapshot.RootProjectId;
        DocumentId firstId = DocumentId.CreateNew(projectId);
        WorkspaceSnapshot before = workspace.AddDocument(firstId,
            "namespace App; int First() { return 1; }", new DocumentVersion(1));
        string firstPath = directory.PathOf("App/src/first.xe");

        Assert.Throws<OperationCanceledException>(() => workspace.SaveDocumentAs(firstId,
            firstPath, new DocumentVersion(1), preCommitCancellation.Token));
        Assert.Same(before, workspace.CurrentSnapshot);
        Assert.False(File.Exists(firstPath));
        Assert.Equal(0, fileSystem.WriteCount);

        DocumentId secondId = DocumentId.CreateNew(projectId);
        workspace.AddDocument(secondId, "namespace App; int Second() { return 2; }",
            new DocumentVersion(1));
        string secondPath = directory.PathOf("App/src/second.xe");
        using var postCommitCancellation = new CancellationTokenSource();
        observer.Callback = static () => { };
        fileSystem.AfterWrite = postCommitCancellation.Cancel;

        WorkspaceSnapshot saved = workspace.SaveDocumentAs(secondId, secondPath,
            new DocumentVersion(1), postCommitCancellation.Token);

        Assert.True(postCommitCancellation.IsCancellationRequested);
        Assert.Same(saved, workspace.CurrentSnapshot);
        Assert.Equal(File.ReadAllText(secondPath), saved.GetDocument(secondId).DiskText!.Text);
        Assert.Equal(Path.GetFullPath(secondPath), saved.GetDocument(secondId).PhysicalPath);
        Assert.False(saved.GetDocument(secondId).IsUnsaved);
    }

    [Fact]
    public void SaveSynchronizesClosedSharedBackingAndPreservesOldSnapshot()
    {
        using var directory = new WorkspaceTestDirectory();
        const string oldText = "namespace Shared; int Value() { return 1; }";
        const string newText = "namespace Shared; int Value() { return 2; }";
        string sharedPath = directory.PathOf("shared/common.xe");
        directory.Write("shared/common.xe", oldText);
        directory.Write("other/only.xe", "namespace Other; int Only() { return 3; }");
        XenonProject first = CreateProject(directory, "One", sharedPath);
        XenonProject second = CreateProject(directory, "Two", sharedPath);
        XenonProject unrelated = CreateProject(directory, "Other", directory.PathOf("other/only.xe"));
        using Workspace workspace = Workspace.Create(
            XenonProjectGraph.Create(first, [first, second, unrelated]));
        WorkspaceSnapshot original = workspace.CurrentSnapshot;
        DocumentSnapshot firstDocument = GetDocument(original, "One");
        DocumentSnapshot secondDocument = GetDocument(original, "Two");
        ProjectSnapshot originalUnrelated = original.Projects.Single(project =>
            project.Configuration.Name == "Other");
        SourceFileId secondSourceId = secondDocument.SourceFileId;

        workspace.OpenDocument(firstDocument.Id, newText, new DocumentVersion(1));
        WorkspaceSnapshot saved = workspace.SaveDocument(firstDocument.Id, new DocumentVersion(1));
        DocumentSnapshot synchronized = saved.GetDocument(secondDocument.Id);

        Assert.Equal(newText, synchronized.DiskText!.Text);
        Assert.Equal(newText, synchronized.EffectiveText.Text);
        Assert.False(synchronized.IsOpen);
        Assert.Equal(secondSourceId, synchronized.SourceFileId);
        Assert.Equal(oldText, original.GetDocument(secondDocument.Id).DiskText!.Text);
        Assert.Equal(oldText, original.GetDocument(secondDocument.Id).EffectiveText.Text);
        Assert.Same(originalUnrelated, saved.GetProject(originalUnrelated.Id));
    }

    [Fact]
    public void SharedBackingSavePreservesDifferentOpenOverlay()
    {
        using var directory = new WorkspaceTestDirectory();
        const string oldText = "namespace Shared; int Value() { return 1; }";
        const string savedText = "namespace Shared; int Value() { return 2; }";
        const string otherOverlay = "namespace Shared; int Value() { return 3; }";
        string sharedPath = directory.PathOf("shared/common.xe");
        directory.Write("shared/common.xe", oldText);
        XenonProject first = CreateProject(directory, "One", sharedPath);
        XenonProject second = CreateProject(directory, "Two", sharedPath);
        using Workspace workspace = Workspace.Create(XenonProjectGraph.Create(first, [first, second]));
        DocumentSnapshot firstDocument = GetDocument(workspace.CurrentSnapshot, "One");
        DocumentSnapshot secondDocument = GetDocument(workspace.CurrentSnapshot, "Two");

        workspace.OpenDocument(secondDocument.Id, otherOverlay, new DocumentVersion(1));
        workspace.OpenDocument(firstDocument.Id, savedText, new DocumentVersion(1));
        WorkspaceSnapshot saved = workspace.SaveDocument(firstDocument.Id, new DocumentVersion(1));
        DocumentSnapshot synchronized = saved.GetDocument(secondDocument.Id);

        Assert.Equal(savedText, synchronized.DiskText!.Text);
        Assert.Equal(otherOverlay, synchronized.OverlayText!.Text);
        Assert.Equal(otherOverlay, synchronized.EffectiveText.Text);
        Assert.True(synchronized.IsUnsaved);
    }

    [Fact]
    public void SharedBackingUpdateDoesNotConsumeEditorDocumentVersion()
    {
        using var directory = new WorkspaceTestDirectory();
        const string oldText = "namespace Shared; int Value() { return 1; }";
        const string savedText = "namespace Shared; int Value() { return 2; }";
        const string editorText = "namespace Shared; int Value() { return 3; }";
        string sharedPath = directory.PathOf("shared/common.xe");
        directory.Write("shared/common.xe", oldText);
        XenonProject first = CreateProject(directory, "One", sharedPath);
        XenonProject second = CreateProject(directory, "Two", sharedPath);
        using Workspace workspace = Workspace.Create(XenonProjectGraph.Create(first, [first, second]));
        DocumentSnapshot firstDocument = GetDocument(workspace.CurrentSnapshot, "One");
        DocumentSnapshot secondDocument = GetDocument(workspace.CurrentSnapshot, "Two");

        workspace.OpenDocument(secondDocument.Id, editorText, new DocumentVersion(10));
        workspace.OpenDocument(firstDocument.Id, savedText, new DocumentVersion(1));
        WorkspaceSnapshot saved = workspace.SaveDocument(firstDocument.Id, new DocumentVersion(1));
        DocumentSnapshot synchronized = saved.GetDocument(secondDocument.Id);

        Assert.Equal(new DocumentVersion(10), synchronized.Version);
        Assert.Equal(new BackingVersion(1), synchronized.BackingVersion);
        Assert.Equal(new DocumentVersion(1), saved.GetDocument(firstDocument.Id).Version);
        Assert.Equal(new BackingVersion(1), saved.GetDocument(firstDocument.Id).BackingVersion);

        int valuePosition = synchronized.EffectiveText.Text.IndexOf('3');
        WorkspaceSnapshot edited = workspace.ApplyDocumentChanges(secondDocument.Id,
            new DocumentVersion(10), new DocumentVersion(11),
            [new DocumentTextChange(new TextSpan(valuePosition, 1), "4")]);

        Assert.Equal(new DocumentVersion(11), edited.GetDocument(secondDocument.Id).Version);
        Assert.Contains("return 4", edited.GetDocument(secondDocument.Id).EffectiveText.Text);
        Assert.Equal(savedText, edited.GetDocument(secondDocument.Id).DiskText!.Text);
    }

    [Fact]
    public void SharedBackingSaveMakesOldDiskOverlayUnsaved()
    {
        using var directory = new WorkspaceTestDirectory();
        const string oldText = "namespace Shared; int Value() { return 1; }";
        const string savedText = "namespace Shared; int Value() { return 2; }";
        string sharedPath = directory.PathOf("shared/common.xe");
        directory.Write("shared/common.xe", oldText);
        XenonProject first = CreateProject(directory, "One", sharedPath);
        XenonProject second = CreateProject(directory, "Two", sharedPath);
        using Workspace workspace = Workspace.Create(XenonProjectGraph.Create(first, [first, second]));
        DocumentSnapshot firstDocument = GetDocument(workspace.CurrentSnapshot, "One");
        DocumentSnapshot secondDocument = GetDocument(workspace.CurrentSnapshot, "Two");

        workspace.OpenDocument(secondDocument.Id, oldText, new DocumentVersion(1));
        Assert.False(workspace.CurrentSnapshot.GetDocument(secondDocument.Id).IsUnsaved);
        workspace.OpenDocument(firstDocument.Id, savedText, new DocumentVersion(1));
        WorkspaceSnapshot saved = workspace.SaveDocument(firstDocument.Id, new DocumentVersion(1));

        Assert.True(saved.GetDocument(secondDocument.Id).IsUnsaved);
        Assert.Equal(oldText, saved.GetDocument(secondDocument.Id).EffectiveText.Text);
        Assert.Equal(savedText, saved.GetDocument(secondDocument.Id).DiskText!.Text);
    }

    [Fact]
    public void SaveSynchronizesAllSharedProjectContexts()
    {
        using var directory = new WorkspaceTestDirectory();
        const string savedText = "namespace Shared; int Value() { return 2; }";
        string sharedPath = directory.PathOf("shared/common.xe");
        directory.Write("shared/common.xe", "namespace Shared; int Value() { return 1; }");
        XenonProject first = CreateProject(directory, "One", sharedPath);
        XenonProject second = CreateProject(directory, "Two", sharedPath);
        XenonProject third = CreateProject(directory, "Three", sharedPath);
        using Workspace workspace = Workspace.Create(
            XenonProjectGraph.Create(first, [first, second, third]));
        DocumentSnapshot firstDocument = GetDocument(workspace.CurrentSnapshot, "One");

        workspace.OpenDocument(firstDocument.Id, savedText, new DocumentVersion(1));
        WorkspaceSnapshot saved = workspace.SaveDocument(firstDocument.Id, new DocumentVersion(1));

        Assert.Equal(3, saved.Documents.Count(document => document.DiskText!.Text == savedText));
        Assert.Equal(3, saved.Documents.Select(document => document.SourceFileId).Distinct().Count());
    }

    [Fact]
    public void SaveAsIntoCrossProjectSharedPathSynchronizesBacking()
    {
        using var directory = new WorkspaceTestDirectory();
        const string committedText = "namespace Shared; int Saved() { return 2; }";
        string sharedPath = directory.PathOf("shared/common.xe");
        directory.Write("shared/common.xe", "namespace Shared; int Old() { return 1; }");
        directory.Write("app/main.xe", "namespace App; int Main() { return 0; }");
        XenonProject app = CreateProject(directory, "App", directory.PathOf("app/main.xe"));
        XenonProject library = CreateProject(directory, "Library", sharedPath);
        using Workspace workspace = Workspace.Create(XenonProjectGraph.Create(app, [app, library]));
        DocumentSnapshot libraryDocument = GetDocument(workspace.CurrentSnapshot, "Library");
        DocumentId untitledId = DocumentId.CreateNew(workspace.CurrentSnapshot.RootProjectId);
        workspace.AddDocument(untitledId, committedText, new DocumentVersion(1));

        WorkspaceSnapshot saved = workspace.SaveDocumentAs(untitledId, sharedPath,
            new DocumentVersion(1));

        Assert.Equal(committedText, saved.GetDocument(untitledId).DiskText!.Text);
        Assert.Equal(committedText, saved.GetDocument(libraryDocument.Id).DiskText!.Text);
        Assert.Equal(committedText, saved.GetDocument(libraryDocument.Id).EffectiveText.Text);
        Assert.NotEqual(saved.GetDocument(untitledId).SourceFileId,
            saved.GetDocument(libraryDocument.Id).SourceFileId);
    }

    [Fact]
    public void ReloadFromDiskSynchronizesEverySharedProjectContext()
    {
        using var directory = new WorkspaceTestDirectory();
        const string externalText = "namespace Shared; int Value() { return 4; }";
        string sharedPath = directory.PathOf("shared/common.xe");
        directory.Write("shared/common.xe", "namespace Shared; int Value() { return 1; }");
        XenonProject first = CreateProject(directory, "One", sharedPath);
        XenonProject second = CreateProject(directory, "Two", sharedPath);
        using Workspace workspace = Workspace.Create(XenonProjectGraph.Create(first, [first, second]));
        WorkspaceSnapshot original = workspace.CurrentSnapshot;
        DocumentSnapshot firstDocument = GetDocument(original, "One");
        DocumentSnapshot secondDocument = GetDocument(original, "Two");
        File.WriteAllText(sharedPath, externalText);

        WorkspaceSnapshot reloaded = workspace.ReloadFromDisk(firstDocument.Id,
            DocumentVersion.Initial);

        Assert.Equal(externalText, reloaded.GetDocument(firstDocument.Id).DiskText!.Text);
        Assert.Equal(externalText, reloaded.GetDocument(secondDocument.Id).DiskText!.Text);
        Assert.Equal(externalText, reloaded.GetDocument(secondDocument.Id).EffectiveText.Text);
        Assert.NotEqual(firstDocument.SourceFileId, secondDocument.SourceFileId);
        Assert.Equal("namespace Shared; int Value() { return 1; }",
            original.GetDocument(secondDocument.Id).DiskText!.Text);
    }

    [Fact]
    public void WindowsPathCasingUsesOnePhysicalOwnershipIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("Main.xe",
            "namespace App; int Main() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot before = workspace.CurrentSnapshot;
        DocumentSnapshot existing = Assert.Single(before.Documents);
        string differentlyCasedPath = directory.PathOf("app/SRC/main.XE");

        Assert.Throws<ProjectSystemException>(() => workspace.AddDocument(
            DocumentId.CreateNew(existing.ProjectId), "namespace App; int Other() { return 1; }",
            new DocumentVersion(1), differentlyCasedPath));

        Assert.Same(before, workspace.CurrentSnapshot);
    }

    [Fact]
    public void UnixCaseSensitiveFilesystemAllowsDistinctSourceCasing()
    {
        if (OperatingSystem.IsWindows()) return;
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources:
        [
            ("Main.xe", "namespace App; int Upper() { return 1; }"),
            ("main.xe", "namespace App; int Lower() { return 2; }"),
        ]);
        string sourceRoot = directory.PathOf("App/src");
        if (Directory.EnumerateFiles(sourceRoot, "*.xe").Select(Path.GetFileName)
            .Distinct(StringComparer.Ordinal).Count() != 2)
            return;

        using Workspace workspace = directory.CreateWorkspace();

        Assert.Equal(2, workspace.CurrentSnapshot.Documents.Length);
        Assert.Equal(2, workspace.CurrentSnapshot.Documents.Select(document => document.PhysicalPath)
            .Distinct(StringComparer.Ordinal).Count());
    }

    private static XenonProject CreateProject(WorkspaceTestDirectory directory, string name, string source) =>
        new(name, XenonProjectType.StaticLibrary, null, directory.Root, directory.Root,
            directory.PathOf($"{name}.xeproj"), [source], [], [], [],
            XenonBuildProfile.Debug, XenonBuildProfile.Release);

    private static DocumentSnapshot GetDocument(WorkspaceSnapshot snapshot, string projectName) =>
        Assert.Single(snapshot.Projects.Single(project => project.Configuration.Name == projectName)
            .Documents);

    private sealed class ObservingWorkspaceFileSystem : IWorkspaceFileSystem
    {
        public Action? BeforeWrite { get; set; }
        public Action? AfterWrite { get; set; }
        public int WriteCount { get; private set; }

        public bool FileExists(string path) => File.Exists(path);
        public string ReadAllText(string path) => File.ReadAllText(path);

        public void WriteAllText(string path, string text)
        {
            BeforeWrite?.Invoke();
            File.WriteAllText(path, text);
            WriteCount++;
            AfterWrite?.Invoke();
        }
    }

    private sealed class CallbackSaveObserver(Action callback) : IWorkspaceSaveObserver
    {
        public Action Callback { get; set; } = callback;
        public int CandidateCount { get; private set; }

        public void CandidatePrepared()
        {
            CandidateCount++;
            Callback();
        }
    }
}
