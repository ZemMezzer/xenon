using System.Globalization;
using System.Text;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

internal sealed class Lexer
{
    private readonly SourceText _source;
    private int _position;

    public Lexer(SourceText source)
    {
        _source = source;
    }

    public DiagnosticBag Diagnostics { get; } = new();

    private char Current => Peek(0);

    private char Lookahead => Peek(1);

    public SyntaxToken Lex()
    {
        SkipTrivia();

        int start = _position;

        if (Current == '\0')
        {
            return MakeToken(SyntaxKind.EndOfFileToken, start);
        }

        if (char.IsLetter(Current) || Current == '_')
        {
            return LexIdentifierOrKeyword();
        }

        if (char.IsDigit(Current))
        {
            return LexNumber();
        }

        if (Current == '"')
        {
            return LexString();
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

        _position += width;
        SyntaxToken token = MakeToken(kind, start);

        if (kind == SyntaxKind.BadToken)
        {
            Diagnostics.ReportInvalidCharacter(token.Location, token.Text[0]);
        }

        return token;
    }

    private SyntaxToken LexIdentifierOrKeyword()
    {
        int start = _position;

        while (char.IsLetterOrDigit(Current) || Current == '_')
        {
            _position++;
        }

        string text = _source.Text[start.._position];
        return MakeToken(SyntaxFacts.GetKeywordKind(text), start);
    }

    private SyntaxToken LexNumber()
    {
        int start = _position;
        int numberBase = 10;
        bool isFloatingPoint = false;
        bool isSinglePrecision = false;

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

            if (Current is 'f' or 'F')
            {
                isFloatingPoint = true;
                isSinglePrecision = true;
                _position++;
            }
        }

        string text = _source.Text[start.._position];

        if (isFloatingPoint)
        {
            string digits = isSinglePrecision ? text[..^1] : text;
            if (isSinglePrecision && float.TryParse(
                    digits,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out float single))
            {
                return MakeToken(SyntaxKind.FloatingPointLiteralToken, start, single);
            }

            if (!isSinglePrecision && double.TryParse(
                    digits,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double @double))
            {
                return MakeToken(SyntaxKind.FloatingPointLiteralToken, start, @double);
            }

            SyntaxToken invalidFloat = MakeToken(SyntaxKind.FloatingPointLiteralToken, start);
            Diagnostics.ReportInvalidNumber(invalidFloat.Location, text, "floating-point");
            return invalidFloat;
        }

        string integerDigits = numberBase == 10 ? text : text[2..];
        if (TryParseUnsignedInteger(integerDigits, numberBase, out ulong integer))
        {
            return MakeToken(SyntaxKind.IntegerLiteralToken, start, integer);
        }

        SyntaxToken invalidInteger = MakeToken(SyntaxKind.IntegerLiteralToken, start);
        Diagnostics.ReportInvalidNumber(invalidInteger.Location, text, "integer");
        return invalidInteger;
    }

    private SyntaxToken LexString()
    {
        int start = _position++;
        var value = new StringBuilder();
        bool terminated = false;

        while (Current is not '\0' and not '\r' and not '\n')
        {
            if (Current == '"')
            {
                _position++;
                terminated = true;
                break;
            }

            if (Current != '\\')
            {
                value.Append(Current);
                _position++;
                continue;
            }

            int escapeStart = _position++;
            if (Current is '\0' or '\r' or '\n')
            {
                break;
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

        SyntaxToken token = MakeToken(SyntaxKind.StringLiteralToken, start, value.ToString());
        if (!terminated)
        {
            Diagnostics.ReportUnterminatedString(token.Location);
        }

        return token;
    }

    private void SkipTrivia()
    {
        while (true)
        {
            if (char.IsWhiteSpace(Current))
            {
                _position++;
                continue;
            }

            if (Current == '/' && Lookahead == '/')
            {
                _position += 2;
                while (Current is not '\0' and not '\r' and not '\n')
                {
                    _position++;
                }

                continue;
            }

            if (Current == '/' && Lookahead == '*')
            {
                int start = _position;
                _position += 2;

                while (Current != '\0' && !(Current == '*' && Lookahead == '/'))
                {
                    _position++;
                }

                if (Current == '\0')
                {
                    Diagnostics.ReportUnterminatedBlockComment(
                        new TextLocation(_source, TextSpan.FromBounds(start, _position)));
                    return;
                }

                _position += 2;
                continue;
            }

            return;
        }
    }

    private SyntaxToken MakeToken(SyntaxKind kind, int start, object? value = null)
    {
        var span = TextSpan.FromBounds(start, _position);
        return new SyntaxToken(kind, new TextLocation(_source, span), _source.GetText(span), value);
    }

    private char Peek(int offset)
    {
        int index = _position + offset;
        return index >= _source.Length ? '\0' : _source[index];
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
