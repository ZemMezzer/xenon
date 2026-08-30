using System.Text.Json;
using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.LanguageServer.Text;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Tests;

public sealed class ImplicitFolderWorkspaceTests
{
    [Fact]
    public async Task FolderSourcesShareOneRecursiveSemanticCompilation()
    {
        const string mainSource = "using Console; namespace Program; void Run() { WriteLine(); }";
        const string consoleSource = "namespace Console; public void WriteLine() {}";
        using var directory = new TestDirectory();
        string main = directory.Write("Main.xe", mainSource);
        string console = directory.Write("Library/Console.xe", consoleSource);
        string mainUri = DocumentUri.FromPath(main).AbsoluteUri;
        string consoleUri = DocumentUri.FromPath(console).AbsoluteUri;
        await using LanguageServerSession session = await InitializeFolderAsync(directory.Path);

        await OpenAsync(session, mainUri, mainSource);

        Workspace workspace = Assert.Single(session.Workspaces);
        Assert.Null(workspace.Configuration);
        Assert.Equal(2, workspace.CurrentSnapshot.Documents.Length);
        Assert.Single(workspace.CurrentSnapshot.Projects);
        Assert.Null(workspace.CurrentSnapshot.RootProject.Configuration.ProjectFilePath);
        JsonElement definition = await DefinitionAsync(session, mainUri, mainSource,
            mainSource.IndexOf("WriteLine", StringComparison.Ordinal));
        Assert.Equal(consoleUri, definition[0].GetProperty("uri").GetString());
    }

    [Fact]
    public async Task ImplicitFolderReconcilesCreatedAndDeletedSources()
    {
        const string mainSource = "using Console; namespace Program; void Run() { WriteLine(); }";
        const string consoleSource = "namespace Console; public void WriteLine() {}";
        using var directory = new TestDirectory();
        string main = directory.Write("Main.xe", mainSource);
        string console = directory.PathOf("Console.xe");
        string mainUri = DocumentUri.FromPath(main).AbsoluteUri;
        string consoleUri = DocumentUri.FromPath(console).AbsoluteUri;
        await using LanguageServerSession session = await InitializeFolderAsync(directory.Path);
        await OpenAsync(session, mainUri, mainSource);

        directory.Write("Console.xe", consoleSource);
        await WatchedAsync(session, (console, 1));
        Assert.Equal(2, Assert.Single(session.Workspaces).CurrentSnapshot.Documents.Length);
        JsonElement addedDefinition = await DefinitionAsync(session, mainUri, mainSource,
            mainSource.IndexOf("WriteLine", StringComparison.Ordinal));
        Assert.Equal(consoleUri, addedDefinition[0].GetProperty("uri").GetString());

        File.Delete(console);
        await WatchedAsync(session, (console, 3));
        Assert.Single(Assert.Single(session.Workspaces).CurrentSnapshot.Documents);
        JsonElement removedDefinition = await DefinitionAsync(session, mainUri, mainSource,
            mainSource.IndexOf("WriteLine", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Null, removedDefinition.ValueKind);
    }

    [Fact]
    public async Task NewExplicitProjectReplacesImplicitFolderAndPreservesOverlay()
    {
        const string diskSource = "namespace Program; void Disk() {}";
        const string overlaySource = "namespace Program; void Overlay() {}";
        using var directory = new TestDirectory();
        string main = directory.Write("src/Main.xe", diskSource);
        directory.Write("ignored/Other.xe", "namespace Ignored;\n");
        string mainUri = DocumentUri.FromPath(main).AbsoluteUri;
        await using LanguageServerSession session = await InitializeFolderAsync(directory.Path);
        await OpenAsync(session, mainUri, overlaySource, version: 7);
        Assert.Equal(2, Assert.Single(session.Workspaces).CurrentSnapshot.Documents.Length);

        string project = directory.Write("Program.xeproj", Project("Program", "src"));
        await WatchedAsync(session, (project, 1));

        Workspace explicitWorkspace = Assert.Single(session.Workspaces);
        DocumentSnapshot document = Assert.Single(explicitWorkspace.CurrentSnapshot.Documents);
        Assert.Equal(System.IO.Path.GetFullPath(project),
            explicitWorkspace.CurrentSnapshot.RootProject.Configuration.ProjectFilePath);
        Assert.True(document.IsOpen);
        Assert.Equal(LspDocumentVersions.FromLsp(7), document.Version);
        Assert.Equal(overlaySource, document.EffectiveText.Text);
    }

    [Fact]
    public async Task FileOutsideImplicitFolderRemainsAnIndependentLooseWorkspace()
    {
        using var folder = new TestDirectory();
        using var outside = new TestDirectory();
        folder.Write("Main.xe", "namespace Folder;\n");
        string external = outside.Write("Loose.xe", "namespace Loose;\n");
        string externalUri = DocumentUri.FromPath(external).AbsoluteUri;
        await using LanguageServerSession session = await InitializeFolderAsync(folder.Path);

        await OpenAsync(session, externalUri, "namespace Loose;\n");

        Assert.Equal(2, session.Workspaces.Count);
        Assert.All(session.Workspaces, workspace => Assert.Single(workspace.CurrentSnapshot.Documents));
        Assert.Single(session.Workspaces.Where(workspace => workspace.CurrentSnapshot.Documents.Any(document =>
            document.PhysicalPath is not null && DocumentUri.PathComparer.Equals(
                DocumentUri.NormalizePath(document.PhysicalPath),
                DocumentUri.NormalizePath(external)))));
    }

    [Fact]
    public async Task NoFolderInitializationStillCreatesTrueLooseFileOnOpen()
    {
        using var directory = new TestDirectory();
        string file = directory.Write("Single.xe", "namespace Single;\n");
        string uri = DocumentUri.FromPath(file).AbsoluteUri;
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            rootUri = (string?)null,
            rootPath = (string?)null,
        }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);

        Assert.Empty(session.Workspaces);
        await OpenAsync(session, uri, "namespace Single;\n");
        Workspace workspace = Assert.Single(session.Workspaces);
        Assert.Single(workspace.CurrentSnapshot.Documents);
        Assert.Null(workspace.CurrentSnapshot.RootProject.Configuration.ProjectFilePath);
    }

    [Fact]
    public async Task WorkspaceFoldersInitializeImplicitFolderWhenRootUriIsAbsent()
    {
        using var directory = new TestDirectory();
        directory.Write("Main.xe", "namespace Main;\n");
        await using var session = new LanguageServerSession((_, _) => Task.CompletedTask);
        string uri = DocumentUri.FromPath(directory.Path).AbsoluteUri;

        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            rootUri = (string?)null,
            workspaceFolders = new[] { new { uri, name = "Scratch" } },
        }), default);

        Assert.Single(session.Workspaces);
        Assert.Single(Assert.Single(session.Workspaces).CurrentSnapshot.Documents);
    }

    [Fact]
    public async Task DefinitionAndImplementationNavigateAcrossImplicitFolderFiles()
    {
        const string contractSource =
            "namespace Contracts; interface IService { void Run(); } " +
            "struct Base { public virtual void Tick() {} }";
        const string implementationSource =
            "using Contracts; namespace Services; struct Service : Base, IService { " +
            "public void Run() {} public override void Tick() {} }";
        const string mainSource =
            "using Contracts; namespace App; void Invoke(IService service, Base value) { " +
            "service.Run(); value.Tick(); }";
        using var directory = new TestDirectory();
        string contract = directory.Write("Contracts.xe", contractSource);
        string implementation = directory.Write("Services/Service.xe", implementationSource);
        string main = directory.Write("Main.xe", mainSource);
        string contractUri = DocumentUri.FromPath(contract).AbsoluteUri;
        string implementationUri = DocumentUri.FromPath(implementation).AbsoluteUri;
        string mainUri = DocumentUri.FromPath(main).AbsoluteUri;
        await using LanguageServerSession session = await InitializeFolderAsync(directory.Path);
        await OpenAsync(session, mainUri, mainSource);

        JsonElement definition = await RequestAtAsync(session, "textDocument/definition",
            mainUri, mainSource, mainSource.LastIndexOf("Run", StringComparison.Ordinal));
        Assert.Equal(contractUri, definition[0].GetProperty("uri").GetString());

        JsonElement implementationResult = await RequestAtAsync(session,
            "textDocument/implementation", mainUri, mainSource,
            mainSource.LastIndexOf("Run", StringComparison.Ordinal));
        Assert.Equal(implementationUri,
            Assert.Single(implementationResult.EnumerateArray()).GetProperty("uri").GetString());

        JsonElement overrideResult = await RequestAtAsync(session,
            "textDocument/implementation", mainUri, mainSource,
            mainSource.LastIndexOf("Tick", StringComparison.Ordinal));
        Assert.Equal(implementationUri,
            Assert.Single(overrideResult.EnumerateArray()).GetProperty("uri").GetString());
    }

    [Fact]
    public async Task DefinitionAndMemberImplementationNavigateAcrossWorkspaceProjects()
    {
        const string contractSource = "namespace Contracts; interface IService { void Run(); }";
        const string appSource =
            "using Contracts; namespace App; struct Service : IService { public void Run() {} } " +
            "void Invoke(IService service) { service.Run(); }";
        using var directory = new TestDirectory();
        string contract = directory.Write("Core/src/Contracts.xe", contractSource);
        directory.Write("Core/Core.xeproj", """
            [project]
            name = "Core"
            type = "static-library"
            [source]
            root = "src"
            """);
        string app = directory.Write("App/src/Main.xe", appSource);
        directory.Write("App/App.xeproj", """
            [project]
            name = "App"
            type = "executable"
            [source]
            root = "src"
            [references]
            projects = ["../Core/Core.xeproj"]
            """);
        directory.Write("Root.xws", """
            [workspace]
            projects = ["Core/Core.xeproj", "App/App.xeproj"]
            """);
        string contractUri = DocumentUri.FromPath(contract).AbsoluteUri;
        string appUri = DocumentUri.FromPath(app).AbsoluteUri;
        await using LanguageServerSession session = await InitializeFolderAsync(directory.Path);
        await OpenAsync(session, appUri, appSource);
        int call = appSource.LastIndexOf("Run", StringComparison.Ordinal);

        JsonElement definition = await RequestAtAsync(session, "textDocument/definition",
            appUri, appSource, call);
        Assert.Equal(contractUri, Assert.Single(definition.EnumerateArray())
            .GetProperty("uri").GetString());
        JsonElement implementation = await RequestAtAsync(session, "textDocument/implementation",
            appUri, appSource, call);
        Assert.Equal(appUri, Assert.Single(implementation.EnumerateArray())
            .GetProperty("uri").GetString());
    }

    private static async Task<LanguageServerSession> InitializeFolderAsync(string path)
    {
        var session = new LanguageServerSession((_, _) => Task.CompletedTask,
            diagnosticDebounce: TimeSpan.Zero);
        await session.HandleRequestAsync("initialize", LspTestProtocol.Json(new
        {
            rootUri = DocumentUri.FromPath(path).AbsoluteUri,
        }), default);
        await session.HandleNotificationAsync("initialized", LspTestProtocol.Json(new { }), default);
        return session;
    }

    private static Task OpenAsync(LanguageServerSession session, string uri, string text,
        int version = 1) => session.HandleNotificationAsync("textDocument/didOpen",
            LspTestProtocol.Json(new
            {
                textDocument = new { uri, version, text },
            }), default);

    private static Task WatchedAsync(LanguageServerSession session,
        params (string Path, int Type)[] changes) => session.HandleNotificationAsync(
            "workspace/didChangeWatchedFiles", LspTestProtocol.Json(new
            {
                changes = changes.Select(change => new
                {
                    uri = DocumentUri.FromPath(change.Path).AbsoluteUri,
                    type = change.Type,
                }).ToArray(),
            }), default);

    private static async Task<JsonElement> DefinitionAsync(LanguageServerSession session,
        string uri, string source, int offset)
        => await RequestAtAsync(session, "textDocument/definition", uri, source, offset);

    private static async Task<JsonElement> RequestAtAsync(LanguageServerSession session,
        string method, string uri, string source, int offset)
    {
        LspPosition position = LspTextCoordinates.ToPosition(SourceText.From(source), offset);
        object? result = await session.HandleRequestAsync(method,
            LspTestProtocol.Json(new { textDocument = new { uri }, position }), default);
        return JsonSerializer.SerializeToElement(result,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    private static string Project(string name, string sourceRoot) => $$"""
        [project]
        name = "{{name}}"
        type = "executable"
        [source]
        root = "{{sourceRoot}}"
        """;
}
