namespace Xenon.Compiler;

public enum CompilationOutputKind
{
    Executable,
    Library,
}

/// <summary>Immutable target-independent options that affect a compilation snapshot.</summary>
public sealed record CompilationOptions(
    CompilationOutputKind OutputKind = CompilationOutputKind.Library);
