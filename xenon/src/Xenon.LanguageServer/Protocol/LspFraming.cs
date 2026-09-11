using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Xenon.LanguageServer.Protocol;

public sealed class LspFramingException(string message) : IOException(message);

public sealed class LspMessageReader(Stream input, int maximumContentLength = 16 * 1024 * 1024)
{
    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var header = new ArrayBufferWriter<byte>();
        byte[] single = new byte[1];
        while (true)
        {
            int read = await input.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (header.WrittenCount == 0) return null;
                throw new LspFramingException("Unexpected EOF inside LSP headers.");
            }
            header.Write(single);
            if (header.WrittenCount > 16 * 1024)
                throw new LspFramingException("LSP header block exceeds 16 KiB.");
            ReadOnlySpan<byte> bytes = header.WrittenSpan;
            if (bytes.Length >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' &&
                bytes[^2] == '\r' && bytes[^1] == '\n')
                break;
        }

        string headerText = Encoding.ASCII.GetString(header.WrittenSpan[..^4]);
        int? contentLength = null;
        foreach (string line in headerText.Split("\r\n", StringSplitOptions.None))
        {
            int separator = line.IndexOf(':');
            if (separator <= 0) throw new LspFramingException($"Malformed LSP header '{line}'.");
            string name = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (contentLength is not null)
                throw new LspFramingException("Duplicate Content-Length header.");
            if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int parsed) || parsed < 0)
                throw new LspFramingException("Content-Length must be a non-negative decimal integer.");
            contentLength = parsed;
        }
        if (contentLength is null) throw new LspFramingException("Missing Content-Length header.");
        if (contentLength > maximumContentLength)
            throw new LspFramingException($"LSP payload exceeds {maximumContentLength} bytes.");

        byte[] payload = GC.AllocateUninitializedArray<byte>(contentLength.Value);
        int offset = 0;
        while (offset < payload.Length)
        {
            int read = await input.ReadAsync(payload.AsMemory(offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0) throw new LspFramingException("Unexpected EOF inside LSP payload.");
            offset += read;
        }
        return payload;
    }
}

public sealed class LspMessageWriter(Stream output) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Task WriteResultAsync(JsonElement id, object? result,
        CancellationToken cancellationToken = default) =>
        WriteAsync(new JsonRpcSuccessResponse("2.0", id, result), cancellationToken);

    public Task WriteErrorAsync(JsonElement? id, int code, string message,
        object? data = null, CancellationToken cancellationToken = default) =>
        WriteAsync(new JsonRpcErrorResponse("2.0", id,
            new JsonRpcError(code, message, data)), cancellationToken);

    public Task WriteNotificationAsync(string method, object? parameters,
        CancellationToken cancellationToken = default) =>
        WriteAsync(new JsonRpcNotification("2.0", method, parameters), cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        JsonTypeInfo typeInfo = JsonOptions.GetTypeInfo(message.GetType());
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, typeInfo);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public void Dispose() => _writeGate.Dispose();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.TypeInfoResolverChain.Add(LspJsonSerializerContext.Default);
        // Keep test-only/custom handlers flexible on CoreCLR. This feature switch is false and
        // the fallback is removed when Xenon is trimmed for Native AOT.
        if (JsonSerializer.IsReflectionEnabledByDefault)
            options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        return options;
    }
}
