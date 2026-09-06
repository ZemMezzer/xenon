using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public enum SymbolOriginKind
{
    Source,
    Library,
    CompilerGenerated,
}

/// <summary>Opaque implementation payload, independent from semantic symbol state.</summary>
public abstract record SymbolImplementation;

internal sealed record SourceSymbolImplementation(SyntaxNode Declaration) : SymbolImplementation;

/// <summary>Optional provenance used by tooling; it is never required to describe semantics.</summary>
public sealed record SymbolOrigin(SymbolOriginKind Kind, ImmutableArray<SyntaxReference> SyntaxReferences)
{
    public static SymbolOrigin Source(SyntaxNode declaration) =>
        new(SymbolOriginKind.Source, [new SyntaxReference(declaration)]);

    public static SymbolOrigin Library { get; } = new(SymbolOriginKind.Library, []);
    public static SymbolOrigin CompilerGenerated { get; } = new(SymbolOriginKind.CompilerGenerated, []);
}

/// <summary>Source-independent, serialization-ready documentation attached to a semantic symbol.</summary>
public sealed record SymbolDocumentation(
    string? Summary,
    string? Remarks,
    string? Returns,
    ImmutableDictionary<string, string> Parameters,
    ImmutableDictionary<string, string> TypeParameters)
{
    public static SymbolDocumentation Empty { get; } = new(null, null, null,
        ImmutableDictionary<string, string>.Empty, ImmutableDictionary<string, string>.Empty);

    public bool IsEmpty => Summary is null && Remarks is null && Returns is null &&
        Parameters.IsEmpty && TypeParameters.IsEmpty;

    internal static SymbolDocumentation FromDeclaration(SyntaxNode declaration)
    {
        string? text = SyntaxNavigator.GetTokens(declaration)
            .Where(token => !token.IsMissing)
            .OrderBy(token => token.Location.Span.Start)
            .FirstOrDefault()
            ?.LeadingDocumentation;
        return Parse(text);
    }

    public static SymbolDocumentation Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;
        try
        {
            XElement root = XElement.Parse($"<doc>{text}</doc>", LoadOptions.PreserveWhitespace);
            return new SymbolDocumentation(
                ElementText(root.Element("summary")),
                ElementText(root.Element("remarks")),
                ElementText(root.Element("returns")),
                NamedElements(root, "param"),
                NamedElements(root, "typeparam"));
        }
        catch
        {
            // Documentation is intentionally non-semantic. Preserve useful text even when markup is malformed.
            string fallback = Normalize(Regex.Replace(text, "<[^>]*>", " "));
            return fallback.Length == 0 ? Empty : Empty with { Summary = fallback };
        }
    }

    private static ImmutableDictionary<string, string> NamedElements(XElement root, string name)
    {
        var result = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (XElement element in root.Elements(name))
        {
            string? key = element.Attribute("name")?.Value;
            string value = Normalize(element.Value);
            if (!string.IsNullOrWhiteSpace(key) && value.Length > 0) result[key] = value;
        }
        return result.ToImmutable();
    }

    private static string? ElementText(XElement? element) => element is null ? null :
        Normalize(element.Value) is { Length: > 0 } value ? value : null;

    private static string Normalize(string value) =>
        Regex.Replace(value, @"\s+", " ").Trim();
}
