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

            extern int puts(const byte* text);

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
    public void Lexer_UsesLongestOperatorMatch()
    {
        const string source = "<<= >>= -> ++ -- == != <= >= && || += -= *= /= %= &= |= ^=";
        SyntaxKind[] expected =
        [
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
}
