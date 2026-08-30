using System.Text.Json;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;

namespace Xenon.LanguageServer.Tests;

public sealed class RealProcessRuntimeTests
{
    [Fact]
    public async Task ActualCliHandlesIntelligenceConcurrencyReloadAndCleanShutdown()
    {
        const string coreSource = """
            namespace Core;
            struct Base { public int Field; }
            public int Utility(int value) { return value; }
            """;
        const string appSource = """
            using Core;
            namespace App;
            struct Player : Base {}
            int Test(Base value) {
                int local = value.Field;
                return Utility(local);
            }
            """;
        using var directory = new TestDirectory();
        directory.Write("Core/Core.xeproj", Project("Core", "static-library"));
        string coreFile = directory.Write("Core/src/core.xe", coreSource);
        directory.Write("App/App.xeproj", """
            [project]
            name = "App"
            type = "executable"
            [source]
            root = "src"
            [references]
            projects = ["../Core/Core.xeproj"]
            """);
        string appFile = directory.Write("App/src/main.xe", appSource);
        string manifest = directory.Write("Root.xws", Workspace("App/App.xeproj"));
        string appUri = DocumentUri.FromPath(appFile).AbsoluteUri;
        string coreUri = DocumentUri.FromPath(coreFile).AbsoluteUri;
        string cli = Path.Combine(AppContext.BaseDirectory, "xenon.dll");
        Assert.True(File.Exists(cli), $"CLI assembly not found at '{cli}'.");

        await using var client = new LspProcessClient(cli);
        JsonElement initialize = Result(await client.RequestAsync("initialize", new
        {
            rootUri = DocumentUri.FromPath(directory.Path).AbsoluteUri,
            initializationOptions = new { workspacePath = manifest },
        }));
        Assert.True(initialize.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
        await client.NotifyAsync("initialized", new { });
        await client.NotifyAsync("textDocument/didOpen", new
        {
            textDocument = new { uri = appUri, version = 1, text = appSource },
        });
        JsonElement initialDiagnostics = await client.WaitForNotificationAsync(
            "textDocument/publishDiagnostics", message =>
                message.GetProperty("params").GetProperty("version").GetInt32() == 1);
        Assert.Empty(initialDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());

        Task<JsonElement>[] concurrent =
        [
            client.RequestAsync("textDocument/hover", At(appUri, appSource,
                appSource.IndexOf("Utility", StringComparison.Ordinal))),
            client.RequestAsync("textDocument/definition", At(appUri, appSource,
                appSource.IndexOf("Utility", StringComparison.Ordinal))),
            client.RequestAsync("textDocument/typeDefinition", At(appUri, appSource,
                appSource.IndexOf("value.Field", StringComparison.Ordinal))),
            client.RequestAsync("textDocument/completion", At(appUri, appSource,
                appSource.IndexOf("Field", StringComparison.Ordinal))),
            client.RequestAsync("textDocument/signatureHelp", At(appUri, appSource,
                appSource.LastIndexOf("local", StringComparison.Ordinal) + 2)),
            client.RequestAsync("textDocument/references", ReferencesAt(appUri, appSource,
                appSource.IndexOf("Utility", StringComparison.Ordinal))),
            client.RequestAsync("textDocument/implementation", At(appUri, appSource,
                appSource.IndexOf("Base", StringComparison.Ordinal))),
            client.RequestAsync("textDocument/semanticTokens/full", new
                { textDocument = new { uri = appUri } }),
            client.RequestAsync("textDocument/rename", RenameAt(appUri, appSource,
                appSource.IndexOf("local =", StringComparison.Ordinal), "renamed")),
        ];
        JsonElement[] responses = await Task.WhenAll(concurrent);
        Assert.All(responses, response => Assert.True(response.TryGetProperty("result", out _),
            response.ToString()));
        Assert.Equal(coreUri, Result(responses[1])[0].GetProperty("uri").GetString());
        Assert.Equal(coreUri, Result(responses[2])[0].GetProperty("uri").GetString());
        Assert.NotEmpty(Result(responses[5]).EnumerateArray());
        Assert.NotEmpty(Result(responses[6]).EnumerateArray());
        Assert.True(Result(responses[7]).GetProperty("data").GetArrayLength() > 0);
        Assert.Equal(2, Result(responses[8]).GetProperty("changes").GetProperty(appUri)
            .GetArrayLength());

        const string invalid = "using Core; namespace App; int Test() { missing; }";
        await client.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri = appUri, version = 2 },
            contentChanges = new[] { new { text = invalid } },
        });
        await client.NotifyAsync("textDocument/didChange", new
        {
            textDocument = new { uri = appUri, version = 3 },
            contentChanges = new[] { new { text = appSource } },
        });
        JsonElement currentDiagnostics = await client.WaitForNotificationAsync(
            "textDocument/publishDiagnostics", message =>
                message.GetProperty("params").GetProperty("version").GetInt32() == 3);
        Assert.Empty(currentDiagnostics.GetProperty("params").GetProperty("diagnostics").EnumerateArray());

        directory.Write("Core/src/core.xe", coreSource + "\npublic int Added() { return 2; }\n");
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new
        {
            changes = new[] { new { uri = coreUri, type = 2 } },
        });
        JsonElement added = Result(await client.RequestAsync("workspace/symbol", new { query = "Added" }));
        Assert.Contains(added.EnumerateArray(), symbol => symbol.GetProperty("name").GetString() == "Added");

        directory.Write("Tools/Tools.xeproj", Project("Tools", "static-library"));
        directory.Write("Tools/src/tool.xe", "namespace Tools; public int ToolSymbol() { return 1; }");
        directory.Write("Root.xws", Workspace("App/App.xeproj", "Tools/Tools.xeproj"));
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new
        {
            changes = new[] { new { uri = DocumentUri.FromPath(manifest).AbsoluteUri, type = 2 } },
        });
        JsonElement tool = Result(await client.RequestAsync("workspace/symbol",
            new { query = "ToolSymbol" }));
        Assert.Contains(tool.EnumerateArray(), symbol =>
            symbol.GetProperty("name").GetString() == "ToolSymbol");

        for (int iteration = 0; iteration < 6; iteration++)
        {
            var hoverRace = client.StartRequest("textDocument/hover", At(appUri, appSource,
                appSource.IndexOf("Utility", StringComparison.Ordinal)));
            var completionRace = client.StartRequest("textDocument/completion", At(appUri, appSource,
                appSource.IndexOf("Field", StringComparison.Ordinal)));
            var symbolsRace = client.StartRequest("workspace/symbol", new { query = "Player" });
            directory.Write("Root.xws", iteration % 2 == 0
                ? Workspace("App/App.xeproj")
                : Workspace("App/App.xeproj", "Tools/Tools.xeproj"));
            await client.NotifyAsync("workspace/didChangeWatchedFiles", new
            {
                changes = new[] { new { uri = DocumentUri.FromPath(manifest).AbsoluteUri, type = 2 } },
            });
            JsonElement barrier = await client.RequestAsync("workspace/symbol",
                new { query = "Player" });
            AssertNoInternalError(barrier);
            foreach (Task<JsonElement> response in new[]
                     { hoverRace.Response, completionRace.Response, symbolsRace.Response })
                AssertNoInternalError(await response.WaitAsync(TimeSpan.FromSeconds(20)));
        }

        JsonElement unknown = await client.RequestAsync("xenon/unknown", new { });
        Assert.Equal(LspErrorCodes.MethodNotFound,
            unknown.GetProperty("error").GetProperty("code").GetInt32());
        await client.SendRawPayloadAsync("{");
        JsonElement parseError = await client.WaitForMessageAsync(message =>
            message.TryGetProperty("error", out JsonElement error) &&
            error.GetProperty("code").GetInt32() == LspErrorCodes.ParseError);
        Assert.Equal(JsonValueKind.Null, parseError.GetProperty("id").ValueKind);
        var cancellable = client.StartRequest("workspace/symbol", new { query = "" });
        directory.Write("Root.xws", Workspace("App/App.xeproj", "Tools/Tools.xeproj"));
        await client.NotifyAsync("workspace/didChangeWatchedFiles", new
        {
            changes = new[] { new { uri = DocumentUri.FromPath(manifest).AbsoluteUri, type = 2 } },
        });
        await client.NotifyAsync("$/cancelRequest", new { id = cancellable.Id });
        JsonElement cancellationRace = await cancellable.Response.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(cancellationRace.TryGetProperty("result", out _) ||
            cancellationRace.GetProperty("error").GetProperty("code").GetInt32() ==
                LspErrorCodes.RequestCancelled);
        Assert.True((await client.RequestAsync("workspace/symbol", new { query = "Player" }))
            .TryGetProperty("result", out _));

        (int exitCode, string stderr) = await client.ShutdownAsync();
        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
    }

    private static void AssertNoInternalError(JsonElement response)
    {
        if (!response.TryGetProperty("error", out JsonElement error)) return;
        Assert.NotEqual(LspErrorCodes.InternalError, error.GetProperty("code").GetInt32());
        Assert.Equal(LspErrorCodes.RequestCancelled, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ActualCliDrainsRequestOnFatalFramingCorruption()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", LargeSource());
        string cli = Path.Combine(AppContext.BaseDirectory, "xenon.dll");
        await using var client = new LspProcessClient(cli);
        Assert.True((await client.RequestAsync("initialize", new
        {
            rootUri = DocumentUri.FromPath(file).AbsoluteUri,
        })).TryGetProperty("result", out _));
        await client.NotifyAsync("initialized", new { });

        _ = client.StartRequest("workspace/symbol", new { query = "Function" });
        await client.SendRawFrameAsync(System.Text.Encoding.ASCII.GetBytes(
            "Content-Length: invalid\r\n\r\n"));
        (int exitCode, string stderr) = await client.WaitForExitAsync();

        Assert.Equal(1, exitCode);
        Assert.Contains("LSP framing error", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActualCliDrainsRequestOnPrematureEof()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", LargeSource());
        string cli = Path.Combine(AppContext.BaseDirectory, "xenon.dll");
        await using var client = new LspProcessClient(cli);
        Assert.True((await client.RequestAsync("initialize", new
        {
            rootUri = DocumentUri.FromPath(file).AbsoluteUri,
        })).TryGetProperty("result", out _));
        await client.NotifyAsync("initialized", new { });

        _ = client.StartRequest("workspace/symbol", new { query = "Function" });
        await client.CloseInputAsync();
        (int exitCode, _) = await client.WaitForExitAsync();

        Assert.Equal(1, exitCode);
    }

    private static object At(string uri, string source, int offset) => new
    {
        textDocument = new { uri },
        position = LspTextCoordinates.ToPosition(SourceText.From(source), offset),
    };

    private static object ReferencesAt(string uri, string source, int offset) => new
    {
        textDocument = new { uri },
        position = LspTextCoordinates.ToPosition(SourceText.From(source), offset),
        context = new { includeDeclaration = true },
    };

    private static object RenameAt(string uri, string source, int offset, string newName) => new
    {
        textDocument = new { uri },
        position = LspTextCoordinates.ToPosition(SourceText.From(source), offset),
        newName,
    };

    private static JsonElement Result(JsonElement response) => response.GetProperty("result");

    private static string Project(string name, string type) => $$"""
        [project]
        name = "{{name}}"
        type = "{{type}}"
        [source]
        root = "src"
        """;

    private static string Workspace(params string[] projects) => $$"""
        [workspace]
        projects = [{{string.Join(", ", projects.Select(project => $"\"{project}\""))}}]
        """;

    private static string LargeSource() => "namespace Stress;\n" + string.Join('\n',
        Enumerable.Range(0, 3000).Select(index =>
            $"int Function{index}(int value) {{ return value + {index}; }}"));
}
