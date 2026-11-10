using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class UsingDeclarationTests
{
    [Theory]
    [InlineData("catch (readonly int& error)")]
    [InlineData("finally")]
    public void UsingDoesNotAllowSuspensionWithAnActiveException(string handler)
    {
        Compilation compilation = Compilation.Create(SourceText.From(ResumableRuntimeTests.Protocol + $$"""
            async Result Work() {
                try { throw 7; }
                {{handler}} { using var resource = new Resource(1); await Operation(); }
                return 42;
            }
            """, "using-await.xe"));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidAwaitContext);
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(
        "namespace UsingTests; struct Item { public int Value; public ~Item() {} } " + source, "using.xe"));

    [Theory]
    [InlineData("using var p = new Item(); p->Value = 10;")]
    [InlineData("using Item p = new Item(); p->Value = 10;")]
    [InlineData("using Item* p = new Item(); p->Value = 10;")]
    [InlineData("using int* p = new int(10); *p = 20;")]
    [InlineData("using var p = new int[3]; p[0] = 10;")]
    [InlineData("using Item[] p = new Item[3]; p[0].Value = 10;")]
    [InlineData("using Item* p = null;")]
    [InlineData("using var p = Create();")]
    public void SupportedDeclarationsReuseDelete(string body)
    {
        Compilation compilation = Compile("Item* Create() { return new Item(); } void Test() { " + body + " }");
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        foreach (MirFunction function in compilation.GetMirFunctions()) Assert.Empty(MirVerifier.Verify(function));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        var functionBody = compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == "Test").Body;
        var local = Assert.IsType<BoundVariableDeclarationStatement>(functionBody.Statements[0]);
        Assert.True(local.Variable.IsReadonly);
        Assert.True(local.Variable.Type is PointerTypeSymbol or ArrayTypeSymbol);
        var scope = Assert.IsType<BoundBlockStatement>(functionBody.Statements[1]);
        Assert.NotNull(scope.ExitCleanup);
    }

    [Theory]
    [InlineData("using var p = 123;", DiagnosticIds.InvalidFreeOperand)]
    [InlineData("using int* p;", DiagnosticIds.UsingRequiresInitializer)]
    [InlineData("using var p;", DiagnosticIds.UsingRequiresInitializer)]
    [InlineData("using void* p = null;", DiagnosticIds.InvalidFreeOperand)]
    [InlineData("using readonly Item* p = null;", DiagnosticIds.FreeThroughReadonlyPointer)]
    [InlineData("if (true) using var p = new Item();", DiagnosticIds.InvalidUsingContext)]
    public void InvalidDeclarationsAreDiagnosed(string body, string id)
    {
        Compilation compilation = Compile("void Test() { " + body + " }");
        Assert.Contains(compilation.Diagnostics, d => d.Id == id);
    }

    [Theory]
    [InlineData("p = new Item();")]
    [InlineData("p++;")]
    [InlineData("Item*& alias = p;")]
    public void UsingBindingCannotBeModified(string statement)
    {
        Compilation compilation = Compile("void Test() { using var p = new Item(); " + statement + " }");
        Assert.True(compilation.HasErrors);
    }

    [Fact]
    public void DeleteAccessChecksAreReused()
    {
        Compilation compilation = Compile("struct Secret { private ~Secret() {} } void Test() { using var p = new Secret(); }");
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.InaccessibleSymbol);
    }

    [Fact]
    public void ReassignmentHasUsingSpecificDiagnostic()
    {
        Compilation compilation = Compile("void Test() { using var p = new Item(); p = null; }");
        Assert.Contains(compilation.Diagnostics, d => d.Message == "a 'using' local cannot be reassigned");
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("p = null;", true)]
    public void ReferenceToPointerUsesNormalDeleteConversion(string statement, bool errors)
    {
        Compilation compilation = Compile("void Test(Item*& source) { using var p = source; p->Value = 1; " + statement + " }");
        Assert.Equal(errors, compilation.HasErrors);
        if (!errors) _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void GenericLibraryBodiesRetainCleanup()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Resources;
            public struct Item<T> { public int Value; public ~Item() {} }
            public int Read<T>(int value) {
                using var item = new Item<T>();
                item->Value = value;
                return item->Value;
            }
            """, "resources.xe"));
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Resources")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Resources;
            namespace App;
            int Main() { return Read<int>(42); }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join("\n", app.Diagnostics));
        Assert.False(LlvmIrGenerator.RequiresNativeExceptionRuntime(app));
        _ = new LlvmIrGenerator().GenerateForTarget(app, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData("using var p = new int(1); namespace Example;")]
    [InlineData("namespace Example; struct S { using int* p = null; }")]
    [InlineData("namespace Example; void Test(using int* p) {}")]
    [InlineData("namespace Example; void Test() { for (using var p = new int(1); true; ) {} }")]
    public void NonlocalContextsAreRejected(string source) =>
        Assert.NotEmpty(SyntaxTree.Parse(SourceText.From(source)).Diagnostics);
}
