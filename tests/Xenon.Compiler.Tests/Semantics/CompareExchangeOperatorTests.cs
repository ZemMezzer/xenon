using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class CompareExchangeOperatorTests
{
    [Fact]
    public void CompareExchangeBindsToBooleanWithoutMutatingValueOperands()
    {
        Compilation compilation = Create("""
            namespace Example;
            bool Try(atomic<int>& value, int expected, int desired)
            {
                bool succeeded = value : expected --> desired;
                int stillExpected = expected;
                int stillDesired = desired;
                return succeeded;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction function = Assert.Single(compilation.SemanticModel.Functions);
        var declaration = Assert.IsType<BoundVariableDeclarationStatement>(function.Body.Statements[0]);
        var compareExchange = Assert.IsType<BoundCompareExchangeExpression>(declaration.Initializer);
        Assert.Same(BuiltinTypes.Bool, compareExchange.Type);

        SyntaxTree tree = compilation.SyntaxTrees[0];
        CompareExchangeExpressionSyntax syntax = Assert.Single(
            SyntaxNavigator.DescendantNodesAndSelf(tree.Root).OfType<CompareExchangeExpressionSyntax>());
        Assert.Same(BuiltinTypes.Bool, compilation.GetSemanticModel(tree).GetTypeInfo(syntax).Type);
    }

    [Theory]
    [InlineData("int value = 0; value : 0 --> 1;", DiagnosticIds.CompareExchangeRequiresAtomicTarget)]
    [InlineData("readonly atomic<int>& value", DiagnosticIds.CompareExchangeRequiresAtomicTarget)]
    [InlineData("atomic<int>& value, long expected", DiagnosticIds.CompareExchangeOperandTypeMismatch)]
    public void CompareExchangeRejectsInvalidTargetOrOperands(string declarationOrBody, string diagnosticId)
    {
        string source = declarationOrBody.Contains(';', StringComparison.Ordinal)
            ? $"namespace Example; void Invalid() {{ {declarationOrBody} }}"
            : $"namespace Example; void Invalid({declarationOrBody}) {{ value : expected --> 1; }}";
        if (declarationOrBody.StartsWith("readonly", StringComparison.Ordinal))
            source = $"namespace Example; void Invalid({declarationOrBody}) {{ value : 0 --> 1; }}";

        Compilation compilation = Create(source);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "compare-exchange.xe"));
}
