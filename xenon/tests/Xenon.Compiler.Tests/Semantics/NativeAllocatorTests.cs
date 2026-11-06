using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class NativeAllocatorTests
{
    private const string Heap = """
        allocator Heap {
            public int allocations;
            public int releases;
            +Heap(nuint size, nuint alignment) { allocations++; return malloc(size, alignment); }
            -Heap(void* ptr) { releases++; free(ptr); }
        }
        """;

    private static Compilation Compile(string code) => Compilation.Create(SourceText.From("namespace Allocators; " + Heap + code));

    [Theory]
    [InlineData("void Use() { Heap* h = new Heap(); int* p = new(h) int(3); delete(p); delete(h); }")]
    [InlineData("allocator Child : Heap {} void Use() { Heap* h = new Child(); int[] a = new(h) int[5]; delete(a); delete(h); }")]
    [InlineData("void Use() { Heap* h = new Heap(); Heap* other = new(h) Heap(); delete(other); delete(h); }")]
    [InlineData("struct Value { public allocator Heap* context; public int x; public @Value(Heap* h) { context = h; } public Value(int y) { x = context->allocations + y; } } void Use() { Heap* h = new Heap(); Value* v = new(h) Value(2); delete(v); delete(h); }")]
    [InlineData("struct Value { public allocator Heap* parent; public @Value(Heap* h) { parent = h; } } void Use() { Heap* h = new Heap(); Value[] a = new(h) Value[3]; delete(a); delete(h); }")]
    [InlineData("struct Value { @Value(Heap* h) {} } void Use() { Heap* h = new Heap(); Value*[] a = new(h) Value*[3]; delete(a); delete(h); }")]
    [InlineData("abstract allocator Abstract { public abstract +Abstract(nuint s, nuint a); public abstract -Abstract(void* p); } allocator Concrete : Abstract { +Concrete(nuint s, nuint a) { return malloc(s,a); } -Concrete(void* p) { free(p); } } void Use(Abstract* h) { int* p = new(h) int(); delete(p); }")]
    [InlineData("allocator Concrete : Empty { +Concrete(nuint s, nuint a) { return malloc(s,a); } -Concrete(void* p) { free(p); } } abstract allocator Empty {} void Use(Concrete* h) { int* p = new(h) int(); delete(p); }")]
    public void ValidAllocationsHaveExecutableMir(string code)
    {
        Compilation compilation = Compile(code);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        foreach (MirFunction function in compilation.GetMirFunctions()) Assert.Empty(MirVerifier.Verify(function));
        Assert.Contains(compilation.GetMirFunctions().SelectMany(f => f.Blocks), b => b.Terminator is MirIntrinsicCall { Function.AllocationOperation: AllocationOperation.Allocate });
    }

    [Theory]
    [InlineData("void Use() { Heap value = Heap(); }", DiagnosticIds.AllocatorRequiresHeap)]
    [InlineData("void Use() { Heap value; }", DiagnosticIds.AllocatorRequiresHeap)]
    [InlineData("void Use() { int* p = new(1) int(); }", DiagnosticIds.InvalidAllocatorOperand)]
    [InlineData("struct Other {} void Use(Other* a) { int* p = new(a) int(); }", DiagnosticIds.InvalidAllocatorOperand)]
    [InlineData("struct Child : Heap {}", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("struct Base {} allocator Child : Base {}", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Bad { +Bad(int size, int align) { return malloc(1); } -Bad(void* ptr) { free(ptr); } }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("allocator Bad { +Bad(nuint size, nuint align) { return malloc(size); } }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("struct Value { @Value(Heap* h) {} } void Use() { Value* v = new Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value(Heap* h) {} } void Use() { Value v = Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value(int h) {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("struct Value { private @Value(Heap* h) {} } void Use(Heap* h) { Value* v = new(h) Value(); }", DiagnosticIds.InaccessibleSymbol)]
    [InlineData("struct Value { @Value(Heap* h) {} } struct Holder { public Value value; } void Use() { Holder value = Holder(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value(Heap* h) {} } void Use() { atomic<Value>[] values = new atomic<Value>[1]; }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("void Use(Heap* h) { int* p = new(h) int(7); void* alias = cast<void*>(p); free(alias); }", DiagnosticIds.InvalidFreeOperand)]
    [InlineData("void Use(Heap* h) { free(new(h) int(7)); }", DiagnosticIds.InvalidFreeOperand)]
    [InlineData("void Use(Heap* h) { free(new(h) int[3]); }", DiagnosticIds.InvalidFreeOperand)]
    [InlineData("void Use() { int* p = cast<int*>(malloc(4)); delete(p); }", DiagnosticIds.InvalidFreeOperand)]
    [InlineData("struct Value { public allocator Heap* parent; public @Value(Heap* h) { parent = h; } } struct Holder { public Value inner; public Holder() {} } void Use() { Holder h = Holder(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Holder { public atomic<Heap> heap; }", DiagnosticIds.AllocatorRequiresHeap)]
    [InlineData("struct Inner { public int x; public @Inner(Heap* h) {} } struct Holder { public Inner inner; public Holder() { inner.x=9; } } void Use() { Holder h=Holder(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("abstract allocator Empty {} void Use(Empty* h) { int* p = new(h) int(); }", DiagnosticIds.InvalidAllocatorOperand)]
    [InlineData("abstract struct Value { public abstract @Value(Heap* h); }", DiagnosticIds.InvalidAllocatorDeclaration)]
    public void InvalidAllocationsExplainTheirConstraint(string code, string diagnostic)
        => Assert.Contains(Compile(code).Diagnostics, d => d.Id == diagnostic);

    [Theory]
    [InlineData("+Bad(nuint s, nuint a) { State.value++; return malloc(s,a); } -Bad(void* p) { free(p); }", "void readonly Use(Bad* h) { int* p = new(h) int(); delete(p); }")]
    [InlineData("+Bad(nuint s, nuint a) { return malloc(s,a); } -Bad(void* p) { State.value++; free(p); }", "void readonly Use(int* p) { delete(p); }")]
    public void ReadonlyChecksAllocatorCallbackBodies(string operations, string use)
    {
        Compilation compilation = Compile("static struct State { public static int value; } allocator Bad { " + operations + " } " + use);
        Assert.Contains(compilation.Diagnostics, d => d.Id == "XE2129");
    }

    [Theory]
    [InlineData("void Use() { int* value = new int(7); void* alias = cast<void*>(value); free(alias); }")]
    [InlineData("void Use() { int[] values = new int[3]; free(values); }")]
    [InlineData("struct Resource { public ~Resource() {} } void Use() { Resource* value = new Resource(); destruct(*value); free(value); }")]
    [InlineData("void Use() { free(new int(7)); free(new int[3]); }")]
    public void BuiltinAllocationsPreserveStorageOnlyRelease(string code)
        => Assert.False(Compile(code).HasErrors);

    [Fact]
    public void ReadonlyPermitsExplicitAllocatorAndOrdinaryAllocations()
    {
        Compilation compilation = Compile("""
            static struct State { public static int value; }
            allocator Bad { +Bad(nuint s, nuint a) { State.value++; return malloc(s,a); } -Bad(void* p) { State.value++; free(p); } }
            void readonly Use(Heap* h) { int* p = new(h) int(); delete(p); }
            void readonly Ordinary() { int* p = new int(); delete(p); }
            """);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
    }

    [Fact]
    public void UnrelatedThrowingAllocatorDoesNotPolluteAsyncEffects()
    {
        Compilation compilation = Compile("""
            allocator Other { +Other(nuint size, nuint alignment) { throw 7; } -Other(void* p) { free(p); throw 8; } }
            struct Ready { public static void operator resolve(Ready& r) {} }
            async Ready Use(Heap* h) { int* p = new(h) int(1); delete(p); }
            """);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
    }

    [Fact]
    public void SourceFreeGenericConstructionChecksAllocationAccess()
    {
        Compilation library = Compile("""
            public template Constructible { Constructible(); }
            public T* Make<T>(Heap* h) where T : Constructible { return new(h) T(); }
            """);
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Allocators")));
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Allocators;
            namespace App;
            struct Value { public Value() {} private @Value(Heap* h) {} }
            void Use(Heap* h) { Value* value = Make<Value>(h); }
            """));
        var diagnostic = Assert.Single(consumer.Diagnostics.Where(d => d.Id == DiagnosticIds.InaccessibleSymbol));
        Assert.NotNull(diagnostic.Location.Source);
        Assert.Contains("inaccessible", diagnostic.ToString());
    }

    [Theory]
    [InlineData("i686-unknown-linux-gnu")]
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("aarch64-unknown-linux-gnu")]
    public void CodeGenerationUsesTargetSizedHeadersAndPreservesLayout(string triple)
    {
        var target = new LlvmTargetOptions(triple);
        Compilation compilation = LlvmIrGenerator.BindForTarget(Compile("""
            struct Value { public allocator Heap* parent; public @Value(Heap* h) { parent = h; } }
            export int Use(Heap* h) { Value* v = new(h) Value(); int x = v->parent->allocations; delete(v); return x; }
            """), target);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, target);
        Assert.Contains("allocator.release", ir);
        Assert.Contains("free.callback", ir);
        Assert.Contains("%Allocators.Value = type { ptr }", ir);
    }

    [Fact]
    public void ImportedAllocatorFieldBindingsRemainImmutableAfterSpecialization()
    {
        var library = Compile("""
            public struct Box<T> {
                public allocator Heap* provider;
                public allocator Heap* alternate;
                public @Box(Heap* h) { provider = h; alternate = null; }
            }
            """);
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Allocators")));
        Compilation Bind(string body) => Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("using Allocators; namespace App; void Use(Box<int>* box) { " + body + " }"));
        var legal = Bind("box->provider->releases++; Heap* selected = box->alternate;");
        Assert.False(legal.HasErrors, string.Join("\n", legal.Diagnostics));
        foreach (string assignment in new[] { "box->provider = null;", "box->alternate = null;" })
            Assert.Contains(Bind(assignment).Diagnostics, d => d.Id == DiagnosticIds.AllocatorFieldWriteOutsidePhase);
    }

    [Fact]
    public void AllocatorContractsAndAllocationOperandsSurviveSourceFreeLibrary()
    {
        Compilation library = Compile("""
            public struct Value { public allocator Heap* parent; public @Value(Heap* h) { parent = h; } }
            public Value* Create(Heap* h) { return new(h) Value(); }
            """);
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Allocators")));
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Allocators;
            namespace App;
            int Main() { Heap* h = new Heap(); Value* v = Create(h); int result = v->parent->allocations; delete(v); delete(h); return result; }
            """));
        Assert.False(consumer.HasErrors, string.Join("\n", consumer.Diagnostics));
        var target = LlvmTargetOptions.CreateHost();
        Compilation bound = LlvmIrGenerator.BindForTarget(consumer, target);
        Assert.False(bound.HasErrors, string.Join("\n", bound.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(bound, target);
    }
}
