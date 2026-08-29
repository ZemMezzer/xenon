namespace Xenon.Compiler;

/// <summary>A stable semantic input captured by a compilation snapshot.</summary>
public abstract class CompilationReference : IEquatable<CompilationReference>
{
    protected CompilationReference(Guid identity) => Identity = identity;

    public Guid Identity { get; }

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
}
