using Xenon.ProjectSystem;

namespace Xenon.LanguageServer;

/// <summary>
/// Order-preserving mapping of the full signed LSP integer domain above Workspace's zero sentinel.
/// </summary>
public static class LspDocumentVersions
{
    public static DocumentVersion FromLsp(int version) =>
        new((long)version - int.MinValue + 1L);

    public static int ToLsp(DocumentVersion version)
    {
        long mapped = version.Value + int.MinValue - 1L;
        if (mapped < int.MinValue || mapped > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(version),
                "DocumentVersion does not represent an LSP-supplied version.");
        return (int)mapped;
    }
}
