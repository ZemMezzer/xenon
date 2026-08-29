using System.Runtime.CompilerServices;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class CompilationSnapshotTests
{
    [Fact]
    public void TreeOperationsPreservePublishedSnapshotsAndStableSourceIdentity()
    {
        SourceText firstText = SourceText.From("namespace App; int First() { return 1; }", "first.xe");
        Compilation first = Compilation.Create(firstText);
        SyntaxTree firstTree = first.SyntaxTrees[0];
        SyntaxTree replacementInput = SyntaxTree.Parse(
            SourceText.From("namespace App; int Second() { return 2; }", "first.xe"));

        Compilation second = first.ReplaceSyntaxTree(firstTree, replacementInput);
        SyntaxTree secondTree = second.SyntaxTrees[0];
        SyntaxTree addedTree = SyntaxTree.Parse(SourceText.From(
            "namespace App; int Added() { return 3; }", "added.xe"));
        Compilation third = second.AddSyntaxTrees(addedTree);
        Compilation fourth = third.RemoveSyntaxTrees(addedTree);

        Assert.Same(firstTree, first.SyntaxTrees[0]);
        Assert.Equal(firstTree.SourceFileId, secondTree.SourceFileId);
        Assert.NotSame(firstTree, secondTree);
        Assert.Single(first.SyntaxTrees);
        Assert.Single(second.SyntaxTrees);
        Assert.Equal(2, third.SyntaxTrees.Length);
        Assert.Single(fourth.SyntaxTrees);
        Assert.Contains(first.SemanticModel.GlobalNamespace.Namespaces.Single().Functions,
            function => function.Name == "First");
        Assert.Contains(second.SemanticModel.GlobalNamespace.Namespaces.Single().Functions,
            function => function.Name == "Second");
    }

    [Fact]
    public void AddRemoveAndReplaceRejectUnknownOrDuplicateSources()
    {
        SyntaxTree first = SyntaxTree.Parse(SourceText.From("namespace App;", "a.xe"));
        Compilation compilation = Compilation.Create([first]);
        SyntaxTree unknown = SyntaxTree.Parse(SourceText.From("namespace App;", "b.xe"));

        Assert.Throws<ArgumentException>(() => compilation.RemoveSyntaxTrees(unknown));
        Assert.Throws<ArgumentException>(() => compilation.ReplaceSyntaxTree(unknown, first));
        Assert.Throws<ArgumentException>(() => compilation.AddSyntaxTrees(first));
    }

    [Fact]
    public void OptionsReferencesAndTargetSpecializationCreateIndependentSnapshots()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Lib; public int Value() { return 1; }", "lib.xe"));
        var reference = new SourceCompilationReference(library);
        Compilation first = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("using Lib; namespace App; int Main() { return Value(); }", "app.xe"));
        Compilation second = first.WithOptions(new CompilationOptions(CompilationOutputKind.Executable));
        Compilation third = second.RemoveReferences(reference);

        Assert.Single(first.References);
        Assert.Single(second.References);
        Assert.Empty(third.References);
        Assert.Equal(CompilationOutputKind.Library, first.Options.OutputKind);
        Assert.Equal(CompilationOutputKind.Executable, second.Options.OutputKind);
        Assert.False(first.HasErrors);
        Assert.Contains(third.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownFunction);
    }

    [Fact]
    public void SourceReferencePinsExactSnapshotAndKeepsDeclarationLocation()
    {
        SourceText libraryText = SourceText.From(
            "namespace Lib; public int OldValue() { return 1; }", "library.xe");
        Compilation library1 = Compilation.Create(libraryText);
        Compilation app1 = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library1)],
            SourceText.From("using Lib; namespace App; int Main() { return OldValue(); }", "app.xe"));
        Compilation library2 = library1.ReplaceSyntaxTree(library1.SyntaxTrees[0], SyntaxTree.Parse(
            libraryText.WithText("namespace Lib; public int NewValue() { return 2; }")));
        Compilation app2 = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library2)],
            SourceText.From("using Lib; namespace App; int Main() { return NewValue(); }", "app.xe"));

        Assert.False(app1.HasErrors);
        Assert.False(app2.HasErrors);
        FunctionSymbol old = library1.SemanticModel.GlobalNamespace.Namespaces.Single().Functions.Single();
        Assert.Equal("library.xe", old.Locations.Single().Source.Path);
        Assert.Same(library1, ((SourceCompilationReference)app1.References[0]).Compilation);
        Assert.Same(library2, ((SourceCompilationReference)app2.References[0]).Compilation);
    }

    [Fact]
    public void ReferencesExposeTypesAndPublicMembersButPreserveAccessibility()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Lib;
            struct Box {
                private int Secret;
                public int Value;
            }
            """, "library.xe"));
        Compilation valid = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)],
            SourceText.From("using Lib; namespace App; int Read(Box box) { return box.Value; }", "valid.xe"));
        Compilation invalid = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)],
            SourceText.From("using Lib; namespace App; int Read(Box box) { return box.Secret; }", "invalid.xe"));

        Assert.False(valid.HasErrors);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InaccessibleSymbol);
    }

    [Fact]
    public void ConflictingReferenceSymbolsAreAmbiguousRatherThanReferenceOrderDependent()
    {
        Compilation first = Compilation.Create(SourceText.From(
            "namespace Shared; public int Value() { return 1; }", "first.xe"));
        Compilation second = Compilation.Create(SourceText.From(
            "namespace Shared; public int Value() { return 2; }", "second.xe"));
        SourceText app = SourceText.From(
            "using Shared; namespace App; int Main() { return Value(); }", "app.xe");

        Compilation forward = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(first), new SourceCompilationReference(second)], app);
        Compilation reverse = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(second), new SourceCompilationReference(first)], app.WithText(app.Text));

        Assert.Contains(forward.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousName);
        Assert.Contains(reverse.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousName);
    }

    [Fact]
    public async Task SemanticReadsAndModelCacheAreThreadSafeAndSnapshotLocal()
    {
        Compilation compilation = Compilation.Create(SourceText.From(
            "namespace App; struct Box { public int Value; } int Main(Box box) { return box.Value; }", "app.xe"));
        SyntaxTree tree = compilation.SyntaxTrees[0];
        var tasks = Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        {
            var model = compilation.GetSemanticModel(tree);
            Assert.Same(model, compilation.GetSemanticModel(tree));
            Assert.Empty(model.GetDiagnostics());
            Assert.NotEmpty(model.LookupSymbols(tree, tree.Source.Length / 2));
            return model;
        }));

        var models = await Task.WhenAll(tasks);
        Assert.All(models, model => Assert.Same(models[0], model));
        Compilation replacement = compilation.ReplaceSyntaxTree(tree,
            SyntaxTree.Parse(tree.Source.WithText("namespace App; int Main() { return 0; }")));
        Assert.NotSame(models[0], replacement.GetSemanticModel(replacement.SyntaxTrees[0]));
    }

    [Fact]
    public async Task ConcurrentSyntheticMemberReadsPublishOneStableSymbolSet()
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace App; int Main() { return 0; }"));
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        var array = compilation.TypeFactory.ArrayOf(BuiltinTypes.Int);
        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => model.LookupMembers(array))));

        Assert.All(results, members =>
        {
            Assert.Equal(3, members.Length);
            Assert.Same(results[0][0], members[0]);
            Assert.Same(results[0][1], members[1]);
            Assert.Same(results[0][2], members[2]);
        });
    }

    [Fact]
    public void QueryCancellationDoesNotPoisonPublishedSnapshot()
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace App; int Main() { return 0; }"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            compilation.GetSemanticModel(compilation.SyntaxTrees[0], cancellation.Token));
        Assert.NotNull(compilation.GetSemanticModel(compilation.SyntaxTrees[0]));
    }

    [Fact]
    public void TargetSpecializationPreservesOptionsReferencesAndOriginalState()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Lib; public int Value() { return 1; }"));
        var reference = new SourceCompilationReference(library);
        var options = new CompilationOptions(CompilationOutputKind.Library);
        Compilation original = Compilation.Create(options, [reference], SourceText.From(
            "using Lib; namespace App; int Value2() { return Value(); }"));
        var layout = new TestLayout();
        Compilation specialized = original.WithTargetLayout(layout);

        Assert.Null(original.TargetLayout);
        Assert.Same(layout, specialized.TargetLayout);
        Assert.Same(options, specialized.Options);
        Assert.Same(reference, specialized.References.Single());
        Assert.Same(original.SyntaxTrees[0], specialized.SyntaxTrees[0]);
    }

    [Fact]
    public void ObsoleteCompilationGraphIsCollectibleWithoutGlobalCaches()
    {
        WeakReference reference = CreateUnrootedCompilation();
        for (int attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(reference.IsAlive);
    }

    [Fact]
    public void SamePathDoesNotConflateSourcesFromSeparateProjectContexts()
    {
        Compilation first = Compilation.Create(SourceText.From("namespace One;", "src/main.xe"));
        Compilation second = Compilation.Create(SourceText.From("namespace Two;", "src/main.xe"));
        Assert.NotEqual(first.SyntaxTrees[0].SourceFileId, second.SyntaxTrees[0].SourceFileId);
    }

    [Fact]
    public void ObsoleteSourceReferenceGraphIsCollectibleAsAUnit()
    {
        (WeakReference application, WeakReference library) = CreateUnrootedReferenceGraph();
        for (int attempt = 0; attempt < 10 && (application.IsAlive || library.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(application.IsAlive);
        Assert.False(library.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateUnrootedCompilation()
    {
        Compilation compilation = Compilation.Create(SourceText.From(
            "namespace Lifetime; struct Temporary { public int Value; }", "temporary.xe"));
        _ = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        return new WeakReference(compilation);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Application, WeakReference Library) CreateUnrootedReferenceGraph()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Lifetime; public int Value() { return 1; }", "library.xe"));
        Compilation application = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From(
                "using Lifetime; namespace App; int Main() { return Value(); }", "app.xe"));
        _ = application.GetSemanticModel(application.SyntaxTrees[0]);
        return (new WeakReference(application), new WeakReference(library));
    }

    private sealed class TestLayout : ITargetTypeLayout
    {
        public int GetIntegerBitWidth(PrimitiveTypeSymbol type) => type.BitWidth ?? 64;
        public ulong GetSize(TypeSymbol type) => 8;
        public uint GetAlignment(TypeSymbol type) => 8;
        public ulong GetFieldOffset(StructTypeSymbol type, FieldSymbol field) => 0;
    }
}
