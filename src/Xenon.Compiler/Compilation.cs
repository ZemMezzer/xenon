using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler;

public sealed class Compilation
{
    private Compilation(ImmutableArray<SyntaxTree> syntaxTrees, ITargetTypeLayout? targetLayout = null,
        CancellationToken cancellationToken = default)
    {
        SyntaxTrees = syntaxTrees;
        cancellationToken.ThrowIfCancellationRequested();
        SemanticModel = SemanticAnalyzer.Analyze(syntaxTrees, TypeFactory, targetLayout, cancellationToken);
        Diagnostics = SemanticModel.Diagnostics;
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public SemanticModel SemanticModel { get; }

    public TypeFactory TypeFactory { get; } = new();

    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public bool RequiresTargetLayout => SemanticModel.RequiresTargetLayout;

    /// <summary>Rebinds the immutable syntax trees for an ABI without mutating this compilation.</summary>
    public Compilation WithTargetLayout(ITargetTypeLayout targetLayout)
        => WithTargetLayout(targetLayout, CancellationToken.None);

    public Compilation WithTargetLayout(ITargetTypeLayout targetLayout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetLayout);
        return new Compilation(SyntaxTrees, targetLayout, cancellationToken);
    }

    public static Compilation Create(params SourceText[] sources) => Create(CancellationToken.None, sources);

    public static Compilation Create(CancellationToken cancellationToken, params SourceText[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        return new Compilation(sources.Select(source => SyntaxTree.Parse(source, cancellationToken)).ToImmutableArray(),
            cancellationToken: cancellationToken);
    }

    public SemanticModel GetSemanticModel(SyntaxTree tree)
    {
        ArgumentNullException.ThrowIfNull(tree);
        if (!SyntaxTrees.Any(candidate => ReferenceEquals(candidate, tree)))
            throw new ArgumentException("The syntax tree does not belong to this compilation.", nameof(tree));
        return SemanticModel.ForTree(tree);
    }
}
