using System.Text.Json.Serialization;

namespace Xenon.LanguageServer.Protocol;

public readonly record struct LspPosition(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("character")] int Character);

public readonly record struct LspRange(
    [property: JsonPropertyName("start")] LspPosition Start,
    [property: JsonPropertyName("end")] LspPosition End);

public sealed record LspLocation(
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("range")] LspRange Range);

public sealed record LspLocationLink(
    [property: JsonPropertyName("targetUri")] string TargetUri,
    [property: JsonPropertyName("targetRange")] LspRange TargetRange,
    [property: JsonPropertyName("targetSelectionRange")] LspRange TargetSelectionRange,
    [property: JsonPropertyName("originSelectionRange"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    LspRange? OriginSelectionRange = null);

public sealed class LspDocumentSymbol
{
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("detail"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }
    [JsonPropertyName("kind")] public required int Kind { get; init; }
    [JsonPropertyName("range")] public required LspRange Range { get; init; }
    [JsonPropertyName("selectionRange")] public required LspRange SelectionRange { get; init; }
    [JsonPropertyName("children"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LspDocumentSymbol>? Children { get; set; }
}

public sealed record LspTextEdit(
    [property: JsonPropertyName("range")] LspRange Range,
    [property: JsonPropertyName("newText")] string NewText);

public static class LspErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerNotInitialized = -32002;
    public const int RequestCancelled = -32800;
}
