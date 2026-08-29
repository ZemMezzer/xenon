using Xenon.Compiler.Text;

namespace Xenon.Compiler.Diagnostics;

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextLocation Location)
{
    // Optional until the compiler has a stable diagnostic catalog; never infer IDs from message text.
    public string? Id { get; init; }
}
