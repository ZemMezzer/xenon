namespace Xenon.ProjectSystem;

internal interface IWorkspaceFileSystem
{
    bool FileExists(string path);
    string ReadAllText(string path);
    void WriteAllText(string path, string text);
}

internal sealed class PhysicalWorkspaceFileSystem : IWorkspaceFileSystem
{
    public static PhysicalWorkspaceFileSystem Instance { get; } = new();

    private PhysicalWorkspaceFileSystem() { }

    public bool FileExists(string path) => File.Exists(path);
    public string ReadAllText(string path) => File.ReadAllText(path);
    public void WriteAllText(string path, string text)
    {
        string fullPath = ProjectPath.Normalize(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        string temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, text);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}

internal interface IWorkspaceSaveObserver
{
    void CandidatePrepared();
}

internal sealed class NullWorkspaceSaveObserver : IWorkspaceSaveObserver
{
    public static NullWorkspaceSaveObserver Instance { get; } = new();

    private NullWorkspaceSaveObserver() { }
    public void CandidatePrepared() { }
}
