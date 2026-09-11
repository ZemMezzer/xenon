using Xenon.LanguageServer.Protocol;

namespace Xenon.LanguageServer;

/// <summary>The single capability construction path extended by later LSP epics.</summary>
public static class ServerCapabilities
{
    public static object Create() => CreateTyped();

    internal static LspServerCapabilities CreateTyped() => new(
        "utf-16",
        new LspTextDocumentSyncOptions(true, 2, new LspSaveOptions(false)),
        HoverProvider: true,
        DefinitionProvider: true,
        TypeDefinitionProvider: true,
        ReferencesProvider: true,
        ImplementationProvider: true,
        DocumentSymbolProvider: true,
        WorkspaceSymbolProvider: true,
        new LspCompletionOptions([".", ">"], ResolveProvider: false),
        new LspSignatureHelpOptions(["(", ",", "["], [","]),
        new LspSemanticTokensOptions(
            new LspSemanticTokensLegend(LspCoreIntelligence.SemanticTokenTypes,
                LspCoreIntelligence.SemanticTokenModifiers),
            Full: true,
            Range: false),
        new LspRenameOptions(PrepareProvider: true));
}
