using System.Collections.Immutable;

namespace Xenon.ProjectSystem;

/// <summary>Deterministic dependency graph of normalized Xenon project snapshots.</summary>
public sealed class XenonProjectGraph
{
    private readonly ImmutableDictionary<string, ImmutableArray<string>> _dependencies;

    private XenonProjectGraph(XenonProject root, ImmutableArray<XenonProject> projects,
        ImmutableArray<XenonProject> buildOrder,
        ImmutableDictionary<string, ImmutableArray<string>> dependencies)
    {
        Root = root;
        Projects = projects;
        BuildOrder = buildOrder;
        _dependencies = dependencies;
    }

    public XenonProject Root { get; }
    public ImmutableArray<XenonProject> Projects { get; }
    /// <summary>Dependencies precede their dependents; ordering is stable by project identity.</summary>
    public ImmutableArray<XenonProject> BuildOrder { get; }

    public ImmutableArray<XenonProject> GetDirectDependencies(XenonProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var byId = Projects.ToDictionary(item => item.Identity, ProjectPath.Comparer);
        return _dependencies.GetValueOrDefault(project.Identity, [])
            .Select(identity => byId[identity]).ToImmutableArray();
    }

    public ImmutableArray<XenonProject> GetTransitiveDependencies(XenonProject project)
    {
        var visited = new HashSet<string>(ProjectPath.Comparer);
        var result = ImmutableArray.CreateBuilder<XenonProject>();
        void Visit(XenonProject current)
        {
            foreach (XenonProject dependency in GetDirectDependencies(current))
            {
                if (!visited.Add(dependency.Identity)) continue;
                Visit(dependency);
                result.Add(dependency);
            }
        }
        Visit(project);
        return result.ToImmutable();
    }

    /// <summary>Native libraries in dependent-before-dependency order for one-pass Unix archive resolution.</summary>
    public ImmutableArray<XenonProject> GetNativeLinkOrder(XenonProject project) =>
        GetTransitiveDependencies(project).Reverse().ToImmutableArray();

    public static XenonProjectGraph Load(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (File.Exists(inputPath) && string.Equals(Path.GetExtension(inputPath), ".xws",
            StringComparison.OrdinalIgnoreCase))
            return XenonWorkspaceLoader.Load(inputPath).Graph;
        XenonProject root = XenonProjectLoader.Resolve(inputPath);
        var projects = new Dictionary<string, XenonProject>(ProjectPath.Comparer)
        {
            [root.Identity] = root,
        };
        var discovered = new HashSet<string>(ProjectPath.Comparer);
        void Discover(XenonProject project)
        {
            if (!discovered.Add(project.Identity)) return;
            foreach (string referencePath in project.ProjectReferences.Order(ProjectPath.Comparer))
            {
                if (!File.Exists(referencePath))
                    throw new ProjectSystemException(
                        $"project '{project.Name}' references missing project '{referencePath}'");
                XenonProject dependency = XenonProjectLoader.LoadProjectFile(referencePath);
                if (!projects.TryAdd(dependency.Identity, dependency))
                    dependency = projects[dependency.Identity];
                Discover(dependency);
            }
        }
        Discover(root);
        return Create(root, projects.Values);
    }

    /// <summary>Creates the same graph from tooling-provided normalized project snapshots.</summary>
    public static XenonProjectGraph Create(XenonProject root, IEnumerable<XenonProject> projects)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(projects);
        var byId = new Dictionary<string, XenonProject>(ProjectPath.Comparer);
        foreach (XenonProject project in projects.Append(root))
        {
            ArgumentNullException.ThrowIfNull(project);
            if (!Enum.IsDefined(project.Type))
                throw new ProjectSystemException($"project '{project.Name}' has invalid project type '{project.Type}'");
            if (byId.TryGetValue(project.Identity, out XenonProject? existing))
            {
                if (!ReferenceEquals(existing, project))
                    throw new ProjectSystemException(
                        $"project identity '{project.Identity}' is represented by conflicting configuration snapshots");
            }
            else byId.Add(project.Identity, project);
        }
        var visiting = new List<string>();
        var visited = new HashSet<string>(ProjectPath.Comparer);
        var order = ImmutableArray.CreateBuilder<XenonProject>();
        var dependencies = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
            ProjectPath.Comparer);
        void Visit(XenonProject project)
        {
            int cycleStart = visiting.FindIndex(identity =>
                string.Equals(identity, project.Identity, ProjectPath.Comparison));
            if (cycleStart >= 0)
                throw new ProjectSystemException($"project reference cycle detected: {string.Join(" -> ", visiting.Skip(cycleStart).Append(project.Identity))}");
            if (!visited.Add(project.Identity)) return;
            visiting.Add(project.Identity);
            var direct = ImmutableArray.CreateBuilder<string>();
            if (project.ProjectReferences.Distinct(ProjectPath.Comparer).Count() !=
                project.ProjectReferences.Length)
                throw new ProjectSystemException($"project '{project.Name}' contains duplicate project references");
            foreach (string identity in project.ProjectReferences.Order(ProjectPath.Comparer))
            {
                if (!byId.TryGetValue(identity, out XenonProject? dependency))
                    throw new ProjectSystemException($"project '{project.Name}' references missing project '{identity}'");
                if (dependency.Type == XenonProjectType.Executable)
                    throw new ProjectSystemException(
                        $"project '{project.Name}' cannot reference executable project '{dependency.Name}'");
                direct.Add(dependency.Identity);
                Visit(dependency);
            }
            dependencies[project.Identity] = direct.ToImmutable();
            visiting.RemoveAt(visiting.Count - 1);
            order.Add(project);
        }
        Visit(root);
        // Workspace manifests may include additional, independent tooling projects.
        foreach (XenonProject project in byId.Values.OrderBy(item => item.Identity,
            ProjectPath.Comparer))
            Visit(project);
        EnsureUniqueProjectNames(byId.Values);
        return new XenonProjectGraph(root,
            byId.Values.OrderBy(project => project.Identity, ProjectPath.Comparer).ToImmutableArray(),
            order.ToImmutable(), dependencies.ToImmutable());
    }

    private static void EnsureUniqueProjectNames(IEnumerable<XenonProject> projects)
    {
        string[] duplicates = projects.GroupBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (duplicates.Length != 0)
            throw new ProjectSystemException(
                $"project graph contains duplicate project name(s): {string.Join(", ", duplicates)}");
    }
}
