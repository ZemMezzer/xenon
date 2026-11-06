using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class AllocatorFieldCapabilityTests
{
    private const string Heap = """
        public allocator Heap {
            public int n;
            public void Write() { n++; }
            +Heap(nuint size, nuint alignment) { return malloc(size, alignment); }
            -Heap(void* block) { free(block); }
        }
        """;

    [Theory]
    [InlineData("Heap** ptr = &v->context;", DiagnosticIds.TypeMismatch)]
    [InlineData("Assign(v->context);", DiagnosticIds.TypeMismatch)]
    [InlineData("v->context++;", DiagnosticIds.InvalidOperatorOperands)]
    [InlineData("Heap* replacement = null; v->context <-> replacement;", DiagnosticIds.InvalidAssignmentTarget)]
    [InlineData("Value a = Value{null};", DiagnosticIds.AllocatorFieldWriteOutsidePhase)]
    [InlineData("Value* a = new Value{null};", DiagnosticIds.AllocatorFieldWriteOutsidePhase)]
    public void ContextFieldCannotProvideWritableStorage(string body, string code)
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace App; " + Heap + $$"""
            struct Value { public allocator Heap* context; public @Value() {} }
            void Assign(Heap*& n) { n = null; }
            void Use(Value* v) { {{body}} }
            """));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == code);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidAllocatorFieldType);
    }

    [Fact]
    public void ImmutableFieldBindingDoesNotFreezePointerTarget()
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace App; " + Heap + """
            struct Value { public allocator Heap* context; }
            void Use(Value* v) { v->context->Write(); v->context->n = 3; }
            """));
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void OpenGenericCannotReassignContextField()
    {
        var compilation = Compilation.Create(SourceText.From("namespace App; " + Heap + """
            struct Base { public allocator Heap* context; }
            void Write<T>(T* ptr) where T : Base { ptr->context = null; }
            """));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.WriteThroughReadonlyReceiver);
    }

    [Theory]
    [InlineData("ptr->context = replacement;", true)]
    [InlineData("ptr->context->Write();", false)]
    public void SourceFreeGenericPreservesReboundAllocatorFieldBinding(string body, bool writesBinding)
    {
        Compilation library = Compilation.Create(SourceText.From("namespace Api; " + Heap + $$"""
            public struct Base { public Heap* context; }
            public void Set<T>(T* ptr, Heap* replacement) where T : Base { {{body}} }
            """));
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Api")));
        var consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Api; namespace App;
            struct Derived : Base { public allocator Heap* context; }
            void Use(Derived* value) { Set<Derived>(value, null); }
            """));
        if (writesBinding)
            Assert.Contains(consumer.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AllocatorFieldWriteOutsidePhase);
        else
            Assert.Empty(consumer.Diagnostics);
    }
}
