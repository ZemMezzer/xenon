using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class OverloadResolutionTests
{
    [Fact]
    public void Analyzer_BindsFreeInstanceAndStaticOverloadsByParameterType()
    {
        Compilation compilation = Create("""
            namespace Example;
            int Pick(int value) { return 1; }
            int Pick(char value) { return 2; }
            int Pick(readonly byte* value) { return 3; }
            struct Writer
            {
                public int Write(int value) { return 4; }
                public int Write(char value) { return 5; }
                public static int Parse(int value) { return 6; }
                public static int Parse(char value) { return 7; }
            }
            int Use(Writer writer)
            {
                return Pick(1) + Pick('A') + Pick("text") + writer.Write(1) + writer.Write('A') +
                    Writer.Parse(1) + Writer.Parse('A');
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionSymbol[] calls = SyntaxNavigator.DescendantNodesAndSelf(compilation.SyntaxTrees.Single().Root)
            .OfType<CallExpressionSyntax>()
            .Select(call => compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol)
            .OfType<FunctionSymbol>().ToArray();
        Assert.Contains(calls, function => function.Name == "Pick" &&
            TypeIdentity.AreSame(function.Parameters[0].Type, BuiltinTypes.Int));
        Assert.Contains(calls, function => function.Name == "Pick" &&
            TypeIdentity.AreSame(function.Parameters[0].Type, BuiltinTypes.Char));
        Assert.Contains(calls, function => function.Name == "Write" &&
            TypeIdentity.AreSame(function.Parameters[0].Type, BuiltinTypes.Char));
        Assert.Contains(calls, function => function.Name == "Parse" &&
            TypeIdentity.AreSame(function.Parameters[0].Type, BuiltinTypes.Char));
    }

    [Theory]
    [InlineData("void Foo(int first) {} void Foo(int second) {}")]
    [InlineData("int Foo(int value) { return 1; } long Foo(int value) { return 2; }")]
    [InlineData("T Foo<T>(T first) { return move first; } U Foo<U>(U second) { return move second; }")]
    [InlineData("struct S { public void Foo(int first) {} public void Foo(int second) {} }")]
    [InlineData("struct S { public static void Foo(int value) {} public void Foo(int value) {} }")]
    public void Analyzer_RejectsEquivalentCallableSignatures(string declarations)
    {
        Compilation compilation = Create("namespace Example; " + declarations);
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id is DiagnosticIds.InvalidOverload or DiagnosticIds.DuplicateDeclaration);
    }

    [Fact]
    public void Analyzer_PrefersExactMatchAndReportsActualAmbiguity()
    {
        Compilation exact = Create("""
            namespace Example;
            int Pick(byte* value) { return 1; }
            int Pick(readonly byte* value) { return 2; }
            int Use(byte* value) { return Pick(value); }
            """);
        Assert.Empty(exact.Diagnostics);
        BoundFunction use = exact.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var call = Assert.IsType<BoundCallExpression>(Assert.IsType<BoundReturnStatement>(
            Assert.Single(use.Body.Statements)).Expression);
        Assert.False(Assert.IsType<PointerTypeSymbol>(call.Function.Parameters[0].Type).IsReadonly);

        Compilation ambiguous = Create("""
            namespace Example;
            void Pick(int* value) {}
            void Pick(float* value) {}
            void Use() { Pick(null); }
            """);
        Assert.Contains(ambiguous.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall &&
            diagnostic.Message.Contains("Pick", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_PrefersNonGenericExactOverGenericExact()
    {
        Compilation compilation = Create("""
            namespace Example;
            int Pick(int value) { return 1; }
            int Pick<T>(T value) { return 2; }
            int Use() { return Pick(1); }
            """);
        Assert.Empty(compilation.Diagnostics);
        BoundFunction use = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var call = Assert.IsType<BoundCallExpression>(Assert.IsType<BoundReturnStatement>(
            Assert.Single(use.Body.Statements)).Expression);
        Assert.Empty(call.Function.TypeParameters);
        Assert.Null(call.Function.GenericDefinition);
    }

    [Fact]
    public void Analyzer_PreservesInheritedOverloadsAndOverridesOneSlot()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Base
            {
                public virtual int Read(int value) { return 1; }
                public virtual int Read(char value) { return 2; }
            }
            struct Derived : Base
            {
                public override int Read(int value) { return 3; }
                public int Read(readonly byte* value) { return 4; }
            }
            int Use(Derived value) { return value.Read(1) + value.Read('A') + value.Read("x"); }
            """);
        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol derived = compilation.SemanticModel.GlobalNamespace.Namespaces.Single()
            .Structs.Single(type => type.Name == "Derived");
        FunctionSymbol overridden = derived.Methods.Single(method => method.Name == "Read" &&
            TypeIdentity.AreSame(method.Parameters[0].Type, BuiltinTypes.Int));
        FunctionSymbol inheritedChar = derived.BaseType!.Methods.Single(method => method.Name == "Read" &&
            TypeIdentity.AreSame(method.Parameters[0].Type, BuiltinTypes.Char));
        Assert.NotNull(overridden.VTableSlot);
        Assert.NotNull(inheritedChar.VTableSlot);
        Assert.NotEqual(overridden.VTableSlot, inheritedChar.VTableSlot);
    }

    [Fact]
    public void Analyzer_RejectsCAbiOverloadCollisionsAndManglesManagedOverloads()
    {
        Compilation external = Create("""
            namespace Example;
            extern void Native(int value);
            extern void Native(float value);
            """);
        Assert.Contains(external.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.NativeSymbolCollision);

        Compilation managed = Create("""
            namespace Example;
            void Work(int value) {}
            void Work(char value) {}
            void Use() { Work(1); Work('A'); }
            """);
        Assert.Empty(managed.Diagnostics);
        string[] names = managed.SemanticModel.GlobalNamespace.Namespaces.Single().Functions
            .Where(function => function.Name == "Work").Select(NativeSymbolNames.Get).ToArray();
        Assert.Equal(2, names.Distinct(StringComparer.Ordinal).Count());
        Assert.All(names, name => Assert.Contains(".__overload_", name, StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_ResolvesOverloadedFunctionAddressFromExpectedType()
    {
        Compilation compilation = Create("""
            namespace Example;
            int Transform(int value) { return value; }
            float Transform(float value) { return value; }
            int Use()
            {
                function int(int)* callback = &Transform;
                return callback(42);
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        BoundFunction use = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var declaration = Assert.IsType<BoundVariableDeclarationStatement>(use.Body.Statements[0]);
        var address = Assert.IsType<BoundFunctionAddressExpression>(declaration.Initializer);
        Assert.True(TypeIdentity.AreSame(address.Function.Parameters[0].Type, BuiltinTypes.Int));
    }

    [Fact]
    public void Analyzer_UsesCallParameterTypeToResolveOverloadedFunctionAddress()
    {
        Compilation compilation = Create("""
            namespace Example;
            int Transform(int value) { return value; }
            float Transform(float value) { return value; }
            void Register(function int(int)* callback) {}
            struct Registrar
            {
                public void Register(function int(int)* callback) {}
            }
            interface IRegistrar
            {
                void Register(function int(int)* callback);
            }
            void Use(Registrar registrar, IRegistrar interfaceRegistrar)
            {
                Register(&Transform);
                registrar.Register(&Transform);
                interfaceRegistrar.Register(&Transform);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionSymbol[] selected = SyntaxNavigator.DescendantNodesAndSelf(compilation.SyntaxTrees.Single().Root)
            .OfType<UnaryExpressionSyntax>()
            .Where(unary => unary.OperatorToken.Kind == SyntaxKind.AmpersandToken)
            .Select(unary => compilation.SemanticModel.GetSymbolInfo(unary.Operand).Symbol)
            .OfType<FunctionSymbol>()
            .ToArray();
        Assert.Equal(3, selected.Length);
        Assert.All(selected, function =>
            Assert.True(TypeIdentity.AreSame(function.Parameters[0].Type, BuiltinTypes.Int)));
    }

    [Fact]
    public void Analyzer_RanksParameterTypesBeforeReadonlyPreferenceInReadonlyCode()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Value
            {
                public int Read(int value) { return value; }
                public int readonly Read(char value) { return 2; }
            }
            int readonly Use(Value& value) { return value.Read(1); }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction use = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var call = Assert.IsType<BoundMethodCallExpression>(Assert.IsType<BoundReturnStatement>(
            Assert.Single(use.Body.Statements)).Expression);
        Assert.False(call.Method.IsReadonly);
        Assert.True(TypeIdentity.AreSame(call.Method.Parameters[0].Type, BuiltinTypes.Int));
    }

    [Fact]
    public void Analyzer_ResolvesInterfaceAndTemplateMethodOverloads()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface IWriter
            {
                int Write(int value);
                int Write(char value);
            }
            template WriterLike
            {
                int Write(int value);
                int Write(float value);
            }
            struct Writer : IWriter
            {
                public int Write(int value) { return value; }
                public int Write(char value) { return 2; }
                public int Write(float value) { return 3; }
            }
            int ThroughInterface(IWriter value) { return value.Write('A'); }
            int ThroughTemplate<T>(T value) where T : WriterLike { return value.Write(1); }
            int Use(Writer value) { return ThroughInterface(value) + ThroughTemplate<Writer>(value); }
            """);

        Assert.Empty(compilation.Diagnostics);
        TemplateMethodRequirementSymbol selected = SyntaxNavigator
            .DescendantNodesAndSelf(compilation.SyntaxTrees.Single().Root)
            .OfType<MemberAccessExpressionSyntax>()
            .Select(access => compilation.SemanticModel.GetSymbolInfo(access).Symbol)
            .OfType<TemplateMethodRequirementSymbol>()
            .Single();
        Assert.True(TypeIdentity.AreSame(selected.Parameters[0].Type, BuiltinTypes.Int));
    }

    [Fact]
    public void Analyzer_RejectsReturnOnlyTemplateMethodOverloads()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Reader
            {
                int Read(int first);
                float Read(int second);
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidOverload);
    }

    [Fact]
    public void Analyzer_ResolvesIndexerReadonlyOverloadsFromReceiverMutability()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Buffer
            {
                int MutableValue;
                int ReadonlyValue;
                public int* this[int index] { get { return &MutableValue; } }
                public readonly int* readonly this[int index] { get { return &ReadonlyValue; } }
            }
            void Use(Buffer& mutable, readonly Buffer& readOnly)
            {
                int* writable = mutable[0];
                readonly int* readable = readOnly[0];
                readonly int* widened = mutable[1];
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction use = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        BoundMethodCallExpression[] calls = use.Body.Statements.OfType<BoundVariableDeclarationStatement>()
            .Select(statement => Assert.IsType<BoundMethodCallExpression>(statement.Initializer))
            .ToArray();
        Assert.Equal(3, calls.Length);
        Assert.False(calls[0].Method.IsReadonly);
        Assert.True(calls[1].Method.IsReadonly);
        Assert.False(calls[2].Method.IsReadonly);
        Assert.False(Assert.IsType<PointerTypeSymbol>(calls[0].Type).IsReadonly);
        Assert.True(Assert.IsType<PointerTypeSymbol>(calls[1].Type).IsReadonly);
        Assert.False(Assert.IsType<PointerTypeSymbol>(calls[2].Type).IsReadonly);
    }

    [Theory]
    [InlineData("")]
    [InlineData("readonly")]
    public void Analyzer_RejectsReturnOnlyIndexerOverloads(string receiverModifier)
    {
        Compilation compilation = Create($$"""
            namespace Example;
            struct Buffer
            {
                public int* {{receiverModifier}} this[int index] { get { return null; } }
                public readonly int* {{receiverModifier}} this[int index] { get { return null; } }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.DuplicateDeclaration);
        Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Structs.Single().Indexers);
    }

    [Fact]
    public void Analyzer_DoesNotFallBackToMutableIndexerForReadonlyReceiver()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Buffer
            {
                public int* this[int index] { get { return null; } }
                public readonly int* readonly this[int index] { get { return null; } }
            }
            void Use(readonly Buffer& value)
            {
                int* pointer = value[0];
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch);
        IndexExpressionSyntax access = SyntaxNavigator
            .DescendantNodesAndSelf(compilation.SyntaxTrees.Single().Root)
            .OfType<IndexExpressionSyntax>().Single();
        IndexerSymbol selected = Assert.IsType<IndexerSymbol>(
            compilation.SemanticModel.GetSymbolInfo(access).Symbol);
        Assert.True(selected.IsReadonly);
    }

    [Fact]
    public void Analyzer_ReportsMutableIndexerOnReadonlyReceiverWithoutAmbiguity()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Buffer
            {
                public int* this[int index] { get { return null; } }
            }
            void Use(readonly Buffer& value)
            {
                readonly int* pointer = value[0];
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.MutableGetterOnReadonlyReceiver);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.AmbiguousCall);
    }

    [Fact]
    public void Analyzer_PreservesSetterSemanticsForMutableReadonlyIndexerPair()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Buffer
            {
                int Value;
                public int this[int index]
                {
                    get { return Value; }
                    set { Value = value; }
                }
                public int readonly this[int index] { get { return Value; } }
            }
            void Use(Buffer& value)
            {
                value[0] = 42;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction use = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var statement = Assert.IsType<BoundExpressionStatement>(Assert.Single(use.Body.Statements));
        var assignment = Assert.IsType<BoundIndexerSetExpression>(statement.Expression);
        Assert.False(assignment.Indexer.IsReadonly);
    }

    [Fact]
    public void Analyzer_ResolvesReadonlyIndexerOverloadsThroughInterfaceAndTemplateConstraints()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface IIndexed
            {
                int* this[int index] { get; }
                readonly int* readonly this[int index] { get; }
            }
            template Indexed
            {
                int* this[int index] { get; }
                readonly int* readonly this[int index] { get; }
            }
            struct Box : IIndexed
            {
                int MutableValue;
                int ReadonlyValue;
                public int* this[int index] { get { return &MutableValue; } }
                public readonly int* readonly this[int index] { get { return &ReadonlyValue; } }
            }
            struct Derived : Box {}
            int ReadMutable<T>(T& value) where T : Indexed
            {
                int* pointer = value[0];
                return *pointer;
            }
            int ReadReadonly<T>(readonly T& value) where T : Indexed
            {
                readonly int* pointer = value[0];
                return *pointer;
            }
            void ThroughInterface(IIndexed& mutable, readonly IIndexed& readOnly)
            {
                int* writable = mutable[0];
                readonly int* readable = readOnly[0];
            }
            void ThroughInheritance(Derived& mutable, readonly Derived& readOnly)
            {
                int* writable = mutable[0];
                readonly int* readable = readOnly[0];
            }
            int Use(Box& value)
            {
                return ReadMutable<Box>(value) + ReadReadonly<Box>(value);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        InterfaceTypeSymbol contract = compilation.SemanticModel.GlobalNamespace.Namespaces.Single()
            .Interfaces.Single();
        Assert.Equal(2, contract.Indexers.Length);
        Assert.Contains(contract.Indexers, indexer => !indexer.IsReadonly);
        Assert.Contains(contract.Indexers, indexer => indexer.IsReadonly);
        TemplateSymbol template = compilation.SemanticModel.GlobalNamespace.Namespaces.Single()
            .Templates.Single();
        Assert.Equal(2, template.Members.OfType<TemplateIndexerRequirementSymbol>().Count());
    }

    [Fact]
    public void Analyzer_RejectsReturnOnlyIndexerOverloadsInInterfacesAndTemplates()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface IInvalid
            {
                int* this[int index] { get; }
                readonly int* this[int index] { get; }
            }
            template Invalid
            {
                int* readonly this[int index] { get; }
                readonly int* readonly this[int index] { get; }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.DuplicateDeclaration);
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.InvalidOverload);
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "overloads.xe"));

}
