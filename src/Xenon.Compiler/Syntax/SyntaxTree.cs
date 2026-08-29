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
        ImmutableArray<Diagnostic> diagnostics)
    {
        Source = source;
        Root = root;
        Tokens = tokens;
        Diagnostics = diagnostics;
    }

    public SourceText Source { get; }

    public SourceFileId SourceFileId => Source.FileId;

    public CompilationUnitSyntax Root { get; }

    public ImmutableArray<SyntaxToken> Tokens { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public static SyntaxTree Parse(SourceText source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LexedSource lexed = LexedSource.Lex(source);
        cancellationToken.ThrowIfCancellationRequested();
        var parser = new Parser(lexed.Tokens);
        CompilationUnitSyntax root = parser.ParseCompilationUnit();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(lexed.Diagnostics);
        diagnostics.AddRange(parser.Diagnostics);

        return new SyntaxTree(source, root, lexed.Tokens, diagnostics.ToImmutable());
    }
}
