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
                if (named.TypeArguments is { } arguments)
                {
                    diagnostics.Report(arguments.LessToken.Location, "generic type arguments are not supported by semantic analysis yet",
                        DiagnosticIds.GenericTypeArgumentsNotSupported);
                    return BuiltinTypes.Error;
                }
                TypeSymbol? type = named.NameParts.Length == 1
                    ? BuiltinTypes.FromSyntaxKind(named.NameToken.Kind) ?? scope.ResolveType(named.Name, named.NameToken.Location, diagnostics)
                    : scope.ResolveQualifiedType(named.NameParts.Select(part => part.Text).ToArray());
                if (type is not null) return type;
                diagnostics.Report(named.NameToken.Location, $"unknown type '{named.Name}'", DiagnosticIds.UnknownType);
                return BuiltinTypes.Error;
            }
            default:
                throw new InvalidOperationException($"Unsupported type syntax '{syntax.Kind}'");
        }
    }
}
