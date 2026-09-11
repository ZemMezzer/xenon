using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xenon.Compiler.Diagnostics;
using Xenon.Driver;

namespace Xenon.EndToEnd.Tests.Infrastructure;

public sealed class E2eResult(string test, string profile, string sandbox)
{
    public string Test { get; } = test;
    public string Profile { get; } = profile;
    public string Sandbox { get; } = sandbox;
    internal TestSandbox? SandboxOwner { get; init; }
    public bool Success { get; set; }
    public string Stage { get; set; } = "Prepare";
    public string? FailureKind { get; set; }
    public string? Failure { get; set; }
    public TestManifest? Expected { get; set; }
    [JsonIgnore] public XenonBuildResult? Build { get; set; }
    public object? Compilation => Build is null ? null : new
    {
        Build.Success, Build.Stage, Build.FailureKind, Build.Failure, Build.TargetTriple,
        Build.LlvmIrPath, Build.ObjectPath, Build.ArtifactPath, Build.ImportLibraryPath, Build.LinkProcess,
        Diagnostics = Build.Diagnostics.Select(d => new
        {
            d.Id, d.Severity, d.Message, d.Location.Path,
            Line = d.Location.Start.Line + 1, Column = d.Location.Start.Character + 1, d.Location.Span,
        }),
    };
    public NativeProcessResult? Execution { get; set; }

    public string Report()
    {
        var text = new StringBuilder();
        text.AppendLine("Xenon E2E Test Failed");
        text.AppendLine($"Test: {Test}\nStage: {Stage}\nFailure kind: {FailureKind}\nProfile: {Profile}");
        text.AppendLine($"Artifact: {Expected?.Artifact}\nTarget: {Build?.TargetTriple}\nFailure: {Failure}");
        text.AppendLine($"Expected build success: {Expected?.ExpectedBuildSuccess}\nActual build success: {Build?.Success}");
        text.AppendLine($"Input object: {Build?.ObjectPath}\nExpected output artifact: {Build?.ArtifactPath}\nLLVM IR: {Build?.LlvmIrPath}");
        text.AppendLine("Expected diagnostics: " + JsonSerializer.Serialize(Expected?.ExpectedDiagnostics, TestManifest.JsonOptions));
        text.AppendLine("Compiler diagnostics (driver API; compiler stdout/stderr are not used):");
        using (var writer = new StringWriter(text))
            foreach (Diagnostic diagnostic in Build?.Diagnostics ?? []) DiagnosticFormatter.Write(writer, diagnostic);
        AppendProcess(text, "Linker / archiver", Build?.LinkProcess);
        AppendProcess(text, "Execution", Execution);
        if (Expected is not null)
        {
            text.AppendLine($"Expected exit code: {Expected.ExpectedExitCode}\nActual exit code: {Execution?.ExitCode}");
            text.AppendLine($"Expected stdout: {JsonSerializer.Serialize(Expected.ExpectedStdout)}");
            text.AppendLine($"Expected stderr: {JsonSerializer.Serialize(Expected.ExpectedStderr)}");
        }
        text.AppendLine(Directory.Exists(Sandbox) ? $"Sandbox preserved at: {Sandbox}" : $"Sandbox could not be created at: {Sandbox}");
        return text.ToString();
    }

    public static void AppendProcess(StringBuilder text, string label, NativeProcessResult? process)
    {
        if (process is null) return;
        text.AppendLine($"{label}:\nExecutable: {process.Command.Executable}");
        text.AppendLine($"Arguments: {JsonSerializer.Serialize(process.Command.Arguments)}\nWorking directory: {process.Command.WorkingDirectory}");
        text.AppendLine($"Exit code: {process.ExitCode}\nDuration: {process.Duration.TotalSeconds:F3}s\nTimed out: {process.TimedOut}\nTimeout: {process.Command.Timeout.TotalSeconds}s");
        text.AppendLine($"Start error: {process.StartError}\nTermination error: {process.TerminationError}");
        text.AppendLine($"Stdout truncated: {process.StdoutTruncated}\nStderr truncated: {process.StderrTruncated}");
        text.AppendLine($"stdout:\n{process.GetStdoutForDiagnostics()}\nstderr:\n{process.GetStderrForDiagnostics()}");
    }
}
