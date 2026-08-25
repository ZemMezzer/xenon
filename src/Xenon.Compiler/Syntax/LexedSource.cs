using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

public sealed class LexedSource
{
    private LexedSource(
        SourceText source,
        ImmutableArray<SyntaxToken> tokens,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Source = source;
        Tokens = tokens;
        Diagnostics = diagnostics;
    }

    public SourceText Source { get; }

    public ImmutableArray<SyntaxToken> Tokens { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public static LexedSource Lex(SourceText source)
    {
        var lexer = new Lexer(source);
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        SyntaxToken token;
        do
        {
            token = lexer.Lex();
            tokens.Add(token);
        }
        while (token.Kind != SyntaxKind.EndOfFileToken);

        return new LexedSource(source, tokens.ToImmutable(), [.. lexer.Diagnostics]);
    }
}
