namespace Xenon.Compiler;

public enum CompilationOutputKind
{
    Executable,
    Library,
}

/// <summary>Immutable options that affect a compilation snapshot.</summary>
public sealed record CompilationOptions(
    CompilationOutputKind OutputKind = CompilationOutputKind.Library,
    bool EnableRuntimeChecks = true,
    ConditionalCompilationOptions? ConditionalCompilation = null)
{
    public ConditionalCompilationOptions ConditionalOptions => ConditionalCompilation ?? ConditionalCompilationOptions.Default;
}
