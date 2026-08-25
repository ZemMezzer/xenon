using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

internal static class TypeResolver
{
    public static TypeSymbol Resolve(
        TypeSyntax syntax,
        NamespaceSymbol containingNamespace,
        DiagnosticBag diagnostics)
    {
        TypeSymbol? type = BuiltinTypes.FromSyntaxKind(syntax.NameToken.Kind) ??
            containingNamespace.FindType(syntax.NameToken.Text);
        if (type is null)
        {
            diagnostics.Report(syntax.NameToken.Location, $"unknown type '{syntax.NameToken.Text}'");
            type = BuiltinTypes.Error;
        }

        if (syntax.IsConst && syntax.PointerDepth == 0)
        {
            diagnostics.Report(syntax.ConstKeyword!.Location, "'const' is currently supported only for pointer element types");
        }

        for (int depth = 0; depth < syntax.PointerDepth; depth++)
        {
            type = BuiltinTypes.PointerTo(type, syntax.IsConst && depth == 0);
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
