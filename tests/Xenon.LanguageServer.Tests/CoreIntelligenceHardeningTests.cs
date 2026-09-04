using System.Text.Json;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;

namespace Xenon.LanguageServer.Tests;

public sealed class CoreIntelligenceHardeningTests
{
    [Fact]
    public async Task IndexerRenameIsRejectedWhilePropertyAndMethodRemainRenameable()
    {
        const string source = """
            namespace App;
            struct Box {
                public int Value { get { return 1; } set {} }
                public int this[int index] { get { return index; } }
                public void Run() {}
            }
            void Test(Box box) { int a = box[0]; int b = box.Value; box.Run(); }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, null, source);
        foreach (int position in new[]
        {
            source.IndexOf("this[", StringComparison.Ordinal),
            source.IndexOf("[0]", StringComparison.Ordinal),
            source.IndexOf("get {", StringComparison.Ordinal),
            source.IndexOf("set {}", StringComparison.Ordinal),
        })
        {
            await Assert.ThrowsAsync<JsonRpcException>(() => RequestAtAsync(session,
                "textDocument/prepareRename", uri, source, position));
            await Assert.ThrowsAsync<JsonRpcException>(() => RequestAtAsync(session,
                "textDocument/rename", uri, source, position, newName: "Item"));
        }

        JsonElement property = await RequestAtAsync(session, "textDocument/rename", uri, source,
            source.IndexOf("Value {", StringComparison.Ordinal), newName: "Current");
        Assert.Equal(2, property.GetProperty("changes").GetProperty(uri).GetArrayLength());
        JsonElement method = await RequestAtAsync(session, "textDocument/rename", uri, source,
            source.IndexOf("Run()", StringComparison.Ordinal), newName: "Execute");
        Assert.Equal(2, method.GetProperty("changes").GetProperty(uri).GetArrayLength());
    }

    [Fact]
    public async Task RenameUpdatesCompleteOverrideAndInterfaceFamilyOnly()
    {
        const string source = """
            namespace App;
            interface IUpdatable { void Update(); }
            struct Base { public virtual void Update() {} }
            struct Child : Base, IUpdatable { public override void Update() {} }
            struct GrandChild : Child { public override void Update() {} }
            struct Second : IUpdatable { public void Update() {} }
            struct Other { public void Update() {} }
            void Test(Base& a, Child& b, GrandChild& c, IUpdatable& d, Second& e, Other& other) {
                a.Update(); b.Update(); c.Update(); d.Update(); e.Update(); other.Update();
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, null, source);
        JsonElement rename = await RequestAtAsync(session, "textDocument/rename", uri, source,
            source.IndexOf("override void Update", StringComparison.Ordinal) + "override void ".Length,
            newName: "Tick");
        string changed = ApplyEdits(source, rename.GetProperty("changes").GetProperty(uri));

        Assert.Contains("interface IUpdatable { void Tick(); }", changed);
        Assert.Contains("public virtual void Tick()", changed);
        Assert.Contains("public override void Tick()", changed);
        Assert.Contains("struct Second : IUpdatable { public void Tick()", changed);
        Assert.Contains("a.Tick(); b.Tick(); c.Tick(); d.Tick(); e.Tick();", changed);
        Assert.Contains("struct Other { public void Update()", changed);
        Assert.Contains("other.Update();", changed);

        const string collision = """
            namespace App;
            interface IUpdatable { void Update(); }
            struct Base { public virtual void Update() {} }
            struct Child : Base, IUpdatable { public override void Update() {} public void Tick() {} }
            """;
        await ChangeAsync(session, uri, collision, 2);
        await Assert.ThrowsAsync<JsonRpcException>(() => RequestAtAsync(session, "textDocument/rename",
            uri, collision, collision.IndexOf("virtual void Update", StringComparison.Ordinal) +
                "virtual void ".Length, newName: "Tick"));
    }

    [Fact]
    public async Task CrossProjectMemberFamilyRenameIsAtomic()
    {
        using var directory = new TestDirectory();
        string coreManifest = directory.Write("Core/Core.xeproj", Project("Core"));
        const string core = """
            namespace Core;
            interface IUpdatable { void Update(); }
            struct Base : IUpdatable { public virtual void Update() {} }
            """;
        string coreFile = directory.Write("Core/src/core.xe", core);
        string gameManifest = directory.Write("Game/Game.xeproj", """
            [project]
            name = "Game"
            type = "executable"
            [source]
            root = "src"
            [references]
            projects = ["../Core/Core.xeproj"]
            """);
        const string game = """
            using Core;
            namespace Game;
            struct Player : Base { public override void Update() {} }
            void Test(Player& player) { player.Update(); }
            """;
        string gameFile = directory.Write("Game/src/main.xe", game);
        string gameUri = DocumentUri.FromPath(gameFile).AbsoluteUri;
        string coreUri = DocumentUri.FromPath(coreFile).AbsoluteUri;
        await using var session = await CreateSessionAsync(gameUri, gameManifest, game);

        JsonElement rename = await RequestAtAsync(session, "textDocument/rename", gameUri, game,
            game.IndexOf("Update()", StringComparison.Ordinal), newName: "Tick");
        JsonElement changes = rename.GetProperty("changes");
        Assert.True(changes.TryGetProperty(coreUri, out JsonElement coreEdits));
        Assert.True(changes.TryGetProperty(gameUri, out JsonElement gameEdits));
        Assert.Equal(2, coreEdits.GetArrayLength());
        Assert.Equal(2, gameEdits.GetArrayLength());
        _ = coreManifest;
    }

    [Fact]
    public async Task SharedPhysicalReferencesAreDeduplicatedAndSharedDeclarationsAreRejected()
    {
        using var directory = new TestDirectory();
        directory.Write("Core/Core.xeproj", Project("Core"));
        const string core = "namespace Core; public void Target() {}";
        string coreFile = directory.Write("Core/src/core.xe", core);
        const string shared = "namespace Shared; void Use() { Core.Target(); }";
        string sharedFile = directory.Write("Shared/shared.xe", shared);
        foreach (string project in new[] { "B", "C" })
            directory.Write($"{project}/{project}.xeproj", $"""
                [project]
                name = "{project}"
                type = "static-library"
                [source]
                root = "../Shared"
                [references]
                projects = ["../Core/Core.xeproj"]
                """);
        string manifest = directory.Write("Both.xws", """
            [workspace]
            projects = ["Core/Core.xeproj", "B/B.xeproj", "C/C.xeproj"]
            """);
        string coreUri = DocumentUri.FromPath(coreFile).AbsoluteUri;
        string sharedUri = DocumentUri.FromPath(sharedFile).AbsoluteUri;
        await using var session = await CreateSessionAsync(coreUri, manifest, core);

        JsonElement rename = await RequestAtAsync(session, "textDocument/rename", coreUri, core,
            core.IndexOf("Target", StringComparison.Ordinal), newName: "Renamed");
        Assert.Single(rename.GetProperty("changes").GetProperty(sharedUri).EnumerateArray());

        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri = sharedUri, version = 1, text = shared },
        }), default);
        await Assert.ThrowsAsync<JsonRpcException>(() => RequestAtAsync(session, "textDocument/rename",
            sharedUri, shared, shared.IndexOf("Use", StringComparison.Ordinal), newName: "Execute"));
    }

    [Fact]
    public async Task RootCompletionAndEditorClassificationsAreConsistentAndHideAccessors()
    {
        using var directory = new TestDirectory();
        directory.Write("Library/Library.xeproj", Project("Library"));
        directory.Write("Library/src/library.xe", "namespace Library.Tools; struct Remote {}");
        directory.Write("Hidden/Hidden.xeproj", Project("Hidden"));
        directory.Write("Hidden/src/hidden.xe", "namespace Hidden.Secret; struct Invisible {}");
        string manifest = directory.Write("App/App.xeproj", """
            [project]
            name = "App"
            type = "executable"
            [source]
            root = "src"
            [references]
            projects = ["../Library/Library.xeproj"]
            """);
        const string source = """
            namespace App;
            template Equatable { void Equal(); }
            enum State { Ready }
            interface IService { int Value { get; set; } }
            const int Global = 1;
            int Free() { return Global; }
            struct Library {
                public int Field;
                const int Limit = 1;
                public int Value { get { return Field; } set { Field = value; } }
                public int this[int index] { get { return index; } }
                public Library() {}
                public void Run() {}
            }
            void Test<T>(Library box, T value) where T : Equatable { int local = 0; box.; State.; }
            """;
        string file = directory.Write("App/src/main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, manifest, source);

        int generalPosition = source.IndexOf("box.;", StringComparison.Ordinal) - 1;
        JsonElement general = await RequestAtAsync(session, "textDocument/completion", uri, source,
            generalPosition);
        JsonElement[] libraries = general.GetProperty("items").EnumerateArray().Where(item =>
            item.GetProperty("filterText").GetString() == "Library").ToArray();
        Assert.Contains(libraries, item => item.GetProperty("kind").GetInt32() == 9);  // Module
        Assert.Contains(libraries, item => item.GetProperty("kind").GetInt32() == 22); // Struct
        Assert.DoesNotContain(general.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("filterText").GetString() == "Hidden");

        JsonElement members = await RequestAtAsync(session, "textDocument/completion", uri, source,
            source.IndexOf("box.;", StringComparison.Ordinal) + "box.".Length);
        Assert.Contains(members.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("filterText").GetString() == "Run" && item.GetProperty("kind").GetInt32() == 3);
        Assert.Contains(members.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("filterText").GetString() == "Value" && item.GetProperty("kind").GetInt32() == 10);
        JsonElement enumMembers = await RequestAtAsync(session, "textDocument/completion", uri, source,
            source.IndexOf("State.;", StringComparison.Ordinal) + "State.".Length);
        Assert.Contains(enumMembers.GetProperty("items").EnumerateArray(), item =>
            item.GetProperty("filterText").GetString() == "Ready" && item.GetProperty("kind").GetInt32() == 20);

        AssertCompletionItem(general, "Library", 9, "namespace");
        AssertCompletionItem(general, "Library", 22, "struct");
        AssertCompletionItem(general, "IService", 8, "interface");
        AssertCompletionItem(general, "Equatable", 18, "template");
        AssertCompletionItem(general, "State", 13, "enum");
        AssertCompletionItem(general, "Global", 21, "constant");
        AssertCompletionItem(general, "Free", 3, "function");
        AssertCompletionItem(general, "box", 12, "parameter");
        AssertCompletionItem(general, "local", 6, "local");
        AssertCompletionItem(general, "T", 25, "type parameter");
        AssertCompletionItem(members, "Field", 5, "field");
        AssertCompletionItem(members, "Value", 10, "property");
        AssertCompletionItem(members, "this", 23, "indexer");
        AssertCompletionItem(members, "Run", 3, "method");
        AssertCompletionItem(enumMembers, "Ready", 20, "enum member");

        JsonElement outline = Result(await session.HandleRequestAsync("textDocument/documentSymbol",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        JsonElement[] flat = FlattenSymbols(outline).ToArray();
        Assert.Contains(flat, item => HasKind(item, "Ready", 22));
        Assert.Contains(flat, item => HasKind(item, "Library", 23));
        Assert.Contains(flat, item => HasKind(item, "IService", 11));
        Assert.Contains(flat, item => HasKind(item, "Global", 14));
        Assert.Contains(flat, item => HasKind(item, "Free", 12));
        Assert.Contains(flat, item => HasKind(item, "this", 7));
        Assert.Contains(flat, item => HasKind(item, "Library", 9));
        Assert.Contains(flat, item => HasKind(item, "Run", 6));
        Assert.DoesNotContain(flat, item => item.GetProperty("name").GetString() is "get_Value" or "set_Value");

        JsonElement tokenResponse = Result(await session.HandleRequestAsync("textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        var tokens = DecodeTokens(source, tokenResponse.GetProperty("data"));
        Assert.Contains(tokens, token => token.Text == "Value");
        Assert.Contains(tokens, token => token.Text == "this");
        Assert.Contains(tokens, token => token.Text == "Run");
        Assert.All(tokens.Where(token => token.Text is "get" or "set"),
            token => Assert.Equal(21, token.Type));
    }

    [Fact]
    public async Task CompletionUsesReferencedProjectNamespacesAndCompilerKeywordSet()
    {
        using var directory = new TestDirectory();
        directory.Write("Library/Library.xeproj", """
            [project]
            name = "Library"
            type = "static-library"
            [source]
            root = "src"
            """);
        directory.Write("Library/src/library.xe", """
            namespace Library.Tools;
            struct Item {}
            public int Run() { return 1; }
            """);
        string manifest = directory.Write("App/App.xeproj", """
            [project]
            name = "App"
            type = "executable"
            [source]
            root = "src"
            [references]
            projects = ["../Library/Library.xeproj"]
            """);
        const string source = "namespace App; void Test() { Library.Tools. }";
        string file = directory.Write("App/src/main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, manifest, source);

        JsonElement memberCompletion = await RequestAtAsync(session, "textDocument/completion", uri,
            source, source.IndexOf("Library.Tools.", StringComparison.Ordinal) + "Library.Tools.".Length);
        string[] memberLabels = Labels(memberCompletion);
        Assert.Contains("Item", memberLabels);
        Assert.Contains("Run", memberLabels);

        const string keywordSource = "namespace App; void Test() {  }";
        await ChangeAsync(session, uri, keywordSource, 2);
        JsonElement keywordCompletion = await RequestAtAsync(session, "textDocument/completion", uri,
            keywordSource, keywordSource.IndexOf("  }", StringComparison.Ordinal) + 1);
        string[] labels = Labels(keywordCompletion);
        Assert.All(SyntaxFacts.GetEditorKeywordTexts(), keyword => Assert.Contains(keyword, labels));
        JsonElement[] keywordItems = keywordCompletion.GetProperty("items").EnumerateArray().ToArray();
        AssertKeywordDetails(keywordItems, ["unique", "shared", "weak", "storage", "pin", "atomic"],
            "type-forming keyword");
        AssertKeywordDetails(keywordItems, ["new", "move", "lock"], "value-forming keyword");
        AssertKeywordDetails(keywordItems, ["free", "destruct"], "lifetime operation keyword");
        AssertKeywordDetails(keywordItems, ["true", "false", "null"], "literal keyword");
        AssertKeywordDetails(keywordItems,
            ["void", "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong",
                "float", "double", "nint", "nuint", "clong", "culong"],
            "primitive type", 22);
    }

    private static void AssertKeywordDetails(JsonElement[] items, string[] keywords, string detail, int kind = 14)
    {
        foreach (string keyword in keywords)
        {
            JsonElement item = Assert.Single(items.Where(candidate =>
                candidate.GetProperty("label").GetString() == keyword));
            Assert.Equal(kind, item.GetProperty("kind").GetInt32());
            Assert.Equal(detail, item.GetProperty("detail").GetString());
        }
    }

    [Fact]
    public async Task RenameUsesExactOwnershipAndRejectsRealScopeCollisions()
    {
        using var directory = new TestDirectory();
        directory.Write("A/A.xeproj", Project("A"));
        directory.Write("B/B.xeproj", Project("B"));
        const string sourceA = """
            namespace Game;
            struct Player { public Player() {} ~Player() {} }
            Player Make() { return Player(); }
            """;
        const string sourceB = """
            namespace Game;
            struct Player { public Player() {} ~Player() {} }
            Player Make() { return Player(); }
            """;
        string fileA = directory.Write("A/src/main.xe", sourceA);
        string fileB = directory.Write("B/src/main.xe", sourceB);
        string manifest = directory.Write("Both.xws", """
            [workspace]
            projects = ["A/A.xeproj", "B/B.xeproj"]
            """);
        string uriA = DocumentUri.FromPath(fileA).AbsoluteUri;
        string uriB = DocumentUri.FromPath(fileB).AbsoluteUri;
        await using var session = await CreateSessionAsync(uriA, manifest, sourceA);

        JsonElement rename = await RequestAtAsync(session, "textDocument/rename", uriA, sourceA,
            sourceA.IndexOf("Player {", StringComparison.Ordinal), newName: "Hero");
        JsonElement changes = rename.GetProperty("changes");
        Assert.True(changes.TryGetProperty(uriA, out JsonElement edits));
        Assert.Equal(5, edits.GetArrayLength());
        Assert.False(changes.TryGetProperty(uriB, out _));

        const string collisions = """
            namespace Game;
            struct Box { int first; int second; }
            void Test(int parameter)
            {
                int value = 1;
                int later = value;
                { int nested = value; }
            }
            """;
        await ChangeAsync(session, uriA, collisions, 2);
        await Assert.ThrowsAsync<JsonRpcException>(() => RequestAtAsync(session, "textDocument/rename",
            uriA, collisions, collisions.IndexOf("value =", StringComparison.Ordinal), newName: "later"));
        await Assert.ThrowsAsync<JsonRpcException>(() => RequestAtAsync(session, "textDocument/rename",
            uriA, collisions, collisions.IndexOf("parameter", StringComparison.Ordinal), newName: "value"));
        await Assert.ThrowsAsync<JsonRpcException>(() => RequestAtAsync(session, "textDocument/rename",
            uriA, collisions, collisions.IndexOf("first", StringComparison.Ordinal), newName: "second"));
        JsonElement nestedRename = await RequestAtAsync(session, "textDocument/rename", uriA, collisions,
            collisions.IndexOf("nested", StringComparison.Ordinal), newName: "later");
        Assert.Single(nestedRename.GetProperty("changes").GetProperty(uriA).EnumerateArray());
    }

    [Fact]
    public async Task ConstructionReferencesAndTypeDefinitionResolveToNominalType()
    {
        const string source = """
            namespace Game;
            struct Player { public Player() {} }
            void Test(Player value, Player* pointer)
            {
                Player local = Player();
                Player* heap = new Player();
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, null, source);
        int declaration = source.IndexOf("Player {", StringComparison.Ordinal);

        JsonElement references = await RequestAtAsync(session, "textDocument/references", uri, source,
            declaration, new { includeDeclaration = true });
        Assert.Equal(7, references.GetArrayLength());
        foreach (int construction in new[]
        {
            source.IndexOf("Player();", StringComparison.Ordinal),
            source.LastIndexOf("Player();", StringComparison.Ordinal),
        })
        {
            JsonElement typeDefinition = await RequestAtAsync(session, "textDocument/typeDefinition", uri,
                source, construction);
            JsonElement location = Assert.Single(typeDefinition.EnumerateArray());
            Assert.Equal(1, location.GetProperty("range").GetProperty("start")
                .GetProperty("line").GetInt32());
        }
    }

    [Fact]
    public async Task WorkspaceKindsAndSemanticTokenDefinitionBitsAreExact()
    {
        const string source = """
            namespace App;
            enum State { Ready }
            interface IService { void InterfaceMethod(); }
            extern void Native();
            struct Box
            {
                public static readonly int Field = 1;
                public Box() {}
                public void Method(int parameter) { int local = parameter; }
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, null, source);

        JsonElement workspaceSymbols = Result(await session.HandleRequestAsync("workspace/symbol",
            LspTestProtocol.Json(new { query = "" }), default));
        JsonElement[] published = workspaceSymbols.EnumerateArray().ToArray();
        Assert.Contains(published, item => HasKind(item, "App", 3));               // Namespace
        Assert.Contains(published, item => HasKind(item, "State", 10));            // Enum
        Assert.Contains(published, item => HasKind(item, "Ready", 22));            // EnumMember
        Assert.Contains(published, item => HasKind(item, "IService", 11));         // Interface
        Assert.Contains(published, item => HasKind(item, "Box", 23));              // Struct
        Assert.Contains(published, item => HasKind(item, "Field", 8));             // Field
        Assert.Contains(published, item => HasKind(item, "Box", 9));               // Constructor
        Assert.DoesNotContain(published,
            item => item.GetProperty("name").GetString() == "__init_fields");

        JsonElement tokens = Result(await session.HandleRequestAsync("textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        var decoded = DecodeTokens(source, tokens.GetProperty("data"));
        Assert.Equal(1, decoded.Single(token => token.Text == "Native").Modifiers);
        Assert.Equal(1, decoded.Single(token => token.Text == "InterfaceMethod").Modifiers);
        Assert.Equal(3, decoded.Single(token => token.Text == "State").Modifiers);
        Assert.Equal(15, decoded.Single(token => token.Text == "Field").Modifiers);
        Assert.Equal(3, decoded.Single(token => token.Text == "Method").Modifiers);
        Assert.Equal(1, decoded.First(token => token.Text == "parameter").Modifiers);
        Assert.Equal(3, decoded.Single(token => token.Text == "local").Modifiers);
    }

    [Fact]
    public async Task GenericStructIndexerAndSetterValueHaveExactSemanticTokenKinds()
    {
        const string source = """
            namespace App;
            struct List<T>
            {
                T[] array;
                public int size;
                public T this[int index]
                {
                    get { return array[index]; }
                    set { array[index] = value; }
                }
                public int Size
                {
                    get { return size; }
                    set { size = value; }
                }
                public List(int capacity) { array = new T[capacity]; }
            }
            void Use() { List<int>* list = new List<int>(10); }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, null, source);

        JsonElement response = Result(await session.HandleRequestAsync("textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        var tokens = DecodeTokens(source, response.GetProperty("data"));

        var lists = tokens.Where(token => token.Text == "List").ToArray();
        Assert.Equal(4, lists.Length);
        Assert.All(lists, token => Assert.Equal(1, token.Type)); // type and constructor spellings share a color
        Assert.Equal(21, tokens.Single(token => token.Text == "this").Type); // expression keyword
        Assert.All(tokens.Where(token => token.Text is "get" or "set"),
            token => Assert.Equal(21, token.Type));
        Assert.Equal(2, tokens.Count(token => token.Text == "value"));
        Assert.All(tokens.Where(token => token.Text == "value"),
            token => Assert.Equal(14, token.Type)); // contextual modifier
    }

    [Fact]
    public async Task GenericParameterValueConstructionIsExposedAsAConstructor()
    {
        const string source = """
            namespace App;
            template VectorLike { VectorLike(float x, float y, float z); }
            T Create<T>(float x, float y, float z) where T : VectorLike
            {
                return T(x, y, z);
            }
            """;
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, null, source);

        JsonElement response = Result(await session.HandleRequestAsync("textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        var tokens = DecodeTokens(source, response.GetProperty("data"));
        Assert.Single(tokens, token => token.Text == "T" && token.Type == 1); // constructor use

        int construction = source.LastIndexOf("T(x", StringComparison.Ordinal);
        JsonElement hover = await RequestAtAsync(session, "textDocument/hover", uri, source, construction);
        Assert.Contains("VectorLike(float x, float y, float z)",
            hover.GetProperty("contents").GetProperty("value").GetString());
    }

    [Fact]
    public async Task ConstructorAndDestructorUseTheSameTypeSemanticTokenGroup()
    {
        const string source = "namespace App; struct Resource { Resource() {} ~Resource() {} }";
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", source);
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = await CreateSessionAsync(uri, null, source);

        JsonElement response = Result(await session.HandleRequestAsync("textDocument/semanticTokens/full",
            LspTestProtocol.Json(new { textDocument = new { uri } }), default));
        var tokens = DecodeTokens(source, response.GetProperty("data"));

        Assert.Equal(3, tokens.Count(token => token.Text == "Resource" && token.Type == 1));
    }

    private static string Project(string name) => $"""
        [project]
        name = "{name}"
        type = "static-library"
        [source]
        root = "src"
        """;

    private static async Task<LanguageServerSession> CreateSessionAsync(string uri,
        string? workspacePath, string source)
    {
        var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        object options = workspacePath is null
            ? new { rootUri = uri }
            : new { rootUri = uri, initializationOptions = new { workspacePath } };
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(options), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = source },
        }), default);
        return session;
    }

    private static Task ChangeAsync(LanguageServerSession session, string uri, string source, int version) =>
        session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version },
            contentChanges = new[] { new { text = source } },
        }), default);

    private static string[] Labels(JsonElement completion) => completion.GetProperty("items")
        .EnumerateArray().Select(item => item.GetProperty("filterText").GetString()!).ToArray();

    private static void AssertCompletionItem(JsonElement completion, string name, int kind,
        string xenonKind)
    {
        JsonElement item = Assert.Single(completion.GetProperty("items").EnumerateArray().Where(candidate =>
            candidate.GetProperty("filterText").GetString() == name &&
            candidate.GetProperty("kind").GetInt32() == kind));
        Assert.Equal(name, item.GetProperty("label").GetString());
        Assert.Equal(name, item.GetProperty("insertText").GetString());
        Assert.Contains(xenonKind, item.GetProperty("detail").GetString());
    }

    [Theory]
    [InlineData(EditorSymbolKind.Namespace, 9)]
    [InlineData(EditorSymbolKind.Struct, 22)]
    [InlineData(EditorSymbolKind.Interface, 8)]
    [InlineData(EditorSymbolKind.Template, 18)]
    [InlineData(EditorSymbolKind.Function, 3)]
    [InlineData(EditorSymbolKind.Method, 3)]
    [InlineData(EditorSymbolKind.Constructor, 4)]
    [InlineData(EditorSymbolKind.Destructor, 24)]
    [InlineData(EditorSymbolKind.Field, 5)]
    [InlineData(EditorSymbolKind.Property, 10)]
    [InlineData(EditorSymbolKind.Indexer, 23)]
    [InlineData(EditorSymbolKind.LocalVariable, 6)]
    [InlineData(EditorSymbolKind.Parameter, 12)]
    [InlineData(EditorSymbolKind.Constant, 21)]
    [InlineData(EditorSymbolKind.TypeParameter, 25)]
    [InlineData(EditorSymbolKind.Enum, 13)]
    [InlineData(EditorSymbolKind.EnumMember, 20)]
    [InlineData(EditorSymbolKind.Type, 22)]
    public void CompletionKindAdapterUsesNativeLspPresentation(EditorSymbolKind kind, int expected)
    {
        Assert.Equal(expected, LspCompletionItemKindAdapter.ToCompletionItemKind(kind));
    }

    private static bool HasKind(JsonElement item, string name, int kind) =>
        item.GetProperty("name").GetString() == name && item.GetProperty("kind").GetInt32() == kind;

    private static IEnumerable<JsonElement> FlattenSymbols(JsonElement symbols)
    {
        foreach (JsonElement symbol in symbols.EnumerateArray())
        {
            yield return symbol;
            if (symbol.TryGetProperty("children", out JsonElement children))
                foreach (JsonElement child in FlattenSymbols(children)) yield return child;
        }
    }

    private static string ApplyEdits(string source, JsonElement edits)
    {
        SourceText text = SourceText.From(source);
        string result = source;
        foreach (JsonElement edit in edits.EnumerateArray().OrderByDescending(item =>
                     LspTextCoordinates.ToOffset(text, new LspPosition(
                         item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
                         item.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32()))))
        {
            JsonElement range = edit.GetProperty("range");
            int start = LspTextCoordinates.ToOffset(text, new LspPosition(
                range.GetProperty("start").GetProperty("line").GetInt32(),
                range.GetProperty("start").GetProperty("character").GetInt32()));
            int end = LspTextCoordinates.ToOffset(text, new LspPosition(
                range.GetProperty("end").GetProperty("line").GetInt32(),
                range.GetProperty("end").GetProperty("character").GetInt32()));
            result = result[..start] + edit.GetProperty("newText").GetString() + result[end..];
        }
        return result;
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

    private static IReadOnlyList<(string Text, int Type, int Modifiers)> DecodeTokens(
        string source, JsonElement data)
    {
        int line = 0;
        int character = 0;
        string[] lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int[] values = data.EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var result = new List<(string, int, int)>();
        for (int i = 0; i < values.Length; i += 5)
        {
            line += values[i];
            character = values[i] == 0 ? character + values[i + 1] : values[i + 1];
            result.Add((lines[line].Substring(character, values[i + 2]), values[i + 3], values[i + 4]));
        }
        return result;
    }

    private static JsonElement Result(object? value) => JsonSerializer.SerializeToElement(value,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
