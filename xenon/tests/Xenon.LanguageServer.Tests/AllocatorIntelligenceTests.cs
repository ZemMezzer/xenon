using System.Text.Json;
using Xenon.Compiler;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;

namespace Xenon.LanguageServer.Tests;

public sealed class AllocatorIntelligenceTests
{
    private const string Source = """
        namespace Example;
        allocator Heap {
            +Heap(nuint size, nuint alignment) { return malloc(size, alignment); }
            -Heap(void* block) { free(block); }
            public int initial = 1;
            public void Ping() {}
        }
        struct Value {
            public allocator Heap* context;
            public int number = context->initial;
            public @Value(Heap* provider) { context = provider; }
            public Value(int first, int second) { number += first + second; }
        }
        allocator Arena : Heap {
            public allocator Heap* parent;
            public @Arena(Heap* provider) { parent = provider; }
            public Arena() {}
            +Arena(nuint size, nuint alignment) { return malloc(size, alignment); }
            -Arena(void* block) { free(block); }
        }
        void Use() {
            Heap* provider = new Heap();
            Value* value = new(provider) Value(2, 3);
            Arena* arena = new(provider) Arena();
            int[] values = new(provider) int[4];
            provider->Ping();
            delete(values); delete(value); delete(arena); delete(provider);
        }
        """;

    [Fact]
    public async Task HoverOutlineCompletionAndSignatureUseSourceAllocatorNames()
    {
        using var fixture = new Fixture();
        await fixture.Open();
        Assert.False(Compilation.Create(SourceText.From(Source)).HasErrors);
        foreach ((string text, string expected) in new[] {
            ("allocator Heap", "allocator Heap"),
            ("+Heap", "+Heap(nuint size, nuint alignment)"),
            ("-Heap", "-Heap(void* block)"),
            ("@Value", "@Value(Heap* provider)"),
            ("@Arena", "@Arena(Heap* provider)"),
            ("+Arena", "+Arena(nuint size, nuint alignment)") })
        {
            int offset = Source.IndexOf(text, StringComparison.Ordinal) +
                (text.StartsWith("allocator") ? "allocator ".Length : 1);
            JsonElement hover = await fixture.At("textDocument/hover", offset);
            Assert.Contains(expected, hover.GetProperty("contents").GetProperty("value").GetString());
        }
        JsonElement outline = await fixture.Request("textDocument/documentSymbol", new { textDocument = new { fixture.uri } });
        Assert.DoesNotContain("$allocate", outline.GetRawText());
        Assert.DoesNotContain("$deallocate", outline.GetRawText());
        Assert.DoesNotContain("$allocationConstructor", outline.GetRawText());
        Assert.Contains(Descendants(outline), x => x.GetProperty("name").GetString() == "@Value");
        JsonElement workspace = await fixture.Request("workspace/symbol", new { query = "+Heap" });
        Assert.Contains(workspace.EnumerateArray(), x => x.GetProperty("name").GetString() == "+Heap");
        JsonElement completion = await fixture.At("textDocument/completion", Source.IndexOf("void Use", StringComparison.Ordinal));
        Assert.Contains(completion.GetProperty("items").EnumerateArray(), x => x.GetProperty("label").GetString() == "allocator");
        JsonElement members = await fixture.At("textDocument/completion", Source.IndexOf("provider->Ping", StringComparison.Ordinal) + "provider->".Length);
        Assert.Contains(members.GetProperty("items").EnumerateArray(), x => x.GetProperty("label").GetString() == "Ping");
        Assert.DoesNotContain(members.GetProperty("items").EnumerateArray(), x => x.GetProperty("label").GetString()!.StartsWith('$'));
        JsonElement signature = await fixture.At("textDocument/signatureHelp", Source.IndexOf("Value(2, 3)", StringComparison.Ordinal) + "Value(2, ".Length);
        Assert.Equal(1, signature.GetProperty("activeParameter").GetInt32());
        Assert.Contains("Value(int first, int second)", signature.GetProperty("signatures")[0].GetProperty("label").GetString());
        JsonElement providerSignature = await fixture.At("textDocument/signatureHelp", Source.IndexOf("new(provider)", StringComparison.Ordinal) + 5);
        Assert.Equal(JsonValueKind.Null, providerSignature.ValueKind);
    }

    [Theory]
    [InlineData("allocator Heap", 10, "Heap", "Provider")]
    [InlineData("+Heap", 1, "Heap", "Provider")]
    [InlineData("-Heap", 1, "Heap", "Provider")]
    [InlineData("struct Value", 7, "Value", "Item")]
    [InlineData("@Value", 1, "Value", "Item")]
    [InlineData("@Arena", 1, "Arena", "Pool")]
    [InlineData("+Arena", 1, "Arena", "Pool")]
    public async Task RenameIncludesAllocationOperations(string marker, int delta, string oldName, string newName)
    {
        using var fixture = new Fixture();
        await fixture.Open();
        int offset = Source.IndexOf(marker, StringComparison.Ordinal) + delta;
        JsonElement edit = await fixture.Request("textDocument/rename", new {
            textDocument = new { fixture.uri }, position = LspTextCoordinates.ToPosition(SourceText.From(Source), offset), newName });
        string changed = Source;
        var edits = edit.GetProperty("changes").GetProperty(fixture.uri).EnumerateArray()
            .Select(e => (Start: Offset(e.GetProperty("range").GetProperty("start")),
                End: Offset(e.GetProperty("range").GetProperty("end")), Text: e.GetProperty("newText").GetString()!))
            .OrderByDescending(e => e.Start).ToArray();
        foreach (var e in edits) changed = changed[..e.Start] + e.Text + changed[e.End..];
        Assert.DoesNotContain(oldName, changed);
        Compilation compilation = Compilation.Create(SourceText.From(changed));
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics.Select(d => d.Message)));
    }

    [Fact]
    public async Task ProviderNavigationAndAllocatorSemanticTokenAreAvailable()
    {
        using var fixture = new Fixture();
        await fixture.Open();
        JsonElement definition = await fixture.At("textDocument/definition", Source.IndexOf("new(provider)", StringComparison.Ordinal) + 5);
        Assert.NotEqual(JsonValueKind.Null, definition.ValueKind);
        JsonElement tokens = await fixture.Request("textDocument/semanticTokens/full", new { textDocument = new { fixture.uri } });
        int[] data = tokens.GetProperty("data").EnumerateArray().Select(x => x.GetInt32()).ToArray();
        int line = 0, character = 0;
        bool found = false;
        for (int i = 0; i < data.Length; i += 5) {
            line += data[i]; character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            if (line == 1 && character == 0) { Assert.Equal(9, data[i + 2]); Assert.Equal(20, data[i + 3]); found = true; }
        }
        Assert.True(found);
    }

    private static int Offset(JsonElement p) => LspTextCoordinates.ToOffset(SourceText.From(Source),
        new LspPosition(p.GetProperty("line").GetInt32(), p.GetProperty("character").GetInt32()));

    private static IEnumerable<JsonElement> Descendants(JsonElement nodes)
    {
        foreach (JsonElement node in nodes.EnumerateArray())
        {
            yield return node;
            if (node.TryGetProperty("children", out JsonElement children))
                foreach (JsonElement child in Descendants(children)) yield return child;
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TestDirectory directory = new();
        private readonly LanguageServerSession session = new((_, _) => Task.CompletedTask, diagnosticDebounce: TimeSpan.Zero);
        public string uri { get; }
        public Fixture() => uri = DocumentUri.FromPath(directory.Write("allocators.xe", Source)).AbsoluteUri;
        public async Task Open() {
            await Request("initialize", new { rootUri = uri });
            await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
            await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new {
                textDocument = new { uri, version = 1, text = Source } }), default);
        }
        public Task<JsonElement> At(string method, int offset) => Request(method, new {
            textDocument = new { uri }, position = LspTextCoordinates.ToPosition(SourceText.From(Source), offset) });
        public async Task<JsonElement> Request(string method, object parameters) => JsonSerializer.SerializeToElement(
            await session.HandleRequestAsync(method, LspTestProtocol.Json(parameters), default),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        public void Dispose() { session.DisposeAsync().AsTask().GetAwaiter().GetResult(); directory.Dispose(); }
    }
}
