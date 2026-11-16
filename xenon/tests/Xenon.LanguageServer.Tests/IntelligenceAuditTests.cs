using System.Text.Json;
using Xenon.Compiler.Text;
using Xenon.LanguageServer;
using Xenon.LanguageServer.Text;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class IntelligenceAuditTests
{
    [Theory]
    [InlineData("public void Test()", "this.")]
    [InlineData("public Ptr()", "this.")]
    [InlineData("public ~Ptr()", "this.")]
    [InlineData("public int readonly Test()", "this.")]
    [InlineData("public void Test()", "(this).")]
    [InlineData("public void Test()", "((this)).")]
    [InlineData("public void Test()", "this->")]
    [InlineData("public ~Ptr()", "this.Va")]
    public async Task ThisCompletionWorksInInstanceBodies(string declaration, string receiver)
    {
        await using var document = await Document.OpenAsync($$"""
            namespace App;
            struct Ptr
            {
                private int Value;
                public static int StaticValue;
                public void Update() {}
                {{declaration}} { var s = {{receiver}}$0 }
            }
            """);
        string[] labels = Labels(await document.RequestAsync("textDocument/completion"));
        Assert.Contains("Value", labels);
        Assert.Contains("Update", labels);
        Assert.DoesNotContain("StaticValue", labels);
        Assert.DoesNotContain("Ptr", labels);
    }

    [Theory]
    [InlineData("Ptr*", "value->", true)]
    [InlineData("Ptr*", "value.", false)]
    [InlineData("Ptr*&", "value->", true)]
    [InlineData("Ptr*&", "value.", false)]
    [InlineData("Ptr&", "value.", true)]
    [InlineData("Ptr&", "value->", false)]
    [InlineData("shared<Ptr>&", "value->", true)]
    [InlineData("storage<Ptr*>", "value->", true)]
    [InlineData("storage<Ptr*>", "value.", false)]
    public async Task CompletionUsesTheEffectiveReceiverType(string type, string receiver, bool hasMembers)
    {
        await using var document = await Document.OpenAsync($$"""
            namespace App;
            struct Ptr { public int Value; private int Hidden; }
            void Use({{type}} value) { {{receiver}}$0 }
            """);
        string[] labels = Labels(await document.RequestAsync("textDocument/completion"));
        Assert.Equal(hasMembers, labels.Contains("Value"));
        Assert.DoesNotContain("Hidden", labels);
    }

    [Theory]
    [InlineData("T", ".")]
    [InlineData("T&", ".")]
    [InlineData("T*", "->")]
    [InlineData("shared<T>", "->")]
    [InlineData("unique<T>", "->")]
    public async Task ConstrainedGenericReceiversExposeTheirMembers(string type, string access)
    {
        await using var document = await Document.OpenAsync($$"""
            namespace App;
            template HasValue { int Value { get; } int Read(); }
            void Use<T>({{type}} value) where T : HasValue { value{{access}}$0 }
            """);
        string[] labels = Labels(await document.RequestAsync("textDocument/completion"));
        Assert.Contains("Value", labels);
        Assert.Contains("Read", labels);
    }

    [Fact]
    public async Task UnqualifiedCompletionPreservesMethodOverloads()
    {
        await using var document = await Document.OpenAsync("""
            namespace App;
            struct Ptr
            {
                public void Update(int value) {}
                public void Update(float value) {}
                public void Use() { Up$0 }
            }
            """);
        JsonElement completion = await document.RequestAsync("textDocument/completion");
        JsonElement[] overloads = completion.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("filterText").GetString() == "Update").ToArray();
        Assert.Equal(2, overloads.Length);
        Assert.Contains(overloads, item => item.GetProperty("detail").GetString()!.Contains("int value"));
        Assert.Contains(overloads, item => item.GetProperty("detail").GetString()!.Contains("float value"));
    }

    [Theory]
    [InlineData("Up", 2)]
    [InlineData("this.Up", 2)]
    [InlineData("int Update = 0; Up", 0)]
    public async Task InheritedOverloadsRespectOverridesAndLocalShadowing(string expression, int expectedMethods)
    {
        await using var document = await Document.OpenAsync($$"""
            namespace App;
            struct Base
            {
                public virtual void Update(int original) {}
                public void Update(float value) {}
                private void Update(byte hidden) {}
            }
            struct Ptr : Base
            {
                public override void Update(int value) {}
                public void Use() { {{expression}}$0 }
            }
            """);
        JsonElement completion = await document.RequestAsync("textDocument/completion");
        JsonElement[] methods = completion.GetProperty("items").EnumerateArray()
            .Where(item => item.GetProperty("filterText").GetString() == "Update" &&
                item.GetProperty("kind").GetInt32() == 3).ToArray();
        Assert.Equal(expectedMethods, methods.Length);
        Assert.DoesNotContain(methods, item => item.GetProperty("detail").GetString()!.Contains("original"));
        Assert.DoesNotContain(methods, item => item.GetProperty("detail").GetString()!.Contains("hidden"));
    }

    [Fact]
    public async Task ThisDoesNotOfferInstanceMembersInStaticMethods()
    {
        await using var document = await Document.OpenAsync("""
            namespace App;
            struct Ptr { public int Value; public static void Use() { this.$0 } }
            """);
        Assert.Empty(Labels(await document.RequestAsync("textDocument/completion")));
    }

    [Fact]
    public async Task StaticInterfaceCompletionDoesNotOfferInstanceProperties()
    {
        await using var document = await Document.OpenAsync("""
            namespace App;
            interface IValue { int Value { get; } }
            void Use() { IValue.$0 }
            """);
        Assert.DoesNotContain("Value", Labels(await document.RequestAsync("textDocument/completion")));
    }

    [Theory]
    [InlineData("Outer(Inner()$0, 2)", "Outer", 0)]
    [InlineData("Outer(Inner(), $0)", "Outer", 1)]
    [InlineData("Outer(Inner($0), 2)", "Inner", 0)]
    [InlineData("Outer(1, 2)$0", null, 0)]
    [InlineData("Outer(new Ptr()$0, 2)", "Outer", 0)]
    [InlineData("Outer(values[0]$0, 2)", "Outer", 0)]
    public async Task SignatureHelpRespectsClosingDelimiters(string expression, string? expected, int parameter)
    {
        await using var document = await Document.OpenAsync($$"""
            namespace App;
            struct Ptr
            {
                public Ptr() {}
                public int this[int index] { get { return index; } }
            }
            int Inner() { return 1; }
            void Outer(int first, int second) {}
            void Outer(Ptr* first, int second) {}
            void Use(Ptr values) { {{expression}}; }
            """);
        JsonElement signature = await document.RequestAsync("textDocument/signatureHelp");
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, signature.ValueKind);
            return;
        }
        Assert.Contains(expected + "(", signature.GetProperty("signatures")[0].GetProperty("label").GetString());
        Assert.Equal(parameter, signature.GetProperty("activeParameter").GetInt32());
    }

    [Fact]
    public async Task ThisMemberSupportsHoverDefinitionAndReferences()
    {
        await using var document = await Document.OpenAsync("""
            namespace App;
            struct Ptr
            {
                private int Value;
                public ~Ptr() { var s = this.$0Value; }
            }
            """);
        JsonElement hover = await document.RequestAsync("textDocument/hover");
        Assert.Contains("int Value", hover.GetProperty("contents").GetProperty("value").GetString());
        JsonElement definition = await document.RequestAsync("textDocument/definition");
        Assert.Equal(3, Assert.Single(definition.EnumerateArray()).GetProperty("range")
            .GetProperty("start").GetProperty("line").GetInt32());
        JsonElement references = await document.RequestAsync("textDocument/references");
        Assert.Single(references.EnumerateArray());
    }

    private static string[] Labels(JsonElement completion) => completion.GetProperty("items")
        .EnumerateArray().Select(item => item.GetProperty("filterText").GetString()!).ToArray();

    private sealed class Document : IAsyncDisposable
    {
        private readonly TestDirectory _directory = new();
        private readonly LanguageServerSession _session = new((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        private string _uri = "";
        private string _source = "";
        private int _position;

        public static async Task<Document> OpenAsync(string markedSource)
        {
            var document = new Document();
            document._position = markedSource.IndexOf("$0", StringComparison.Ordinal);
            Assert.True(document._position >= 0);
            document._source = markedSource.Replace("$0", "", StringComparison.Ordinal);
            document._uri = DocumentUri.FromPath(document._directory.Write("main.xe", document._source)).AbsoluteUri;
            await document._session.HandleRequestAsync("initialize",
                LspTestProtocol.Json(new { rootUri = document._uri }), default);
            await document._session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
            await document._session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
            {
                textDocument = new { uri = document._uri, version = 1, text = document._source },
            }), default);
            return document;
        }

        public async Task<JsonElement> RequestAsync(string method)
        {
            object? response = await _session.HandleRequestAsync(method, LspTestProtocol.Json(new
            {
                textDocument = new { uri = _uri },
                position = LspTextCoordinates.ToPosition(SourceText.From(_source), _position),
            }), default);
            return JsonSerializer.SerializeToElement(response,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        }

        public async ValueTask DisposeAsync()
        {
            await _session.DisposeAsync();
            _directory.Dispose();
        }
    }
}
