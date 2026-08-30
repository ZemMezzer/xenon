using System.Diagnostics;
using System.Text.Json;
using Xenon.Driver;
using Xenon.EndToEnd.Tests.Infrastructure;
using Xunit;

namespace Xenon.EndToEnd.Tests;

[Trait("Category", "Harness")]
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task CapturesStreamsExitCodeAndLiteralArguments()
    {
        const string literal = "space \"quote\" $HOME ; & | ü";
        NativeProcessResult result = await Run("echo", literal, "stderr", "17");
        Assert.Null(result.StartError);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal(literal, result.Stdout);
        Assert.Equal("stderr", result.Stderr);
        Assert.False(result.TimedOut);
        Assert.False(result.StdoutTruncated);
        Assert.False(result.StderrTruncated);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task DrainsBothLargeStreamsWithoutDeadlock()
    {
        var command = TestProcess.Command(AppContext.BaseDirectory, TimeSpan.FromSeconds(10), "flood") with
        {
            MaxCapturedStdoutChars = 1024 * 1024,
            MaxCapturedStderrChars = 1024 * 1024,
        };
        NativeProcessResult result = await new NativeProcessRunner().RunAsync(command);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1024 * 1024, result.Stdout.Length);
        Assert.Equal(1024 * 1024, result.Stderr.Length);
        Assert.False(result.TimedOut);
        Assert.False(result.StdoutTruncated);
        Assert.False(result.StderrTruncated);
    }

    [Theory]
    [InlineData(2_000_003, 3_000_007, 1025, 8201)]
    [InlineData(2_000_003, 11, 1024, 17)]
    [InlineData(11, 2_000_003, 17, 1024)]
    [InlineData(37, 18, 37, 37)]
    [InlineData(38, 38, 37, 37)]
    [InlineData(4099, 4097, 1, 2)]
    public async Task CaptureIsBoundedAndKeepsEachStreamsBeginningAndEnd(int stdoutSize, int stderrSize, int stdoutLimit, int stderrLimit)
    {
        var command = TestProcess.Command(AppContext.BaseDirectory, TimeSpan.FromSeconds(10), "pattern", stdoutSize.ToString(), stderrSize.ToString()) with
        {
            MaxCapturedStdoutChars = stdoutLimit,
            MaxCapturedStderrChars = stderrLimit,
        };
        NativeProcessResult result = await new NativeProcessRunner().RunAsync(command);
        Assert.Null(result.StartError);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(stdoutSize > stdoutLimit, result.StdoutTruncated);
        Assert.Equal(stderrSize > stderrLimit, result.StderrTruncated);
        Assert.Equal(ExpectedCapture(stdoutSize, stdoutLimit, 'A'), result.Stdout);
        Assert.Equal(ExpectedCapture(stderrSize, stderrLimit, 'a'), result.Stderr);

        var failure = new E2eResult("output-capture", "debug", AppContext.BaseDirectory) { Execution = result };
        Assert.Contains($"Stdout truncated: {result.StdoutTruncated}", failure.Report());
        Assert.Contains($"Stderr truncated: {result.StderrTruncated}", failure.Report());
        Assert.Equal(result.StdoutTruncated, failure.Report().Contains("stdout truncated: middle omitted", StringComparison.Ordinal));
        Assert.Equal(result.StderrTruncated, failure.Report().Contains("stderr truncated: middle omitted", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(failure, TestManifest.JsonOptions));
        Assert.Equal(result.StdoutTruncated, json.RootElement.GetProperty("execution").GetProperty("stdoutTruncated").GetBoolean());
        Assert.Equal(result.StderrTruncated, json.RootElement.GetProperty("execution").GetProperty("stderrTruncated").GetBoolean());
    }

    [Fact]
    public async Task DefaultCaptureLimitsAlsoBoundUnconfiguredProcesses()
    {
        NativeProcessResult result = await Run("flood");
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(NativeProcessRunner.DefaultCaptureLimitChars, result.Stdout.Length);
        Assert.Equal(NativeProcessRunner.DefaultCaptureLimitChars, result.Stderr.Length);
        Assert.True(result.StdoutTruncated);
        Assert.True(result.StderrTruncated);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    [InlineData(1, -1)]
    public async Task InvalidCaptureLimitsAreRejectedBeforeStartingTheProcess(int stdoutLimit, int stderrLimit)
    {
        var command = TestProcess.Command(AppContext.BaseDirectory, TimeSpan.FromSeconds(10), "wait") with
        {
            MaxCapturedStdoutChars = stdoutLimit,
            MaxCapturedStderrChars = stderrLimit,
        };
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new NativeProcessRunner().RunAsync(command));
    }

    private static string ExpectedCapture(int size, int limit, char start)
    {
        // Generate just the expected retained segments, never the multi-megabyte input.
        int head = size <= limit ? size : limit - limit / 2;
        int tail = size <= limit ? 0 : limit / 2;
        return new string(Enumerable.Range(0, head).Concat(Enumerable.Range(size - tail, tail))
            .Select(index => (char)(start + index % 26)).ToArray());
    }

    [Fact]
    public async Task StartFailureIsStructured()
    {
        var command = new NativeProcessRequest(Path.Combine(AppContext.BaseDirectory, "does-not-exist"), [], AppContext.BaseDirectory, TimeSpan.FromSeconds(1));
        var result = await new NativeProcessRunner().RunAsync(command);
        Assert.NotNull(result.StartError);
        Assert.Null(result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task TimeoutKillsDescendantsAndKeepsPartialOutput()
    {
        var sandbox = new TestSandbox("process-tree");
        Directory.CreateDirectory(sandbox.Root);
        string pidPath = Path.Combine(sandbox.Root, "child.pid");
        var command = TestProcess.Command(sandbox.Root, TimeSpan.FromSeconds(3), "tree", pidPath);
        NativeProcessResult result = await new NativeProcessRunner().RunAsync(command);
        Assert.True(result.TimedOut);
        Assert.Null(result.TerminationError);
        Assert.Contains("ready", result.Stdout);
        Assert.True(result.Duration < TimeSpan.FromSeconds(10));
        int pid = int.Parse(File.ReadAllText(pidPath));
        Assert.True(await WaitForProcessTerminationAsync(pid, TimeSpan.FromSeconds(5)),
            $"Descendant process {pid} was still running after process-tree termination.");
        sandbox.Delete();
    }

    [Fact]
    public async Task CancellationTerminatesProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var command = TestProcess.Command(AppContext.BaseDirectory, TimeSpan.FromSeconds(30), "wait");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NativeProcessRunner().RunAsync(command, cancellation.Token));
    }

    [Fact]
    public async Task InfiniteTimeoutIsRejected()
    {
        var command = TestProcess.Command(AppContext.BaseDirectory, Timeout.InfiniteTimeSpan, "wait");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new NativeProcessRunner().RunAsync(command));
    }

    private static Task<NativeProcessResult> Run(params string[] args) =>
        new NativeProcessRunner().RunAsync(TestProcess.Command(AppContext.BaseDirectory, TimeSpan.FromSeconds(10), args));

    private static async Task<bool> WaitForProcessTerminationAsync(int pid, TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (IsProcessTerminated(pid)) return true;
            await Task.Delay(25);
        }
        return IsProcessTerminated(pid);
    }

    private static bool IsProcessTerminated(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (process.HasExited) return true;
            if (!OperatingSystem.IsLinux()) return false;

            // A killed orphan may remain in /proc as a zombie until init reaps it. It no
            // longer executes or owns the inherited pipes, so it satisfies termination.
            string stat = File.ReadAllText($"/proc/{pid}/stat");
            int commandEnd = stat.LastIndexOf(')');
            char state = commandEnd >= 0 && commandEnd + 2 < stat.Length
                ? stat[commandEnd + 2]
                : '\0';
            return state is 'Z' or 'X';
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
