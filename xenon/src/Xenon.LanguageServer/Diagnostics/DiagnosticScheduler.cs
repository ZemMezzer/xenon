using Xenon.ProjectSystem;

namespace Xenon.LanguageServer;

public sealed record DiagnosticResult(WorkspaceGeneration Generation, DocumentId DocumentId,
    DocumentVersion Version, object? Value);

/// <summary>
/// Per-document debounce/coalescing with explicit ownership of every started job until completion.
/// Scheduling after disposal begins throws <see cref="ObjectDisposedException"/>.
/// </summary>
public sealed class DiagnosticScheduler : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly LanguageServerAnalysisContextFactory _contexts;
    private readonly Func<LanguageServerAnalysisContext, Task<object?>> _analyze;
    private readonly Func<string, DiagnosticResult, Task> _publish;
    private readonly Func<Xenon.ProjectSystem.Workspace, string, long, CancellationToken,
        LanguageServerAnalysisContext?> _acquireContext;
    private readonly Func<Xenon.ProjectSystem.Workspace, long, bool> _canPublish;
    private readonly Func<Xenon.ProjectSystem.Workspace, long, string, DiagnosticResult,
        Task<bool>> _publishIfCurrent;
    private readonly Dictionary<string, Job> _currentByKey = new(DocumentUri.PathComparer);
    private readonly HashSet<Job> _inFlight = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _disposeTask;
    private bool _disposing;
    private bool _disposed;

    public DiagnosticScheduler(LanguageServerAnalysisContextFactory contexts,
        Func<LanguageServerAnalysisContext, Task<object?>> analyze,
        Func<string, DiagnosticResult, Task> publish, TimeSpan? debounce = null,
        Func<Xenon.ProjectSystem.Workspace, string, long, CancellationToken,
            LanguageServerAnalysisContext?>? acquireContext = null,
        Func<Xenon.ProjectSystem.Workspace, long, bool>? canPublish = null,
        Func<Xenon.ProjectSystem.Workspace, long, string, DiagnosticResult,
            Task<bool>>? publishIfCurrent = null)
    {
        _contexts = contexts;
        _analyze = analyze;
        _publish = publish;
        _acquireContext = acquireContext ?? ((workspace, uri, _, cancellationToken) =>
            _contexts.Create(workspace, uri, staleSensitive: true, cancellationToken));
        _canPublish = canPublish ?? ((_, _) => true);
        _publishIfCurrent = publishIfCurrent ?? (async (workspace, generation, uri, result) =>
        {
            if (!_canPublish(workspace, generation)) return false;
            await _publish(uri, result).ConfigureAwait(false);
            return true;
        });
        Debounce = debounce ?? TimeSpan.FromMilliseconds(200);
        if (Debounce < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(debounce));
    }

    public TimeSpan Debounce { get; }

    public int CurrentJobCount
    {
        get { lock (_gate) return _currentByKey.Count; }
    }

    public int InFlightJobCount
    {
        get { lock (_gate) return _inFlight.Count; }
    }

    public bool IsDisposed
    {
        get { lock (_gate) return _disposed; }
    }

    public void Schedule(Xenon.ProjectSystem.Workspace workspace, string uri,
        long workspaceSetGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string key = CreateKey(workspace, uri);
        Job? previous;
        lock (_gate)
        {
            ThrowIfNotAcceptingWork();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            var job = new Job(key, cancellation, workspaceSetGeneration);
            _currentByKey.TryGetValue(key, out previous);
            _currentByKey[key] = job;
            _inFlight.Add(job);
            // Disposal uses the same gate, so every visible in-flight job has an awaitable task.
            job.Task = RunAsync(workspace, uri, job);
        }
        if (previous is not null) TryCancel(previous.Cancellation);
    }

    public void ScheduleMany(Xenon.ProjectSystem.Workspace workspace, IEnumerable<string> uris,
        long workspaceSetGeneration = 0)
    {
        ArgumentNullException.ThrowIfNull(uris);
        foreach (string uri in uris.Distinct(StringComparer.Ordinal))
            Schedule(workspace, uri, workspaceSetGeneration);
    }

    public bool Cancel(Xenon.ProjectSystem.Workspace workspace, string uri)
    {
        string key = CreateKey(workspace, uri);
        Job? job;
        lock (_gate)
        {
            if (!_currentByKey.Remove(key, out job)) return false;
        }
        // Cancellation clears coalescing ownership, but _inFlight retains lifetime ownership.
        TryCancel(job.Cancellation);
        return true;
    }

    private async Task RunAsync(Xenon.ProjectSystem.Workspace workspace, string uri, Job job)
    {
        CancellationToken analysisCancellation = default;
        try
        {
            await Task.Delay(Debounce, job.Cancellation.Token).ConfigureAwait(false);
            using LanguageServerAnalysisContext? context = _acquireContext(workspace, uri,
                job.WorkspaceSetGeneration, job.Cancellation.Token);
            if (context is null) return;
            analysisCancellation = context.CancellationToken;
            WorkspaceGeneration generation = context.Snapshot.Generation;
            DocumentVersion version = context.Document.Version;
            object? value = await _analyze(context).ConfigureAwait(false);
            job.Cancellation.Token.ThrowIfCancellationRequested();
            analysisCancellation.ThrowIfCancellationRequested();
            _shutdown.Token.ThrowIfCancellationRequested();

            WorkspaceSnapshot current = workspace.CurrentSnapshot;
            if (current.Generation != generation ||
                !current.TryGetDocument(context.Document.Id, out DocumentSnapshot? currentDocument) ||
                currentDocument!.Version != version || !IsCurrent(job) ||
                !_canPublish(workspace, job.WorkspaceSetGeneration))
                return;
            await _publishIfCurrent(workspace, job.WorkspaceSetGeneration, uri,
                new DiagnosticResult(generation, context.Document.Id, version, value))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            job.Cancellation.IsCancellationRequested ||
            analysisCancellation.IsCancellationRequested ||
            _shutdown.IsCancellationRequested)
        {
            // Replacement, explicit cancellation, stale Workspace generation, and shutdown are
            // all expected diagnostic cancellation paths.
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(job);
                if (_currentByKey.TryGetValue(job.Key, out Job? current) &&
                    ReferenceEquals(current, job))
                    _currentByKey.Remove(job.Key);
            }
            job.Cancellation.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask is not null) return new ValueTask(_disposeTask);
            _disposing = true;
            Job[] owned = _inFlight.ToArray();
            _currentByKey.Clear();
            _disposeTask = DisposeCoreAsync(owned);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Job[] owned)
    {
        try
        {
            TryCancel(_shutdown);
            foreach (Job job in owned) TryCancel(job.Cancellation);
            await Task.WhenAll(owned.Select(job => job.Task)).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _currentByKey.Clear();
                _disposed = true;
            }
            _shutdown.Dispose();
        }
    }

    private static string CreateKey(Xenon.ProjectSystem.Workspace workspace, string uri)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        return $"{workspace.Id}:{DocumentUri.ToNormalizedPath(uri)}";
    }

    private void ThrowIfNotAcceptingWork()
    {
        if (_disposing || _disposed)
            throw new ObjectDisposedException(nameof(DiagnosticScheduler),
                "Diagnostic scheduling is unavailable after disposal begins.");
    }

    private bool IsCurrent(Job job)
    {
        lock (_gate)
            return _currentByKey.TryGetValue(job.Key, out Job? current) &&
                ReferenceEquals(current, job);
    }

    private sealed class Job(string key, CancellationTokenSource cancellation,
        long workspaceSetGeneration)
    {
        public string Key { get; } = key;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public long WorkspaceSetGeneration { get; } = workspaceSetGeneration;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    private static void TryCancel(CancellationTokenSource cancellation)
    {
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }
}
