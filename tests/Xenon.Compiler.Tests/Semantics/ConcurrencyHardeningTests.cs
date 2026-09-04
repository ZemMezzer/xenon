using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class ConcurrencyHardeningTests
{
    [Fact]
    public void FirstAssignmentToUninitializedAtomicLocalsIsInitialization()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Payload { public int Value; }
            struct Resource {}
            void Use(shared<Resource> owner, weak<Resource> observer, int[] array)
            {
                atomic<int> scalar;
                scalar = 1;
                scalar = 2;
                atomic<Payload> composite;
                composite = Payload { 1 };
                composite = Payload { 2 };
                atomic<shared<Resource>> strong;
                strong = owner;
                strong = owner;
                atomic<weak<Resource>> weakSlot;
                weakSlot = observer;
                weakSlot = observer;
                atomic<int[]> handle;
                handle = array;
                handle = array;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction function = Assert.Single(compilation.SemanticModel.Functions,
            candidate => candidate.Symbol.Name == "Use");
        BoundAssignmentExpression[] assignments = function.Body.Statements
            .OfType<BoundExpressionStatement>()
            .Select(statement => statement.Expression)
            .OfType<BoundAssignmentExpression>()
            .ToArray();
        Assert.Equal(10, assignments.Length);
        for (int index = 0; index < assignments.Length; index += 2)
        {
            Assert.True(assignments[index].IsInitialization);
            Assert.False(assignments[index + 1].IsInitialization);
        }
    }

    [Fact]
    public void AtomicContainingStructsAreRecursivelyNonCopyableAndNonRelocatable()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct A { public atomic<int> Value; }
            struct B { public atomic<shared<Resource>> Owner; }
            struct Nested { public B Inner; }
            void CopyA(A value) { A copy = value; }
            void CopyB(B value) { B copy = value; }
            void CopyNested(Nested value) { Nested copy = value; }
            void SwapA(A first, A second) { first <-> second; }
            void MoveNested(Nested value) { Nested moved = move value; }
            """);

        Assert.DoesNotContain(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.ValueNotCopyable);
        Assert.Equal(5, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.AtomicStorageNotRelocatable));

        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        Assert.All(new[] { "A", "B", "Nested" }, name =>
        {
            StructTypeSymbol type = ns.Structs.Single(candidate => candidate.Name == name);
            Assert.True(TypeFacts.ContainsAtomicStorage(type));
            Assert.False(TypeFacts.CanCopy(type));
            Assert.False(TypeFacts.CanRelocate(type));
        });
    }

    [Fact]
    public void StackArraysCannotEscapeThroughAtomicStoreSwapOrCompareExchange()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Globals { public static atomic<int[]> Current; }
            void Store()
            {
                int[] stack = int[16];
                Globals.Current = stack;
            }
            void Swap()
            {
                int[] stack = int[16];
                Globals.Current <-> stack;
            }
            void Compare()
            {
                int[] expected = new int[16];
                int[] desired = int[16];
                Globals.Current : expected --> desired;
                free(expected);
            }
            void Valid()
            {
                int[] expected = new int[16];
                int[] desired = new int[16];
                Globals.Current = desired;
                Globals.Current <-> desired;
                Globals.Current : expected --> desired;
                free(expected);
            }
            """);

        Assert.Equal(3, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.StackArrayEscape));
    }

    [Fact]
    public void NativeAbiRecursivelyRejectsAtomicStorageAndPointersToIt()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct NativeState { public atomic<int> Value; }
            struct Nested { public NativeState Inner; }
            extern void Direct(atomic<int> value);
            extern void AtomicPointer(atomic<int>* value);
            extern void StructPointer(NativeState* value);
            extern void NestedPointer(Nested* value);
            export atomic<int>* ReturnAtomicPointer() { return null; }
            """);

        Diagnostic[] diagnostics = compilation.Diagnostics.Where(diagnostic =>
            diagnostic.Id == DiagnosticIds.UnsupportedNativeAtomicType).ToArray();
        Assert.Equal(5, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
            Assert.Contains("atomic storage", diagnostic.Message, StringComparison.Ordinal));
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "concurrency-hardening.xe"));
}
