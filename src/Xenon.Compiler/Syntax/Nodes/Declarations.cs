using System.Collections.Immutable;

namespace Xenon.Compiler.Syntax;

public sealed record CompilationUnitSyntax(
    ImmutableArray<UsingDirectiveSyntax> Usings,
    NamespaceDeclarationSyntax Namespace,
    ImmutableArray<MemberDeclarationSyntax> Members,
    SyntaxToken EndOfFileToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.CompilationUnit;
}

public sealed record UsingDirectiveSyntax(
    SyntaxToken UsingKeyword,
    SyntaxToken? AliasToken,
    SyntaxToken? EqualsToken,
    ImmutableArray<SyntaxToken> NameParts,
    ImmutableArray<SyntaxToken> DotTokens,
    SyntaxToken SemicolonToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.UsingDirective;

    public bool HasAlias => AliasToken is not null;

    public string Name => string.Join('.', NameParts.Select(part => part.Text));
}

public sealed record NamespaceDeclarationSyntax(
    SyntaxToken NamespaceKeyword,
    ImmutableArray<SyntaxToken> NameParts,
    ImmutableArray<SyntaxToken> DotTokens,
    SyntaxToken SemicolonToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.NamespaceDeclaration;

    public string Name => string.Join('.', NameParts.Select(part => part.Text));
}

public sealed record TypeSyntax(
    SyntaxToken? ConstKeyword,
    SyntaxToken? ReadonlyKeyword,
    ImmutableArray<SyntaxToken> NameParts,
    ImmutableArray<SyntaxToken> DotTokens,
    ImmutableArray<SyntaxToken> PointerTokens,
    SyntaxToken? ReferenceToken,
    SyntaxToken? OpenBracketToken,
    SyntaxToken? CloseBracketToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Type;

    public SyntaxToken NameToken => NameParts[^1];

    public string Name => string.Join('.', NameParts.Select(part => part.Text));

    public bool IsQualifiedName => NameParts.Length > 1;

    public bool IsConst => ConstKeyword is not null;

    public bool IsReadonly => ReadonlyKeyword is not null;

    public SyntaxToken? PointerReadonlyKeyword { get; init; }

    public bool IsBindingReadonly => PointerDepth > 0
        ? PointerReadonlyKeyword is not null
        : IsReadonly;

    // Suffixes are written outermost first: int[][,] is an array of matrices.
    public ImmutableArray<int> ArrayRanks { get; init; } = [];

    public int PointerDepth => PointerTokens.Length;

    public bool IsReference => ReferenceToken is not null;

    public bool IsArray => OpenBracketToken is not null;

    public bool IsUnsizedArray => IsArray;
}

public sealed record ParameterSyntax(
    TypeSyntax Type,
    SyntaxToken IdentifierToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Parameter;
}

public sealed record EnumDeclarationSyntax(
    SyntaxToken EnumKeyword,
    SyntaxToken IdentifierToken,
    TypeSyntax? UnderlyingType,
    ImmutableArray<EnumMemberDeclarationSyntax> Members) : MemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.EnumDeclaration;
}

public sealed record EnumMemberDeclarationSyntax(
    SyntaxToken IdentifierToken,
    ExpressionSyntax? Value) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.EnumMemberDeclaration;
}

public sealed record FieldDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? StaticKeyword,
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken? EqualsToken,
    ExpressionSyntax? Initializer,
    SyntaxToken SemicolonToken) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.FieldDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
    public bool IsStatic => StaticKeyword is not null;
    public bool IsReadonly => ReadonlyKeyword is not null;
}

public sealed record ModuleConstantDeclarationSyntax(
    SyntaxToken ConstKeyword,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer,
    SyntaxToken SemicolonToken) : MemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ConstantDeclaration;
}

public sealed record StructConstantDeclarationSyntax(
    SyntaxToken ConstKeyword,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer,
    SyntaxToken SemicolonToken) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ConstantDeclaration;
}

public sealed record PropertyAccessorDeclarationSyntax(
    SyntaxToken KeywordToken,
    BlockStatementSyntax? Body,
    SyntaxToken? SemicolonToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.PropertyAccessorDeclaration;

    public bool IsGetter => KeywordToken.Kind == SyntaxKind.GetKeyword;
    public bool IsSetter => KeywordToken.Kind == SyntaxKind.SetKeyword;
}

public sealed record PropertyDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? StaticKeyword,
    SyntaxToken? VirtualKeyword,
    SyntaxToken? OverrideKeyword,
    SyntaxToken? AbstractKeyword,
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenBraceToken,
    ImmutableArray<PropertyAccessorDeclarationSyntax> Accessors,
    SyntaxToken CloseBraceToken) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.PropertyDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;
    public bool IsStatic => StaticKeyword is not null;
    public bool IsVirtual => VirtualKeyword is not null;
    public bool IsOverride => OverrideKeyword is not null;
    public bool IsAbstract => AbstractKeyword is not null;
    public bool IsReadonly => ReadonlyKeyword is not null;
    public PropertyAccessorDeclarationSyntax? Getter => Accessors.FirstOrDefault(accessor => accessor.IsGetter);
    public PropertyAccessorDeclarationSyntax? Setter => Accessors.FirstOrDefault(accessor => accessor.IsSetter);
}

public sealed record IndexerDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? StaticKeyword,
    SyntaxToken? VirtualKeyword,
    SyntaxToken? OverrideKeyword,
    SyntaxToken? AbstractKeyword,
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax Type,
    SyntaxToken ThisKeyword,
    SyntaxToken OpenBracketToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseBracketToken,
    SyntaxToken OpenBraceToken,
    ImmutableArray<PropertyAccessorDeclarationSyntax> Accessors,
    SyntaxToken CloseBraceToken) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.IndexerDeclaration;
    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;
    public bool IsStatic => StaticKeyword is not null;
    public bool IsVirtual => VirtualKeyword is not null;
    public bool IsOverride => OverrideKeyword is not null;
    public bool IsAbstract => AbstractKeyword is not null;
    public bool IsReadonly => ReadonlyKeyword is not null;
    public PropertyAccessorDeclarationSyntax? Getter => Accessors.FirstOrDefault(accessor => accessor.IsGetter);
    public PropertyAccessorDeclarationSyntax? Setter => Accessors.FirstOrDefault(accessor => accessor.IsSetter);
}

public sealed record MethodDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? StaticKeyword,
    SyntaxToken? VirtualKeyword,
    SyntaxToken? OverrideKeyword,
    SyntaxToken? AbstractKeyword,
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax? Body,
    SyntaxToken? SemicolonToken) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.MethodDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
    public bool IsStatic => StaticKeyword is not null;
    public bool IsVirtual => VirtualKeyword is not null;
    public bool IsOverride => OverrideKeyword is not null;
    public bool IsAbstract => AbstractKeyword is not null;
    public bool IsReadonly => ReadonlyKeyword is not null;
}

public sealed record ConstructorDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    SyntaxToken? ColonToken,
    SyntaxToken? BaseKeyword,
    SyntaxToken? BaseOpenParenthesisToken,
    ImmutableArray<ExpressionSyntax> BaseArguments,
    ImmutableArray<SyntaxToken> BaseCommaTokens,
    SyntaxToken? BaseCloseParenthesisToken,
    BlockStatementSyntax Body) : StructMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ConstructorDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
}

public sealed record DestructorDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? VirtualKeyword,
    SyntaxToken TildeToken,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax Body) : StructMemberDeclarationSyntax
{
    public SyntaxToken? OverrideKeyword { get; init; }
    public bool IsOverride => OverrideKeyword is not null;
    public override SyntaxKind Kind => SyntaxKind.DestructorDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;

    public bool IsVirtual => VirtualKeyword is not null;
}

public sealed record StructDeclarationSyntax(
    SyntaxToken StructKeyword,
    SyntaxToken IdentifierToken,
    SyntaxToken? ColonToken,
    ImmutableArray<TypeSyntax> BaseTypes,
    ImmutableArray<SyntaxToken> BaseCommaTokens,
    SyntaxToken OpenBraceToken,
    ImmutableArray<StructMemberDeclarationSyntax> Members,
    SyntaxToken CloseBraceToken) : MemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.StructDeclaration;

    public ImmutableArray<FieldDeclarationSyntax> Fields =>
        Members.OfType<FieldDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<MethodDeclarationSyntax> Methods =>
        Members.OfType<MethodDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<PropertyDeclarationSyntax> Properties =>
        Members.OfType<PropertyDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<IndexerDeclarationSyntax> Indexers =>
        Members.OfType<IndexerDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<StructConstantDeclarationSyntax> Constants =>
        Members.OfType<StructConstantDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<ConstructorDeclarationSyntax> Constructors =>
        Members.OfType<ConstructorDeclarationSyntax>().ToImmutableArray();

    public DestructorDeclarationSyntax? Destructor =>
        Members.OfType<DestructorDeclarationSyntax>().FirstOrDefault();
}

public sealed record InterfaceMethodDeclarationSyntax(
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    SyntaxToken SemicolonToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.InterfaceMethodDeclaration;
    public bool IsReadonly => ReadonlyKeyword is not null;
}

public sealed record InterfacePropertyDeclarationSyntax(
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenBraceToken,
    ImmutableArray<PropertyAccessorDeclarationSyntax> Accessors,
    SyntaxToken CloseBraceToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.PropertyDeclaration;
    public PropertyAccessorDeclarationSyntax? Getter => Accessors.FirstOrDefault(accessor => accessor.IsGetter);
    public PropertyAccessorDeclarationSyntax? Setter => Accessors.FirstOrDefault(accessor => accessor.IsSetter);
    public bool IsReadonly => ReadonlyKeyword is not null;
}

public sealed record InterfaceIndexerDeclarationSyntax(
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax Type,
    SyntaxToken ThisKeyword,
    SyntaxToken OpenBracketToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseBracketToken,
    SyntaxToken OpenBraceToken,
    ImmutableArray<PropertyAccessorDeclarationSyntax> Accessors,
    SyntaxToken CloseBraceToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.IndexerDeclaration;
    public PropertyAccessorDeclarationSyntax? Getter => Accessors.FirstOrDefault(accessor => accessor.IsGetter);
    public PropertyAccessorDeclarationSyntax? Setter => Accessors.FirstOrDefault(accessor => accessor.IsSetter);
    public bool IsReadonly => ReadonlyKeyword is not null;
}

public sealed record InterfaceDeclarationSyntax(
    SyntaxToken InterfaceKeyword,
    SyntaxToken IdentifierToken,
    SyntaxToken? ColonToken,
    ImmutableArray<TypeSyntax> BaseInterfaces,
    ImmutableArray<SyntaxToken> BaseCommaTokens,
    SyntaxToken OpenBraceToken,
    ImmutableArray<InterfaceMethodDeclarationSyntax> Methods,
    ImmutableArray<InterfacePropertyDeclarationSyntax> Properties,
    ImmutableArray<InterfaceIndexerDeclarationSyntax> Indexers,
    SyntaxToken CloseBraceToken) : MemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.InterfaceDeclaration;
}

public sealed record FunctionDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? AbiModifierToken,
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax? Body,
    SyntaxToken? SemicolonToken) : MemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.FunctionDeclaration;

    public SyntaxToken? ReadonlyKeyword { get; init; }

    public bool IsReadonly => ReadonlyKeyword is not null;

    public bool IsExtern => AbiModifierToken?.Kind == SyntaxKind.ExternKeyword;

    public bool IsExport => AbiModifierToken?.Kind == SyntaxKind.ExportKeyword;

    public bool IsPublic => IsExport || AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
}
