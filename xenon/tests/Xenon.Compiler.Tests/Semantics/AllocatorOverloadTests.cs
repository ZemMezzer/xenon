using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class AllocatorOverloadTests
{
    private const string Providers = """
        namespace Overloads;
        allocator Heap {
            +Heap(nuint size, nuint alignment) { return malloc(size, alignment); }
            -Heap(void* block) { free(block); }
        }
        allocator Arena : Heap {}
        allocator Pool : Arena {}
        allocator Other : Heap {}
        """;

    [Theory]
    [InlineData("struct")]
    [InlineData("allocator")]
    public void ContextOverloadsUseFullNominalIdentityAndStoreTheSelectedMirCallee(string kind)
    {
        Compilation compilation = Compile($$"""
            {{kind}} Value{{(kind == "allocator" ? " : Heap" : "")}} {
                public @Value(Heap* h) {}
                public @Value(Arena* h) {}
                public @Value(Other* h) {}
            }
            void Exact(Arena* h) { Value* v = new(h) Value(); delete(v); }
            void Ancestor(Pool* h) { Value* v = new(h) Value(); delete(v); }
            void Erased(Heap* h) { Value* v = new(h) Value(); delete(v); }
            void Array(Pool* h) { Value[] v = new(h) Value[3]; delete(v); }
            void Pointers(Heap* h) { Value*[] v = new(h) Value*[3]; delete(v); }
            """);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        var functions = compilation.GetMirFunctions().ToArray();
        foreach (MirFunction function in functions) Assert.Empty(MirVerifier.Verify(function));
        foreach (var (name, expected) in new[] { ("Exact", "Arena"), ("Ancestor", "Arena"), ("Erased", "Heap"), ("Array", "Arena") })
        {
            MirFunction function = functions.Single(f => f.Symbol.Name == name);
            FunctionSymbol selected = Assert.Single(ContextCalls(function));
            Assert.Equal(expected, Assert.IsType<PointerTypeSymbol>(selected.Parameters[0].Type).ElementType.Name);
        }
        Assert.Empty(ContextCalls(functions.Single(f => f.Symbol.Name == "Pointers")));
    }

    [Theory]
    [InlineData("@Value(Arena* h) {} @Value(Other* h) {}", "void Use(Heap* h) { Value* v = new(h) Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("@Value(Arena* h) {} @Value(Other* h) {}", "void Use(Heap* h) { Value[] v = new(h) Value[2]; }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("@Value(Heap* first) {} @Value(Heap* second) {}", "", DiagnosticIds.InvalidOverload)]
    [InlineData("public @Value(Heap* h) {} private @Value(Arena* h) {}", "void Use(Arena* h) { Value* v = new(h) Value(); }", DiagnosticIds.InaccessibleSymbol)]
    [InlineData("public @Value(Heap* h) {} private @Value(Arena* h) {}", "void Use(Arena* h) { Value[] v = new(h) Value[2]; }", DiagnosticIds.InaccessibleSymbol)]
    [InlineData("@Value(Heap* h) {} @Value(Arena* h, int value) {}", "", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("@Value(Heap* h) {} @Value(Provider* h) {}", "", DiagnosticIds.InvalidAllocatorDeclaration)]
    public void InvalidOverloadSetsNeverFallBackToAnArbitraryCandidate(string members, string use, string diagnostic)
    {
        foreach (string kind in new[] { "struct", "allocator" })
            Assert.Contains(Compile($"interface Provider {{}} {kind} Value{(kind == "allocator" ? " : Heap" : "")} {{ {members} }} {use}").Diagnostics,
                item => item.Id == diagnostic);
    }

    [Fact]
    public void EveryInheritanceLevelMustHaveACompatibleOverload()
    {
        foreach (string kind in new[] { "struct", "allocator" })
            Assert.Contains(Compile($$"""
                {{kind}} Parent{{(kind == "allocator" ? " : Heap" : "")}} { public @Parent(Other* h) {} }
                {{kind}} Child : Parent { public @Child(Heap* h) {} public @Child(Arena* h) {} }
                void Use(Arena* h) { Child[] values = new(h) Child[2]; }
                """).Diagnostics, item => item.Id == DiagnosticIds.AllocationConstructorMismatch);
    }

    [Theory]
    [InlineData("struct", false)] [InlineData("allocator", false)]
    [InlineData("struct", true)] [InlineData("allocator", true)]
    public void GenericSubstitutionRejectsCollidingContextSignatures(string kind, bool library)
    {
        Compilation definition = Compile($$"""
            public allocator Provider<T> : Heap {}
            public {{kind}} Value<T>{{(kind == "allocator" ? " : Heap" : "")}} {
                public @Value(Provider<T>* h) {}
                public @Value(Provider<int>* h) {}
            }
            """);
        Assert.False(definition.HasErrors, string.Join("\n", definition.Diagnostics));
        string consumer = "void Use(Provider<int>* h) { Value<int>* v = new(h) Value<int>(); }";
        Compilation collision = library
            ? Compilation.Create(new CompilationOptions(), [XelibReader.Read(XelibWriter.Write(definition, new XelibWriteOptions("Overloads")))],
                SourceText.From("using Overloads; namespace Consumer; " + consumer))
            : Compile($$"""
                allocator Provider<T> : Heap {}
                {{kind}} Value<T>{{(kind == "allocator" ? " : Heap" : "")}} {
                    public @Value(Provider<T>* h) {}
                    public @Value(Provider<int>* h) {}
                }
                {{consumer}}
                """);
        Assert.Contains(collision.Diagnostics, item => item.Id == DiagnosticIds.InvalidOverload);
        Assert.Contains(collision.Diagnostics, item => item.Id == DiagnosticIds.AmbiguousCall);
    }

    [Theory]
    [InlineData("struct")]
    [InlineData("allocator")]
    public void DistinctGenericSignaturesAndPointerArraysRemainValid(string kind)
    {
        Compilation compilation = Compile($$"""
            allocator Provider<T> : Heap {}
            {{kind}} Value<T>{{(kind == "allocator" ? " : Heap" : "")}} {
                public @Value(Provider<T>* h) {}
                public @Value(Provider<int>* h) {}
            }
            void Use(Provider<byte>* h, Heap* unrelated) {
                Value<byte>* v = new(h) Value<byte>(); delete(v);
                Value<byte>*[] pointers = new(unrelated) Value<byte>*[2]; delete(pointers);
            }
            """);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
    }

    private static IEnumerable<FunctionSymbol> ContextCalls(MirFunction function) =>
        function.Blocks.Select(b => b.Terminator).OfType<MirCall>()
            .Select(c => c.Callee).OfType<MirFunctionOperand>()
            .Select(c => c.Function).Where(f => f.AllocationOperation == AllocationOperation.Construct);

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(Providers + source));
}
