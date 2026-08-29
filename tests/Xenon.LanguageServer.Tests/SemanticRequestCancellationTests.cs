using System.Text.Json;
using Xenon.LanguageServer.Protocol;

namespace Xenon.LanguageServer.Tests;

public sealed class SemanticRequestCancellationTests
{
    [Fact]
    public async Task WorkspaceStaleCancellationReturnsRequestCancelledAndNextRequestSucceeds()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "one\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var staleStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] inputBytes = StandardPrefix(file, uri)
            .Concat(LspTestProtocol.Frame(new
            {
                jsonrpc = "2.0", id = "stale", method = "test/semantic-wait",
                @params = new { uri },
            }))
            .Concat(LspTestProtocol.Frame(new
            {
                jsonrpc = "2.0", method = "textDocument/didChange",
                @params = new
                {
                    textDocument = new { uri, version = 2 },
                    contentChanges = new[] { new { text = "two\n" } },
                },
            }))
            .Concat(LspTestProtocol.Frame(new
            {
                jsonrpc = "2.0", id = "next", method = "test/semantic-current",
                @params = new { uri },
            }))
            .Concat(StandardSuffix())
            .ToArray();
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();
        var host = new LspServerHost(input, output, additionalRequestHandler:
            (session, method, parameters, token) => ExecuteTestSemanticAsync(session, method,
                parameters, token, staleStarted));

        Assert.Equal(0, await host.RunAsync());

        IReadOnlyList<JsonElement> responses = await LspTestProtocol.ReadFramesAsync(output.ToArray());
        JsonElement stale = FindById(responses, "stale");
        JsonElement next = FindById(responses, "next");
        Assert.True(staleStarted.Task.IsCompleted);
        Assert.Equal(LspErrorCodes.RequestCancelled,
            stale.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(stale.GetProperty("error").GetProperty("code").GetInt32() ==
            LspErrorCodes.InternalError);
        Assert.Equal(2, next.GetProperty("result").GetProperty("version").GetInt32());
        Assert.Equal(0, host.ActiveRequestCount);
    }

    [Fact]
    public async Task ExitShutdownCancellationReturnsRequestCancelledAndCleansRequestState()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "one\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] inputBytes = StandardPrefix(file, uri)
            .Concat(LspTestProtocol.Frame(new
            {
                jsonrpc = "2.0", id = "active", method = "test/semantic-wait",
                @params = new { uri },
            }))
            .Concat(StandardSuffix())
            .ToArray();
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();
        var host = new LspServerHost(input, output, additionalRequestHandler:
            (session, method, parameters, token) => ExecuteTestSemanticAsync(session, method,
                parameters, token, started));

        Assert.Equal(0, await host.RunAsync());

        JsonElement cancelled = FindById(
            await LspTestProtocol.ReadFramesAsync(output.ToArray()), "active");
        Assert.True(started.Task.IsCompleted);
        Assert.Equal(LspErrorCodes.RequestCancelled,
            cancelled.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(0, host.ActiveRequestCount);
    }

    [Theory]
    [InlineData("test/invalid-operation")]
    [InlineData("test/unrelated-cancellation")]
    public async Task UnownedFailuresRemainInternalErrors(string method)
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "one\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        byte[] inputBytes = StandardPrefix(file, uri)
            .Concat(LspTestProtocol.Frame(new
            {
                jsonrpc = "2.0", id = "failure", method, @params = new { uri },
            }))
            .Concat(StandardSuffix())
            .ToArray();
        using var input = new MemoryStream(inputBytes);
        using var output = new MemoryStream();
        using var errors = new StringWriter();
        var host = new LspServerHost(input, output, errors,
            (session, requestMethod, parameters, token) => session.ExecuteSemanticRequestAsync(
                RequireUri(parameters), _ => requestMethod == "test/invalid-operation"
                    ? Task.FromException<object?>(new InvalidOperationException("injected"))
                    : Task.FromException<object?>(new OperationCanceledException("unowned")), token));

        Assert.Equal(0, await host.RunAsync());

        JsonElement failure = FindById(
            await LspTestProtocol.ReadFramesAsync(output.ToArray()), "failure");
        Assert.Equal(LspErrorCodes.InternalError,
            failure.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Contains("LSP request", errors.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, host.ActiveRequestCount);
    }

    private static Task<object?> ExecuteTestSemanticAsync(LanguageServerSession session,
        string method, JsonElement? parameters, CancellationToken token,
        TaskCompletionSource started)
    {
        string uri = RequireUri(parameters);
        return session.ExecuteSemanticRequestAsync(uri, async context =>
        {
            if (method == "test/semantic-wait")
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            }
            return new
            {
                generation = context.Snapshot.Generation.Value,
                version = LspDocumentVersions.ToLsp(context.Document.Version),
            };
        }, token);
    }

    private static byte[] StandardPrefix(string file, string uri) =>
    [
        .. LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { rootUri = DocumentUri.FromPath(file).AbsoluteUri },
        }),
        .. LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0", method = "initialized", @params = new { },
        }),
        .. LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0", method = "textDocument/didOpen",
            @params = new { textDocument = new { uri, version = 1, text = "one\n" } },
        }),
    ];

    private static byte[] StandardSuffix() =>
    [
        .. LspTestProtocol.Frame(new
        {
            jsonrpc = "2.0", id = 99, method = "shutdown", @params = new { },
        }),
        .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
    ];

    private static string RequireUri(JsonElement? parameters) =>
        parameters!.Value.GetProperty("uri").GetString()!;

    private static JsonElement FindById(IEnumerable<JsonElement> responses, string id) =>
        Assert.Single(responses.Where(response =>
            response.TryGetProperty("id", out JsonElement responseId) &&
            responseId.ValueKind == JsonValueKind.String && responseId.GetString() == id));
}
