using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class AtomicTypeSystemTests
{
    [Fact]
    public void AtomicArraysSupportIndependentElementsAndAtomicUnmanagedHandles()
    {
        Compilation compilation = Create("""
            namespace Example;

            struct State { public int Left; public int Right; }

            bool Use(
                atomic<int>[] counters,
                atomic<State>[] states,
                atomic<int[]>& current,
                int[] replacement)
            {
                counters[0]++;
                counters[1] += 4;
                State snapshot = states[0];
                states[1] = snapshot;
                int[] expected = current;
                current = replacement;
                current <-> replacement;
                return current : expected --> replacement;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void AtomicArrayHandlesRejectStackArrayEscape()
    {
        Compilation compilation = Create("""
            namespace Example;
            void Invalid(atomic<int[]>& current) { current = int[4]; }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.StackArrayEscape);
    }

    [Fact]
    public void CompositeAtomicsSupportReadWriteExchangeAndCompareExchange()
    {
        Compilation compilation = Create("""
            namespace Example;

            struct State
            {
                public int Id;
                public bool Active;
                public float Progress;
            }

            bool Update(atomic<State>& current, State expected, State desired)
            {
                State snapshot = current;
                current = desired;
                current <-> snapshot;
                return current : expected --> desired;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction update = Assert.Single(compilation.SemanticModel.Functions,
            function => function.Symbol.Name == "Update");
        Assert.Contains(update.Body.Statements.OfType<BoundReturnStatement>(),
            statement => statement.Expression is BoundCompareExchangeExpression);
    }

    [Fact]
    public void AtomicOwnershipSupportsSharedWeakAndOwnershipContainingValues()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct State { public shared<Resource> Owner; public int Generation; }
            void Use(
                atomic<shared<Resource>>& sharedCurrent,
                atomic<weak<Resource>>& weakCurrent,
                atomic<State>& state,
                shared<Resource> replacement,
                weak<Resource> observer,
                State next)
            {
                shared<Resource> sharedSnapshot = sharedCurrent;
                weak<Resource> weakSnapshot = weakCurrent;
                State stateSnapshot = state;
                sharedCurrent = replacement;
                weakCurrent = observer;
                state = next;
                sharedCurrent <-> replacement;
                weakCurrent <-> observer;
                bool sharedChanged = sharedCurrent : replacement --> sharedSnapshot;
                bool weakChanged = weakCurrent : observer --> weakSnapshot;
                bool stateChanged = state : next --> stateSnapshot;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void AtomicPointersSupportReadsWritesExchangeCompareExchangeAndNull()
    {
        Compilation compilation = Create("""
            namespace Example;

            struct Resource { public int Value; }
            Resource* Identity(Resource* value) { return value; }

            void Use(atomic<Resource*>& location, Resource* first, Resource* second)
            {
                atomic<Resource*> local = null;
                Resource* snapshot = local;
                location = first;
                location <-> second;
                bool replaced = location : second --> first;
                bool cleared = location : first --> null;
                Resource* passed = Identity(location);
                bool empty = local == null;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        VariableDeclarationStatementSyntax snapshot = SyntaxNavigator.DescendantNodesAndSelf(tree.Root)
            .OfType<VariableDeclarationStatementSyntax>()
            .Single(declaration => declaration.IdentifierToken.Text == "snapshot");
        TypeInfo readInfo = compilation.GetSemanticModel(tree).GetTypeInfo(snapshot.Initializer!);
        Assert.IsType<AtomicTypeSymbol>(readInfo.Type);
        Assert.IsType<PointerTypeSymbol>(readInfo.ConvertedType);
    }

    [Fact]
    public void AtomicPointersRejectArithmeticAndIncompatibleCompareExchangeValues()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct First {}
            struct Second {}
            void Invalid(atomic<First*>& pointer, Second* other)
            {
                pointer++;
                pointer += 1;
                pointer : other --> other;
            }
            """);

        Assert.True(compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.InvalidOperatorOperands) >= 2);
        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.CompareExchangeOperandTypeMismatch);
    }

    [Fact]
    public void PrimitiveAtomicValuesSupportReadsWritesAndSingleStepRmwOperators()
    {
        Compilation compilation = Create("""
            namespace Example;

            void Mutate(atomic<int>& value)
            {
                int current = value;
                value = current + 1;
                value++;
                value--;
                ++value;
                --value;
                value += 5;
                value -= 3;
                value |= 8;
                value &= 15;
                value ^= 2;
            }

            int Consume(int value) { return value; }

            void Local()
            {
                atomic<int> value = 0;
                Mutate(value);
                int snapshot = value;
                int consumed = Consume(value);
            }

            struct State { public static atomic<int> Value = 7; }
            """);

        Assert.Empty(compilation.Diagnostics);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        VariableDeclarationStatementSyntax snapshot = SyntaxNavigator.DescendantNodesAndSelf(tree.Root)
            .OfType<VariableDeclarationStatementSyntax>()
            .Single(declaration => declaration.IdentifierToken.Text == "snapshot");
        TypeInfo readInfo = model.GetTypeInfo(snapshot.Initializer!);
        Assert.IsType<AtomicTypeSymbol>(readInfo.Type);
        Assert.Same(BuiltinTypes.Int, readInfo.ConvertedType);
    }

    [Theory]
    [InlineData("atomic<float> value = 0.0; value |= 1.0;")]
    [InlineData("atomic<bool> value = false; value++;")]
    [InlineData("atomic<int> value = 0; value *= 2;")]
    [InlineData("atomic<Data> value; Data replacement; value *= replacement;")]
    public void PrimitiveAtomicValuesRejectUnsupportedRmwOperators(string body)
    {
        Compilation compilation = Create($"namespace Example; struct Data {{ public int Value; }} void Invalid() {{ {body} }}");

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.InvalidOperatorOperands);
    }

    [Fact]
    public void AtomicTypesSupportReferencesPointersArraysOwnershipAndGenericSubstitution()
    {
        Compilation compilation = Create("""
            namespace Example;

            struct Resource {}
            struct Box<T> { public atomic<T> Value; }

            void Consume<T>(atomic<T>& value) {}

            void Use(
                atomic<int>& value,
                readonly atomic<int>& readonlyValue,
                atomic<Resource*> pointer,
                atomic<int>[] elements,
                atomic<int[]> handle,
                atomic<shared<Resource>> sharedValue,
                atomic<weak<Resource>> weakValue,
                Box<int> box)
            {
                Consume(value);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        FunctionSymbol use = ns.Functions.Single(function => function.Name == "Use");

        var mutableReference = Assert.IsType<ReferenceTypeSymbol>(use.Parameters[0].Type);
        Assert.False(mutableReference.IsReadonly);
        Assert.Equal("atomic<int>", Assert.IsType<AtomicTypeSymbol>(mutableReference.ElementType).ToDisplayString());
        var readonlyReference = Assert.IsType<ReferenceTypeSymbol>(use.Parameters[1].Type);
        Assert.True(readonlyReference.IsReadonly);
        Assert.IsType<AtomicTypeSymbol>(readonlyReference.ElementType);
        Assert.IsType<PointerTypeSymbol>(Assert.IsType<AtomicTypeSymbol>(use.Parameters[2].Type).ElementType);
        Assert.IsType<AtomicTypeSymbol>(Assert.IsType<ArrayTypeSymbol>(use.Parameters[3].Type).ElementType);
        Assert.IsType<ArrayTypeSymbol>(Assert.IsType<AtomicTypeSymbol>(use.Parameters[4].Type).ElementType);
        Assert.IsType<SharedTypeSymbol>(Assert.IsType<AtomicTypeSymbol>(use.Parameters[5].Type).ElementType);
        Assert.IsType<WeakTypeSymbol>(Assert.IsType<AtomicTypeSymbol>(use.Parameters[6].Type).ElementType);

        StructTypeSymbol box = ns.Structs.Single(type => type.Name == "Box<int>");
        AtomicTypeSymbol field = Assert.IsType<AtomicTypeSymbol>(Assert.Single(box.Fields).Type);
        Assert.Same(BuiltinTypes.Int, field.ElementType);
        Assert.Contains(compilation.SemanticModel.Functions,
            function => function.Symbol.Name == "Consume<int>");
    }

    [Fact]
    public void AtomicSyntaxAndElementTypeAreAvailableThroughSemanticModel()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            void Use(atomic<Resource*> value) {}
            """);
        Assert.Empty(compilation.Diagnostics);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        NamedTypeSyntax atomicSyntax = SyntaxNavigator.DescendantNodesAndSelf(tree.Root)
            .OfType<NamedTypeSyntax>().Single(type => type.NameToken.Kind == SyntaxKind.AtomicKeyword);
        PointerTypeSyntax pointerSyntax = Assert.IsType<PointerTypeSyntax>(
            Assert.Single(atomicSyntax.TypeArguments!.Arguments));

        AtomicTypeSymbol atomic = Assert.IsType<AtomicTypeSymbol>(model.GetTypeInfo(atomicSyntax).Type);
        Assert.Equal("atomic<Resource*>", atomic.ToDisplayString());
        Assert.Equal("Resource*", model.GetTypeInfo(pointerSyntax).Type.ToDisplayString());
        Assert.Equal("Resource", model.GetTypeInfo(pointerSyntax.ElementType).Type.ToDisplayString());
        Assert.Contains(model.GetResolvedReferences(), reference =>
            reference.Kind == ResolvedReferenceKind.Type && reference.Symbol.Name == "Resource");
    }

    [Theory]
    [InlineData("atomic<unique<int>>", DiagnosticIds.AtomicUniqueTypeNotSupported)]
    [InlineData("atomic<int&>", DiagnosticIds.AtomicReferenceTypeNotSupported)]
    [InlineData("atomic<void>", DiagnosticIds.InvalidAtomicTypeArgument)]
    [InlineData("atomic<atomic<int>>", DiagnosticIds.InvalidAtomicTypeArgument)]
    [InlineData("atomic<storage<int>>", DiagnosticIds.InvalidAtomicTypeArgument)]
    public void InvalidAtomicElementTypesHaveEarlySpecificDiagnostics(string type, string diagnosticId)
    {
        Compilation compilation = Create($"namespace Example; void Use({type} value) {{}}");

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics.Where(item => item.Id == diagnosticId));
        Assert.Contains("atomic", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericSubstitutionCannotHideAtomicUniqueOwnership()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T> { public atomic<T> Value; }
            void Use(Box<unique<int>> value) {}
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.AtomicUniqueTypeNotSupported);
    }

    [Fact]
    public void AtomicTypesAreRejectedAtNativeAbiBoundaries()
    {
        Compilation compilation = Create("""
            namespace Example;
            extern atomic<int> Read();
            export void Write(atomic<int> value) {}
            """);

        Diagnostic[] diagnostics = compilation.Diagnostics
            .Where(item => item.Id == DiagnosticIds.UnsupportedNativeAtomicType).ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Contains("external ABI", diagnostic.Message));
    }

    [Fact]
    public void AtomicTypeIdentityParticipatesInIndexerSignatures()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Operations
            {
                public int this[int value] { get { return 1; } }
                public int this[atomic<int> value] { get { return 2; } }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol operations = Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Structs);
        Assert.Equal(2, operations.Indexers.Length);
        Assert.Contains(operations.Indexers, indexer => indexer.Parameters[0].Type is AtomicTypeSymbol);
        Assert.Contains(operations.Indexers,
            indexer => TypeIdentity.AreSame(indexer.Parameters[0].Type, BuiltinTypes.Int));
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "atomic-types.xe"));
}
