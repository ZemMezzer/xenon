using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Xenon.LanguageServer.Protocol;

namespace Xenon.LanguageServer.Tests;

internal sealed class TestDirectory : IDisposable
{
    public TestDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "xenon-lsp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string Write(string relativePath, string text)
    {
        string path = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }
    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

internal sealed class PushReadStream : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[]? _current;
    private int _offset;

    public void Push(byte[] bytes) => Assert.True(_chunks.Writer.TryWrite(bytes));
    public void Complete() => _chunks.Writer.TryComplete();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        while (_current is null || _offset == _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
            if (!_chunks.Reader.TryRead(out _current)) continue;
            _offset = 0;
        }
        int count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class LspProcessClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly LspMessageReader _reader;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly Channel<JsonElement> _messages = Channel.CreateUnbounded<JsonElement>();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _pump;
    private int _nextId;

    public LspProcessClient(string cliPath)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(cliPath);
        start.ArgumentList.Add("lsp");
        _process = Process.Start(start)!;
        _reader = new LspMessageReader(_process.StandardOutput.BaseStream);
        _pump = PumpAsync();
    }

    public (int Id, Task<JsonElement> Response) StartRequest(string method,
        object? parameters = null)
    {
        int id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.True(_requests.TryAdd(id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            completion));
        _ = WriteAsync(new { jsonrpc = "2.0", id, method, @params = parameters });
        return (id, completion.Task);
    }

    public Task<JsonElement> RequestAsync(string method, object? parameters = null) =>
        StartRequest(method, parameters).Response.WaitAsync(TimeSpan.FromSeconds(20));

    public Task NotifyAsync(string method, object? parameters = null) =>
        WriteAsync(new { jsonrpc = "2.0", method, @params = parameters });

    public Task SendRawPayloadAsync(string payload)
    {
        byte[] body = Encoding.UTF8.GetBytes(payload);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        return WriteFrameAsync([.. header, .. body]);
    }

    public Task SendRawFrameAsync(byte[] frame) => WriteFrameAsync(frame);

    public async Task CloseInputAsync()
    {
        await _writeGate.WaitAsync();
        try { _process.StandardInput.Close(); }
        finally { _writeGate.Release(); }
    }

    public async Task<JsonElement> WaitForNotificationAsync(string method,
        Func<JsonElement, bool>? predicate = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (await _messages.Reader.WaitToReadAsync(timeout.Token))
        {
            while (_messages.Reader.TryRead(out JsonElement message))
                if (message.TryGetProperty("method", out JsonElement actual) &&
                    actual.GetString() == method && (predicate is null || predicate(message)))
                    return message;
        }
        throw new TimeoutException($"LSP notification '{method}' was not received.");
    }

    public async Task<JsonElement> WaitForMessageAsync(Func<JsonElement, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (await _messages.Reader.WaitToReadAsync(timeout.Token))
            while (_messages.Reader.TryRead(out JsonElement message))
                if (predicate(message)) return message;
        throw new TimeoutException("A matching LSP message was not received.");
    }

    public async Task<(int ExitCode, string Error)> ShutdownAsync()
    {
        JsonElement shutdown = await RequestAsync("shutdown", new { });
        Assert.True(shutdown.TryGetProperty("result", out JsonElement result));
        Assert.Equal(JsonValueKind.Null, result.ValueKind);
        await NotifyAsync("exit");
        _process.StandardInput.Close();
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await _pump.WaitAsync(TimeSpan.FromSeconds(5));
        return (_process.ExitCode, await _process.StandardError.ReadToEndAsync());
    }

    public async Task<(int ExitCode, string Error)> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await _pump.WaitAsync(TimeSpan.FromSeconds(5));
        return (_process.ExitCode, await _process.StandardError.ReadToEndAsync());
    }

    private async Task WriteAsync(object message)
    {
        byte[] frame = LspTestProtocol.Frame(message);
        await WriteFrameAsync(frame);
    }

    private async Task WriteFrameAsync(byte[] frame)
    {
        await _writeGate.WaitAsync();
        try
        {
            await _process.StandardInput.BaseStream.WriteAsync(frame);
            await _process.StandardInput.BaseStream.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task PumpAsync()
    {
        try
        {
            while (await _reader.ReadAsync() is { } payload)
            {
                using JsonDocument document = JsonDocument.Parse(payload);
                JsonElement message = document.RootElement.Clone();
                if (message.TryGetProperty("id", out JsonElement id) &&
                    id.ValueKind is JsonValueKind.Number or JsonValueKind.String &&
                    _requests.TryRemove(id.GetRawText().Trim('"'), out var completion))
                    completion.TrySetResult(message);
                else
                    await _messages.Writer.WriteAsync(message);
            }
            foreach (var request in _requests.Values)
                request.TrySetException(new EndOfStreamException(
                    "The language server exited before returning a response."));
            _requests.Clear();
            _messages.Writer.TryComplete();
        }
        catch (Exception exception)
        {
            foreach (var request in _requests.Values) request.TrySetException(exception);
            _messages.Writer.TryComplete(exception);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }
        _writeGate.Dispose();
        _process.Dispose();
    }
}

internal static class LspTestProtocol
{
    public static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    public static byte[] Frame(object value)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        return [.. header, .. payload];
    }

    public static async Task<IReadOnlyList<JsonElement>> ReadFramesAsync(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var reader = new LspMessageReader(stream);
        var result = new List<JsonElement>();
        while (await reader.ReadAsync() is { } payload)
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            result.Add(document.RootElement.Clone());
        }
        return result;
    }
}
