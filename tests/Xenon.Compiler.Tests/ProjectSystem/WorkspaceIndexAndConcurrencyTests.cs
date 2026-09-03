using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Semantics;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class WorkspaceIndexAndConcurrencyTests
{
    [Fact]
    public async Task MemberRelationshipIndexBuildsTransitiveOverrideAndInterfaceFamilies()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", """
            namespace App;
            interface IUpdatable { void Update(); }
            struct Base { public virtual void Update() {} }
            struct Child : Base, IUpdatable { public override void Update() {} }
            struct GrandChild : Child { public override void Update() {} }
            struct Second : IUpdatable { public void Update() {} }
            struct Other { public void Update() {} }
            """)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSymbolIndex symbols = await workspace.CurrentSnapshot.GetSymbolIndexAsync();
        WorkspaceMemberRelationshipIndex relationships = await workspace.CurrentSnapshot
            .GetMemberRelationshipIndexAsync();
        WorkspaceSymbolId baseMethod = symbols.Search(qualifiedName: "App.Base.Update").Single().Id;
        string[] family = relationships.GetFamily(baseMethod).Select(id =>
            symbols.Entries.Single(entry => entry.Id == id).QualifiedName).ToArray();

        Assert.Equal(new[]
        {
            "App.IUpdatable.Update", "App.Base.Update", "App.Child.Update",
            "App.GrandChild.Update", "App.Second.Update",
        }.OrderBy(name => name, StringComparer.Ordinal), family.OrderBy(name => name, StringComparer.Ordinal));
        Assert.DoesNotContain("App.Other.Update", family);
        Assert.Contains(relationships.Entries, entry => entry.Kind == MemberRelationshipKind.Override);
        Assert.Contains(relationships.Entries,
            entry => entry.Kind == MemberRelationshipKind.InterfaceImplementation);
    }

    [Fact]
    public async Task MemberRelationshipIndexConnectsCrossProjectContractAndOverride()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Core", "static-library", sources: [("core.xe", """
            namespace Core;
            interface IUpdatable { void Update(); }
            struct Base : IUpdatable { public virtual void Update() {} }
            """)]);
        directory.WriteProject("Game", "executable", ["../Core/Core.xeproj"], ("main.xe", """
            using Core;
            namespace Game;
            struct Player : Base { public override void Update() {} }
            """));
        using Workspace workspace = directory.CreateWorkspace("Game");
        WorkspaceSymbolIndex symbols = await workspace.CurrentSnapshot.GetSymbolIndexAsync();
        WorkspaceMemberRelationshipIndex relationships = await workspace.CurrentSnapshot
            .GetMemberRelationshipIndexAsync();
        WorkspaceSymbolId implementation = symbols.Search(qualifiedName: "Game.Player.Update").Single().Id;
        string[] family = relationships.GetFamily(implementation).Select(id =>
            symbols.Entries.Single(entry => entry.Id == id).QualifiedName).ToArray();

        Assert.Contains("Core.IUpdatable.Update", family);
        Assert.Contains("Core.Base.Update", family);
        Assert.Contains("Game.Player.Update", family);
    }

    [Fact]
    public async Task SharedPhysicalReferencesRemainDuplicateSemanticallyButDeclarationsAreConservative()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Core", "static-library", sources: [("core.xe",
            "namespace Core; public void Target() {}")]);
        directory.Write("Shared/shared.xe", "namespace Shared; void Use() { Core.Target(); }");
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
        directory.Write("Both.xws", """
            [workspace]
            projects = ["Core/Core.xeproj", "B/B.xeproj", "C/C.xeproj"]
            """);
        using Workspace workspace = Workspace.Create(directory.PathOf("Both.xws"));
        WorkspaceSymbolIndex symbols = await workspace.CurrentSnapshot.GetSymbolIndexAsync();
        WorkspaceReferenceIndex references = await workspace.CurrentSnapshot.GetReferenceIndexAsync();
        WorkspaceSymbolId target = symbols.Search(qualifiedName: "Core.Target").Single().Id;
        ReferenceIndexEntry[] physicalReferences = references.FindReferences(target).ToArray();

        Assert.Equal(2, physicalReferences.Length);
        Assert.Single(physicalReferences.Select(reference =>
            (Path.GetFullPath(reference.Source.Path), reference.Source.Span)).Distinct());
        SymbolIndexEntry shared = symbols.Search(qualifiedName: "Shared.Use").First();
        SharedPhysicalDeclarationGroup group = symbols.GetSharedPhysicalDeclarationGroup(shared.Id);
        Assert.Equal(2, group.Entries.Length);
        Assert.False(group.IsCompatible);
    }

    [Fact]
    public async Task SymbolIndexPublishesExactEditorKindsOwnershipAndNoCompilerGeneratedSymbols()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", """
            namespace App;
            enum State { Ready }
            interface IService { void InterfaceMethod(); }
            const int Global = 1;
            int FreeFunction() { return Global; }
            struct Widget
            {
                public int Field;
                public int Property { get { return Field; } }
                public int this[int index] { get { return index; } }
                const int MemberConstant = 2;
                public Widget() {}
                ~Widget() {}
                public void Method() {}
            }
            """)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSymbolIndex index = await workspace.CurrentSnapshot.GetSymbolIndexAsync();

        Assert.Equal(EditorSymbolKind.Namespace,
            index.Search(qualifiedName: "App").Single().EditorKind);
        Assert.Equal(EditorSymbolKind.Enum,
            index.Search(qualifiedName: "App.State").Single().EditorKind);
        Assert.Equal(EditorSymbolKind.EnumMember,
            index.Search(qualifiedName: "App.State.Ready").Single().EditorKind);
        Assert.Equal(EditorSymbolKind.Interface,
            index.Search(qualifiedName: "App.IService").Single().EditorKind);
        Assert.Equal(EditorSymbolKind.Constant,
            index.Search(qualifiedName: "App.Global").Single().EditorKind);
        Assert.Equal(EditorSymbolKind.Function,
            index.Search(qualifiedName: "App.FreeFunction").Single().EditorKind);
        SymbolIndexEntry widget = index.Search(qualifiedName: "App.Widget").Single();
        Assert.Equal(EditorSymbolKind.Struct, widget.EditorKind);
        Assert.Equal(EditorSymbolKind.Field,
            index.Search(qualifiedName: "App.Widget.Field").Single().EditorKind);
        Assert.Equal(EditorSymbolKind.Property,
            index.Search(qualifiedName: "App.Widget.Property").Single().EditorKind);
        Assert.Equal(EditorSymbolKind.Indexer,
            index.Search(qualifiedName: "App.Widget.this").Single().EditorKind);
        Assert.Equal(EditorSymbolKind.Constant,
            index.Search(qualifiedName: "App.Widget.MemberConstant").Single().EditorKind);
        SymbolIndexEntry constructor = index.Entries.Single(entry =>
            entry.FunctionKind == FunctionKind.Constructor);
        Assert.Equal(EditorSymbolKind.Constructor, constructor.EditorKind);
        Assert.Equal(widget.Id, constructor.ContainingSymbolId);
        Assert.Equal(EditorSymbolKind.Destructor, index.Entries.Single(entry =>
            entry.FunctionKind == FunctionKind.Destructor).EditorKind);
        Assert.Equal(EditorSymbolKind.Method,
            index.Search(qualifiedName: "App.Widget.Method").Single().EditorKind);
        Assert.DoesNotContain(index.Entries, entry => entry.Name == "__init_fields");
    }

    [Fact]
    public async Task ConstructionSitesKeepConstructorIdentityAndAlsoCountAsTypeReferences()
    {
        const string source = """
            namespace App;
            struct Player
            {
                public Player() {}
                ~Player() {}
            }
            void Test(Player value, Player* pointer)
            {
                Player created = Player();
                Player* heap = new Player();
            }
            """;
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", source)]);
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSymbolIndex symbols = await workspace.CurrentSnapshot.GetSymbolIndexAsync();
        WorkspaceReferenceIndex references = await workspace.CurrentSnapshot.GetReferenceIndexAsync();
        SymbolIndexEntry type = symbols.Search(qualifiedName: "App.Player").Single();
        SymbolIndexEntry constructor = symbols.Entries.Single(entry =>
            entry.FunctionKind == FunctionKind.Constructor);

        Assert.Equal(type.Id, constructor.ContainingSymbolId);
        ReferenceIndexEntry[] typeReferences = references.FindReferences(type.Id).ToArray();
        ReferenceIndexEntry[] constructorReferences = references.FindReferences(constructor.Id).ToArray();
        int call = source.IndexOf("Player();", StringComparison.Ordinal);
        int allocation = source.LastIndexOf("Player();", StringComparison.Ordinal);
        Assert.Contains(typeReferences, reference => reference.Source.Span.Start == call &&
            reference.Kind == Xenon.Compiler.Semantics.ResolvedReferenceKind.Type);
        Assert.Contains(typeReferences, reference => reference.Source.Span.Start == allocation &&
            reference.Kind == Xenon.Compiler.Semantics.ResolvedReferenceKind.Type);
        Assert.Contains(constructorReferences, reference => reference.Source.Span.Start == call);
        Assert.Contains(constructorReferences, reference => reference.Source.Span.Start == allocation);
        Assert.True(typeReferences.Length >= 6);
        Assert.Equal(2, constructorReferences.Length);
    }

    [Fact]
    public async Task TypeRelationshipIndexFindsCrossProjectDirectAndTransitiveImplementations()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Library", "static-library", sources: [("types.xe", """
            namespace Library;
            interface IEntity {}
            struct Entity {}
            """)]);
        directory.WriteProject("App", "executable", ["../Library/Library.xeproj"], ("main.xe", """
            using Library;
            namespace App;
            struct Player : Entity, IEntity {}
            struct AdvancedPlayer : Player {}
            """));
        using Workspace workspace = directory.CreateWorkspace();
        WorkspaceSnapshot snapshot = workspace.CurrentSnapshot;
        WorkspaceSymbolIndex symbols = await snapshot.GetSymbolIndexAsync();
        WorkspaceTypeRelationshipIndex relationships = await snapshot.GetTypeRelationshipIndexAsync();

        WorkspaceSymbolId entity = symbols.Search(qualifiedName: "Library.Entity").Single().Id;
        WorkspaceSymbolId contract = symbols.Search(qualifiedName: "Library.IEntity").Single().Id;
        Assert.Equal("App.Player", Assert.Single(relationships.FindDirect(entity)).DerivedType.QualifiedName);
        Assert.Equal(new[] { "App.Player", "App.AdvancedPlayer" },
            relationships.FindTransitive(contract).Select(item => item.DerivedType.QualifiedName).ToArray());
    }

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
