using System.Text.Json;
using System.Text.Json.Serialization;
using Xenon.ProjectSystem;

namespace Xenon.EndToEnd.Tests.Infrastructure;

public sealed record ExpectedDiagnostic(string? Id = null, string? Message = null);

public sealed record TestManifest
{
    public string Input { get; init; } = ".";
    public XenonProjectType Artifact { get; init; } = XenonProjectType.Executable;
    public string[] Profiles { get; init; } = ["debug"];
    public bool CompileOnly { get; init; }
    public bool Run { get; init; }
    public bool ExpectedBuildSuccess { get; init; } = true;
    public ExpectedDiagnostic[] ExpectedDiagnostics { get; init; } = [];
    public int ExpectedExitCode { get; init; }
    public string ExpectedStdout { get; init; } = "";
    public string ExpectedStderr { get; init; } = "";
    public int TimeoutSeconds { get; init; } = 10;
    public int ToolTimeoutSeconds { get; init; } = 120;
    public string? TargetTriple { get; init; }
    public string? ExportSymbol { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static TestManifest Load(string directory)
    {
        var manifest = JsonSerializer.Deserialize<TestManifest>(File.ReadAllText(Path.Combine(directory, "test.json")), JsonOptions)
            ?? throw new InvalidDataException("Manifest cannot be null.");
        manifest.Validate();
        return manifest;
    }

    public void Validate()
    {
        if (Profiles is null || Profiles.Length == 0 || Profiles.Any(p => p is not "debug" and not "release") ||
            Profiles.Distinct().Count() != Profiles.Length)
            throw new InvalidDataException("Profiles must contain unique debug/release values.");
        if (string.IsNullOrWhiteSpace(Input) || Path.IsPathRooted(Input) || Input.Split('/', '\\').Contains(".."))
            throw new InvalidDataException("Input must be a relative path inside the fixture.");
        if (TimeoutSeconds is < 1 or > 3600 || ToolTimeoutSeconds is < 1 or > 3600)
            throw new InvalidDataException("Timeouts must be between 1 and 3600 seconds.");
        if (Run && (CompileOnly || !ExpectedBuildSuccess || Artifact != XenonProjectType.Executable))
            throw new InvalidDataException("Run requires a successful native executable build.");
        if (ExportSymbol is not null && (Artifact != XenonProjectType.SharedLibrary || Run || CompileOnly || !ExpectedBuildSuccess))
            throw new InvalidDataException("ExportSymbol requires a successful shared library build without Run.");
        if (ExpectedStdout is null || ExpectedStderr is null || ExpectedDiagnostics is null ||
            ExpectedDiagnostics.Any(d => d is null || (string.IsNullOrWhiteSpace(d.Id) && string.IsNullOrWhiteSpace(d.Message))))
            throw new InvalidDataException("Each diagnostic needs an ID or exact message; expected streams cannot be null.");
        if (!ExpectedBuildSuccess && (ExpectedDiagnostics.Length == 0 || !CompileOnly))
            throw new InvalidDataException("Negative compilation tests require CompileOnly and expected diagnostics.");
        if (TargetTriple is not null && string.IsNullOrWhiteSpace(TargetTriple))
            throw new InvalidDataException("TargetTriple cannot be empty.");
    }
}
