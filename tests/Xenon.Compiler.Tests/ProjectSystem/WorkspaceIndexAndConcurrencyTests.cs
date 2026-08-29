using Xenon.Compiler.Semantics.Symbols;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class WorkspaceIndexAndConcurrencyTests
{
    [Fact]
    public async Task SymbolIndexesSearchAcrossProjectsAndRemainSnapshotIsolated()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library", sources: [("library.xe",
            "namespace Library; public int Shared() { return 1; }")]);
        directory.WriteProject("App", "executable", ["../Library/Library.xeproj"], ("main.xe",
            "using Library; namespace App; int Shared() { return Library.Shared(); }"));
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot s1 = workspace.CurrentSnapshot;
        WorkspaceSymbolIndex oldIndex = await s1.GetSymbolIndexAsync();

        Assert.Equal(2, oldIndex.Search(name: "Shared", kind: SymbolKind.Function).Length);
        SymbolIndexEntry librarySymbol = oldIndex.Search(qualifiedName: "Library.Shared").Single();
        Assert.Equal("Library", s1.GetProject(librarySymbol.Id.ProjectId).Configuration.Name);
        Assert.Equal(librarySymbol.Id.DocumentId, librarySymbol.Declaration.DocumentId);

        ProjectSnapshot library = s1.Projects.Single(project => project.Configuration.Name == "Library");
        WorkspaceSnapshot s2 = workspace.OpenDocument(Assert.Single(library.Documents).Id,
            "namespace Library; public int Renamed() { return 1; }", new DocumentVersion(1));
        WorkspaceSymbolIndex newIndex = await s2.GetSymbolIndexAsync();

        Assert.Single(oldIndex.Search(qualifiedName: "Library.Shared"));
        Assert.Empty(newIndex.Search(qualifiedName: "Library.Shared"));
        Assert.Single(newIndex.Search(qualifiedName: "Library.Renamed"));
    }

    [Fact]
    public async Task ReferenceIndexUsesResolvedIdentityForLocalMemberCrossFileAndCrossProjectReferences()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library", sources: [("library.xe", """
            namespace Library;
            struct Box { public int Value; }
            public int Get() { return 1; }
            """)]);
        directory.WriteProject("App", "executable", ["../Library/Library.xeproj"],
            ("helper.xe", "namespace App; int Local() { return 2; }"),
            ("main.xe", """
                using Library;
                namespace App;
                int Main(Box box) { return Get() + Local() + box.Value + Missing(); }
                """));
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot snapshot = workspace.CurrentSnapshot;
        WorkspaceSymbolIndex symbols = await snapshot.GetSymbolIndexAsync();
        WorkspaceReferenceIndex references = await snapshot.GetReferenceIndexAsync();

        foreach (string qualifiedName in new[] { "Library.Get", "App.Local", "Library.Box.Value" })
        {
            SymbolIndexEntry symbol = symbols.Search(qualifiedName: qualifiedName).Single();
            Assert.NotEmpty(references.FindReferences(symbol.Id));
        }
        Assert.DoesNotContain(references.Entries,
            entry => entry.Target.QualifiedName.EndsWith("Missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReferenceContributionsAreReplacedForEditedAndRemovedDocuments()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources:
        [
            ("values.xe", "namespace App; int First() { return 1; } int Second() { return 2; }"),
            ("main.xe", "namespace App; int Main() { return First(); }"),
        ]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot s1 = workspace.CurrentSnapshot;
        WorkspaceSymbolIndex symbols1 = await s1.GetSymbolIndexAsync();
        WorkspaceReferenceIndex references1 = await s1.GetReferenceIndexAsync();
        DocumentSnapshot main = s1.Documents.Single(document => document.PhysicalPath!.EndsWith("main.xe"));
        WorkspaceSymbolId first = symbols1.Search(qualifiedName: "App.First").Single().Id;
        WorkspaceSymbolId second = symbols1.Search(qualifiedName: "App.Second").Single().Id;
        Assert.Single(references1.FindReferences(first));
        Assert.Empty(references1.FindReferences(second));

        WorkspaceSnapshot s2 = workspace.OpenDocument(main.Id,
            "namespace App; int Main() { return Second(); }", new DocumentVersion(1));
        WorkspaceReferenceIndex references2 = await s2.GetReferenceIndexAsync();
        Assert.Single(references1.FindReferences(first));
        Assert.Empty(references2.FindReferences(first));
        Assert.Single(references2.FindReferences(second));

        WorkspaceSnapshot s3 = workspace.RemoveDocument(main.Id);
        WorkspaceReferenceIndex references3 = await s3.GetReferenceIndexAsync();
        Assert.DoesNotContain(references3.Entries, entry => entry.Source.DocumentId == main.Id);
    }

    [Fact]
    public async Task ConcurrentAnalysisPublishesOneStableCachePerSnapshot()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources:
        [
            ("a.xe", "namespace App; struct Box { public int Value; }"),
            ("b.xe", "namespace App; int Read(Box box) { return box.Value; }"),
        ]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot snapshot = workspace.CurrentSnapshot;
        var tasks = Enumerable.Range(0, 64)
            .Select(async _ =>
            {
                var compilation = await snapshot.RootProject.GetCompilationAsync();
                var symbols = await snapshot.GetSymbolIndexAsync();
                var references = await snapshot.GetReferenceIndexAsync();
                Assert.False(compilation.HasErrors);
                return (compilation, symbols, references);
            }).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, result =>
        {
            Assert.Same(results[0].compilation, result.compilation);
            Assert.Same(results[0].symbols, result.symbols);
            Assert.Same(results[0].references, result.references);
        });
    }

    [Fact]
    public async Task OldAndNewSnapshotsCanBeAnalyzedConcurrentlyWithoutCrossGenerationResults()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe",
            "namespace App; int Old() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot old = workspace.CurrentSnapshot;
        DocumentSnapshot document = Assert.Single(old.Documents);
        WorkspaceSnapshot next = workspace.OpenDocument(document.Id,
            "namespace App; int New() { return 1; }", new DocumentVersion(1));

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(async index =>
        {
            WorkspaceSnapshot snapshot = index % 2 == 0 ? old : next;
            var compilation = await snapshot.RootProject.GetCompilationAsync();
            string name = compilation.SemanticModel.GlobalNamespace.Namespaces.Single()
                .Functions.Single().Name;
            return (snapshot.Generation, name, compilation);
        }));

        Assert.All(results.Where(result => result.Generation == old.Generation),
            result => Assert.Equal("Old", result.name));
        Assert.All(results.Where(result => result.Generation == next.Generation),
            result => Assert.Equal("New", result.name));
        Assert.NotSame(results.First(result => result.Generation == old.Generation).compilation,
            results.First(result => result.Generation == next.Generation).compilation);
    }

    [Fact]
    public async Task NewGenerationCancelsOnlyStaleSensitiveRequests()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "namespace App; int Main() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        DocumentSnapshot document = Assert.Single(workspace.CurrentSnapshot.Documents);
        using WorkspaceAnalysisRequest stale = workspace.CreateAnalysisRequest(staleSensitive: true);
        using WorkspaceAnalysisRequest durable = workspace.CreateAnalysisRequest(staleSensitive: false);

        WorkspaceSnapshot next = workspace.OpenDocument(document.Id,
            "namespace App; int Main() { return 1; }", new DocumentVersion(1));

        Assert.True(stale.CancellationToken.IsCancellationRequested);
        Assert.False(durable.CancellationToken.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stale.Snapshot.RootProject.GetCompilationAsync(stale.CancellationToken));
        Assert.False((await next.RootProject.GetCompilationAsync()).HasErrors);
        Assert.False((await durable.Snapshot.RootProject.GetCompilationAsync()).HasErrors);
    }

    [Fact]
    public void CanceledIncrementalUpdateDoesNotPublishPartialState()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "namespace App; int Main() { return 0; }")]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot before = workspace.CurrentSnapshot;
        DocumentSnapshot document = Assert.Single(before.Documents);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => workspace.OpenDocument(document.Id,
            "namespace App; int Main() { return 1; }", new DocumentVersion(1), cancellation.Token));

        Assert.Same(before, workspace.CurrentSnapshot);
        Assert.Equal(DocumentVersion.Initial, workspace.CurrentSnapshot.GetDocument(document.Id).Version);
    }

    [Fact]
    public async Task BodyEditThatShiftsDeclarationPreservesStableIdAndCrossFileReferences()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources:
        [
            ("values.xe", """
                namespace App;
                int First() { return 1; }
                int Target() { return 2; }
                """),
            ("main.xe", "namespace App; int Main() { return Target(); }"),
        ]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot before = workspace.CurrentSnapshot;
        SymbolIndexEntry oldTarget = (await before.GetSymbolIndexAsync())
            .Search(qualifiedName: "App.Target").Single();
        Assert.Single((await before.GetReferenceIndexAsync()).FindReferences(oldTarget.Id));
        DocumentSnapshot values = before.Documents.Single(document =>
            document.PhysicalPath!.EndsWith("values.xe", StringComparison.Ordinal));

        WorkspaceSnapshot after = workspace.OpenDocument(values.Id, """
            namespace App;
            int First()
            {
                int value = 100;
                return value;
            }
            int Target() { return 2; }
            """, new DocumentVersion(1));
        SymbolIndexEntry newTarget = (await after.GetSymbolIndexAsync())
            .Search(qualifiedName: "App.Target").Single();
        WorkspaceReferenceIndex references = await after.GetReferenceIndexAsync();

        Assert.Equal(oldTarget.Id, newTarget.Id);
        Assert.NotEqual(oldTarget.Declaration.Span, newTarget.Declaration.Span);
        ReferenceIndexEntry reference = Assert.Single(references.FindReferences(newTarget.Id));
        Assert.EndsWith("main.xe", reference.Source.Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DependencyBodyEditThatShiftsDeclarationPreservesCrossProjectReferences()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library", sources: [("library.xe", """
            namespace Library;
            public int First() { return 1; }
            public int Target() { return 2; }
            """)]);
        directory.WriteProject("App", "executable", ["../Library/Library.xeproj"],
            ("main.xe", "using Library; namespace App; int Main() { return Target(); }"));
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot before = workspace.CurrentSnapshot;
        SymbolIndexEntry oldTarget = (await before.GetSymbolIndexAsync())
            .Search(qualifiedName: "Library.Target").Single();
        Assert.Single((await before.GetReferenceIndexAsync()).FindReferences(oldTarget.Id));
        ProjectSnapshot library = before.Projects.Single(project =>
            project.Configuration.Name == "Library");

        WorkspaceSnapshot after = workspace.OpenDocument(Assert.Single(library.Documents).Id, """
            namespace Library;
            public int First()
            {
                int value = 100;
                return value;
            }
            public int Target() { return 2; }
            """, new DocumentVersion(1));
        SymbolIndexEntry newTarget = (await after.GetSymbolIndexAsync())
            .Search(qualifiedName: "Library.Target").Single();
        ReferenceIndexEntry reference = Assert.Single(
            (await after.GetReferenceIndexAsync()).FindReferences(newTarget.Id));

        Assert.Equal(oldTarget.Id, newTarget.Id);
        Assert.NotEqual(oldTarget.Declaration.Span, newTarget.Declaration.Span);
        Assert.Equal("App", after.GetProject(reference.Source.ProjectId).Configuration.Name);
    }

    [Fact]
    public async Task DeclarationSignatureChangeCreatesNewIdAndDoesNotKeepStaleReferences()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources:
        [
            ("values.xe", "namespace App; int Target() { return 2; }"),
            ("main.xe", "namespace App; int Main() { return Target(); }"),
        ]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot before = workspace.CurrentSnapshot;
        WorkspaceSymbolId oldId = (await before.GetSymbolIndexAsync())
            .Search(qualifiedName: "App.Target").Single().Id;
        DocumentSnapshot values = before.Documents.Single(document =>
            document.PhysicalPath!.EndsWith("values.xe", StringComparison.Ordinal));

        WorkspaceSnapshot after = workspace.OpenDocument(values.Id,
            "namespace App; int Target(int value) { return value; }", new DocumentVersion(1));
        WorkspaceSymbolId newId = (await after.GetSymbolIndexAsync())
            .Search(qualifiedName: "App.Target").Single().Id;
        WorkspaceReferenceIndex references = await after.GetReferenceIndexAsync();

        Assert.NotEqual(oldId, newId);
        Assert.Empty(references.FindReferences(oldId));
        Assert.Single(references.FindReferences(newId));
    }

    [Fact]
    public async Task RemovedDeclarationReaddedWithNewSignatureGetsNewSemanticIdentity()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources:
        [
            ("values.xe", "namespace App; int Target() { return 2; }"),
            ("main.xe", "namespace App; int Main() { return Target(); }"),
        ]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot initial = workspace.CurrentSnapshot;
        WorkspaceSymbolId originalId = (await initial.GetSymbolIndexAsync())
            .Search(qualifiedName: "App.Target").Single().Id;
        DocumentSnapshot values = initial.Documents.Single(document =>
            document.PhysicalPath!.EndsWith("values.xe", StringComparison.Ordinal));

        WorkspaceSnapshot removed = workspace.OpenDocument(values.Id,
            "namespace App; int Other() { return 0; }", new DocumentVersion(1));
        Assert.Empty((await removed.GetSymbolIndexAsync()).Search(qualifiedName: "App.Target"));
        WorkspaceSnapshot readded = workspace.OpenDocument(values.Id,
            "namespace App; int Target(int seed) { return seed; }", new DocumentVersion(2));
        WorkspaceSymbolId replacementId = (await readded.GetSymbolIndexAsync())
            .Search(qualifiedName: "App.Target").Single().Id;

        Assert.NotEqual(originalId, replacementId);
        Assert.Empty((await readded.GetReferenceIndexAsync()).FindReferences(originalId));
    }

    [Fact]
    public async Task ShadowedLocalsHaveDistinctIdsAndExactReferenceOwnership()
    {
        using var directory = new WorkspaceTestDirectory();
        const string text = """
            namespace App;
            int Test()
            {
                int value = 1;
                value = value + 1;
                {
                    int value = 2;
                    value = value + 1;
                }
                return 0;
            }
            """;
        directory.WriteProject("App", sources: [("main.xe", text)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceReferenceIndex index = await workspace.CurrentSnapshot.GetReferenceIndexAsync();
        var groups = index.Entries
            .Where(entry => entry.Target.Kind == SymbolKind.LocalVariable &&
                entry.Target.QualifiedName.EndsWith(".value", StringComparison.Ordinal))
            .GroupBy(entry => entry.Target).OrderBy(group => group.Min(entry => entry.Source.Span.Start))
            .ToArray();

        Assert.Equal(2, groups.Length);
        Assert.NotEqual(groups[0].Key, groups[1].Key);
        int innerDeclaration = text.LastIndexOf("int value", StringComparison.Ordinal);
        Assert.All(index.FindReferences(groups[0].Key),
            reference => Assert.True(reference.Source.Span.Start < innerDeclaration));
        Assert.All(index.FindReferences(groups[1].Key),
            reference => Assert.True(reference.Source.Span.Start > innerDeclaration));
        Assert.Equal(2, index.FindReferences(groups[0].Key).Length);
        Assert.Equal(2, index.FindReferences(groups[1].Key).Length);
    }

    [Fact]
    public async Task SameNamedLocalsInSiblingScopesRemainSeparate()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", """
            namespace App;
            int Test()
            {
                { int value = 1; value = value + 1; }
                { int value = 2; value = value + 1; }
                return 0;
            }
            """)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceReferenceIndex index = await workspace.CurrentSnapshot.GetReferenceIndexAsync();
        var targets = index.Entries.Where(entry =>
                entry.Target.Kind == SymbolKind.LocalVariable &&
                entry.Target.QualifiedName.EndsWith(".value", StringComparison.Ordinal))
            .Select(entry => entry.Target).Distinct().ToArray();

        Assert.Equal(2, targets.Length);
        Assert.All(targets, target => Assert.Equal(2, index.FindReferences(target).Length));
    }

    [Fact]
    public async Task LocalIdSurvivesNonDeclarationStatementInsertedBeforeIt()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", """
            namespace App;
            int Test()
            {
                int value = 1;
                return value;
            }
            """)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot before = workspace.CurrentSnapshot;
        WorkspaceSymbolId oldId = (await before.GetReferenceIndexAsync()).Entries
            .Single(entry => entry.Target.Kind == SymbolKind.LocalVariable).Target;
        DocumentSnapshot document = Assert.Single(before.Documents);

        WorkspaceSnapshot after = workspace.OpenDocument(document.Id, """
            namespace App;
            int Test()
            {
                if (true) { }
                int value = 1;
                return value;
            }
            """, new DocumentVersion(1));
        WorkspaceSymbolId newId = (await after.GetReferenceIndexAsync()).Entries
            .Single(entry => entry.Target.Kind == SymbolKind.LocalVariable).Target;

        Assert.Equal(oldId, newId);
    }

    [Fact]
    public async Task ParameterIdentityIncludesCallableAndOrdinal()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", """
            namespace App;
            int First(int value) { return value; }
            int Second(int value) { return value; }
            int Pair(int left, int right) { return left + right; }
            """)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceReferenceIndex index = await workspace.CurrentSnapshot.GetReferenceIndexAsync();
        WorkspaceSymbolId[] parameters = index.Entries
            .Where(entry => entry.Target.Kind == SymbolKind.Parameter)
            .Select(entry => entry.Target).Distinct().ToArray();

        Assert.Equal(4, parameters.Length);
        Assert.Equal(2, parameters.Count(parameter =>
            parameter.QualifiedName.EndsWith(".value", StringComparison.Ordinal)));
        Assert.Equal(4, parameters.Select(parameter => parameter.DeclarationIdentity).Distinct().Count());
        Assert.All(parameters, parameter => Assert.Single(index.FindReferences(parameter)));
    }

    [Fact]
    public async Task ConstructorParameterIdentityIncludesFullCallableSignature()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", """
            namespace App;
            struct Box
            {
                public Box(int value) { value = value; }
                public Box(int value, bool flag) { value = value; flag = flag; }
            }
            """)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceReferenceIndex index = await workspace.CurrentSnapshot.GetReferenceIndexAsync();
        WorkspaceSymbolId[] values = index.Entries.Where(entry =>
                entry.Target.Kind == SymbolKind.Parameter &&
                entry.Target.QualifiedName.EndsWith(".value", StringComparison.Ordinal))
            .Select(entry => entry.Target).Distinct().ToArray();

        Assert.Equal(2, values.Length);
        Assert.NotEqual(values[0].DeclarationIdentity, values[1].DeclarationIdentity);
        Assert.All(values, value => Assert.Equal(2, index.FindReferences(value).Length));
    }
}
