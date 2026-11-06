using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class StrictAllocatorPhaseTests
{
    private const string Prelude = """
        namespace Phases;
        allocator Heap {
            public int calls;
            +Heap(nuint size, nuint alignment) { return malloc(size, alignment); }
            -Heap(void* block) { free(block); }
        }
        void Observe(Item* item) {}
        void Record(Heap* heap) { heap->calls++; }
        """;

    private static Compilation Compile(string member) => Compilation.Create(SourceText.From(Prelude + """
        struct Item {
            public allocator Heap* provider;
            private int ordinary;
            public int Prop { get { return ordinary; } set { ordinary = value; } }
            public void Method() {}
            public static void Notify(Heap* heap) { Record(heap); }
            public static void Inspect(Item* item) {}
        """ + member + "}"));

    [Theory]
    [InlineData("int n = ordinary;")]
    [InlineData("ordinary = 3;")]
    [InlineData("ordinary++;")]
    [InlineData("int n = Prop;")]
    [InlineData("Prop = 3;")]
    [InlineData("Method();")]
    [InlineData("this.Method();")]
    [InlineData("Observe(this);")]
    [InlineData("Item.Inspect(this);")]
    [InlineData("Item* alias = this;")]
    [InlineData("return this;")]
    [InlineData("Heap* copy = provider;")]
    [InlineData("Record(this.provider);")]
    [InlineData("provider->calls++;")]
    public void ContextRejectsAccessToIncompleteInstance(string body)
    {
        var compilation = Compile($"@Item(Heap* h) {{ {body} }}");
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.InvalidAllocatorPhaseAccess && d.Location.Span.Length > 0);
    }

    [Theory]
    [InlineData("public Item() { provider = null; }")]
    [InlineData("public void Update() { this.provider = null; }")]
    [InlineData("public static void Update(Item* other) { other->provider = null; }")]
    public void OnlyOwnerContextCanWriteField(string member) =>
        Assert.Contains(Compile(member).Diagnostics, d => d.Id == DiagnosticIds.AllocatorFieldWriteOutsidePhase);

    [Theory]
    [InlineData("allocator Heap* Other { get { return null; } }")]
    [InlineData("allocator void Other() {}")]
    [InlineData("void Other(allocator Heap* arg) {}")]
    [InlineData("void Other() { allocator Heap* local; }")]
    [InlineData("allocator readonly Heap* bad;")]
    [InlineData("allocator const Heap* bad = null;")]
    [InlineData("const allocator Heap* bad = null;")]
    [InlineData("static allocator Heap* bad;")]
    [InlineData("allocator Heap* bad = null;")]
    public void InvalidModifierHasLocatedDiagnostic(string member) =>
        Assert.Contains(Compile(member).Diagnostics, d => d.Id == DiagnosticIds.InvalidAllocatorField && d.Location.Span.Length > 0);

    [Theory]
    [InlineData("allocator struct Value {}")]
    [InlineData("allocator interface IValue {}")]
    [InlineData("allocator void Run() {}")]
    public void TopLevelAllocatorModifierIsNotAnAllocatorDeclaration(string declaration)
    {
        var compilation = Compilation.Create(SourceText.From("namespace Phases; " + declaration));
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.InvalidAllocatorField && d.Location.Span.Length == 9);
    }

    [Fact]
    public void RestrictedReceiverDoesNotRestrictExternalEffects()
    {
        var compilation = Compile("""
            private const int Capacity = 4;
            @Item() {}
            @Item(Heap* allocator) {
                this.provider = null;
                for (int i = 0; i < Capacity; i++) {
                    Item.Notify(allocator);
                    if (i > 1) this.provider = allocator;
                }
                Record(allocator);
            }
            public Heap* Get() { return provider; }
            public Item() { if (provider != null) provider->calls++; }
            """);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
    }
}
