using System.Collections.Immutable;

namespace Xenon.ProjectSystem;

/// <summary>A complete immutable Workspace generation.</summary>
public sealed class WorkspaceSnapshot
{
    private readonly ImmutableDictionary<ProjectId, ProjectSnapshot> _projectsById;
    private readonly ImmutableDictionary<DocumentId, DocumentSnapshot> _documentsById;
    private WorkspaceSymbolIndex? _symbolIndex;
    private WorkspaceReferenceIndex? _referenceIndex;
    private WorkspaceTypeRelationshipIndex? _typeRelationshipIndex;
    private WorkspaceMemberRelationshipIndex? _memberRelationshipIndex;

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

    public bool TryGetSymbolId(Xenon.Compiler.Semantics.Symbols.Symbol symbol, out WorkspaceSymbolId id)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var sourceMap = Documents.ToDictionary(document => document.SourceFileId,
            document => (document.ProjectId, document.Id));
        return WorkspaceIndexBuilder.TryCreateSymbolId(symbol, sourceMap, out id);
    }

    public SourceReference? GetDeclaration(Xenon.Compiler.Semantics.Symbols.Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        var sourceMap = Documents.ToDictionary(document => document.SourceFileId,
            document => (document.ProjectId, document.Id));
        return symbol.DeclaringSyntaxReferences.Any(reference => sourceMap.ContainsKey(reference.Source.FileId))
            ? WorkspaceIndexBuilder.CreateDeclarationReference(symbol, sourceMap) : null;
    }

    public async Task<WorkspaceTypeRelationshipIndex> GetTypeRelationshipIndexAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceTypeRelationshipIndex? cached = Volatile.Read(ref _typeRelationshipIndex);
        if (cached is not null) return cached;
        var entries = new List<TypeRelationshipIndexEntry>();
        foreach (ProjectSnapshot project in Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Xenon.Compiler.Compilation compilation = await project.GetCompilationAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (Xenon.Compiler.Semantics.Symbols.DeclaredTypeSymbol type in
                     EnumerateTypes(compilation.SemanticModel.GlobalNamespace))
            {
                if (!TryGetSymbolId(type, out WorkspaceSymbolId derived)) continue;
                SourceReference? declaration = GetDeclaration(type);
                if (declaration is null || declaration.Value.ProjectId != project.Id) continue;
                if (type is Xenon.Compiler.Semantics.Symbols.StructTypeSymbol structure)
                {
                    Add(structure.BaseType, TypeRelationshipKind.DerivedType);
                    foreach (var implemented in structure.Interfaces)
                        Add(implemented, TypeRelationshipKind.InterfaceImplementation);
                }
                else if (type is Xenon.Compiler.Semantics.Symbols.InterfaceTypeSymbol @interface)
                    foreach (var baseInterface in @interface.BaseInterfaces)
                        Add(baseInterface, TypeRelationshipKind.DerivedInterface);

                void Add(Xenon.Compiler.Semantics.Symbols.DeclaredTypeSymbol? baseType,
                    TypeRelationshipKind kind)
                {
                    if (baseType is not null && TryGetSymbolId(baseType, out WorkspaceSymbolId target))
                        entries.Add(new TypeRelationshipIndexEntry(target, derived, declaration.Value, kind));
                }
            }
        }
        var created = new WorkspaceTypeRelationshipIndex(entries);
        WorkspaceTypeRelationshipIndex? winner = Interlocked.CompareExchange(
            ref _typeRelationshipIndex, created, null);
        return winner ?? created;

    }

    public async Task<WorkspaceMemberRelationshipIndex> GetMemberRelationshipIndexAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkspaceMemberRelationshipIndex? cached = Volatile.Read(ref _memberRelationshipIndex);
        if (cached is not null) return cached;
        var entries = new List<MemberRelationshipIndexEntry>();
        var nonEditable = new HashSet<WorkspaceSymbolId>();
        foreach (ProjectSnapshot project in Projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Xenon.Compiler.Compilation compilation = await project.GetCompilationAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (Xenon.Compiler.Semantics.Symbols.StructTypeSymbol structure in
                     EnumerateTypes(compilation.SemanticModel.GlobalNamespace)
                         .OfType<Xenon.Compiler.Semantics.Symbols.StructTypeSymbol>())
            {
                SourceReference? declaration = GetDeclaration(structure);
                if (declaration is null || declaration.Value.ProjectId != project.Id) continue;

                foreach (Xenon.Compiler.Semantics.Symbols.FunctionSymbol method in structure.Methods.Where(
                             method => ReferenceEquals(method.ContainingSymbol, structure) && method.IsOverride))
                {
                    Xenon.Compiler.Semantics.Symbols.FunctionSymbol? inherited = structure.BaseType?
                        .VirtualMethods.FirstOrDefault(method.HasSameSignature);
                    if (inherited is not null) Add(inherited, method, MemberRelationshipKind.Override);
                }
                foreach (Xenon.Compiler.Semantics.Symbols.PropertySymbol property in
                         structure.Properties.Where(property => property.IsOverride))
                {
                    Xenon.Compiler.Semantics.Symbols.PropertySymbol? inherited =
                        structure.BaseType?.FindProperty(property.Name);
                    if (inherited is not null && Xenon.Compiler.Semantics.Symbols.TypeIdentity.AreSame(
                            inherited.Type, property.Type))
                        Add(inherited, property, MemberRelationshipKind.Override);
                }

                foreach (Xenon.Compiler.Semantics.Symbols.InterfaceTypeSymbol @interface in
                         structure.ImplementedInterfaces)
                {
                    foreach (Xenon.Compiler.Semantics.Symbols.FunctionSymbol required in @interface.Methods)
                        if (structure.FindInterfaceImplementation(required) is { } implementation)
                            Add(required, implementation, MemberRelationshipKind.InterfaceImplementation);
                    foreach (Xenon.Compiler.Semantics.Symbols.InterfacePropertySymbol required in
                             @interface.Properties)
                    {
                        foreach (Xenon.Compiler.Semantics.Symbols.FunctionSymbol accessor in
                                 new[] { required.Getter, required.Setter }.OfType<Xenon.Compiler.Semantics.Symbols.FunctionSymbol>())
                            if (structure.FindInterfaceImplementation(accessor)?.ContainingProperty is { } implementation)
                                Add(required, implementation, MemberRelationshipKind.InterfaceImplementation);
                    }
                }
            }
        }

        var created = new WorkspaceMemberRelationshipIndex(entries, nonEditable);
        WorkspaceMemberRelationshipIndex? winner = Interlocked.CompareExchange(
            ref _memberRelationshipIndex, created, null);
        return winner ?? created;

        void Add(Xenon.Compiler.Semantics.Symbols.Symbol contract,
            Xenon.Compiler.Semantics.Symbols.Symbol implementation, MemberRelationshipKind kind)
        {
            bool hasContract = TryGetSymbolId(contract, out WorkspaceSymbolId contractId);
            bool hasImplementation = TryGetSymbolId(implementation, out WorkspaceSymbolId implementationId);
            if (hasContract && hasImplementation)
                entries.Add(new MemberRelationshipIndexEntry(contractId, implementationId, kind));
            else if (hasContract)
                nonEditable.Add(contractId);
            else if (hasImplementation)
                nonEditable.Add(implementationId);
        }
    }

    public async Task<bool> HasRenameConflictAsync(IEnumerable<WorkspaceSymbolId> members,
        string newName, CancellationToken cancellationToken = default)
    {
        var remaining = members.ToHashSet();
        foreach (ProjectSnapshot project in Projects)
        {
            if (remaining.Count == 0) break;
            cancellationToken.ThrowIfCancellationRequested();
            Xenon.Compiler.Compilation compilation = await project.GetCompilationAsync(cancellationToken)
                .ConfigureAwait(false);
            Xenon.Compiler.Semantics.SemanticModel model = compilation.SemanticModel;
            foreach (Xenon.Compiler.Semantics.Symbols.Symbol symbol in
                     model.GetDeclaredSymbols(cancellationToken))
            {
                if (!TryGetSymbolId(symbol, out WorkspaceSymbolId id) || !remaining.Contains(id)) continue;
                if (model.HasRenameConflict(symbol, newName, cancellationToken)) return true;
                remaining.Remove(id);
            }
        }
        return remaining.Count != 0;
    }

    internal WorkspaceSymbolIndex? TryGetCachedSymbolIndex() => Volatile.Read(ref _symbolIndex);
    internal WorkspaceReferenceIndex? TryGetCachedReferenceIndex() => Volatile.Read(ref _referenceIndex);

    private static IEnumerable<Xenon.Compiler.Semantics.Symbols.DeclaredTypeSymbol> EnumerateTypes(
        Xenon.Compiler.Semantics.Symbols.NamespaceSymbol root)
    {
        foreach (Xenon.Compiler.Semantics.Symbols.DeclaredTypeSymbol type in root.Types)
            yield return type;
        foreach (Xenon.Compiler.Semantics.Symbols.NamespaceSymbol child in root.Namespaces)
            foreach (Xenon.Compiler.Semantics.Symbols.DeclaredTypeSymbol type in EnumerateTypes(child))
                yield return type;
    }
}
