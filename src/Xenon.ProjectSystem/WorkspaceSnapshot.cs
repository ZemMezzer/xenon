using System.Collections.Immutable;

namespace Xenon.ProjectSystem;

/// <summary>A complete immutable Workspace generation.</summary>
public sealed class WorkspaceSnapshot
{
    private readonly ImmutableDictionary<ProjectId, ProjectSnapshot> _projectsById;
    private readonly ImmutableDictionary<DocumentId, DocumentSnapshot> _documentsById;
    private WorkspaceSymbolIndex? _symbolIndex;
    private WorkspaceReferenceIndex? _referenceIndex;

    internal WorkspaceSnapshot(WorkspaceId id, WorkspaceGeneration generation, ProjectId rootProjectId,
        ImmutableArray<ProjectSnapshot> projects, IncrementalAnalysisMetrics metrics,
        WorkspaceSymbolIndex? reusableSymbolIndex = null,
        WorkspaceReferenceIndex? reusableReferenceIndex = null)
    {
        Id = id;
        Generation = generation;
        RootProjectId = rootProjectId;
        Projects = projects;
        Metrics = metrics;
        _projectsById = projects.ToImmutableDictionary(project => project.Id);
        _documentsById = projects.SelectMany(project => project.Documents)
            .ToImmutableDictionary(document => document.Id);
        if (!_projectsById.ContainsKey(rootProjectId))
            throw new ArgumentException("The root project must belong to the Workspace snapshot.", nameof(rootProjectId));
        foreach (ProjectSnapshot project in projects)
            if (project.ProjectReferences.Any(reference =>
                !_projectsById.TryGetValue(reference.Id, out ProjectSnapshot? pinned) ||
                !ReferenceEquals(reference, pinned)))
                throw new ArgumentException("Project references must pin generations in this snapshot.", nameof(projects));
        _symbolIndex = reusableSymbolIndex;
        _referenceIndex = reusableReferenceIndex;
    }

    public WorkspaceId Id { get; }
    public WorkspaceGeneration Generation { get; }
    public ProjectId RootProjectId { get; }
    public ImmutableArray<ProjectSnapshot> Projects { get; }
    public ImmutableArray<DocumentSnapshot> Documents => _documentsById.Values
        .OrderBy(document => document.Id).ToImmutableArray();
    public IncrementalAnalysisMetrics Metrics { get; }
    public ProjectSnapshot RootProject => GetProject(RootProjectId);

    public ProjectSnapshot GetProject(ProjectId id) =>
        _projectsById.TryGetValue(id, out ProjectSnapshot? project) ? project :
            throw new KeyNotFoundException($"Project '{id}' does not belong to Workspace generation {Generation}.");

    public DocumentSnapshot GetDocument(DocumentId id) =>
        _documentsById.TryGetValue(id, out DocumentSnapshot? document) ? document :
            throw new KeyNotFoundException($"Document '{id}' does not belong to Workspace generation {Generation}.");

    public bool TryGetProject(ProjectId id, out ProjectSnapshot? project) => _projectsById.TryGetValue(id, out project);
    public bool TryGetDocument(DocumentId id, out DocumentSnapshot? document) => _documentsById.TryGetValue(id, out document);

    public async Task<WorkspaceSymbolIndex> GetSymbolIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceSymbolIndex? cached = Volatile.Read(ref _symbolIndex);
        if (cached is not null) return cached;
        ProjectSymbolIndex[] indexes = await Task.WhenAll(Projects.Select(project =>
            project.GetSymbolIndexAsync(cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var created = new WorkspaceSymbolIndex(indexes);
        WorkspaceSymbolIndex? winner = Interlocked.CompareExchange(ref _symbolIndex, created, null);
        return winner ?? created;
    }

    public async Task<WorkspaceReferenceIndex> GetReferenceIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceReferenceIndex? cached = Volatile.Read(ref _referenceIndex);
        if (cached is not null) return cached;
        ProjectReferenceIndex[] indexes = await Task.WhenAll(Projects.Select(project =>
            project.GetReferenceIndexAsync(cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var created = new WorkspaceReferenceIndex(indexes);
        WorkspaceReferenceIndex? winner = Interlocked.CompareExchange(ref _referenceIndex, created, null);
        return winner ?? created;
    }

    internal WorkspaceSymbolIndex? TryGetCachedSymbolIndex() => Volatile.Read(ref _symbolIndex);
    internal WorkspaceReferenceIndex? TryGetCachedReferenceIndex() => Volatile.Read(ref _referenceIndex);
}
