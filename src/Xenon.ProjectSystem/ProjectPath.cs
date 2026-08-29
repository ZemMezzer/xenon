namespace Xenon.ProjectSystem;

/// <summary>One host-aware path policy shared by project and Workspace identity/ownership code.</summary>
internal static class ProjectPath
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static string Normalize(string path) => Path.GetFullPath(path);
    public static string Normalize(string path, string basePath) => Path.GetFullPath(path, basePath);

    public static string StableIdentity(string normalizedPath) => OperatingSystem.IsWindows()
        ? normalizedPath.ToUpperInvariant()
        : normalizedPath;
}
