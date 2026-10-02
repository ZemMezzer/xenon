using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

internal static class NumericLiteralBinding
{
    // Shared by function bodies and declaration-level constant initializers.
    public static bool TryBindExplicit(SyntaxToken token, ConstantEvaluationContext constants,
        DiagnosticBag diagnostics, out BoundExpression expression)
    {
        PrimitiveTypeSymbol? type = token.NumericSuffix switch
        {
            NumericLiteralSuffix.Float => BuiltinTypes.Float,
            NumericLiteralSuffix.Double => BuiltinTypes.Double,
            NumericLiteralSuffix.UInt => BuiltinTypes.UInt,
            NumericLiteralSuffix.Long => BuiltinTypes.Long,
            NumericLiteralSuffix.ULong => BuiltinTypes.ULong,
            NumericLiteralSuffix.NInt => BuiltinTypes.NInt,
            NumericLiteralSuffix.NUInt => BuiltinTypes.NUInt,
            _ => null,
        };
        expression = new BoundErrorExpression();
        if (type is null) return false;

        if (type.IsInteger && token.Value is ulong integer)
        {
            if (type.BitWidth is null && constants.TargetLayout is null)
                constants.RequireTargetLayout();

            if (!SemanticAnalyzer.FitsInteger(integer, type, constants.TargetLayout))
            {
                diagnostics.ReportInvalidNumber(token.Location, token.Text, type.Name);
                return true;
            }

            expression = new BoundLiteralExpression(
                SemanticAnalyzer.IntegerValue(integer, type, constants.TargetLayout), type);
        }
        else if (token.Value is not null)
        {
            expression = new BoundLiteralExpression(token.Value, type);
        }
        return true;
    }
}
