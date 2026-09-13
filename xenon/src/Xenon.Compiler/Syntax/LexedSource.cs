using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

public sealed class LexedSource
{
    private LexedSource(
        SourceText source,
        ImmutableArray<SyntaxToken> tokens,
        ImmutableArray<Diagnostic> diagnostics, bool targetDependent, bool hasDirectives)
    {
        Source = source;
        Tokens = tokens;
        Diagnostics = diagnostics;
        IsTargetDependent = targetDependent;
        HasConditionalDirectives = hasDirectives;
    }

    public SourceText Source { get; }

    public ImmutableArray<SyntaxToken> Tokens { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public bool IsTargetDependent { get; }
    public bool HasConditionalDirectives { get; }

    public static LexedSource Lex(SourceText source, ConditionalCompilationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var filtered = ConditionalSource.Filter(source, options ?? ConditionalCompilationOptions.Default, cancellationToken);
        var lexer = new Lexer(source, filtered.Text);
        var tokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        SyntaxToken token;
        do
        {
            token = lexer.Lex();
            tokens.Add(token);
        }
        while (token.Kind != SyntaxKind.EndOfFileToken);

        return new LexedSource(source, tokens.ToImmutable(), [.. filtered.Diagnostics, .. lexer.Diagnostics], filtered.TargetDependent, filtered.HasDirectives);
    }
}
