using System.Text.Json;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;

namespace Xenon.LanguageServer.Tests;

public sealed class LambdaIntelligenceTests
{
    [Fact]
    public async Task LambdaParameterSupportsHoverDefinitionRenameAndIsolatedCompletion()
    {
        const string source = """
            namespace App;
            int Test(int outer) {
                function int(int)* callback = function int(int value) {
                    return value + 1;
                };
                return callback(outer);
            }
            """;
        using var directory = new TestDirectory();
        string uri = DocumentUri.FromPath(directory.Write("main.xe", source)).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new { rootUri = uri }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new {
            textDocument = new { uri, version = 1, text = source },
        }), default);

        int offset = source.IndexOf("return value", StringComparison.Ordinal) + 7;
        LspPosition position = LspTextCoordinates.ToPosition(SourceText.From(source), offset);
        var parameters = new { textDocument = new { uri }, position };
        JsonElement hover = Result(await session.HandleRequestAsync("textDocument/hover",
            LspTestProtocol.Json(parameters), default));
        Assert.Contains("int value", hover.GetProperty("contents").GetProperty("value").GetString());
        JsonElement definition = Result(await session.HandleRequestAsync("textDocument/definition",
            LspTestProtocol.Json(parameters), default));
        Assert.Equal(1, definition.GetArrayLength());
        Assert.Equal(2, definition[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        JsonElement rename = Result(await session.HandleRequestAsync("textDocument/rename",
            LspTestProtocol.Json(new { textDocument = new { uri }, position, newName = "input" }), default));
        Assert.Equal(2, rename.GetProperty("changes").GetProperty(uri).GetArrayLength());
        JsonElement completion = Result(await session.HandleRequestAsync("textDocument/completion",
            LspTestProtocol.Json(parameters), default));
        string?[] labels = completion.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("label").GetString()).ToArray();
        Assert.Contains("value", labels);
        Assert.DoesNotContain("outer", labels);
        Assert.DoesNotContain("callback", labels);
        Assert.DoesNotContain(labels, label => label?.Contains("<lambda_", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ClosureCaptureListAndBodyShareOriginalLocalIdentity()
    {
        const string source = """
            namespace App;
            int Test() {
                int offset = 10;
                function int(int) callback = [offset](int value) => {
                    return offset + value;
                };
                return callback(1);
            }
            """;
        using var directory = new TestDirectory();
        string uri = DocumentUri.FromPath(directory.Write("main.xe", source)).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new { rootUri = uri }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new {
            textDocument = new { uri, version = 1, text = source },
        }), default);

        SourceText text = SourceText.From(source);
        int captureOffset = source.IndexOf("[offset]", StringComparison.Ordinal) + 1;
        int bodyOffset = source.IndexOf("return offset", StringComparison.Ordinal) + 7;
        foreach (int offset in new[] { captureOffset, bodyOffset })
        {
            LspPosition position = LspTextCoordinates.ToPosition(text, offset);
            var parameters = new { textDocument = new { uri }, position };
            JsonElement definition = Result(await session.HandleRequestAsync("textDocument/definition",
                LspTestProtocol.Json(parameters), default));
            Assert.Single(definition.EnumerateArray());
            Assert.Equal(2, definition[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
            JsonElement references = Result(await session.HandleRequestAsync("textDocument/references",
                LspTestProtocol.Json(new { textDocument = new { uri }, position, context = new { includeDeclaration = true } }), default));
            Assert.Equal(3, references.GetArrayLength());
        }

        LspPosition callbackPosition = LspTextCoordinates.ToPosition(text,
            source.IndexOf("callback =", StringComparison.Ordinal));
        JsonElement hover = Result(await session.HandleRequestAsync("textDocument/hover",
            LspTestProtocol.Json(new { textDocument = new { uri }, position = callbackPosition }), default));
        Assert.Contains("function int(int)", hover.GetProperty("contents").GetProperty("value").GetString());
    }

    [Fact]
    public async Task OverloadedCallContextualizesLambdaWithSelectedSignature()
    {
        const string source = """
            namespace App;
            void Run(function void(float) callback) { }
            void Run(function void(int) callback) { }
            void Test() {
                Run((int value) => {
                    int copy = value;
                });
            }
            """;
        using var directory = new TestDirectory();
        string uri = DocumentUri.FromPath(directory.Write("main.xe", source)).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new { rootUri = uri }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new {
            textDocument = new { uri, version = 1, text = source },
        }), default);

        SourceText text = SourceText.From(source);
        LspPosition bodyPosition = LspTextCoordinates.ToPosition(text,
            source.LastIndexOf("value", StringComparison.Ordinal));
        var parameters = new { textDocument = new { uri }, position = bodyPosition };
        JsonElement hover = Result(await session.HandleRequestAsync("textDocument/hover",
            LspTestProtocol.Json(parameters), default));
        Assert.Contains("int value", hover.GetProperty("contents").GetProperty("value").GetString());
        JsonElement definition = Result(await session.HandleRequestAsync("textDocument/definition",
            LspTestProtocol.Json(parameters), default));
        Assert.Single(definition.EnumerateArray());
        Assert.Equal(4, definition[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
    }

    private static JsonElement Result(object? value) => JsonSerializer.SerializeToElement(value,
        new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}
