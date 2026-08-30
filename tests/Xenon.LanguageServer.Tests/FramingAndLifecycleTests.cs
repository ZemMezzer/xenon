using System.Text;
using System.Text.Json;
using Xenon.LanguageServer.Protocol;

namespace Xenon.LanguageServer.Tests;

public sealed class FramingAndLifecycleTests
{
    [Fact]
    public async Task FramingUsesUtf8ByteLengthAndReadsPartialStreams()
    {
        using var output = new MemoryStream();
        using (var writer = new LspMessageWriter(output))
            await writer.WriteNotificationAsync("test", new { text = "Ж😀" });
        byte[] bytes = output.ToArray();
        IReadOnlyList<JsonElement> messages = await LspTestProtocol.ReadFramesAsync(bytes);
        Assert.Single(messages);
        Assert.Equal("Ж😀", messages[0].GetProperty("params").GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("X-Length: 2\r\n\r\n{}")]
    [InlineData("Content-Length: nope\r\n\r\n")]
    [InlineData("Content-Length: 4\n\nnull")]
    public async Task MalformedFramingIsRejected(string framed)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(framed));
        await Assert.ThrowsAsync<LspFramingException>(async () =>
            await new LspMessageReader(input).ReadAsync());
    }

    [Fact]
    public async Task HostCompletesLifecycleAndKeepsStdoutProtocolOnly()
    {
        byte[] inputBytes = [
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "initialized", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = "stop", method = "shutdown", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();
        using var errors = new StringWriter();

        int exitCode = await new LspServerHost(input, output, errors).RunAsync();

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, errors.ToString());
        IReadOnlyList<JsonElement> responses = await LspTestProtocol.ReadFramesAsync(output.ToArray());
        Assert.Equal(2, responses.Count);
        Assert.Equal(1, responses[0].GetProperty("id").GetInt32());
        Assert.Equal(2, responses[0].GetProperty("result").GetProperty("capabilities")
            .GetProperty("textDocumentSync").GetProperty("change").GetInt32());
        Assert.Equal("stop", responses[1].GetProperty("id").GetString());
        Assert.True(responses[1].TryGetProperty("result", out JsonElement shutdownResult));
        Assert.Equal(JsonValueKind.Null, shutdownResult.ValueKind);
    }

    [Fact]
    public async Task HostReturnsLifecycleAndMethodErrors()
    {
        byte[] inputBytes = [
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 1, method = "unknown" }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 2, method = "shutdown" }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 3, method = "initialize", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 4, method = "initialize", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();
        int exit = await new LspServerHost(input, output).RunAsync();
        IReadOnlyList<JsonElement> messages = await LspTestProtocol.ReadFramesAsync(output.ToArray());
        Assert.Equal(1, exit);
        Assert.Equal(LspErrorCodes.ServerNotInitialized,
            messages[0].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(LspErrorCodes.ServerNotInitialized,
            messages[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(LspErrorCodes.InvalidRequest,
            messages[3].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task FramedMalformedJsonGetsParseError()
    {
        byte[] malformed = Encoding.UTF8.GetBytes("{");
        byte[] inputBytes = [
            .. Encoding.ASCII.GetBytes($"Content-Length: {malformed.Length}\r\n\r\n"), .. malformed,
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();
        await new LspServerHost(input, output).RunAsync();
        JsonElement response = Assert.Single(await LspTestProtocol.ReadFramesAsync(output.ToArray()));
        Assert.Equal(LspErrorCodes.ParseError,
            response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.True(response.TryGetProperty("id", out JsonElement id));
        Assert.Equal(JsonValueKind.Null, id.ValueKind);
    }

    [Fact]
    public async Task InvalidRequestWithoutIdSerializesRequiredNullId()
    {
        byte[] inputBytes = [
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0" }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();

        await new LspServerHost(input, output).RunAsync();

        JsonElement response = Assert.Single(await LspTestProtocol.ReadFramesAsync(output.ToArray()));
        Assert.Equal(LspErrorCodes.InvalidRequest,
            response.GetProperty("error").GetProperty("code").GetInt32());
        Assert.True(response.TryGetProperty("id", out JsonElement id));
        Assert.Equal(JsonValueKind.Null, id.ValueKind);
    }

    [Fact]
    public async Task ErrorResponsesPreserveKnownStringAndNumericIdsExactly()
    {
        byte[] inputBytes = [
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "initialized", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = "alpha", method = "missing/string" }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 42, method = "missing/number" }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 2, method = "shutdown", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();

        await new LspServerHost(input, output).RunAsync();

        IReadOnlyList<JsonElement> responses = await LspTestProtocol.ReadFramesAsync(output.ToArray());
        JsonElement stringResponse = Assert.Single(responses.Where(response =>
            response.GetProperty("id").ValueKind == JsonValueKind.String));
        JsonElement numericResponse = Assert.Single(responses.Where(response =>
            response.GetProperty("id").ValueKind == JsonValueKind.Number &&
            response.GetProperty("id").GetInt32() == 42));
        Assert.Equal("alpha", stringResponse.GetProperty("id").GetString());
        Assert.Equal(42, numericResponse.GetProperty("id").GetInt32());
        Assert.Equal(LspErrorCodes.MethodNotFound,
            stringResponse.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(LspErrorCodes.MethodNotFound,
            numericResponse.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task InvalidRequestPreservesRecognizableIdAcrossEarlyValidationFailure()
    {
        byte[] inputBytes = [
            .. LspTestProtocol.Frame(new { jsonrpc = "1.0", id = "known", method = "anything" }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();

        await new LspServerHost(input, output).RunAsync();

        JsonElement response = Assert.Single(await LspTestProtocol.ReadFramesAsync(output.ToArray()));
        Assert.Equal("known", response.GetProperty("id").GetString());
        Assert.Equal(LspErrorCodes.InvalidRequest,
            response.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task NotificationNeverProducesAResponse()
    {
        byte[] inputBytes = [
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "unknown/notification" }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();

        Assert.Equal(1, await new LspServerHost(input, output).RunAsync());
        Assert.Empty(await LspTestProtocol.ReadFramesAsync(output.ToArray()));
    }

    [Fact]
    public async Task CancelRequestRoutesToActiveRequest()
    {
        byte[] inputBytes = [
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "initialized", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = "slow", method = "test/slow" }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "$/cancelRequest", @params = new { id = "slow" } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 2, method = "shutdown", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();
        var host = new LspServerHost(input, output,
            additionalRequestHandler: async (_, _, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return null;
        });

        Assert.Equal(0, await host.RunAsync());
        IReadOnlyList<JsonElement> messages = await LspTestProtocol.ReadFramesAsync(output.ToArray());
        JsonElement cancellation = Assert.Single(messages.Where(message =>
            message.GetProperty("id").ValueKind == JsonValueKind.String));
        Assert.Equal(LspErrorCodes.RequestCancelled,
            cancellation.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task FatalFramingErrorCancelsAndDrainsActiveRequestBeforeDisposal()
    {
        using var input = new PushReadStream();
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var host = new LspServerHost(input, output, errors,
            additionalRequestHandler: async (_, _, _, token) =>
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    await release.Task;
                    throw;
                }
                return null;
            });
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
    }

    [Fact]
    public async Task PrematureEofCancelsAndDrainsActiveRequestBeforeDisposal()
    {
        using var input = new PushReadStream();
        using var output = new MemoryStream();
        var started = Signal();
        var cancelled = Signal();
        var release = Signal();
        var host = new LspServerHost(input, output,
            additionalRequestHandler: DrainingHandler(started, cancelled, release));
        Task<int> run = host.RunAsync();
        PushInitializedSlowRequest(input);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        input.Complete();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();
        Assert.False(run.IsCompleted);
        release.TrySetResult();

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, host.ActiveRequestCount);
    }

    [Fact]
    public async Task ExternalCancellationDrainsEveryActiveRequest()
    {
        using var input = new PushReadStream();
        using var output = new MemoryStream();
        using var lifetime = new CancellationTokenSource();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Signal();
        int starts = 0, cancellations = 0;
        var host = new LspServerHost(input, output,
            additionalRequestHandler: async (_, _, _, token) =>
            {
                if (Interlocked.Increment(ref starts) == 2) bothStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException)
                {
                    if (Interlocked.Increment(ref cancellations) == 2) bothCancelled.TrySetResult();
                    await release.Task;
                    throw;
                }
                return null;
            });
        Task<int> run = host.RunAsync(lifetime.Token);
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
            id = "slow-a",
            method = "test/slow"
        }));
        input.Push(LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0",
            id = "slow-b",
            method = "test/slow"
        }));
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lifetime.Cancel();
        await bothCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Yield();
        Assert.False(run.IsCompleted);
        release.TrySetResult();

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, host.ActiveRequestCount);
    }

    private static Func<LanguageServerSession, string, JsonElement?, CancellationToken,
        Task<object?>> DrainingHandler(TaskCompletionSource started,
        TaskCompletionSource cancelled, TaskCompletionSource release) => async (_, _, _, token) =>
    {
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
