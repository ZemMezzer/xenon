using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.CodeGen;

public sealed class StableLayoutTests
{
    private const string Library = """
        namespace Layout;
        struct Scalar { public int Value; }
        struct Base { public byte A; public long B; public int C; }
        struct Middle : Base { public byte Tail; }
        enum Measurements
        {
            ScalarSize = cast<int>(sizeof(Scalar)), ScalarAlign = cast<int>(alignof(Scalar)),
            ScalarOffset = cast<int>(offsetof(Scalar, Value)),
            BaseSize = cast<int>(sizeof(Base)), BaseAlign = cast<int>(alignof(Base)),
            A = cast<int>(offsetof(Base, A)), B = cast<int>(offsetof(Base, B)), C = cast<int>(offsetof(Base, C)),
            MiddleSize = cast<int>(sizeof(Middle)), MiddleAlign = cast<int>(alignof(Middle)),
            Inherited = cast<int>(offsetof(Middle, C)), Tail = cast<int>(offsetof(Middle, Tail))
        }
        export int Process(Base* value) { value->C += 1; return value->C; }
        Base RoundTrip(Base value) { return value; }
        """;

    [Theory]
    [InlineData("i686-pc-windows-msvc")]
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("x86_64-unknown-linux-gnu")]
    public void Layout_IsIndependentOfDescendantsAndSourceOrder(string triple)
    {
        var target = new LlvmTargetOptions(triple);
        Compilation baseline = Bind(target, Library);
        int[] expected = Measurements(baseline);
        Assert.Equal(new[] { 4, 4, 0 }, expected.Take(3));
        // The base's tail padding belongs to the base, never to a descendant.
        Assert.Equal(expected[3], expected[11]);
        string baselineIr = new LlvmIrGenerator().GenerateForTarget(baseline, target);

        foreach (string descendants in new[]
        {
            "struct Derived : Middle { public int Extra; } struct ScalarChild : Scalar { }",
            "interface IFoo { int Foo(); } struct Derived : Middle, IFoo { public int Foo() { return C; } } struct ScalarChild : Scalar, IFoo { public int Foo() { return Value; } }",
            "struct Derived : Middle { public virtual int Foo() { return C; } } struct Leaf : Derived { public override int Foo() { return 42; } } struct ScalarChild : Scalar { public virtual int Foo() { return Value; } }",
            "struct Derived : Middle { public virtual ~Derived() { } } struct ScalarChild : Scalar { public virtual ~ScalarChild() { } }",
        })
        {
            string downstream = "namespace Layout; " + descendants;
            foreach (string[] sources in new[] { new[] { Library, downstream }, new[] { downstream, Library } })
            {
                Compilation compilation = Bind(target, sources);
                Assert.Equal(expected, Measurements(compilation));
                StructTypeSymbol[] types = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Structs.ToArray();
                foreach (string name in new[] { "Scalar", "Base", "Middle" })
                {
                    StructTypeSymbol type = Assert.Single(types, type => type.Name == name);
                    Assert.False(type.HasVirtualDispatch);
                    Assert.Null(type.DispatchStorageOwner);
                }
                string ir = new LlvmIrGenerator().GenerateForTarget(compilation, target);
                // Independently compiled library declarations and by-value signatures stay identical.
                foreach (string line in baselineIr.Split('\n').Where(line => line.StartsWith("%Layout.") ||
                    line.StartsWith("define i32 @Layout_Process") || line.StartsWith("define internal %Layout.Base @Layout.RoundTrip")))
                    Assert.Contains(line, ir, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Layout_DispatchStorageBelongsToFirstPolymorphicDeclaration()
    {
        Compilation compilation = Bind(new LlvmTargetOptions("x86_64-pc-windows-msvc"), """
            namespace Layout;
            struct Leaf : Poly { public override int Read() { return Value + 1; } }
            struct Poly : Root, IValue { public int Value; public virtual int Read() { return Value; } }
            interface IValue { int Read(); }
            struct Root { public long Guard; public byte Tag; }
            enum Measurements { RootSize = cast<int>(sizeof(Root)), PolyValue = cast<int>(offsetof(Poly, Value)), LeafValue = cast<int>(offsetof(Leaf, Value)) }
            """);
        StructTypeSymbol[] types = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Structs.ToArray();
        StructTypeSymbol root = Assert.Single(types, type => type.Name == "Root");
        StructTypeSymbol poly = Assert.Single(types, type => type.Name == "Poly");
        StructTypeSymbol leaf = Assert.Single(types, type => type.Name == "Leaf");
        Assert.False(root.HasVirtualDispatch);
        Assert.True(poly.IntroducesVirtualDispatch);
        Assert.False(leaf.IntroducesVirtualDispatch);
        Assert.Same(poly, leaf.DispatchStorageOwner);
        Assert.Equal(new[] { 16, 24, 24 }, Measurements(compilation));
    }

    private static Compilation Bind(LlvmTargetOptions target, params string[] sources)
    {
        Compilation compilation = Compilation.Create(sources.Select((source, index) => SourceText.From(source, $"unit{index}.xe")).ToArray());
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        Compilation bound = LlvmIrGenerator.BindForTarget(compilation, target);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        return bound;
    }

    private static int[] Measurements(Compilation compilation) =>
        Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Enums)
            .Members.Select(member => (int)member.Value!).ToArray();
}
