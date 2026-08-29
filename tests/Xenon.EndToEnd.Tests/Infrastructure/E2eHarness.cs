using System.Text.Json;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Driver;
using Xunit.Sdk;

namespace Xenon.EndToEnd.Tests.Infrastructure;

public sealed class E2eHarness(INativeProcessRunner? processRunner = null)
{
    public async Task<E2eResult> RunAsync(string fixture, string profile, TestManifest? expectation = null)
    {
        string name = Path.GetRelativePath(AppContext.BaseDirectory, fixture).Replace('\\', '/');
        TestSandbox? sandbox = null;
        var result = new E2eResult(name, profile, Environment.GetEnvironmentVariable("XENON_TEST_SANDBOX_ROOT") ?? "<not allocated>");
        try
        {
            sandbox = new TestSandbox(name + "-" + profile);
            result = new E2eResult(name, profile, sandbox.Root) { SandboxOwner = sandbox };
            sandbox.Prepare(fixture);
            TestManifest manifest = expectation ?? TestManifest.Load(sandbox.Source);
            result.Expected = manifest;
            manifest.Validate();
            if (!manifest.Profiles.Contains(profile)) throw new InvalidDataException($"Profile '{profile}' is not requested by this fixture.");
            result.Stage = "Build";
            XenonBuildResult build = new XenonBuildDriver(processRunner).Build(new XenonBuildRequest(
                Path.GetFullPath(manifest.Input, sandbox.Source), profile, sandbox.Build,
                manifest.TargetTriple, manifest.CompileOnly, TimeSpan.FromSeconds(manifest.ToolTimeoutSeconds)));
            result.Build = build;
            result.Stage = build.Stage.ToString();
            if (build.Project is not null && build.Project.Type != manifest.Artifact)
                return Fail(result, "Harness/Environment", "Manifest artifact kind does not match the loaded project.");
            // Negative tests accept only frontend diagnostics, never linker/LLVM/environment failures.
            if (!build.Success && !(build.Stage == BuildStage.Compilation && build.FailureKind == BuildFailureKind.Compiler &&
                                    !manifest.ExpectedBuildSuccess && build.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)))
                return Fail(result, build.FailureKind == BuildFailureKind.Environment ? "Harness/Environment" : "Xenon", build.Failure ?? "Build failed.");
            result.Stage = "ResultValidation";
            if (build.Success != manifest.ExpectedBuildSuccess)
                return Fail(result, "Assertion", $"Expected build success {manifest.ExpectedBuildSuccess}, actual {build.Success}.");
            if (!DiagnosticsMatch(manifest.ExpectedDiagnostics, build.Diagnostics))
                return Fail(result, "Assertion", "Compiler diagnostics do not exactly match expectations (including additional diagnostics).");

            if (manifest.Run || manifest.ExportSymbol is not null)
            {
                result.Stage = "Execute";
                if (!string.Equals(build.TargetTriple, LlvmTargetPlatform.HostTriple, StringComparison.OrdinalIgnoreCase))
                    return Fail(result, "Harness/Environment", "Cannot execute an artifact for a different target triple.");
                NativeProcessRequest command = manifest.ExportSymbol is null
                    ? new(build.ArtifactPath!, [], sandbox.Source, TimeSpan.FromSeconds(manifest.TimeoutSeconds))
                    : TestProcess.Command(sandbox.Source, TimeSpan.FromSeconds(manifest.TimeoutSeconds), "export", build.ArtifactPath!, manifest.ExportSymbol);
                NativeProcessResult execution = await (processRunner ?? new NativeProcessRunner()).RunAsync(command);
                result.Execution = execution;
                if (execution.StartError is not null || execution.TerminationError is not null)
                    return Fail(result, "Harness/Environment", execution.StartError ?? execution.TerminationError!);
                if (execution.TimedOut)
                    return Fail(result, "Timeout", $"Process exceeded timeout of {manifest.TimeoutSeconds} seconds; process-tree termination was requested.");
                result.Stage = "ResultValidation";
                if (execution.StdoutTruncated || execution.StderrTruncated)
                    return Fail(result, "Assertion", "Runtime output exceeded capture limits; truncated streams cannot satisfy exact output assertions.");
                if (execution.ExitCode != manifest.ExpectedExitCode || Normalize(execution.Stdout) != Normalize(manifest.ExpectedStdout) ||
                    Normalize(execution.Stderr) != Normalize(manifest.ExpectedStderr))
                    return Fail(result, "Assertion", "Runtime result did not match expected exit code/stdout/stderr.");
            }
            result.Stage = "Cleanup";
            sandbox.Delete();
            result.Success = true;
            result.Stage = "Complete";
            return result;
        }
        catch (Exception exception)
        {
            return Fail(result, "Harness/Environment", exception.ToString());
        }
        finally
        {
            if (!result.Success && sandbox is not null)
            {
                try
                {
                    Directory.CreateDirectory(sandbox.Logs);
                    File.WriteAllText(Path.Combine(sandbox.Root, "test-result.json"), JsonSerializer.Serialize(result, TestManifest.JsonOptions));
                    File.WriteAllText(Path.Combine(sandbox.Logs, "failure.txt"), result.Report());
                    if (result.Execution is { } runtime)
                    {
                        File.WriteAllText(Path.Combine(sandbox.Logs, "runtime.stdout.txt"), runtime.GetStdoutForDiagnostics());
                        File.WriteAllText(Path.Combine(sandbox.Logs, "runtime.stderr.txt"), runtime.GetStderrForDiagnostics());
                    }
                    if (result.Build?.LinkProcess is { } link)
                    {
                        File.WriteAllText(Path.Combine(sandbox.Logs, "linker.stdout.txt"), link.GetStdoutForDiagnostics());
                        File.WriteAllText(Path.Combine(sandbox.Logs, "linker.stderr.txt"), link.GetStderrForDiagnostics());
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    result.Failure += $"\nCould not persist failure artifacts: {exception.Message}";
                }
            }
        }
    }

    public static void AssertSuccess(E2eResult result)
    {
        if (!result.Success) throw new XunitException(result.Report());
    }

    /// <summary>
    /// Self-tests place all verification inside this callback. Only a successful verification permits cleanup;
    /// an assertion/inspection failure retains the invocation and includes its diagnostic report in test output.
    /// Ordinary regression tests must continue to use AssertSuccess, which never deletes a failed invocation.
    /// </summary>
    public static void VerifyIntentionalFailure(E2eResult result, Action<E2eResult> verify)
    {
        try
        {
            if (result.Success) throw new XunitException("Expected an intentionally failed E2E invocation.");
            // Use the original owner, not an arbitrary path from a deserialized or fabricated result.
            TestSandbox sandbox = result.SandboxOwner ?? throw new XunitException("Invocation has no owned sandbox to verify.");
            verify(result);
            sandbox.Delete();
        }
        catch (Exception exception)
        {
            throw new XunitException($"Harness self-test verification/cleanup failed: {exception}\n{result.Report()}");
        }
    }

    public static bool DiagnosticsMatch(IReadOnlyList<ExpectedDiagnostic> expected, IReadOnlyList<Diagnostic> actual)
    {
        var remaining = actual.ToList();
        foreach (ExpectedDiagnostic diagnostic in expected)
        {
            int index = remaining.FindIndex(d => (diagnostic.Id is null || d.Id == diagnostic.Id) &&
                                                (diagnostic.Message is null || d.Message == diagnostic.Message));
            if (index < 0) return false;
            remaining.RemoveAt(index);
        }
        return remaining.Count == 0;
    }

    private static E2eResult Fail(E2eResult result, string kind, string message)
    {
        result.FailureKind = kind; result.Failure = message; return result;
    }
    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}

public static class TestProcess
{
    public static NativeProcessRequest Command(string workingDirectory, TimeSpan timeout, params string[] args) =>
        new(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            [Path.Combine(AppContext.BaseDirectory, "TestProcess", "Xenon.TestProcess.dll"), .. args], workingDirectory, timeout, "utf-8");
}
