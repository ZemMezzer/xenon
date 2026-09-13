using System.Collections.Immutable;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class CharPrimitiveTests
{
    [Fact]
    public void Lexer_DecodesOneUnicodeScalarIncludingNonBmpAndEscapes()
    {
        LexedSource source = LexedSource.Lex(SourceText.From(
            """char 'A' 'Ж' '😀' '\n' '\r' '\t' '\0' '\\' '\''"""));

        Assert.Empty(source.Diagnostics);
        Assert.Equal(SyntaxKind.CharKeyword, source.Tokens[0].Kind);
        Assert.Equal(new ulong[] { 0x41, 0x416, 0x1F600, 10, 13, 9, 0, 0x5C, 0x27 },
            source.Tokens.Where(token => token.Kind == SyntaxKind.CharacterLiteralToken)
                .Select(token => Assert.IsType<ulong>(token.Value)));
    }

    [Theory]
    [InlineData("''", DiagnosticIds.EmptyCharacter)]
    [InlineData("'ab'", DiagnosticIds.MultiScalarCharacter)]
    [InlineData("'é'", DiagnosticIds.MultiScalarCharacter)]
    [InlineData("'\\q'", DiagnosticIds.UnknownEscapeSequence)]
    [InlineData("'", DiagnosticIds.UnterminatedCharacter)]
    [InlineData("'x", DiagnosticIds.UnterminatedCharacter)]
    public void Lexer_RejectsMalformedOrMultiScalarCharacterLiterals(string text, string diagnosticId)
    {
        LexedSource source = LexedSource.Lex(SourceText.From(text));

        Assert.Contains(source.Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void Lexer_RejectsDanglingCharacterEscape()
    {
        LexedSource source = LexedSource.Lex(SourceText.From("'" + "\\"));

        Assert.Contains(source.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UnterminatedCharacter);
    }

    [Fact]
    public void Lexer_RejectsUnpairedUtf16Surrogate()
    {
        LexedSource direct = LexedSource.Lex(SourceText.From("'\uD800'"));
        LexedSource escaped = LexedSource.Lex(SourceText.From("'\\\uD800'"));

        Assert.Contains(direct.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidUnicodeScalar);
        Assert.Contains(escaped.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidUnicodeScalar);
    }

    [Fact]
    public void Semantics_SupportsCharWithoutImplicitNumericConversionsOrArithmetic()
    {
        Compilation valid = Compile("""
            namespace Unicode;
            const char Smile = '😀';
            const bool ScalarOrdering = 'A' < 'Ж' && '😀' == '😀';
            uint Scalar(char value) { return cast<uint>(value); }
            char FromScalar(uint value) { return cast<char>(value); }
            int Classify(char value)
            {
                switch (value)
                {
                    case 'A': return 1;
                    case '😀': return 2;
                    default: return 0;
                }
            }
            T Identity<T>(T value) { return move value; }
            void Use(char& reference)
            {
                atomic<char> current = 'Ж';
                char read = current;
                char* pointer = &read;
                readonly char* readonlyPointer = pointer;
                char[] values = char[2];
                values[0] = Identity<char>(read);
                reference = values[0];
                unique<char> uniqueValue = new char('A');
                shared<char> sharedValue = new char('Ж');
            }
            """);
        Assert.False(valid.HasErrors, string.Join(Environment.NewLine, valid.Diagnostics));

        Compilation implicitConversions = Compile("""
            namespace Unicode;
            void Bad() { uint number = 'A'; char character = 65; }
            """);
        Assert.Equal(2, implicitConversions.Diagnostics.Count(diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch));

        Compilation arithmetic = Compile("namespace Unicode; char Bad() { return 'A' + 'B'; }");
        Assert.Contains(arithmetic.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidOperatorOperands);
    }

    [Fact]
    public void SemanticModel_ReportsCharAsASeparateNonIntegerPrimitive()
    {
        Compilation compilation = Compile("namespace Unicode; char Value() { return '😀'; }");
        LiteralExpressionSyntax literal = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root)
            .OfType<LiteralExpressionSyntax>().Single();
        TypeSymbol type = compilation.GetSemanticModel(compilation.SyntaxTrees.Single()).GetTypeInfo(literal).Type;

        Assert.Same(BuiltinTypes.Char, type);
        Assert.False(BuiltinTypes.Char.IsInteger);
        Assert.False(TypeIdentity.AreSame(BuiltinTypes.Char, BuiltinTypes.UInt));
        Assert.False(TypeIdentity.AreSame(BuiltinTypes.Char, BuiltinTypes.Byte));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("0xD7FF")]
    [InlineData("0xE000")]
    [InlineData("0x10FFFF")]
    public void Semantics_AcceptsValidConstantIntegerToCharCasts(string value)
    {
        Compilation compilation = Compile($"namespace Unicode; char Value() {{ return cast<char>({value}); }}");

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("0xD800")]
    [InlineData("0xDFFF")]
    [InlineData("0x110000")]
    [InlineData("-1")]
    public void Semantics_RejectsInvalidConstantIntegerToCharCasts(string value)
    {
        Compilation compilation = Compile($"namespace Unicode; char Value() {{ return cast<char>({value}); }}");

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.InvalidUnicodeScalarCast);
    }

    [Fact]
    public void Llvm_UsesOneI32ForCharConstantsLayoutAndCAbi()
    {
        Compilation compilation = Compile("""
            namespace Unicode;
            export char Echo(char value) { return value; }
            uint Emoji() { return cast<uint>('😀'); }
            nuint CharSize() { return sizeof(char); }
            bool Ordered() { return 'A' < 'Ж' && '😀' == '😀'; }
            """);
        var target = LlvmTargetOptions.CreateHost();
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, target, "unicode-char");

        string exportStorage = target.Triple.Contains("windows", StringComparison.OrdinalIgnoreCase)
            ? "dllexport "
            : string.Empty;
        Assert.Contains($"define {exportStorage}i32 @Unicode_Echo(i32", ir, StringComparison.Ordinal);
        Assert.Contains("ret i32 128512", ir, StringComparison.Ordinal);
        Assert.Contains("ret i64 4", ir, StringComparison.Ordinal);
        Assert.Equal("cdecl;fixed;i32(i32)", NativeSymbolNames.GetAbiSignature(
            compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Echo").Symbol,
            layout: null));
    }

    [Theory]
    [InlineData("i686-pc-windows-msvc")]
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("aarch64-unknown-linux-gnu")]
    public void Llvm_CharLayoutIsFourBytesOnEverySupportedTarget(string triple)
    {
        Compilation compilation = Compile("""
            namespace Unicode;
            struct Layout { public byte Prefix; public char Value; }
            uint Size() { return cast<uint>(sizeof(char)); }
            uint Alignment() { return cast<uint>(alignof(char)); }
            uint Offset() { return cast<uint>(offsetof(Layout, Value)); }
            """);

        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions(triple),
            "unicode-char-layout");

        Assert.Equal(3, Count(ir, "ret i32 4"));
    }

    [Fact]
    public void Xelib_UsesStableCharKindsAndRoundTripsNonBmpBodies()
    {
        Compilation library = Compile("""
            namespace UnicodeLibrary;
            const char Smile = '😀';
            public char Echo(char value) { return value; }
            public char GetSmile() { return '😀'; }
            public T Identity<T>(T value) { return move value; }
            public char ReadPointer(readonly char* value) { return *value; }
            public char First(char[] values) { return values[0]; }
            public void Assign(char& target, char value) { target = value; }
            public char ReadAtomic(readonly atomic<char>& value) { return value; }
            struct Data { public char Value; }
            """);
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("UnicodeLibrary"));
        XelibContainer container = XelibContainer.Read(bytes);
        ImmutableArray<XelibTypeRecord> types = XelibJson.Deserialize<ImmutableArray<XelibTypeRecord>>(
            container.GetRequiredSection(XelibSectionKind.Types).AsSpan(), null);
        ImmutableArray<XelibSymbolRecord> symbols = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(
            container.GetRequiredSection(XelibSectionKind.Symbols).AsSpan(), null);

        Assert.Contains(types, type => type.Kind == XelibTypeKind.Char && type.PrimitiveName is null);
        Assert.Contains(symbols, symbol => symbol.Name == "Smile" &&
            symbol.ConstantValue is { Kind: XelibConstantKind.UnicodeScalar, Value: "128512" });
        Assert.Equal(XelibVersions.LibraryIr, container.Header.LibraryIrVersion);
        Assert.Equal((ushort)1, container.Header.ContainerVersion);

        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using UnicodeLibrary;
            namespace App;
            uint Run()
            {
                char value = GetSmile();
                Assign(value, Echo(Identity<char>(value)));
                char[] values = new char[1];
                values[0] = value;
                readonly char* pointer = &value;
                atomic<char> atomicValue = value;
                char first = First(values);
                delete(values);
                if (first != ReadPointer(pointer)) return cast<uint>(0);
                return cast<uint>(ReadAtomic(atomicValue));
            }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            app, LlvmTargetOptions.CreateHost(), "unicode-char-xelib");
        Assert.Contains("i32 128512", ir, StringComparison.Ordinal);
    }

    private static Compilation Compile(string source) =>
        Compilation.Create(SourceText.From(source, "char.xe"));

    private static int Count(string text, string value)
    {
        int count = 0;
        for (int start = 0; (start = text.IndexOf(value, start, StringComparison.Ordinal)) >= 0;
             start += value.Length)
            count++;
        return count;
    }
}
