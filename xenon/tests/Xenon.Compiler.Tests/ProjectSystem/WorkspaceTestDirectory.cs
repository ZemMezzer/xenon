using System.Collections.Immutable;
using Xenon.ProjectSystem;

namespace Xenon.Compiler.Tests.ProjectSystem;

internal sealed class WorkspaceTestDirectory : IDisposable
{
    public WorkspaceTestDirectory()
    {
        Root = Path.Combine(Path.GetTempPath(), "xenon-workspace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public string PathOf(string relativePath) => Path.Combine(Root,
        relativePath.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relativePath, string content)
    {
        string path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void WriteProject(string name, string type = "executable",
        string[]? references = null, params (string Path, string Text)[] sources)
    {
        references ??= [];
        string referencesText = references.Length == 0 ? string.Empty : $"""

            [references]
            projects = [{string.Join(", ", references.Select(reference => $"\"{reference}\""))}]
            """;
        Write($"{name}/{name}.xeproj", $"""
            [project]
            name = "{name}"
            type = "{type}"

            [source]
            root = "src"
            {referencesText}
            """);
        if (sources.Length == 0) sources = [("main.xe", $"namespace {name}; int Value() {{ return 1; }}")];
        foreach ((string path, string text) in sources) Write($"{name}/src/{path}", text);
    }

    public Workspace CreateWorkspace(string rootProject = "App") =>
        Workspace.Create(PathOf($"{rootProject}/{rootProject}.xeproj"));

    public Workspace CreateWorkspace(IWorkspaceFileSystem fileSystem,
        IWorkspaceSaveObserver? saveObserver = null, string rootProject = "App") =>
        Workspace.Create(XenonProjectGraph.Load(PathOf($"{rootProject}/{rootProject}.xeproj")),
            fileSystem, saveObserver);

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
