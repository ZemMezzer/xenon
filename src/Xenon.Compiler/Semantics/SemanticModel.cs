using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

public sealed class SemanticModel
{
    internal SemanticModel(
        NamespaceSymbol globalNamespace,
        ImmutableArray<BoundFunction> functions,
        ImmutableArray<Diagnostic> diagnostics,
        bool requiresTargetLayout = false)
    {
        GlobalNamespace = globalNamespace;
        Functions = functions;
        Diagnostics = diagnostics;
        RequiresTargetLayout = requiresTargetLayout;
    }

    public NamespaceSymbol GlobalNamespace { get; }

    public ImmutableArray<BoundFunction> Functions { get; }

    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool RequiresTargetLayout { get; }

    internal static SemanticModel CreateEmpty() => new(
        new NamespaceSymbol(string.Empty, null),
        [],
        []);
}
