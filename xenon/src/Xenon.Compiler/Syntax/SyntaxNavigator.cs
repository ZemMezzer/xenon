using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

/// <summary>
/// Editor-agnostic traversal helpers for the immutable Xenon syntax model. The syntax records do
/// not carry parent/child tables; this navigator derives children from their public record shape
/// and caches that shape once per node type.
/// </summary>
public static class SyntaxNavigator
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ChildProperties = new();

    public static IEnumerable<SyntaxNode> DescendantNodesAndSelf(SyntaxNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        yield return root;
        foreach (SyntaxNode child in GetChildren(root))
            foreach (SyntaxNode descendant in DescendantNodesAndSelf(child))
                yield return descendant;
    }

    public static IEnumerable<SyntaxToken> GetTokens(SyntaxNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        foreach (PropertyInfo property in Properties(node.GetType()))
        {
            object? value = property.GetValue(node);
            if (value is SyntaxToken token)
                yield return token;
            else if (value is SyntaxNode child)
                foreach (SyntaxToken nested in GetTokens(child)) yield return nested;
            else if (value is IEnumerable sequence and not string)
                foreach (object? item in sequence)
                    if (item is SyntaxToken itemToken) yield return itemToken;
                    else if (item is SyntaxNode itemNode)
                        foreach (SyntaxToken nested in GetTokens(itemNode)) yield return nested;
        }
    }

    public static TextSpan GetSpan(SyntaxNode node)
    {
        SyntaxToken[] tokens = GetTokens(node).Where(token => !token.IsMissing).ToArray();
        if (tokens.Length == 0)
        {
            SyntaxToken? missing = GetTokens(node).FirstOrDefault();
            return missing?.Location.Span ?? default;
        }
        int start = tokens.Min(token => token.Location.Span.Start);
        int end = tokens.Max(token => token.Location.Span.End);
        return TextSpan.FromBounds(start, end);
    }

    public static SyntaxNode? FindInnermostNode(SyntaxNode root, int position,
        Func<SyntaxNode, bool>? predicate = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return DescendantNodesAndSelf(root)
            .Where(node => predicate?.Invoke(node) != false)
            .Select(node => (Node: node, Span: GetSpan(node)))
            .Where(item => Contains(item.Span, position) ||
                GetTokens(item.Node).Any(token => token.IsMissing && token.Location.Span.Start == position))
            .OrderBy(item => item.Span.Length)
            .ThenByDescending(item => item.Span.Start)
            .Select(item => item.Node).FirstOrDefault();
    }

    private static IEnumerable<SyntaxNode> GetChildren(SyntaxNode node)
    {
        foreach (PropertyInfo property in Properties(node.GetType()))
        {
            object? value = property.GetValue(node);
            if (value is SyntaxNode child) yield return child;
            else if (value is IEnumerable sequence and not string)
                foreach (object? item in sequence)
                    if (item is SyntaxNode itemNode) yield return itemNode;
        }
    }

    private static PropertyInfo[] Properties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type)
    {
        if (ChildProperties.TryGetValue(type, out PropertyInfo[]? properties)) return properties;

        PropertyInfo[] discovered = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0)
            .ToArray();
        return ChildProperties.GetOrAdd(type, discovered);
    }

    private static bool Contains(TextSpan span, int position) =>
        position >= span.Start && (position < span.End || span.Length == 0 && position == span.Start);
}
