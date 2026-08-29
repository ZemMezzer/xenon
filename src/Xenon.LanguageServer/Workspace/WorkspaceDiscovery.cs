using Xenon.ProjectSystem;

namespace Xenon.LanguageServer;

public sealed record WorkspaceDiscoveryResult(
    Xenon.ProjectSystem.Workspace? Workspace,
    string? ConfigurationPath,
    string SearchRoot,
    bool IsLoose);

/// <summary>Deterministic, bounded discovery which delegates all parsing to ProjectSystem.</summary>
public static class WorkspaceDiscovery
{
    public const int MaximumParentTraversal = 32;

    public static WorkspaceDiscoveryResult Discover(string? explicitPath, string? rootUri,
        string? rootPath, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return LoadExplicit(explicitPath, cancellationToken);

        string? suppliedRoot = !string.IsNullOrWhiteSpace(rootUri)
            ? DocumentUri.ToNormalizedPath(rootUri) : rootPath;
        if (string.IsNullOrWhiteSpace(suppliedRoot))
            return new WorkspaceDiscoveryResult(null, null, Directory.GetCurrentDirectory(), IsLoose: true);

        string normalized = DocumentUri.NormalizePath(suppliedRoot);
        if (File.Exists(normalized))
        {
            string extension = Path.GetExtension(normalized);
            if (extension.Equals(".xws", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xeproj", StringComparison.OrdinalIgnoreCase))
                return LoadExplicit(normalized, cancellationToken);
            if (extension.Equals(".xe", StringComparison.OrdinalIgnoreCase))
                return new WorkspaceDiscoveryResult(
                    Xenon.ProjectSystem.Workspace.Create(normalized, cancellationToken: cancellationToken),
                    normalized, Path.GetDirectoryName(normalized)!, IsLoose: true);
            throw new ProjectSystemException($"initialization path '{normalized}' has an unsupported file type");
        }
        if (!Directory.Exists(normalized))
            throw new ProjectSystemException($"initialization path '{normalized}' does not exist");

        List<string> ancestors = EnumerateAncestors(normalized).ToList();
        string? workspaceFile = FindUnique(ancestors, "*.xws", "workspace");
        if (workspaceFile is not null)
            return new WorkspaceDiscoveryResult(
                Xenon.ProjectSystem.Workspace.Create(workspaceFile, cancellationToken: cancellationToken),
                workspaceFile, normalized, IsLoose: false);

        string? projectFile = FindUnique(ancestors, "*.xeproj", "project");
        if (projectFile is not null)
            return new WorkspaceDiscoveryResult(
                Xenon.ProjectSystem.Workspace.Create(projectFile, cancellationToken: cancellationToken),
                projectFile, normalized, IsLoose: false);

        return new WorkspaceDiscoveryResult(null, null, normalized, IsLoose: true);
    }

    public static Xenon.ProjectSystem.Workspace CreateLooseFile(string path,
        CancellationToken cancellationToken = default)
    {
        string normalized = DocumentUri.NormalizePath(path);
        if (!File.Exists(normalized))
            throw new ProjectSystemException($"loose Xenon source '{normalized}' does not exist");
        if (!Path.GetExtension(normalized).Equals(".xe", StringComparison.OrdinalIgnoreCase))
            throw new ProjectSystemException($"loose source '{normalized}' must use the .xe extension");
        return Xenon.ProjectSystem.Workspace.Create(normalized, cancellationToken: cancellationToken);
    }

    private static WorkspaceDiscoveryResult LoadExplicit(string path, CancellationToken cancellationToken)
    {
        string normalized = DocumentUri.NormalizePath(path);
        if (!File.Exists(normalized) && !Directory.Exists(normalized))
            throw new ProjectSystemException($"explicit initialization path '{normalized}' does not exist");
        var workspace = Xenon.ProjectSystem.Workspace.Create(normalized,
            cancellationToken: cancellationToken);
        bool loose = File.Exists(normalized) &&
            Path.GetExtension(normalized).Equals(".xe", StringComparison.OrdinalIgnoreCase);
        return new WorkspaceDiscoveryResult(workspace, normalized,
            Directory.Exists(normalized) ? normalized : Path.GetDirectoryName(normalized)!, loose);
    }

    private static IEnumerable<string> EnumerateAncestors(string start)
    {
        var current = new DirectoryInfo(start);
        for (int depth = 0; current is not null && depth < MaximumParentTraversal; depth++)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static string? FindUnique(IEnumerable<string> directories, string pattern, string kind)
    {
        foreach (string directory in directories)
        {
            string[] candidates = Directory.EnumerateFiles(directory, pattern,
                    SearchOption.TopDirectoryOnly)
                .Select(DocumentUri.NormalizePath)
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (candidates.Length > 1)
                throw new ProjectSystemException(
                    $"directory '{directory}' contains multiple {kind} files; specify one explicitly");
            if (candidates.Length == 1) return candidates[0];
        }
        return null;
    }
}
