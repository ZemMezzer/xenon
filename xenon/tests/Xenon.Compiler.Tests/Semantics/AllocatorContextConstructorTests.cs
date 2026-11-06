using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class AllocatorContextConstructorTests
{
    private const string Heap = """
        namespace Contexts;
        allocator Heap {
            +Heap(nuint size, nuint alignment) { return malloc(size, alignment); }
            -Heap(void* block) { free(block); }
        }
        """;

    [Fact]
    public void ParserSeparatesAllFourAllocatorMembers()
    {
        SyntaxTree tree = SyntaxTree.Parse(SourceText.From("""
            namespace Contexts;
            allocator Arena {
                @Arena(Heap* parent) {}
                public Arena() {}
                +Arena(nuint size, nuint alignment) { return malloc(size, alignment); }
                -Arena(void* block) { free(block); }
            }
            """));
        Assert.Empty(tree.Diagnostics);
        var type = Assert.IsType<StructDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var context = Assert.IsType<MethodDeclarationSyntax>(type.Members[0]);
        Assert.Equal(SyntaxKind.AtToken, context.AllocationContextToken!.Kind);
        Assert.Null(context.AllocationOperatorToken);
        Assert.Null(context.VirtualKeyword);
        Assert.Equal("$allocationConstructor", context.IdentifierToken.Text);
        Assert.IsType<ConstructorDeclarationSyntax>(type.Members[1]);
        var allocate = Assert.IsType<MethodDeclarationSyntax>(type.Members[2]);
        var release = Assert.IsType<MethodDeclarationSyntax>(type.Members[3]);
        Assert.Null(allocate.AllocationContextToken);
        Assert.Equal(SyntaxKind.PlusToken, allocate.AllocationOperatorToken!.Kind);
        Assert.Equal("$allocate", allocate.IdentifierToken.Text);
        Assert.Equal(SyntaxKind.MinusToken, release.AllocationOperatorToken!.Kind);
        Assert.Equal("$deallocate", release.IdentifierToken.Text);
    }

    [Theory]
    [InlineData("allocator Arena : Heap { public @Arena(Heap* parent) {} } void Use(Heap* h) { Arena* a = new(h) Arena(); delete(a); }")]
    [InlineData("allocator Arena : Heap {} allocator Pool : Heap { public @Pool(Heap* p) {} } void Use(Arena* h) { Pool* p = new(h) Pool(); delete(p); }")]
    [InlineData("allocator Arena : Heap {} void Use() { Arena* p = new Arena(); delete(p); }")]
    [InlineData("allocator Arena : Heap { public @Arena(Heap* p) {} } allocator Pool : Arena { public @Pool(Arena* p) {} } void Use(Arena* h) { Pool* p = new(h) Pool(); delete(p); }")]
    [InlineData("allocator Arena<T> : Heap { public @Arena(Heap* parent) {} } void Use(Heap* h) { Arena<int>* a = new(h) Arena<int>(); delete(a); }")]
    public void AllocatorContextUsesExistingConstructionAndMirRules(string source)
    {
        Compilation compilation = Compile(source);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        foreach (MirFunction function in compilation.GetMirFunctions()) Assert.Empty(MirVerifier.Verify(function));
    }

    [Theory]
    [InlineData("struct Value { +Value(Heap* h) {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Arena : Heap { +Arena(Heap* h) {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Arena : Heap { @Arena(Heap* h) {} } void Use() { Arena* a = new Arena(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("allocator Arena : Heap {} allocator Pool : Heap { @Pool(Arena* h) {} } void Use(Heap* h) { Pool* p = new(h) Pool(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("allocator Arena : Heap { @Arena(Heap* h) {} } void Use(Heap* h) { Arena a = Arena(); }", DiagnosticIds.AllocatorRequiresHeap)]
    [InlineData("allocator Arena : Heap { @Arena(int h) {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Arena : Heap { @Arena(readonly Heap* h) {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Arena : Heap { @Arena(Heap* h, int other) {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Arena : Heap { @Wrong(Heap* h) {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Arena : Heap { public virtual @Arena(Heap* h) {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("abstract allocator Arena : Heap { public abstract @Arena(Heap* h); }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Arena : Heap { private @Arena(Heap* h) {} } void Use(Heap* h) { Arena* a = new(h) Arena(); }", DiagnosticIds.InaccessibleSymbol)]
    [InlineData("interface Provider {} allocator Arena : Heap, Provider {} void Use(Provider* provider) { int* p = new(provider) int(); }", DiagnosticIds.InvalidAllocatorOperand)]
    public void InvalidContextsDoNotFallBackToAllocationOperators(string source, string diagnostic)
        => Assert.Contains(Compile(source).Diagnostics, item => item.Id == diagnostic);

    [Theory]
    [InlineData("struct Value { &Value(Value& other) {} }")]
    [InlineData("struct Value { &&Value(Value& other) {} }")]
    [InlineData("allocator Arena : Heap { @Arena(Heap* h) {} } void Use(Arena* a, Heap* h) { a->@Arena(h); }")]
    public void ReservedAndDirectContextCallSyntaxRemainsUnavailable(string source)
        => Assert.True(Compile(source).HasErrors);

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(Heap + source));
}
