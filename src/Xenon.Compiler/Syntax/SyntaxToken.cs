using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

public sealed record SyntaxToken(
    SyntaxKind Kind,
    TextLocation Location,
    string Text,
    object? Value = null,
    bool IsMissing = false);
