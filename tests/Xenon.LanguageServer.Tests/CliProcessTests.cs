using System.Diagnostics;
using System.Text.Json;

namespace Xenon.LanguageServer.Tests;

public sealed class CliProcessTests
{
    [Fact]
    public async Task XenonLspCompletesRealStdioLifecycleWithoutProtocolCorruption()
    {
        string cli = System.IO.Path.Combine(AppContext.BaseDirectory, "xenon.dll");
        Assert.True(File.Exists(cli), $"CLI assembly not found at '{cli}'.");
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(cli);
        start.ArgumentList.Add("lsp");
        using Process process = Process.Start(start)!;
        byte[] messages = [
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "initialized", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", id = 2, method = "shutdown", @params = new { } }),
            .. LspTestProtocol.Frame(new { jsonrpc = "2.0", method = "exit" }),
        ];
        await process.StandardInput.BaseStream.WriteAsync(messages);
        process.StandardInput.Close();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, stderr);
        IReadOnlyList<JsonElement> responses = await LspTestProtocol.ReadFramesAsync(
            System.Text.Encoding.UTF8.GetBytes(stdout));
        Assert.Equal(2, responses.Count);
        Assert.All(responses, response => Assert.Equal("2.0",
            response.GetProperty("jsonrpc").GetString()));
    }
}
