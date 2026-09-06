namespace Xenon.Compiler.Text;

public readonly record struct TextLocation(SourceText Source, TextSpan Span)
{
    /// <summary>A valid non-source fallback for diagnostics about metadata-only symbols.</summary>
    public static TextLocation None { get; } = new(SourceText.From(string.Empty, "<metadata>"), new TextSpan(0, 0));

    /// <summary>The file identity of this immutable source snapshot.</summary>
    public string Path => Source.Path;

    public LinePosition Start => Source.GetLinePosition(Span.Start);

    public LinePosition End => Source.GetLinePosition(Span.End);
}
