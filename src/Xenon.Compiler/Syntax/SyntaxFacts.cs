using System.Collections.Frozen;

namespace Xenon.Compiler.Syntax;

public static class SyntaxFacts
{
    private static readonly FrozenDictionary<string, SyntaxKind> Keywords =
        new Dictionary<string, SyntaxKind>(StringComparer.Ordinal)
        {
            ["using"] = SyntaxKind.UsingKeyword,
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
            ["readonly"] = SyntaxKind.ReadonlyKeyword,
            ["struct"] = SyntaxKind.StructKeyword,
            ["interface"] = SyntaxKind.InterfaceKeyword,
            ["template"] = SyntaxKind.TemplateKeyword,
            ["where"] = SyntaxKind.WhereKeyword,
            ["static"] = SyntaxKind.StaticKeyword,
            ["virtual"] = SyntaxKind.VirtualKeyword,
            ["override"] = SyntaxKind.OverrideKeyword,
            ["abstract"] = SyntaxKind.AbstractKeyword,
            ["get"] = SyntaxKind.GetKeyword,
            ["set"] = SyntaxKind.SetKeyword,
            ["base"] = SyntaxKind.BaseKeyword,
            ["this"] = SyntaxKind.ThisKeyword,
            ["sizeof"] = SyntaxKind.SizeOfKeyword,
            ["alignof"] = SyntaxKind.AlignOfKeyword,
            ["offsetof"] = SyntaxKind.OffsetOfKeyword,
            ["enum"] = SyntaxKind.EnumKeyword,
            ["switch"] = SyntaxKind.SwitchKeyword,
            ["case"] = SyntaxKind.CaseKeyword,
            ["default"] = SyntaxKind.DefaultKeyword,
            ["if"] = SyntaxKind.IfKeyword,
            ["else"] = SyntaxKind.ElseKeyword,
            ["while"] = SyntaxKind.WhileKeyword,
            ["for"] = SyntaxKind.ForKeyword,
            ["break"] = SyntaxKind.BreakKeyword,
            ["continue"] = SyntaxKind.ContinueKeyword,
            ["return"] = SyntaxKind.ReturnKeyword,
            ["extern"] = SyntaxKind.ExternKeyword,
            ["export"] = SyntaxKind.ExportKeyword,
            ["public"] = SyntaxKind.PublicKeyword,
            ["private"] = SyntaxKind.PrivateKeyword,
            ["true"] = SyntaxKind.TrueKeyword,
            ["false"] = SyntaxKind.FalseKeyword,
            ["null"] = SyntaxKind.NullKeyword,
            ["cast"] = SyntaxKind.CastKeyword,
            ["bitcast"] = SyntaxKind.BitCastKeyword,
            ["new"] = SyntaxKind.NewKeyword,
            ["free"] = SyntaxKind.FreeKeyword,
            ["move"] = SyntaxKind.MoveKeyword,
            ["lock"] = SyntaxKind.LockKeyword,
            ["unique"] = SyntaxKind.UniqueKeyword,
            ["shared"] = SyntaxKind.SharedKeyword,
            ["weak"] = SyntaxKind.WeakKeyword,
            ["storage"] = SyntaxKind.StorageKeyword,
            ["pin"] = SyntaxKind.PinKeyword,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public static SyntaxKind GetKeywordKind(string text) =>
        Keywords.TryGetValue(text, out SyntaxKind kind) ? kind : SyntaxKind.IdentifierToken;

    /// <summary>The canonical lexer keyword set for editor/tooling consumers.</summary>
    public static IReadOnlyList<string> GetKeywordTexts() => Keywords.Keys
        .OrderBy(keyword => keyword, StringComparer.Ordinal).ToArray();

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
        SyntaxKind.UniqueKeyword or
        SyntaxKind.SharedKeyword or
        SyntaxKind.WeakKeyword or
        SyntaxKind.StorageKeyword or
        SyntaxKind.PinKeyword or
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
