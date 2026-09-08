using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public enum OperatorKind
{
    Invalid,
    UnaryPlus, UnaryNegation, LogicalNot, BitwiseNot,
    Add, Subtract, Multiply, Divide, Modulo,
    BitwiseAnd, BitwiseOr, BitwiseXor, ShiftLeft, ShiftRight,
    Equal, NotEqual, Less, Greater, LessEqual, GreaterEqual,
    ImplicitConversion, ExplicitConversion,
}

public static class OperatorFacts
{
    public static bool IsConversion(OperatorKind? kind) =>
        kind is OperatorKind.ImplicitConversion or OperatorKind.ExplicitConversion;

    public static bool IsAllowed(OperatorKind kind, int arity) => kind != OperatorKind.Invalid &&
        arity == (kind is OperatorKind.UnaryPlus or OperatorKind.UnaryNegation or
            OperatorKind.LogicalNot or OperatorKind.BitwiseNot || IsConversion(kind) ? 1 : 2);

    public static OperatorKind FromSource(string spelling, int arity) => (spelling, arity) switch
    {
        ("+", 1) => OperatorKind.UnaryPlus, ("-", 1) => OperatorKind.UnaryNegation,
        ("!", 1) => OperatorKind.LogicalNot, ("~", 1) => OperatorKind.BitwiseNot,
        ("+", 2) => OperatorKind.Add, ("-", 2) => OperatorKind.Subtract,
        ("*", 2) => OperatorKind.Multiply, ("/", 2) => OperatorKind.Divide, ("%", 2) => OperatorKind.Modulo,
        ("&", 2) => OperatorKind.BitwiseAnd, ("|", 2) => OperatorKind.BitwiseOr, ("^", 2) => OperatorKind.BitwiseXor,
        ("<<", 2) => OperatorKind.ShiftLeft, (">>", 2) => OperatorKind.ShiftRight,
        ("==", 2) => OperatorKind.Equal, ("!=", 2) => OperatorKind.NotEqual,
        ("<", 2) => OperatorKind.Less, (">", 2) => OperatorKind.Greater,
        ("<=", 2) => OperatorKind.LessEqual, (">=", 2) => OperatorKind.GreaterEqual,
        ("implicit", _) => OperatorKind.ImplicitConversion, ("explicit", _) => OperatorKind.ExplicitConversion,
        _ => OperatorKind.Invalid,
    };

    public static OperatorKind FromSyntax(SyntaxKind kind, int arity) => FromSource(kind switch
    {
        SyntaxKind.PlusToken => "+", SyntaxKind.MinusToken => "-",
        SyntaxKind.BangToken => "!", SyntaxKind.TildeToken => "~",
        SyntaxKind.StarToken => "*", SyntaxKind.SlashToken => "/", SyntaxKind.PercentToken => "%",
        SyntaxKind.AmpersandToken => "&", SyntaxKind.PipeToken => "|", SyntaxKind.CaretToken => "^",
        SyntaxKind.LessLessToken => "<<", SyntaxKind.GreaterGreaterToken => ">>",
        SyntaxKind.EqualsEqualsToken => "==", SyntaxKind.BangEqualsToken => "!=",
        SyntaxKind.LessToken => "<", SyntaxKind.GreaterToken => ">",
        SyntaxKind.LessOrEqualsToken => "<=", SyntaxKind.GreaterOrEqualsToken => ">=",
        _ => "",
    }, arity);

    public static string GetSpelling(OperatorKind kind) => kind switch
    {
        OperatorKind.UnaryPlus or OperatorKind.Add => "+",
        OperatorKind.UnaryNegation or OperatorKind.Subtract => "-",
        OperatorKind.LogicalNot => "!", OperatorKind.BitwiseNot => "~",
        OperatorKind.Multiply => "*", OperatorKind.Divide => "/", OperatorKind.Modulo => "%",
        OperatorKind.BitwiseAnd => "&", OperatorKind.BitwiseOr => "|", OperatorKind.BitwiseXor => "^",
        OperatorKind.ShiftLeft => "<<", OperatorKind.ShiftRight => ">>",
        OperatorKind.Equal => "==", OperatorKind.NotEqual => "!=",
        OperatorKind.Less => "<", OperatorKind.Greater => ">",
        OperatorKind.LessEqual => "<=", OperatorKind.GreaterEqual => ">=",
        OperatorKind.ImplicitConversion => "implicit", OperatorKind.ExplicitConversion => "explicit",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string GetNativeName(OperatorKind kind) => kind switch
    {
        OperatorKind.UnaryPlus => "op_unary_plus", OperatorKind.UnaryNegation => "op_unary_minus",
        OperatorKind.LogicalNot => "op_logical_not", OperatorKind.BitwiseNot => "op_bitwise_not",
        OperatorKind.Add => "op_add", OperatorKind.Subtract => "op_subtract",
        OperatorKind.Multiply => "op_multiply", OperatorKind.Divide => "op_divide", OperatorKind.Modulo => "op_modulo",
        OperatorKind.BitwiseAnd => "op_bitwise_and", OperatorKind.BitwiseOr => "op_bitwise_or", OperatorKind.BitwiseXor => "op_bitwise_xor",
        OperatorKind.ShiftLeft => "op_shift_left", OperatorKind.ShiftRight => "op_shift_right",
        OperatorKind.Equal => "op_equal", OperatorKind.NotEqual => "op_not_equal",
        OperatorKind.Less => "op_less", OperatorKind.Greater => "op_greater",
        OperatorKind.LessEqual => "op_less_equal", OperatorKind.GreaterEqual => "op_greater_equal",
        OperatorKind.ImplicitConversion => "op_implicit_conversion", OperatorKind.ExplicitConversion => "op_explicit_conversion",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static TypeSymbol ValueType(TypeSymbol type) =>
        type is ReferenceTypeSymbol reference ? reference.ElementType : type;

    internal static bool HaveSameConversionPair(FunctionSymbol left, FunctionSymbol right)
    {
        // Compare open declarations modulo parameter renaming, preserving nominal type identity.
        // Concrete specializations are compared by their definition and actual arguments.
        var parameters = new Dictionary<GenericParameterSymbol, GenericParameterSymbol>();
        bool Same(TypeSymbol a, TypeSymbol b)
        {
            if (a is GenericParameterSymbol ga && b is GenericParameterSymbol gb)
            {
                if (parameters.TryGetValue(ga, out GenericParameterSymbol? mapped)) return ReferenceEquals(mapped, gb);
                if (parameters.ContainsValue(gb)) return false;
                parameters.Add(ga, gb);
                return true;
            }
            if (a is StructTypeSymbol { GenericDefinition: { } da } sa &&
                b is StructTypeSymbol { GenericDefinition: { } db } sb)
                return ReferenceEquals(da, db) && sa.TypeArguments.Length == sb.TypeArguments.Length &&
                    sa.TypeArguments.Zip(sb.TypeArguments).All(pair => Same(pair.First, pair.Second));
            return (a, b) switch
            {
                (PointerTypeSymbol x, PointerTypeSymbol y) => x.IsReadonly == y.IsReadonly && Same(x.ElementType, y.ElementType),
                (ReferenceTypeSymbol x, ReferenceTypeSymbol y) => x.IsReadonly == y.IsReadonly && Same(x.ElementType, y.ElementType),
                (ArrayTypeSymbol x, ArrayTypeSymbol y) => x.Rank == y.Rank && Same(x.ElementType, y.ElementType),
                (AtomicTypeSymbol x, AtomicTypeSymbol y) => Same(x.ElementType, y.ElementType),
                (OwnershipTypeSymbol x, OwnershipTypeSymbol y) => x.GetType() == y.GetType() && Same(x.ElementType, y.ElementType),
                (LifetimeModifierTypeSymbol x, LifetimeModifierTypeSymbol y) => x.GetType() == y.GetType() && Same(x.ElementType, y.ElementType),
                (FunctionPointerTypeSymbol x, FunctionPointerTypeSymbol y) => Same(x.ReturnType, y.ReturnType) &&
                    x.ParameterTypes.Length == y.ParameterTypes.Length && x.ParameterTypes.Zip(y.ParameterTypes).All(pair => Same(pair.First, pair.Second)),
                _ => TypeIdentity.AreSame(a, b),
            };
        }
        return Same(ValueType(left.Parameters[0].Type), ValueType(right.Parameters[0].Type)) &&
            Same(left.ReturnType, right.ReturnType);
    }
}
