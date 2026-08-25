namespace Xenon.ProjectSystem;

public sealed class ProjectSystemException : Exception
{
    public ProjectSystemException(string message)
        : base(message)
    {
    }

    public ProjectSystemException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
