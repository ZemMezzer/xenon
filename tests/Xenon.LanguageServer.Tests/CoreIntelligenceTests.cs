using System.Reflection;
using System.Text.Json;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;

namespace Xenon.LanguageServer.Tests;

public sealed class CoreIntelligenceTests
{
    [Fact]
    public async Task GenericTemplateSyntaxFlowsThroughHoverDefinitionCompletionAndSemanticTokens()
    {
        const string source = """
            namespace Game;
            template Contract { void Run(); }
            struct Box<T> where T : Contract { T value; }
            void Invoke<T>(T value) where T : Contract { value.Run(); value. }
            T Identity<T>(T value) { return value; }
            int UseIdentity() { return Identity<int>(1); }
            struct GenericBox<T> { public T value; public T Get() { return value; } }
            void UseBox(GenericBox<int> box) { box. }
            struct Pair<T> { public T first; }
            struct Container<T> { public Pair<T> pair; }
            void UseContainer(Container<int> container) { container. }
            void Test() { }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("templates.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new { rootUri = uri }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = source },
        }), default);

        int fieldType = source.LastIndexOf("T value", StringComparison.Ordinal);
        JsonElement hover = await RequestAtAsync(session, "textDocument/hover", uri, source, fieldType);
        Assert.Contains("T", hover.GetProperty("contents").GetProperty("value").GetString());
        JsonElement definition = await RequestAtAsync(session, "textDocument/definition", uri, source, fieldType);
        Assert.Single(definition.EnumerateArray());

        int completionPosition = source.IndexOf("{ }", StringComparison.Ordinal) + 1;
        JsonElement completion = await RequestAtAsync(session, "textDocument/completion", uri, source, completionPosition);
        string[] labels = completion.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("label").GetString() ?? string.Empty).ToArray();
        Assert.Contains("template", labels);
        Assert.Contains("where", labels);

        int genericMemberPosition = source.LastIndexOf("value.", StringComparison.Ordinal) + "value.".Length;
        JsonElement genericMemberCompletion = await RequestAtAsync(session, "textDocument/completion", uri,
            source, genericMemberPosition);
        string[] genericMemberLabels = genericMemberCompletion.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("label").GetString() ?? string.Empty).ToArray();
        Assert.Contains("Run", genericMemberLabels);

        int genericCall = source.LastIndexOf("Identity<int>", StringComparison.Ordinal);
        JsonElement genericCallHover = await RequestAtAsync(session, "textDocument/hover", uri, source, genericCall);
        Assert.Contains("Identity<int>", genericCallHover.GetProperty("contents").GetProperty("value").GetString());
        JsonElement genericCallDefinition = await RequestAtAsync(session, "textDocument/definition", uri, source,
            genericCall);
        Assert.Single(genericCallDefinition.EnumerateArray());

        int genericStructMemberPosition = source.LastIndexOf("box.", StringComparison.Ordinal) + "box.".Length;
        JsonElement genericStructCompletion = await RequestAtAsync(session, "textDocument/completion", uri,
            source, genericStructMemberPosition);
        string[] genericStructLabels = genericStructCompletion.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("label").GetString() ?? string.Empty).ToArray();
        Assert.Contains("value", genericStructLabels);
        Assert.Contains("Get", genericStructLabels);

        int nestedGenericPosition = source.LastIndexOf("container.", StringComparison.Ordinal) + "container.".Length;
        JsonElement nestedGenericCompletion = await RequestAtAsync(session, "textDocument/completion", uri,
            source, nestedGenericPosition);
        Assert.Contains(nestedGenericCompletion.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("label").GetString() == "pair");

        JsonElement semanticTokens = Result(await session.HandleRequestAsync("textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        int[] data = semanticTokens.GetProperty("data").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        Assert.Contains(data.Chunk(5), token => token[3] == 13); // typeParameter
        Assert.Contains(data.Chunk(5), token => token[3] == 2); // structural template uses interface coloring
    }

    [Fact]
    public async Task CoreRequestsUseSemanticModelAndWorkspaceIndexes()
    {
        const string source = """
            namespace Game;
            interface IEntity {}
            struct Entity {}
            struct Player : Entity, IEntity {
                public int Health;
                public Player(int health) { Health = health; }
                public void Move(int speed) {}
            }
            Player Create() { return Player(1); }
            void Test(Player player) {
                int local = player.Health;
                player.Move(local);
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        JsonElement initialize = Result(await session.HandleRequestAsync("initialize",
            LspTestProtocol.Json(new { rootUri = uri }), default));
        Assert.True(initialize.GetProperty("capabilities").GetProperty("hoverProvider").GetBoolean());
        string? informationalVersion = typeof(LanguageServerSession).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        Assert.False(string.IsNullOrWhiteSpace(informationalVersion));
        Assert.Equal(informationalVersion,
            initialize.GetProperty("serverInfo").GetProperty("version").GetString());
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = source },
        }), default);
        Xenon.ProjectSystem.WorkspaceSymbolIndex semanticIndex = await Assert.Single(session.Workspaces)
            .CurrentSnapshot.GetSymbolIndexAsync();
        Xenon.ProjectSystem.SymbolIndexEntry constructor = Assert.Single(semanticIndex.Entries.Where(entry =>
            entry.FunctionKind == Xenon.Compiler.Semantics.Symbols.FunctionKind.Constructor));
        Assert.Equal("Game.Player.Player", constructor.QualifiedName);

        JsonElement hover = await RequestAtAsync(session, "textDocument/hover", uri, source,
            source.LastIndexOf("Health", StringComparison.Ordinal));
        Assert.Contains("public int Health", hover.GetProperty("contents").GetProperty("value").GetString());

        JsonElement definition = await RequestAtAsync(session, "textDocument/definition", uri, source,
            source.LastIndexOf("Health", StringComparison.Ordinal));
        Assert.Equal(uri, definition[0].GetProperty("uri").GetString());

        JsonElement typeDefinition = await RequestAtAsync(session, "textDocument/typeDefinition", uri, source,
            source.IndexOf("player.Health", StringComparison.Ordinal));
        Assert.Single(typeDefinition.EnumerateArray());

        JsonElement references = await RequestAtAsync(session, "textDocument/references", uri, source,
            source.IndexOf("Health;", StringComparison.Ordinal), new { includeDeclaration = true });
        Assert.Equal(3, references.GetArrayLength());

        JsonElement implementations = await RequestAtAsync(session, "textDocument/implementation", uri, source,
            source.IndexOf("IEntity", StringComparison.Ordinal));
        Assert.Contains(implementations.EnumerateArray(), item =>
            item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() == 3);

        JsonElement symbols = Result(await session.HandleRequestAsync("textDocument/documentSymbol",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        JsonElement game = Assert.Single(symbols.EnumerateArray());
        Assert.Contains(game.GetProperty("children").EnumerateArray(), item =>
            item.GetProperty("name").GetString() == "Player");

        JsonElement workspaceSymbols = Result(await session.HandleRequestAsync("workspace/symbol",
            LspTestProtocol.Json(new { query = "Play" }), default));
        Assert.Contains(workspaceSymbols.EnumerateArray(), item => item.GetProperty("name").GetString() == "Player");

        int memberPosition = source.IndexOf("player.Health", StringComparison.Ordinal) + "player.".Length;
        JsonElement completion = await RequestAtAsync(session, "textDocument/completion", uri, source, memberPosition);
        Assert.Contains(completion.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("label").GetString() == "Health");

        int argumentPosition = source.LastIndexOf("local", StringComparison.Ordinal) + 2;
        JsonElement signature = await RequestAtAsync(session, "textDocument/signatureHelp", uri, source,
            argumentPosition);
        Assert.Contains("Move", signature.GetProperty("signatures")[0].GetProperty("label").GetString());

        JsonElement semanticTokens = Result(await session.HandleRequestAsync("textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        Assert.True(semanticTokens.GetProperty("data").GetArrayLength() >= 25);

        JsonElement prepare = await RequestAtAsync(session, "textDocument/prepareRename", uri, source,
            source.LastIndexOf("local", StringComparison.Ordinal));
        Assert.Equal("local", prepare.GetProperty("placeholder").GetString());
        JsonElement rename = await RequestAtAsync(session, "textDocument/rename", uri, source,
            source.LastIndexOf("local", StringComparison.Ordinal), null, "renamed");
        Assert.Equal(2, rename.GetProperty("changes").GetProperty(uri).GetArrayLength());

        JsonElement typeRename = await RequestAtAsync(session, "textDocument/rename", uri, source,
            source.IndexOf("Player :", StringComparison.Ordinal), null, "Hero");
        string[] renamedLines = typeRename.GetProperty("changes").GetProperty(uri).EnumerateArray()
            .Select(edit => edit.GetProperty("range").GetProperty("start") is JsonElement start
                ? $"{start.GetProperty("line").GetInt32()}:{start.GetProperty("character").GetInt32()}" : "")
            .ToArray();
        Assert.Equal(new[] { "9:10", "8:25", "8:0", "5:11", "3:7" }, renamedLines);
    }

    [Fact]
    public async Task DiagnosticsPublishStableCompilerCodeAndCurrentVersion()
    {
        const string source = "namespace Game; void Test() { missing; }";
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var published = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new LanguageServerSession((method, value) =>
        {
            if (method == "textDocument/publishDiagnostics") published.TrySetResult(Result(value));
            return Task.CompletedTask;
        }, diagnosticDebounce: TimeSpan.Zero);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new { rootUri = uri }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 7, text = source },
        }), default);

        JsonElement notification = await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(7, notification.GetProperty("version").GetInt32());
        JsonElement diagnostic = Assert.Single(notification.GetProperty("diagnostics").EnumerateArray());
        Assert.Matches("^XE[0-9]{4}$", diagnostic.GetProperty("code").GetString());
        Assert.Equal("xenon", diagnostic.GetProperty("source").GetString());
        Assert.Equal(1, diagnostic.GetProperty("severity").GetInt32());

        JsonElement definition = await RequestAtAsync(session, "textDocument/definition", uri, source,
            source.IndexOf("missing", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, definition.ValueKind);
        JsonRpcException renameError = await Assert.ThrowsAsync<JsonRpcException>(() =>
            RequestAtAsync(session, "textDocument/rename", uri, source,
                source.IndexOf("missing", StringComparison.Ordinal), null, "renamed"));
        Assert.Equal(LspErrorCodes.InvalidParams, renameError.Code);
    }

    [Fact]
    public async Task IncompleteSourceKeepsCompletionAndSignatureHelpAcrossGenerations()
    {
        const string memberSource =
            "namespace Game; struct Player { public int Health; } void Test(Player player) { player.";
        const string callSource =
            "namespace Game; void Move(int speed) {} void Test() { Move(";
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", memberSource);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new { rootUri = uri }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = memberSource },
        }), default);

        JsonElement completion = await RequestAtAsync(session, "textDocument/completion", uri,
            memberSource, memberSource.Length);
        Assert.Contains(completion.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("label").GetString() == "Health");

        await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = callSource } },
        }), default);
        JsonElement signature = await RequestAtAsync(session, "textDocument/signatureHelp", uri,
            callSource, callSource.Length);
        Assert.Contains("Move", signature.GetProperty("signatures")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task HoverAndDefinitionNeverLeakSymbolsAtSameOffsetFromAnotherFile()
    {
        const string targetSource = """
            namespace Target;
            struct Vector3 {}
            void Use()
            {
                Vector3 vector = Vector3();
            }
            """;
        int targetPosition = targetSource.IndexOf("Vector3 vector", StringComparison.Ordinal);
        const string foreignPrefix = "namespace Other; struct Noise { private float ";
        Assert.True(foreignPrefix.Length <= targetPosition);
        string foreignSource = foreignPrefix +
            new string(' ', targetPosition - foreignPrefix.Length) +
            "Y; public float get() { return Y; } }";
        using var directory = new TestDirectory();
        string targetFile = directory.Write("Target.xe", targetSource);
        directory.Write("Foreign.xe", foreignSource);
        string targetUri = DocumentUri.FromPath(targetFile).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            rootUri = DocumentUri.FromPath(directory.Path).AbsoluteUri,
        }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri = targetUri, version = 1, text = targetSource },
        }), default);

        foreach (int position in Enumerable.Range(targetPosition, "Vector3".Length))
        {
            JsonElement hover = await RequestAtAsync(session, "textDocument/hover", targetUri,
                targetSource, position);
            Assert.Contains("struct Vector3",
                hover.GetProperty("contents").GetProperty("value").GetString());
            JsonElement definition = await RequestAtAsync(session, "textDocument/definition",
                targetUri, targetSource, position);
            Assert.Equal(targetUri, Assert.Single(definition.EnumerateArray())
                .GetProperty("uri").GetString());
        }
    }

    private static async Task<JsonElement> RequestAtAsync(LanguageServerSession session, string method,
        string uri, string source, int offset, object? context = null, string? newName = null)
    {
        LspPosition position = LspTextCoordinates.ToPosition(SourceText.From(source), offset);
        object parameters = method switch
        {
            "textDocument/references" => new { textDocument = new { uri }, position, context },
            "textDocument/rename" => new { textDocument = new { uri }, position, newName },
            _ => new { textDocument = new { uri }, position },
        };
        return Result(await session.HandleRequestAsync(method, LspTestProtocol.Json(parameters), default));
    }

    private static JsonElement Result(object? value) => JsonSerializer.SerializeToElement(value,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
