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
        hoverProvider = true,
        definitionProvider = true,
        typeDefinitionProvider = true,
        referencesProvider = true,
        implementationProvider = true,
        documentSymbolProvider = true,
        workspaceSymbolProvider = true,
        completionProvider = new { triggerCharacters = new[] { "." }, resolveProvider = false },
        signatureHelpProvider = new { triggerCharacters = new[] { "(", ",", "[" },
            retriggerCharacters = new[] { "," } },
        semanticTokensProvider = new
        {
            legend = new
            {
                tokenTypes = LspCoreIntelligence.SemanticTokenTypes,
                tokenModifiers = LspCoreIntelligence.SemanticTokenModifiers,
            },
            full = true,
            range = false,
        },
        renameProvider = new { prepareProvider = true },
    };
}
