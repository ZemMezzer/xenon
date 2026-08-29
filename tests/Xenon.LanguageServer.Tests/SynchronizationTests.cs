using System.Text.Json;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class SynchronizationTests
{
    [Fact]
    public async Task OpenChangeSaveClosePreservesEditorAndBackingVersions()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "disk\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        var notifications = new List<string>();
        await using var session = new LanguageServerSession((method, _) =>
        {
            notifications.Add(method);
            return Task.CompletedTask;
        }, diagnosticDebounce: TimeSpan.Zero);
        await InitializeAsync(session, file);

        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = "a😀b\n" },
        }), default);
        Workspace workspace = Assert.Single(session.Workspaces);
        DocumentSnapshot opened = Assert.Single(workspace.CurrentSnapshot.Documents);
        Assert.Equal("a😀b\n", opened.OverlayText!.Text);
        Assert.Equal(LspDocumentVersions.FromLsp(1), opened.Version);

        await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new object[]
            {
                new { range = new { start = new { line = 0, character = 1 }, end = new { line = 0, character = 3 } }, rangeLength = 2, text = "X" },
                new { range = new { start = new { line = 0, character = 2 }, end = new { line = 0, character = 3 } }, rangeLength = 1, text = "Y" },
            },
        }), default);
        DocumentSnapshot changed = Assert.Single(workspace.CurrentSnapshot.Documents);
        Assert.Equal("aXY\n", changed.EffectiveText.Text);
        Assert.Equal(LspDocumentVersions.FromLsp(2), changed.Version);

        directory.Write("main.xe", "saved\n");
        BackingVersion beforeSave = changed.BackingVersion;
        await session.HandleNotificationAsync("textDocument/didSave", LspTestProtocol.Json(new
        {
            textDocument = new { uri },
        }), default);
        DocumentSnapshot saved = Assert.Single(workspace.CurrentSnapshot.Documents);
        Assert.Equal(LspDocumentVersions.FromLsp(2), saved.Version);
        Assert.True(saved.BackingVersion > beforeSave);
        Assert.Equal("saved\n", saved.DiskText!.Text);
        Assert.Equal("aXY\n", saved.EffectiveText.Text);

        await session.HandleNotificationAsync("textDocument/didClose", LspTestProtocol.Json(new
        {
            textDocument = new { uri },
        }), default);
        DocumentSnapshot closed = Assert.Single(workspace.CurrentSnapshot.Documents);
        Assert.False(closed.IsOpen);
        Assert.Equal("saved\n", closed.EffectiveText.Text);
        Assert.Equal(LspDocumentVersions.FromLsp(2), closed.Version);

        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = "reopened\n" },
        }), default);
        await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 2 },
            contentChanges = new[] { new { text = "reopened changed\n" } },
        }), default);
        DocumentSnapshot reopened = Assert.Single(workspace.CurrentSnapshot.Documents);
        Assert.Equal("reopened changed\n", reopened.EffectiveText.Text);
        Assert.Equal(LspDocumentVersions.FromLsp(2), reopened.Version);
    }

    [Fact]
    public async Task InvalidMultiChangePublishesNothingAndStaleVersionIsRejected()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("main.xe", "abc\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await InitializeAsync(session, file);
        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1, text = "abc\n" },
        }), default);
        Workspace workspace = Assert.Single(session.Workspaces);
        WorkspaceGeneration generation = workspace.CurrentSnapshot.Generation;

        await Assert.ThrowsAsync<Xenon.LanguageServer.Protocol.JsonRpcException>(() =>
            session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
            {
                textDocument = new { uri, version = 2 },
                contentChanges = new[]
                {
                    new { range = new { start = new { line = 0, character = 1 }, end = new { line = 0, character = 99 } }, text = "x" },
                },
            }), default));
        Assert.Equal(generation, workspace.CurrentSnapshot.Generation);

        await Assert.ThrowsAsync<Xenon.LanguageServer.Protocol.JsonRpcException>(() =>
            session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
            {
                textDocument = new { uri, version = 1 },
                contentChanges = new[] { new { text = "stale" } },
            }), default));
        Assert.Equal("abc\n", Assert.Single(workspace.CurrentSnapshot.Documents).EffectiveText.Text);
    }

    [Fact]
    public async Task SharedPhysicalFileSynchronizesEveryProjectContext()
    {
        using var directory = new TestDirectory();
        string shared = directory.Write("shared/common.xe", "disk\n");
        directory.Write("A/A.xeproj", """
            [project]
            name = "A"
            type = "executable"
            [source]
            root = "../shared"
            """);
        directory.Write("B/B.xeproj", """
            [project]
            name = "B"
            type = "executable"
            [source]
            root = "../shared"
            """);
        string manifest = directory.Write("Both.xws", """
            [workspace]
            projects = ["A/A.xeproj", "B/B.xeproj"]
            """);
        string uri = DocumentUri.FromPath(shared).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            initializationOptions = new { workspacePath = manifest },
        }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);

        await session.HandleNotificationAsync("textDocument/didOpen", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 0, text = "abc\n" },
        }), default);
        await session.HandleNotificationAsync("textDocument/didChange", LspTestProtocol.Json(new
        {
            textDocument = new { uri, version = 1 },
            contentChanges = new[]
            {
                new { range = new { start = new { line = 0, character = 1 }, end = new { line = 0, character = 2 } }, text = "X" },
            },
        }), default);

        Workspace workspace = Assert.Single(session.Workspaces);
        WorkspaceSnapshot beforeClose = workspace.CurrentSnapshot;
        DocumentSnapshot[] documents = workspace.CurrentSnapshot.Documents.ToArray();
        Assert.Equal(2, documents.Length);
        Assert.All(documents, document =>
        {
            Assert.True(document.IsOpen);
            Assert.Equal("aXc\n", document.EffectiveText.Text);
            Assert.Equal(LspDocumentVersions.FromLsp(1), document.Version);
        });

        await session.HandleNotificationAsync("textDocument/didClose", LspTestProtocol.Json(new
        {
            textDocument = new { uri },
        }), default);
        Assert.All(workspace.CurrentSnapshot.Documents, document =>
        {
            Assert.False(document.IsOpen);
            Assert.Equal(LspDocumentVersions.FromLsp(1), document.Version);
        });
        Assert.All(beforeClose.Documents, document =>
        {
            Assert.True(document.IsOpen);
            Assert.Equal("aXc\n", document.EffectiveText.Text);
        });
    }

    private static async Task InitializeAsync(LanguageServerSession session, string file)
    {
        JsonElement initialize = LspTestProtocol.Json(new
        {
            rootUri = DocumentUri.FromPath(file).AbsoluteUri,
        });
        await session.HandleRequestAsync("initialize", initialize, default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
    }
}
