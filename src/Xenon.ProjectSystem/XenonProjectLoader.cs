using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Xenon.ProjectSystem;

public static class XenonProjectLoader
{
    private static readonly HashSet<string> SupportedSettings = new(StringComparer.Ordinal)
    {
        "project.name",
        "project.type",
        "project.version",
        "source.root",
        "profile.debug.optimization",
        "profile.debug.debug-info",
        "profile.debug.checks",
        "profile.release.optimization",
        "profile.release.debug-info",
        "profile.release.checks",
    };

    public static XenonProject Resolve(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        string fullPath = Path.GetFullPath(inputPath);

        if (Directory.Exists(fullPath))
        {
            return LoadDirectory(fullPath);
        }

        if (!File.Exists(fullPath))
        {
            throw new ProjectSystemException($"input path '{inputPath}' does not exist");
        }

        string extension = Path.GetExtension(fullPath);
        if (string.Equals(extension, ".xeproj", StringComparison.OrdinalIgnoreCase))
        {
            return LoadProjectFile(fullPath);
        }

        if (string.Equals(extension, ".xe", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSingleFileProject(fullPath);
        }

        throw new ProjectSystemException(
            $"input path '{inputPath}' must be a directory, .xeproj, or .xe file");
    }

    public static XenonProject LoadDirectory(string directoryPath)
    {
        string directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
        {
            throw new ProjectSystemException($"project directory '{directoryPath}' does not exist");
        }

        string[] projectFiles = EnumerateFiles(directory, SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".xeproj", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return projectFiles.Length switch
        {
            0 => CreateImplicitDirectoryProject(directory),
            1 => LoadProjectFile(projectFiles[0]),
            _ => throw new ProjectSystemException(
                $"project directory '{directory}' contains multiple .xeproj files; specify one explicitly"),
        };
    }

    public static XenonProject LoadProjectFile(string projectFilePath)
    {
        string fullPath = Path.GetFullPath(projectFilePath);
        if (!File.Exists(fullPath))
        {
            throw new ProjectSystemException($"project file '{projectFilePath}' does not exist");
        }

        if (!string.Equals(Path.GetExtension(fullPath), ".xeproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectSystemException($"project file '{projectFilePath}' must use the .xeproj extension");
        }

        IReadOnlyDictionary<string, ProjectSetting> settings;
        try
        {
            settings = ParseSettings(File.ReadAllLines(fullPath, Encoding.UTF8), fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectSystemException($"cannot read project file '{fullPath}': {exception.Message}", exception);
        }

        foreach ((string key, ProjectSetting setting) in settings)
        {
            if (!SupportedSettings.Contains(key))
            {
                throw Error(fullPath, setting.Line, $"unknown project setting '{key}'");
            }
        }

        string name = GetRequiredString(settings, "project.name", fullPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            throw Error(fullPath, settings["project.name"].Line, "project name cannot be empty");
        }

        string typeText = GetRequiredString(settings, "project.type", fullPath);
        XenonProjectType type = typeText switch
        {
            "executable" => XenonProjectType.Executable,
            "static-library" => XenonProjectType.StaticLibrary,
            "shared-library" => XenonProjectType.SharedLibrary,
            _ => throw Error(
                fullPath,
                settings["project.type"].Line,
                $"unknown project type '{typeText}'"),
        };

        string? version = GetOptionalString(settings, "project.version", fullPath);
        string sourceRootText = GetOptionalString(settings, "source.root", fullPath) ?? "src";
        string rootDirectory = Path.GetDirectoryName(fullPath)!;
        string sourceRoot = Path.GetFullPath(sourceRootText, rootDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new ProjectSystemException(
                $"source root '{sourceRootText}' does not exist for project '{name}'");
        }

        ImmutableArray<string> sourceFiles = DiscoverSources(sourceRoot);
        EnsureHasSources(sourceFiles, sourceRoot);

        XenonBuildProfile debugProfile = ReadProfile(
            settings,
            "profile.debug",
            XenonBuildProfile.Debug,
            fullPath);
        XenonBuildProfile releaseProfile = ReadProfile(
            settings,
            "profile.release",
            XenonBuildProfile.Release,
            fullPath);

        return new XenonProject(
            name,
            type,
            version,
            rootDirectory,
            sourceRoot,
            fullPath,
            sourceFiles,
            debugProfile,
            releaseProfile);
    }

    private static XenonProject CreateSingleFileProject(string sourceFile)
    {
        string rootDirectory = Path.GetDirectoryName(sourceFile)!;
        return new XenonProject(
            Path.GetFileNameWithoutExtension(sourceFile),
            XenonProjectType.Executable,
            version: null,
            rootDirectory,
            rootDirectory,
            projectFilePath: null,
            [sourceFile],
            XenonBuildProfile.Debug,
            XenonBuildProfile.Release);
    }

    private static XenonProject CreateImplicitDirectoryProject(string directory)
    {
        ImmutableArray<string> sourceFiles = DiscoverSources(directory);
        EnsureHasSources(sourceFiles, directory);
        string name = new DirectoryInfo(directory).Name;
        return new XenonProject(
            name,
            XenonProjectType.Executable,
            version: null,
            directory,
            directory,
            projectFilePath: null,
            sourceFiles,
            XenonBuildProfile.Debug,
            XenonBuildProfile.Release);
    }

    private static ImmutableArray<string> DiscoverSources(string sourceRoot) =>
        EnumerateFiles(sourceRoot, SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".xe", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();

    private static IEnumerable<string> EnumerateFiles(string directory, SearchOption searchOption)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", searchOption).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectSystemException($"cannot enumerate project directory '{directory}': {exception.Message}", exception);
        }
    }

    private static void EnsureHasSources(ImmutableArray<string> sourceFiles, string sourceRoot)
    {
        if (sourceFiles.IsEmpty)
        {
            throw new ProjectSystemException($"no .xe source files were found under '{sourceRoot}'");
        }
    }

    private static XenonBuildProfile ReadProfile(
        IReadOnlyDictionary<string, ProjectSetting> settings,
        string prefix,
        XenonBuildProfile defaults,
        string path)
    {
        int optimization = GetOptionalInteger(settings, $"{prefix}.optimization", path)
            ?? defaults.OptimizationLevel;
        if (optimization is < 0 or > 3)
        {
            int line = settings[$"{prefix}.optimization"].Line;
            throw Error(path, line, "optimization level must be between 0 and 3");
        }

        bool debugInformation = GetOptionalBoolean(settings, $"{prefix}.debug-info", path)
            ?? defaults.EmitDebugInformation;
        bool checks = GetOptionalBoolean(settings, $"{prefix}.checks", path)
            ?? defaults.EnableChecks;
        return new XenonBuildProfile(optimization, debugInformation, checks);
    }

    private static IReadOnlyDictionary<string, ProjectSetting> ParseSettings(
        IReadOnlyList<string> lines,
        string path)
    {
        var settings = new Dictionary<string, ProjectSetting>(StringComparer.Ordinal);
        string? section = null;

        for (int index = 0; index < lines.Count; index++)
        {
            int lineNumber = index + 1;
            string line = StripComment(lines[index]).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                if (!line.EndsWith("]", StringComparison.Ordinal) || line.Length < 3)
                {
                    throw Error(path, lineNumber, "invalid section header");
                }

                section = line[1..^1].Trim();
                if (section.Length == 0)
                {
                    throw Error(path, lineNumber, "section name cannot be empty");
                }

                continue;
            }

            if (section is null)
            {
                throw Error(path, lineNumber, "project settings must appear inside a section");
            }

            int equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0 || equalsIndex == line.Length - 1)
            {
                throw Error(path, lineNumber, "expected 'name = value'");
            }

            string name = line[..equalsIndex].Trim();
            string value = line[(equalsIndex + 1)..].Trim();
            string key = $"{section}.{name}";
            if (!settings.TryAdd(key, new ProjectSetting(value, lineNumber)))
            {
                throw Error(path, lineNumber, $"project setting '{key}' is already defined");
            }
        }

        return settings;
    }

    private static string StripComment(string line)
    {
        bool insideString = false;
        bool escaped = false;
        for (int index = 0; index < line.Length; index++)
        {
            char character = line[index];
            if (insideString && escaped)
            {
                escaped = false;
                continue;
            }

            if (insideString && character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                insideString = !insideString;
            }
            else if (character == '#' && !insideString)
            {
                return line[..index];
            }
        }

        return line;
    }

    private static string GetRequiredString(
        IReadOnlyDictionary<string, ProjectSetting> settings,
        string key,
        string path)
    {
        if (!settings.TryGetValue(key, out ProjectSetting setting))
        {
            throw new ProjectSystemException($"project file '{path}' is missing required setting '{key}'");
        }

        return ParseString(setting, key, path);
    }

    private static string? GetOptionalString(
        IReadOnlyDictionary<string, ProjectSetting> settings,
        string key,
        string path) =>
        settings.TryGetValue(key, out ProjectSetting setting) ? ParseString(setting, key, path) : null;

    private static string ParseString(ProjectSetting setting, string key, string path)
    {
        string value = setting.Value;
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            throw Error(path, setting.Line, $"project setting '{key}' must be a quoted string");
        }

        try
        {
            return Unescape(value[1..^1]);
        }
        catch (FormatException exception)
        {
            throw Error(path, setting.Line, exception.Message);
        }
    }

    private static int? GetOptionalInteger(
        IReadOnlyDictionary<string, ProjectSetting> settings,
        string key,
        string path)
    {
        if (!settings.TryGetValue(key, out ProjectSetting setting))
        {
            return null;
        }

        if (!int.TryParse(setting.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            throw Error(path, setting.Line, $"project setting '{key}' must be an integer");
        }

        return value;
    }

    private static bool? GetOptionalBoolean(
        IReadOnlyDictionary<string, ProjectSetting> settings,
        string key,
        string path)
    {
        if (!settings.TryGetValue(key, out ProjectSetting setting))
        {
            return null;
        }

        if (!bool.TryParse(setting.Value, out bool value))
        {
            throw Error(path, setting.Line, $"project setting '{key}' must be true or false");
        }

        return value;
    }

    private static string Unescape(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            char character = text[index];
            if (character != '\\')
            {
                result.Append(character);
                continue;
            }

            if (++index == text.Length)
            {
                throw new FormatException("unterminated escape sequence in string");
            }

            result.Append(text[index] switch
            {
                '"' => '"',
                '\\' => '\\',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                _ => throw new FormatException($"unsupported escape sequence '\\{text[index]}'"),
            });
        }

        return result.ToString();
    }

    private static ProjectSystemException Error(string path, int line, string message) =>
        new($"{path}({line}): {message}");

    private readonly record struct ProjectSetting(string Value, int Line);
}
