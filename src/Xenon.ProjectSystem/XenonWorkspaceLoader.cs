using System.Collections.Immutable;
using System.Text;

namespace Xenon.ProjectSystem;

public sealed record WorkspaceProjectEntry(
    string ProjectPath,
    XenonProject Project,
    bool IsExplicitMember);

/// <summary>Normalized immutable persistent Workspace configuration.</summary>
public sealed class WorkspaceConfiguration
{
    internal WorkspaceConfiguration(WorkspaceId id, string? name, string filePath,
        string rootDirectory, ImmutableArray<WorkspaceProjectEntry> projects,
        XenonProjectGraph graph)
    {
        Id = id;
        Name = name;
        FilePath = filePath;
        RootDirectory = rootDirectory;
        Projects = projects;
        Graph = graph;
    }

    public WorkspaceId Id { get; }
    public string? Name { get; }
    public string FilePath { get; }
    public string RootDirectory { get; }
    /// <summary>Explicit members in source order, followed by deterministic transitive dependencies.</summary>
    public ImmutableArray<WorkspaceProjectEntry> Projects { get; }
    public ImmutableArray<WorkspaceProjectEntry> ExplicitProjects =>
        Projects.Where(project => project.IsExplicitMember).ToImmutableArray();
    public XenonProjectGraph Graph { get; }
}

/// <summary>Strict loader for the protocol-independent .xws solution format.</summary>
public static class XenonWorkspaceLoader
{
    private static readonly ImmutableHashSet<string> SupportedKeys =
        ImmutableHashSet.Create(StringComparer.Ordinal, "name", "projects");

    public static WorkspaceConfiguration Load(string workspaceFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceFilePath);
        string fullPath;
        try
        {
            fullPath = ProjectPath.Normalize(workspaceFilePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            throw new ProjectSystemException($"invalid workspace path '{workspaceFilePath}': {exception.Message}",
                exception);
        }
        if (!File.Exists(fullPath))
            throw new ProjectSystemException($"workspace file '{workspaceFilePath}' does not exist");
        if (!string.Equals(Path.GetExtension(fullPath), ".xws", StringComparison.OrdinalIgnoreCase))
            throw new ProjectSystemException($"workspace file '{workspaceFilePath}' must use the .xws extension");

        Dictionary<string, Setting> settings;
        try
        {
            settings = Parse(File.ReadAllLines(fullPath, Encoding.UTF8), fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectSystemException($"cannot read workspace file '{fullPath}': {exception.Message}",
                exception);
        }
        foreach ((string key, Setting setting) in settings)
            if (!SupportedKeys.Contains(key))
                throw Error(fullPath, setting.Line, $"unknown workspace setting '{key}'");
        if (!settings.TryGetValue("projects", out Setting projectsSetting))
            throw new ProjectSystemException($"workspace file '{fullPath}' is missing required setting 'workspace.projects'");

        string directory = Path.GetDirectoryName(fullPath)!;
        ImmutableArray<string> declared = ParseArray(projectsSetting, "projects", fullPath);
        if (declared.IsEmpty)
            throw Error(fullPath, projectsSetting.Line, "workspace.projects must contain at least one .xeproj");
        ImmutableArray<string> explicitPaths = declared.Select(path =>
            ResolveProjectPath(path, directory, projectsSetting.Line, fullPath)).ToImmutableArray();
        string? duplicate = explicitPaths.GroupBy(path => path, ProjectPath.Comparer)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (duplicate is not null)
            throw Error(fullPath, projectsSetting.Line,
                $"duplicate workspace project resolves to '{duplicate}'");

        var byIdentity = new Dictionary<string, XenonProject>(ProjectPath.Comparer);
        var explicitProjects = ImmutableArray.CreateBuilder<XenonProject>();
        foreach (string projectPath in explicitPaths)
        {
            XenonProject project = XenonProjectLoader.LoadProjectFile(projectPath);
            if (!byIdentity.TryAdd(project.Identity, project))
                throw Error(fullPath, projectsSetting.Line,
                    $"duplicate logical workspace project '{project.Identity}'");
            explicitProjects.Add(project);
        }
        void DiscoverDependencies(XenonProject project)
        {
            foreach (string referencePath in project.ProjectReferences.Order(ProjectPath.Comparer))
            {
                if (!File.Exists(referencePath))
                    throw new ProjectSystemException(
                        $"project '{project.Name}' references missing project '{referencePath}'");
                if (!byIdentity.TryGetValue(referencePath, out XenonProject? dependency))
                {
                    dependency = XenonProjectLoader.LoadProjectFile(referencePath);
                    byIdentity.Add(dependency.Identity, dependency);
                }
                DiscoverDependenciesOnce(dependency);
            }
        }
        var discovered = new HashSet<string>(ProjectPath.Comparer);
        void DiscoverDependenciesOnce(XenonProject project)
        {
            if (discovered.Add(project.Identity)) DiscoverDependencies(project);
        }
        foreach (XenonProject project in explicitProjects) DiscoverDependenciesOnce(project);

        XenonProject primary = explicitProjects[0];
        XenonProjectGraph graph = XenonProjectGraph.Create(primary, byIdentity.Values);
        var entries = ImmutableArray.CreateBuilder<WorkspaceProjectEntry>();
        foreach (XenonProject project in explicitProjects)
            entries.Add(new WorkspaceProjectEntry(project.Identity, project, IsExplicitMember: true));
        foreach (XenonProject project in byIdentity.Values
            .Where(project => !explicitPaths.Contains(project.Identity, ProjectPath.Comparer))
            .OrderBy(project => project.Identity, ProjectPath.Comparer))
            entries.Add(new WorkspaceProjectEntry(project.Identity, project, IsExplicitMember: false));
        string? name = settings.TryGetValue("name", out Setting nameSetting)
            ? ParseString(nameSetting, "name", fullPath) : null;
        if (name is not null && string.IsNullOrWhiteSpace(name))
            throw Error(fullPath, nameSetting.Line, "workspace name cannot be empty");
        return new WorkspaceConfiguration(WorkspaceId.FromNormalizedPath(fullPath), name, fullPath,
            directory, entries.ToImmutable(), graph);
    }

    private static string ResolveProjectPath(string path, string directory, int line, string workspacePath)
    {
        string fullPath;
        try
        {
            fullPath = ProjectPath.Normalize(path, directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
            or PathTooLongException)
        {
            throw Error(workspacePath, line, $"invalid workspace project path '{path}': {exception.Message}");
        }
        if (!string.Equals(Path.GetExtension(fullPath), ".xeproj", StringComparison.OrdinalIgnoreCase))
            throw Error(workspacePath, line, $"workspace project '{path}' must use the .xeproj extension");
        if (!File.Exists(fullPath))
            throw Error(workspacePath, line, $"workspace project '{path}' does not exist");
        return fullPath;
    }

    private static Dictionary<string, Setting> Parse(string[] lines, string path)
    {
        var settings = new Dictionary<string, Setting>(StringComparer.Ordinal);
        bool inWorkspace = false;
        for (int index = 0; index < lines.Length; index++)
        {
            int line = index + 1;
            string text = StripComment(lines[index]).Trim();
            if (text.Length == 0) continue;
            if (text.StartsWith("[", StringComparison.Ordinal))
            {
                if (text != "[workspace]")
                    throw Error(path, line, $"unknown workspace section '{text}'");
                inWorkspace = true;
                continue;
            }
            if (!inWorkspace) throw Error(path, line, "settings must appear inside [workspace]");
            int equals = text.IndexOf('=');
            if (equals <= 0 || equals == text.Length - 1)
                throw Error(path, line, "expected 'name = value'");
            string key = text[..equals].Trim();
            string value = text[(equals + 1)..].Trim();
            if (value.StartsWith("[", StringComparison.Ordinal) && !IsCompleteArray(value))
            {
                var builder = new StringBuilder(value);
                while (!IsCompleteArray(builder.ToString()))
                {
                    if (++index == lines.Length) throw Error(path, line, "unterminated array value");
                    builder.AppendLine().Append(StripComment(lines[index]).Trim());
                }
                value = builder.ToString();
            }
            if (!settings.TryAdd(key, new Setting(value, line)))
                throw Error(path, line, $"workspace setting 'workspace.{key}' is already defined");
        }
        return settings;
    }

    private static string ParseString(Setting setting, string key, string path)
    {
        string value = setting.Value;
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
            throw Error(path, setting.Line, $"workspace setting 'workspace.{key}' must be a quoted string");
        return Unescape(value[1..^1], path, setting.Line);
    }

    private static ImmutableArray<string> ParseArray(Setting setting, string key, string path)
    {
        string value = setting.Value.Trim();
        if (value.Length < 2 || value[0] != '[' || value[^1] != ']')
            throw Error(path, setting.Line, $"workspace setting 'workspace.{key}' must be an array of strings");
        var items = ImmutableArray.CreateBuilder<string>();
        int index = 1;
        while (true)
        {
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
            if (index == value.Length - 1) return items.ToImmutable();
            if (value[index] != '"')
                throw Error(path, setting.Line, $"workspace setting 'workspace.{key}' must contain quoted strings");
            int start = ++index;
            bool escaped = false;
            while (index < value.Length - 1)
            {
                if (escaped) escaped = false;
                else if (value[index] == '\\') escaped = true;
                else if (value[index] == '"') break;
                index++;
            }
            if (index >= value.Length - 1)
                throw Error(path, setting.Line, "unterminated string in workspace array");
            items.Add(Unescape(value[start..index], path, setting.Line));
            index++;
            while (index < value.Length && char.IsWhiteSpace(value[index])) index++;
            if (index == value.Length - 1) return items.ToImmutable();
            if (value[index++] != ',') throw Error(path, setting.Line, "expected ',' between workspace projects");
        }
    }

    private static string Unescape(string value, string path, int line)
    {
        var result = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\') { result.Append(value[index]); continue; }
            if (++index == value.Length) throw Error(path, line, "unterminated escape sequence");
            result.Append(value[index] switch
            {
                '"' => '"', '\\' => '\\', 'n' => '\n', 'r' => '\r', 't' => '\t',
                _ => throw Error(path, line, $"unsupported escape sequence '\\{value[index]}'"),
            });
        }
        return result.ToString();
    }

    private static string StripComment(string value)
    {
        bool quoted = false, escaped = false;
        for (int index = 0; index < value.Length; index++)
        {
            if (escaped) { escaped = false; continue; }
            if (quoted && value[index] == '\\') { escaped = true; continue; }
            if (value[index] == '"') quoted = !quoted;
            else if (!quoted && value[index] == '#') return value[..index];
        }
        return value;
    }

    private static bool IsCompleteArray(string value)
    {
        bool quoted = false, escaped = false;
        int depth = 0;
        foreach (char character in value)
        {
            if (escaped) { escaped = false; continue; }
            if (quoted && character == '\\') { escaped = true; continue; }
            if (character == '"') quoted = !quoted;
            else if (!quoted && character == '[') depth++;
            else if (!quoted && character == ']') depth--;
        }
        return depth == 0 && !quoted;
    }

    private static ProjectSystemException Error(string path, int line, string message) =>
        new($"{path}({line}): {message}");

    private readonly record struct Setting(string Value, int Line);
}
