using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class NumericLiteralSuffixTests
{
    public static IEnumerable<object[]> IntegerForms()
    {
        (string Suffix, NumericLiteralSuffix Kind, string Type)[] suffixes =
        [
            ("u", NumericLiteralSuffix.UInt, "uint"),
            ("l", NumericLiteralSuffix.Long, "long"),
            ("ul", NumericLiteralSuffix.ULong, "ulong"),
            ("n", NumericLiteralSuffix.NInt, "nint"),
            ("un", NumericLiteralSuffix.NUInt, "nuint"),
        ];
        foreach (var suffix in suffixes)
        foreach (string body in new[] { "16", "0x10", "0X10", "0b10000", "0B10000" })
        for (int mask = 0; mask < (1 << suffix.Suffix.Length); mask++)
        {
            string spelling = new(suffix.Suffix.Select((c, i) =>
                (mask & (1 << i)) == 0 ? c : char.ToUpperInvariant(c)).ToArray());
            yield return [body + spelling, suffix.Kind, suffix.Type];
        }
    }

    [Theory]
    [MemberData(nameof(IntegerForms))]
    public void IntegerSuffixes_PreserveTokenValueAndDetermineBoundLiteralType(
        string source, NumericLiteralSuffix suffix, string type)
    {
        LexedSource lexed = LexedSource.Lex(SourceText.From(source));
        Assert.Empty(lexed.Diagnostics);
        Assert.Equal(2, lexed.Tokens.Length);
        Assert.Equal(source, lexed.Tokens[0].Text);
        Assert.Equal(source.Length, lexed.Tokens[0].Location.Span.Length);
        Assert.Equal(NumericLiteralSuffix.None, lexed.Tokens[1].NumericSuffix);
        Assert.Equal(suffix, lexed.Tokens[0].NumericSuffix);
        Assert.Equal(16UL, Assert.IsType<ulong>(lexed.Tokens[0].Value));

        Compilation compilation = Compile($"{type} Value() {{ return {source}; }}");
        AssertValid(compilation);
        BoundLiteralExpression bound = ReturnLiteral(compilation);
        Assert.Equal(type, bound.Type.Name);
        Assert.Equal(16UL, Convert.ToUInt64(bound.Value));
        AssertLiteralType(compilation, type);
    }

    [Theory]
    [InlineData("100f", "float")]
    [InlineData("100F", "float")]
    [InlineData("100.0f", "float")]
    [InlineData("100.0F", "float")]
    [InlineData("1e2f", "float")]
    [InlineData("1E+2F", "float")]
    [InlineData("100d", "double")]
    [InlineData("100D", "double")]
    [InlineData("100.0d", "double")]
    [InlineData("100.0D", "double")]
    [InlineData("1e2d", "double")]
    [InlineData("1E+2D", "double")]
    public void FloatingSuffixes_DetermineType(string source, string type)
    {
        Compilation compilation = Compile($"{type} Value() {{ return {source}; }}");
        AssertValid(compilation);
        BoundLiteralExpression bound = ReturnLiteral(compilation);
        Assert.Equal(type, bound.Type.Name);
        if (type == "float") Assert.Equal(100f, Assert.IsType<float>(bound.Value));
        else Assert.Equal(100d, Assert.IsType<double>(bound.Value));
        AssertLiteralType(compilation, type);
    }

    [Theory]
    [InlineData("0u", "uint", 0UL)]
    [InlineData("1u", "uint", 1UL)]
    [InlineData("4294967295u", "uint", uint.MaxValue)]
    [InlineData("0xFFFFFFFFu", "uint", uint.MaxValue)]
    [InlineData("1l", "long", 1UL)]
    [InlineData("9223372036854775807l", "long", (ulong)long.MaxValue)]
    [InlineData("0x7FFFFFFFFFFFFFFFl", "long", (ulong)long.MaxValue)]
    [InlineData("18446744073709551615ul", "ulong", ulong.MaxValue)]
    [InlineData("0xFFFFFFFFFFFFFFFFul", "ulong", ulong.MaxValue)]
    [InlineData("0b1111111111111111111111111111111111111111111111111111111111111111ul", "ulong", ulong.MaxValue)]
    public void IntegerLimits_ArePreservedInBodiesAndConstants(string source, string type, ulong expected)
    {
        Compilation compilation = Compile($"const {type} Limit = {source}; {type} Value() {{ return {source}; }}");
        AssertValid(compilation);
        Assert.Equal(expected, Convert.ToUInt64(ReturnLiteral(compilation).Value));
        ConstantSymbol constant = Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Constants);
        Assert.Equal(expected, Convert.ToUInt64(constant.Value));
        AssertLiteralType(compilation, type);
    }

    [Theory]
    [InlineData("4294967296u")]
    [InlineData("0x100000000u")]
    [InlineData("0b100000000000000000000000000000000u")]
    [InlineData("9223372036854775808l")]
    [InlineData("0x8000000000000000L")]
    [InlineData("18446744073709551616ul")]
    [InlineData("0x10000000000000000ul")]
    [InlineData("0b10000000000000000000000000000000000000000000000000000000000000000ul")]
    [InlineData("9223372036854775808n")]
    [InlineData("18446744073709551616un")]
    [InlineData("1e39f")]
    [InlineData("1e309d")]
    public void OutOfRangeLiterals_AreRejectedBeforeAssignmentOrConstantConversion(string source)
    {
        foreach (string declaration in new[] { $"void Use() {{ ulong value = {source}; }}", $"const ulong Value = {source};" })
        {
            Compilation compilation = Compile(declaration);
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidNumber);
        }
    }

    [Theory]
    [InlineData("10uu")]
    [InlineData("10ll")]
    [InlineData("10ull")]
    [InlineData("10ln")]
    [InlineData("10nu")]
    [InlineData("10fu")]
    [InlineData("10df")]
    [InlineData("10lu")]
    [InlineData("10i")]
    [InlineData("10clong")]
    [InlineData("10u8")]
    [InlineData("10ULl")]
    [InlineData("10.0u")]
    [InlineData("10e2l")]
    [InlineData("0xFFuu")]
    [InlineData("0b10unx")]
    [InlineData("0b10f")]
    [InlineData("0b10d")]
    public void InvalidSuffix_IsOneTokenWithSpecificDiagnostic(string source)
    {
        LexedSource lexed = LexedSource.Lex(SourceText.From(source));
        Diagnostic diagnostic = Assert.Single(lexed.Diagnostics);
        Assert.Equal(DiagnosticIds.InvalidNumericSuffix, diagnostic.Id);
        Assert.Contains("invalid numeric suffix", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(2, lexed.Tokens.Length);
        Assert.Equal(source, lexed.Tokens[0].Text);
    }

    [Theory]
    [InlineData("0", "int")]
    [InlineData("2147483647", "int")]
    [InlineData("2147483648", "long")]
    [InlineData("9223372036854775807", "long")]
    [InlineData("9223372036854775808", "ulong")]
    [InlineData("18446744073709551615", "ulong")]
    [InlineData("0xFF", "int")]
    [InlineData("0x10d", "int")]
    [InlineData("0x10f", "int")]
    [InlineData("0b10", "int")]
    [InlineData("100.0", "double")]
    [InlineData("1e2", "double")]
    [InlineData("1e-2", "double")]
    public void UnsuffixedLiteralTypes_AreUnchanged(string source, string type)
    {
        Compilation compilation = Compile($"{type} Value() {{ return {source}; }}");
        AssertValid(compilation);
        AssertLiteralType(compilation, type);
    }

    [Theory]
    [InlineData("n", "nint", "2147483647", 32, false)]
    [InlineData("n", "nint", "2147483648", 32, true)]
    [InlineData("un", "nuint", "4294967295", 32, false)]
    [InlineData("un", "nuint", "4294967296", 32, true)]
    [InlineData("n", "nint", "2147483648", 64, false)]
    [InlineData("n", "nint", "9223372036854775807", 64, false)]
    [InlineData("un", "nuint", "18446744073709551615", 64, false)]
    [InlineData("n", "nint", "0x80000000", 32, true)]
    [InlineData("un", "nuint", "0x100000000", 32, true)]
    public void NativeRanges_UseTargetWidth(string suffix, string type, string digits, int width, bool error)
    {
        Compilation original = Compile($"const {type} Limit = {digits}{suffix}; {type} Value() {{ return {digits}{suffix}; }}");
        AssertValid(original);
        Assert.True(original.RequiresTargetLayout);
        Compilation bound = original.WithTargetLayout(new TestLayout(width));
        Assert.Equal(error, bound.HasErrors);
        if (error)
            Assert.Equal(2, bound.Diagnostics.Count(diagnostic => diagnostic.Id == DiagnosticIds.InvalidNumber));
        else
        {
            Assert.False(bound.RequiresTargetLayout);
            AssertLiteralType(bound, type);
        }
        AssertValid(original.WithTargetLayout(new TestLayout(64)));
    }

    [Theory]
    [InlineData("10l", "long")]
    [InlineData("10n", "nint")]
    [InlineData("9223372036854775808", "ulong")]
    public void Negation_RemainsUnary(string source, string type)
    {
        Compilation compilation = Compile($"{type} Value() {{ return -{source}; }}");
        AssertValid(compilation);
        BoundUnaryExpression unary = Assert.IsType<BoundUnaryExpression>(
            Assert.IsType<BoundReturnStatement>(Assert.Single(compilation.SemanticModel.Functions).Body.Statements[0]).Expression);
        Assert.Equal(SyntaxKind.MinusToken, unary.OperatorKind);
        Assert.Equal(type, Assert.IsType<BoundLiteralExpression>(unary.Operand).Type.Name);
        Assert.Single(SyntaxNavigator.DescendantNodesAndSelf(compilation.SyntaxTrees[0].Root).OfType<UnaryExpressionSyntax>());
    }

    [Fact]
    public void SignedMinimum_UsesInRangeOperandsAndRejectsOutOfRangePositiveBody()
    {
        Compilation valid = Compile("const long Minimum = -9223372036854775807l - 1l;");
        AssertValid(valid);
        Assert.Equal(long.MinValue, Assert.Single(Assert.Single(valid.SemanticModel.GlobalNamespace.Namespaces).Constants).Value);
        Compilation invalid = Compile("long Value() { return -9223372036854775808l; }");
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidNumber);
    }

    [Fact]
    public void ConstantOperators_UseExplicitSignednessAndWidth()
    {
        Compilation compilation = Compile("""
            const uint UIntBits = ~0u;
            const ulong ULongBits = ~0ul;
            const ulong HighBit = 1ul << 63;
            const long Negative = -10l;
            const nuint NativeBits = ~0un;
            const nint NativeMinimum = -2147483647n - 1n;
            """).WithTargetLayout(new TestLayout(32));
        AssertValid(compilation);
        var constants = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Constants
            .ToDictionary(constant => constant.Name, constant => constant.Value);
        Assert.Equal((ulong)uint.MaxValue, constants["UIntBits"]);
        Assert.Equal(ulong.MaxValue, constants["ULongBits"]);
        Assert.Equal(1UL << 63, constants["HighBit"]);
        Assert.Equal(-10L, constants["Negative"]);
        Assert.Equal((ulong)uint.MaxValue, constants["NativeBits"]);
        Assert.Equal(int.MinValue, constants["NativeMinimum"]);
    }

    [Fact]
    public void OverloadsAndLocalInitializers_SeeLiteralTypesWithoutCasts()
    {
        string[] types = ["int", "float", "double", "uint", "long", "ulong", "nint", "nuint"];
        string[] values = ["10", "10f", "10d", "10u", "10l", "10ul", "10n", "10un"];
        string declarations = string.Join("\n", types.Select(type => $"void Test({type} value) {{ }}"));
        string statements = string.Join("\n", types.Select((type, i) => $"{type} local{i} = {values[i]}; Test({values[i]});"));
        Compilation compilation = Compile(declarations + " void Use() { " + statements + " }");
        AssertValid(compilation);
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        var calls = SyntaxNavigator.DescendantNodesAndSelf(compilation.SyntaxTrees[0].Root).OfType<CallExpressionSyntax>().ToArray();
        Assert.Equal(types, calls.Select(call => Assert.IsType<FunctionSymbol>(model.GetSymbolInfo(call).Symbol).Parameters[0].Type.Name));
        BoundBlockStatement body = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use").Body;
        Assert.Equal(types, body.Statements.OfType<BoundVariableDeclarationStatement>()
            .Select(local => Assert.IsType<BoundLiteralExpression>(local.Initializer).Type.Name));
    }

    [Theory]
    [InlineData("i686-unknown-linux-gnu", 32)]
    [InlineData("x86_64-pc-windows-msvc", 64)]
    public void CodeGeneration_EmitsTypedConstants(string triple, int width)
    {
        Compilation compilation = Compile("""
            uint U() { return 4294967295u; }
            long L() { return 9223372036854775807l; }
            ulong UL() { return 18446744073709551615ul; }
            nint N() { return -10n; }
            nuint UN() { return 10un; }
            float F() { return 100f; }
            double D() { return 100d; }
            """);
        AssertValid(compilation);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions(triple));
        Assert.Contains("ret i32 -1", ir, StringComparison.Ordinal);
        Assert.Contains("ret i64 9223372036854775807", ir, StringComparison.Ordinal);
        Assert.Contains("ret i64 -1", ir, StringComparison.Ordinal);
        Assert.Contains($"ret i{width} 10", ir, StringComparison.Ordinal);
        Assert.Contains("ret float", ir, StringComparison.Ordinal);
        Assert.Contains("ret double", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeGeneration_RejectsNativeOverflowWithoutPriorConstantFolding()
    {
        Compilation compilation = Compile("nuint Value() { return 4294967296un; }");
        AssertValid(compilation);
        Assert.Throws<LlvmCodeGenerationException>(() => new LlvmIrGenerator().GenerateForTarget(
            compilation, new LlvmTargetOptions("i686-unknown-linux-gnu")));
        new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions("x86_64-pc-windows-msvc"));
    }

    private static Compilation Compile(string source) =>
        Compilation.Create(SourceText.From("namespace Numbers; " + source, "numbers.xe"));

    private static void AssertValid(Compilation compilation) =>
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));

    private static BoundLiteralExpression ReturnLiteral(Compilation compilation) =>
        Assert.IsType<BoundLiteralExpression>(Assert.IsType<BoundReturnStatement>(
            Assert.Single(compilation.SemanticModel.Functions).Body.Statements[0]).Expression);

    private static void AssertLiteralType(Compilation compilation, string type)
    {
        var model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        Assert.All(SyntaxNavigator.DescendantNodesAndSelf(compilation.SyntaxTrees[0].Root).OfType<LiteralExpressionSyntax>(),
            literal => Assert.Equal(type, model.GetTypeInfo(literal).Type.Name));
    }

    private sealed class TestLayout(int width) : ITargetTypeLayout
    {
        public int GetIntegerBitWidth(PrimitiveTypeSymbol type) => type.BitWidth ?? width;
        public ulong GetSize(TypeSymbol type) => (ulong)(width / 8);
        public uint GetAlignment(TypeSymbol type) => (uint)(width / 8);
        public ulong GetFieldOffset(StructTypeSymbol type, FieldSymbol field) => 0;
    }
}
