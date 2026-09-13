namespace Xenon.Compiler;

internal enum ConditionalExpressionKind : byte { False = 1, True = 2, Symbol = 3, Not = 4, And = 5, Or = 6 }

/// <summary>Boolean expression used only while filtering source directives.</summary>
internal sealed record ConditionalExpression(ConditionalExpressionKind Kind, string? Symbol = null,
    ConditionalExpression? Left = null, ConditionalExpression? Right = null)
{
    public static ConditionalExpression False { get; } = new(ConditionalExpressionKind.False);
    public static ConditionalExpression True { get; } = new(ConditionalExpressionKind.True);
    public static ConditionalExpression Named(string name) => new(ConditionalExpressionKind.Symbol, name);
    public bool Evaluate(IReadOnlySet<string> symbols) => Kind switch
    {
        ConditionalExpressionKind.False => false,
        ConditionalExpressionKind.True => true,
        ConditionalExpressionKind.Symbol => symbols.Contains(Symbol!),
        ConditionalExpressionKind.Not => !Left!.Evaluate(symbols),
        ConditionalExpressionKind.And => Left!.Evaluate(symbols) && Right!.Evaluate(symbols),
        ConditionalExpressionKind.Or => Left!.Evaluate(symbols) || Right!.Evaluate(symbols),
        _ => throw new ArgumentException("unknown conditional expression kind"),
    };
    public IEnumerable<string> Symbols => Kind == ConditionalExpressionKind.Symbol ? [Symbol!] :
        (Left?.Symbols ?? []).Concat(Right?.Symbols ?? []);
    public static ConditionalExpression Not(ConditionalExpression value) => value.Kind switch
    {
        ConditionalExpressionKind.True => False,
        ConditionalExpressionKind.False => True,
        ConditionalExpressionKind.Not => value.Left!,
        _ => new(ConditionalExpressionKind.Not, Left: value),
    };
    public static ConditionalExpression And(ConditionalExpression left, ConditionalExpression right) =>
        left == False || right == False ? False : left == True ? right : right == True || left == right ? left :
        new(ConditionalExpressionKind.And, Left: left, Right: right);
    public static ConditionalExpression Or(ConditionalExpression left, ConditionalExpression right) =>
        left == True || right == True ? True : left == False ? right : right == False || left == right ? left :
        new(ConditionalExpressionKind.Or, Left: left, Right: right);
    internal static ConditionalExpression All(IReadOnlyList<ConditionalExpression> values) => Combine(values, false);
    internal static ConditionalExpression Any(IReadOnlyList<ConditionalExpression> values) => Combine(values, true);
    private static ConditionalExpression Combine(IReadOnlyList<ConditionalExpression> values, bool disjunction)
    {
        ConditionalExpression Combine(int start, int count) => count switch
        {
            0 => disjunction ? False : True,
            1 => values[start],
            _ => disjunction
                ? Or(Combine(start, count / 2), Combine(start + count / 2, count - count / 2))
                : And(Combine(start, count / 2), Combine(start + count / 2, count - count / 2)),
        };
        return Combine(0, values.Count);
    }
}
