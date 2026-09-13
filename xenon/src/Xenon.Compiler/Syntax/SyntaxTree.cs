using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

public sealed class SyntaxTree
{
    private SyntaxTree(
        SourceText source,
        CompilationUnitSyntax root,
        ImmutableArray<SyntaxToken> tokens,
        ImmutableArray<Diagnostic> diagnostics, ConditionalCompilationOptions conditionalOptions, bool targetDependent, bool hasDirectives)
    {
        Source = source;
        Root = root;
        Tokens = tokens;
        Diagnostics = diagnostics;
        ConditionalOptions = conditionalOptions;
        IsTargetDependent = targetDependent;
        HasConditionalDirectives = hasDirectives;
    }


    public SourceText Source { get; }

    public SourceFileId SourceFileId => Source.FileId;

    public CompilationUnitSyntax Root { get; }

    public ImmutableArray<SyntaxToken> Tokens { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public ConditionalCompilationOptions ConditionalOptions { get; }
    public bool IsTargetDependent { get; }
    public bool HasConditionalDirectives { get; }

    public static SyntaxTree Parse(SourceText source, CancellationToken cancellationToken = default,
        ConditionalCompilationOptions? conditionalOptions = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        conditionalOptions ??= ConditionalCompilationOptions.Default;
        LexedSource lexed = LexedSource.Lex(source, conditionalOptions, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var parser = new Parser(lexed.Tokens);
        CompilationUnitSyntax root = parser.ParseCompilationUnit();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(lexed.Diagnostics);
        diagnostics.AddRange(parser.Diagnostics);

        return new SyntaxTree(source, root, lexed.Tokens, diagnostics.ToImmutable(), conditionalOptions, lexed.IsTargetDependent, lexed.HasConditionalDirectives);
    }
}
