using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class AllocatorConstructorAccessibilityTests
{
    private const string Providers = """
        namespace Api;
        public allocator Heap {
            public +Heap(nuint size, nuint alignment) { return malloc(size, alignment); }
            public -Heap(void* block) { free(block); }
        }
        public allocator Arena : Heap {}
        """;

    [Theory]
    [InlineData("public", "public", true)]
    [InlineData("private", "public", false)]
    [InlineData("", "public", false)]
    [InlineData("public", "private", false)]
    public void BothConstructorsMustBeAccessibleAtTheNewExpression(string contextAccess, string ordinaryAccess, bool valid)
    {
        const string expression = "new(heap) Buffer(7)";
        var source = SourceText.From(Providers + $$"""
            struct Buffer {
                {{contextAccess}} @Buffer(Heap* heap) {}
                {{ordinaryAccess}} Buffer(int size) {}
                public static Buffer* Create(Heap* heap) { return new(heap) Buffer(7); }
            }
            Buffer* External(Heap* heap) { return {{expression}}; }
            """, "construction.xe");
        var compilation = Compilation.Create(source);
        if (valid)
        {
            Assert.Empty(compilation.Diagnostics);
            return;
        }

        var error = Assert.Single(compilation.Diagnostics);
        Assert.Equal(DiagnosticIds.InaccessibleSymbol, error.Id);
        Assert.Same(source, error.Location.Source);
        int start = source.Text.LastIndexOf(expression, StringComparison.Ordinal);
        Assert.InRange(error.Location.Span.Start, start, start + expression.Length - 1);
        if (contextAccess != "public")
        {
            Assert.Contains("Buffer", error.Message);
            Assert.Contains("Heap*", error.Message);
        }
    }

    [Fact]
    public void ExactPrivateOverloadCannotFallBackToPublicDefaultOrAncestor()
    {
        var compilation = Compile("""
            struct Buffer {
                public @Buffer() {}
                public @Buffer(Heap* heap) {}
                private @Buffer(Arena* arena) {}
            }
            void Use(Heap* heap, Arena* arena) {
                Buffer* system = new Buffer();
                Buffer* ancestor = new(heap) Buffer();
                Buffer* inaccessible = new(arena) Buffer();
            }
            """);
        var error = Assert.Single(compilation.Diagnostics);
        Assert.Equal(DiagnosticIds.InaccessibleSymbol, error.Id);
        Assert.Contains("Arena*", error.Message);
    }

    [Theory]
    [InlineData("public", true, true, true)]
    [InlineData("private", false, false, false)]
    [InlineData("internal", true, false, false)]
    [InlineData("protected", false, true, false)]
    [InlineData("protected internal", true, true, false)]
    public void BaseContextUsesItsDeclaringTypeAndProjectBoundary(
        string access, bool sameProjectUnrelated, bool importedDerived, bool importedUnrelated)
    {
        string declaration = $$"""
            public struct Base {
                {{access}} @Base(Heap* h) {}
                public static Base* Create(Heap* h) { return new(h) Base(); }
            }
            """;
        const string unrelated = "Base* Use(Heap* h) { return new(h) Base(); }";
        const string derived = """
            struct Derived : Base {
                public @Derived(Heap* h) {}
                public static Derived* Make(Heap* h) { return new(h) Derived(); }
            }
            """;
        var library = Compile(declaration);
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        AssertAccess(Compilation.Create(
            SourceText.From(Providers + declaration, "api.xe"),
            SourceText.From("using Api; namespace Other; " + unrelated, "other.xe")), sameProjectUnrelated);
        AssertAccess(Consumer(reference, derived), importedDerived);
        AssertAccess(Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("namespace Api; " + unrelated, "other-project.xe")), importedUnrelated);
    }

    [Theory]
    [InlineData("internal", true)]
    [InlineData("private", false)]
    public void ImportedGenericFactoryUsesItsOwnAccessContext(string access, bool genericFactoryCanConstruct)
    {
        var library = Compile($$"""
            public template Constructible { Constructible(); }
            public allocator Provider<T> : Heap {}
            public struct Buffer<T> {
                {{access}} @Buffer(Provider<T>* h) {}
                public Buffer() {}
                public static Buffer<T>* Create(Provider<T>* h) { return new(h) Buffer<T>(); }
            }
            public T* Make<T>(Provider<int>* h) where T : Constructible { return new(h) T(); }
            """);
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        AssertAccess(Consumer(reference, "Buffer<int>* Use(Provider<int>* h) { return Buffer<int>.Create(h); }"), true);
        AssertAccess(Consumer(reference, "Buffer<int>* Use(Provider<int>* h) { return new(h) Buffer<int>(); }"), false);
        AssertAccess(Consumer(reference, "Buffer<int>* Use(Provider<int>* h) { return Make<Buffer<int>>(h); }"), genericFactoryCanConstruct);
    }

    [Theory]
    [InlineData("public", "public", false, null)]
    [InlineData("private", "public", false, DiagnosticIds.InaccessibleSymbol)]
    [InlineData("internal", "public", false, DiagnosticIds.InaccessibleSymbol)]
    [InlineData("public", "private", false, DiagnosticIds.GenericConstraintNotSatisfied)]
    [InlineData("public", "public", true, null)]
    [InlineData("private", "public", true, DiagnosticIds.InaccessibleSymbol)]
    [InlineData("public", "private", true, DiagnosticIds.GenericConstraintNotSatisfied)]
    public void SourceFreeGenericLambdaChecksBothConstructorsOfConsumerType(
        string context, string ordinary, bool metadataOnly, string? expectedError)
    {
        var library = Compile("""
            public template Constructible { Constructible(); }
            public T* Make<T>(Heap* heap) where T : Constructible {
                function T*(Heap*) invoke = [](Heap* h) => { return new(h) T(); };
                return invoke(heap);
            }
            """);
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")), metadataOnly: metadataOnly);
        var consumer = Consumer(reference, $$"""
            struct Secret {
                {{context}} @Secret(Heap* h) {}
                {{ordinary}} Secret() {}
            }
            Secret* Use(Heap* h) { return Make<Secret>(h); }
            """);
        if (expectedError is null)
            Assert.Empty(consumer.Diagnostics);
        else
            Assert.Equal(expectedError, Assert.Single(consumer.Diagnostics).Id);
    }

    [Fact]
    public void LibraryKeepsOrdinaryLambdaAndSpecializesNestedGenericLambdas()
    {
        var library = Compile("""
            public template Constructible { Constructible(); }
            public int Ordinary() {
                function int() value = []() => { return 42; };
                return value();
            }
            public T* Nested<T>(Heap* heap) where T : Constructible {
                function T*(Heap*) outer = [](Heap* first) => {
                    function T*(Heap*) inner = [](Heap* second) => { return new(second) T(); };
                    return inner(first);
                };
                return outer(heap);
            }
            """);
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        string client = """
            struct Item { public @Item(Heap* heap) {} public Item() {} }
            int Main() {
                Heap* heap = new Heap();
                Item* item = Nested<Item>(heap);
                delete(item);
                delete(heap);
                return Ordinary();
            }
            """;
        var consumer = Consumer(reference, client);
        Assert.Empty(consumer.Diagnostics);
        var lambdas = consumer.GetMirFunctions().Where(f => f.Symbol.IsLambda).ToArray();
        Assert.Single(lambdas, f => f.Symbol.ReturnType == BuiltinTypes.Int);
        var specialized = lambdas.Where(f => f.Symbol.ReturnType is PointerTypeSymbol { ElementType.Name: "Item" }).ToArray();
        Assert.Equal(2, specialized.Length);
        Assert.Single(specialized.SelectMany(f => f.Blocks).Select(b => b.Terminator)
            .OfType<MirCall>(), call => call.Callee is MirFunctionOperand { Function.AllocationOperation: AllocationOperation.Construct });

        var denied = Consumer(reference, client.Replace("public @Item", "private @Item"));
        var error = Assert.Single(denied.Diagnostics);
        Assert.Equal(DiagnosticIds.InaccessibleSymbol, error.Id);
        Assert.Contains("Item", error.Message);
    }

    [Fact]
    public void ImportedGenericInitializerPointsAtClientSpecializationForInaccessibleContext()
    {
        var library = Compile("""
            public template Constructible { Constructible(); }
            public struct Holder<T> where T : Constructible { public T* value = new T(); }
            """);
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        var source = SourceText.From("""
            using Api; namespace Client;
            struct Secret { private @Secret() {} public Secret() {} }
            void Use() { delete(new Holder<Secret>()); }
            """, "client.xe");
        var consumer = Compilation.Create(new CompilationOptions(), [reference], source);
        var error = Assert.Single(consumer.Diagnostics);
        Assert.Equal(DiagnosticIds.InaccessibleSymbol, error.Id);
        Assert.Same(source, error.Location.Source);
        int start = source.Text.IndexOf("Holder<Secret>", StringComparison.Ordinal);
        Assert.InRange(error.Location.Span.Start, start, start + "Holder<Secret>".Length - 1);
    }

    [Fact]
    public void GenericDerivedFactoryCanInvokeImportedProtectedBaseContext()
    {
        var library = Compile("""
            public struct Base<T> { protected @Base(Heap* h) {} }
            """);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        AssertAccess(Consumer(reference, """
            struct Derived<T> : Base<T> {
                public @Derived(Heap* h) {}
                public static Derived<T>* Make(Heap* h) { return new(h) Derived<T>(); }
            }
            Derived<int>* Use(Heap* h) { return Derived<int>.Make(h); }
            """), true);
    }

    [Fact]
    public void ProtectedConstructorsAreAccessibleInsideTheirOwnGenericType()
    {
        var library = Compile("""
            public struct Box<T> {
                protected @Box(Heap* h) {}
                protected Box(int size) {}
                public static Box<T>* Create(Heap* h) { return new(h) Box<T>(7); }
            }
            """);
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        AssertAccess(Consumer(reference, "Box<int>* Use(Heap* h) { return Box<int>.Create(h); }"), true);
    }

    private static void AssertAccess(Compilation compilation, bool valid)
    {
        if (valid)
            Assert.Empty(compilation.Diagnostics);
        else
            Assert.Equal(DiagnosticIds.InaccessibleSymbol, Assert.Single(compilation.Diagnostics).Id);
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(Providers + source, "api.xe"));

    private static Compilation Consumer(LibraryCompilationReference reference, string source) =>
        Compilation.Create(new CompilationOptions(), [reference], SourceText.From("using Api; namespace Client; " + source, "client.xe"));
}
