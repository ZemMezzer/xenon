namespace Xenon.Compiler.Semantics.Symbols;

public enum Accessibility
{
    Private,
    Internal,
    Protected,
    ProtectedInternal,
    Public,
}

public static class AccessibilityFacts
{
    public static Accessibility FromSyntax(
        Syntax.SyntaxToken? primary,
        Syntax.SyntaxToken? secondary,
        Accessibility defaultValue)
    {
        if (primary is null) return defaultValue;
        if (secondary is not null &&
            (primary.Kind is Syntax.SyntaxKind.ProtectedKeyword && secondary.Kind is Syntax.SyntaxKind.InternalKeyword ||
             primary.Kind is Syntax.SyntaxKind.InternalKeyword && secondary.Kind is Syntax.SyntaxKind.ProtectedKeyword))
            return Accessibility.ProtectedInternal;
        return primary.Kind switch
        {
            Syntax.SyntaxKind.PublicKeyword => Accessibility.Public,
            Syntax.SyntaxKind.InternalKeyword => Accessibility.Internal,
            Syntax.SyntaxKind.ProtectedKeyword => Accessibility.Protected,
            _ => Accessibility.Private,
        };
    }

    public static bool IsExternallyInheritable(Accessibility accessibility) => accessibility is
        Accessibility.Public or Accessibility.Protected or Accessibility.ProtectedInternal;

    public static bool DoesNotReduce(Accessibility candidate, Accessibility inherited) => inherited switch
    {
        Accessibility.Public => candidate == Accessibility.Public,
        Accessibility.ProtectedInternal => candidate is Accessibility.ProtectedInternal or Accessibility.Public,
        Accessibility.Protected => candidate is Accessibility.Protected or Accessibility.ProtectedInternal or
            Accessibility.Public,
        Accessibility.Internal => candidate is Accessibility.Internal or Accessibility.ProtectedInternal or
            Accessibility.Public,
        Accessibility.Private => true,
        _ => false,
    };

    public static string ToDisplayText(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Private => "private",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedInternal => "protected internal",
        Accessibility.Public => "public",
        _ => throw new ArgumentOutOfRangeException(nameof(accessibility)),
    };
}

/// <summary>Single semantic accessibility policy shared by binding and tooling.</summary>
public static class AccessibilityRules
{
    public static bool IsAccessible(Symbol symbol, Symbol accessingSymbol,
        DeclaredTypeSymbol? receiverType = null)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentNullException.ThrowIfNull(accessingSymbol);
        Accessibility accessibility = GetAccessibility(symbol);
        if (accessibility == Accessibility.Public) return true;

        DeclaredTypeSymbol? declaringType = symbol as DeclaredTypeSymbol ?? symbol.GetContainingSymbol<DeclaredTypeSymbol>();
        DeclaredTypeSymbol? accessingType = accessingSymbol as DeclaredTypeSymbol ?? accessingSymbol.GetContainingSymbol<DeclaredTypeSymbol>();
        bool sameType = declaringType is not null && accessingType is not null &&
            TypeIdentity.AreSame(declaringType, accessingType);
        bool privateAccess = declaringType is null
            ? ReferenceEquals(ContainingNamespace(symbol), ContainingNamespace(accessingSymbol))
            : sameType;
        bool internalAccess = HaveSameInternalBoundary(symbol, accessingSymbol);
        bool protectedAccess = declaringType is not null && accessingType is StructTypeSymbol accessingStruct &&
            declaringType is StructTypeSymbol declaringStruct && IsSameOrDerivedFrom(accessingStruct, declaringStruct) &&
            (receiverType is null || receiverType is StructTypeSymbol receiverStruct &&
                IsSameOrDerivedFrom(receiverStruct, accessingStruct));

        return accessibility switch
        {
            Accessibility.Private => privateAccess,
            Accessibility.Internal => internalAccess,
            Accessibility.Protected => protectedAccess,
            Accessibility.ProtectedInternal => internalAccess || protectedAccess,
            _ => false,
        };
    }

    public static Accessibility GetAccessibility(Symbol symbol) => symbol switch
    {
        DeclaredTypeSymbol type => type.Accessibility,
        TemplateSymbol template => template.Accessibility,
        FunctionSymbol function => function.Accessibility,
        FieldSymbol field => field.Accessibility,
        PropertySymbol property => property.Accessibility,
        IndexerSymbol indexer => indexer.Accessibility,
        TemplateMemberRequirementSymbol requirement => requirement.Accessibility,
        ConstantSymbol constant => constant.Accessibility,
        _ => Accessibility.Public,
    };

    public static bool IsSameOrDerivedFrom(StructTypeSymbol candidate, StructTypeSymbol expected)
    {
        for (StructTypeSymbol? current = candidate; current is not null; current = current.BaseType)
            if (TypeIdentity.AreSame(current, expected)) return true;
        return false;
    }

    private static bool HaveSameInternalBoundary(Symbol left, Symbol right)
    {
        Symbol leftOrigin = SemanticOrigin(left);
        Symbol rightOrigin = SemanticOrigin(right);
        string? leftLibrary = leftOrigin.Origin.LibraryContentIdentity;
        string? rightLibrary = rightOrigin.Origin.LibraryContentIdentity;
        if (leftLibrary is not null || rightLibrary is not null)
            return leftLibrary is not null && string.Equals(leftLibrary, rightLibrary, StringComparison.Ordinal);
        return ReferenceEquals(Root(leftOrigin), Root(rightOrigin));
    }

    private static Symbol SemanticOrigin(Symbol symbol)
    {
        if (symbol.Origin.LibraryContentIdentity is not null) return symbol;
        return symbol switch
        {
            FunctionSymbol { GenericDefinition: not null } function => SemanticOrigin(function.GenericDefinition),
            StructTypeSymbol { GenericDefinition: not null } type => SemanticOrigin(type.GenericDefinition),
            FieldSymbol { GenericDefinition: not null } field => SemanticOrigin(field.GenericDefinition),
            PropertySymbol { GenericDefinition: not null } property => SemanticOrigin(property.GenericDefinition),
            IndexerSymbol { GenericDefinition: not null } indexer => SemanticOrigin(indexer.GenericDefinition),
            ConstantSymbol { GenericDefinition: not null } constant => SemanticOrigin(constant.GenericDefinition),
            { Origin.Kind: SymbolOriginKind.CompilerGenerated, ContainingSymbol: not null } generated =>
                SemanticOrigin(generated.ContainingSymbol),
            _ => symbol,
        };
    }

    private static Symbol Root(Symbol symbol)
    {
        while (symbol.ContainingSymbol is { } containing) symbol = containing;
        return symbol;
    }

    private static NamespaceSymbol? ContainingNamespace(Symbol symbol) =>
        symbol as NamespaceSymbol ?? symbol.GetContainingSymbol<NamespaceSymbol>();
}

public enum FunctionKind
{
    Ordinary,
    Method,
    Constructor,
    InstanceInitializer,
    ThreadLocalInitializer,
    Destructor,
    DestructorGlue,
    OwnershipDestructor,
    StorageDestructor,
}

public enum AccessorKind
{
    None,
    Getter,
    Setter,
}

public enum ArrayStorageKind
{
    Unknown,
    Heap,
    Stack,
}
