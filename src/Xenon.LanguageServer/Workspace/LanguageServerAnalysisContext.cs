using Xenon.ProjectSystem;

namespace Xenon.LanguageServer;

public sealed class LanguageServerAnalysisContext : IDisposable
{
    private readonly WorkspaceAnalysisRequest _request;

    internal LanguageServerAnalysisContext(WorkspaceAnalysisRequest request, ProjectSnapshot project,
        DocumentSnapshot document)
    {
        _request = request;
        Snapshot = request.Snapshot;
        Project = project;
        Document = document;
    }

    public WorkspaceSnapshot Snapshot { get; }
    public ProjectSnapshot Project { get; }
    public DocumentSnapshot Document { get; }
    public CancellationToken CancellationToken => _request.CancellationToken;
    public void Dispose() => _request.Dispose();
}

public sealed class LanguageServerAnalysisContextFactory(DocumentContextResolver resolver)
{
    public LanguageServerAnalysisContext Create(Xenon.ProjectSystem.Workspace workspace, string uri,
        bool staleSensitive = true, CancellationToken cancellationToken = default)
    {
        WorkspaceAnalysisRequest request = workspace.CreateAnalysisRequest(staleSensitive,
            cancellationToken);
        try
        {
            DocumentContext context = resolver.ResolvePrimary(request.Snapshot, uri);
            ProjectSnapshot project = request.Snapshot.GetProject(context.ProjectId);
            DocumentSnapshot document = project.GetDocument(context.DocumentId);
            return new LanguageServerAnalysisContext(request, project, document);
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }
}
