using System.Collections.Immutable;

namespace Xenon.ProjectSystem;

public enum XenonProjectType
{
    Executable,
    StaticLibrary,
    SharedLibrary,
}

public sealed record XenonBuildProfile(
    int OptimizationLevel,
    bool EmitDebugInformation,
    bool EnableChecks)
{
    public static XenonBuildProfile Debug { get; } = new(0, true, true);

    public static XenonBuildProfile Release { get; } = new(3, false, false);
}

public sealed class XenonProject
{
    internal XenonProject(
        string name,
        XenonProjectType type,
        string? version,
        string rootDirectory,
        string sourceRoot,
        string? projectFilePath,
        ImmutableArray<string> sourceFiles,
        ImmutableArray<string> nativeLibraries,
        ImmutableArray<string> nativeLibraryPaths,
        XenonBuildProfile debugProfile,
        XenonBuildProfile releaseProfile)
    {
        Name = name;
        Type = type;
        Version = version;
        RootDirectory = rootDirectory;
        SourceRoot = sourceRoot;
        ProjectFilePath = projectFilePath;
        SourceFiles = sourceFiles;
        NativeLibraries = nativeLibraries;
        NativeLibraryPaths = nativeLibraryPaths;
        DebugProfile = debugProfile;
        ReleaseProfile = releaseProfile;
    }

    public string Name { get; }

    public XenonProjectType Type { get; }

    public string? Version { get; }

    public string RootDirectory { get; }

    public string SourceRoot { get; }

    public string? ProjectFilePath { get; }

    public ImmutableArray<string> SourceFiles { get; }

    public ImmutableArray<string> NativeLibraries { get; }

    public ImmutableArray<string> NativeLibraryPaths { get; }

    public XenonBuildProfile DebugProfile { get; }

    public XenonBuildProfile ReleaseProfile { get; }

    public bool IsImplicit => ProjectFilePath is null;

    public XenonBuildProfile GetProfile(string name) => name switch
    {
        "debug" => DebugProfile,
        "release" => ReleaseProfile,
        _ => throw new ProjectSystemException($"unknown build profile '{name}'"),
    };
}
