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
    public static bool DoesNotReduceOverrideAccessibility(
        FunctionSymbol inherited,
        FunctionSymbol overriding)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(overriding);
        bool sameInternalBoundary = HaveSameInternalBoundary(inherited, overriding);
        return inherited.Accessibility switch
        {
            Accessibility.Public => overriding.Accessibility == Accessibility.Public,
            Accessibility.ProtectedInternal when sameInternalBoundary =>
                overriding.Accessibility is Accessibility.ProtectedInternal or Accessibility.Public,
            Accessibility.ProtectedInternal => overriding.Accessibility is
                Accessibility.Protected or Accessibility.ProtectedInternal or Accessibility.Public,
            Accessibility.Protected => overriding.Accessibility is
                Accessibility.Protected or Accessibility.ProtectedInternal or Accessibility.Public,
            Accessibility.Internal when sameInternalBoundary => overriding.Accessibility is
                Accessibility.Internal or Accessibility.ProtectedInternal or Accessibility.Public,
            Accessibility.Internal => false,
            Accessibility.Private => true,
            _ => false,
        };
    }

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

    /// <summary>
    /// Checks every nominal type participating in a signature, including wrapped and generic
    /// forms, against the effective accessibility of the declaration which exposes it.
    /// </summary>
    public static bool IsTypeAccessibleEnough(
        TypeSymbol type,
        Symbol exposingDeclaration,
        out Symbol? lessAccessibleType)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(exposingDeclaration);
        Accessibility required = GetEffectiveAccessibility(exposingDeclaration);
        var visited = new HashSet<TypeSymbol>(ReferenceEqualityComparer.Instance);
        Symbol? failure = null;

        bool Visit(TypeSymbol candidate)
        {
            if (!visited.Add(candidate)) return true;
            switch (candidate)
            {
                case StructTypeSymbol structure:
                    Symbol definition = structure.GenericDefinition ?? structure;
                    if (!IsSymbolAccessibleEnough(definition, required, exposingDeclaration))
                    {
                        failure = definition;
                        return false;
                    }
                    foreach (TypeSymbol argument in structure.TypeArguments)
                        if (!Visit(argument)) return false;
                    return true;
                case DeclaredTypeSymbol declared:
                    if (IsSymbolAccessibleEnough(declared, required, exposingDeclaration)) return true;
                    failure = declared;
                    return false;
                case TemplateSelfTypeSymbol self:
                    if (IsSymbolAccessibleEnough(self.Template, required, exposingDeclaration)) return true;
                    failure = self.Template;
                    return false;
                case PointerTypeSymbol pointer: return Visit(pointer.ElementType);
                case ReferenceTypeSymbol reference: return Visit(reference.ElementType);
                case ArrayTypeSymbol array: return Visit(array.ElementType);
                case AtomicTypeSymbol atomic: return Visit(atomic.ElementType);
                case OwnershipTypeSymbol ownership: return Visit(ownership.ElementType);
                case LifetimeModifierTypeSymbol modifier: return Visit(modifier.ElementType);
                case FunctionPointerTypeSymbol function:
                    if (!Visit(function.ReturnType)) return false;
                    foreach (TypeSymbol parameter in function.ParameterTypes)
                        if (!Visit(parameter)) return false;
                    return true;
                case FunctionValueTypeSymbol function:
                    if (!Visit(function.ReturnType)) return false;
                    foreach (TypeSymbol parameter in function.ParameterTypes)
                        if (!Visit(parameter)) return false;
                    return true;
                default:
                    return true;
            }
        }

        bool result = Visit(type);
        lessAccessibleType = failure;
        return result;
    }

    public static bool IsSymbolAccessibleEnough(Symbol candidate, Symbol exposingDeclaration) =>
        IsSymbolAccessibleEnough(candidate, GetEffectiveAccessibility(exposingDeclaration), exposingDeclaration);

    public static Accessibility GetEffectiveAccessibility(Symbol symbol)
    {
        Accessibility result = GetAccessibility(symbol);
        for (Symbol? containing = symbol.ContainingSymbol; containing is not null; containing = containing.ContainingSymbol)
        {
            if (containing is not (DeclaredTypeSymbol or TemplateSymbol)) continue;
            Accessibility outer = GetAccessibility(containing);
            if (result == Accessibility.Private || outer == Accessibility.Private)
                return Accessibility.Private;
            if (outer == Accessibility.Internal)
                result = Accessibility.Internal;
            else if (outer == Accessibility.Protected && result != Accessibility.Internal)
                result = Accessibility.Protected;
            else if (outer == Accessibility.ProtectedInternal && result == Accessibility.Public)
                result = Accessibility.ProtectedInternal;
        }
        return result;
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

    private static bool IsSymbolAccessibleEnough(
        Symbol candidate,
        Accessibility required,
        Symbol exposingDeclaration)
    {
        Accessibility available = GetEffectiveAccessibility(candidate);
        return available switch
        {
            Accessibility.Public => true,
            Accessibility.ProtectedInternal => required switch
            {
                Accessibility.Private => true,
                Accessibility.Internal => HaveSameInternalBoundary(candidate, exposingDeclaration),
                Accessibility.Protected => true,
                Accessibility.ProtectedInternal => HaveSameInternalBoundary(candidate, exposingDeclaration),
                _ => false,
            },
            Accessibility.Protected => required is Accessibility.Private or Accessibility.Protected,
            Accessibility.Internal => required is Accessibility.Private ||
                required == Accessibility.Internal && HaveSameInternalBoundary(candidate, exposingDeclaration),
            Accessibility.Private => required == Accessibility.Private &&
                IsAccessible(candidate, exposingDeclaration),
            _ => false,
        };
    }

    public static bool IsSameOrDerivedFrom(StructTypeSymbol candidate, StructTypeSymbol expected)
    {
        for (StructTypeSymbol? current = candidate; current is not null; current = current.BaseType)
            if (TypeIdentity.AreSame(current, expected)) return true;
        return false;
    }

    public static bool HaveSameInternalBoundary(Symbol left, Symbol right)
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
    FunctionValueDestructor,
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
