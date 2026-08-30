using System.Collections.Immutable;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.ProjectSystem;

public readonly record struct SourceReference(
    ProjectId ProjectId,
    DocumentId DocumentId,
    SourceFileId SourceFileId,
    string Path,
    TextSpan Span);

/// <summary>
/// Stable semantic declaration identity within one logical document. Source coordinates are
/// navigation metadata on SymbolIndexEntry and are deliberately excluded from equality.
/// </summary>
public readonly record struct WorkspaceSymbolId(
    ProjectId ProjectId,
    DocumentId DocumentId,
    SymbolKind Kind,
    string QualifiedName,
    string DeclarationIdentity);

public sealed record SymbolIndexEntry(
    WorkspaceSymbolId Id,
    string Name,
    string QualifiedName,
    SymbolKind Kind,
    SourceReference Declaration)
{
    public FunctionKind? FunctionKind { get; init; }
    public required EditorSymbolKind EditorKind { get; init; }
    public WorkspaceSymbolId? ContainingSymbolId { get; init; }
    public bool IsDefinition { get; init; }
    public bool CanRename { get; init; }
}

public sealed class ProjectSymbolIndex
{
    private readonly ImmutableDictionary<DocumentId, ImmutableArray<SymbolIndexEntry>> _byDocument;

    internal ProjectSymbolIndex(ProjectId projectId,
        ImmutableDictionary<DocumentId, ImmutableArray<SymbolIndexEntry>> byDocument)
    {
        ProjectId = projectId;
        _byDocument = byDocument;
        Entries = byDocument.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value)
            .OrderBy(entry => entry.QualifiedName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Declaration.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Declaration.Span.Start).ToImmutableArray();
    }

    public ProjectId ProjectId { get; }
    public ImmutableArray<SymbolIndexEntry> Entries { get; }

    public ImmutableArray<SymbolIndexEntry> Search(string? name = null, string? qualifiedName = null,
        SymbolKind? kind = null) => Entries.Where(entry =>
            (name is null || string.Equals(entry.Name, name, StringComparison.Ordinal)) &&
            (qualifiedName is null || string.Equals(entry.QualifiedName, qualifiedName, StringComparison.Ordinal)) &&
            (kind is null || entry.Kind == kind)).ToImmutableArray();

    public ImmutableArray<SymbolIndexEntry> FindMembers(WorkspaceSymbolId containingSymbol) =>
        Entries.Where(entry => entry.ContainingSymbolId == containingSymbol).ToImmutableArray();

    internal ImmutableDictionary<DocumentId, ImmutableArray<SymbolIndexEntry>> Contributions => _byDocument;
}

public sealed class WorkspaceSymbolIndex
{
    internal WorkspaceSymbolIndex(IEnumerable<ProjectSymbolIndex> projects)
    {
        Entries = projects.SelectMany(project => project.Entries)
            .OrderBy(entry => entry.QualifiedName, StringComparer.Ordinal)
            .ThenBy(entry => entry.Id.ProjectId).ThenBy(entry => entry.Declaration.Span.Start)
            .ToImmutableArray();
    }

    public ImmutableArray<SymbolIndexEntry> Entries { get; }

    public ImmutableArray<SymbolIndexEntry> Search(string? name = null, string? qualifiedName = null,
        SymbolKind? kind = null, ProjectId? projectId = null) => Entries.Where(entry =>
            (name is null || string.Equals(entry.Name, name, StringComparison.Ordinal)) &&
            (qualifiedName is null || string.Equals(entry.QualifiedName, qualifiedName, StringComparison.Ordinal)) &&
            (kind is null || entry.Kind == kind) &&
            (projectId is null || entry.Id.ProjectId == projectId)).ToImmutableArray();

    public ImmutableArray<SymbolIndexEntry> FindMembers(WorkspaceSymbolId containingSymbol) =>
        Entries.Where(entry => entry.ContainingSymbolId == containingSymbol).ToImmutableArray();

    public SharedPhysicalDeclarationGroup GetSharedPhysicalDeclarationGroup(WorkspaceSymbolId symbol)
    {
        SymbolIndexEntry target = Entries.Single(entry => entry.Id == symbol);
        ImmutableArray<SymbolIndexEntry> entries = Entries.Where(entry =>
                ProjectPath.Comparer.Equals(ProjectPath.Normalize(entry.Declaration.Path),
                    ProjectPath.Normalize(target.Declaration.Path)) &&
                entry.Declaration.Span == target.Declaration.Span)
            .ToImmutableArray();
        // A physical declaration compiled in multiple project contexts may bind differently.
        // Until cross-compilation type identities can be proven equivalent, reject it conservatively.
        bool compatible = entries.Select(entry => entry.Id.ProjectId).Distinct().Take(2).Count() == 1;
        return new SharedPhysicalDeclarationGroup(entries, compatible);
    }
}

public sealed record SharedPhysicalDeclarationGroup(
    ImmutableArray<SymbolIndexEntry> Entries,
    bool IsCompatible);

public sealed record ReferenceIndexEntry(
    WorkspaceSymbolId Target,
    SourceReference Source,
    ResolvedReferenceKind Kind);

public sealed class ProjectReferenceIndex
{
    private readonly ImmutableDictionary<DocumentId, ImmutableArray<ReferenceIndexEntry>> _byDocument;

    internal ProjectReferenceIndex(ProjectId projectId,
        ImmutableDictionary<DocumentId, ImmutableArray<ReferenceIndexEntry>> byDocument)
    {
        ProjectId = projectId;
        _byDocument = byDocument;
        Entries = byDocument.OrderBy(pair => pair.Key).SelectMany(pair => pair.Value)
            .OrderBy(entry => entry.Source.Path, StringComparer.Ordinal)
            .ThenBy(entry => entry.Source.Span.Start).ToImmutableArray();
    }

    public ProjectId ProjectId { get; }
    public ImmutableArray<ReferenceIndexEntry> Entries { get; }

    public ImmutableArray<ReferenceIndexEntry> FindReferences(WorkspaceSymbolId symbol) =>
        Entries.Where(entry => entry.Target == symbol).ToImmutableArray();

    internal ImmutableDictionary<DocumentId, ImmutableArray<ReferenceIndexEntry>> Contributions => _byDocument;
}

public sealed class WorkspaceReferenceIndex
{
    internal WorkspaceReferenceIndex(IEnumerable<ProjectReferenceIndex> projects)
    {
        Entries = projects.SelectMany(project => project.Entries)
            .OrderBy(entry => entry.Source.ProjectId).ThenBy(entry => entry.Source.DocumentId)
            .ThenBy(entry => entry.Source.Span.Start).ToImmutableArray();
    }

    public ImmutableArray<ReferenceIndexEntry> Entries { get; }
    public ImmutableArray<ReferenceIndexEntry> FindReferences(WorkspaceSymbolId symbol) =>
        Entries.Where(entry => entry.Target == symbol).ToImmutableArray();
}

public enum TypeRelationshipKind
{
    DerivedType,
    InterfaceImplementation,
    DerivedInterface,
}

public sealed record TypeRelationshipIndexEntry(
    WorkspaceSymbolId BaseType,
    WorkspaceSymbolId DerivedType,
    SourceReference DerivedDeclaration,
    TypeRelationshipKind Kind);

/// <summary>Reverse, semantic type relationships for editor and other tooling consumers.</summary>
public sealed class WorkspaceTypeRelationshipIndex
{
    internal WorkspaceTypeRelationshipIndex(IEnumerable<TypeRelationshipIndexEntry> entries)
    {
        Entries = entries.Distinct().OrderBy(entry => entry.DerivedDeclaration.Path,
            StringComparer.Ordinal).ThenBy(entry => entry.DerivedDeclaration.Span.Start).ToImmutableArray();
    }

    public ImmutableArray<TypeRelationshipIndexEntry> Entries { get; }

    public ImmutableArray<TypeRelationshipIndexEntry> FindDirect(WorkspaceSymbolId type) =>
        Entries.Where(entry => entry.BaseType == type).ToImmutableArray();

    public ImmutableArray<TypeRelationshipIndexEntry> FindTransitive(WorkspaceSymbolId type)
    {
        var result = ImmutableArray.CreateBuilder<TypeRelationshipIndexEntry>();
        var pending = new Queue<WorkspaceSymbolId>();
        var visited = new HashSet<WorkspaceSymbolId> { type };
        pending.Enqueue(type);
        while (pending.TryDequeue(out WorkspaceSymbolId current))
            foreach (TypeRelationshipIndexEntry entry in FindDirect(current))
                if (visited.Add(entry.DerivedType))
                {
                    result.Add(entry);
                    pending.Enqueue(entry.DerivedType);
                }
        return result.ToImmutable();
    }
}

public enum MemberRelationshipKind
{
    Override,
    InterfaceImplementation,
}

public sealed record MemberRelationshipIndexEntry(
    WorkspaceSymbolId Contract,
    WorkspaceSymbolId Implementation,
    MemberRelationshipKind Kind);

/// <summary>Exact semantic member families used by editor refactorings.</summary>
public sealed class WorkspaceMemberRelationshipIndex
{
    private readonly ImmutableHashSet<WorkspaceSymbolId> _nonEditableMembers;

    internal WorkspaceMemberRelationshipIndex(IEnumerable<MemberRelationshipIndexEntry> entries,
        IEnumerable<WorkspaceSymbolId> nonEditableMembers)
    {
        Entries = entries.Distinct().OrderBy(entry => entry.Contract.ProjectId)
            .ThenBy(entry => entry.Contract.DocumentId)
            .ThenBy(entry => entry.Contract.DeclarationIdentity, StringComparer.Ordinal)
            .ThenBy(entry => entry.Implementation.ProjectId)
            .ThenBy(entry => entry.Implementation.DocumentId)
            .ThenBy(entry => entry.Implementation.DeclarationIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        _nonEditableMembers = nonEditableMembers.ToImmutableHashSet();
    }

    public ImmutableArray<MemberRelationshipIndexEntry> Entries { get; }

    public ImmutableArray<WorkspaceSymbolId> GetFamily(WorkspaceSymbolId member)
    {
        var family = new HashSet<WorkspaceSymbolId> { member };
        var pending = new Queue<WorkspaceSymbolId>();
        pending.Enqueue(member);
        while (pending.TryDequeue(out WorkspaceSymbolId current))
            foreach (MemberRelationshipIndexEntry relationship in Entries.Where(entry =>
                         entry.Contract == current || entry.Implementation == current))
            {
                WorkspaceSymbolId related = relationship.Contract == current
                    ? relationship.Implementation : relationship.Contract;
                if (family.Add(related)) pending.Enqueue(related);
            }
        return family.OrderBy(item => item.ProjectId).ThenBy(item => item.DocumentId)
            .ThenBy(item => item.DeclarationIdentity, StringComparer.Ordinal).ToImmutableArray();
    }

    public bool HasNonEditableMember(IEnumerable<WorkspaceSymbolId> family) =>
        family.Any(_nonEditableMembers.Contains);
}

internal static class WorkspaceIndexBuilder
{
    public static ImmutableArray<SymbolIndexEntry> BuildSymbols(ProjectId projectId,
        SemanticModel model,
        IReadOnlyDictionary<SourceFileId, (ProjectId ProjectId, DocumentId DocumentId)> sourceMap,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<SymbolIndexEntry>();
        var visited = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        Visit(model.GlobalNamespace);
        return result.ToImmutable();

        void Visit(Symbol symbol)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!visited.Add(symbol)) return;
            foreach (var syntaxReference in symbol.IsUserVisible ? symbol.DeclaringSyntaxReferences : [])
            {
                SourceText source = syntaxReference.Source;
                if (!sourceMap.TryGetValue(source.FileId, out var owner) || owner.ProjectId != projectId) continue;
                SourceReference declaration = CreateSourceReference(owner, syntaxReference.Location);
                WorkspaceSymbolId id = CreateSymbolId(symbol, owner);
                result.Add(new SymbolIndexEntry(id, symbol.Name, symbol.QualifiedName, symbol.Kind, declaration)
                {
                    FunctionKind = (symbol as FunctionSymbol)?.FunctionKind,
                    EditorKind = EditorSymbolClassifier.GetKind(symbol),
                    ContainingSymbolId = symbol.ContainingSymbol is { IsSourceDefined: true } containing &&
                        TryCreateSymbolId(containing, sourceMap, out WorkspaceSymbolId containingId)
                            ? containingId : null,
                    IsDefinition = symbol.IsDefinition,
                    CanRename = EditorSymbolClassifier.CanRename(symbol),
                });
            }

            switch (symbol)
            {
                case NamespaceSymbol @namespace:
                    foreach (Symbol child in @namespace.Namespaces.Cast<Symbol>()
                        .Concat(@namespace.Types).Concat(@namespace.Functions).Concat(@namespace.Constants))
                        Visit(child);
                    break;
                case DeclaredTypeSymbol type:
                    foreach (Symbol child in type.GetMembers()) Visit(child);
                    break;
            }
        }
    }

    public static ImmutableArray<ReferenceIndexEntry> BuildReferences(DocumentSnapshot document,
        SemanticModel model,
        IReadOnlyDictionary<SourceFileId, (ProjectId ProjectId, DocumentId DocumentId)> sourceMap,
        CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<ReferenceIndexEntry>();
        foreach (ResolvedSymbolReference reference in model.GetResolvedReferences(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryCreateSymbolId(reference.Symbol, sourceMap, out WorkspaceSymbolId target)) continue;
            SourceReference source = CreateSourceReference(
                (document.ProjectId, document.Id), reference.Location);
            result.Add(new ReferenceIndexEntry(target, source, reference.Kind));
        }
        return result.ToImmutable();
    }

    internal static bool TryCreateSymbolId(Symbol symbol,
        IReadOnlyDictionary<SourceFileId, (ProjectId ProjectId, DocumentId DocumentId)> sourceMap,
        out WorkspaceSymbolId id)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences
            .OrderBy(reference => reference.Path, StringComparer.Ordinal)
            .ThenBy(reference => reference.Span.Start))
        {
            if (!sourceMap.TryGetValue(syntaxReference.Source.FileId, out var owner)) continue;
            id = CreateSymbolId(symbol, owner);
            return true;
        }
        id = default;
        return false;
    }

    internal static SourceReference CreateDeclarationReference(Symbol symbol,
        IReadOnlyDictionary<SourceFileId, (ProjectId ProjectId, DocumentId DocumentId)> sourceMap)
    {
        SyntaxReference declaration = symbol.DeclaringSyntaxReferences
            .OrderBy(reference => reference.Path, StringComparer.Ordinal)
            .ThenBy(reference => reference.Span.Start).First();
        return CreateSourceReference(sourceMap[declaration.Source.FileId], declaration.Location);
    }

    private static WorkspaceSymbolId CreateSymbolId(Symbol symbol,
        (ProjectId ProjectId, DocumentId DocumentId) owner) =>
        new(owner.ProjectId, owner.DocumentId, symbol.Kind, symbol.QualifiedName,
            CreateDeclarationIdentity(symbol));

    private static string CreateDeclarationIdentity(Symbol symbol) => symbol switch
    {
        LocalVariableSymbol local => string.Join('|',
            "local",
            CreateDeclarationIdentity(local.ContainingSymbol!),
            GetLocalDeclarationOrdinal(local),
            local.Name,
            Type(local.Type),
            local.IsReadonly),
        ParameterSymbol parameter => string.Join('|',
            "parameter",
            CreateDeclarationIdentity(parameter.ContainingSymbol!),
            parameter.Ordinal,
            Type(parameter.Type),
            parameter.IsReadonly),
        FunctionSymbol function => string.Join('|',
            "function",
            function.FunctionKind,
            function.QualifiedName,
            Type(function.ReturnType),
            Parameters(function.Parameters),
            function.IsStatic,
            function.IsReadonly),
        IndexerSymbol indexer => string.Join('|',
            "indexer", indexer.QualifiedName, Type(indexer.Type), Parameters(indexer.Parameters)),
        InterfaceIndexerSymbol indexer => string.Join('|',
            "interface-indexer", indexer.QualifiedName, Type(indexer.Type), Parameters(indexer.Parameters)),
        FieldSymbol field => string.Join('|',
            "field", field.QualifiedName, Type(field.Type), field.IsStatic, field.IsReadonly),
        PropertySymbol property => string.Join('|',
            "property", property.QualifiedName, Type(property.Type)),
        InterfacePropertySymbol property => string.Join('|',
            "interface-property", property.QualifiedName, Type(property.Type)),
        ConstantSymbol constant => string.Join('|',
            "constant", constant.QualifiedName, Type(constant.Type)),
        DeclaredTypeSymbol type => string.Join('|',
            "type", type.DeclarationKind, type.QualifiedName),
        NamespaceSymbol @namespace => $"namespace|{@namespace.QualifiedName}",
        _ => string.Join('|', symbol.Kind, symbol.QualifiedName,
            symbol.ToDisplayString(SymbolDisplayFormat.QualifiedSignature)),
    };

    private static string Parameters(IEnumerable<ParameterSymbol> parameters) =>
        string.Join(',', parameters.Select(parameter =>
            $"{(parameter.IsReadonly ? "readonly:" : string.Empty)}{Type(parameter.Type)}"));

    private static string Type(TypeSymbol type) =>
        type.ToDisplayString(TypeDisplayFormat.FullyQualified);

    private static int GetLocalDeclarationOrdinal(LocalVariableSymbol local)
    {
        SyntaxNode declaration = local.DeclaringSyntaxReferences.Single().Declaration;
        FunctionSymbol function = (FunctionSymbol)local.ContainingSymbol!;
        SyntaxNode functionDeclaration = function.DeclaringSyntaxReferences.Single().Declaration;
        BlockStatementSyntax? body = functionDeclaration switch
        {
            FunctionDeclarationSyntax syntax => syntax.Body,
            MethodDeclarationSyntax syntax => syntax.Body,
            ConstructorDeclarationSyntax syntax => syntax.Body,
            DestructorDeclarationSyntax syntax => syntax.Body,
            PropertyAccessorDeclarationSyntax syntax => syntax.Body,
            _ => null,
        };
        if (body is null)
            throw new InvalidOperationException(
                $"source local '{local.QualifiedName}' has no containing callable body");

        int ordinal = 0;
        foreach (VariableDeclarationStatementSyntax candidate in EnumerateLocalDeclarations(body))
        {
            if (ReferenceEquals(candidate, declaration)) return ordinal;
            ordinal++;
        }
        throw new InvalidOperationException(
            $"source local '{local.QualifiedName}' is not part of its containing callable body");
    }

    private static IEnumerable<VariableDeclarationStatementSyntax> EnumerateLocalDeclarations(
        StatementSyntax statement)
    {
        switch (statement)
        {
            case VariableDeclarationStatementSyntax declaration:
                yield return declaration;
                break;
            case BlockStatementSyntax block:
                foreach (StatementSyntax child in block.Statements)
                    foreach (VariableDeclarationStatementSyntax declaration in EnumerateLocalDeclarations(child))
                        yield return declaration;
                break;
            case IfStatementSyntax @if:
                foreach (VariableDeclarationStatementSyntax declaration in EnumerateLocalDeclarations(@if.ThenStatement))
                    yield return declaration;
                if (@if.ElseStatement is not null)
                    foreach (VariableDeclarationStatementSyntax declaration in EnumerateLocalDeclarations(@if.ElseStatement))
                        yield return declaration;
                break;
            case WhileStatementSyntax @while:
                foreach (VariableDeclarationStatementSyntax declaration in EnumerateLocalDeclarations(@while.Body))
                    yield return declaration;
                break;
            case ForStatementSyntax @for:
                if (@for.Initializer is not null)
                    foreach (VariableDeclarationStatementSyntax declaration in EnumerateLocalDeclarations(@for.Initializer))
                        yield return declaration;
                foreach (VariableDeclarationStatementSyntax declaration in EnumerateLocalDeclarations(@for.Body))
                    yield return declaration;
                break;
            case SwitchStatementSyntax @switch:
                foreach (SwitchSectionSyntax section in @switch.Sections)
                    foreach (StatementSyntax child in section.Statements)
                        foreach (VariableDeclarationStatementSyntax declaration in EnumerateLocalDeclarations(child))
                            yield return declaration;
                break;
        }
    }

    private static SourceReference CreateSourceReference(
        (ProjectId ProjectId, DocumentId DocumentId) owner, TextLocation location) =>
        new(owner.ProjectId, owner.DocumentId, location.Source.FileId,
            location.Source.Path, location.Span);
}
