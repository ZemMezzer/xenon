using System.Text;
using System.Text.Json;
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
