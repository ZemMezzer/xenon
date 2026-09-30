using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirFeatureLoweringTests
{
    [Theory]
    [InlineData("struct Item { public int x; public ~Item() {} } void Use(Item x) {} void Main() { Use(Item()); }")]
    [InlineData("struct Item { public int x; } int Main() { unique<Item> p = new Item(); return p->x; }")]
    [InlineData("struct Item { public int x; } int Main() { shared<Item> p = new Item(); weak<Item> w = p; shared<Item> q = lock w; return 0; }")]
    [InlineData("int Main() { int* p = new int(7); int x = *p; delete(p); return x; }")]
    [InlineData("int Main() { int x = 2; function int(int) add = [x](int y) => { return x + y; }; return add(4); }")]
    [InlineData("struct Item { private int x; public int Value { get { return x; } set { x = value; } } } int Main() { Item x = Item(); x.Value = 3; x.Value += 2; return x.Value; }")]
    [InlineData("int Main() { atomic<int> x = 1; x++; x += 3; x = 7; return x; }")]
    [InlineData("bool Use(atomic<int>& x) { return x : 0 --> 1; }")]
    [InlineData("int* Use(int* x) { x++; x += 2; return x; }")]
    [InlineData("struct Item { public int x; public Item(int value) { x = value; } } void Main() { storage<Item> s; s = Item(2); }")]
    [InlineData("void Use(atomic<int>& x, int& y) { x <-> y; y <-> x; }")]
    [InlineData("readonly int* Use(int* x) { readonly int* y = x; return y; }")]
    [InlineData("void Read(readonly int* x) {} void Use(int* x) { Read(x); }")]
    [InlineData("struct Base {} struct Derived : Base {} Base* Use(Derived* x) { Base* y = x; return y; }")]
    [InlineData("struct Base {} struct Derived : Base {} void Read(Base& x) {} void Use() { Derived x = Derived(); Read(x); }")]
    [InlineData("struct State { public function void() next; } void Use() { State s = State(); }")]
    public void ExecutableFunctionsLowerWithoutBoundPayloads(string program)
    {
        foreach (MirFunction function in Lower(program))
        {
            Assert.Empty(MirVerifier.Verify(function));
            Assert.NotEmpty(MirPrinter.Dump(function));
        }
    }

    [Fact]
    public void FullExpressionHasGuardedNormalAndUnwindDrops()
    {
        MirFunction function = Lower("struct Item { public ~Item() {} public int Read() { return 1; } } int Main() { return Item().Read(); }").Single(f => f.Symbol.Name == "Main");
        Assert.Contains(function.Blocks, block => block.Terminator is MirDrop);
        Assert.Contains(function.Blocks, block => block.Terminator is MirAbort);
        Assert.Contains(function.Blocks, block => block.Terminator is MirSwitch);
    }

    private const string Protocol = """
        struct Result {
            public static void operator resolve(Result& target, int value) {}
            public static void operator resolve(Result& target) {}
            public static void operator reject(Result& target, int error) {}
        }
        struct Operation {
            public static bool operator await(readonly Operation& value, storage<int>& result, function void() continuation)
            { result = 42; return true; }
        }
        """;

    [Theory]
    [InlineData("return 1;", false)]
    [InlineData("int value = await Operation(); return value;", true)]
    [InlineData("try { return await Operation(); } finally { int x = 1; }", true)]
    [InlineData("await Operation(); throw 1;", true)]
    public void AsyncBodiesExposeCompletionAndResumeEdges(string body, bool suspends)
    {
        MirFunction function = Lower(Protocol + "async Result Use() { " + body + " }").Single(f => f.Symbol.Name == "Use");
        Assert.Equal(suspends, function.Blocks.Any(block => block.Terminator is MirSuspend));
        Assert.Contains(function.Blocks, block => block.Terminator is MirReturn);
        Assert.Contains(function.Blocks, block => block.Terminator is MirCall { Callee: MirFunctionOperand });
    }

    internal static MirFunction[] Lower(string program)
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace MirFeatures; " + program));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        return compilation.SemanticModel.Functions.Select(function => MirLowerer.Lower(function, compilation.SemanticModel.TypeFactory)).ToArray();
    }
}
