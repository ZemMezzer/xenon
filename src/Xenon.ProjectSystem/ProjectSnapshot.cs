using System.Collections.Immutable;
using Xenon.Compiler;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Text;

namespace Xenon.ProjectSystem;

/// <summary>One immutable project generation with exact document and dependency snapshots.</summary>
public sealed class ProjectSnapshot
{
    private readonly string _profileName;
    private readonly ImmutableDictionary<DocumentId, DocumentSnapshot> _documentsById;
    private readonly ImmutableDictionary<SourceFileId, (ProjectId ProjectId, DocumentId DocumentId)> _sourceMap;
    private readonly ImmutableDictionary<DocumentId, ImmutableArray<SymbolIndexEntry>>? _symbolContributions;
    private readonly ImmutableDictionary<DocumentId, ImmutableArray<ReferenceIndexEntry>>? _referenceContributions;
    private readonly ImmutableHashSet<DocumentId> _symbolDocumentsToRebuild;
    private readonly ImmutableHashSet<DocumentId> _referenceDocumentsToRebuild;
    private Compilation? _compilation;
    private ProjectSymbolIndex? _symbolIndex;
    private ProjectReferenceIndex? _referenceIndex;

    internal ProjectSnapshot(ProjectId id, ProjectVersion version, XenonProject configuration,
        ImmutableArray<DocumentSnapshot> documents, ImmutableArray<ProjectSnapshot> projectReferences,
        string profileName,
        ImmutableDictionary<SourceFileId, (ProjectId ProjectId, DocumentId DocumentId)> sourceMap,
        Compilation? reusableCompilation = null,
        ProjectSymbolIndex? reusableSymbolIndex = null,
        ProjectReferenceIndex? reusableReferenceIndex = null,
        ImmutableHashSet<DocumentId>? symbolDocumentsToRebuild = null,
        ImmutableHashSet<DocumentId>? referenceDocumentsToRebuild = null)
    {
        Id = id;
        Version = version;
        Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        Documents = documents;
        ProjectReferences = projectReferences;
        _profileName = profileName;
        _sourceMap = sourceMap;
        _documentsById = documents.ToImmutableDictionary(document => document.Id);
        if (documents.Any(document => document.ProjectId != id))
            throw new ArgumentException("Every document must belong to this project.", nameof(documents));
        if (documents.Select(document => document.SourceFileId).Distinct().Count() != documents.Length)
            throw new ArgumentException("Project documents must have distinct source identities.", nameof(documents));
        _compilation = reusableCompilation;
        _symbolIndex = reusableSymbolIndex is not null && symbolDocumentsToRebuild is { Count: 0 }
            ? reusableSymbolIndex : null;
        _referenceIndex = reusableReferenceIndex is not null && referenceDocumentsToRebuild is { Count: 0 }
            ? reusableReferenceIndex : null;
        _symbolContributions = reusableSymbolIndex?.Contributions;
        _referenceContributions = reusableReferenceIndex?.Contributions;
        _symbolDocumentsToRebuild = reusableSymbolIndex is null
            ? documents.Select(item => item.Id).ToImmutableHashSet()
            : symbolDocumentsToRebuild ?? documents.Select(item => item.Id).ToImmutableHashSet();
        _referenceDocumentsToRebuild = reusableReferenceIndex is null
            ? documents.Select(item => item.Id).ToImmutableHashSet()
            : referenceDocumentsToRebuild ?? documents.Select(item => item.Id).ToImmutableHashSet();
        DeclarationFingerprint = string.Join('|', documents.OrderBy(document => document.Id)
            .Select(document => $"{document.Id}:{document.DeclarationFingerprint}"));
    }

    public ProjectId Id { get; }
    public ProjectVersion Version { get; }
    public XenonProject Configuration { get; }
    public ImmutableArray<DocumentSnapshot> Documents { get; }
    public ImmutableArray<ProjectSnapshot> ProjectReferences { get; }
    public string DeclarationFingerprint { get; }

    public DocumentSnapshot GetDocument(DocumentId id) =>
        _documentsById.TryGetValue(id, out DocumentSnapshot? document) ? document :
            throw new KeyNotFoundException($"Document '{id}' does not belong to project '{Id}'.");

    public bool TryGetDocument(DocumentId id, out DocumentSnapshot? document) =>
        _documentsById.TryGetValue(id, out document);

    public async Task<Compilation> GetCompilationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Compilation? cached = Volatile.Read(ref _compilation);
        if (cached is not null) return cached;

        var dependencies = new Dictionary<string, Compilation>(ProjectPath.Comparer);
        foreach (ProjectSnapshot reference in ProjectReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dependencies.Add(reference.Configuration.Identity,
                await reference.GetCompilationAsync(cancellationToken).ConfigureAwait(false));
        }
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        Compilation created = XenonProjectCompilationFactory.Create(Configuration, _profileName,
            Documents.Select(document => document.SyntaxTree), dependencies, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        Compilation? winner = Interlocked.CompareExchange(ref _compilation, created, null);
        return winner ?? created;
    }

    public async Task<SemanticModel> GetSemanticModelAsync(DocumentId documentId,
        CancellationToken cancellationToken = default)
    {
        DocumentSnapshot document = GetDocument(documentId);
        Compilation compilation = await GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        return compilation.GetSemanticModel(document.SyntaxTree, cancellationToken);
    }

    public async Task<ProjectSymbolIndex> GetSymbolIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProjectSymbolIndex? cached = Volatile.Read(ref _symbolIndex);
        if (cached is not null) return cached;
        Compilation compilation = await GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        ImmutableArray<SymbolIndexEntry> rebuilt = WorkspaceIndexBuilder.BuildSymbols(Id,
            compilation.SemanticModel, _sourceMap, cancellationToken);
        var contributions = (_symbolContributions ??
            ImmutableDictionary<DocumentId, ImmutableArray<SymbolIndexEntry>>.Empty).ToBuilder();
        foreach (DocumentId removed in contributions.Keys.Except(_documentsById.Keys).ToArray())
            contributions.Remove(removed);
        foreach (DocumentId documentId in _symbolDocumentsToRebuild)
            contributions[documentId] = rebuilt.Where(entry => entry.Id.DocumentId == documentId).ToImmutableArray();
        foreach (DocumentSnapshot document in Documents)
            contributions.TryAdd(document.Id, []);
        var created = new ProjectSymbolIndex(Id, contributions.ToImmutable());
        cancellationToken.ThrowIfCancellationRequested();
        ProjectSymbolIndex? winner = Interlocked.CompareExchange(ref _symbolIndex, created, null);
        return winner ?? created;
    }

    public async Task<ProjectReferenceIndex> GetReferenceIndexAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProjectReferenceIndex? cached = Volatile.Read(ref _referenceIndex);
        if (cached is not null) return cached;
        Compilation compilation = await GetCompilationAsync(cancellationToken).ConfigureAwait(false);
        var contributions = (_referenceContributions ??
            ImmutableDictionary<DocumentId, ImmutableArray<ReferenceIndexEntry>>.Empty).ToBuilder();
        foreach (DocumentId removed in contributions.Keys.Except(_documentsById.Keys).ToArray())
            contributions.Remove(removed);
        foreach (DocumentId documentId in _referenceDocumentsToRebuild)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentSnapshot document = GetDocument(documentId);
            SemanticModel model = compilation.GetSemanticModel(document.SyntaxTree, cancellationToken);
            contributions[documentId] = WorkspaceIndexBuilder.BuildReferences(document, model,
                _sourceMap, cancellationToken);
        }
        foreach (DocumentSnapshot document in Documents)
            contributions.TryAdd(document.Id, []);
        var created = new ProjectReferenceIndex(Id, contributions.ToImmutable());
        cancellationToken.ThrowIfCancellationRequested();
        ProjectReferenceIndex? winner = Interlocked.CompareExchange(ref _referenceIndex, created, null);
        return winner ?? created;
    }

    internal Compilation? TryGetCachedCompilation() => Volatile.Read(ref _compilation);
    internal ProjectSymbolIndex? TryGetCachedSymbolIndex() => Volatile.Read(ref _symbolIndex);
    internal ProjectReferenceIndex? TryGetCachedReferenceIndex() => Volatile.Read(ref _referenceIndex);
}
