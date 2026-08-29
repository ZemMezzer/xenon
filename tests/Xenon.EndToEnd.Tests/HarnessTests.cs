using System.Text.Json;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;
using Xenon.Driver;
using Xenon.EndToEnd.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Xenon.EndToEnd.Tests;

[Trait("Category", "Harness")]
public sealed class HarnessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SuccessCleansSandboxAndParallelBuildsAreIndependent()
    {
        string fixture = Case("Cases", "Basics/Hello");
        var originalFiles = Directory.GetFiles(fixture).ToDictionary(p => p, File.ReadAllBytes);
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => new E2eHarness().RunAsync(fixture, "debug"))));
        Assert.Equal(4, results.Select(r => r.Sandbox).Distinct().Count());
        foreach (var result in results)
        {
            E2eHarness.AssertSuccess(result);
            Assert.False(Directory.Exists(result.Sandbox));
        }
        Assert.Equal(originalFiles.Keys.Order(), Directory.GetFiles(fixture).Order());
        foreach (var file in originalFiles) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Theory]
    [InlineData("Compilation", "Cases", "Negative/Undefined", "unknown identifier 'missing'")]
    [InlineData("Link", "HarnessCases", "Unresolved", "xenon_e2e_deliberately_missing_symbol")]
    [InlineData("Execute", "HarnessCases", "Timeout", "before timeout")]
    [InlineData("Prepare", "HarnessCases", "InvalidManifest", "Timeouts must")]
    [InlineData("LlvmGeneration", "HarnessCases", "MissingMain", "Main")]
    public async Task FailuresPreserveActionableReports(string stage, string root, string name, string evidence)
    {
        TestManifest? expectation = stage == "Compilation" ? new() { Input = "main.xe", CompileOnly = true } : null;
        E2eResult result = await new E2eHarness().RunAsync(Case(root, name), "debug", expectation);
        E2eHarness.VerifyIntentionalFailure(result, verified =>
        {
            Assert.False(verified.Success);
            Assert.Equal(stage, verified.Stage);
            Assert.Contains(evidence, verified.Report(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(verified.Sandbox, verified.Report());
            Assert.True(Directory.Exists(Path.Combine(verified.Sandbox, "source")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(verified.Sandbox, "test-result.json")));
            Assert.Equal(verified.Report(), File.ReadAllText(Path.Combine(verified.Sandbox, "logs", "failure.txt")));
            Assert.Equal(stage, json.RootElement.GetProperty("stage").GetString());
            if (stage == "Link")
            {
                Assert.NotNull(verified.Build!.LinkProcess);
                Assert.NotEqual(0, verified.Build.LinkProcess!.ExitCode);
                Assert.Contains("Arguments:", verified.Report());
                Assert.True(File.Exists(verified.Build.ObjectPath));
                Assert.True(File.Exists(verified.Build.LlvmIrPath));
            }
            if (stage == "Prepare") Assert.Equal("Harness/Environment", verified.FailureKind);
            if (stage == "Execute") Assert.True(verified.Execution!.TimedOut);
            output.WriteLine(verified.Report());
        });
        Assert.False(Directory.Exists(result.Sandbox));
    }

    [Fact]
    public async Task RuntimeAssertionShowsBothExpectedAndActual()
    {
        var manifest = TestManifest.Load(Case("Cases", "Basics/Hello")) with { ExpectedExitCode = 7, ExpectedStdout = "wrong\n" };
        E2eResult result = await new E2eHarness().RunAsync(Case("Cases", "Basics/Hello"), "debug", manifest);
        E2eHarness.VerifyIntentionalFailure(result, verified =>
        {
            Assert.Equal("ResultValidation", verified.Stage);
            Assert.Equal("Assertion", verified.FailureKind);
            Assert.Contains("Expected exit code: 7", verified.Report());
            Assert.Contains("Actual exit code: 0", verified.Report());
            Assert.Contains("Hello, Xenon!", verified.Report());
            Assert.Contains("wrong", verified.Report());
            output.WriteLine(verified.Report());
        });
        Assert.False(Directory.Exists(result.Sandbox));
    }

    [Fact]
    public async Task NegativeTestCannotPassOnProjectLoadingFailure()
    {
        var manifest = new TestManifest { Input = "missing.xe", CompileOnly = true, ExpectedBuildSuccess = false,
            ExpectedDiagnostics = [new(Message: "unknown identifier 'missing'")] };
        E2eResult result = await new E2eHarness().RunAsync(Case("Cases", "Negative/Undefined"), "debug", manifest);
        E2eHarness.VerifyIntentionalFailure(result, verified =>
        {
            Assert.False(verified.Success);
            Assert.Equal("ProjectLoading", verified.Stage);
            Assert.Equal("Harness/Environment", verified.FailureKind);
        });
        Assert.False(Directory.Exists(result.Sandbox));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeInfrastructureFailuresAreNotLanguageRegressions(bool cannotStart)
    {
        var harness = new E2eHarness(new NativeFailureProbe(cannotStart));
        E2eResult result = await harness.RunAsync(Case("Cases", "Basics/Hello"), "debug");
        E2eHarness.VerifyIntentionalFailure(result, verified =>
        {
            Assert.False(verified.Success);
            Assert.Equal("Link", verified.Stage);
            Assert.Equal("Harness/Environment", verified.FailureKind);
            Assert.NotNull(verified.Build!.LinkProcess);
            Assert.Contains(cannotStart ? "tool unavailable (test probe)" : "did not produce", verified.Report());
            Assert.Contains("Working directory:", verified.Report());
            output.WriteLine(verified.Report());
        });
        Assert.False(Directory.Exists(result.Sandbox));
    }

    [Fact]
    public async Task MissingFixtureIsAPrepareFailure()
    {
        E2eResult result = await new E2eHarness().RunAsync(Case("HarnessCases", "not-present"), "debug");
        E2eHarness.VerifyIntentionalFailure(result, verified =>
        {
            Assert.False(verified.Success);
            Assert.Equal("Prepare", verified.Stage);
            Assert.Equal("Harness/Environment", verified.FailureKind);
            Assert.Contains("not-present", verified.Report());
        });
        Assert.False(Directory.Exists(result.Sandbox));
    }

    [Fact]
    public async Task FailedSelfTestKeepsSandboxUntilItsEnclosingVerificationSucceeds()
    {
        E2eResult result = await new E2eHarness().RunAsync(Case("Cases", "Negative/Undefined"), "debug",
            new TestManifest { Input = "main.xe", CompileOnly = true });
        E2eHarness.VerifyIntentionalFailure(result, verified =>
        {
            // Simulate a failing outer assertion, then verify the retained evidence before the enclosing test passes.
            var exception = Assert.Throws<XunitException>(() => E2eHarness.VerifyIntentionalFailure(verified, _ =>
                Assert.True(false, "deliberate self-test assertion failure")));
            Assert.Contains("deliberate self-test assertion failure", exception.Message);
            Assert.Contains(verified.Sandbox, exception.Message);
            Assert.True(Directory.Exists(verified.Sandbox));
            Assert.True(File.Exists(Path.Combine(verified.Sandbox, "source", "main.xe")));
            Assert.Equal(verified.Report(), File.ReadAllText(Path.Combine(verified.Sandbox, "logs", "failure.txt")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(verified.Sandbox, "test-result.json")));
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());

            // The ordinary E2E assertion must also preserve a genuine regression's evidence.
            Assert.Throws<XunitException>(() => E2eHarness.AssertSuccess(verified));
            Assert.True(File.Exists(Path.Combine(verified.Sandbox, "test-result.json")));
        });
        Assert.False(Directory.Exists(result.Sandbox));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TruncatedOutputIsReportedInLogsAndCannotPassExactAssertions(bool stdout)
    {
        // Mark otherwise matching streams as truncated to ensure missing data can never produce a false pass.
        var manifest = TestManifest.Load(Case("Cases", "Basics/Hello"));
        var harness = new E2eHarness(new TruncatedRuntimeProbe(manifest, stdout));
        E2eResult result = await harness.RunAsync(Case("Cases", "Basics/Hello"), "debug", manifest);
        E2eHarness.VerifyIntentionalFailure(result, verified =>
        {
            Assert.Equal("ResultValidation", verified.Stage);
            Assert.Equal("Assertion", verified.FailureKind);
            Assert.Contains("truncated streams cannot satisfy exact output assertions", verified.Failure);
            string stream = stdout ? "stdout" : "stderr";
            Assert.Contains($"{stream} truncated: middle omitted", verified.Report());
            Assert.Contains($"{stream} truncated: middle omitted",
                File.ReadAllText(Path.Combine(verified.Sandbox, "logs", $"runtime.{stream}.txt")));
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(verified.Sandbox, "test-result.json")));
            Assert.True(json.RootElement.GetProperty("execution").GetProperty(stream + "Truncated").GetBoolean());
        });
        Assert.False(Directory.Exists(result.Sandbox));
    }

    [Fact]
    public void DiagnosticMatchingUsesIdsAndRejectsExtraOrDuplicateErrors()
    {
        var diagnostic = new Diagnostic(DiagnosticSeverity.Error, "message", new(SourceText.From("bad", "main.xe"), new(0, 3))) { Id = "XE_TEST" };
        Assert.True(E2eHarness.DiagnosticsMatch([new(Id: "XE_TEST")], [diagnostic]));
        Assert.False(E2eHarness.DiagnosticsMatch([new(Id: "OTHER")], [diagnostic]));
        Assert.False(E2eHarness.DiagnosticsMatch([new(Message: "message")], [diagnostic, diagnostic]));
        Assert.False(E2eHarness.DiagnosticsMatch([new(Message: "message"), new(Message: "message")], [diagnostic]));
    }

    [Theory]
    [InlineData("../outside.xe")]
    [InlineData("nested/../../outside.xe")]
    public void ManifestRejectsEscapingInput(string path) =>
        Assert.Throws<InvalidDataException>(() => new TestManifest { Input = path }.Validate());

    private static string Case(string root, string name) => Path.Combine(AppContext.BaseDirectory, root, name.Replace('/', Path.DirectorySeparatorChar));

    private sealed class NativeFailureProbe(bool cannotStart) : INativeProcessRunner
    {
        public Task<NativeProcessResult> RunAsync(NativeProcessRequest command, CancellationToken cancellationToken = default) =>
            Task.FromResult(new NativeProcessResult(command, cannotStart ? null : 0, "probe stdout", "probe stderr", TimeSpan.Zero,
                false, cannotStart ? "tool unavailable (test probe)" : null));
    }

    private sealed class TruncatedRuntimeProbe(TestManifest manifest, bool stdout) : INativeProcessRunner
    {
        private int _calls;
        public Task<NativeProcessResult> RunAsync(NativeProcessRequest command, CancellationToken cancellationToken = default)
        {
            if (_calls++ == 0) return new NativeProcessRunner().RunAsync(command, cancellationToken);
            return Task.FromResult(new NativeProcessResult(command, manifest.ExpectedExitCode,
                manifest.ExpectedStdout, manifest.ExpectedStderr, TimeSpan.Zero, false,
                StdoutTruncated: stdout, StderrTruncated: !stdout));
        }
    }
}
