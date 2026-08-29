namespace Xenon.Compiler.Diagnostics;

public static class DiagnosticFormatter
{
    public static void Write(TextWriter writer, Diagnostic diagnostic)
    {
        var start = diagnostic.Location.Start;
        string severity = diagnostic.Severity.ToString().ToLowerInvariant();

        writer.WriteLine($"{severity}{(diagnostic.Id is null ? "" : " " + diagnostic.Id)}: {diagnostic.Message}");
        writer.WriteLine($" --> {diagnostic.Location.Source.Path}:{start.Line + 1}:{start.Character + 1}");

        string lineText = diagnostic.Location.Source.GetLineText(start.Line);
        writer.WriteLine();
        writer.WriteLine($" {start.Line + 1,4} | {lineText}");

        int markerLength = Math.Max(1, Math.Min(diagnostic.Location.Span.Length, lineText.Length - start.Character));
        writer.WriteLine($"      | {new string(' ', start.Character)}{new string('^', markerLength)}");
        writer.WriteLine();
    }
}
