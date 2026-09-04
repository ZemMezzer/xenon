using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class StructValueEqualityTests
{
    [Fact]
    public void StructEqualityAcceptsRecursiveComparableFieldsAndGenericSpecializations()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct Inner
            {
                public int Number;
                public float Floating;
                public Resource* Pointer;
                public int[] Array;
                public shared<Resource> Owner;
                public weak<Resource> Observer;
            }
            struct Outer { public Inner Value; public bool Enabled; }
            struct Pair<T> { public T First; public T Second; }

            bool Equal(Outer left, Outer right) { return left == right; }
            bool Different(Outer left, Outer right) { return left != right; }
            bool EqualPair(Pair<int> left, Pair<int> right) { return left == right; }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.All(compilation.SemanticModel.Functions
            .Where(function => function.Symbol.Name is "Equal" or "Different" or "EqualPair"),
            function => Assert.IsType<BoundBinaryExpression>(
                Assert.IsType<BoundReturnStatement>(Assert.Single(function.Body.Statements)).Expression));
    }

    [Fact]
    public void StructEqualityRejectsUnrelatedAndUnsafeFieldTypesBeforeCodeGeneration()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct Position { public int X; }
            struct Velocity { public int X; }
            struct AtomicState { public atomic<int> Counter; }
            struct UniqueState { public unique<Resource> Owner; }

            bool Unrelated(Position left, Velocity right) { return left == right; }
            bool Atomic(readonly AtomicState& left, readonly AtomicState& right) { return left == right; }
            bool Unique(readonly UniqueState& left, readonly UniqueState& right) { return left != right; }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.InvalidOperatorOperands);
        Diagnostic[] equalityDiagnostics = compilation.Diagnostics.Where(diagnostic =>
            diagnostic.Id == DiagnosticIds.StructValueEqualityNotSupported).ToArray();
        Assert.Equal(2, equalityDiagnostics.Length);
        Assert.Contains(equalityDiagnostics, diagnostic =>
            diagnostic.Message.Contains("atomic storage", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Counter", StringComparison.Ordinal));
        Assert.Contains(equalityDiagnostics, diagnostic =>
            diagnostic.Message.Contains("unique<", StringComparison.Ordinal) &&
            diagnostic.Message.Contains("Owner", StringComparison.Ordinal));
    }

    [Fact]
    public void ConstructorFieldAssignmentsTrackFirstInitializationAcrossControlFlow()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct Holder
            {
                public shared<Resource> Normal;
                public atomic<shared<Resource>> Atomic;

                public Holder(bool choose, shared<Resource> first, shared<Resource> second)
                {
                    if (choose)
                    {
                        Normal = first;
                        Atomic = first;
                    }
                    else
                    {
                        Normal = second;
                        Atomic = second;
                    }
                    Normal = second;
                    Atomic = second;
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction constructor = Assert.Single(compilation.SemanticModel.Functions, function =>
            function.Symbol.FunctionKind == FunctionKind.Constructor &&
            function.Symbol.ContainingType?.Name == "Holder");
        BoundAssignmentExpression[] normal = Assignments(constructor.Body)
            .Where(assignment => assignment.Target is BoundMemberAccessExpression { Field.Name: "Normal" })
            .ToArray();
        BoundAssignmentExpression[] atomic = Assignments(constructor.Body)
            .Where(assignment => assignment.Target is BoundMemberAccessExpression { Field.Name: "Atomic" })
            .ToArray();

        Assert.Equal([true, true, false], normal.Select(assignment => assignment.IsInitialization));
        Assert.Equal([true, true, false], atomic.Select(assignment => assignment.IsInitialization));
    }

    [Theory]
    [InlineData("if (choose) Normal = value; Normal = value;")]
    [InlineData("while (choose) { Normal = value; }")]
    [InlineData("if (choose) Atomic = value; Atomic = value;")]
    [InlineData("while (choose) { Atomic = value; }")]
    public void ConstructorTracksLifetimeSensitiveInitializationAtRuntimeWhenControlFlowIsAmbiguous(string body)
    {
        Compilation compilation = Create($$"""
            namespace Example;
            struct Resource {}
            struct Holder
            {
                public shared<Resource> Normal;
                public atomic<shared<Resource>> Atomic;
                public Holder(bool choose, shared<Resource> value)
                {
                    {{body}}
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction constructor = Assert.Single(compilation.SemanticModel.Functions, function =>
            function.Symbol.FunctionKind == FunctionKind.Constructor &&
            function.Symbol.ContainingType?.Name == "Holder");
        Assert.Single(Assignments(constructor.Body).Where(assignment =>
            assignment.RequiresRuntimeInitializationCheck));
    }

    [Fact]
    public void AtomicContainingCopyDiagnosticDoesNotSuggestMove()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct State { public atomic<int> Value; }
            void Copy(State source) { State copy = source; }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics.Where(item =>
            item.Id == DiagnosticIds.AtomicStorageNotRelocatable));
        Assert.Contains("cannot be implicitly copied or relocated", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("use 'move'", diagnostic.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<BoundAssignmentExpression> Assignments(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                foreach (BoundStatement child in block.Statements)
                foreach (BoundAssignmentExpression assignment in Assignments(child))
                    yield return assignment;
                break;
            case BoundExpressionStatement { Expression: BoundAssignmentExpression assignment }:
                yield return assignment;
                break;
            case BoundExpressionStatement { Expression: BoundFullExpression { Expression: BoundAssignmentExpression assignment } }:
                yield return assignment;
                break;
            case BoundIfStatement conditional:
                foreach (BoundAssignmentExpression assignment in Assignments(conditional.ThenStatement))
                    yield return assignment;
                if (conditional.ElseStatement is not null)
                    foreach (BoundAssignmentExpression assignment in Assignments(conditional.ElseStatement))
                        yield return assignment;
                break;
            case BoundWhileStatement loop:
                foreach (BoundAssignmentExpression assignment in Assignments(loop.Body))
                    yield return assignment;
                break;
            case BoundForStatement loop:
                if (loop.Initializer is not null)
                    foreach (BoundAssignmentExpression assignment in Assignments(loop.Initializer))
                        yield return assignment;
                foreach (BoundAssignmentExpression assignment in Assignments(loop.Body))
                    yield return assignment;
                break;
        }
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "struct-value-equality.xe"));
}
