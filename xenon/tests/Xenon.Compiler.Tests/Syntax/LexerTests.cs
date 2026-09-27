using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Syntax;

public sealed class LexerTests
{
    [Fact]
    public void Lexer_RecognizesCoreProgram()
    {
        const string source = """
            namespace Example;

            extern int puts(readonly byte* text);

            int Main()
            {
                puts("Hello from Xenon");
                return 0;
            }
            """;

        LexedSource tree = LexedSource.Lex(SourceText.From(source));

        Assert.Empty(tree.Diagnostics);
        Assert.Contains(tree.Tokens, token => token.Kind == SyntaxKind.NamespaceKeyword);
        Assert.Contains(tree.Tokens, token => token.Kind == SyntaxKind.ExternKeyword);
        Assert.Contains(tree.Tokens, token => token.Kind == SyntaxKind.StringLiteralToken);
        Assert.Equal(SyntaxKind.EndOfFileToken, tree.Tokens[^1].Kind);
    }

    [Fact]
    public void Lexer_RecognizesVisibilityKeywords()
    {
        LexedSource source = LexedSource.Lex(SourceText.From("public private"));

        Assert.Empty(source.Diagnostics);
        Assert.Equal(SyntaxKind.PublicKeyword, source.Tokens[0].Kind);
        Assert.Equal(SyntaxKind.PrivateKeyword, source.Tokens[1].Kind);
        Assert.Equal(SyntaxKind.EndOfFileToken, source.Tokens[2].Kind);
    }

    [Fact]
    public void Lexer_RecognizesUsingKeyword()
    {
        LexedSource source = LexedSource.Lex(SourceText.From("using Example.Math;"));

        Assert.Empty(source.Diagnostics);
        Assert.Equal(SyntaxKind.UsingKeyword, source.Tokens[0].Kind);
    }

    [Fact]
    public void Lexer_RecognizesTemplateConstraintKeywords()
    {
        LexedSource source = LexedSource.Lex(SourceText.From("template where"));

        Assert.Empty(source.Diagnostics);
        Assert.Equal(SyntaxKind.TemplateKeyword, source.Tokens[0].Kind);
        Assert.Equal(SyntaxKind.WhereKeyword, source.Tokens[1].Kind);
    }

    [Fact]
    public void Lexer_RecognizesAtomicAsCoreTypeKeyword()
    {
        LexedSource source = LexedSource.Lex(SourceText.From("atomic<int>"));

        Assert.Empty(source.Diagnostics);
        Assert.Equal(SyntaxKind.AtomicKeyword, source.Tokens[0].Kind);
        Assert.Contains("atomic", SyntaxFacts.GetEditorKeywordTexts());
    }

    [Fact]
    public void Lexer_RecognizesThreadLocalAsFieldModifierKeyword()
    {
        LexedSource source = LexedSource.Lex(SourceText.From("static threadlocal int Value;"));

        Assert.Empty(source.Diagnostics);
        Assert.Equal(SyntaxKind.StaticKeyword, source.Tokens[0].Kind);
        Assert.Equal(SyntaxKind.ThreadLocalKeyword, source.Tokens[1].Kind);
        Assert.Contains("threadlocal", SyntaxFacts.GetEditorKeywordTexts());
    }

    [Fact]
    public void Lexer_DoesNotExposeMemoryOrderingAsSourceKeywords()
    {
        string[] futureOrderingNames =
            ["MemoryOrder", "Relaxed", "Acquire", "Release", "AcqRel", "SeqCst", "Fence"];

        foreach (string name in futureOrderingNames)
            Assert.Equal(SyntaxKind.IdentifierToken, SyntaxFacts.GetKeywordKind(name));
    }

    [Fact]
    public void Lexer_UsesLongestOperatorMatch()
    {
        const string source = "<-> --> <<= >>= -> ++ -- == != <= >= && || += -= *= /= %= &= |= ^=";
        SyntaxKind[] expected =
        [
            SyntaxKind.SwapToken,
            SyntaxKind.CompareExchangeArrowToken,
            SyntaxKind.LessLessEqualsToken,
            SyntaxKind.GreaterGreaterEqualsToken,
            SyntaxKind.ArrowToken,
            SyntaxKind.PlusPlusToken,
            SyntaxKind.MinusMinusToken,
            SyntaxKind.EqualsEqualsToken,
            SyntaxKind.BangEqualsToken,
            SyntaxKind.LessOrEqualsToken,
            SyntaxKind.GreaterOrEqualsToken,
            SyntaxKind.AmpersandAmpersandToken,
            SyntaxKind.PipePipeToken,
            SyntaxKind.PlusEqualsToken,
            SyntaxKind.MinusEqualsToken,
            SyntaxKind.StarEqualsToken,
            SyntaxKind.SlashEqualsToken,
            SyntaxKind.PercentEqualsToken,
            SyntaxKind.AmpersandEqualsToken,
            SyntaxKind.PipeEqualsToken,
            SyntaxKind.CaretEqualsToken,
            SyntaxKind.EndOfFileToken,
        ];

        LexedSource tree = LexedSource.Lex(SourceText.From(source));

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(expected, tree.Tokens.Select(token => token.Kind));
    }

    [Fact]
    public void Lexer_SkipsComments()
    {
        const string source = "int /* block */ value; // line\nreturn value;";

        LexedSource tree = LexedSource.Lex(SourceText.From(source));

        Assert.Empty(tree.Diagnostics);
        Assert.DoesNotContain(tree.Tokens, token => token.Text.Contains("comment", StringComparison.Ordinal));
        Assert.Equal(7, tree.Tokens.Length);
    }

    [Theory]
    [InlineData("42", 42UL)]
    [InlineData("0xFF", 255UL)]
    [InlineData("0b101010", 42UL)]
    public void Lexer_ParsesIntegerLiterals(string source, ulong expected)
    {
        LexedSource tree = LexedSource.Lex(SourceText.From(source));

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(expected, tree.Tokens[0].Value);
    }

    [Fact]
    public void Lexer_ParsesStringEscapes()
    {
        LexedSource tree = LexedSource.Lex(SourceText.From("\"line\\ntext\""));

        Assert.Empty(tree.Diagnostics);
        Assert.Equal("line\ntext", tree.Tokens[0].Value);
    }

    [Fact]
    public void Lexer_ReportsUnterminatedConstructs()
    {
        LexedSource stringTree = LexedSource.Lex(SourceText.From("\"unterminated"));
        LexedSource commentTree = LexedSource.Lex(SourceText.From("/* unterminated"));

        Assert.Contains(stringTree.Diagnostics, diagnostic => diagnostic.Message == "unterminated string literal");
        Assert.Contains(commentTree.Diagnostics, diagnostic => diagnostic.Message == "unterminated block comment");
    }

    [Fact]
    public void Lexer_ReportsSourceLineAndColumn()
    {
        LexedSource tree = LexedSource.Lex(SourceText.From("namespace Example;\r\n@", "main.xe"));

        var diagnostic = Assert.Single(tree.Diagnostics);
        Assert.Equal("main.xe", diagnostic.Location.Source.Path);
        Assert.Equal(new LinePosition(1, 0), diagnostic.Location.Start);
    }
    [Theory]
    [InlineData("\0int later;", "later")]
    [InlineData("int\0 value;", "value")]
    [InlineData("alpha\0beta", "beta")]
    [InlineData("// hidden\0text\nint after;", "after")]
    [InlineData("/* hidden\0text */ int after;", "after")]
    public void Lexer_ReportsPhysicalNulWithoutEndingSource(string source, string laterIdentifier)
    {
        LexedSource tree = LexedSource.Lex(SourceText.From(source));
        Assert.Contains(tree.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.InvalidCharacter &&
            diagnostic.Message == "unexpected character U+0000");
        Assert.Contains(tree.Tokens, token =>
            token.Kind == SyntaxKind.IdentifierToken && token.Text == laterIdentifier);
        Assert.Equal(source.Length, tree.Tokens[^1].Location.Span.Start);
    }

    [Fact]
    public void Lexer_DoesNotHideInvalidTailAfterNul()
    {
        const string source = "int Main() { return 0; }\0@@";
        LexedSource tree = LexedSource.Lex(SourceText.From(source));
        Assert.Equal(3, tree.Diagnostics.Count(diagnostic => diagnostic.Id == DiagnosticIds.InvalidCharacter));
        Assert.Equal(3, tree.Tokens.Count(token => token.Kind == SyntaxKind.BadToken));
        Assert.Equal(source.Length, tree.Tokens[^1].Location.Span.Start);
    }

    [Fact]
    public void Lexer_ReportsPhysicalNulInsideStringAndContinues()
    {
        LexedSource tree = LexedSource.Lex(SourceText.From("\"a\0b\" tail"));
        Assert.Contains(tree.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.InvalidCharacter &&
            diagnostic.Message == "unexpected character U+0000");
        Assert.Equal("ab", tree.Tokens[0].Value);
        Assert.Contains(tree.Tokens, token => token.Text == "tail");
    }

    [Theory]
    [InlineData("alpha")]
    [InlineData("Привет")]
    [InlineData("λ")]
    [InlineData("漢字")]
    [InlineData("\U00010400value")]
    [InlineData("A\U00010400B")]
    public void Lexer_RecognizesUnicodeScalarIdentifiersWithUtf16Spans(string identifier)
    {
        LexedSource tree = LexedSource.Lex(SourceText.From(identifier));
        Assert.Empty(tree.Diagnostics);
        SyntaxToken token = tree.Tokens[0];
        Assert.Equal(SyntaxKind.IdentifierToken, token.Kind);
        Assert.Equal(identifier, token.Text);
        Assert.Equal(identifier.Length, token.Location.Span.Length);
    }

    [Theory]
    [InlineData("\uD800name", 0)]
    [InlineData("ab\uD800cd", 2)]
    [InlineData("\U0001F600name", 0)]
    public void Lexer_RejectsInvalidIdentifierScalarsWithoutLosingTheTail(string source, int invalidStart)
    {
        LexedSource tree = LexedSource.Lex(SourceText.From(source));
        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Location.Span.Start == invalidStart);
        Assert.Contains(tree.Tokens, token =>
            token.Kind == SyntaxKind.IdentifierToken &&
            (token.Text.EndsWith("name", StringComparison.Ordinal) || token.Text == "cd"));
        Assert.Equal(source.Length, tree.Tokens[^1].Location.Span.Start);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void DocumentationTriviaHandlesEveryLineEnding(string newline)
    {
        LexedSource attached = LexedSource.Lex(SourceText.From("/// docs" + newline + "int value;"));
        LexedSource separated = LexedSource.Lex(SourceText.From("/// docs" + newline + newline + "int value;"));
        Assert.Equal("docs", attached.Tokens[0].LeadingDocumentation);
        Assert.Null(separated.Tokens[0].LeadingDocumentation);
    }

}
