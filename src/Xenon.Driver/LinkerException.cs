namespace Xenon.Driver;

public sealed class LinkerException : Exception
{
    public NativeProcessResult? ProcessResult { get; init; }

    public bool IsEnvironmentFailure { get; init; } = true;

    public LinkerException(string message)
        : base(message)
    {
    }

    public LinkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
