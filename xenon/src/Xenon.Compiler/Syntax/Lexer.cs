using System.Buffers;
using System.Globalization;
using System.Text;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

internal sealed class Lexer
{
    private readonly SourceText _source;
    private readonly string _text;
    private int _position;

    public Lexer(SourceText source, string? filteredText = null)
    {
        _source = source;
        _text = filteredText ?? source.Text;
    }

    public DiagnosticBag Diagnostics { get; } = new();

    private bool IsAtEnd => _position >= _text.Length;

    private char Current => Peek(0);

    private char Lookahead => Peek(1);

    private bool IsAtLineBreak => Current is '\r' or '\n';

    // Count CRLF once, when the LF is consumed.
    private bool CompletesLineBreak => Current == '\n' || (Current == '\r' && Lookahead != '\n');

    public SyntaxToken Lex()
    {
        string? leadingDocumentation = SkipTrivia();

        int start = _position;

        if (IsAtEnd)
        {
            return MakeToken(SyntaxKind.EndOfFileToken, start, leadingDocumentation: leadingDocumentation);
        }

        if (IdentifierFacts.TryGetStart(_text, _position, out _))
        {
            return LexIdentifierOrKeyword(leadingDocumentation);
        }

        if (char.IsDigit(Current))
        {
            return LexNumber(leadingDocumentation);
        }

        if (Current == '"')
        {
            return LexString(leadingDocumentation);
        }

        if (Current == '\'')
        {
            return LexCharacter(leadingDocumentation);
        }

        SyntaxKind kind = Current switch
        {
            '(' => SyntaxKind.OpenParenthesisToken,
            ')' => SyntaxKind.CloseParenthesisToken,
            '{' => SyntaxKind.OpenBraceToken,
            '}' => SyntaxKind.CloseBraceToken,
            '[' => SyntaxKind.OpenBracketToken,
            ']' => SyntaxKind.CloseBracketToken,
            ';' => SyntaxKind.SemicolonToken,
            ',' => SyntaxKind.CommaToken,
            ':' => SyntaxKind.ColonToken,
            '.' => SyntaxKind.DotToken,
            '~' => SyntaxKind.TildeToken,
            '+' when Lookahead == '+' => SyntaxKind.PlusPlusToken,
            '+' when Lookahead == '=' => SyntaxKind.PlusEqualsToken,
            '+' => SyntaxKind.PlusToken,
            '-' when Lookahead == '-' && Peek(2) == '>' => SyntaxKind.CompareExchangeArrowToken,
            '-' when Lookahead == '>' => SyntaxKind.ArrowToken,
            '-' when Lookahead == '-' => SyntaxKind.MinusMinusToken,
            '-' when Lookahead == '=' => SyntaxKind.MinusEqualsToken,
            '-' => SyntaxKind.MinusToken,
            '*' when Lookahead == '=' => SyntaxKind.StarEqualsToken,
            '*' => SyntaxKind.StarToken,
            '/' when Lookahead == '=' => SyntaxKind.SlashEqualsToken,
            '/' => SyntaxKind.SlashToken,
            '%' when Lookahead == '=' => SyntaxKind.PercentEqualsToken,
            '%' => SyntaxKind.PercentToken,
            '=' when Lookahead == '>' => SyntaxKind.FatArrowToken,
            '=' when Lookahead == '=' => SyntaxKind.EqualsEqualsToken,
            '=' => SyntaxKind.EqualsToken,
            '!' when Lookahead == '=' => SyntaxKind.BangEqualsToken,
            '!' => SyntaxKind.BangToken,
            '<' when Lookahead == '-' && Peek(2) == '>' => SyntaxKind.SwapToken,
            '<' when Lookahead == '<' && Peek(2) == '=' => SyntaxKind.LessLessEqualsToken,
            '<' when Lookahead == '<' => SyntaxKind.LessLessToken,
            '<' when Lookahead == '=' => SyntaxKind.LessOrEqualsToken,
            '<' => SyntaxKind.LessToken,
            '>' when Lookahead == '>' && Peek(2) == '=' => SyntaxKind.GreaterGreaterEqualsToken,
            '>' when Lookahead == '>' => SyntaxKind.GreaterGreaterToken,
            '>' when Lookahead == '=' => SyntaxKind.GreaterOrEqualsToken,
            '>' => SyntaxKind.GreaterToken,
            '&' when Lookahead == '&' => SyntaxKind.AmpersandAmpersandToken,
            '&' when Lookahead == '=' => SyntaxKind.AmpersandEqualsToken,
            '&' => SyntaxKind.AmpersandToken,
            '|' when Lookahead == '|' => SyntaxKind.PipePipeToken,
            '|' when Lookahead == '=' => SyntaxKind.PipeEqualsToken,
            '|' => SyntaxKind.PipeToken,
            '^' when Lookahead == '=' => SyntaxKind.CaretEqualsToken,
            '^' => SyntaxKind.CaretToken,
            _ => SyntaxKind.BadToken,
        };

        int width = kind is SyntaxKind.SwapToken or SyntaxKind.CompareExchangeArrowToken or
            SyntaxKind.LessLessEqualsToken or SyntaxKind.GreaterGreaterEqualsToken
            ? 3
            : IsTwoCharacterToken(kind) ? 2 : 1;

        if (kind == SyntaxKind.BadToken && char.IsHighSurrogate(Current) &&
            Rune.DecodeFromUtf16(_text.AsSpan(_position), out _, out int scalarWidth) == OperationStatus.Done)
            width = scalarWidth;

        _position += width;
        SyntaxToken token = MakeToken(kind, start, leadingDocumentation: leadingDocumentation);

        if (kind == SyntaxKind.BadToken)
        {
            OperationStatus status = Rune.DecodeFromUtf16(token.Text, out Rune rune, out int decodedWidth);
            if (status != OperationStatus.Done && char.IsSurrogate(token.Text[0]))
                Diagnostics.ReportInvalidUnicodeScalar(token.Location);
            else if (decodedWidth == 2)
                Diagnostics.Report(token.Location, $"invalid character U+{rune.Value:X4}",
                    DiagnosticIds.InvalidCharacter);
            else
                Diagnostics.ReportInvalidCharacter(token.Location, token.Text[0]);
        }

        return token;
    }

    private SyntaxToken LexIdentifierOrKeyword(string? leadingDocumentation)
    {
        int start = _position;

        while (IdentifierFacts.TryGetContinue(_text, _position, out int width))
        {
            _position += width;
        }

        string text = _source.Text[start.._position];
        SyntaxToken token = MakeToken(SyntaxFacts.GetKeywordKind(text), start, leadingDocumentation: leadingDocumentation);
        if (RuntimeAbiNames.IsReservedIdentifier(text))
            Diagnostics.Report(token.Location,
                $"identifier '{text}' uses reserved compiler/runtime prefix '{RuntimeAbiNames.Prefix}'",
                DiagnosticIds.ReservedRuntimeIdentifier);
        return token;
    }

    private SyntaxToken LexNumber(string? leadingDocumentation)
    {
        int start = _position;
        int numberBase = 10;
        bool isFloatingPoint = false;

        if (Current == '0' && Lookahead is 'x' or 'X')
        {
            numberBase = 16;
            _position += 2;
            while (IsDigitForBase(Current, numberBase))
            {
                _position++;
            }
        }
        else if (Current == '0' && Lookahead is 'b' or 'B')
        {
            numberBase = 2;
            _position += 2;
            while (IsDigitForBase(Current, numberBase))
            {
                _position++;
            }
        }
        else
        {
            while (char.IsDigit(Current))
            {
                _position++;
            }

            if (Current == '.' && char.IsDigit(Lookahead))
            {
                isFloatingPoint = true;
                _position++;

                while (char.IsDigit(Current))
                {
                    _position++;
                }
            }

            if (Current is 'e' or 'E')
            {
                isFloatingPoint = true;
                _position++;

                if (Current is '+' or '-')
                {
                    _position++;
                }

                while (char.IsDigit(Current))
                {
                    _position++;
                }
            }
        }

        int bodyEnd = _position;
        while (IdentifierFacts.TryGetContinue(_text, _position, out int width))
            _position += width;

        string suffixText = _source.Text[bodyEnd.._position];
        NumericLiteralSuffix? parsedSuffix = suffixText.ToLowerInvariant() switch
        {
            "" => NumericLiteralSuffix.None,
            "f" => NumericLiteralSuffix.Float,
            "d" => NumericLiteralSuffix.Double,
            "u" => NumericLiteralSuffix.UInt,
            "l" => NumericLiteralSuffix.Long,
            "ul" => NumericLiteralSuffix.ULong,
            "n" => NumericLiteralSuffix.NInt,
            "un" => NumericLiteralSuffix.NUInt,
            _ => null,
        };
        bool floatingSuffix = parsedSuffix is NumericLiteralSuffix.Float or NumericLiteralSuffix.Double;
        if (parsedSuffix is null || (floatingSuffix && numberBase != 10) ||
            (isFloatingPoint && parsedSuffix != NumericLiteralSuffix.None && !floatingSuffix))
        {
            SyntaxToken invalidSuffix = MakeToken(isFloatingPoint
                ? SyntaxKind.FloatingPointLiteralToken : SyntaxKind.IntegerLiteralToken,
                start, leadingDocumentation: leadingDocumentation);
            Diagnostics.Report(new TextLocation(_source, TextSpan.FromBounds(bodyEnd, _position)),
                $"invalid numeric suffix '{suffixText}'", DiagnosticIds.InvalidNumericSuffix);
            return invalidSuffix;
        }

        NumericLiteralSuffix suffix = parsedSuffix.Value;
        isFloatingPoint |= floatingSuffix;
        string text = _source.Text[start.._position];
        string body = _source.Text[start..bodyEnd];

        if (isFloatingPoint)
        {
            bool isSinglePrecision = suffix == NumericLiteralSuffix.Float;
            if (isSinglePrecision && float.TryParse(
                    body,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float single) && float.IsFinite(single))
            {
                return MakeToken(SyntaxKind.FloatingPointLiteralToken, start, single, leadingDocumentation, suffix);
            }

            if (!isSinglePrecision && double.TryParse(
                    body,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double @double) && (suffix == NumericLiteralSuffix.None || double.IsFinite(@double)))
            {
                return MakeToken(SyntaxKind.FloatingPointLiteralToken, start, @double, leadingDocumentation, suffix);
            }

            SyntaxToken invalidFloat = MakeToken(SyntaxKind.FloatingPointLiteralToken, start,
                leadingDocumentation: leadingDocumentation);
            Diagnostics.ReportInvalidNumber(invalidFloat.Location, text, "floating-point");
            return invalidFloat;
        }

        string integerDigits = numberBase == 10 ? body : body[2..];
        if (TryParseUnsignedInteger(integerDigits, numberBase, out ulong integer))
        {
            return MakeToken(SyntaxKind.IntegerLiteralToken, start, integer, leadingDocumentation, suffix);
        }

        SyntaxToken invalidInteger = MakeToken(SyntaxKind.IntegerLiteralToken, start,
            leadingDocumentation: leadingDocumentation);
        Diagnostics.ReportInvalidNumber(invalidInteger.Location, text, "integer");
        return invalidInteger;
    }

    private SyntaxToken LexString(string? leadingDocumentation)
    {
        int start = _position++;
        var value = new StringBuilder();
        bool terminated = false;

        while (!IsAtEnd && !IsAtLineBreak)
        {
            if (Current == '"')
            {
                _position++;
                terminated = true;
                break;
            }

            if (Current == '\0')
            {
                ReportInvalidNul();
                _position++;
                continue;
            }

            if (Current != '\\')
            {
                value.Append(Current);
                _position++;
                continue;
            }

            int escapeStart = _position++;
            if (IsAtEnd || IsAtLineBreak)
            {
                break;
            }

            if (Current == '\0')
            {
                ReportInvalidNul();
                _position++;
                continue;
            }

            char escaped = Current;
            _position++;

            value.Append(escaped switch
            {
                '0' => '\0',
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '"' => '"',
                '\\' => '\\',
                _ => escaped,
            });

            if (escaped is not ('0' or 'n' or 'r' or 't' or '"' or '\\'))
            {
                Diagnostics.ReportUnknownEscapeSequence(
                    new TextLocation(_source, new TextSpan(escapeStart, 2)),
                    escaped);
            }
        }

        SyntaxToken token = MakeToken(SyntaxKind.StringLiteralToken, start, value.ToString(), leadingDocumentation);
        if (!terminated)
        {
            Diagnostics.ReportUnterminatedString(token.Location);
        }

        return token;
    }

    private SyntaxToken LexCharacter(string? leadingDocumentation)
    {
        int start = _position++;
        ulong value = 0;
        int scalarCount = 0;
        bool terminated = false;

        while (!IsAtEnd && !IsAtLineBreak)
        {
            if (Current == '\'')
            {
                _position++;
                terminated = true;
                break;
            }

            if (Current == '\0')
            {
                ReportInvalidNul();
                _position++;
                scalarCount++;
                continue;
            }

            Rune rune;
            if (Current == '\\')
            {
                int escapeStart = _position++;
                if (IsAtEnd || IsAtLineBreak) break;

                if (Current == '\0')
                {
                    ReportInvalidNul();
                    _position++;
                    scalarCount++;
                    continue;
                }

                char escaped = Current;
                _position++;
                char decoded = escaped switch
                {
                    '0' => '\0',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '\'' => '\'',
                    '"' => '"',
                    '\\' => '\\',
                    _ => escaped,
                };
                if (escaped is not ('0' or 'n' or 'r' or 't' or '\'' or '"' or '\\'))
                {
                    Diagnostics.ReportUnknownEscapeSequence(
                        new TextLocation(_source, new TextSpan(escapeStart, 2)), escaped);
                }
                if (!Rune.TryCreate(decoded, out rune))
                {
                    Diagnostics.ReportInvalidUnicodeScalar(
                        new TextLocation(_source, new TextSpan(escapeStart, 2)));
                    scalarCount++;
                    continue;
                }
            }
            else
            {
                OperationStatus status = Rune.DecodeFromUtf16(
                    _source.Text.AsSpan(_position), out rune, out int consumed);
                if (status != OperationStatus.Done)
                {
                    Diagnostics.ReportInvalidUnicodeScalar(
                        new TextLocation(_source, new TextSpan(_position, 1)));
                    _position++;
                    scalarCount++;
                    continue;
                }
                _position += consumed;
            }

            if (scalarCount++ == 0) value = (uint)rune.Value;
        }

        SyntaxToken token = MakeToken(SyntaxKind.CharacterLiteralToken, start, value, leadingDocumentation);
        if (!terminated)
            Diagnostics.ReportUnterminatedCharacter(token.Location);
        else if (scalarCount == 0)
            Diagnostics.ReportEmptyCharacter(token.Location);
        else if (scalarCount != 1)
            Diagnostics.ReportMultiScalarCharacter(token.Location);
        return token;
    }

    private string? SkipTrivia()
    {
        var documentation = new List<string>();
        int lineBreaksAfterDocumentation = 0;
        while (true)
        {
            if (char.IsWhiteSpace(Current))
            {
                if (CompletesLineBreak)
                    lineBreaksAfterDocumentation++;
                _position++;
                if (documentation.Count > 0 && lineBreaksAfterDocumentation > 1)
                    documentation.Clear();
                continue;
            }

            if (Current == '/' && Lookahead == '/')
            {
                bool isDocumentation = Peek(2) == '/';
                _position += isDocumentation ? 3 : 2;
                int contentStart = _position;
                while (!IsAtEnd && !IsAtLineBreak)
                {
                    if (Current == '\0') ReportInvalidNul();
                    _position++;
                }

                if (isDocumentation)
                {
                    string line = _source.Text[contentStart.._position];
                    documentation.Add(line.StartsWith(' ') ? line[1..] : line);
                    lineBreaksAfterDocumentation = 0;
                }
                else
                {
                    documentation.Clear();
                }

                continue;
            }

            if (Current == '/' && Lookahead == '*')
            {
                int start = _position;
                _position += 2;

                while (!IsAtEnd && !(Current == '*' && Lookahead == '/'))
                {
                    if (Current == '\0') ReportInvalidNul();
                    _position++;
                }

                if (IsAtEnd)
                {
                    Diagnostics.ReportUnterminatedBlockComment(
                        new TextLocation(_source, TextSpan.FromBounds(start, _position)));
                    return null;
                }

                _position += 2;
                documentation.Clear();
                continue;
            }

            return documentation.Count == 0 ? null : string.Join('\n', documentation);
        }
    }

    private void ReportInvalidNul() =>
        Diagnostics.ReportInvalidCharacter(new TextLocation(_source, new TextSpan(_position, 1)), '\0');

    private SyntaxToken MakeToken(SyntaxKind kind, int start, object? value = null,
        string? leadingDocumentation = null, NumericLiteralSuffix numericSuffix = NumericLiteralSuffix.None)
    {
        var span = TextSpan.FromBounds(start, _position);
        return new SyntaxToken(kind, new TextLocation(_source, span), _source.GetText(span), value,
            LeadingDocumentation: leadingDocumentation, NumericSuffix: numericSuffix);
    }

    private char Peek(int offset)
    {
        int index = _position + offset;
        return index >= _text.Length ? '\0' : _text[index];
    }

    private static bool IsDigitForBase(char character, int numberBase) => numberBase switch
    {
        2 => character is '0' or '1',
        16 => char.IsAsciiHexDigit(character),
        _ => char.IsDigit(character),
    };

    private static bool TryParseUnsignedInteger(string text, int numberBase, out ulong value)
    {
        value = 0;
        if (text.Length == 0)
        {
            return false;
        }

        foreach (char character in text)
        {
            int digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'f' => character - 'a' + 10,
                >= 'A' and <= 'F' => character - 'A' + 10,
                _ => -1,
            };

            if (digit < 0 || digit >= numberBase)
            {
                return false;
            }

            try
            {
                value = checked((value * (ulong)numberBase) + (ulong)digit);
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTwoCharacterToken(SyntaxKind kind) => kind is
        SyntaxKind.ArrowToken or
        SyntaxKind.FatArrowToken or
        SyntaxKind.EqualsEqualsToken or
        SyntaxKind.BangEqualsToken or
        SyntaxKind.LessOrEqualsToken or
        SyntaxKind.GreaterOrEqualsToken or
        SyntaxKind.AmpersandAmpersandToken or
        SyntaxKind.PipePipeToken or
        SyntaxKind.LessLessToken or
        SyntaxKind.GreaterGreaterToken or
        SyntaxKind.PlusPlusToken or
        SyntaxKind.MinusMinusToken or
        SyntaxKind.PlusEqualsToken or
        SyntaxKind.MinusEqualsToken or
        SyntaxKind.StarEqualsToken or
        SyntaxKind.SlashEqualsToken or
        SyntaxKind.PercentEqualsToken or
        SyntaxKind.AmpersandEqualsToken or
        SyntaxKind.PipeEqualsToken or
        SyntaxKind.CaretEqualsToken;
}
