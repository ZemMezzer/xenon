using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class AllocatorFieldTypeTests
{
    private const string Types = """
        public abstract allocator Root {}
        public abstract allocator Pool : Root {}
        public allocator Heap : Pool {
            +Heap(nuint size, nuint alignment) { return malloc(size, alignment); }
            -Heap(void* block) { free(block); }
        }
        public allocator Arena<T> : Heap {}
        public struct Wrapper { public Heap* provider; }
        public interface IProvider {}
        public struct HeapAllocator {}
        """;

    [Theory]
    [InlineData("int")]
    [InlineData("bool")]
    [InlineData("float")]
    [InlineData("char*")]
    [InlineData("void*")]
    [InlineData("Wrapper")]
    [InlineData("Wrapper*")]
    [InlineData("HeapAllocator*")]
    [InlineData("IProvider*")]
    [InlineData("Heap")]
    [InlineData("Heap**")]
    [InlineData("Heap*[]")]
    [InlineData("storage<Heap*>")]
    [InlineData("atomic<int>")]
    [InlineData("T")]
    [InlineData("T*")]
    [InlineData("Alias*")]
    public void InvalidFieldIsDiagnosedAtDeclaration(string type)
    {
        var source = SourceText.From("using Alias = App.Wrapper; namespace App; " + Types +
            $"struct Holder<T> {{ private allocator {type} context; }}");
        var compilation = Compilation.Create(source);
        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(DiagnosticIds.InvalidAllocatorFieldType, diagnostic.Id);
        Assert.Contains("'context'", diagnostic.Message);
        Assert.Same(source, diagnostic.Location.Source);
        int typeStart = source.Text.LastIndexOf(type + " context;", StringComparison.Ordinal);
        Assert.Equal(typeStart, diagnostic.Location.Span.Start);
        Assert.InRange(diagnostic.Location.Span.Length, 1, type.Length);
    }

    [Theory]
    [InlineData("Missing*", DiagnosticIds.UnknownType)]
    [InlineData("int", DiagnosticIds.InvalidAllocatorFieldType)]
    public void InvalidFieldDoesNotCauseSecondaryInitializationErrors(string type, string diagnosticId)
    {
        var source = SourceText.From($$"""
            namespace App;
            struct Holder {
                private allocator {{type}} context;
                @Holder() { context = null; }
                public Holder() { if (context != null) {} }
            }
            """);
        Assert.Equal(diagnosticId, Assert.Single(Compilation.Create(source).Diagnostics).Id);
    }

    [Fact]
    public void AllocatorTypesResolveAcrossAliasesInheritanceAndSpecialization()
    {
        var compilation = Compilation.Create(SourceText.From("using H = App.Heap; namespace App; " + Types + """
            struct Holder<T> {
                private allocator Root* root;
                private allocator Pool* pool;
                private allocator H* heap;
                private allocator Arena<T>* arena;
                public @Holder() {}
                public @Holder(Arena<T>* provider) {
                    root = provider;
                    pool = provider;
                    heap = provider;
                    arena = provider;
                }
                public Holder() { if (root != null) { Heap* value = heap; } }
            }
            void Use(Arena<int>* provider) {
                Holder<int> empty = Holder<int>();
                Holder<int>* value = new(provider) Holder<int>();
                delete(value);
            }
            """));
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("H*", true)]
    [InlineData("A<int>*", true)]
    [InlineData("W*", false)]
    public void ResolvedImportedTypeControlsEligibility(string type, bool valid)
    {
        var library = Compilation.Create(SourceText.From("namespace Api; " + Types));
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        var consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using A = Api.Arena; using H = Api.Heap; using W = Api.Wrapper; namespace App; " +
            $"struct Holder {{ public allocator {type} context; }}"));
        if (valid)
            Assert.Empty(consumer.Diagnostics);
        else
            Assert.Equal(DiagnosticIds.InvalidAllocatorFieldType, Assert.Single(consumer.Diagnostics).Id);
    }

    [Fact]
    public void ImportedGenericFieldKeepsItsSpecializedAllocatorType()
    {
        var library = Compilation.Create(SourceText.From("namespace Api; " + Types + """
            public struct Holder<T> {
                public allocator Arena<T>* context;
                public @Holder(Arena<T>* provider) { context = provider; }
            }
            """));
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        var consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Api; namespace App;
            void Use(Arena<int>* provider) {
                Holder<int>* value = new(provider) Holder<int>();
                Arena<int>* stored = value->context;
                delete(value);
            }
            """));
        Assert.Empty(consumer.Diagnostics);
    }
}
