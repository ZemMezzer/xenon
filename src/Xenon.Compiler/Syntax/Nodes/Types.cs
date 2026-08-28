using System.Collections.Immutable;

namespace Xenon.Compiler.Syntax;

public abstract record TypeSyntax : SyntaxNode
{
    public abstract SyntaxToken NameToken { get; }
    public abstract string Name { get; }
}

public sealed record NamedTypeSyntax(
    ImmutableArray<SyntaxToken> NameParts,
    ImmutableArray<SyntaxToken> DotTokens,
    TypeArgumentListSyntax? TypeArguments = null) : TypeSyntax
{
    public override SyntaxKind Kind => SyntaxKind.NamedType;
    public override SyntaxToken NameToken => NameParts[^1];
    public override string Name => string.Join('.', NameParts.Select(part => part.Text));
}

public abstract record UnaryTypeSyntax(TypeSyntax ElementType) : TypeSyntax
{
    public override SyntaxToken NameToken => ElementType.NameToken;
    public override string Name => ElementType.Name;
}

public sealed record PointerTypeSyntax(TypeSyntax ElementType, SyntaxToken StarToken) : UnaryTypeSyntax(ElementType)
{
    public override SyntaxKind Kind => SyntaxKind.PointerType;
}

public sealed record ReferenceTypeSyntax(TypeSyntax ElementType, SyntaxToken AmpersandToken) : UnaryTypeSyntax(ElementType)
{
    public override SyntaxKind Kind => SyntaxKind.ReferenceType;
}

// Array suffixes are written outermost first: int[][,] is an array of matrices.
public sealed record ArrayTypeSyntax(
    TypeSyntax ElementType,
    SyntaxToken OpenBracketToken,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseBracketToken) : UnaryTypeSyntax(ElementType)
{
    public override SyntaxKind Kind => SyntaxKind.ArrayType;
    public int Rank => CommaTokens.Length + 1;
}

public enum TypeQualifierPosition { Prefix, Postfix }

public sealed record QualifiedTypeSyntax(
    TypeSyntax ElementType,
    SyntaxToken QualifierToken,
    TypeQualifierPosition Position = TypeQualifierPosition.Prefix) : UnaryTypeSyntax(ElementType)
{
    public override SyntaxKind Kind => SyntaxKind.QualifiedType;
}

public sealed record TypeArgumentListSyntax(
    SyntaxToken LessToken,
    ImmutableArray<TypeSyntax> Arguments,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken GreaterToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.TypeArgumentList;
}

/// <summary>Queries and transformations along a type's construction chain (excluding generic arguments).</summary>
public static class TypeSyntaxFacts
{
    public static IEnumerable<TypeSyntax> ConstructionChain(this TypeSyntax type)
    {
        while (true)
        {
            yield return type;
            if (type is not UnaryTypeSyntax unary) yield break;
            type = unary.ElementType;
        }
    }

    public static bool Contains<T>(this TypeSyntax type) where T : TypeSyntax => type.ConstructionChain().Any(node => node is T);

    public static SyntaxToken? GetQualifier(this TypeSyntax type, SyntaxKind kind,
        TypeQualifierPosition position = TypeQualifierPosition.Prefix) =>
        type.ConstructionChain().OfType<QualifiedTypeSyntax>()
            .FirstOrDefault(node => node.QualifierToken.Kind == kind && node.Position == position)?.QualifierToken;

    public static bool IsBindingReadonly(this TypeSyntax type) =>
        type.GetQualifier(SyntaxKind.ReadonlyKeyword, type.Contains<PointerTypeSyntax>()
            ? TypeQualifierPosition.Postfix : TypeQualifierPosition.Prefix) is not null;

    public static TypeSyntax WithoutQualifier(this TypeSyntax type, SyntaxKind kind,
        TypeQualifierPosition position = TypeQualifierPosition.Prefix) => type switch
    {
        QualifiedTypeSyntax qualified when qualified.QualifierToken.Kind == kind && qualified.Position == position =>
            qualified.ElementType.WithoutQualifier(kind, position),
        UnaryTypeSyntax unary => unary with { ElementType = unary.ElementType.WithoutQualifier(kind, position) },
        _ => type,
    };
}
