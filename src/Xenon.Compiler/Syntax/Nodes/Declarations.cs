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

public sealed record ParameterSyntax(
    TypeSyntax Type,
    SyntaxToken IdentifierToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.Parameter;
}

public sealed record GenericParameterSyntax(SyntaxToken IdentifierToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.GenericParameter;
}

public sealed record GenericParameterListSyntax(
    SyntaxToken LessToken,
    ImmutableArray<GenericParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken GreaterToken) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.GenericParameterList;
}

public sealed record GenericConstraintSyntax(TypeSyntax Type) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.GenericConstraint;
}

public sealed record WhereClauseSyntax(
    SyntaxToken WhereKeyword,
    SyntaxToken TypeParameterToken,
    SyntaxToken ColonToken,
    ImmutableArray<GenericConstraintSyntax> Constraints,
    ImmutableArray<SyntaxToken> CommaTokens) : SyntaxNode
{
    public override SyntaxKind Kind => SyntaxKind.WhereClause;
}

public sealed record EnumDeclarationSyntax(
    SyntaxToken EnumKeyword,
    SyntaxToken IdentifierToken,
    TypeSyntax? UnderlyingType,
    ImmutableArray<EnumMemberDeclarationSyntax> Members) : TypeDeclarationSyntax(IdentifierToken)
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
    SyntaxToken? ThreadLocalKeyword,
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken? EqualsToken,
    ExpressionSyntax? Initializer,
    SyntaxToken SemicolonToken) : TypeMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.FieldDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
    public bool IsStatic => StaticKeyword is not null;
    public bool IsThreadLocal => ThreadLocalKeyword is not null;
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

public sealed record TypeConstantDeclarationSyntax(
    SyntaxToken ConstKeyword,
    TypeSyntax Type,
    SyntaxToken IdentifierToken,
    SyntaxToken EqualsToken,
    ExpressionSyntax Initializer,
    SyntaxToken SemicolonToken) : TypeMemberDeclarationSyntax
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
    SyntaxToken CloseBraceToken) : TypeMemberDeclarationSyntax
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
    SyntaxToken CloseBraceToken) : TypeMemberDeclarationSyntax
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
    SyntaxToken? SemicolonToken) : TypeMemberDeclarationSyntax
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
    BlockStatementSyntax Body) : TypeMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.ConstructorDeclaration;

    public bool IsPublic => AccessModifierToken?.Kind == SyntaxKind.PublicKeyword;

    public bool IsPrivate => !IsPublic;
    public bool HasThisInitializer => BaseKeyword?.Kind == SyntaxKind.ThisKeyword;
}

public sealed record DestructorDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? VirtualKeyword,
    SyntaxToken TildeToken,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax Body) : TypeMemberDeclarationSyntax
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
    GenericParameterListSyntax? TypeParameters,
    SyntaxToken? ColonToken,
    ImmutableArray<TypeSyntax> BaseTypes,
    ImmutableArray<SyntaxToken> BaseCommaTokens,
    ImmutableArray<WhereClauseSyntax> WhereClauses,
    SyntaxToken OpenBraceToken,
    ImmutableArray<TypeMemberDeclarationSyntax> Members,
    SyntaxToken CloseBraceToken) : TypeDeclarationSyntax(IdentifierToken)
{
    public SyntaxToken? AbstractKeyword { get; init; }
    public bool IsAbstract => AbstractKeyword is not null;
    public override SyntaxKind Kind => SyntaxKind.StructDeclaration;

    public ImmutableArray<FieldDeclarationSyntax> Fields =>
        Members.OfType<FieldDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<MethodDeclarationSyntax> Methods =>
        Members.OfType<MethodDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<PropertyDeclarationSyntax> Properties =>
        Members.OfType<PropertyDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<IndexerDeclarationSyntax> Indexers =>
        Members.OfType<IndexerDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<TypeConstantDeclarationSyntax> Constants =>
        Members.OfType<TypeConstantDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<ConstructorDeclarationSyntax> Constructors =>
        Members.OfType<ConstructorDeclarationSyntax>().ToImmutableArray();

    public DestructorDeclarationSyntax? Destructor =>
        Members.OfType<DestructorDeclarationSyntax>().FirstOrDefault();
}

public sealed record TemplateConstructorDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    BlockStatementSyntax? Body,
    SyntaxToken? SemicolonToken) : TypeMemberDeclarationSyntax
{
    public override SyntaxKind Kind => SyntaxKind.TemplateConstructorDeclaration;
    public bool IsPublic => AccessModifierToken?.Kind != SyntaxKind.PrivateKeyword;
}

public sealed record TemplateDeclarationSyntax(
    SyntaxToken TemplateKeyword,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenBraceToken,
    ImmutableArray<TypeMemberDeclarationSyntax> Members,
    SyntaxToken CloseBraceToken) : TypeDeclarationSyntax(IdentifierToken)
{
    public override SyntaxKind Kind => SyntaxKind.TemplateDeclaration;

    public ImmutableArray<MethodDeclarationSyntax> Methods =>
        Members.OfType<MethodDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<PropertyDeclarationSyntax> Properties =>
        Members.OfType<PropertyDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<IndexerDeclarationSyntax> Indexers =>
        Members.OfType<IndexerDeclarationSyntax>().ToImmutableArray();

    public ImmutableArray<TemplateConstructorDeclarationSyntax> Constructors =>
        Members.OfType<TemplateConstructorDeclarationSyntax>().ToImmutableArray();
}

public sealed record InterfaceMethodDeclarationSyntax(
    SyntaxToken? ReadonlyKeyword,
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    SyntaxToken SemicolonToken) : TypeMemberDeclarationSyntax
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
    SyntaxToken CloseBraceToken) : TypeMemberDeclarationSyntax
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
    SyntaxToken CloseBraceToken) : TypeMemberDeclarationSyntax
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
    SyntaxToken CloseBraceToken) : TypeDeclarationSyntax(IdentifierToken)
{
    public override SyntaxKind Kind => SyntaxKind.InterfaceDeclaration;
}

public sealed record FunctionDeclarationSyntax(
    SyntaxToken? AccessModifierToken,
    SyntaxToken? AbiModifierToken,
    TypeSyntax ReturnType,
    SyntaxToken IdentifierToken,
    GenericParameterListSyntax? TypeParameters,
    SyntaxToken OpenParenthesisToken,
    ImmutableArray<ParameterSyntax> Parameters,
    ImmutableArray<SyntaxToken> CommaTokens,
    SyntaxToken CloseParenthesisToken,
    ImmutableArray<WhereClauseSyntax> WhereClauses,
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
