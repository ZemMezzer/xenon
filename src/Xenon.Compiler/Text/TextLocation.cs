namespace Xenon.Compiler.Text;

public readonly record struct TextLocation(SourceText Source, TextSpan Span)
{
    public LinePosition Start => Source.GetLinePosition(Span.Start);

    public LinePosition End => Source.GetLinePosition(Span.End);
}
