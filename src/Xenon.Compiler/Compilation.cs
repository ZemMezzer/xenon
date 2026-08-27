using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler;

public sealed class Compilation
{
    private Compilation(ImmutableArray<SyntaxTree> syntaxTrees, ITargetTypeLayout? targetLayout = null)
    {
        SyntaxTrees = syntaxTrees;
        ImmutableArray<Diagnostic> syntaxDiagnostics = syntaxTrees
            .SelectMany(tree => tree.Diagnostics)
            .ToImmutableArray();

        SemanticModel = syntaxDiagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            ? SemanticModel.CreateEmpty()
            : SemanticAnalyzer.Analyze(syntaxTrees, targetLayout);
        Diagnostics = [.. syntaxDiagnostics, .. SemanticModel.Diagnostics];
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public SemanticModel SemanticModel { get; }

    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    public bool RequiresTargetLayout => SemanticModel.RequiresTargetLayout;

    /// <summary>Rebinds the immutable syntax trees for an ABI without mutating this compilation.</summary>
    public Compilation WithTargetLayout(ITargetTypeLayout targetLayout)
    {
        ArgumentNullException.ThrowIfNull(targetLayout);
        return new Compilation(SyntaxTrees, targetLayout);
    }

    public static Compilation Create(params SourceText[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return new Compilation(sources.Select(SyntaxTree.Parse).ToImmutableArray());
    }
}
