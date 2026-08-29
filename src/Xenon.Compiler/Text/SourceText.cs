using System.Collections.Immutable;

namespace Xenon.Compiler.Text;

public sealed class SourceText
{
    private readonly ImmutableArray<int> _lineStarts;

    private SourceText(string text, string path, SourceFileId fileId)
    {
        Text = text;
        Path = path;
        FileId = fileId;
        _lineStarts = BuildLineStarts(text);
    }

    public string Text { get; }

    public string Path { get; }

    public SourceFileId FileId { get; }

    public int Length => Text.Length;

    public char this[int index] => Text[index];

    public static SourceText From(string text, string path = "<memory>") =>
        new(text, path, SourceFileId.CreateNew());

    public static SourceText From(string text, string path, SourceFileId fileId) =>
        new(text, path, fileId);

    /// <summary>Creates a new content version of the same logical source file.</summary>
    public SourceText WithText(string text) => new(text, Path, FileId);

    /// <summary>Creates a path-updated version while preserving logical source identity.</summary>
    public SourceText WithPath(string path) => new(Text, path, FileId);

    public string GetText(TextSpan span) => Text.Substring(span.Start, span.Length);

    public LinePosition GetLinePosition(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Length);

        int line = FindLineIndex(position);
        return new LinePosition(line, position - _lineStarts[line]);
    }

    public string GetLineText(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, _lineStarts.Length);

        int start = _lineStarts[line];
        int end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Length;

        while (end > start && Text[end - 1] is '\r' or '\n')
        {
            end--;
        }

        return Text[start..end];
    }

    private int FindLineIndex(int position)
    {
        int index = _lineStarts.BinarySearch(position);
        return index >= 0 ? index : ~index - 1;
    }

    private static ImmutableArray<int> BuildLineStarts(string text)
    {
        var builder = ImmutableArray.CreateBuilder<int>();
        builder.Add(0);

        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                index++;
                builder.Add(index + 1);
            }
            else if (text[index] is '\r' or '\n')
            {
                builder.Add(index + 1);
            }
        }

        return builder.ToImmutable();
    }
}
