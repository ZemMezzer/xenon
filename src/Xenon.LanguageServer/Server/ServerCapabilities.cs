namespace Xenon.LanguageServer;

/// <summary>The single capability construction path extended by later LSP epics.</summary>
public static class ServerCapabilities
{
    public static object Create() => new
    {
        positionEncoding = "utf-16",
        textDocumentSync = new
        {
            openClose = true,
            change = 2,
            save = new { includeText = false },
        },
    };
}
