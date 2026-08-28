namespace Xenon.Compiler.Text;

public readonly record struct TextLocation(SourceText Source, TextSpan Span)
{
    /// <summary>The file identity of this immutable source snapshot.</summary>
    public string Path => Source.Path;

    public LinePosition Start => Source.GetLinePosition(Span.Start);

    public LinePosition End => Source.GetLinePosition(Span.End);
}
