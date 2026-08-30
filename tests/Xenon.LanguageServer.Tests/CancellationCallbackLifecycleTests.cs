using System.Text;
using System.Text.Json;
using Xenon.LanguageServer.Protocol;

namespace Xenon.LanguageServer.Tests;

public sealed class CancellationCallbackLifecycleTests
{
    [Fact]
    public async Task FatalExitDrainsRequestWhenItsCancellationCallbackThrows()
    {
        using var input = new PushReadStream();
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var host = new LspServerHost(input, output, errors,
            additionalRequestHandler: ThrowingCancellationHandler(started, cancelled, release));
        Task<int> run = host.RunAsync();
        PushInitializedSlowRequest(input);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        input.Push(Encoding.ASCII.GetBytes("Content-Length: nope\r\n\r\n"));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();
        Assert.False(run.IsCompleted);
        release.TrySetResult();

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, host.ActiveRequestCount);
        Assert.Contains("LSP framing error", errors.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("request cancellation failed", errors.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task NormalShutdownDrainsRequestWhenItsCancellationCallbackThrows()
    {
        using var input = new PushReadStream();
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var host = new LspServerHost(input, output, errors,
            additionalRequestHandler: ThrowingCancellationHandler(started, cancelled, release));
        Task<int> run = host.RunAsync();
        PushInitializedSlowRequest(input);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        input.Push(LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0",
            id = "shutdown",
            method = "shutdown",
            @params = new { }
        }));
        input.Push(LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0",
            method = "exit"
        }));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();
        Assert.False(run.IsCompleted);
        release.TrySetResult();

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, host.ActiveRequestCount);
        Assert.Equal(string.Empty, errors.ToString());
        IReadOnlyList<JsonElement> responses =
            await LspTestProtocol.ReadFramesAsync(output.ToArray());
        Assert.Contains(responses, response =>
            response.TryGetProperty("id", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String && id.GetString() == "shutdown");
        Assert.Contains(responses, response =>
            response.TryGetProperty("id", out JsonElement id) &&
            id.ValueKind == JsonValueKind.String && id.GetString() == "slow" &&
            response.GetProperty("error").GetProperty("code").GetInt32() ==
            LspErrorCodes.RequestCancelled);
    }

    private static Func<LanguageServerSession, string, JsonElement?, CancellationToken,
        Task<object?>> ThrowingCancellationHandler(TaskCompletionSource started,
        TaskCompletionSource cancelled, TaskCompletionSource release) => async (_, _, _, token) =>
    {
        using CancellationTokenRegistration registration = token.Register(() =>
            throw new InvalidOperationException("consumer cancellation callback failure"));
        started.TrySetResult();
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        catch (OperationCanceledException)
        {
            cancelled.TrySetResult();
            await release.Task;
            throw;
        }
        return null;
    };

    private static void PushInitializedSlowRequest(PushReadStream input)
    {
        input.Push(LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize",
            @params = new { }
        }));
        input.Push(LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0",
            method = "initialized",
            @params = new { }
        }));
        input.Push(LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0",
            id = "slow",
            method = "test/slow"
        }));
    }

    private static TaskCompletionSource Signal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
