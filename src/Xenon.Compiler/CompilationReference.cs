using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Semantics;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Binding;

namespace Xenon.Compiler;

/// <summary>A stable semantic input captured by a compilation snapshot.</summary>
public abstract class CompilationReference : IEquatable<CompilationReference>
{
    protected CompilationReference(Guid identity) => Identity = identity;

    public Guid Identity { get; }

    /// <summary>The immutable semantic namespace surface imported by a consuming compilation.</summary>
    public abstract NamespaceSymbol GlobalNamespace { get; }

    /// <summary>Opaque compile-time implementations required to specialize exported generics.</summary>
    public virtual GenericImplementationStore GenericImplementations => GenericImplementationStore.Empty;

    public bool Equals(CompilationReference? other) =>
        other is not null && GetType() == other.GetType() && Identity == other.Identity;

    public override bool Equals(object? obj) => Equals(obj as CompilationReference);

    public override int GetHashCode() => HashCode.Combine(GetType(), Identity);
}

/// <summary>Pins one exact immutable Xenon compilation generation.</summary>
public sealed class SourceCompilationReference : CompilationReference
{
    public SourceCompilationReference(Compilation compilation)
        : base(Guid.NewGuid())
    {
        Compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
    }

    public Compilation Compilation { get; }

    public override NamespaceSymbol GlobalNamespace => Compilation.SemanticModel.GlobalNamespace;

    public override GenericImplementationStore GenericImplementations => Compilation.GenericImplementations;
}

/// <summary>A source-less, portable XELIB semantic and implementation snapshot.</summary>
public sealed class LibraryCompilationReference : CompilationReference
{
    private readonly ImmutableDictionary<string, Symbol> _exports;

    internal LibraryCompilationReference(XelibLibraryIdentity libraryIdentity,
        NamespaceSymbol globalNamespace, GenericImplementationStore genericImplementations,
        ImmutableArray<BoundFunction> implementationFunctions,
        ImmutableDictionary<string, Symbol> exports, string? path)
        : base(CreateIdentity(libraryIdentity.ContentIdentity))
    {
        LibraryIdentity = libraryIdentity;
        GlobalNamespace = globalNamespace;
        GenericImplementations = genericImplementations;
        ImplementationFunctions = implementationFunctions;
        _exports = exports;
        Path = path;
    }

    public XelibLibraryIdentity LibraryIdentity { get; }
    public override NamespaceSymbol GlobalNamespace { get; }
    public override GenericImplementationStore GenericImplementations { get; }
    public ImmutableArray<BoundFunction> ImplementationFunctions { get; }
    public string? Path { get; }

    internal bool TryResolveExport(string key, out Symbol symbol) => _exports.TryGetValue(key, out symbol!);

    private static Guid CreateIdentity(string contentIdentity)
    {
        byte[] digest;
        try { digest = Convert.FromHexString(contentIdentity); }
        catch (FormatException)
        {
            digest = SHA256.HashData(Encoding.UTF8.GetBytes(contentIdentity));
        }
        if (digest.Length < 16) digest = SHA256.HashData(digest);
        return new Guid(digest.AsSpan(0, 16));
    }
}
