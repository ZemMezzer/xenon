using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

internal static class TypeResolver
{
    public static TypeSymbol ResolveReturnType(
        TypeSyntax syntax,
        FileSymbolScope scope,
        DiagnosticBag diagnostics)
    {
        // Return values have no binding to qualify. Keep readonly only where it
        // restricts pointer/reference access (including inside an array type).
        if (syntax.IsReadonly && syntax.PointerDepth == 0 && !syntax.IsReference)
        {
            diagnostics.Report(syntax.ReadonlyKeyword!.Location,
                "'readonly' cannot qualify a by-value return type; place 'readonly' before the method name to declare a readonly method");
        }

        return Resolve(syntax, scope, diagnostics);
    }

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

        foreach (int rank in syntax.ArrayRanks.IsEmpty ? [1] : syntax.ArrayRanks.Reverse())
            type = BuiltinTypes.ArrayOf(type, rank);
        return type;
    }
}
