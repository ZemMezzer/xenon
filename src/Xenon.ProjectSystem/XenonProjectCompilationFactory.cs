using Xenon.Compiler;
using Xenon.Compiler.Text;

namespace Xenon.ProjectSystem;

/// <summary>Shared project-configuration to compiler-snapshot projection for build and tooling.</summary>
public static class XenonProjectCompilationFactory
{
    public static Compilation Create(
        XenonProject project,
        string profileName,
        IReadOnlyDictionary<string, Compilation>? dependencyCompilations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        SourceText[] sources = project.SourceFiles.Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SourceText.From(File.ReadAllText(path), path);
        }).ToArray();
        return Create(project, profileName, sources, dependencyCompilations, cancellationToken);
    }

    public static Compilation Create(
        XenonProject project,
        string profileName,
        IEnumerable<SourceText> sources,
        IReadOnlyDictionary<string, Compilation>? dependencyCompilations = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sources);
        dependencyCompilations ??= new Dictionary<string, Compilation>(StringComparer.OrdinalIgnoreCase);
        var references = new List<CompilationReference>(project.ProjectReferences.Length);
        foreach (string identity in project.ProjectReferences)
        {
            if (!dependencyCompilations.TryGetValue(identity, out Compilation? dependency))
                throw new ProjectSystemException(
                    $"compilation for project reference '{identity}' is unavailable while compiling '{project.Name}'");
            references.Add(new SourceCompilationReference(dependency));
        }
        XenonBuildProfile profile = project.GetProfile(profileName);
        var options = new CompilationOptions(
            project.Type == XenonProjectType.Executable
                ? CompilationOutputKind.Executable : CompilationOutputKind.Library);
        return Compilation.Create(options, references, cancellationToken, sources.ToArray());
    }
}
