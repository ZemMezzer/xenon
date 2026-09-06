using System.Runtime.CompilerServices;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
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
    public void CompilationImportsSemanticSurfaceFromAnyReferenceImplementation()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Lib; public int Value() { return 1; }", "library.xe"));
        Compilation app = Compilation.Create(new CompilationOptions(),
            [new TestSemanticReference(library.SemanticModel.GlobalNamespace)],
            SourceText.From("using Lib; namespace App; int Main() { return Value(); }", "app.xe"));

        Assert.False(app.HasErrors);
        Assert.IsNotType<SourceCompilationReference>(app.References.Single());
    }

    [Fact]
    public void AnyReferenceImplementationCanExportGenericFunctionImplementation()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Library;
            public T Identity<T>(T value) { return move value; }
            """, "library.xe"));
        Compilation app = Compilation.Create(new CompilationOptions(),
            [new TestSemanticReference(library.SemanticModel.GlobalNamespace,
                library.GenericImplementations)], SourceText.From("""
                using Library;
                namespace App;
                int Main() { return Identity<int>(42); }
                """, "app.xe"));

        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        Assert.False(library.GenericImplementations.IsEmpty);
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        BoundFunction specialization = Assert.Single(app.SemanticModel.Functions,
            function => function.Symbol.IsGenericSpecialization);
        Assert.Equal("Identity<int>", specialization.Symbol.Name);
        Assert.Same(BuiltinTypes.Int, specialization.Symbol.ReturnType);
        Assert.True(app.IsSymbolDefinedHere(specialization.Symbol));
    }

    [Fact]
    public void SourceReferenceExportsGenericStructBodiesInitializersConstantsAndAccessors()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Library;
            struct Box<T>
            {
                const int Offset = 2;
                const nuint Width = sizeof(T);
                private T _value;
                private int _offset = Offset;
                public static int State = 7;
                public Box(T value) { _value = move value; }
                public T Get() { return move _value; }
                public int OffsetValue { get { return _offset; } }
                public T this[int index]
                {
                    get { return move _value; }
                    set { _value = move value; }
                }
            }
            struct Cleanup<T> { public ~Cleanup() { } }
            """, "library.xe"));
        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From("""
                using Library;
                namespace App;
                int Main()
                {
                    Box<int> box = Box<int>(40);
                    Cleanup<int> cleanup = Cleanup<int>();
                    box[0] = 40;
                    return box.OffsetValue + box.Get();
                }
                """, "app.xe"));

        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        Assert.False(library.GenericImplementations.IsEmpty);
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        StructTypeSymbol box = app.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "Library").Structs
            .Single(type => type.GenericDefinition?.Name == "Box");
        Assert.True(app.IsSymbolDefinedHere(box));
        Assert.Equal(2, box.Constants.Single(constant => constant.Name == "Offset").Value);
        Assert.IsType<BoundTypeLayoutExpression>(
            box.Constants.Single(constant => constant.Name == "Width").BoundValue);
        Assert.Equal(7, box.StaticFields.Single(field => field.Name == "State").ConstantValue);
        Assert.NotNull(box.Fields.Single(field => field.Name == "_offset").Initializer);
        Assert.NotNull(box.InstanceInitializer);
        Assert.Contains(app.SemanticModel.Functions,
            function => ReferenceEquals(function.Symbol, box.FindMethod("Get")));
        Assert.Contains(app.SemanticModel.Functions,
            function => function.Symbol.AccessorKind == AccessorKind.Getter &&
                ReferenceEquals(function.Symbol.ContainingType, box));
        StructTypeSymbol cleanup = app.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "Library").Structs
            .Single(type => type.GenericDefinition?.Name == "Cleanup");
        Assert.NotNull(cleanup.Destructor);
        Assert.Contains(app.SemanticModel.Functions,
            function => ReferenceEquals(function.Symbol, cleanup.Destructor));
    }

    [Fact]
    public void SourceReferenceVirtualLayoutRemainsOwnedByTheReferencedCompilation()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Library;
            struct Base
            {
                public virtual int Read() { return 1; }
                public virtual int Value { get { return 2; } set { } }
                public virtual int this[int index] { get { return index; } set { } }
                public virtual ~Base() { }
            }
            """, "virtual-library.xe"));
        StructTypeSymbol baseType = library.SemanticModel.GlobalNamespace.Namespaces
            .Single().Structs.Single();
        var before = baseType.VirtualMethods;

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From("""
                using Library;
                namespace App;
                struct Derived : Base
                {
                    public override int Read() { return 40; }
                    public override int Value { get { return 1; } set { } }
                    public override int this[int index] { get { return index; } set { } }
                    public override ~Derived() { }
                }
                """, "virtual-app.xe"));

        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        Assert.True(before == baseType.VirtualMethods);
        StructTypeSymbol derived = app.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "App").Structs.Single();
        Assert.Equal(baseType.VirtualMethods.Length, derived.VirtualMethods.Length);
        Assert.All(derived.VirtualMethods, member => Assert.Same(derived, member.ContainingType));
        Assert.Equal(baseType.VirtualMethods.Select(member => member.VTableSlot),
            derived.VirtualMethods.Select(member => member.VTableSlot));
    }

    [Fact]
    public void ReferencedGenericSpecializationReusesBaseMethodAndDestructorSlots()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Library;
            struct Root
            {
                public virtual int Read() { return 1; }
                public virtual ~Root() { }
            }
            struct Box<T> : Root
            {
                T Item;
                public override int Read() { return 42; }
                public override ~Box() { }
            }
            """, "generic-virtual-library.xe"));
        StructTypeSymbol root = library.SemanticModel.GlobalNamespace.Namespaces
            .Single().Structs.Single(type => type.Name == "Root");
        var rootVirtualMethods = root.VirtualMethods;

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From("""
                using Library;
                namespace App;
                void Use(Box<int>* value) { }
                """, "generic-virtual-app.xe"));

        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        StructTypeSymbol specialization = app.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "Library").Structs
            .Single(type => type.GenericDefinition?.Name == "Box");
        Assert.Equal(rootVirtualMethods.Length, specialization.VirtualMethods.Length);
        Assert.All(specialization.VirtualMethods, member => Assert.Same(specialization, member.ContainingType));
        Assert.Equal(rootVirtualMethods.Select(member => member.VTableSlot),
            specialization.VirtualMethods.Select(member => member.VTableSlot));
        Assert.Contains(specialization.VirtualMethods,
            member => member.FunctionKind == FunctionKind.Destructor);
        Assert.True(rootVirtualMethods == root.VirtualMethods);
    }

    [Fact]
    public void TransitiveSourceReferencesPreserveTemplateConstraintsForGenericImplementations()
    {
        Compilation contracts = Compilation.Create(SourceText.From("""
            namespace Contracts;
            template Readable { int Read(); }
            """, "contracts.xe"));
        Compilation generics = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(contracts)], SourceText.From("""
                using Contracts;
                namespace Generics;
                public int ReadValue<T>(T value) where T : Readable { return value.Read(); }
                """, "generics.xe"));
        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(generics)], SourceText.From("""
                using Generics;
                namespace App;
                struct Value { public int Read() { return 42; } }
                int Main() { Value value = Value(); return ReadValue<Value>(value); }
                """, "app.xe"));

        Assert.False(contracts.HasErrors, string.Join(Environment.NewLine, contracts.Diagnostics));
        Assert.False(generics.HasErrors, string.Join(Environment.NewLine, generics.Diagnostics));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        Assert.Contains(app.SemanticModel.Functions,
            function => function.Symbol.IsGenericSpecialization &&
                function.Symbol.Name.StartsWith("ReadValue<", StringComparison.Ordinal));
    }

    [Fact]
    public void DiamondReferencesDeduplicateTransitiveGenericImplementationsBySymbolIdentity()
    {
        Compilation core = Compilation.Create(SourceText.From(
            "namespace Core; public T Identity<T>(T value) { return move value; }", "core.xe"));
        Compilation left = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(core)],
            SourceText.From("namespace Left; public int Ready() { return 1; }", "left.xe"));
        Compilation right = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(core)],
            SourceText.From("namespace Right; public int Ready() { return 1; }", "right.xe"));
        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(left), new SourceCompilationReference(right)],
            SourceText.From("using Core; namespace App; int Main() { return Identity<int>(42); }", "app.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        Assert.Equal(1, app.GenericImplementations.FunctionCount);
        Assert.Contains(app.SemanticModel.Functions,
            function => function.Symbol.IsGenericSpecialization);
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
    public void PositionLookupNeverReturnsOverlappingSymbolFromAnotherSourceTree()
    {
        const string targetText = """
            namespace Target;
            struct Vector3 {}
            void Use()
            {
                Vector3 vector = Vector3();
            }
            """;
        int targetPosition = targetText.IndexOf("Vector3 vector", StringComparison.Ordinal);
        const string foreignPrefix = "namespace Other; struct Noise { private float ";
        Assert.True(foreignPrefix.Length <= targetPosition);
        string foreignText = foreignPrefix + new string(' ', targetPosition - foreignPrefix.Length) +
            "Y; public float get() { return Y; } }";
        Compilation compilation = Compilation.Create(
            SourceText.From(foreignText, "foreign.xe"),
            SourceText.From(targetText, "target.xe"));
        SyntaxTree targetTree = compilation.SyntaxTrees.Single(tree => tree.Source.Path == "target.xe");
        SemanticModel model = compilation.GetSemanticModel(targetTree);

        foreach (int position in Enumerable.Range(targetPosition, "Vector3".Length))
        {
            StructTypeSymbol symbol = Assert.IsType<StructTypeSymbol>(
                model.GetSymbolInfoAtPosition(targetTree, position).Symbol);
            Assert.Equal("Vector3", symbol.Name);
            Assert.All(symbol.DeclaringSyntaxReferences,
                reference => Assert.Equal(targetTree.SourceFileId, reference.Source.FileId));
        }
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

    private sealed class TestSemanticReference(NamespaceSymbol globalNamespace,
        GenericImplementationStore? genericImplementations = null)
        : CompilationReference(Guid.NewGuid())
    {
        public override NamespaceSymbol GlobalNamespace { get; } = globalNamespace;
        public override GenericImplementationStore GenericImplementations { get; } =
            genericImplementations ?? GenericImplementationStore.Empty;
    }
}
