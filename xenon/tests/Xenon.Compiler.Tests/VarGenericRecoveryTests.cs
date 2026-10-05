using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class VarGenericRecoveryTests
{
    private const string TaskSource = """
        namespace Example;
        public struct Task<T>
        {
            public storage<T> Value;
            public static void operator resolve(Task<T>& result, T value)
            {
                result.Value = move value;
            }
        }
        """;

    [Theory]
    [InlineData("var", DiagnosticIds.InvalidVarContext)]
    [InlineData("Task<var>", DiagnosticIds.InvalidVarContext)]
    [InlineData("shared<var>", DiagnosticIds.InvalidVarContext)]
    [InlineData("weak<var>", DiagnosticIds.InvalidVarContext)]
    [InlineData("unique<var>", DiagnosticIds.InvalidVarContext)]
    [InlineData("storage<var>", DiagnosticIds.InvalidVarContext)]
    [InlineData("pin<var>", DiagnosticIds.InvalidVarContext)]
    [InlineData("atomic<var>", DiagnosticIds.InvalidVarContext)]
    [InlineData("readonly var", DiagnosticIds.InvalidVarContext)]
    [InlineData("function var()", DiagnosticIds.InvalidVarContext)]
    [InlineData("function int(var)*", DiagnosticIds.InvalidVarContext)]
    [InlineData("Missing", DiagnosticIds.UnknownType)]
    [InlineData("Missing*", DiagnosticIds.UnknownType)]
    [InlineData("Missing&", DiagnosticIds.UnknownType)]
    [InlineData("Missing[]", DiagnosticIds.UnknownType)]
    public void AsyncReturnWithInvalidArgumentReportsSourceDiagnosticWithoutLoweringAnErrorSpecialization(
        string argument, string diagnosticId)
    {
        string source = $"namespace Example; public async Task<{argument}> Main() {{ return 42; }}";
        SourceText main = SourceText.From(source, "main.xe");
        if (diagnosticId == DiagnosticIds.InvalidVarContext)
            Assert.Contains(SyntaxTree.Parse(main).Diagnostics, d => d.Id == diagnosticId);

        Compilation compilation = Compilation.Create(SourceText.From(TaskSource, "Task.xe"), main);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(diagnosticId, diagnostic.Id);
        Assert.Equal("main.xe", diagnostic.Location.Path);
        string invalidName = diagnosticId == DiagnosticIds.InvalidVarContext ? "var" : "Missing";
        Assert.Equal(source.IndexOf(invalidName, StringComparison.Ordinal), diagnostic.Location.Span.Start);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function =>
            function.Symbol.Parameters.Any(parameter => parameter.Type is ErrorTypeSymbol));
    }

    [Theory]
    [InlineData("void Test() { Task<var> value; }")]
    [InlineData("void Test(Task<var> value) {}")]
    [InlineData("struct Holder { public Task<var> Value; }")]
    [InlineData("interface IValue<T> { T Get(); } void Test(IValue<var>& value) {}")]
    public void NonAsyncGenericContextsRejectVarWithoutSpecializingErrorTypes(string declaration)
    {
        Compilation compilation = Compilation.Create(SourceText.From(TaskSource, "Task.xe"),
            SourceText.From("namespace Example; " + declaration, "main.xe"));
        Assert.Equal(DiagnosticIds.InvalidVarContext, Assert.Single(compilation.Diagnostics).Id);
    }

    [Fact]
    public void ConcreteAsyncReturnAndInferredLocalRemainValid()
    {
        Compilation compilation = Compilation.Create(SourceText.From(TaskSource, "Task.xe"),
            SourceText.From("namespace Example; public async Task<int> Main() { var value = 42; return value; }", "main.xe"));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }
}
