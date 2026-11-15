using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Mir.Analysis;

// Reuse the language's typed constant evaluator. Only single-definition values
// whose address never escapes are reconstructed; this is not symbolic algebra.
internal sealed class MirPointerConstants
{
    private readonly Dictionary<MirLocalId, MirRValue> _definitions;
    private readonly Dictionary<MirLocalId, BoundExpression?> _cache = [];
    public MirPointerConstants(MirFunction function)
    {
        var assignments = function.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>().ToArray();
        var addressed = assignments.Select(assign => assign.Value).OfType<MirBorrow>().Select(borrow => borrow.Place.Local).ToHashSet();
        _definitions = assignments.Where(assign => assign.Destination.Projections.IsEmpty && !addressed.Contains(assign.Destination.Local))
            .GroupBy(assign => assign.Destination.Local).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Value);
    }
    public object? Value(MirOperand operand) => Expression(operand, []) is { } expression &&
        new ConstantEvaluationContext(null).TryFold(expression, out var value) ? value : null;

    internal static object? Binary(MirBinary binary, object left, object right) => Operator(binary.Operator) is { } op &&
        new ConstantEvaluationContext(null).TryFold(new BoundBinaryExpression(new BoundLiteralExpression(left, binary.Left.Type), op,
            new BoundLiteralExpression(right, binary.Right.Type), binary.Type), out var value) ? value : null;

    private BoundExpression? Expression(MirOperand operand, HashSet<MirLocalId> seen)
    {
        if (operand is MirConstant constant) return new BoundLiteralExpression(constant.Value, constant.Type);
        if (operand is not MirCopy { Place.Projections.IsEmpty: true } copy) return null;
        if (_cache.TryGetValue(copy.Place.Local, out var cached)) return cached;
        if (!seen.Add(copy.Place.Local) || !_definitions.TryGetValue(copy.Place.Local, out var value)) return null;
        var result = value switch
        {
            MirUse use => Expression(use.Operand, seen),
            MirCast cast when Expression(cast.Operand, seen) is { } source => new BoundCastExpression(source, cast.Type),
            MirUnary unary when Expression(unary.Operand, seen) is { } source => new BoundUnaryExpression(unary.Operator switch
            { MirUnaryOperator.Negate => SyntaxKind.MinusToken, MirUnaryOperator.Not => SyntaxKind.BangToken, _ => SyntaxKind.TildeToken }, source, unary.Type),
            MirBinary binary when Operator(binary.Operator) is { } op && Expression(binary.Left, seen) is { } left &&
                Expression(binary.Right, seen) is { } right => new BoundBinaryExpression(left, op, right, binary.Type),
            _ => null,
        };
        seen.Remove(copy.Place.Local);
        return _cache[copy.Place.Local] = result;
    }
    private static SyntaxKind? Operator(MirBinaryOperator op) => op switch
    {
        MirBinaryOperator.Add => SyntaxKind.PlusToken, MirBinaryOperator.Subtract => SyntaxKind.MinusToken,
        MirBinaryOperator.Multiply => SyntaxKind.StarToken, MirBinaryOperator.Divide => SyntaxKind.SlashToken,
        MirBinaryOperator.Remainder => SyntaxKind.PercentToken, MirBinaryOperator.BitwiseAnd => SyntaxKind.AmpersandToken,
        MirBinaryOperator.BitwiseOr => SyntaxKind.PipeToken, MirBinaryOperator.BitwiseXor => SyntaxKind.CaretToken,
        MirBinaryOperator.Equal => SyntaxKind.EqualsEqualsToken, MirBinaryOperator.NotEqual => SyntaxKind.BangEqualsToken,
        MirBinaryOperator.Less => SyntaxKind.LessToken, MirBinaryOperator.Greater => SyntaxKind.GreaterToken,
        MirBinaryOperator.LessOrEqual => SyntaxKind.LessOrEqualsToken, MirBinaryOperator.GreaterOrEqual => SyntaxKind.GreaterOrEqualsToken,
        _ => null,
    };
}
