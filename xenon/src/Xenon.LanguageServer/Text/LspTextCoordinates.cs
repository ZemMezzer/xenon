using Xenon.Compiler.Text;
using Xenon.LanguageServer.Protocol;
using Xenon.ProjectSystem;

namespace Xenon.LanguageServer.Text;

/// <summary>The single UTF-16 coordinate conversion boundary used by LSP handlers.</summary>
public static class LspTextCoordinates
{
    public static int ToOffset(SourceText source, LspPosition position)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (position.Line < 0 || position.Line >= source.LineCount)
            throw new ArgumentOutOfRangeException(nameof(position), "LSP line is outside the source.");
        if (position.Character < 0)
            throw new ArgumentOutOfRangeException(nameof(position), "LSP character cannot be negative.");

        int lineStart = source.GetLineStart(position.Line);
        int lineLength = source.GetLineText(position.Line).Length;
        if (position.Character > lineLength)
            throw new ArgumentOutOfRangeException(nameof(position),
                "LSP character is outside the line's UTF-16 content.");
        return checked(lineStart + position.Character);
    }

    public static LspPosition ToPosition(SourceText source, int offset)
    {
        LinePosition position = source.GetLinePosition(offset);
        return new LspPosition(position.Line, position.Character);
    }

    public static TextSpan ToTextSpan(SourceText source, LspRange range)
    {
        int start = ToOffset(source, range.Start);
        int end = ToOffset(source, range.End);
        if (end < start)
            throw new ArgumentOutOfRangeException(nameof(range), "LSP range end precedes its start.");
        return TextSpan.FromBounds(start, end);
    }

    public static LspRange ToRange(SourceText source, TextSpan span)
    {
        if (span.Start < 0 || span.End < span.Start || span.End > source.Length)
            throw new ArgumentOutOfRangeException(nameof(span));
        return new LspRange(ToPosition(source, span.Start), ToPosition(source, span.End));
    }

    public static LspLocation ToLocation(SourceReference source, SourceText text) =>
        new(DocumentUri.FromPath(source.Path).AbsoluteUri, ToRange(text, source.Span));

    public static LspLocationLink ToLocationLink(SourceReference source, SourceText text,
        LspRange? originSelectionRange = null)
    {
        LspRange range = ToRange(text, source.Span);
        return new LspLocationLink(DocumentUri.FromPath(source.Path).AbsoluteUri, range, range,
            originSelectionRange);
    }
}
