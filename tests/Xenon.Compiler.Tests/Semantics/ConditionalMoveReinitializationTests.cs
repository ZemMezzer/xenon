using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class ConditionalMoveReinitializationTests
{
    [Fact]
    public void BinderDistinguishesLiveDefinitelyMovedAndMaybeMovedReassignment()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            void Maybe(bool take)
            {
                shared<Resource> value = new Resource();
                shared<Resource> moved;
                if (take) moved = move value;
                value = new Resource();
            }
            void Definite(bool take)
            {
                shared<Resource> value = new Resource();
                shared<Resource> moved;
                if (take) moved = move value;
                else moved = move value;
                value = new Resource();
            }
            void Live(bool take)
            {
                shared<Resource> value = new Resource();
                shared<Resource> moved;
                if (take) { moved = move value; return; }
                value = new Resource();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(MovedPlaceReinitializationState.MaybeMoved, FinalAssignment(compilation, "Maybe").MovedPlaceReinitialization);
        Assert.Equal(MovedPlaceReinitializationState.DefinitelyMoved, FinalAssignment(compilation, "Definite").MovedPlaceReinitialization);
        Assert.Equal(MovedPlaceReinitializationState.Live, FinalAssignment(compilation, "Live").MovedPlaceReinitialization);
    }

    [Fact]
    public void BinderKeepsConditionalUseAfterMoveAndAtomicMoveDiagnostics()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource { public int Value; }
            struct AtomicOwner { public atomic<shared<Resource>> Value; }
            int UseAfterMove(bool take)
            {
                unique<Resource> value = new Resource();
                unique<Resource> moved;
                if (take) moved = move value;
                return value->Value;
            }
            void AtomicMove(AtomicOwner value)
            {
                AtomicOwner moved = move value;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.AtomicStorageNotRelocatable &&
            diagnostic.Message.Contains("cannot move", StringComparison.Ordinal));
    }

    [Fact]
    public void BinderTracksMaybeMovedAcrossNestedBranchesAndLoopBreaks()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            void Nested(bool first, bool second)
            {
                shared<Resource> value = new Resource();
                shared<Resource> moved;
                if (first)
                {
                    if (second) moved = move value;
                }
                value = new Resource();
            }
            void Loop(bool condition, bool take)
            {
                shared<Resource> value = new Resource();
                shared<Resource> moved;
                while (condition)
                {
                    if (take)
                    {
                        moved = move value;
                        break;
                    }
                }
                value = new Resource();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(MovedPlaceReinitializationState.MaybeMoved,
            FinalAssignment(compilation, "Nested").MovedPlaceReinitialization);
        Assert.Equal(MovedPlaceReinitializationState.MaybeMoved,
            FinalAssignment(compilation, "Loop").MovedPlaceReinitialization);
    }

    [Fact]
    public void BinderRejectsMaybeMovedReceiverFieldWithoutRuntimeCleanupFlag()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct Owner
            {
                public shared<Resource> Value;
                public void Reset(bool take, shared<Resource> replacement)
                {
                    shared<Resource> moved;
                    if (take) moved = move Value;
                    Value = replacement;
                }
            }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics.Where(item =>
            item.Id == DiagnosticIds.ConditionalMoveReinitializationNotTracked));
        Assert.Contains("runtime lifetime flag", diagnostic.Message, StringComparison.Ordinal);
    }

    private static BoundAssignmentExpression FinalAssignment(Compilation compilation, string functionName)
    {
        BoundFunction function = Assert.Single(compilation.SemanticModel.Functions, candidate =>
            candidate.Symbol.Name == functionName);
        BoundExpression expression = Assert.IsType<BoundExpressionStatement>(function.Body.Statements[^1]).Expression;
        return Assert.IsType<BoundAssignmentExpression>(expression is BoundFullExpression full
            ? full.Expression
            : expression);
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "conditional-move-reinitialization.xe"));
}
