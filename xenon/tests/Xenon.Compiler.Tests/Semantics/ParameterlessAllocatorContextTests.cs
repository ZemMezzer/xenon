using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class ParameterlessAllocatorContextTests
{
    private const string Providers = """
        namespace Defaults;
        allocator Heap {
            +Heap(nuint s, nuint a) { return malloc(s, a); }
            -Heap(void* p) { free(p); }
        }
        allocator Arena : Heap {}
        """;

    [Theory]
    [InlineData("struct")]
    [InlineData("allocator")]
    public void ParameterlessContextHasAConcreteZeroArgumentMirCall(string kind)
    {
        var compilation = Compile($$"""
            {{kind}} Value{{(kind == "allocator" ? " : Heap" : "")}} {
                public @Value() {}
                public @Value(Heap* h) {}
                public Value() {}
            }
            void System() { Value* v = new Value(); delete(v); }
            void Custom(Heap* h) { Value* v = new(h) Value(); delete(v); }
            void Array() { Value[] v = new Value[2]; delete(v); }
            void Pointers() { Value*[] v = new Value*[2]; delete(v); }
            {{(kind == "struct" ? "void Stack() { Value v = Value(); } void StackArray() { Value[] v = Value[2]; }" : "")}}
            """);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        var functions = compilation.GetMirFunctions().ToArray();
        foreach (var function in functions) Assert.Empty(MirVerifier.Verify(function));
        foreach (var name in kind == "struct" ? new[] { "System", "Array", "Stack", "StackArray", "Custom" } : new[] { "System", "Array", "Custom" })
        {
            var call = Assert.Single(Calls(functions.Single(f => f.Symbol.Name == name)));
            var target = Assert.IsType<MirFunctionOperand>(call.Callee).Function;
            Assert.Equal(name == "Custom" ? 1 : 0, target.Parameters.Length);
            Assert.Equal(target.Parameters.Length, call.Arguments.Length);
        }
        Assert.Empty(Calls(functions.Single(f => f.Symbol.Name == "Pointers")));
    }

    [Theory]
    [InlineData("struct Value { @Value() {} @Value(Arena* h) {} } void Use(Heap* h) { Value* v = new(h) Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value() {} @Value(Arena* h) {} } void Use(Heap* h) { Value[] v = new(h) Value[2]; }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value() {} } void Use(Heap* h) { Value* v = new(h) Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value(Heap* h) {} } void Use() { Value v = Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value(Heap* h) {} } void Use() { Value* v = new Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value(Heap* h) {} } void Use() { Value[] v = new Value[2]; }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value(Heap* h) {} } void Use() { Value[] v = Value[2]; }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Value { @Value() {} @Value() {} }", DiagnosticIds.InvalidOverload)]
    [InlineData("struct Value { private @Value() {} public @Value(Heap* h) {} } void Use() { Value* v = new Value(); }", DiagnosticIds.InaccessibleSymbol)]
    [InlineData("struct Value { private @Value() {} } void Use() { Value v = Value(); }", DiagnosticIds.InaccessibleSymbol)]
    [InlineData("struct Value { private @Value() {} } void Use() { Value[] v = new Value[2]; }", DiagnosticIds.InaccessibleSymbol)]
    [InlineData("struct Parent { public @Parent(Heap* h) {} } struct Value : Parent { public @Value() {} } void Use() { Value* v = new Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("struct Parent { public @Parent() {} } struct Value : Parent { public @Value(Heap* h) {} } void Use(Heap* h) { Value* v = new(h) Value(); }", DiagnosticIds.AllocationConstructorMismatch)]
    [InlineData("allocator Value : Heap { public @Value() {} } void Use() { Value v = Value(); }", DiagnosticIds.AllocatorRequiresHeap)]
    [InlineData("allocator Value : Heap { public @Value() {} } void Use() { Value[] v = Value[2]; }", DiagnosticIds.AllocatorRequiresHeap)]
    [InlineData("struct Value { public virtual @Value() {} }", DiagnosticIds.InvalidAllocatorDeclaration)]
    [InlineData("abstract struct Value { public abstract @Value(); }", DiagnosticIds.InvalidAllocatorDeclaration)]
    public void MissingOrInvalidDefaultContextsAreDiagnosed(string source, string diagnostic)
        => Assert.Contains(Compile(source).Diagnostics, d => d.Id == diagnostic);

    private static IEnumerable<MirCall> Calls(MirFunction function) => function.Blocks
        .Select(b => b.Terminator).OfType<MirCall>()
        .Where(c => c.Callee is MirFunctionOperand { Function.AllocationOperation: AllocationOperation.Construct });

    [Theory]
    [InlineData("storage<T> v = T();")]
    [InlineData("pin<T> v = T();")]
    [InlineData("T[] v = T[2];")]
    public void SourceFreeGenericStackStorageCannotHideAllocatorValues(string construction)
    {
        var library = Compile($$"""
            public template Constructible { Constructible(); }
            public void Stack<T>() where T : Constructible { {{construction}} }
            public allocator Value : Heap { public @Value() {} public Value() {} }
            """);
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Defaults")));
        var consumer = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("using Defaults; namespace Consumer; void Use() { Stack<Value>(); }"));
        Assert.Contains(consumer.Diagnostics, d => d.Id == DiagnosticIds.AllocatorRequiresHeap);
    }

    [Theory]
    [InlineData("storage<T> v = T();")]
    [InlineData("pin<T> v = T();")]
    public void SourceFreeGenericStorageStillRequiresAnAvailableParameterlessContext(string construction)
    {
        var library = Compile($$"""
            public template Constructible { Constructible(); }
            public void Stack<T>() where T : Constructible { {{construction}} }
            public struct Value { @Value(Heap* h) {} public Value() {} }
            """);
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Defaults")));
        var consumer = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("using Defaults; namespace Consumer; void Use() { Stack<Value>(); }"));
        Assert.Contains(consumer.Diagnostics, d => d.Id == DiagnosticIds.AllocationConstructorMismatch);
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(Providers + source));
}
