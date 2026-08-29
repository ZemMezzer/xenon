using System.Collections;
using System.Collections.Immutable;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Diagnostics;

public sealed class DiagnosticBag : IReadOnlyCollection<Diagnostic>
{
    private readonly List<Diagnostic> _diagnostics;
    public DiagnosticBag() => _diagnostics = [];

    public int Count => _diagnostics.Count;

    public IEnumerator<Diagnostic> GetEnumerator() => _diagnostics.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Report(TextLocation location, string message, string id,
        IEnumerable<RelatedDiagnosticLocation>? relatedLocations = null) =>
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Error, message, location)
        {
            Id = id,
            RelatedLocations = relatedLocations?.ToImmutableArray() ?? [],
        });

    public void ReportInvalidCharacter(TextLocation location, char character) =>
        Report(location, $"invalid character '{character}'", DiagnosticIds.InvalidCharacter);

    public void ReportInvalidNumber(TextLocation location, string text, string expectedType) =>
        Report(location, $"'{text}' is not a valid {expectedType} literal", DiagnosticIds.InvalidNumber);

    public void ReportUnterminatedString(TextLocation location) =>
        Report(location, "unterminated string literal", DiagnosticIds.UnterminatedString);

    public void ReportUnknownEscapeSequence(TextLocation location, char character) =>
        Report(location, $"unknown escape sequence '\\{character}'", DiagnosticIds.UnknownEscapeSequence);

    public void ReportUnterminatedBlockComment(TextLocation location) =>
        Report(location, "unterminated block comment", DiagnosticIds.UnterminatedBlockComment);

    public void ReportUnexpectedToken(
        TextLocation location,
        SyntaxKind actualKind,
        SyntaxKind expectedKind) =>
        Report(location, $"unexpected token '{actualKind}', expected '{expectedKind}'", DiagnosticIds.UnexpectedToken);

    public void AddRange(IEnumerable<Diagnostic> diagnostics) => _diagnostics.AddRange(diagnostics);
}
