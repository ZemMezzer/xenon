using Xenon.Compiler;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xenon.Compiler.Libraries;

namespace Xenon.ProjectSystem;

/// <summary>Shared project-configuration to compiler-snapshot projection for build and tooling.</summary>
public static class XenonProjectCompilationFactory
{
    public static Compilation Create(
        XenonProject project,
        string profileName,
        IReadOnlyDictionary<string, Compilation>? dependencyCompilations = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, LibraryCompilationReference>? dependencyLibraries = null,
        bool metadataOnlyXelib = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        SourceText[] sources = project.SourceFiles.Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SourceText.From(File.ReadAllText(path), path);
        }).ToArray();
        return Create(project, profileName, sources, dependencyCompilations, cancellationToken,
            dependencyLibraries, metadataOnlyXelib);
    }

    public static Compilation Create(
        XenonProject project,
        string profileName,
        IEnumerable<SourceText> sources,
        IReadOnlyDictionary<string, Compilation>? dependencyCompilations = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, LibraryCompilationReference>? dependencyLibraries = null,
        bool metadataOnlyXelib = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sources);
        dependencyCompilations ??= new Dictionary<string, Compilation>(ProjectPath.Comparer);
        dependencyLibraries ??= new Dictionary<string, LibraryCompilationReference>(ProjectPath.Comparer);
        var references = new List<CompilationReference>(project.ProjectReferences.Length + project.XenonLibraries.Length);
        var libraryIdentities = new HashSet<string>(StringComparer.Ordinal);
        var libraryVersions = new Dictionary<(string Name, string? Version), string>();
        void AddLibraryClosure(LibraryCompilationReference library)
        {
            var logicalIdentity = (library.LibraryIdentity.Name, library.LibraryIdentity.Version);
            if (libraryVersions.TryGetValue(logicalIdentity, out string? existingIdentity) &&
                !string.Equals(existingIdentity, library.LibraryIdentity.ContentIdentity, StringComparison.Ordinal))
                throw new XelibFormatException(XelibErrorCode.DuplicateLibraryIdentity,
                    $"multiple incompatible XELIB artifacts declare '{logicalIdentity.Name}' version '{logicalIdentity.Version}'");
            libraryVersions.TryAdd(logicalIdentity, library.LibraryIdentity.ContentIdentity);
            if (!libraryIdentities.Add(library.LibraryIdentity.ContentIdentity)) return;
            references.Add(library);
            foreach (LibraryCompilationReference dependency in library.Dependencies)
                AddLibraryClosure(dependency);
        }
        foreach (string identity in project.ProjectReferences)
        {
            if (dependencyLibraries.TryGetValue(identity, out LibraryCompilationReference? library))
            {
                AddLibraryClosure(library);
                continue;
            }
            if (!dependencyCompilations.TryGetValue(identity, out Compilation? dependency))
                throw new ProjectSystemException(
                    $"compilation for project reference '{identity}' is unavailable while compiling '{project.Name}'");
            references.Add(new SourceCompilationReference(dependency));
        }
        foreach (LibraryCompilationReference library in
                 XelibReferenceLoader.LoadFiles(project.XenonLibraries, metadataOnlyXelib))
            AddLibraryClosure(library);
        XenonBuildProfile profile = project.GetProfile(profileName);
        var options = new CompilationOptions(
            project.Type == XenonProjectType.Executable
                ? CompilationOutputKind.Executable : CompilationOutputKind.Library,
            profile.EnableChecks);
        return Compilation.Create(options, references, cancellationToken, sources.ToArray());
    }

    /// <summary>Creates a project compilation from already parsed immutable tooling trees.</summary>
    public static Compilation Create(
        XenonProject project,
        string profileName,
        IEnumerable<SyntaxTree> syntaxTrees,
        IReadOnlyDictionary<string, Compilation>? dependencyCompilations = null,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, LibraryCompilationReference>? dependencyLibraries = null,
        bool metadataOnlyXelib = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        dependencyCompilations ??= new Dictionary<string, Compilation>(ProjectPath.Comparer);
        dependencyLibraries ??= new Dictionary<string, LibraryCompilationReference>(ProjectPath.Comparer);
        var references = new List<CompilationReference>(project.ProjectReferences.Length + project.XenonLibraries.Length);
        var libraryIdentities = new HashSet<string>(StringComparer.Ordinal);
        var libraryVersions = new Dictionary<(string Name, string? Version), string>();
        void AddLibraryClosure(LibraryCompilationReference library)
        {
            var logicalIdentity = (library.LibraryIdentity.Name, library.LibraryIdentity.Version);
            if (libraryVersions.TryGetValue(logicalIdentity, out string? existingIdentity) &&
                !string.Equals(existingIdentity, library.LibraryIdentity.ContentIdentity, StringComparison.Ordinal))
                throw new XelibFormatException(XelibErrorCode.DuplicateLibraryIdentity,
                    $"multiple incompatible XELIB artifacts declare '{logicalIdentity.Name}' version '{logicalIdentity.Version}'");
            libraryVersions.TryAdd(logicalIdentity, library.LibraryIdentity.ContentIdentity);
            if (!libraryIdentities.Add(library.LibraryIdentity.ContentIdentity)) return;
            references.Add(library);
            foreach (LibraryCompilationReference dependency in library.Dependencies)
                AddLibraryClosure(dependency);
        }
        foreach (string identity in project.ProjectReferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (dependencyLibraries.TryGetValue(identity, out LibraryCompilationReference? library))
            {
                AddLibraryClosure(library);
                continue;
            }
            if (!dependencyCompilations.TryGetValue(identity, out Compilation? dependency))
                throw new ProjectSystemException(
                    $"compilation for project reference '{identity}' is unavailable while compiling '{project.Name}'");
            references.Add(new SourceCompilationReference(dependency));
        }
        foreach (LibraryCompilationReference library in
                 XelibReferenceLoader.LoadFiles(project.XenonLibraries, metadataOnlyXelib))
            AddLibraryClosure(library);
        XenonBuildProfile profile = project.GetProfile(profileName);
        var options = new CompilationOptions(project.Type == XenonProjectType.Executable
            ? CompilationOutputKind.Executable : CompilationOutputKind.Library,
            profile.EnableChecks);
        return Compilation.Create(syntaxTrees, options, references, cancellationToken);
    }
}
