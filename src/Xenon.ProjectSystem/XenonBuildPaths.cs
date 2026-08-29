namespace Xenon.ProjectSystem;

public static class XenonBuildPaths
{
    public static string GetArtifactPath(string rootDirectory, string projectName, XenonProjectType type,
        string profileName, string targetTriple) => type switch
    {
        XenonProjectType.Executable => GetExecutablePath(rootDirectory, projectName, profileName, targetTriple),
        XenonProjectType.StaticLibrary => GetStaticLibraryPath(rootDirectory, projectName, profileName, targetTriple),
        XenonProjectType.SharedLibrary => GetSharedLibraryPath(rootDirectory, projectName, profileName, targetTriple),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static string GetObjectFilePath(
        XenonProject project,
        string profileName,
        string targetTriple,
        string objectFileExtension)
    {
        ArgumentNullException.ThrowIfNull(project);
        return GetObjectFilePath(
            project.RootDirectory,
            project.Name,
            profileName,
            targetTriple,
            objectFileExtension);
    }

    public static string GetObjectFilePath(
        string rootDirectory,
        string projectName,
        string profileName,
        string targetTriple,
        string objectFileExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTriple);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectFileExtension);

        string extension = objectFileExtension.StartsWith('.')
            ? objectFileExtension
            : $".{objectFileExtension}";
        return Path.Combine(
            rootDirectory,
            ".xenon",
            "obj",
            SanitizePathSegment(profileName),
            SanitizePathSegment(targetTriple),
            $"{SanitizePathSegment(projectName)}{extension}");
    }

    public static string GetExecutablePath(
        string rootDirectory,
        string projectName,
        string profileName,
        string targetTriple)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTriple);

        string extension = IsWindowsTarget(targetTriple) ? ".exe" : string.Empty;
        return GetBuildArtifactPath(rootDirectory, projectName, profileName, targetTriple, projectName, extension);
    }

    public static string GetStaticLibraryPath(
        string rootDirectory,
        string projectName,
        string profileName,
        string targetTriple)
    {
        string fileName = IsWindowsTarget(targetTriple) ? projectName : $"lib{projectName}";
        string extension = IsWindowsTarget(targetTriple) ? ".lib" : ".a";
        return GetBuildArtifactPath(rootDirectory, projectName, profileName, targetTriple, fileName, extension);
    }

    public static string GetSharedLibraryPath(
        string rootDirectory,
        string projectName,
        string profileName,
        string targetTriple)
    {
        bool windows = IsWindowsTarget(targetTriple);
        string fileName = windows ? projectName : $"lib{projectName}";
        string extension = windows
            ? ".dll"
            : IsAppleTarget(targetTriple) ? ".dylib" : ".so";
        return GetBuildArtifactPath(rootDirectory, projectName, profileName, targetTriple, fileName, extension);
    }

    public static string? GetImportLibraryPath(
        string rootDirectory,
        string projectName,
        string profileName,
        string targetTriple) =>
        IsWindowsTarget(targetTriple)
            ? GetBuildArtifactPath(rootDirectory, projectName, profileName, targetTriple, projectName, ".lib")
            : null;

    private static string GetBuildArtifactPath(
        string rootDirectory,
        string projectName,
        string profileName,
        string targetTriple,
        string fileName,
        string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTriple);

        return Path.Combine(
            rootDirectory,
            "build",
            SanitizePathSegment(profileName),
            SanitizePathSegment(targetTriple),
            $"{SanitizePathSegment(fileName)}{extension}");
    }

    private static bool IsWindowsTarget(string triple) =>
        triple.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
        triple.Contains("win32", StringComparison.OrdinalIgnoreCase);

    private static bool IsAppleTarget(string triple) =>
        triple.Contains("darwin", StringComparison.OrdinalIgnoreCase) ||
        triple.Contains("macos", StringComparison.OrdinalIgnoreCase) ||
        triple.Contains("ios", StringComparison.OrdinalIgnoreCase);

    private static string SanitizePathSegment(string value)
    {
        char[] invalidCharacters = [.. Path.GetInvalidFileNameChars(), Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        var result = new string(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }
}
