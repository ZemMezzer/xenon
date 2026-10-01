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
    [Theory]
    [InlineData("int value = 1; value = move value;")]
    [InlineData("Pair value = Pair(); value.First = move value.First;")]
    public void SelfMoveIsDiagnosedFromMirTransferDestination(string body)
    {
        var compilation = Compilation.Create(SourceText.From($$"""
            namespace Migration;
            struct Pair { public int First; public int Second; }
            void F() { {{body}} }
            """));
        Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.SelfMove);
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "F");
        var mir = MirLowerer.Lower(function, compilation.TypeFactory);
        Assert.Contains("transfer to", MirPrinter.Dump(mir));
        Assert.Single(Xenon.Compiler.Mir.Analysis.MirOwnershipChecks.Check(mir),
            violation => violation.Check == Xenon.Compiler.Mir.Analysis.MirOwnershipCheck.SelfMove);
        var cleared = mir with { Blocks = [.. mir.Blocks.Select(block => block with
        {
            Statements = [.. block.Statements.Select(statement => statement is MirAssign assign
                ? assign with { TransferDestination = null } : statement)],
        })] };
        Assert.DoesNotContain(Xenon.Compiler.Mir.Analysis.MirOwnershipChecks.Check(cleared),
            violation => violation.Check == Xenon.Compiler.Mir.Analysis.MirOwnershipCheck.SelfMove);
    }

    [Theory]
    [InlineData("int", "42", false)]
    [InlineData("unique<Resource>", "new Resource()", true)]
    public void IndirectReceiverMoveKeepsPlainValuesAndRejectsHiddenOwnership(string type, string initial, bool rejected)
    {
        var compilation = Compilation.Create(SourceText.From($$"""
            namespace Migration;
            struct Resource {}
            struct Box<T>
            {
                T value;
                public Box(T initial) { value = move initial; }
                public T Take() { return move value; }
            }
            void F() { Box<{{type}}>* box = new Box<{{type}}>({{initial}}); {{type}} value = box->Take(); delete(box); }
            """));
        Assert.Equal(rejected, compilation.Diagnostics.Any(diagnostic => diagnostic.Id == DiagnosticIds.HiddenVirtualMoveEffect));
        if (!rejected) Assert.Empty(compilation.Diagnostics);
    }
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
    [Theory]
    [InlineData("Resource other = move alias;")]
    [InlineData("destruct(alias);")]
    [InlineData("Take(move alias);")]
    public void LifetimeCleanupDoesNotTrustBoundOwnerAnnotations(string operation)
    {
        Compilation compilation = Compilation.Create(SourceText.From($$"""
            namespace Migration;
            struct Resource { public ~Resource() {} }
            void Take(Resource value) {}
            void F() { Resource value = Resource(); Resource& alias = value; {{operation}} }
            """));
        Assert.Empty(compilation.Diagnostics);
        BoundFunction function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "F");
        MirFunction original = MirLowerer.Lower(function, compilation.TypeFactory);
        int rewritten = 0;
        var cache = new Dictionary<BoundExpression, BoundExpression>(ReferenceEqualityComparer.Instance);
        BoundExpression Rewrite(BoundExpression expression)
        {
            if (cache.TryGetValue(expression, out var existing)) return existing;
            return cache[expression] = RewriteCore(expression);
        }
        BoundExpression RewriteCore(BoundExpression expression)
        {
            if (expression is BoundMoveExpression move)
            { rewritten++; return move with { TrackedVariable = null, TrackedPath = [] }; }
            if (expression is BoundExplicitDestructExpression destruct)
            { rewritten++; return destruct with { TrackedVariable = null, TrackedPath = [] }; }
            return expression switch
            {
                BoundFullExpression full => full with { Expression = Rewrite(full.Expression),
                    Temporaries = [.. full.Temporaries.Select(temporary => temporary with { Value = Rewrite(temporary.Value) })] },
                BoundCallExpression call => call with { Arguments = [.. call.Arguments.Select(Rewrite)] },
                _ => expression,
            };
        }
        var altered = function with { Body = function.Body with
        {
            Statements = [.. function.Body.Statements.Select(statement => statement switch
            {
                BoundVariableDeclarationStatement { Initializer: { } initializer } declaration => declaration with { Initializer = Rewrite(initializer) },
                BoundExpressionStatement expression => expression with { Expression = Rewrite(expression.Expression) },
                _ => statement,
            })],
        }};
        Assert.Equal(1, rewritten);
        Assert.Equal(MirPrinter.Dump(original), MirPrinter.Dump(MirLowerer.Lower(altered, compilation.TypeFactory)));
    }
    [Fact]
    public void ArrayCleanupAndBackingRetentionIgnoreBoundFlowAnnotations()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Migration;
            struct A { public ~A() {} }
            void F() { A[] outer; { A[] inner = A[2]; outer = move inner; } }
            """));
        Assert.Empty(compilation.Diagnostics);
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "F");
        var original = MirLowerer.Lower(function, compilation.TypeFactory);
        BoundBlockStatement Rewrite(BoundBlockStatement block) => block with
        {
            RetainsStackStorage = !block.RetainsStackStorage,
            Statements = [.. block.Statements.Select(statement =>
            {
                if (statement is BoundVariableDeclarationStatement declaration)
                    declaration.Variable.RequiresArrayCleanupTransfer = !declaration.Variable.RequiresArrayCleanupTransfer;
                return statement is BoundBlockStatement nested ? Rewrite(nested) : statement;
            })],
        };
        function.Symbol.HasStackArrays = false;
        function.Symbol.HasScalarCleanup = false;
        var lowered = MirLowerer.Lower(function with { Body = Rewrite(function.Body) }, compilation.TypeFactory);
        Assert.Equal(MirPrinter.Dump(original), MirPrinter.Dump(lowered));
    }
}