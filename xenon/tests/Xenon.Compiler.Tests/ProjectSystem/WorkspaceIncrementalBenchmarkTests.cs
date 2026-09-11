using System.Diagnostics;
using Xenon.Compiler.Text;
using Xenon.ProjectSystem;
using Xunit;
using Xunit.Abstractions;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class WorkspaceIncrementalBenchmarkTests
{
    private readonly ITestOutputHelper _output;
    public WorkspaceIncrementalBenchmarkTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RequiredIncrementalScenariosReportStableReuseCounters()
    {
        using var directory = new WorkspaceTestDirectory();
        var sources = Enumerable.Range(0, 30).Select(index =>
            ($"file{index}.xe", $"namespace App; int F{index}() {{ return {index}; }}")).ToArray();
        directory.WriteProject("App", sources: sources);
        using Workspace workspace = directory.CreateWorkspace();
        await workspace.CurrentSnapshot.RootProject.GetCompilationAsync();
        await workspace.CurrentSnapshot.GetSymbolIndexAsync();
        DocumentSnapshot first = workspace.CurrentSnapshot.Documents.Single(document =>
            document.PhysicalPath!.EndsWith("file0.xe", StringComparison.Ordinal));

        WorkspaceSnapshot body = Measure("single-project body edit", () => workspace.OpenDocument(first.Id,
            first.EffectiveText.Text.Replace("return 0", "return 100", StringComparison.Ordinal),
            new DocumentVersion(1)));
        Assert.Equal(29, body.Metrics.SyntaxTreesReused);
        Assert.Equal(1, body.Metrics.DocumentsReparsed);

        WorkspaceSnapshot declaration = Measure("single-project declaration edit", () => workspace.OpenDocument(first.Id,
            "namespace App; long Changed(int value) { return value; }", new DocumentVersion(2)));
        Assert.Equal(1, declaration.Metrics.ProjectsInvalidated);
        await declaration.RootProject.GetCompilationAsync();

        WorkspaceSnapshot overlay = Measure("open unsaved overlay with identical text", () =>
            workspace.OpenDocument(first.Id, declaration.GetDocument(first.Id).EffectiveText.Text,
                new DocumentVersion(3)));
        Assert.Same(declaration.GetDocument(first.Id).SyntaxTree, overlay.GetDocument(first.Id).SyntaxTree);
        Assert.Equal(1, overlay.Metrics.CompilationsReused);

        string text = overlay.GetDocument(first.Id).EffectiveText.Text;
        int insertion = text.IndexOf("value", StringComparison.Ordinal);
        WorkspaceSnapshot incremental = Measure("incremental text update", () =>
            workspace.ApplyDocumentChanges(first.Id, new DocumentVersion(3), new DocumentVersion(4),
                [new DocumentTextChange(new TextSpan(insertion, "value".Length), "number")]));
        Assert.Equal(1, incremental.Metrics.DocumentsReparsed);

        Stopwatch indexTimer = Stopwatch.StartNew();
        WorkspaceSymbolIndex index = await incremental.GetSymbolIndexAsync();
        indexTimer.Stop();
        _output.WriteLine($"workspace symbol-index update: {indexTimer.Elapsed.TotalMilliseconds:F3} ms; entries={index.Entries.Length}; rebuilt={incremental.Metrics.SymbolIndexesRebuilt}");
        Assert.Equal(30, index.Entries.Count(entry => entry.Kind == Xenon.Compiler.Semantics.Symbols.SymbolKind.Function));
    }

    [Fact]
    public async Task MultiProjectBodyAndPublicDeclarationScenariosExposePropagationCounters()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library", sources: [("library.xe",
            "namespace Library; public int Value() { return 1; }")]);
        directory.WriteProject("App", "executable", ["../Library/Library.xeproj"], ("main.xe",
            "using Library; namespace App; int Main() { return Value(); }"));
        using Workspace workspace = directory.CreateWorkspace();
        await Task.WhenAll(workspace.CurrentSnapshot.Projects.Select(project => project.GetCompilationAsync()));
        DocumentSnapshot library = workspace.CurrentSnapshot.Projects
            .Single(project => project.Configuration.Name == "Library").Documents.Single();

        WorkspaceSnapshot body = Measure("multi-project leaf body edit", () => workspace.OpenDocument(library.Id,
            "namespace Library; public int Value() { return 2; }", new DocumentVersion(1)));
        Assert.Equal(2, body.Metrics.ProjectsInvalidated);
        Assert.Equal(1, body.Metrics.DocumentsReparsed);

        WorkspaceSnapshot declaration = Measure("multi-project public declaration edit", () =>
            workspace.OpenDocument(library.Id,
                "namespace Library; public long Value() { return 2; }", new DocumentVersion(2)));
        Assert.Equal(2, declaration.Metrics.ProjectsInvalidated);
        Assert.Equal(2, declaration.Metrics.CompilationsRebuilt);
    }

    private WorkspaceSnapshot Measure(string name, Func<WorkspaceSnapshot> action)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Stopwatch timer = Stopwatch.StartNew();
        WorkspaceSnapshot snapshot = action();
        timer.Stop();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"{name}: {timer.Elapsed.TotalMilliseconds:F3} ms; allocated={allocated}; " +
            $"parsed={snapshot.Metrics.DocumentsReparsed}; reusedTrees={snapshot.Metrics.SyntaxTreesReused}; " +
            $"rebuiltCompilations={snapshot.Metrics.CompilationsRebuilt}; reusedCompilations={snapshot.Metrics.CompilationsReused}; " +
            $"invalidatedProjects={snapshot.Metrics.ProjectsInvalidated}");
        return snapshot;
    }
}
