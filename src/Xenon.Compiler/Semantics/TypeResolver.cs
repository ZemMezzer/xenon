using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

internal static class TypeResolver
{
    public static TypeSymbol ResolveReturnType(TypeSyntax syntax, FileSymbolScope scope, DiagnosticBag diagnostics)
    {
        if (syntax.GetQualifier(SyntaxKind.ReadonlyKeyword) is { } qualifier &&
            !syntax.Contains<PointerTypeSyntax>() && !syntax.Contains<ReferenceTypeSyntax>())
            diagnostics.Report(qualifier.Location,
                "'readonly' cannot qualify a by-value return type; place 'readonly' before the method name to declare a readonly method",
                DiagnosticIds.InvalidReadonlyReturnQualifier);
        return Resolve(syntax, scope, diagnostics);
    }

    public static TypeSymbol Resolve(TypeSyntax syntax, FileSymbolScope scope, DiagnosticBag diagnostics)
    {
        if (syntax.GetQualifier(SyntaxKind.ConstKeyword) is { } qualifier)
            diagnostics.Report(qualifier.Location, syntax.Contains<PointerTypeSyntax>()
                ? "'const T*' is no longer supported; use 'readonly T*'"
                : syntax.Contains<ReferenceTypeSyntax>()
                    ? "'const T&' is no longer supported; use 'readonly T&'"
                    : "'const' cannot qualify a runtime type; use a const declaration for compile-time values",
                DiagnosticIds.DeprecatedConstTypeQualifier);
        TypeSymbol result = ResolveCore(syntax, scope, diagnostics);
        scope.SemanticInfo?.RecordType(syntax, result);
        if (result is GenericParameterSymbol parameter)
            scope.SemanticInfo?.Symbols[syntax] = SymbolInfo.FromSymbol(parameter);
        else if (result is TemplateSelfTypeSymbol selfType)
            scope.SemanticInfo?.Symbols[syntax] = SymbolInfo.FromSymbol(selfType.Template);
        return result;
    }

    private static TypeSymbol ResolveCore(TypeSyntax syntax, FileSymbolScope scope, DiagnosticBag diagnostics, bool isReadonly = false)
    {
        switch (syntax)
        {
            case QualifiedTypeSyntax qualified:
                return ResolveCore(qualified.ElementType, scope, diagnostics, isReadonly ||
                    qualified.Position == TypeQualifierPosition.Prefix && qualified.QualifierToken.Kind == SyntaxKind.ReadonlyKeyword);
            case PointerTypeSyntax pointer:
                return scope.TypeFactory.PointerTo(ResolveCore(pointer.ElementType, scope, diagnostics, isReadonly),
                    isReadonly && !pointer.ElementType.Contains<PointerTypeSyntax>());
            case ReferenceTypeSyntax reference:
            {
                TypeSymbol element = ResolveCore(reference.ElementType, scope, diagnostics, isReadonly);
                if (!TypeIdentity.AreSame(element, BuiltinTypes.Void)) return scope.TypeFactory.ReferenceTo(element, isReadonly);
                diagnostics.Report(reference.NameToken.Location, "reference element type cannot be 'void'",
                    DiagnosticIds.VoidReferenceElementType);
                return BuiltinTypes.Error;
            }
            case ArrayTypeSyntax array:
            {
                TypeSymbol element = ResolveCore(array.ElementType, scope, diagnostics, isReadonly);
                if (!TypeIdentity.AreSame(element, BuiltinTypes.Void)) return scope.TypeFactory.ArrayOf(element, array.Rank);
                diagnostics.Report(array.NameToken.Location, "array element type cannot be 'void'",
                    DiagnosticIds.VoidArrayElementType);
                return BuiltinTypes.Error;
            }
            case NamedTypeSyntax named:
            {
                if (named.NameParts.Length == 1 && named.NameToken.Kind is
                    SyntaxKind.UniqueKeyword or SyntaxKind.SharedKeyword or SyntaxKind.WeakKeyword)
                {
                    string ownershipKind = named.NameToken.Text;
                    if (named.TypeArguments is not { } ownershipArguments || ownershipArguments.Arguments.Length != 1)
                    {
                        diagnostics.Report(
                            named.TypeArguments?.LessToken.Location ?? named.NameToken.Location,
                            $"ownership type '{ownershipKind}' requires exactly one type argument",
                            DiagnosticIds.GenericArityMismatch);
                        return BuiltinTypes.Error;
                    }

                    TypeSymbol element = ResolveCore(ownershipArguments.Arguments[0], scope, diagnostics);
                    if (TypeIdentity.AreSame(element, BuiltinTypes.Void) ||
                        element is ReferenceTypeSymbol or PointerTypeSymbol or OwnershipTypeSymbol)
                    {
                        diagnostics.Report(
                            ownershipArguments.Arguments[0].NameToken.Location,
                            $"type '{element.ToDisplayString()}' cannot be owned by '{ownershipKind}'; ownership wrappers cannot directly wrap pointers, references, void, or another ownership wrapper",
                            DiagnosticIds.InvalidUniqueTypeArgument);
                        return BuiltinTypes.Error;
                    }

                    OwnershipTypeSymbol ownership = named.NameToken.Kind switch
                    {
                        SyntaxKind.UniqueKeyword => scope.TypeFactory.UniqueOf(element),
                        SyntaxKind.SharedKeyword => scope.TypeFactory.SharedOf(element),
                        SyntaxKind.WeakKeyword => scope.TypeFactory.WeakOf(element),
                        _ => throw new InvalidOperationException(),
                    };
                    scope.TypeFactory.EnsureOwnershipDestructor(ownership, scope.GlobalNamespace, named);
                    return ownership;
                }

                TypeSymbol? type = named.NameParts.Length == 1
                    ? BuiltinTypes.FromSyntaxKind(named.NameToken.Kind) ?? scope.ResolveType(named.Name, named.NameToken.Location, diagnostics)
                    : scope.ResolveQualifiedType(named.NameParts.Select(part => part.Text).ToArray());
                if (named.TypeArguments is { } arguments)
                {
                    if (type is not StructTypeSymbol structure)
                    {
                        diagnostics.Report(arguments.LessToken.Location,
                            $"type '{named.Name}' is not a generic struct",
                            DiagnosticIds.GenericTypeArgumentsNotSupported);
                        return BuiltinTypes.Error;
                    }
                    if (scope.GenericStructSpecializer is null)
                    {
                        diagnostics.Report(arguments.LessToken.Location,
                            "generic struct specialization is not available in this declaration context yet",
                            DiagnosticIds.GenericSpecializationNotImplemented);
                        return BuiltinTypes.Error;
                    }
                    ImmutableArray<TypeSymbol> typeArguments = arguments.Arguments
                        .Select(argument => ResolveCore(argument, scope, diagnostics)).ToImmutableArray();
                    return (TypeSymbol?)scope.GenericStructSpecializer.GetOrCreate(structure, typeArguments,
                        arguments.LessToken.Location) ?? BuiltinTypes.Error;
                }
                if (type is not null) return type;
                TemplateSymbol? template = named.NameParts.Length == 1
                    ? scope.ResolveTemplate(named.Name, named.NameToken.Location, diagnostics)
                    : scope.ResolveQualifiedTemplate(named.NameParts.Select(part => part.Text).ToArray());
                if (template is not null)
                {
                    diagnostics.Report(named.NameToken.Location,
                        $"template '{template.Name}' is a compile-time constraint and cannot be used as a runtime type",
                        DiagnosticIds.TemplateCannotBeUsedAsType);
                    return BuiltinTypes.Error;
                }
                diagnostics.Report(named.NameToken.Location, $"unknown type '{named.Name}'", DiagnosticIds.UnknownType);
                return BuiltinTypes.Error;
            }
            default:
                throw new InvalidOperationException($"Unsupported type syntax '{syntax.Kind}'");
        }
    }
}
