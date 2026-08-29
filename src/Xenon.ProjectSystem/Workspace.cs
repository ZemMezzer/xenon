using System.Collections.Immutable;
using Xenon.Compiler;
using Xenon.Compiler.Text;

namespace Xenon.ProjectSystem;

/// <summary>
/// Editor-facing state controller. Updates are serialized, but all analysis reads are lock-free
/// against an atomically captured immutable WorkspaceSnapshot.
/// </summary>
public sealed class Workspace : IDisposable
{
    private readonly object _updateGate = new();
    private readonly string _profileName;
    private readonly IWorkspaceFileSystem _fileSystem;
    private readonly IWorkspaceSaveObserver _saveObserver;
    private WorkspaceSnapshot _currentSnapshot;
    private CancellationTokenSource _staleCancellation = new();
    private bool _disposed;

    private Workspace(WorkspaceSnapshot initialSnapshot, string profileName,
        IWorkspaceFileSystem fileSystem, IWorkspaceSaveObserver saveObserver,
        WorkspaceConfiguration? configuration = null)
    {
        _currentSnapshot = initialSnapshot;
        _profileName = profileName;
        _fileSystem = fileSystem;
        _saveObserver = saveObserver;
        Configuration = configuration;
    }

    public WorkspaceSnapshot CurrentSnapshot => Volatile.Read(ref _currentSnapshot);
    public WorkspaceId Id => CurrentSnapshot.Id;
    public WorkspaceConfiguration? Configuration { get; }

    public static Workspace Create(string inputPath, string profileName = "debug",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (string.Equals(Path.GetExtension(inputPath), ".xws", StringComparison.OrdinalIgnoreCase))
            return Create(XenonWorkspaceLoader.Load(inputPath), profileName, cancellationToken);
        return Create(XenonProjectGraph.Load(inputPath), profileName, cancellationToken);
    }

    public static Workspace Create(WorkspaceConfiguration configuration,
        string profileName = "debug", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return CreateCore(configuration.Graph, configuration.Id, profileName,
            cancellationToken, PhysicalWorkspaceFileSystem.Instance,
            NullWorkspaceSaveObserver.Instance, configuration);
    }

    public static Workspace Create(XenonProjectGraph graph, string profileName = "debug",
        CancellationToken cancellationToken = default)
        => CreateCore(graph, WorkspaceId.CreateNew(), profileName, cancellationToken,
            PhysicalWorkspaceFileSystem.Instance, NullWorkspaceSaveObserver.Instance, null);

    internal static Workspace Create(XenonProjectGraph graph,
        IWorkspaceFileSystem fileSystem, IWorkspaceSaveObserver? saveObserver = null,
        string profileName = "debug", CancellationToken cancellationToken = default) =>
        CreateCore(graph, WorkspaceId.CreateNew(), profileName, cancellationToken,
            fileSystem, saveObserver ?? NullWorkspaceSaveObserver.Instance, null);

    private static Workspace CreateCore(XenonProjectGraph graph, WorkspaceId workspaceId,
        string profileName, CancellationToken cancellationToken,
        IWorkspaceFileSystem fileSystem, IWorkspaceSaveObserver saveObserver,
        WorkspaceConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(saveObserver);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        var idsByIdentity = graph.Projects.ToImmutableDictionary(project => project.Identity,
            _ => ProjectId.CreateNew(), ProjectPath.Comparer);
        var documents = ImmutableDictionary.CreateBuilder<ProjectId, ImmutableArray<DocumentSnapshot>>();
        foreach (XenonProject project in graph.Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = project.GetProfile(profileName);
            ProjectId projectId = idsByIdentity[project.Identity];
            var paths = new HashSet<string>(PhysicalPathComparer);
            var projectDocuments = ImmutableArray.CreateBuilder<DocumentSnapshot>();
            foreach (string path in project.SourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = NormalizePhysicalPath(path);
                if (!paths.Add(normalized))
                    throw new ProjectSystemException($"project '{project.Name}' contains duplicate source '{normalized}'");
                SourceText source = SourceText.From(fileSystem.ReadAllText(normalized), normalized);
                projectDocuments.Add(new DocumentSnapshot(DocumentId.CreateNew(projectId), normalized,
                    source, null, DocumentVersion.Initial, cancellationToken: cancellationToken));
            }
            documents[projectId] = projectDocuments.ToImmutable();
        }

        WorkspaceSnapshot snapshot = BuildInitialSnapshot(workspaceId, graph, idsByIdentity,
            documents.ToImmutable(), profileName, cancellationToken);
        return new Workspace(snapshot, profileName, fileSystem, saveObserver, configuration);
    }

    public WorkspaceSnapshot OpenDocument(DocumentId documentId, string editorText,
        DocumentVersion version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(editorText);
        return UpdateDocument(documentId, document =>
        {
            SourceText overlay = document.EffectiveText.WithText(editorText);
            return document.WithEditorState(document.DiskText, overlay, version, cancellationToken);
        });
    }

    public WorkspaceSnapshot ApplyDocumentChanges(DocumentId documentId,
        DocumentVersion expectedVersion, DocumentVersion newVersion,
        ImmutableArray<DocumentTextChange> changes, CancellationToken cancellationToken = default) =>
        UpdateDocument(documentId, document =>
    {
        if (!document.IsOpen)
            throw new InvalidOperationException($"Document '{documentId}' must be open before applying editor changes.");
        return document.ApplyChanges(expectedVersion, newVersion, changes, cancellationToken);
    });

    public WorkspaceSnapshot CloseDocument(DocumentId documentId, DocumentVersion version,
        CancellationToken cancellationToken = default)
    {
        lock (_updateGate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceSnapshot current = _currentSnapshot;
            DocumentSnapshot document = current.GetDocument(documentId);
            if (!document.IsOpen)
                throw new InvalidOperationException($"Document '{documentId}' is not open.");
            if (version <= document.Version)
                throw new StaleDocumentVersionException(document.Id, document.Version, version);
            if (document.DiskText is null)
                return RemoveDocumentCore(current, documentId);
            var change = document.WithEditorState(document.DiskText, null, version, cancellationToken);
            return PublishDocumentChange(current, change.Snapshot, change.Kind);
        }
    }

    public WorkspaceSnapshot SaveDocument(DocumentId documentId, DocumentVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        lock (_updateGate)
        {
            ThrowIfDisposed();
            WorkspaceSnapshot current = _currentSnapshot;
            DocumentSnapshot document = current.GetDocument(documentId);
            if (!document.IsOpen)
                throw new InvalidOperationException($"Document '{documentId}' is not open.");
            if (document.PhysicalPath is null)
                throw new InvalidOperationException("An untitled document requires SaveDocumentAs.");
            EnsureCurrentVersion(document, expectedVersion);
            cancellationToken.ThrowIfCancellationRequested();
            SourceText disk = document.EffectiveText.WithPath(document.PhysicalPath);
            var change = document.WithBackingState(disk, document.OverlayText, cancellationToken);
            WorkspaceSnapshot candidate = BuildSynchronizedBackingSnapshot(current, change.Snapshot,
                change.Kind, document.PhysicalPath, document.EffectiveText.Text, cancellationToken);
            return CommitSave(candidate, document.PhysicalPath, document.EffectiveText.Text,
                cancellationToken);
        }
    }

    public WorkspaceSnapshot SaveDocumentAs(DocumentId documentId, string physicalPath,
        DocumentVersion expectedVersion, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(physicalPath);
        lock (_updateGate)
        {
            ThrowIfDisposed();
            WorkspaceSnapshot current = _currentSnapshot;
            DocumentSnapshot document = current.GetDocument(documentId);
            if (!document.IsOpen)
                throw new InvalidOperationException($"Document '{documentId}' is not open.");
            EnsureCurrentVersion(document, expectedVersion);
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = NormalizePhysicalPath(physicalPath);
            EnsurePhysicalPathAvailable(current.GetProject(document.ProjectId), document.Id, normalized);
            SourceText effective = document.EffectiveText.WithPath(normalized);
            var replacement = new DocumentSnapshot(document.Id, normalized, effective, effective,
                document.Version, cancellationToken: cancellationToken,
                backingVersion: new BackingVersion(checked(document.BackingVersion.Value + 1)));
            WorkspaceSnapshot candidate = BuildSynchronizedBackingSnapshot(current, replacement,
                DocumentChangeKind.Declaration, normalized, document.EffectiveText.Text,
                cancellationToken);
            return CommitSave(candidate, normalized, document.EffectiveText.Text,
                cancellationToken);
        }
    }

    public WorkspaceSnapshot ReloadFromDisk(DocumentId documentId, DocumentVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        lock (_updateGate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceSnapshot current = _currentSnapshot;
            DocumentSnapshot document = current.GetDocument(documentId);
            if (document.PhysicalPath is null)
                throw new InvalidOperationException("An untitled document has no disk source to reload.");
            SourceText disk = SourceText.From(_fileSystem.ReadAllText(document.PhysicalPath),
                document.PhysicalPath, document.SourceFileId);
            EnsureCurrentVersion(document, expectedVersion);
            var change = document.WithBackingState(disk, document.OverlayText, cancellationToken);
            WorkspaceSnapshot snapshot = BuildSynchronizedBackingSnapshot(current,
                change.Snapshot, change.Kind, document.PhysicalPath, disk.Text, cancellationToken);
            Publish(snapshot);
            return snapshot;
        }
    }

    /// <summary>Adds a new logical document. Remove and re-add uses a caller-created new ID.</summary>
    public WorkspaceSnapshot AddDocument(DocumentId documentId, string text,
        DocumentVersion version, string? physicalPath = null, bool isOpen = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_updateGate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceSnapshot current = _currentSnapshot;
            if (current.TryGetDocument(documentId, out _))
                throw new ArgumentException($"Document '{documentId}' already exists.", nameof(documentId));
            ProjectSnapshot project = current.GetProject(documentId.ProjectId);
            string sourcePath = physicalPath is null ? $"<untitled:{documentId.Value:D}>" :
                NormalizePhysicalPath(physicalPath);
            if (physicalPath is not null)
                EnsurePhysicalPathAvailable(project, documentId, sourcePath);
            SourceFileId sourceId = SourceFileId.CreateNew();
            SourceText? disk = null;
            if (physicalPath is not null && _fileSystem.FileExists(sourcePath))
                disk = SourceText.From(_fileSystem.ReadAllText(sourcePath), sourcePath, sourceId);
            SourceText supplied = SourceText.From(text, sourcePath, sourceId);
            if (!isOpen && disk is null) disk = supplied;
            SourceText? overlay = isOpen ? supplied : null;
            var document = new DocumentSnapshot(documentId, physicalPath is null ? null : sourcePath,
                disk, overlay, version, cancellationToken: cancellationToken);
            var documents = project.Documents.Add(document);
            return PublishProjectDocumentSet(current, project.Id, documents,
                DocumentChangeKind.Declaration, [document.Id]);
        }
    }

    public WorkspaceSnapshot RemoveDocument(DocumentId documentId)
    {
        lock (_updateGate)
        {
            ThrowIfDisposed();
            return RemoveDocumentCore(_currentSnapshot, documentId);
        }
    }

    public WorkspaceSnapshot UpdateProject(ProjectId projectId, XenonProject configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        lock (_updateGate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            WorkspaceSnapshot current = _currentSnapshot;
            ProjectSnapshot project = current.GetProject(projectId);
            if (!string.Equals(project.Configuration.Identity, configuration.Identity,
                ProjectPath.Comparison))
                throw new ArgumentException("UpdateProject must preserve the logical project identity.", nameof(configuration));
            _ = configuration.GetProfile(_profileName);
            var configuredPaths = ImmutableArray.CreateBuilder<string>(configuration.SourceFiles.Length);
            var uniqueConfiguredPaths = new HashSet<string>(PhysicalPathComparer);
            foreach (string configuredPath in configuration.SourceFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = NormalizePhysicalPath(configuredPath);
                if (!uniqueConfiguredPaths.Add(normalized))
                    throw new ProjectSystemException(
                        $"project '{configuration.Name}' contains duplicate physical source '{normalized}'");
                configuredPaths.Add(normalized);
            }
            var existing = project.Documents.Where(document => document.PhysicalPath is not null)
                .ToDictionary(document => NormalizePhysicalPath(document.PhysicalPath!),
                    PhysicalPathComparer);
            var documents = ImmutableArray.CreateBuilder<DocumentSnapshot>();
            var changed = ImmutableHashSet.CreateBuilder<DocumentId>();
            foreach (string path in configuredPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existing.Remove(path, out DocumentSnapshot? document)) documents.Add(document);
                else
                {
                    SourceText source = SourceText.From(_fileSystem.ReadAllText(path), path);
                    var added = new DocumentSnapshot(DocumentId.CreateNew(projectId), path, source,
                        null, DocumentVersion.Initial, cancellationToken: cancellationToken);
                    documents.Add(added);
                    changed.Add(added.Id);
                }
            }
            foreach (DocumentSnapshot untitled in project.Documents.Where(document => document.PhysicalPath is null))
                documents.Add(untitled);
            foreach (DocumentSnapshot removed in existing.Values) changed.Add(removed.Id);
            return RebuildAndPublish(current,
                new Dictionary<ProjectId, ProjectMutation>
                {
                    [projectId] = new(configuration, documents.ToImmutable(),
                        DocumentChangeKind.Declaration, changed.ToImmutable(), ConfigurationChanged: true),
                });
        }
    }

    public WorkspaceAnalysisRequest CreateAnalysisRequest(bool staleSensitive = true,
        CancellationToken cancellationToken = default)
    {
        lock (_updateGate)
        {
            ThrowIfDisposed();
            return new WorkspaceAnalysisRequest(_currentSnapshot, cancellationToken,
                _staleCancellation.Token, staleSensitive);
        }
    }

    public void Dispose()
    {
        lock (_updateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _staleCancellation.Cancel();
            _staleCancellation.Dispose();
        }
    }

    private WorkspaceSnapshot UpdateDocument(DocumentId documentId,
        Func<DocumentSnapshot, (DocumentSnapshot Snapshot, DocumentChangeKind Kind)> update)
    {
        lock (_updateGate)
        {
            ThrowIfDisposed();
            WorkspaceSnapshot current = _currentSnapshot;
            var change = update(current.GetDocument(documentId));
            return PublishDocumentChange(current, change.Snapshot, change.Kind);
        }
    }

    private WorkspaceSnapshot PublishDocumentChange(WorkspaceSnapshot current,
        DocumentSnapshot document, DocumentChangeKind kind)
    {
        WorkspaceSnapshot snapshot = BuildDocumentChange(current, document, kind);
        Publish(snapshot);
        return snapshot;
    }

    private WorkspaceSnapshot BuildDocumentChange(WorkspaceSnapshot current,
        DocumentSnapshot document, DocumentChangeKind kind)
    {
        ProjectSnapshot project = current.GetProject(document.ProjectId);
        ImmutableArray<DocumentSnapshot> documents = project.Documents
            .Select(item => item.Id == document.Id ? document : item).ToImmutableArray();
        return BuildProjectDocumentSet(current, project.Id, documents, kind, [document.Id]);
    }

    private WorkspaceSnapshot BuildSynchronizedBackingSnapshot(WorkspaceSnapshot current,
        DocumentSnapshot savedDocument, DocumentChangeKind savedKind, string committedPath,
        string committedText, CancellationToken cancellationToken)
    {
        string normalizedPath = NormalizePhysicalPath(committedPath);
        var replacements = new Dictionary<DocumentId,
            (DocumentSnapshot Document, DocumentChangeKind Kind)>
        {
            [savedDocument.Id] = (savedDocument, savedKind),
        };

        foreach (DocumentSnapshot document in current.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Id == savedDocument.Id || document.PhysicalPath is null ||
                !PhysicalPathComparer.Equals(NormalizePhysicalPath(document.PhysicalPath),
                    normalizedPath))
                continue;

            SourceText disk = SourceText.From(committedText, document.PhysicalPath,
                document.SourceFileId);
            var change = document.WithBackingState(disk, document.OverlayText, cancellationToken);
            replacements.Add(document.Id, change);
        }

        var mutations = new Dictionary<ProjectId, ProjectMutation>();
        foreach (IGrouping<ProjectId, KeyValuePair<DocumentId,
                     (DocumentSnapshot Document, DocumentChangeKind Kind)>> group in
                 replacements.GroupBy(pair => pair.Key.ProjectId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectSnapshot project = current.GetProject(group.Key);
            var byId = group.ToDictionary(pair => pair.Key, pair => pair.Value.Document);
            ImmutableArray<DocumentSnapshot> documents = project.Documents.Select(document =>
                byId.TryGetValue(document.Id, out DocumentSnapshot? replacement)
                    ? replacement : document).ToImmutableArray();
            DocumentChangeKind kind = group.Max(pair => pair.Value.Kind);
            mutations.Add(project.Id, new ProjectMutation(project.Configuration, documents, kind,
                group.Select(pair => pair.Key).ToImmutableHashSet(), ConfigurationChanged: false));
        }

        return BuildSnapshot(current, mutations);
    }

    private WorkspaceSnapshot PublishProjectDocumentSet(WorkspaceSnapshot current, ProjectId projectId,
        ImmutableArray<DocumentSnapshot> documents, DocumentChangeKind kind,
        ImmutableHashSet<DocumentId> changedDocuments)
    {
        WorkspaceSnapshot snapshot = BuildProjectDocumentSet(current, projectId, documents,
            kind, changedDocuments);
        Publish(snapshot);
        return snapshot;
    }

    private WorkspaceSnapshot BuildProjectDocumentSet(WorkspaceSnapshot current, ProjectId projectId,
        ImmutableArray<DocumentSnapshot> documents, DocumentChangeKind kind,
        ImmutableHashSet<DocumentId> changedDocuments) => BuildSnapshot(current,
            new Dictionary<ProjectId, ProjectMutation>
        {
            [projectId] = new(current.GetProject(projectId).Configuration, documents,
                kind, changedDocuments, ConfigurationChanged: false),
        });

    private WorkspaceSnapshot RemoveDocumentCore(WorkspaceSnapshot current, DocumentId documentId)
    {
        ProjectSnapshot project = current.GetProject(documentId.ProjectId);
        _ = project.GetDocument(documentId);
        return PublishProjectDocumentSet(current, project.Id,
            project.Documents.Where(document => document.Id != documentId).ToImmutableArray(),
            DocumentChangeKind.Declaration, [documentId]);
    }

    private WorkspaceSnapshot RebuildAndPublish(WorkspaceSnapshot current,
        IReadOnlyDictionary<ProjectId, ProjectMutation> mutations)
    {
        WorkspaceSnapshot snapshot = BuildSnapshot(current, mutations);
        Publish(snapshot);
        return snapshot;
    }

    private WorkspaceSnapshot BuildSnapshot(WorkspaceSnapshot current,
        IReadOnlyDictionary<ProjectId, ProjectMutation> mutations)
    {
        var configurations = current.Projects.ToDictionary(project => project.Id,
            project => mutations.TryGetValue(project.Id, out ProjectMutation? mutation)
                ? mutation.Configuration : project.Configuration);
        var documents = current.Projects.ToDictionary(project => project.Id,
            project => mutations.TryGetValue(project.Id, out ProjectMutation? mutation)
                ? mutation.Documents : project.Documents);
        foreach ((ProjectId projectId, ImmutableArray<DocumentSnapshot> projectDocuments) in documents)
            EnsureUniquePhysicalPaths(current.GetProject(projectId), projectDocuments);
        var idByIdentity = configurations.ToImmutableDictionary(pair => pair.Value.Identity,
            pair => pair.Key, ProjectPath.Comparer);
        XenonProject rootConfiguration = configurations[current.RootProjectId];
        XenonProjectGraph graph = XenonProjectGraph.Create(rootConfiguration, configurations.Values);
        var sourceMap = documents.SelectMany(pair => pair.Value.Select(document =>
                new KeyValuePair<SourceFileId, (ProjectId, DocumentId)>(document.SourceFileId,
                    (pair.Key, document.Id))))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
        var built = new Dictionary<ProjectId, ProjectSnapshot>();
        int invalidated = 0, reused = 0, compilationsRebuilt = 0, compilationsReused = 0;
        int semanticReused = 0, symbolRebuilt = 0, symbolDocumentsReused = 0;
        int referenceRebuilt = 0, referenceDocumentsReused = 0;

        foreach (XenonProject configuration in graph.BuildOrder)
        {
            ProjectId id = idByIdentity[configuration.Identity];
            ProjectSnapshot old = current.GetProject(id);
            ImmutableArray<ProjectSnapshot> references = configuration.ProjectReferences
                .Select(identity => built[idByIdentity[identity]]).ToImmutableArray();
            bool dependencyChanged = old.ProjectReferences.Length != references.Length ||
                old.ProjectReferences.Where((reference, index) => !ReferenceEquals(reference, references[index])).Any();
            bool dependencySurfaceChanged = dependencyChanged && references.Any(reference =>
                old.ProjectReferences.FirstOrDefault(item => item.Id == reference.Id)?.DeclarationFingerprint !=
                reference.DeclarationFingerprint);
            bool directChanged = mutations.TryGetValue(id, out ProjectMutation? mutation);
            if (!directChanged && !dependencyChanged)
            {
                built.Add(id, old);
                reused++;
                compilationsReused++;
                semanticReused += old.Documents.Length;
                symbolDocumentsReused += old.Documents.Length;
                referenceDocumentsReused += old.Documents.Length;
                continue;
            }

            invalidated++;
            ImmutableArray<DocumentSnapshot> projectDocuments = documents[id];
            DocumentChangeKind kind = mutation?.Kind ?? DocumentChangeKind.None;
            bool sameCompilationInputs = !dependencyChanged && mutation is not { ConfigurationChanged: true } &&
                old.Documents.Length == projectDocuments.Length && old.Documents.Zip(projectDocuments)
                    .All(pair => ReferenceEquals(pair.First.SyntaxTree, pair.Second.SyntaxTree));
            Compilation? reusableCompilation = sameCompilationInputs ? old.TryGetCachedCompilation() : null;
            if (reusableCompilation is null) compilationsRebuilt++;
            else
            {
                compilationsReused++;
                semanticReused += projectDocuments.Length;
            }

            ProjectSymbolIndex? oldSymbols = old.TryGetCachedSymbolIndex();
            ImmutableHashSet<DocumentId> symbolDocuments = kind is DocumentChangeKind.Declaration or DocumentChangeKind.BodyOnly
                ? (mutation?.ChangedDocuments ?? []).Intersect(projectDocuments.Select(item => item.Id)).ToImmutableHashSet()
                : [];
            if (oldSymbols is null || !symbolDocuments.IsEmpty) symbolRebuilt++;
            symbolDocumentsReused += oldSymbols is null ? 0 : projectDocuments.Length - symbolDocuments.Count;

            ProjectReferenceIndex? oldReferences = old.TryGetCachedReferenceIndex();
            ImmutableHashSet<DocumentId> referenceDocuments = kind switch
            {
                DocumentChangeKind.BackingStateOnly or DocumentChangeKind.None => [],
                DocumentChangeKind.BodyOnly => mutation!.ChangedDocuments,
                _ => projectDocuments.Select(item => item.Id).ToImmutableHashSet(),
            };
            if (mutation is { ConfigurationChanged: true } || dependencySurfaceChanged)
                referenceDocuments = projectDocuments.Select(item => item.Id).ToImmutableHashSet();
            if (oldReferences is null || !referenceDocuments.IsEmpty) referenceRebuilt++;
            referenceDocumentsReused += oldReferences is null ? 0 :
                projectDocuments.Length - referenceDocuments.Count;

            built.Add(id, new ProjectSnapshot(id, new ProjectVersion(old.Version.Value + 1),
                configuration, projectDocuments, references, _profileName, sourceMap,
                reusableCompilation, oldSymbols, oldReferences, symbolDocuments, referenceDocuments));
        }

        ImmutableArray<ProjectSnapshot> projects = built.Values.OrderBy(project => project.Id).ToImmutableArray();
        int changedCount = mutations.Values.Sum(mutation => mutation.ChangedDocuments.Count);
        int reparsed = mutations.Values.Sum(mutation => mutation.Documents.Count(document =>
            mutation.ChangedDocuments.Contains(document.Id) &&
            current.TryGetDocument(document.Id, out DocumentSnapshot? old) &&
            !ReferenceEquals(old!.SyntaxTree, document.SyntaxTree) ||
            mutation.ChangedDocuments.Contains(document.Id) && !current.TryGetDocument(document.Id, out _)));
        int treesReused = projects.SelectMany(project => project.Documents).Count(document =>
            current.TryGetDocument(document.Id, out DocumentSnapshot? old) &&
            ReferenceEquals(old!.SyntaxTree, document.SyntaxTree));
        var metrics = new IncrementalAnalysisMetrics(changedCount, reparsed, treesReused,
            invalidated, reused, compilationsRebuilt, compilationsReused, semanticReused,
            symbolRebuilt, symbolDocumentsReused, referenceRebuilt, referenceDocumentsReused);
        bool workspaceSymbolsUnchanged = mutations.Values.All(mutation =>
            mutation.Kind is DocumentChangeKind.BackingStateOnly or DocumentChangeKind.None &&
            !mutation.ConfigurationChanged);
        bool workspaceReferencesUnchanged = mutations.Values.All(mutation =>
            mutation.Kind is DocumentChangeKind.BackingStateOnly or DocumentChangeKind.None &&
            !mutation.ConfigurationChanged) && !built.Values.Any(project =>
                current.TryGetProject(project.Id, out ProjectSnapshot? old) &&
                (old!.ProjectReferences.Length != project.ProjectReferences.Length ||
                old.ProjectReferences.Where((reference, index) =>
                    !ReferenceEquals(reference, project.ProjectReferences[index])).Any()));
        var snapshot = new WorkspaceSnapshot(current.Id,
            new WorkspaceGeneration(current.Generation.Value + 1),
            current.RootProjectId, projects, metrics,
            workspaceSymbolsUnchanged ? current.TryGetCachedSymbolIndex() : null,
            workspaceReferencesUnchanged ? current.TryGetCachedReferenceIndex() : null);
        return snapshot;
    }

    private static WorkspaceSnapshot BuildInitialSnapshot(WorkspaceId workspaceId,
        XenonProjectGraph graph,
        ImmutableDictionary<string, ProjectId> idsByIdentity,
        ImmutableDictionary<ProjectId, ImmutableArray<DocumentSnapshot>> documents,
        string profileName, CancellationToken cancellationToken)
    {
        var sourceMap = documents.SelectMany(pair => pair.Value.Select(document =>
                new KeyValuePair<SourceFileId, (ProjectId, DocumentId)>(document.SourceFileId,
                    (pair.Key, document.Id))))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
        var built = new Dictionary<ProjectId, ProjectSnapshot>();
        foreach (XenonProject configuration in graph.BuildOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectId id = idsByIdentity[configuration.Identity];
            ImmutableArray<ProjectSnapshot> references = configuration.ProjectReferences
                .Select(identity => built[idsByIdentity[identity]]).ToImmutableArray();
            built[id] = new ProjectSnapshot(id, ProjectVersion.Initial, configuration,
                documents[id], references, profileName, sourceMap);
        }
        ImmutableArray<ProjectSnapshot> projects = built.Values.OrderBy(project => project.Id).ToImmutableArray();
        int documentCount = projects.Sum(project => project.Documents.Length);
        return new WorkspaceSnapshot(workspaceId, WorkspaceGeneration.Initial,
            idsByIdentity[graph.Root.Identity],
            projects, IncrementalAnalysisMetrics.Initial(documentCount, projects.Length));
    }

    private WorkspaceSnapshot CommitSave(WorkspaceSnapshot candidate, string physicalPath,
        string text, CancellationToken cancellationToken)
    {
        _saveObserver.CandidatePrepared();
        CancellationTokenSource? nextStaleCancellation = new();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // This is the save commit boundary. Once the write begins, caller cancellation is
            // deliberately ignored and the already-built matching snapshot is always published.
            _fileSystem.WriteAllText(physicalPath, text);
            Publish(candidate, nextStaleCancellation);
            nextStaleCancellation = null;
            return candidate;
        }
        finally
        {
            nextStaleCancellation?.Dispose();
        }
    }

    private void Publish(WorkspaceSnapshot snapshot,
        CancellationTokenSource? nextStaleCancellation = null)
    {
        CancellationTokenSource stale = _staleCancellation;
        _staleCancellation = nextStaleCancellation ?? new CancellationTokenSource();
        Volatile.Write(ref _currentSnapshot, snapshot);
        try
        {
            stale.Cancel();
        }
        catch (AggregateException)
        {
            // A consumer cancellation callback cannot roll back an already-published generation.
        }
        finally
        {
            stale.Dispose();
        }
    }

    private static StringComparer PhysicalPathComparer => ProjectPath.Comparer;

    private static string NormalizePhysicalPath(string path) => ProjectPath.Normalize(path);

    private static void EnsurePhysicalPathAvailable(ProjectSnapshot project,
        DocumentId documentId, string normalizedPath)
    {
        DocumentSnapshot? owner = project.Documents.FirstOrDefault(document =>
            document.Id != documentId && document.PhysicalPath is not null &&
            PhysicalPathComparer.Equals(NormalizePhysicalPath(document.PhysicalPath), normalizedPath));
        if (owner is not null)
            throw new ProjectSystemException(
                $"project '{project.Configuration.Name}' already owns physical source '{normalizedPath}' " +
                $"as document '{owner.Id}'");
    }

    private static void EnsureUniquePhysicalPaths(ProjectSnapshot project,
        ImmutableArray<DocumentSnapshot> documents)
    {
        var owners = new Dictionary<string, DocumentId>(PhysicalPathComparer);
        foreach (DocumentSnapshot document in documents)
        {
            if (document.PhysicalPath is null) continue;
            string path = NormalizePhysicalPath(document.PhysicalPath);
            if (owners.TryAdd(path, document.Id)) continue;
            throw new ProjectSystemException(
                $"project '{project.Configuration.Name}' contains duplicate physical source '{path}' " +
                $"owned by documents '{owners[path]}' and '{document.Id}'");
        }
    }

    private static void EnsureCurrentVersion(DocumentSnapshot document,
        DocumentVersion expectedVersion)
    {
        if (expectedVersion != document.Version)
            throw new StaleDocumentVersionException(document.Id, document.Version, expectedVersion);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed record ProjectMutation(XenonProject Configuration,
        ImmutableArray<DocumentSnapshot> Documents, DocumentChangeKind Kind,
        ImmutableHashSet<DocumentId> ChangedDocuments, bool ConfigurationChanged);
}
