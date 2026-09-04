using System.Runtime.CompilerServices;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class TypeArchitectureTests
{
    [Fact]
    public void DeclaredTypesExposeTheirDeclarationsAndMembersUniformly()
    {
        var compilation = Create("""
            namespace Example;
            struct S { int field; const int Limit = 2; int Read(int x) { return x; } }
            interface I { int Read(int x); }
            enum E { First, Second }
            """);
        var scope = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        Assert.Equal(new[] { "enum", "interface", "struct" }, scope.Types.Select(type => type.DeclarationKind).Order().ToArray());
        foreach (DeclaredTypeSymbol type in scope.Types)
        {
            Assert.Same(scope, type.ContainingSymbol);
            Assert.Same(scope, type.ContainingNamespace);
            Assert.Equal($"Example.{type.Name}", type.QualifiedName);
            Assert.Equal(type.Name, type.Declaration.IdentifierToken.Text);
            Assert.All(type.GetMembers(), member => Assert.Same(type, member.ContainingSymbol));
        }
        var structure = Assert.Single(scope.Structs);
        Assert.NotNull(structure.FindMember<FieldSymbol>("field"));
        Assert.NotNull(structure.FindMember<ConstantSymbol>("Limit"));
        Assert.NotNull(Assert.Single(scope.Interfaces).FindMember<FunctionSymbol>("Read"));
        var enumeration = Assert.Single(scope.Enums);
        Assert.All(enumeration.Members, member => Assert.Same(enumeration, member.ContainingType));
    }

    [Fact]
    public void OwnershipIncludesAccessorsParametersAndLocalsWithoutSharingParameterSymbols()
    {
        var compilation = Create("""
            namespace Example.Nested;
            struct S {
                int field;
                int Read(int x) { int local = x; return local; }
                int Value { get { return field; } set { field = value; } }
                int this[int index] { get { return index; } set { field = value; } }
            }
            interface I { int this[int index] { get; set; } }
            int Free(int p) { int local = p; return local; }
            """);
        var outer = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        var scope = Assert.Single(outer.Namespaces);
        var structure = Assert.Single(scope.Structs);
        var property = Assert.Single(structure.Properties);
        Assert.Same(property, property.Getter!.ContainingSymbol);
        Assert.Same(property, property.Setter!.ContainingSymbol);
        Assert.Same(structure, property.Getter.ContainingType);
        Assert.Same(scope, property.Getter.ContainingNamespace);
        Assert.Equal("Example.Nested.S.Value.get_Value", property.Getter.QualifiedName);

        var indexer = Assert.Single(structure.Indexers);
        Assert.Same(indexer, indexer.Parameters[0].ContainingSymbol);
        Assert.Same(indexer, indexer.Getter!.ContainingSymbol);
        Assert.Same(indexer, indexer.Setter!.ContainingSymbol);
        Assert.NotSame(indexer.Parameters[0], indexer.Getter.Parameters[0]);
        Assert.NotSame(indexer.Getter.Parameters[0], indexer.Setter.Parameters[0]);
        Assert.Same(indexer.Getter, indexer.Getter.Parameters[0].ContainingSymbol);
        Assert.All(indexer.Setter.Parameters, parameter => Assert.Same(indexer.Setter, parameter.ContainingSymbol));

        var contract = Assert.Single(scope.Interfaces);
        var interfaceIndexer = Assert.Single(contract.Indexers);
        Assert.Same(interfaceIndexer, interfaceIndexer.Parameters[0].ContainingSymbol);
        Assert.Same(interfaceIndexer, interfaceIndexer.Getter!.ContainingSymbol);
        Assert.Same(contract, interfaceIndexer.Getter.ContainingType);
        Assert.Same(contract, interfaceIndexer.Getter.ContainingInterface);
        Assert.NotSame(interfaceIndexer.Parameters[0], interfaceIndexer.Getter.Parameters[0]);
        Assert.NotSame(interfaceIndexer.Getter.Parameters[0], interfaceIndexer.Setter!.Parameters[0]);

        foreach (var function in compilation.SemanticModel.Functions)
        {
            Assert.All(function.Symbol.Parameters, parameter => Assert.Same(function.Symbol, parameter.ContainingSymbol));
            foreach (var local in function.Body.Statements.OfType<BoundVariableDeclarationStatement>())
                Assert.Same(function.Symbol, local.Variable.ContainingSymbol);
        }
        var free = Assert.Single(scope.Functions);
        Assert.Same(scope, free.ContainingSymbol);
        Assert.Equal("Example.Nested.Free.p", free.Parameters[0].QualifiedName);
    }

    [Fact]
    public void GenericArgumentsAreSyntaxOnlyAndNeverSilentlyDiscarded()
    {
        var compilation = Compilation.Create(SourceText.From("namespace Example; struct Box {} void F(Box<int> value) {}"));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("generic type arguments"));
    }

    [Fact]
    public void FactoryInternsEveryDerivedKindAndNormalizesForeignConstructions()
    {
        var compilation = Create("namespace Example; struct S {}");
        var type = Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types);
        TypeFactory factory = compilation.TypeFactory;
        Assert.Same(factory, compilation.SemanticModel.TypeFactory);
        Assert.Same(factory.PointerTo(type), factory.PointerTo(type));
        Assert.Same(factory.ReferenceTo(type, true), factory.ReferenceTo(type, true));
        Assert.Same(factory.ArrayOf(type, 3), factory.ArrayOf(type, 3));
        Assert.Same(factory.AtomicOf(type), factory.AtomicOf(type));
        Assert.Same(factory.UniqueOf(type), factory.UniqueOf(type));
        Assert.Same(factory.SharedOf(type), factory.SharedOf(type));
        Assert.Same(factory.WeakOf(type), factory.WeakOf(type));
        Assert.NotSame(factory.PointerTo(type), factory.PointerTo(type, true));
        var other = new TypeFactory();
        TypeSymbol foreign = other.ReferenceTo(other.ArrayOf(other.PointerTo(type, true), 3));
        TypeSymbol local = factory.ReferenceTo(factory.ArrayOf(factory.PointerTo(type, true), 3));
        Assert.NotSame(foreign, local);
        Assert.Same(local, factory.Intern(foreign));
        Assert.Same(local, factory.ReferenceTo(other.ArrayOf(other.PointerTo(type, true), 3)));
        Assert.True(TypeIdentity.AreSame(factory.UniqueOf(type), other.UniqueOf(type)));
        Assert.True(TypeIdentity.AreSame(factory.SharedOf(type), other.SharedOf(type)));
        Assert.True(TypeIdentity.AreSame(factory.WeakOf(type), other.WeakOf(type)));
        Assert.True(TypeIdentity.AreSame(factory.AtomicOf(type), other.AtomicOf(type)));
        Assert.False(TypeIdentity.AreSame(factory.UniqueOf(type), factory.SharedOf(type)));
        Assert.False(TypeIdentity.AreSame(factory.SharedOf(type), factory.WeakOf(type)));
        Assert.False(TypeIdentity.AreSame(factory.AtomicOf(type), type));
        Assert.Equal("unique<Example.S>", factory.UniqueOf(type).ToDisplayString(TypeDisplayFormat.FullyQualified));
        Assert.Equal("shared<Example.S>", factory.SharedOf(type).ToDisplayString(TypeDisplayFormat.FullyQualified));
        Assert.Equal("weak<Example.S>", factory.WeakOf(type).ToDisplayString(TypeDisplayFormat.FullyQualified));
        Assert.Equal("atomic<Example.S>", factory.AtomicOf(type).ToDisplayString(TypeDisplayFormat.FullyQualified));
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.ArrayOf(type, 0));
        Assert.Throws<ArgumentNullException>(() => factory.PointerTo(null!));
    }

    [Fact]
    public void FactoryIsCanonicalUnderConcurrentConstruction()
    {
        var factory = new TypeFactory();
        var results = new TypeSymbol[128];
        Parallel.For(0, results.Length, index =>
            results[index] = factory.AtomicOf(
                factory.ArrayOf(factory.ReferenceTo(factory.PointerTo(BuiltinTypes.Int, true), true), 2)));
        Assert.All(results, type => Assert.Same(results[0], type));
    }

    [Fact]
    public void CompilationSnapshotsHaveDistinctNominalAndDerivedTypesEvenWhenSyntaxIsShared()
    {
        var first = Create("namespace Example; struct S {} void F(S* pointer) {}");
        var second = first.WithTargetLayout(new TestLayout());
        Assert.Empty(second.Diagnostics);
        Assert.Same(first.SyntaxTrees[0], second.SyntaxTrees[0]);
        Assert.NotSame(first.TypeFactory, second.TypeFactory);
        var a = Assert.Single(first.SemanticModel.GlobalNamespace.Namespaces).Types.Single();
        var b = Assert.Single(second.SemanticModel.GlobalNamespace.Namespaces).Types.Single();
        Assert.Equal(a.FullName, b.FullName);
        Assert.False(TypeIdentity.AreSame(a, b));
        Assert.False(TypeIdentity.AreSame(first.TypeFactory.PointerTo(a), second.TypeFactory.PointerTo(b)));
        Assert.NotSame(first.TypeFactory.PointerTo(BuiltinTypes.Int), second.TypeFactory.PointerTo(BuiltinTypes.Int));
        Assert.True(TypeIdentity.AreSame(first.TypeFactory.PointerTo(BuiltinTypes.Int), second.TypeFactory.PointerTo(BuiltinTypes.Int)));
    }

    [Fact]
    public void StructuralEqualityAndHashingDoNotDependOnCanonicalReferences()
    {
        var a = new TypeFactory();
        var b = new TypeFactory();
        TypeSymbol left = a.ArrayOf(a.ReferenceTo(a.PointerTo(BuiltinTypes.Int, true), true), 2);
        TypeSymbol right = b.ArrayOf(b.ReferenceTo(b.PointerTo(BuiltinTypes.Int, true), true), 2);
        Assert.NotSame(left, right);
        Assert.True(TypeIdentity.AreSame(left, right));
        Assert.True(TypeIdentity.AreSame(right, left));
        Assert.Equal(TypeIdentity.GetHashCode(left), TypeIdentity.GetHashCode(right));
        var set = new HashSet<TypeSymbol>(TypeIdentity.Comparer) { left, right };
        Assert.Single(set);

        TypeSymbol[] different = [
            b.ArrayOf(b.ReferenceTo(b.PointerTo(BuiltinTypes.Int), true), 2),
            b.ArrayOf(b.ReferenceTo(b.PointerTo(BuiltinTypes.Int, true)), 2),
            b.ArrayOf(b.ReferenceTo(b.PointerTo(BuiltinTypes.Int, true), true), 1),
            b.ArrayOf(b.ReferenceTo(b.PointerTo(BuiltinTypes.UInt, true), true), 2),
            b.PointerTo(b.ReferenceTo(b.PointerTo(BuiltinTypes.Int, true), true)),
        ];
        Assert.All(different, type => Assert.False(TypeIdentity.AreSame(left, type)));
        Assert.False(TypeIdentity.AreSame(a.ArrayOf(a.ArrayOf(BuiltinTypes.Int)), b.ArrayOf(BuiltinTypes.Int, 2)));
        Assert.False(TypeIdentity.AreSame(BuiltinTypes.Long, BuiltinTypes.NInt));
        Assert.False(TypeIdentity.AreSame(BuiltinTypes.Int, BuiltinTypes.UInt));
        Assert.True(TypeIdentity.AreSame(null, null));
        Assert.False(TypeIdentity.AreSame(null, BuiltinTypes.Int));
    }

    [Fact]
    public void EqualityIsNotReadonlyConversionOrEnumUnderlyingLayout()
    {
        var compilation = Create("namespace Example; enum E { A } void F(int* p) { readonly int* view = p; }");
        var factory = compilation.TypeFactory;
        Assert.False(TypeIdentity.AreSame(factory.PointerTo(BuiltinTypes.Int), factory.PointerTo(BuiltinTypes.Int, true)));
        Assert.False(TypeIdentity.AreSame(factory.ReferenceTo(BuiltinTypes.Int), factory.PointerTo(BuiltinTypes.Int)));
        Assert.False(TypeIdentity.AreSame(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Enums.Single(), BuiltinTypes.Int));
    }

    [Fact]
    public void DiscardedCompilationsDoNotRemainAliveThroughTypeInterning()
    {
        var references = MakeCollectibleCompilation();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        Assert.False(references.Type.TryGetTarget(out _));
        Assert.False(references.Source.TryGetTarget(out _));
        Assert.False(references.Factory.TryGetTarget(out _));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<TypeSymbol> Type, WeakReference<SourceText> Source, WeakReference<TypeFactory> Factory) MakeCollectibleCompilation()
    {
        SourceText source = SourceText.From("namespace Example; struct S {} void F(S* p, S& r, S[] a) {}");
        var compilation = Compilation.Create(source);
        Assert.Empty(compilation.Diagnostics);
        TypeSymbol type = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Types.Single();
        return (new(type), new(source), new(compilation.TypeFactory));
    }

    [Theory]
    [InlineData("int", "int", "int")]
    [InlineData("S", "S", "Example.S")]
    [InlineData("readonly S*", "readonly S*", "readonly Example.S*")]
    [InlineData("readonly S**", "readonly S**", "readonly Example.S**")]
    [InlineData("readonly S&", "readonly S&", "readonly Example.S&")]
    [InlineData("readonly S*&", "readonly S*&", "readonly Example.S*&")]
    [InlineData("readonly S**&[][,]", "readonly S**&[][,]", "readonly Example.S**&[][,]")]
    [InlineData("S[][,][]", "S[][,][]", "Example.S[][,][]")]
    public void DisplayFormatsSourceTypesAndRoundTripsTheirSemanticShape(string spelling, string shortName, string fullName)
    {
        var compilation = Create($"namespace Example; struct S {{}} void F({spelling} value) {{}}");
        TypeSymbol type = Assert.Single(compilation.SemanticModel.Functions).Symbol.Parameters[0].Type;
        Assert.Equal(shortName, type.ToDisplayString());
        Assert.Equal(fullName, type.ToDisplayString(TypeDisplayFormat.FullyQualified));
        Assert.Equal(shortName, type.Name);
        Assert.Equal(shortName, type.ToString());
        var roundTrip = Create($"namespace Example; struct S {{}} void F({fullName} value) {{}}");
        Assert.Equal(fullName, Assert.Single(roundTrip.SemanticModel.Functions).Symbol.Parameters[0].Type.ToDisplayString(TypeDisplayFormat.FullyQualified));
    }

    [Fact]
    public void DisplayDisambiguatesQualifierScopeForCompilerCreatedTypes()
    {
        var factory = new TypeFactory();
        TypeSymbol innerReadonly = factory.PointerTo(factory.PointerTo(BuiltinTypes.Int, true));
        TypeSymbol outerReadonly = factory.PointerTo(factory.PointerTo(BuiltinTypes.Int), true);
        Assert.Equal("readonly int**", innerReadonly.ToDisplayString());
        Assert.Equal("readonly (int*)*", outerReadonly.ToDisplayString());
        Assert.Equal("(readonly int*)&", factory.ReferenceTo(factory.PointerTo(BuiltinTypes.Int, true)).ToDisplayString());
        Assert.Equal("readonly (int*)&", factory.ReferenceTo(factory.PointerTo(BuiltinTypes.Int), true).ToDisplayString());
        Assert.Equal("(int[])*", factory.PointerTo(factory.ArrayOf(BuiltinTypes.Int)).ToDisplayString());
    }

    [Fact]
    public void GeneralMemberLookupDoesNotMakeConstructorsOrStaticFieldsIntoInstanceMembers()
    {
        var compilation = Compilation.Create(SourceText.From(
            "namespace Example; struct S { public S() {} public static int Count = 1; } void F() { S value = S(); value.S(); int x = value.Count; }"));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("does not contain method 'S'"));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("does not contain field 'Count'"));
        Create("namespace Example; struct S { public static int Count = 1; } int F() { return S.Count; }");
    }

    [Fact]
    public void FieldStorageCapabilityIncludesInheritedFieldsWithoutMakingInterfacesIntoStorage()
    {
        var compilation = Create("namespace Example; struct Base { int field; } struct Derived : Base { int other; } interface I {}");
        var scope = compilation.SemanticModel.GlobalNamespace.Namespaces.Single();
        var derived = scope.Structs.Single(type => type.Name == "Derived");
        var storage = Assert.IsAssignableFrom<IFieldStorageTypeSymbol>(derived);
        Assert.Equal(new[] { "field", "other" }, storage.AllInstanceFields.Select(field => field.Name).ToArray());
        Assert.False((TypeSymbol)scope.Interfaces.Single() is IFieldStorageTypeSymbol);
    }

    [Fact]
    public void StaticFieldLookupDoesNotIntroduceImplicitInheritance()
    {
        var compilation = Compilation.Create(SourceText.From(
            "namespace Example; struct Base { public static int Count = 1; } struct Derived : Base {} int F() { return Derived.Count; }"));
        Assert.True(compilation.HasErrors);
        var scope = compilation.SemanticModel.GlobalNamespace.Namespaces.Single();
        Assert.NotNull(scope.Types.Single(type => type.Name == "Base").FindStaticField("Count"));
        Assert.Null(scope.Types.Single(type => type.Name == "Derived").FindStaticField("Count"));
    }


    [Fact]
    public void SourceSymbolsExposeDeclarationSyntaxAndCanonicalDiagnosticCoordinates()
    {
        var source = SourceText.From("""
            namespace Example.Nested;
            const int Global = 1;
            struct S {
                int field;
                const int Limit = 2;
                public S(int seed) { field = seed; }
                public ~S() {}
                int Read(int x) { int local = x; return local; }
                int Value { get { return field; } set { field = value; } }
                int this[int index] { get { return index; } set { field = value; } }
            }
            interface I {
                int Read(int x);
                int Value { get; set; }
                int this[int index] { get; set; }
            }
            enum E { First, Second }
            extern int Native(int p);
            """, "symbols.xe");
        var compilation = Compilation.Create(source);
        Assert.Empty(compilation.Diagnostics);
        Symbol[] symbols = Descendants(compilation.SemanticModel.GlobalNamespace).ToArray();
        Assert.Contains(symbols, symbol => symbol is EnumTypeSymbol);
        Assert.Contains(symbols, symbol => symbol is InterfaceIndexerSymbol);
        foreach (Symbol symbol in symbols)
        {
            if (symbol is ParameterSymbol { Name: "value" })
            {
                Assert.False(symbol.IsSourceDefined); // The setter value parameter is implicit.
                continue;
            }
            var reference = Assert.Single(symbol.DeclaringSyntaxReferences);
            Assert.True(symbol.IsSourceDefined);
            Assert.Same(source, reference.Source);
            Assert.Equal("symbols.xe", reference.Path);
            Assert.Equal(reference.IdentifierToken.Location, Assert.Single(symbol.Locations));
            Assert.Equal(reference.Span, reference.Location.Span);
            string expectedName = symbol is FunctionSymbol { FunctionKind: FunctionKind.Destructor }
                ? symbol.Name[1..]
                : reference.Declaration is PropertyAccessorDeclarationSyntax accessorSyntax
                    ? accessorSyntax.KeywordToken.Text : symbol.Name;
            Assert.Equal(expectedName, source.GetText(reference.Span));
            Assert.Equal(reference.Path, reference.Location.Path);
        }
        var structure = symbols.OfType<StructTypeSymbol>().Single();
        Assert.Same(structure.Declaration, structure.DeclaringSyntaxReferences[0].Declaration);
        var accessor = structure.Indexers[0].Getter!;
        Assert.Equal("get", source.GetText(accessor.Locations[0].Span));
        Assert.IsType<PropertyAccessorDeclarationSyntax>(accessor.DeclaringSyntaxReferences[0].Declaration);
        Assert.Same(structure.Indexers[0].Parameters[0].DeclaringSyntaxReferences[0].Declaration,
            accessor.Parameters[0].DeclaringSyntaxReferences[0].Declaration);
        var local = compilation.SemanticModel.Functions.SelectMany(function => function.Body.Statements)
            .OfType<BoundVariableDeclarationStatement>().Single().Variable;
        Assert.Equal("local", source.GetText(Assert.Single(local.Locations).Span));
        Assert.IsType<VariableDeclarationStatementSyntax>(Assert.Single(local.DeclaringSyntaxReferences).Declaration);
        Assert.IsType<EnumMemberDeclarationSyntax>(symbols.OfType<EnumTypeSymbol>().Single().Members[0]
            .DeclaringSyntaxReferences[0].Declaration);
    }

    [Fact]
    public void NamespaceReferencesRetainEachFileAndEachQualifiedNamePart()
    {
        var first = SourceText.From("namespace Example.Nested; struct A {}", "a.xe");
        var second = SourceText.From("// second file\r\nnamespace Example.Nested; struct B {}", "b.xe");
        var compilation = Compilation.Create(first, second);
        Assert.Empty(compilation.Diagnostics);
        var outer = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        var inner = Assert.Single(outer.Namespaces);
        Assert.Equal(2, outer.DeclaringSyntaxReferences.Length);
        Assert.Equal(2, inner.Locations.Length);
        Assert.Equal(new[] { "a.xe", "b.xe" }, inner.Locations.Select(location => location.Path).ToArray());
        Assert.All(outer.Locations, location => Assert.Equal("Example", location.Source.GetText(location.Span)));
        Assert.All(inner.Locations, location => Assert.Equal("Nested", location.Source.GetText(location.Span)));
        Assert.Equal(new LinePosition(1, 18), inner.Locations[1].Start);
        Assert.Same(compilation.SyntaxTrees[1].Root.Namespace, inner.DeclaringSyntaxReferences[1].Declaration);
        Assert.Same(outer.DeclaringSyntaxReferences[1].Declaration, inner.DeclaringSyntaxReferences[1].Declaration);
        Assert.Empty(compilation.SemanticModel.GlobalNamespace.Locations);
    }

    [Fact]
    public void NonSourceSymbolsHaveNoFabricatedDeclarationLocations()
    {
        var compilation = Create("""
            namespace Example;
            struct Base {}
            struct Derived : Base {
                int field = 1;
                int Value { get { return field; } set { field = value; } }
            }
            """);
        var derived = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Structs.Single(type => type.Name == "Derived");
        Symbol[] implicitSymbols = [
            BuiltinTypes.Int, BuiltinTypes.Void, compilation.TypeFactory.PointerTo(derived),
            compilation.TypeFactory.ReferenceTo(derived), compilation.TypeFactory.ArrayOf(derived),
            compilation.SemanticModel.GlobalNamespace, derived.Constructor!, derived.InstanceInitializer!,
            derived.Properties[0].Setter!.Parameters[0],
        ];
        Assert.All(implicitSymbols, symbol =>
        {
            Assert.NotNull(symbol);
            Assert.False(symbol.IsSourceDefined);
            Assert.Empty(symbol.Locations);
            Assert.Empty(symbol.DeclaringSyntaxReferences);
        });
        Assert.True(derived.IsSourceDefined);
    }

    [Fact]
    public void SourceReferencesDistinguishSnapshotsEvenAtTheSameFilePath()
    {
        var first = Compilation.Create(SourceText.From("namespace Example; struct S {}", "same.xe"));
        var second = Compilation.Create(SourceText.From("namespace Example;\r\nstruct S {}", "same.xe"));
        var a = first.SemanticModel.GlobalNamespace.Namespaces.Single().Types.Single().DeclaringSyntaxReferences[0];
        var b = second.SemanticModel.GlobalNamespace.Namespaces.Single().Types.Single().DeclaringSyntaxReferences[0];
        Assert.Equal(a.Path, b.Path);
        Assert.NotSame(a.Source, b.Source);
        Assert.NotEqual(a.Location, b.Location);
        Assert.Equal(0, a.Location.Start.Line);
        Assert.Equal(1, b.Location.Start.Line);
        var rebound = first.WithTargetLayout(new TestLayout());
        Assert.Same(a.Declaration, rebound.SemanticModel.GlobalNamespace.Namespaces.Single().Types.Single()
            .DeclaringSyntaxReferences[0].Declaration);
    }

    [Fact]
    public void DiagnosticLocationsUseTheSameSourceSnapshotAndSpansAsDeclarations()
    {
        var source = SourceText.From("namespace Example;\r\nvoid F(int value, int value) {}", "duplicate.xe");
        var compilation = Compilation.Create(source);
        var diagnostic = Assert.Single(compilation.Diagnostics);
        var function = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Functions.Single();
        Assert.Equal(function.Parameters[1].Locations[0], diagnostic.Location);
        Assert.Same(source, diagnostic.Location.Source);
        Assert.Equal(new LinePosition(1, 22), diagnostic.Location.Start);
        Assert.Equal("value", source.GetText(diagnostic.Location.Span));

        var tree = SyntaxTree.Parse(SourceText.From("namespace Example; struct {}", "incomplete.xe"));
        var declaration = tree.Root.Members.OfType<StructDeclarationSyntax>().Single();
        var reference = new SyntaxReference(declaration);
        Assert.True(reference.IdentifierToken.IsMissing);
        Assert.Equal(0, reference.Span.Length);
        Assert.Contains(tree.Diagnostics, error => error.Location.Span.Start == reference.Span.Start);
        Assert.Throws<ArgumentException>(() => new SyntaxReference(tree.Root));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SyntaxReference(tree.Root.Namespace, 10));
    }

    [Fact]
    public void SymbolDisplayFormatsNamesSignaturesDeclarationsAndCanonicalEmbeddedTypes()
    {
        var compilation = Create("""
            namespace Example.Nested;
            struct S {
                public static int Count = 1;
                public int Value { get { return S.Count; } }
                public int this[int index] { get { return index; } }
                public int readonly Read(int x) { return x; }
                public S(int seed) {}
                public ~S() {}
            }
            readonly S* Transform(readonly S**&[][,] items, readonly int* readonly pointer) { return null; }
            """);
        var scope = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Namespaces.Single();
        var type = scope.Structs.Single();
        var function = scope.Functions.Single();
        Assert.Equal("Nested", scope.ToDisplayString(SymbolDisplayFormat.ShortName));
        Assert.Equal("Example.Nested", scope.ToDisplayString(SymbolDisplayFormat.QualifiedName));
        Assert.Equal("namespace Nested", scope.ToDisplayString(SymbolDisplayFormat.Declaration));
        Assert.Equal("struct S", type.ToDisplayString(SymbolDisplayFormat.Declaration));
        Assert.Equal(type.ToDisplayString(TypeDisplayFormat.FullyQualified), SymbolDisplay.ToDisplayString(type, SymbolDisplayFormat.QualifiedName));
        Assert.Equal("Transform", function.ToDisplayString(SymbolDisplayFormat.ShortName));
        Assert.Equal("Example.Nested.Transform", function.ToDisplayString(SymbolDisplayFormat.QualifiedName));
        Assert.Equal("readonly S* Transform(readonly S**&[][,] items, readonly int* readonly pointer)",
            SymbolDisplay.ToDisplayString(function));
        Assert.Equal("readonly Example.Nested.S* Example.Nested.Transform(readonly Example.Nested.S**&[][,] items, readonly int* readonly pointer)",
            function.ToDisplayString(SymbolDisplayFormat.QualifiedSignature));
        Assert.Equal("Example.Nested.Transform(readonly S**&[][,], readonly int*)",
            function.ToDisplayString(SymbolDisplayFormat.Diagnostic));
        Assert.Equal("readonly int* readonly pointer", function.Parameters[1].ToDisplayString(SymbolDisplayFormat.Signature));
        Assert.Equal("Example.Nested.Transform.pointer", function.Parameters[1].ToDisplayString(SymbolDisplayFormat.QualifiedName));
        Assert.Equal("public static int Count", type.StaticFields[0].ToDisplayString(SymbolDisplayFormat.Declaration));
        Assert.Equal("int Example.Nested.S.Value", type.Properties[0].ToDisplayString(SymbolDisplayFormat.QualifiedSignature));
        Assert.Equal("int Example.Nested.S.this[int index]", type.Indexers[0].ToDisplayString(SymbolDisplayFormat.QualifiedSignature));
        Assert.Equal("Example.Nested.S.this[int]", type.Indexers[0].ToDisplayString(SymbolDisplayFormat.Diagnostic));
        Assert.Equal("int Read(int x) readonly", type.Methods.Single(method => method.Name == "Read")
            .ToDisplayString(SymbolDisplayFormat.Signature));
        Assert.Equal("S(int seed)", type.Constructors[0].ToDisplayString(SymbolDisplayFormat.Signature));
        Assert.Equal("~S()", type.Destructor!.ToDisplayString(SymbolDisplayFormat.Signature));
    }

    [Fact]
    public void SymbolDisplayUsesAccessorOwnershipWithoutLeakingNativeMangledNames()
    {
        var compilation = Create("""
            namespace Example;
            interface I { int Value { get; set; } int this[int index] { get; set; } }
            enum E { First }
            const int Limit = 2;
            void F(int x) { int local = x; }
            """);
        var scope = compilation.SemanticModel.GlobalNamespace.Namespaces.Single();
        var contract = scope.Interfaces.Single();
        Assert.Equal("interface I", SymbolDisplay.ToDisplayString(contract, SymbolDisplayFormat.Declaration));
        Assert.Equal("enum E", SymbolDisplay.ToDisplayString(scope.Enums.Single(), SymbolDisplayFormat.Declaration));
        Assert.Equal("const Example.E Example.E.First", scope.Enums.Single().Members[0].ToDisplayString(SymbolDisplayFormat.QualifiedSignature));
        Assert.Equal("const int Limit", scope.Constants.Single().ToDisplayString(SymbolDisplayFormat.Declaration));
        Assert.Equal("public abstract int Value", contract.Properties[0].ToDisplayString(SymbolDisplayFormat.Declaration));
        Assert.Equal("int Example.I.this[int index]", contract.Indexers[0].ToDisplayString(SymbolDisplayFormat.QualifiedSignature));
        Assert.Equal("Example.I.Value.get", contract.Properties[0].Getter!.ToDisplayString(SymbolDisplayFormat.QualifiedName));
        var setter = contract.Indexers[0].Setter!;
        Assert.Equal("void Example.I.this.set(int index, int value)", setter.ToDisplayString(SymbolDisplayFormat.QualifiedSignature));
        Assert.Equal("Example.I.this.set.value", setter.Parameters[1].ToDisplayString(SymbolDisplayFormat.QualifiedName));
        var local = compilation.SemanticModel.Functions.Single().Body.Statements.OfType<BoundVariableDeclarationStatement>().Single().Variable;
        Assert.Equal("int local", local.ToDisplayString(SymbolDisplayFormat.Signature));
        Assert.Equal("Example.F.local", local.ToDisplayString(SymbolDisplayFormat.QualifiedName));
    }

    [Fact]
    public void DuplicateConstructorDiagnosticUsesHumanReadableSymbolSignature()
    {
        var compilation = Compilation.Create(SourceText.From(
            "namespace Example; struct S { S(int x) {} S(int y) {} }", "constructors.xe"));
        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("constructor 'Example.S.S(int)' is already declared", diagnostic.Message);
        Assert.Equal("S", diagnostic.Location.Source.GetText(diagnostic.Location.Span));
        Assert.Equal("constructors.xe", diagnostic.Location.Path);
    }


    [Theory]
    [InlineData("readonly int")]
    [InlineData("readonly int&")]
    [InlineData("readonly int&[]")]
    [InlineData("readonly int&[][,]")]
    [InlineData("readonly int* readonly")]
    public void SymbolDisplayDoesNotDuplicateReadonlySharedByBindingAndType(string spelling)
    {
        var compilation = Create($"namespace Example; void F({spelling} value) {{}}");
        var parameter = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Functions.Single().Parameters[0];
        Assert.Equal($"{spelling} value", parameter.ToDisplayString(SymbolDisplayFormat.Signature));
    }

    private static IEnumerable<Symbol> Descendants(Symbol owner)
    {
        IEnumerable<Symbol> children = owner switch
        {
            NamespaceSymbol scope => scope.Namespaces.Cast<Symbol>().Concat(scope.Types).Concat(scope.Functions).Concat(scope.Constants),
            DeclaredTypeSymbol type => type.GetMembers(),
            FunctionSymbol function => function.Parameters,
            PropertySymbol property => new[] { property.Getter, property.Setter }.OfType<Symbol>(),
            InterfacePropertySymbol property => new[] { property.Getter, property.Setter }.OfType<Symbol>(),
            IndexerSymbol indexer => indexer.Parameters.Cast<Symbol>().Concat(new[] { indexer.Getter, indexer.Setter }.OfType<Symbol>()),
            InterfaceIndexerSymbol indexer => indexer.Parameters.Cast<Symbol>().Concat(new[] { indexer.Getter, indexer.Setter }.OfType<Symbol>()),
            _ => [],
        };
        foreach (Symbol child in children)
        {
            yield return child;
            foreach (Symbol descendant in Descendants(child)) yield return descendant;
        }
    }

    private sealed class TestLayout : ITargetTypeLayout
    {
        public int GetIntegerBitWidth(PrimitiveTypeSymbol type) => type.BitWidth ?? 64;
        public ulong GetSize(TypeSymbol type) => 8;
        public uint GetAlignment(TypeSymbol type) => 8;
        public ulong GetFieldOffset(StructTypeSymbol type, FieldSymbol field) => 0;
    }

    private static Compilation Create(string source)
    {
        var compilation = Compilation.Create(SourceText.From(source));
        Assert.Empty(compilation.Diagnostics);
        return compilation;
    }
}
