using System.Collections;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Diagnostics;

public sealed class DiagnosticBag : IReadOnlyCollection<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics = [];

    public int Count => _diagnostics.Count;

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Report(TextLocation location, string message) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location));

    public void ReportInvalidCharacter(TextLocation location, char character) =>
        Report(location, $"invalid character '{character}'");

    public void ReportInvalidNumber(TextLocation location, string text, string expectedType) =>
        Report(location, $"'{text}' is not a valid {expectedType} literal");

    public void ReportUnterminatedString(TextLocation location) =>
        Report(location, "unterminated string literal");

    public void ReportUnknownEscapeSequence(TextLocation location, char character) =>
        Report(location, $"unknown escape sequence '\\{character}'");

    public void ReportUnterminatedBlockComment(TextLocation location) =>
        Report(location, "unterminated block comment");

    public void ReportUnexpectedToken(
        TextLocation location,
        SyntaxKind actualKind,
        SyntaxKind expectedKind) =>
        Report(location, $"unexpected token '{actualKind}', expected '{expectedKind}'");

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);
}
