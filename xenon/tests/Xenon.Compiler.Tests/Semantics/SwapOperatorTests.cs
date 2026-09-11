using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class SwapOperatorTests
{
    [Fact]
    public void SwapSupportsOrdinaryMoveOnlyAndSingleAtomicOperands()
    {
        Compilation compilation = Create("""
            namespace Example;

            struct Resource { public int Value; }

            void Use()
            {
                int first = 10;
                int second = 20;
                first <-> second;

                unique<Resource> left = new Resource();
                unique<Resource> right = new Resource();
                left <-> right;

                atomic<int> current = 1;
                int replacement = 2;
                current <-> replacement;
                replacement <-> current;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction function = Assert.Single(compilation.SemanticModel.Functions,
            candidate => candidate.Symbol.Name == "Use");
        BoundSwapExpression[] swaps = function.Body.Statements
            .OfType<BoundExpressionStatement>()
            .Select(statement => statement.Expression)
            .OfType<BoundSwapExpression>()
            .ToArray();
        Assert.Equal(4, swaps.Length);
        Assert.All(swaps, swap => Assert.Same(BuiltinTypes.Void, swap.Type));
    }

    [Fact]
    public void SwapRejectsTwoAtomicOperandsWithSpecificDiagnostic()
    {
        Compilation compilation = Create("""
            namespace Example;
            void Invalid()
            {
                atomic<int> first = 1;
                atomic<int> second = 2;
                first <-> second;
            }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.AtomicToAtomicSwap);
        Assert.Contains("at most one atomic", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("int first = 1; long second = 2; first <-> second;", DiagnosticIds.InvalidOperatorOperands)]
    [InlineData("int first = 1; first <-> 2;", DiagnosticIds.InvalidAssignmentTarget)]
    [InlineData("atomic<Data> first; Data second; first <-> second;", DiagnosticIds.InvalidOperatorOperands)]
    public void SwapRejectsInvalidOperands(string body, string diagnosticId)
    {
        Compilation compilation = Create($"namespace Example; struct Data {{ public atomic<int> Value; }} void Invalid() {{ {body} }}");

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void SemanticModelReportsVoidSwapExpression()
    {
        Compilation compilation = Create("namespace Example; void Use() { int a = 1; int b = 2; a <-> b; }");
        Assert.Empty(compilation.Diagnostics);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SwapExpressionSyntax swap = Assert.Single(
            SyntaxNavigator.DescendantNodesAndSelf(tree.Root).OfType<SwapExpressionSyntax>());

        Assert.Same(BuiltinTypes.Void, compilation.GetSemanticModel(tree).GetTypeInfo(swap).Type);
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "swap.xe"));
}
