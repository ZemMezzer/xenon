using System.Security.Cryptography;
using System.Text;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.ProjectSystem;

internal static class DeclarationFingerprint
{
    public static string Create(SyntaxTree tree, CancellationToken cancellationToken = default)
    {
        // Recovery trees are conservatively declaration-affecting: their complete token stream
        // participates so an uncertain edit can never produce false semantic reuse.
        TextSpan[] bodies = tree.Diagnostics.IsEmpty ? GetBodies(tree.Root).OrderBy(span => span.Start).ToArray() : [];
        var builder = new StringBuilder();
        foreach (SyntaxToken token in tree.Tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bodies.Any(body => Contains(body, token.Location.Span))) continue;
            builder.Append((int)token.Kind).Append(':').Append(token.Text.Length).Append(':')
                .Append(token.Text).Append(';');
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool Contains(TextSpan outer, TextSpan inner) =>
        inner.Start >= outer.Start && inner.End <= outer.End;

    private static IEnumerable<TextSpan> GetBodies(CompilationUnitSyntax root)
    {
        foreach (MemberDeclarationSyntax member in root.Members)
        {
            switch (member)
            {
                case FunctionDeclarationSyntax { Body: { } body }:
                    yield return GetSpan(body);
                    break;
                case StructDeclarationSyntax structure:
                    foreach (TextSpan span in GetStructBodies(structure)) yield return span;
                    break;
            }
        }
    }

    private static IEnumerable<TextSpan> GetStructBodies(StructDeclarationSyntax structure)
    {
        foreach (TypeMemberDeclarationSyntax member in structure.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax { Body: { } body }:
                    yield return GetSpan(body);
                    break;
                case ConstructorDeclarationSyntax constructor:
                    yield return GetSpan(constructor.Body);
                    break;
                case DestructorDeclarationSyntax destructor:
                    yield return GetSpan(destructor.Body);
                    break;
                case PropertyDeclarationSyntax property:
                    foreach (PropertyAccessorDeclarationSyntax accessor in property.Accessors)
                        if (accessor.Body is { } accessorBody) yield return GetSpan(accessorBody);
                    break;
                case IndexerDeclarationSyntax indexer:
                    foreach (PropertyAccessorDeclarationSyntax accessor in indexer.Accessors)
                        if (accessor.Body is { } accessorBody) yield return GetSpan(accessorBody);
                    break;
            }
        }
    }

    private static TextSpan GetSpan(BlockStatementSyntax body)
    {
        int start = body.OpenBraceToken.Location.Span.Start;
        int end = body.CloseBraceToken.Location.Span.End;
        return new TextSpan(start, Math.Max(0, end - start));
    }
}
