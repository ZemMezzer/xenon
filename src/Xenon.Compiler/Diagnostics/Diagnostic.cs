using Xenon.Compiler.Text;

namespace Xenon.Compiler.Diagnostics;

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Message,
    TextLocation Location);
