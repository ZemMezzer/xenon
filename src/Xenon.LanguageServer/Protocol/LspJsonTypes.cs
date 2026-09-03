using System.Text.Json;
using System.Text.Json.Serialization;

namespace Xenon.LanguageServer.Protocol;

internal sealed record JsonRpcSuccessResponse(
    [property: JsonPropertyName("jsonrpc")] string Jsonrpc,
    [property: JsonPropertyName("id")] JsonElement Id,
    [property: JsonPropertyName("result"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    object? Result);

internal sealed record JsonRpcErrorResponse(
    [property: JsonPropertyName("jsonrpc")] string Jsonrpc,
    [property: JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    JsonElement? Id,
    [property: JsonPropertyName("error")] JsonRpcError Error);

internal sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] object? Data);

internal sealed record JsonRpcNotification(
    [property: JsonPropertyName("jsonrpc")] string Jsonrpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] object? Params);

internal sealed record LspWorkspaceSymbol(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("location")] LspLocation Location,
    [property: JsonPropertyName("containerName")] string? ContainerName);

internal sealed record LspDiagnosticRelatedInformation(
    [property: JsonPropertyName("location")] LspLocation Location,
    [property: JsonPropertyName("message")] string Message);

internal sealed record LspDiagnostic(
    [property: JsonPropertyName("range")] LspRange Range,
    [property: JsonPropertyName("severity")] int Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("relatedInformation")]
    LspDiagnosticRelatedInformation[]? RelatedInformation);

internal sealed record LspMarkupContent(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("value")] string Value);

internal sealed record LspHover(
    [property: JsonPropertyName("contents")] LspMarkupContent Contents,
    [property: JsonPropertyName("range")] LspRange? Range);

internal sealed record LspCompletionItem(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] int Kind,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("insertText")] string InsertText,
    [property: JsonPropertyName("sortText")] string SortText,
    [property: JsonPropertyName("filterText")] string FilterText);

internal sealed record LspCompletionList(
    [property: JsonPropertyName("isIncomplete")] bool IsIncomplete,
    [property: JsonPropertyName("items")] LspCompletionItem[] Items);

internal sealed record LspParameterInformation(
    [property: JsonPropertyName("label")] string Label);

internal sealed record LspSignatureInformation(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("parameters")] LspParameterInformation[] Parameters);

internal sealed record LspSignatureHelp(
    [property: JsonPropertyName("signatures")] LspSignatureInformation[] Signatures,
    [property: JsonPropertyName("activeSignature")] int ActiveSignature,
    [property: JsonPropertyName("activeParameter")] int ActiveParameter);

internal sealed record LspSemanticTokens([property: JsonPropertyName("data")] int[] Data);

internal sealed record LspPrepareRename(
    [property: JsonPropertyName("range")] LspRange? Range,
    [property: JsonPropertyName("placeholder")] string Placeholder);

internal sealed record LspWorkspaceEdit(
    [property: JsonPropertyName("changes")] Dictionary<string, LspTextEdit[]> Changes);

internal sealed record LspPublishDiagnostics(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("version"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Version,
    [property: JsonPropertyName("diagnostics")] LspDiagnostic[] Diagnostics);

internal sealed record LspInitializeResult(
    [property: JsonPropertyName("capabilities")] LspServerCapabilities Capabilities,
    [property: JsonPropertyName("serverInfo")] LspServerInfo ServerInfo);

internal sealed record LspServerInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version")] string Version);

internal sealed record LspServerCapabilities(
    [property: JsonPropertyName("positionEncoding")] string PositionEncoding,
    [property: JsonPropertyName("textDocumentSync")] LspTextDocumentSyncOptions TextDocumentSync,
    [property: JsonPropertyName("hoverProvider")] bool HoverProvider,
    [property: JsonPropertyName("definitionProvider")] bool DefinitionProvider,
    [property: JsonPropertyName("typeDefinitionProvider")] bool TypeDefinitionProvider,
    [property: JsonPropertyName("referencesProvider")] bool ReferencesProvider,
    [property: JsonPropertyName("implementationProvider")] bool ImplementationProvider,
    [property: JsonPropertyName("documentSymbolProvider")] bool DocumentSymbolProvider,
    [property: JsonPropertyName("workspaceSymbolProvider")] bool WorkspaceSymbolProvider,
    [property: JsonPropertyName("completionProvider")] LspCompletionOptions CompletionProvider,
    [property: JsonPropertyName("signatureHelpProvider")] LspSignatureHelpOptions SignatureHelpProvider,
    [property: JsonPropertyName("semanticTokensProvider")] LspSemanticTokensOptions SemanticTokensProvider,
    [property: JsonPropertyName("renameProvider")] LspRenameOptions RenameProvider);

internal sealed record LspTextDocumentSyncOptions(
    [property: JsonPropertyName("openClose")] bool OpenClose,
    [property: JsonPropertyName("change")] int Change,
    [property: JsonPropertyName("save")] LspSaveOptions Save);

internal sealed record LspSaveOptions(
    [property: JsonPropertyName("includeText")] bool IncludeText);

internal sealed record LspCompletionOptions(
    [property: JsonPropertyName("triggerCharacters")] string[] TriggerCharacters,
    [property: JsonPropertyName("resolveProvider")] bool ResolveProvider);

internal sealed record LspSignatureHelpOptions(
    [property: JsonPropertyName("triggerCharacters")] string[] TriggerCharacters,
    [property: JsonPropertyName("retriggerCharacters")] string[] RetriggerCharacters);

internal sealed record LspSemanticTokensOptions(
    [property: JsonPropertyName("legend")] LspSemanticTokensLegend Legend,
    [property: JsonPropertyName("full")] bool Full,
    [property: JsonPropertyName("range")] bool Range);

internal sealed record LspSemanticTokensLegend(
    [property: JsonPropertyName("tokenTypes")] string[] TokenTypes,
    [property: JsonPropertyName("tokenModifiers")] string[] TokenModifiers);

internal sealed record LspRenameOptions(
    [property: JsonPropertyName("prepareProvider")] bool PrepareProvider);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JsonRpcSuccessResponse))]
[JsonSerializable(typeof(JsonRpcErrorResponse))]
[JsonSerializable(typeof(JsonRpcNotification))]
[JsonSerializable(typeof(LspWorkspaceSymbol[]))]
[JsonSerializable(typeof(LspDiagnostic[]))]
[JsonSerializable(typeof(LspHover))]
[JsonSerializable(typeof(LspLocation[]))]
[JsonSerializable(typeof(List<LspDocumentSymbol>))]
[JsonSerializable(typeof(LspCompletionList))]
[JsonSerializable(typeof(LspSignatureHelp))]
[JsonSerializable(typeof(LspSemanticTokens))]
[JsonSerializable(typeof(LspPrepareRename))]
[JsonSerializable(typeof(LspWorkspaceEdit))]
[JsonSerializable(typeof(LspPublishDiagnostics))]
[JsonSerializable(typeof(LspInitializeResult))]
internal sealed partial class LspJsonSerializerContext : JsonSerializerContext;
