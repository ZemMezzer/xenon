using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class VarInferenceTests
{
    [Fact]
    public void Keyword_IsReservedButNotATypeName()
    {
        LexedSource lexed = LexedSource.Lex(SourceText.From("var variable Var"));
        Assert.Empty(lexed.Diagnostics);
        Assert.Equal(SyntaxKind.VarKeyword, lexed.Tokens[0].Kind);
        Assert.Equal(SyntaxKind.IdentifierToken, lexed.Tokens[1].Kind);
        Assert.Equal(SyntaxKind.IdentifierToken, lexed.Tokens[2].Kind);
        Assert.True(SyntaxFacts.IsKeyword(SyntaxKind.VarKeyword));
        Assert.False(SyntaxFacts.IsTypeName(SyntaxKind.VarKeyword));
        Assert.False(SyntaxFacts.IsPrimitiveTypeKeyword(SyntaxKind.VarKeyword));
        Assert.Contains("var", SyntaxFacts.GetEditorKeywordTexts());
    }

    [Theory]
    [InlineData("var value = 10;")]
    [InlineData("readonly var value = 10;")]
    [InlineData("for (var value = 0; value < 10; value++) {}")]
    public void Parser_UsesDeclarationMarker(string statement)
    {
        SyntaxTree tree = SyntaxTree.Parse(SourceText.From("namespace Inference; void Test() { " + statement + " }"));
        Assert.Empty(tree.Diagnostics);
        var declaration = Assert.Single(SyntaxNavigator.DescendantNodesAndSelf(tree.Root)
            .OfType<VariableDeclarationStatementSyntax>());
        Assert.True(declaration.Type.Contains<InferredTypeSyntax>());
        Assert.NotNull(declaration.Initializer);
    }

    [Theory]
    [InlineData("10", "int")]
    [InlineData("10u", "uint")]
    [InlineData("10l", "long")]
    [InlineData("10ul", "ulong")]
    [InlineData("10n", "nint")]
    [InlineData("10un", "nuint")]
    [InlineData("10f", "float")]
    [InlineData("10d", "double")]
    [InlineData("10.0", "double")]
    [InlineData("2147483648", "long")]
    [InlineData("9223372036854775808", "ulong")]
    [InlineData("0xFFu", "uint")]
    [InlineData("true", "bool")]
    [InlineData("'A'", "char")]
    public void Inference_UsesNormalLiteralType(string expression, string expected)
    {
        Compilation compilation = Compile($"void Test() {{ var value = {expression}; }}");
        AssertValid(compilation);
        AssertLocalType(compilation, "value", expected);
    }

    [Theory]
    [InlineData("uint", "10u")]
    [InlineData("int*", "null")]
    [InlineData("readonly int*", "null")]
    [InlineData("unique<int>", "new int(10)")]
    [InlineData("shared<int>", "new int(10)")]
    public void FunctionResult_PreservesFullType(string type, string expression)
    {
        Compilation compilation = Compile($"{type} Get() {{ return {expression}; }} void Test() {{ var value = Get(); }}");
        AssertValid(compilation);
        AssertLocalType(compilation, "value", type);
    }

    [Theory]
    [InlineData("int&")]
    [InlineData("readonly int&")]
    public void References_PreserveInitializerType(string type)
    {
        Compilation compilation = Compile($"{type} Get({type} p) {{ return p; }} void Test({type} p) {{ var value = Get(p); }}");
        AssertValid(compilation);
        AssertLocalType(compilation, "value", type);
    }

    [Theory]
    [InlineData("Item*")]
    [InlineData("readonly Item*")]
    [InlineData("Item&")]
    [InlineData("readonly Item&")]
    [InlineData("shared<Item>")]
    [InlineData("weak<Item>")]
    public void ParenthesizedInitializer_PreservesCompleteStructType(string type)
    {
        Compilation compilation = Compile($"struct Item {{}} void Test({type} input) {{ var value = ((input)); }}");
        AssertValid(compilation);
        AssertLocalType(compilation, "value", type);
    }

    [Fact]
    public void MutableLocal_AndReadonlyPointerBindingUseNormalRules()
    {
        Compilation compilation = Compile("void Test(int* pointer) { var count = 10; count = 20; readonly var value = pointer; *value = count; }");
        AssertValid(compilation);
        AssertLocalType(compilation, "value", "int*");
    }

    [Fact]
    public void FunctionValues_PreserveTheirSignature()
    {
        Compilation compilation = Compile("void Test(function int(int) input) { var value = input; int result = value(10); }");
        AssertValid(compilation);
        AssertLocalType(compilation, "value", "function int(int)");
    }

    [Fact]
    public void WeakAndArrayTypes_ArePreserved()
    {
        Compilation compilation = Compile("""
            void Test() {
                shared<int> owner = new int(10);
                weak<int> observer = owner;
                var value = observer;
                var items = new int[3];
                free(items);
            }
            """);
        AssertValid(compilation);
        AssertLocalType(compilation, "value", "weak<int>");
        AssertLocalType(compilation, "items", "int[]");
    }

    [Fact]
    public void Local_IsMutableAndLaterUsesDoNotInfluenceInference()
    {
        Compilation compilation = Compile("void Use(long p) {} void Test() { var value = 10; value = 20; Use(value); }");
        Assert.Single(compilation.Diagnostics);
        Compilation explicitDeclaration = Compile("void Use(long p) {} void Test() { int value = 10; value = 20; Use(value); }");
        Assert.Equal(explicitDeclaration.Diagnostics.Select(d => d.Id), compilation.Diagnostics.Select(d => d.Id));
        AssertLocalType(compilation, "value", "int");
    }

    [Theory]
    [InlineData("var", "int", "value = true;")]
    [InlineData("readonly var", "readonly int", "value = 20;")]
    public void InvalidAssignment_MatchesExplicitDeclaration(string inferred, string explicitType, string use)
    {
        Compilation actual = Compile($"void Test() {{ {inferred} value = 10; {use} }}");
        Compilation expected = Compile($"void Test() {{ {explicitType} value = 10; {use} }}");
        Assert.True(expected.HasErrors);
        Assert.Equal(expected.Diagnostics.Select(d => d.Id), actual.Diagnostics.Select(d => d.Id));
    }

    [Theory]
    [InlineData("var value;")]
    [InlineData("readonly var value;")]
    public void MissingInitializer_HasOneClearDiagnostic(string declaration)
    {
        Compilation compilation = Compile("void Test() { " + declaration + " }");
        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(DiagnosticIds.VarRequiresInitializer, diagnostic.Id);
        Assert.Contains("requires an initializer", diagnostic.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("Nothing()")]
    public void NonValueInitializer_IsRejected(string expression)
    {
        Compilation compilation = Compile($"void Nothing() {{}} void Test() {{ var value = {expression}; }}");
        Assert.Equal(DiagnosticIds.InvalidVarInitializer, Assert.Single(compilation.Diagnostics).Id);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("value")]
    [InlineData("Nothing")]
    public void ErrorInitializer_DoesNotCascade(string expression)
    {
        Compilation compilation = Compile($"void Nothing() {{}} void Test() {{ var value = {expression}; }}");
        Assert.Single(compilation.Diagnostics);
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Id == DiagnosticIds.InvalidVarInitializer);
    }

    [Theory]
    [InlineData("struct S { var field; }")]
    [InlineData("void Test(var value) {}")]
    [InlineData("var Test();")]
    [InlineData("void Test() { cast<var>(10); }")]
    [InlineData("void Test() { sizeof(var); }")]
    [InlineData("void Test() { unique<var> value; }")]
    [InlineData("void Test() { var* value = null; }")]
    [InlineData("void Test() { var& value = 10; }")]
    [InlineData("void Test() { var[] value = new int[1]; }")]
    [InlineData("void Test() { function var() value; }")]
    [InlineData("void Test() { try {} catch(var error) {} }")]
    public void InvalidTypeContexts_AreRejected(string source)
    {
        Compilation compilation = Compile(source);
        Assert.Contains(compilation.Diagnostics, d => d.Id == DiagnosticIds.InvalidVarContext);
    }

    [Theory]
    [InlineData("move owner", "")]
    [InlineData("move owner", "int result = *owner;")]
    [InlineData("owner", "")]
    public void Ownership_MatchesExplicitDeclaration(string initializer, string use)
    {
        string Body(string type) => $"void Test() {{ unique<int> owner = new int(10); {type} value = {initializer}; {use} }}";
        Compilation actual = Compile(Body("var"));
        Compilation expected = Compile(Body("unique<int>"));
        Assert.Equal(expected.Diagnostics.Select(d => d.Id), actual.Diagnostics.Select(d => d.Id));
        if (initializer == "move owner" && use.Length == 0) AssertValid(actual);
        else Assert.True(actual.HasErrors);
        AssertLocalType(actual, "value", "unique<int>");
    }

    [Fact]
    public void BorrowLifetime_MatchesExplicitDeclaration()
    {
        string Body(string type) => $"int& Test(int& input) {{ int local = 1; {type} value = input; return value; }}";
        Compilation actual = Compile(Body("var"));
        Compilation expected = Compile(Body("int&"));
        AssertValid(actual);
        Assert.Equal(expected.Diagnostics.Select(d => d.Id), actual.Diagnostics.Select(d => d.Id));

        string Escape(string type) => $"int& Test() {{ int local = 1; int& reference = local; {type} value = reference; return value; }}";
        actual = Compile(Escape("var"));
        expected = Compile(Escape("int&"));
        Assert.True(expected.HasErrors);
        Assert.Equal(expected.Diagnostics.Select(d => d.Id), actual.Diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void BorrowConflict_MatchesExplicitDeclaration()
    {
        string Body(string type) => $"void Test() {{ int local = 1; int& reference = local; {type} value = reference; local = 2; int result = value; }}";
        Compilation actual = Compile(Body("var"));
        Compilation expected = Compile(Body("int&"));
        Assert.True(expected.HasErrors);
        Assert.Equal(expected.Diagnostics.Select(d => d.Id), actual.Diagnostics.Select(d => d.Id));
    }

    [Fact]
    public void OwnershipCleanup_ProducesSameIrAsExplicitDeclarations()
    {
        string Body(string type) => $$"""
            struct Item { public int Number; public ~Item() {} }
            unique<Item> Create() { return new Item(); }
            void Test() { {{type}} first = Create(); {{type}} second = move first; }
            """;
        Compilation actual = Compile(Body("var"));
        Compilation expected = Compile(Body("unique<Item>"));
        AssertValid(actual);
        AssertValid(expected);
        AssertLocalType(actual, "first", "unique<Item>");
        AssertLocalType(actual, "second", "unique<Item>");
        var target = new LlvmTargetOptions("x86_64-pc-windows-msvc");
        Assert.Equal(new LlvmIrGenerator().GenerateForTarget(expected, target),
            new LlvmIrGenerator().GenerateForTarget(actual, target));
    }

    [Fact]
    public void CodeGeneration_UsesResolvedTypes()
    {
        Compilation compilation = Compile("int Test() { var value = 10; for (var i = 0; i < 3; i++) { value += i; } return value; }");
        AssertValid(compilation);
        string ir = new LlvmIrGenerator().Generate(compilation);
        Assert.Contains("i32", ir);
    }

    private static Compilation Compile(string source) =>
        Compilation.Create(SourceText.From("namespace Inference; " + source, "inference.xe"));

    private static void AssertValid(Compilation compilation) =>
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));

    private static void AssertLocalType(Compilation compilation, string name, string expected)
    {
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        var syntax = SyntaxNavigator.DescendantNodesAndSelf(tree.Root).OfType<VariableDeclarationStatementSyntax>()
            .Single(declaration => declaration.IdentifierToken.Text == name);
        var local = Assert.IsType<LocalVariableSymbol>(model.GetDeclaredSymbol(syntax));
        Assert.Equal(expected, local.Type.ToDisplayString());
        Assert.True(TypeIdentity.AreSame(local.Type, model.GetTypeInfo(syntax.Type).Type));
        Assert.True(TypeIdentity.AreSame(local.Type, model.GetTypeInfo(syntax.Initializer!).Type));
    }
}
