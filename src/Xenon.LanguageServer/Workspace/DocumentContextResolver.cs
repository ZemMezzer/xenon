using System.Collections.Immutable;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer;

public sealed record DocumentContext(ProjectId ProjectId, DocumentId DocumentId,
    string PhysicalPath, bool IsRootProject);

/// <summary>Routes URI/path identity only; semantic and text state remains in Workspace snapshots.</summary>
public sealed class DocumentContextResolver
{
    public ImmutableArray<DocumentContext> ResolveAll(WorkspaceSnapshot snapshot, string uri)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        string path = DocumentUri.ToNormalizedPath(uri);
        return snapshot.Projects
            .SelectMany(project => project.Documents
                .Where(document => document.PhysicalPath is not null &&
                    DocumentUri.PathComparer.Equals(
                        DocumentUri.NormalizePath(document.PhysicalPath), path))
                .Select(document => new DocumentContext(project.Id, document.Id,
                    document.PhysicalPath!, project.Id == snapshot.RootProjectId)))
            .OrderByDescending(context => context.IsRootProject)
            .ThenBy(context => snapshot.GetProject(context.ProjectId).Configuration.Identity,
                DocumentUri.PathComparer)
            .ThenBy(context => context.DocumentId)
            .ToImmutableArray();
    }

    public DocumentContext ResolvePrimary(WorkspaceSnapshot snapshot, string uri)
    {
        ImmutableArray<DocumentContext> contexts = ResolveAll(snapshot, uri);
        return contexts.IsEmpty
            ? throw new KeyNotFoundException($"Document URI '{uri}' is not part of Workspace generation {snapshot.Generation}.")
            : contexts[0];
    }
}
