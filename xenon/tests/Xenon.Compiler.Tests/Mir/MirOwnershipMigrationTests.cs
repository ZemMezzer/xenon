using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirOwnershipMigrationTests
{
    [Fact]
    public void UninstantiatedGenericDefinitionStillChecksOwnership()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Migration;
            void Broken<T>(int value) { int first = move value; int second = move value; }
            """));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Theory]
    [InlineData("pin<Resource> value = Resource(); value = Resource();", DiagnosticIds.PinnedRelocation)]
    [InlineData("pin<Resource> value; value = Resource(); value = Resource();", DiagnosticIds.PinnedRelocation)]
    public void PinInitializationIsCheckedOnMir(string body, string diagnosticId)
    {
        Compilation compilation = Compilation.Create(SourceText.From($$"""
            namespace Migration;
            struct Resource {}
            void F() { {{body}} }
            """));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void ConstructorReplacementDoesNotTrustBoundFlowAnnotations()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Migration;
            struct Resource {}
            struct Holder
            {
                public shared<Resource> Value;
                public Holder(shared<Resource> first, shared<Resource> second)
                {
                    Value = first;
                    Value = second;
                }
            }
            """));
        Assert.Empty(compilation.Diagnostics);
        BoundFunction function = compilation.SemanticModel.Functions.Single(function =>
            function.Symbol.FunctionKind == FunctionKind.Constructor && function.Symbol.ContainingType?.Name == "Holder");
        MirFunction original = MirLowerer.Lower(function, compilation.TypeFactory);
        BoundExpression Rewrite(BoundExpression expression) => expression switch
        {
            BoundFullExpression full => full with { Expression = Rewrite(full.Expression) },
            BoundAssignmentExpression assignment => assignment with
            {
                IsInitialization = !assignment.IsInitialization,
                MovedPlaceReinitialization = MovedPlaceReinitializationState.DefinitelyMoved,
                ConstructorField = null,
                RequiresRuntimeInitializationCheck = !assignment.RequiresRuntimeInitializationCheck,
            },
            _ => expression,
        };
        var altered = function with { Body = function.Body with
        {
            Statements = [.. function.Body.Statements.Select(statement => statement is BoundExpressionStatement expression
                ? expression with { Expression = Rewrite(expression.Expression) } : statement)],
        }};
        MirFunction lowered = MirLowerer.Lower(altered, compilation.TypeFactory);
        Assert.Equal(MirPrinter.Dump(original), MirPrinter.Dump(lowered));
        MirAssign[] writes = lowered.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>()
            .Where(assign => assign.Destination.Projections.LastOrDefault() is MirFieldProjection { Field.Name: "Value" }).ToArray();
        Assert.Equal([MirWriteKind.Initialize, MirWriteKind.Replace], writes.Select(assign => assign.WriteKind));
    }
}