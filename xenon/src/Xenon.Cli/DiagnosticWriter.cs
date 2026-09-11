using Xenon.Compiler.Diagnostics;

namespace Xenon.Cli;

internal static class DiagnosticWriter
{
    public static void Write(TextWriter writer, Diagnostic diagnostic) => DiagnosticFormatter.Write(writer, diagnostic);
}