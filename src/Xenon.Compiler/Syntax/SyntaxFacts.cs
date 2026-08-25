namespace Xenon.Compiler.Syntax;

public static class SyntaxFacts
{
    private static readonly IReadOnlyDictionary<string, SyntaxKind> Keywords =
        new Dictionary<string, SyntaxKind>(StringComparer.Ordinal)
        {
            ["namespace"] = SyntaxKind.NamespaceKeyword,
            ["void"] = SyntaxKind.VoidKeyword,
            ["bool"] = SyntaxKind.BoolKeyword,
            ["byte"] = SyntaxKind.ByteKeyword,
            ["sbyte"] = SyntaxKind.SByteKeyword,
            ["short"] = SyntaxKind.ShortKeyword,
            ["ushort"] = SyntaxKind.UShortKeyword,
            ["int"] = SyntaxKind.IntKeyword,
            ["uint"] = SyntaxKind.UIntKeyword,
            ["long"] = SyntaxKind.LongKeyword,
            ["ulong"] = SyntaxKind.ULongKeyword,
            ["float"] = SyntaxKind.FloatKeyword,
            ["double"] = SyntaxKind.DoubleKeyword,
            ["nint"] = SyntaxKind.NIntKeyword,
            ["nuint"] = SyntaxKind.NUIntKeyword,
            ["clong"] = SyntaxKind.CLongKeyword,
            ["culong"] = SyntaxKind.CULongKeyword,
            ["const"] = SyntaxKind.ConstKeyword,
            ["struct"] = SyntaxKind.StructKeyword,
            ["enum"] = SyntaxKind.EnumKeyword,
            ["if"] = SyntaxKind.IfKeyword,
            ["else"] = SyntaxKind.ElseKeyword,
            ["while"] = SyntaxKind.WhileKeyword,
            ["for"] = SyntaxKind.ForKeyword,
            ["break"] = SyntaxKind.BreakKeyword,
            ["continue"] = SyntaxKind.ContinueKeyword,
            ["return"] = SyntaxKind.ReturnKeyword,
            ["extern"] = SyntaxKind.ExternKeyword,
            ["export"] = SyntaxKind.ExportKeyword,
            ["true"] = SyntaxKind.TrueKeyword,
            ["false"] = SyntaxKind.FalseKeyword,
            ["null"] = SyntaxKind.NullKeyword,
            ["cast"] = SyntaxKind.CastKeyword,
            ["bitcast"] = SyntaxKind.BitCastKeyword,
            ["new"] = SyntaxKind.NewKeyword,
            ["free"] = SyntaxKind.FreeKeyword,
        };

    public static SyntaxKind GetKeywordKind(string text) =>
        Keywords.TryGetValue(text, out SyntaxKind kind) ? kind : SyntaxKind.IdentifierToken;

    public static bool IsTypeName(SyntaxKind kind) => kind is
        SyntaxKind.VoidKeyword or
        SyntaxKind.BoolKeyword or
        SyntaxKind.ByteKeyword or
        SyntaxKind.SByteKeyword or
        SyntaxKind.ShortKeyword or
        SyntaxKind.UShortKeyword or
        SyntaxKind.IntKeyword or
        SyntaxKind.UIntKeyword or
        SyntaxKind.LongKeyword or
        SyntaxKind.ULongKeyword or
        SyntaxKind.FloatKeyword or
        SyntaxKind.DoubleKeyword or
        SyntaxKind.NIntKeyword or
        SyntaxKind.NUIntKeyword or
        SyntaxKind.CLongKeyword or
        SyntaxKind.CULongKeyword or
        SyntaxKind.IdentifierToken;

    public static bool IsAssignmentOperator(SyntaxKind kind) => kind is
        SyntaxKind.EqualsToken or
        SyntaxKind.PlusEqualsToken or
        SyntaxKind.MinusEqualsToken or
        SyntaxKind.StarEqualsToken or
        SyntaxKind.SlashEqualsToken or
        SyntaxKind.PercentEqualsToken or
        SyntaxKind.AmpersandEqualsToken or
        SyntaxKind.PipeEqualsToken or
        SyntaxKind.CaretEqualsToken or
        SyntaxKind.LessLessEqualsToken or
        SyntaxKind.GreaterGreaterEqualsToken;

    public static int GetUnaryOperatorPrecedence(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken or
        SyntaxKind.MinusToken or
        SyntaxKind.BangToken or
        SyntaxKind.TildeToken or
        SyntaxKind.StarToken or
        SyntaxKind.AmpersandToken or
        SyntaxKind.PlusPlusToken or
        SyntaxKind.MinusMinusToken => 12,
        _ => 0,
    };

    public static int GetBinaryOperatorPrecedence(SyntaxKind kind) => kind switch
    {
        SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken => 11,
        SyntaxKind.PlusToken or SyntaxKind.MinusToken => 10,
        SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken => 9,
        SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or
            SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken => 8,
        SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken => 7,
        SyntaxKind.AmpersandToken => 6,
        SyntaxKind.CaretToken => 5,
        SyntaxKind.PipeToken => 4,
        SyntaxKind.AmpersandAmpersandToken => 3,
        SyntaxKind.PipePipeToken => 2,
        _ => 0,
    };
}
