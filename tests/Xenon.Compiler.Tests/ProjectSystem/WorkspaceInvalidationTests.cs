using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class WorkspaceInvalidationTests
{
    [Fact]
    public async Task IdenticalOverlayReusesExactCompilationAndSemanticModel()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe",
            "namespace App; int Main() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot old = workspace.CurrentSnapshot;
        DocumentSnapshot document = Assert.Single(old.Documents);
        var compilation = await old.RootProject.GetCompilationAsync();
        var semanticModel = compilation.GetSemanticModel(document.SyntaxTree);

        WorkspaceSnapshot next = workspace.OpenDocument(document.Id, document.EffectiveText.Text,
            new DocumentVersion(1));
        var nextCompilation = await next.RootProject.GetCompilationAsync();

        Assert.Same(document.SyntaxTree, next.GetDocument(document.Id).SyntaxTree);
        Assert.Same(next.GetDocument(document.Id).EffectiveText,
            next.GetDocument(document.Id).SyntaxTree.Source);
        Assert.Same(compilation, nextCompilation);
        Assert.Same(semanticModel,
            nextCompilation.GetSemanticModel(next.GetDocument(document.Id).SyntaxTree));
        Assert.Equal(1, next.Metrics.CompilationsReused);
        Assert.Equal(1, next.Metrics.SemanticModelsReused);
    }

    [Fact]
    public async Task BodyEditReusesUnchangedTreesAndDeclarationIndexButNotCompilation()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources:
        [
            ("a.xe", "namespace App; int Value() { return 1; }"),
            ("b.xe", "namespace App; int Main() { return Value(); }"),
        ]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot s1 = workspace.CurrentSnapshot;
        DocumentSnapshot a1 = s1.Documents.Single(document => document.PhysicalPath!.EndsWith("a.xe"));
        DocumentSnapshot b1 = s1.Documents.Single(document => document.PhysicalPath!.EndsWith("b.xe"));
        var compilation1 = await s1.RootProject.GetCompilationAsync();
        ProjectSymbolIndex symbols1 = await s1.RootProject.GetSymbolIndexAsync();

        WorkspaceSnapshot s2 = workspace.OpenDocument(a1.Id,
            "namespace App; int Value() { return 2; }", new DocumentVersion(1));
        var compilation2 = await s2.RootProject.GetCompilationAsync();
        ProjectSymbolIndex symbols2 = await s2.RootProject.GetSymbolIndexAsync();

        Assert.NotSame(a1.SyntaxTree, s2.GetDocument(a1.Id).SyntaxTree);
        Assert.Same(b1, s2.GetDocument(b1.Id));
        Assert.NotSame(compilation1, compilation2);
        Assert.NotSame(symbols1, symbols2);
        Assert.Equal(1, s2.Metrics.DocumentsReparsed);
        Assert.Equal(1, s2.Metrics.SyntaxTreesReused);
        Assert.Equal(1, s2.Metrics.SymbolIndexDocumentsReused);
    }

    [Theory]
    [InlineData("namespace App; public int Value(int x) { return x; }")]
    [InlineData("namespace App; public long Value() { return 1; }")]
    [InlineData("namespace App; struct Box { public int Field; }")]
    [InlineData("namespace App; struct Base {} struct Box : Base { }")]
    [InlineData("namespace App; const int Answer = 43;")]
    public async Task DeclarationSurfaceEditsInvalidateDeclarationIndex(string replacement)
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe",
            "namespace App; public int Value() { return 1; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        DocumentSnapshot document = Assert.Single(workspace.CurrentSnapshot.Documents);
        ProjectSymbolIndex oldIndex = await workspace.CurrentSnapshot.RootProject.GetSymbolIndexAsync();

        WorkspaceSnapshot changed = workspace.OpenDocument(document.Id, replacement, new DocumentVersion(1));
        ProjectSymbolIndex newIndex = await changed.RootProject.GetSymbolIndexAsync();

        Assert.NotSame(oldIndex, newIndex);
        Assert.Equal(1, changed.Metrics.SymbolIndexesRebuilt);
    }

    [Fact]
    public async Task DirectTransitiveAndDiamondDependantsPinNewGenerationsWhileOldGraphSurvives()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Core", "static-library", sources: [("core.xe",
            "namespace Core; public int Value() { return 1; }")]);
        directory.WriteProject("Left", "static-library", ["../Core/Core.xeproj"], ("left.xe",
            "using Core; namespace Left; public int LeftValue() { return Value(); }"));
        directory.WriteProject("Right", "static-library", ["../Core/Core.xeproj"], ("right.xe",
            "using Core; namespace Right; public int RightValue() { return Value(); }"));
        directory.WriteProject("App", "executable",
            ["../Left/Left.xeproj", "../Right/Right.xeproj"], ("main.xe",
            "using Left; using Right; namespace App; int Main() { return LeftValue() + RightValue(); }"));
        directory.WriteProject("Unrelated", "static-library", sources: [("other.xe",
            "namespace Other; public int OtherValue() { return 9; }")]);
        XenonProjectGraph appGraph = XenonProjectGraph.Load(directory.PathOf("App/App.xeproj"));
        XenonProject unrelated = XenonProjectLoader.LoadProjectFile(directory.PathOf("Unrelated/Unrelated.xeproj"));
        XenonProjectGraph graph = XenonProjectGraph.Create(appGraph.Root, appGraph.Projects.Add(unrelated));
        using Workspace workspace = Workspace.Create(graph);
        WorkspaceSnapshot s1 = workspace.CurrentSnapshot;
        ProjectSnapshot core1 = Find(s1, "Core");
        ProjectSnapshot left1 = Find(s1, "Left");
        ProjectSnapshot right1 = Find(s1, "Right");
        ProjectSnapshot app1 = Find(s1, "App");
        ProjectSnapshot unrelated1 = Find(s1, "Unrelated");
        await Task.WhenAll(s1.Projects.Select(project => project.GetCompilationAsync()));
        DocumentSnapshot coreDocument = Assert.Single(core1.Documents);

        WorkspaceSnapshot s2 = workspace.OpenDocument(coreDocument.Id,
            "namespace Core; public int Value() { return 2; }", new DocumentVersion(1));
        ProjectSnapshot core2 = Find(s2, "Core");
        ProjectSnapshot left2 = Find(s2, "Left");
        ProjectSnapshot right2 = Find(s2, "Right");
        ProjectSnapshot app2 = Find(s2, "App");

        Assert.NotSame(core1, core2);
        Assert.NotSame(left1, left2);
        Assert.NotSame(right1, right2);
        Assert.NotSame(app1, app2);
        Assert.Same(unrelated1, Find(s2, "Unrelated"));
        Assert.Same(core1, Assert.Single(left1.ProjectReferences));
        Assert.Same(core2, Assert.Single(left2.ProjectReferences));
        Assert.Contains(left2, app2.ProjectReferences);
        Assert.Contains(right2, app2.ProjectReferences);
        Assert.False((await app1.GetCompilationAsync()).HasErrors);
        Assert.False((await app2.GetCompilationAsync()).HasErrors);
        Assert.Equal(4, s2.Metrics.ProjectsInvalidated);
        Assert.Equal(1, s2.Metrics.ProjectsReused);
    }

    [Fact]
    public async Task DeclarationChangeInReferencedProjectUpdatesDependentDiagnostics()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library", sources: [("library.xe",
            "namespace Library; public int Value() { return 1; }")]);
        directory.WriteProject("App", "executable", ["../Library/Library.xeproj"], ("main.xe",
            "using Library; namespace App; int Main() { return Value(); }"));
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot s1 = workspace.CurrentSnapshot;
        ProjectSnapshot library1 = Find(s1, "Library");
        var oldAppCompilation = await Find(s1, "App").GetCompilationAsync();
        Assert.False(oldAppCompilation.HasErrors);

        WorkspaceSnapshot s2 = workspace.OpenDocument(Assert.Single(library1.Documents).Id,
            "namespace Library; public int Renamed() { return 1; }", new DocumentVersion(1));
        var newAppCompilation = await Find(s2, "App").GetCompilationAsync();

        Assert.True(newAppCompilation.HasErrors);
        Assert.False(oldAppCompilation.HasErrors);
        Assert.NotSame(oldAppCompilation, newAppCompilation);
    }

    private static ProjectSnapshot Find(WorkspaceSnapshot snapshot, string name) =>
        snapshot.Projects.Single(project => project.Configuration.Name == name);
}
