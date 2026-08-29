namespace Xenon.ProjectSystem;

/// <summary>A request-scoped snapshot capture and cancellation lifetime.</summary>
public sealed class WorkspaceAnalysisRequest : IDisposable
{
    private readonly CancellationTokenSource? _linkedCancellation;

    internal WorkspaceAnalysisRequest(WorkspaceSnapshot snapshot, CancellationToken externalToken,
        CancellationToken staleToken, bool staleSensitive)
    {
        Snapshot = snapshot;
        IsStaleSensitive = staleSensitive;
        if (staleSensitive || externalToken.CanBeCanceled)
        {
            _linkedCancellation = staleSensitive
                ? CancellationTokenSource.CreateLinkedTokenSource(externalToken, staleToken)
                : CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            CancellationToken = _linkedCancellation.Token;
        }
        else CancellationToken = CancellationToken.None;
    }

    public WorkspaceSnapshot Snapshot { get; }
    public bool IsStaleSensitive { get; }
    public CancellationToken CancellationToken { get; }
    public void Dispose() => _linkedCancellation?.Dispose();
}
