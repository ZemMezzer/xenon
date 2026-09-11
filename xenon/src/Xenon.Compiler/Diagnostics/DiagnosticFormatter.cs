namespace Xenon.Compiler.Diagnostics;

public static class DiagnosticFormatter
{
    public static void Write(TextWriter writer, Diagnostic diagnostic)
    {
        var start = diagnostic.Location.Start;
        string severity = diagnostic.Severity.ToString().ToLowerInvariant();

        writer.WriteLine($"{severity} {diagnostic.Id}: {diagnostic.Message}");
        writer.WriteLine($" --> {diagnostic.Location.Source.Path}:{start.Line + 1}:{start.Character + 1}");

        string lineText = diagnostic.Location.Source.GetLineText(start.Line);
        writer.WriteLine();
        writer.WriteLine($" {start.Line + 1,4} | {lineText}");

        int markerLength = Math.Max(1, Math.Min(diagnostic.Location.Span.Length, lineText.Length - start.Character));
        writer.WriteLine($"      | {new string(' ', start.Character)}{new string('^', markerLength)}");
        foreach (RelatedDiagnosticLocation related in diagnostic.RelatedLocations)
        {
            var relatedStart = related.Location.Start;
            writer.WriteLine($" note: {related.Message ?? "related location"}");
            writer.WriteLine($"  --> {related.Location.Path}:{relatedStart.Line + 1}:{relatedStart.Character + 1}");
        }
        writer.WriteLine();
    }
}
