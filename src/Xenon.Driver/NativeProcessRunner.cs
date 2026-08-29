using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Xenon.Driver;

public sealed record NativeProcessRequest(
    string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout,
    string? OutputEncodingName = null,
    int MaxCapturedStdoutChars = NativeProcessRunner.DefaultCaptureLimitChars,
    int MaxCapturedStderrChars = NativeProcessRunner.DefaultCaptureLimitChars);

public sealed record NativeProcessResult(
    NativeProcessRequest Command, int? ExitCode, string Stdout, string Stderr,
    TimeSpan Duration, bool TimedOut, string? StartError = null, string? TerminationError = null,
    bool StdoutTruncated = false, bool StderrTruncated = false)
{
    public string GetStdoutForDiagnostics() => FormatOutput(Stdout, StdoutTruncated, Command.MaxCapturedStdoutChars, "stdout");
    public string GetStderrForDiagnostics() => FormatOutput(Stderr, StderrTruncated, Command.MaxCapturedStderrChars, "stderr");

    private static string FormatOutput(string text, bool truncated, int limit, string stream) => truncated
        ? text.Insert(Math.Min(text.Length, limit - limit / 2), $"\n[... {stream} truncated: middle omitted; capture limit {limit} UTF-16 chars ...]\n")
        : text;
}

public interface INativeProcessRunner
{
    Task<NativeProcessResult> RunAsync(NativeProcessRequest command, CancellationToken cancellationToken = default);
}

/// <summary>Shell-free execution with concurrent output capture and bounded process-tree termination.</summary>
public sealed class NativeProcessRunner : INativeProcessRunner
{
    public const int DefaultCaptureLimitChars = 64 * 1024;

    public async Task<NativeProcessResult> RunAsync(
        NativeProcessRequest command, CancellationToken cancellationToken = default)
    {
        if (command.Timeout <= TimeSpan.Zero || command.Timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(command), "A finite positive process timeout is required.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.MaxCapturedStdoutChars);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(command.MaxCapturedStderrChars);
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo(command.Executable)
        {
            WorkingDirectory = command.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (command.OutputEncodingName is not null)
        {
            startInfo.StandardOutputEncoding = Encoding.GetEncoding(command.OutputEncodingName);
            startInfo.StandardErrorEncoding = startInfo.StandardOutputEncoding;
        }
        foreach (string argument in command.Arguments) startInfo.ArgumentList.Add(argument);
        // Allocate bounded storage before starting the child so an allocation failure cannot orphan it.
        var stdout = new BoundedOutput(command.MaxCapturedStdoutChars);
        var stderr = new BoundedOutput(command.MaxCapturedStderrChars);
        using var process = new Process { StartInfo = startInfo };
        var clock = Stopwatch.StartNew();
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Process.Start returned false.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(command, null, "", "", clock.Elapsed, false, exception.Message);
        }

        using var captureCancellation = new CancellationTokenSource();
        Task output = CaptureAsync(process.StandardOutput, stdout, captureCancellation.Token);
        Task error = CaptureAsync(process.StandardError, stderr, captureCancellation.Token);
        Task completion = Task.WhenAll(process.WaitForExitAsync(), output, error);
        bool timedOut = false;
        string? terminationError = null;
        try
        {
            // Include pipe drainage: a descendant holding an inherited pipe must not hang the runner.
            await completion.WaitAsync(command.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            timedOut = exception is TimeoutException;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception killException) when (killException is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                terminationError = killException.Message;
            }

            try { await completion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                terminationError ??= "Process or inherited output pipes did not close after termination.";
                await captureCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var capturedOutput = stdout.Snapshot();
        var capturedError = stderr.Snapshot();
        return new(command, process.HasExited ? process.ExitCode : null, capturedOutput.Text, capturedError.Text,
            clock.Elapsed, timedOut, TerminationError: terminationError,
            StdoutTruncated: capturedOutput.Truncated, StderrTruncated: capturedError.Truncated);
    }

    private static async Task CaptureAsync(StreamReader reader, BoundedOutput destination, CancellationToken token)
    {
        var buffer = new char[4096];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) != 0)
                destination.Append(buffer.AsSpan(0, count));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    /// <summary>A fixed prefix plus a circular suffix. Readers always drain even after the limit is reached.</summary>
    private sealed class BoundedOutput(int capacity)
    {
        private readonly object _gate = new();
        private readonly char[] _head = new char[capacity - capacity / 2];
        private readonly char[] _tail = new char[capacity / 2];
        private int _headLength;
        private int _tailLength;
        private int _tailPosition;
        private bool _truncated;

        public void Append(ReadOnlySpan<char> text)
        {
            lock (_gate)
            {
                _truncated |= text.Length > capacity - _headLength - _tailLength;
                int headCount = Math.Min(text.Length, _head.Length - _headLength);
                text[..headCount].CopyTo(_head.AsSpan(_headLength));
                _headLength += headCount;
                text = text[headCount..];
                if (text.IsEmpty || _tail.Length == 0) return;
                if (text.Length >= _tail.Length)
                {
                    text[^_tail.Length..].CopyTo(_tail);
                    _tailLength = _tail.Length;
                    _tailPosition = 0;
                    return;
                }
                int firstCount = Math.Min(text.Length, _tail.Length - _tailPosition);
                text[..firstCount].CopyTo(_tail.AsSpan(_tailPosition));
                text[firstCount..].CopyTo(_tail);
                _tailPosition = (_tailPosition + text.Length) % _tail.Length;
                _tailLength = Math.Min(_tail.Length, _tailLength + text.Length);
            }
        }

        public (string Text, bool Truncated) Snapshot()
        {
            lock (_gate)
            {
                string text = string.Create(_headLength + _tailLength, this, static (destination, capture) =>
                {
                    capture._head.AsSpan(0, capture._headLength).CopyTo(destination);
                    destination = destination[capture._headLength..];
                    int start = capture._tailLength == capture._tail.Length ? capture._tailPosition : 0;
                    int firstCount = Math.Min(capture._tailLength, capture._tail.Length - start);
                    capture._tail.AsSpan(start, firstCount).CopyTo(destination);
                    capture._tail.AsSpan(0, capture._tailLength - firstCount).CopyTo(destination[firstCount..]);
                });
                return (text, _truncated);
            }
        }
    }
}
