namespace Xenon.ProjectSystem;

public static class XenonBuildPaths
{
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

        string extension = targetTriple.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
            targetTriple.Contains("win32", StringComparison.OrdinalIgnoreCase)
                ? ".exe"
                : string.Empty;
        return Path.Combine(
            rootDirectory,
            "build",
            SanitizePathSegment(profileName),
            SanitizePathSegment(targetTriple),
            $"{SanitizePathSegment(projectName)}{extension}");
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalidCharacters = [.. Path.GetInvalidFileNameChars(), Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        var result = new string(value
            .Select(character => invalidCharacters.Contains(character) ? '_' : character)
            .ToArray());
        return string.IsNullOrWhiteSpace(result) ? "_" : result;
    }
}
