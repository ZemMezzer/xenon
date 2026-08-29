using System.Collections.Concurrent;

namespace Xenon.LanguageServer;

public sealed class RequestCancellationRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    private readonly CancellationTokenSource _shutdown = new();

    public int ActiveCount => _active.Count;

    public CancellationTokenSource Register(string requestId)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        if (!_active.TryAdd(requestId, source))
        {
            source.Dispose();
            throw new InvalidOperationException($"JSON-RPC request id {requestId} is already active.");
        }
        return source;
    }

    public void Complete(string requestId, CancellationTokenSource source)
    {
        _active.TryRemove(new KeyValuePair<string, CancellationTokenSource>(requestId, source));
        source.Dispose();
    }

    public bool Cancel(string requestId)
    {
        if (!_active.TryGetValue(requestId, out CancellationTokenSource? source)) return false;
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void CancelAll()
    {
        try
        {
            if (!_shutdown.IsCancellationRequested) _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        CancelAll();
        foreach (CancellationTokenSource source in _active.Values) source.Dispose();
        _active.Clear();
        _shutdown.Dispose();
    }
}
