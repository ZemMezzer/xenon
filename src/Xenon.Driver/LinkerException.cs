namespace Xenon.Driver;

public sealed class LinkerException : Exception
{
    public LinkerException(string message)
        : base(message)
    {
    }

    public LinkerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
