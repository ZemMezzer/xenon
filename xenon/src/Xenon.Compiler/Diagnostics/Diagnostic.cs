using System.Collections.Immutable;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Diagnostics;

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextLocation Location)
{
    public required string Id { get; init; }
    public ImmutableArray<RelatedDiagnosticLocation> RelatedLocations { get; init; } = [];
}

public sealed record RelatedDiagnosticLocation(TextLocation Location, string? Message = null);
