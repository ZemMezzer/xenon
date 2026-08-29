namespace Xenon.EndToEnd.Tests.Infrastructure;

public sealed class TestSandbox
{
    private static readonly string RunId = Guid.NewGuid().ToString("N");
    public string Root { get; }
    public string Source => Path.Combine(Root, "source");
    public string Build => Path.Combine(Root, "build");
    public string Logs => Path.Combine(Root, "logs");

    public TestSandbox(string testName)
    {
        string? configuredRoot = Environment.GetEnvironmentVariable("XENON_TEST_SANDBOX_ROOT");
        string root = configuredRoot ?? Path.Combine(FindRepository(), ".xenon-test-sandboxes");
        string name = string.Concat(testName.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_'));
        // MSVC tools still encounter MAX_PATH restrictions; keep the label short, retain uniqueness.
        Root = Path.GetFullPath(Path.Combine(root, RunId, $"{name[..Math.Min(12, name.Length)]}-{Guid.NewGuid().ToString("N")[..16]}"));
    }

    public void Prepare(string fixture)
    {
        Directory.CreateDirectory(Source); Directory.CreateDirectory(Build); Directory.CreateDirectory(Logs);
        CopyDirectory(fixture, Source);
    }

    public void Delete()
    {
        // Only this instance's unique directory is ever deleted, never the configured parent.
        for (int attempt = 0; ; attempt++)
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); return; }
            catch (IOException) when (attempt < 4) { Thread.Sleep(50 * (attempt + 1)); }
            catch (UnauthorizedAccessException) when (attempt < 4) { Thread.Sleep(50 * (attempt + 1)); }
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"Fixture symlinks/reparse points are not supported: {from}");
        Directory.CreateDirectory(to);
        foreach (string entry in Directory.EnumerateFileSystemEntries(from))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Fixture symlinks/reparse points are not supported: {entry}");
            string destination = Path.Combine(to, Path.GetFileName(entry));
            if ((attributes & FileAttributes.Directory) != 0) CopyDirectory(entry, destination);
            else File.Copy(entry, destination);
        }
    }

    private static string FindRepository()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Xenon.sln"))) return directory.FullName;
        return Path.GetTempPath();
    }
}
