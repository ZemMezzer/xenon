using System.Text.Json;
using Xenon.LanguageServer.Protocol;

namespace Xenon.LanguageServer;

public sealed class LspServerHost(Stream input, Stream output, TextWriter? error = null,
    Func<LanguageServerSession, string, JsonElement?, CancellationToken, Task<object?>>?
        additionalRequestHandler = null)
{
    private readonly LspMessageReader _reader = new(input);
    private readonly LspMessageWriter _writer = new(output);
    private readonly TextWriter _error = error ?? TextWriter.Null;
    private readonly RequestCancellationRegistry _requests = new();
    private readonly Func<LanguageServerSession, string, JsonElement?, CancellationToken,
        Task<object?>>? _additionalRequestHandler = additionalRequestHandler;

    public int ActiveRequestCount => _requests.ActiveCount;

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var session = new LanguageServerSession(
            (method, parameters) => _writer.WriteNotificationAsync(method, parameters), _error);
        var active = new List<Task>();
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   session.State != LanguageServerLifecycleState.Exited)
            {
                byte[]? payload;
                try { payload = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _requests.CancelAll();
                    return 1;
                }
                catch (LspFramingException exception)
                {
                    await _error.WriteLineAsync($"LSP framing error: {exception.Message}");
                    _requests.CancelAll();
                    return 1;
                }
                if (payload is null)
                {
                    _requests.CancelAll();
                    return session.State == LanguageServerLifecycleState.ShutdownRequested ? 0 : 1;
                }

                JsonElement message;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(payload);
                    message = document.RootElement.Clone();
                }
                catch (JsonException exception)
                {
                    await _writer.WriteErrorAsync(null, LspErrorCodes.ParseError,
                        "Invalid JSON payload.", exception.Message, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!TryValidateMessage(message, out string? method, out JsonElement? id,
                        out JsonElement? parameters, out string? validationError))
                {
                    await _writer.WriteErrorAsync(id, LspErrorCodes.InvalidRequest,
                        validationError!, cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (id is null)
                {
                    if (method == "$/cancelRequest")
                    {
                        RouteCancellation(parameters);
                        continue;
                    }
                    try
                    {
                        await session.HandleNotificationAsync(method!, parameters, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await _error.WriteLineAsync(
                            $"LSP notification '{method}' failed: {exception.Message}");
                    }
                    if (method == "exit") break;
                    continue;
                }

                string requestKey = NormalizeId(id.Value);
                CancellationTokenSource requestCancellation;
                try { requestCancellation = _requests.Register(requestKey); }
                catch (InvalidOperationException exception)
                {
                    await _writer.WriteErrorAsync(id, LspErrorCodes.InvalidRequest,
                        exception.Message, cancellationToken: cancellationToken).ConfigureAwait(false);
                    continue;
                }
                Task task = HandleRequestAsync(session, method!, parameters, id.Value,
                    requestKey, requestCancellation);
                active.Add(task);

                // Lifecycle requests are serialized so transitions and their responses are ordered.
                if (method is "initialize" or "shutdown") await task.ConfigureAwait(false);
                active.RemoveAll(item => item.IsCompleted);
            }

            _requests.CancelAll();
            await Task.WhenAll(active).ConfigureAwait(false);
            return session.ExitCode;
        }
        finally
        {
            _requests.Dispose();
            _writer.Dispose();
        }
    }

    private async Task HandleRequestAsync(LanguageServerSession session, string method,
        JsonElement? parameters, JsonElement id, string requestKey,
        CancellationTokenSource requestCancellation)
    {
        try
        {
            object? result;
            try
            {
                result = await session.HandleRequestAsync(method, parameters,
                    requestCancellation.Token).ConfigureAwait(false);
            }
            catch (JsonRpcException exception) when (
                exception.Code == LspErrorCodes.MethodNotFound && _additionalRequestHandler is not null)
            {
                result = await _additionalRequestHandler(session, method, parameters,
                    requestCancellation.Token).ConfigureAwait(false);
            }
            await _writer.WriteResultAsync(id, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (requestCancellation.IsCancellationRequested)
        {
            await _writer.WriteErrorAsync(id, LspErrorCodes.RequestCancelled,
                "Request cancelled.").ConfigureAwait(false);
        }
        catch (JsonRpcException exception)
        {
            await _writer.WriteErrorAsync(id, exception.Code, exception.Message,
                exception.DataObject).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _error.WriteLineAsync($"LSP request '{method}' failed: {exception}");
            await _writer.WriteErrorAsync(id, LspErrorCodes.InternalError,
                "Internal server error.").ConfigureAwait(false);
        }
        finally { _requests.Complete(requestKey, requestCancellation); }
    }

    private void RouteCancellation(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("id", out JsonElement id) ||
            id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            return;
        _requests.Cancel(NormalizeId(id));
    }

    private static bool TryValidateMessage(JsonElement message, out string? method,
        out JsonElement? id, out JsonElement? parameters, out string? error)
    {
        method = null;
        id = null;
        parameters = null;
        error = null;
        if (message.ValueKind != JsonValueKind.Object)
        {
            error = "A JSON-RPC message must be an object.";
            return false;
        }
        if (message.TryGetProperty("id", out JsonElement recognizedId) &&
            recognizedId.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            id = recognizedId.Clone();
        if (!message.TryGetProperty("jsonrpc", out JsonElement version) ||
            version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
        {
            error = "The jsonrpc property must be '2.0'.";
            return false;
        }
        if (!message.TryGetProperty("method", out JsonElement methodElement) ||
            methodElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(methodElement.GetString()))
        {
            error = "The method property must be a non-empty string.";
            return false;
        }
        method = methodElement.GetString();
        if (message.TryGetProperty("id", out JsonElement idElement))
        {
            if (idElement.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
            {
                error = "A request id must be a string or number.";
                return false;
            }
        }
        if (message.TryGetProperty("params", out JsonElement paramsElement))
        {
            if (paramsElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                error = "The params property must be an object or array.";
                return false;
            }
            parameters = paramsElement.Clone();
        }
        return true;
    }

    private static string NormalizeId(JsonElement id) => id.GetRawText();
}
