using System.Collections.Concurrent;
using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler;

public sealed class Compilation
{
    private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _semanticModels =
        new(ReferenceEqualityComparer.Instance);

    private Compilation(ImmutableArray<SyntaxTree> syntaxTrees, CompilationOptions options,
        ImmutableArray<CompilationReference> references, ITargetTypeLayout? targetLayout,
        CancellationToken cancellationToken)
    {
        ValidateTrees(syntaxTrees);
        ValidateReferences(references);
        SyntaxTrees = syntaxTrees;
        Options = options;
        References = references;
        TargetLayout = targetLayout;
        cancellationToken.ThrowIfCancellationRequested();
        TypeFactory = new TypeFactory();
        SemanticModel = SemanticAnalyzer.Analyze(syntaxTrees, TypeFactory,
            references.OfType<SourceCompilationReference>()
                .Select(reference => reference.Compilation.SemanticModel.GlobalNamespace)
                .ToImmutableArray(), targetLayout, cancellationToken);
        Diagnostics = SemanticModel.Diagnostics;
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public CompilationOptions Options { get; }
    public ImmutableArray<CompilationReference> References { get; }
    public ITargetTypeLayout? TargetLayout { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public SemanticModel SemanticModel { get; }
    public TypeFactory TypeFactory { get; }
    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    public bool RequiresTargetLayout => SemanticModel.RequiresTargetLayout;

    public static Compilation Create(params SourceText[] sources) =>
        Create(new CompilationOptions(), [], CancellationToken.None, sources);

    public static Compilation Create(CancellationToken cancellationToken, params SourceText[] sources) =>
        Create(new CompilationOptions(), [], cancellationToken, sources);

    public static Compilation Create(CompilationOptions options,
        IEnumerable<CompilationReference>? references, params SourceText[] sources) =>
        Create(options, references, CancellationToken.None, sources);

    public static Compilation Create(CompilationOptions options,
        IEnumerable<CompilationReference>? references, CancellationToken cancellationToken,
        params SourceText[] sources)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        return new Compilation(
            sources.Select(source => SyntaxTree.Parse(source, cancellationToken)).ToImmutableArray(),
            options, references?.ToImmutableArray() ?? [], null, cancellationToken);
    }

    public static Compilation Create(IEnumerable<SyntaxTree> syntaxTrees,
        CompilationOptions? options = null, IEnumerable<CompilationReference>? references = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        return new Compilation(syntaxTrees.ToImmutableArray(), options ?? new CompilationOptions(),
            references?.ToImmutableArray() ?? [], null, cancellationToken);
    }

    public Compilation WithOptions(CompilationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options == Options ? this : Derive(SyntaxTrees, options, References, TargetLayout, cancellationToken);
    }

    public Compilation WithReferences(IEnumerable<CompilationReference> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        ImmutableArray<CompilationReference> value = references.ToImmutableArray();
        return References.SequenceEqual(value) ? this : Derive(SyntaxTrees, Options, value, TargetLayout, cancellationToken);
    }

    public Compilation AddReferences(params CompilationReference[] references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return references.Length == 0 ? this : WithReferences(References.AddRange(references));
    }

    public Compilation RemoveReferences(params CompilationReference[] references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var remove = references.ToHashSet();
        if (remove.Any(reference => !References.Contains(reference)))
            throw new ArgumentException("Every reference to remove must belong to this compilation.", nameof(references));
        return remove.Count == 0 ? this : WithReferences(References.Where(reference => !remove.Contains(reference)));
    }

    public Compilation ReplaceSyntaxTree(SyntaxTree oldTree, SyntaxTree newTree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldTree);
        ArgumentNullException.ThrowIfNull(newTree);
        int index = IndexOfTree(oldTree);
        if (index < 0)
            throw new ArgumentException("The syntax tree does not belong to this compilation.", nameof(oldTree));
        if (ReferenceEquals(oldTree, newTree)) return this;
        SyntaxTree replacement = newTree.SourceFileId == oldTree.SourceFileId ? newTree :
            SyntaxTree.Parse(SourceText.From(newTree.Source.Text, newTree.Source.Path, oldTree.SourceFileId), cancellationToken);
        if (SyntaxTrees.Where((_, candidateIndex) => candidateIndex != index)
            .Any(tree => tree.SourceFileId == replacement.SourceFileId))
            throw new ArgumentException("A syntax tree with the replacement source identity already belongs to this compilation.", nameof(newTree));
        return Derive(SyntaxTrees.SetItem(index, replacement), Options, References, TargetLayout, cancellationToken);
    }

    public Compilation AddSyntaxTrees(params SyntaxTree[] syntaxTrees) => AddSyntaxTrees((IEnumerable<SyntaxTree>)syntaxTrees);

    public Compilation AddSyntaxTrees(IEnumerable<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        ImmutableArray<SyntaxTree> added = syntaxTrees.ToImmutableArray();
        return added.IsEmpty ? this : Derive(SyntaxTrees.AddRange(added), Options, References, TargetLayout, cancellationToken);
    }

    public Compilation RemoveSyntaxTrees(params SyntaxTree[] syntaxTrees) => RemoveSyntaxTrees((IEnumerable<SyntaxTree>)syntaxTrees);

    public Compilation RemoveSyntaxTrees(IEnumerable<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        var remove = new HashSet<SyntaxTree>(syntaxTrees, ReferenceEqualityComparer.Instance);
        if (remove.Any(tree => IndexOfTree(tree) < 0))
            throw new ArgumentException("Every syntax tree to remove must belong to this compilation.", nameof(syntaxTrees));
        return remove.Count == 0 ? this : Derive(SyntaxTrees.Where(tree => !remove.Contains(tree)).ToImmutableArray(),
            Options, References, TargetLayout, cancellationToken);
    }

    /// <summary>Specializes this snapshot for an ABI without mutating it.</summary>
    public Compilation WithTargetLayout(ITargetTypeLayout targetLayout) => WithTargetLayout(targetLayout, CancellationToken.None);

    public Compilation WithTargetLayout(ITargetTypeLayout targetLayout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetLayout);
        return ReferenceEquals(TargetLayout, targetLayout) ? this :
            Derive(SyntaxTrees, Options, References, targetLayout, cancellationToken);
    }

    public SemanticModel GetSemanticModel(SyntaxTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        cancellationToken.ThrowIfCancellationRequested();
        if (IndexOfTree(tree) < 0)
            throw new ArgumentException("The syntax tree does not belong to this compilation.", nameof(tree));
        SemanticModel model = _semanticModels.GetOrAdd(tree, static (syntaxTree, compilation) =>
            compilation.SemanticModel.ForTree(syntaxTree), this);
        cancellationToken.ThrowIfCancellationRequested();
        return model;
    }

    /// <summary>True when this snapshot, rather than one of its references, defines the symbol.</summary>
    public bool IsSymbolDefinedHere(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        Symbol root = symbol;
        while (root.ContainingSymbol is Symbol containing) root = containing;
        return ReferenceEquals(root, SemanticModel.GlobalNamespace);
    }

    private Compilation Derive(ImmutableArray<SyntaxTree> syntaxTrees, CompilationOptions options,
        ImmutableArray<CompilationReference> references, ITargetTypeLayout? targetLayout,
        CancellationToken cancellationToken) => new(syntaxTrees, options, references, targetLayout, cancellationToken);

    private int IndexOfTree(SyntaxTree tree)
    {
        for (int index = 0; index < SyntaxTrees.Length; index++)
            if (ReferenceEquals(SyntaxTrees[index], tree)) return index;
        return -1;
    }

    private static void ValidateTrees(ImmutableArray<SyntaxTree> syntaxTrees)
    {
        if (syntaxTrees.Any(tree => tree is null))
            throw new ArgumentException("Syntax tree collections cannot contain null values.", nameof(syntaxTrees));
        var identities = new HashSet<SourceFileId>();
        foreach (SyntaxTree tree in syntaxTrees)
            if (!identities.Add(tree.SourceFileId))
                throw new ArgumentException($"Duplicate source identity '{tree.SourceFileId}'.", nameof(syntaxTrees));
    }

    private static void ValidateReferences(ImmutableArray<CompilationReference> references)
    {
        if (references.Any(reference => reference is null))
            throw new ArgumentException("Reference collections cannot contain null values.", nameof(references));
        if (references.Distinct().Count() != references.Length)
            throw new ArgumentException("Duplicate compilation references are not allowed.", nameof(references));
    }
}
