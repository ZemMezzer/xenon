using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Libraries;

/// <summary>Format-owned identifiers; neither CLR enum ordinals nor native names are serialized.</summary>
internal static class XelibOperatorKinds
{
    private static readonly IReadOnlyDictionary<string, OperatorKind> Kinds = new Dictionary<string, OperatorKind>(StringComparer.Ordinal)
    {
        ["unary_plus"] = OperatorKind.UnaryPlus, ["unary_negation"] = OperatorKind.UnaryNegation,
        ["logical_not"] = OperatorKind.LogicalNot, ["bitwise_not"] = OperatorKind.BitwiseNot,
        ["add"] = OperatorKind.Add, ["subtract"] = OperatorKind.Subtract,
        ["multiply"] = OperatorKind.Multiply, ["divide"] = OperatorKind.Divide, ["modulo"] = OperatorKind.Modulo,
        ["bitwise_and"] = OperatorKind.BitwiseAnd, ["bitwise_or"] = OperatorKind.BitwiseOr, ["bitwise_xor"] = OperatorKind.BitwiseXor,
        ["shift_left"] = OperatorKind.ShiftLeft, ["shift_right"] = OperatorKind.ShiftRight,
        ["equal"] = OperatorKind.Equal, ["not_equal"] = OperatorKind.NotEqual,
        ["less"] = OperatorKind.Less, ["greater"] = OperatorKind.Greater,
        ["less_equal"] = OperatorKind.LessEqual, ["greater_equal"] = OperatorKind.GreaterEqual,
        ["implicit_conversion"] = OperatorKind.ImplicitConversion, ["explicit_conversion"] = OperatorKind.ExplicitConversion,
    };

    public static string? Encode(OperatorKind? kind) => kind is null ? null :
        Kinds.First(pair => pair.Value == kind).Key;

    public static OperatorKind? Decode(string? id, int arity)
    {
        if (id is null) return null;
        // Accept the spellings emitted by the initial version-1 operator implementation.
        OperatorKind kind = Kinds.TryGetValue(id, out OperatorKind mapped) ? mapped : OperatorFacts.FromSource(id, arity);
        if (!OperatorFacts.IsAllowed(kind, arity))
            throw new XelibFormatException(XelibErrorCode.InvalidRecord, $"invalid operator identifier or arity: '{id}'");
        return kind;
    }
}
