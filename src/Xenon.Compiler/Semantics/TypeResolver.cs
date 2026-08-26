using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

internal static class TypeResolver
{
    public static TypeSymbol Resolve(
        TypeSyntax syntax,
        FileSymbolScope scope,
        DiagnosticBag diagnostics)
    {
        TypeSymbol? type;
        if (!syntax.IsQualifiedName)
        {
            type = BuiltinTypes.FromSyntaxKind(syntax.NameToken.Kind) ??
                scope.ResolveType(syntax.NameToken.Text, syntax.NameToken.Location, diagnostics);
        }
        else
        {
            type = scope.ResolveQualifiedType(syntax.NameParts.Select(part => part.Text).ToArray());
        }

        if (type is null)
        {
            diagnostics.Report(syntax.NameToken.Location, $"unknown type '{syntax.Name}'");
            type = BuiltinTypes.Error;
        }

        if (syntax.IsConst && syntax.PointerDepth > 0)
        {
            diagnostics.Report(syntax.ConstKeyword!.Location, "'const T*' is no longer supported; use 'readonly T*'");
        }
        else if (syntax.IsConst && syntax.IsReference)
        {
            diagnostics.Report(syntax.ConstKeyword!.Location, "'const T&' is no longer supported; use 'readonly T&'");
        }
        else if (syntax.IsConst)
        {
            diagnostics.Report(syntax.ConstKeyword!.Location, "'const' cannot qualify a runtime type; use a const declaration for compile-time values");
        }

        for (int depth = 0; depth < syntax.PointerDepth; depth++)
        {
            type = BuiltinTypes.PointerTo(type, syntax.IsReadonly && depth == 0);
        }

        if (syntax.IsReference)
        {
            if (ReferenceEquals(type, BuiltinTypes.Void))
            {
                diagnostics.Report(syntax.NameToken.Location, "reference element type cannot be 'void'");
                type = BuiltinTypes.Error;
            }
            else
            {
                type = BuiltinTypes.ReferenceTo(type, syntax.IsReadonly);
            }
        }

        if (!syntax.IsArray)
        {
            return type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Void))
        {
            diagnostics.Report(syntax.NameToken.Location, "array element type cannot be 'void'");
            return BuiltinTypes.Error;
        }

        return BuiltinTypes.ArrayOf(type);
    }
}
