using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

// Masks excluded source without changing any original offset, line break or token location.
internal sealed class ConditionalSource
{
    private sealed class Frame(bool parent, bool taken, int start)
    {
        public bool Parent = parent;
        public bool Taken = taken;
        public bool Active = parent && taken;
        public bool ElseSeen;
        public int Start = start;
    }

    private readonly SourceText _source;
    private readonly ConditionalCompilationOptions _options;
    private readonly Stack<Frame> _stack = new();
    private readonly DiagnosticBag _diagnostics = new();
    private bool _targetDependent;
    private bool _hasDirectives;
    private ConditionalSource(SourceText source, ConditionalCompilationOptions options)
    {
        _source = source;
        _options = options;
    }

    public static (string Text, ImmutableArray<Diagnostic> Diagnostics, bool TargetDependent, bool HasDirectives) Filter(
        SourceText source, ConditionalCompilationOptions options, CancellationToken cancellationToken)
    {
        var scanner = new ConditionalSource(source, options);
        return scanner.Scan(cancellationToken);
    }

    private bool Active => _stack.Count == 0 || _stack.Peek().Active;
    private void Error(int start, int length, string message) => _diagnostics.Report(
        new TextLocation(_source, new TextSpan(start, length)), message, "XE1100");

    private (string, ImmutableArray<Diagnostic>, bool, bool) Scan(CancellationToken cancellationToken)
    {
        string text = _source.Text;
        char[] result = text.ToCharArray();
        bool blockComment = false;
        int start = 0;
        while (start < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            int first = start;
            while (first < end && char.IsWhiteSpace(text[first])) first++;
            bool directive = !blockComment && first < end && text[first] == '#';
            bool active = Active;
            if (directive)
            {
                _hasDirectives = true;
                ProcessDirective(first, end);
            }
            else
            {
                // Track comments and literals even in excluded source; malformed ordinary
                // tokens are ignored. Xenon strings/chars cannot span physical newlines.
                char quote = '\0';
                for (int i = start; i < end; i++)
                {
                    char c = text[i], next = i + 1 < end ? text[i + 1] : '\0';
                    if (blockComment)
                    {
                        if (c == '*' && next == '/')
                        {
                            blockComment = false;
                            i++;
                        }
                    }
                    else if (quote != '\0')
                    {
                        if (c == '\\') i++;
                        else if (c == quote) quote = '\0';
                    }
                    else if (c == '/' && next == '/') break;
                    else if (c == '/' && next == '*')
                    {
                        blockComment = true;
                        i++;
                    }
                    else if (c is '\'' or '"') quote = c;
                }
            }
            if (directive || !active)
                Array.Fill(result, ' ', start, end - start);
            start = end;
            if (start < text.Length && text[start] == '\r') start++;
            if (start < text.Length && text[start] == '\n') start++;
        }
        foreach (Frame frame in _stack) Error(frame.Start, 3, "unterminated #if: expected #endif");
        return (new string(result), [.. _diagnostics], _targetDependent, _hasDirectives);
    }

    private void ProcessDirective(int start, int end)
    {
        int p = start + 1;
        while (p < end && char.IsWhiteSpace(_source[p])) p++;
        int wordStart = p;
        while (p < end && (char.IsLetterOrDigit(_source[p]) || _source[p] == '_')) p++;
        string word = _source.Text[wordStart..p];
        string expression = _source.Text[p..end];
        int comment = expression.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0) expression = expression[..comment];
        bool condition = false;
        if (word is "if" or "elif")
        {
            var parser = new ConditionParser(expression, _options.Defines);
            condition = parser.Parse();
            if (!_options.AllowProfileDefines && parser.Expression.Symbols.Any(name => name is "XENON_DEBUG" or "XENON_RELEASE"))
                Error(start, end - start, "XENON_DEBUG and XENON_RELEASE are unavailable in XELIB builds; use an explicit project or global feature define");
            // Include every target name in a condition, even one whose value short-circuits.
            if (parser.UsesTarget)
            {
                _targetDependent = true;
                if (_options.TargetTriple is null) Error(start, end - start, "target-dependent #if requires a compilation target");
            }
            if (parser.Failure is { } failure) Error(p + parser.ErrorOffset, Math.Min(1, end - p - parser.ErrorOffset), failure);
        }
        else if (word is "else" or "endif")
        {
            if (!string.IsNullOrWhiteSpace(expression)) Error(p, end - p, $"unexpected text after #{word}");
        }
        else
        {
            Error(start, end - start, $"unsupported conditional directive '#{word}'; only #if, #elif, #else and #endif are supported");
            return;
        }
        if (word == "if")
        {
            _stack.Push(new Frame(Active, condition, start));
            return;
        }
        if (!_stack.TryPeek(out Frame? frame))
        {
            Error(start, end - start, $"#{word} without #if");
            return;
        }
        switch (word)
        {
            case "endif":
                _stack.Pop();
                break;
            case "else":
                if (frame.ElseSeen) Error(start, end - start, "multiple #else directives for one #if");
                frame.Active = frame.Parent && !frame.Taken && !frame.ElseSeen;
                frame.ElseSeen = true;
                frame.Taken = true;
                break;
            case "elif":
                if (frame.ElseSeen) Error(start, end - start, "#elif after #else");
                frame.Active = frame.Parent && !frame.Taken && !frame.ElseSeen && condition;
                frame.Taken |= condition;
                break;
        }
    }

    private sealed class ConditionParser(string text, ImmutableSortedSet<string> defines)
    {
        private int _position;
        private int _depth;
        public string? Failure { get; private set; }
        public int ErrorOffset { get; private set; }
        public bool UsesTarget { get; private set; }
        public ConditionalExpression Expression { get; private set; } = ConditionalExpression.False;
        private void Fail(string message)
        {
            if (Failure is null)
            {
                Failure = message;
                ErrorOffset = _position;
            }
        }

        private void Space()
        {
            while (_position < text.Length && char.IsWhiteSpace(text[_position])) _position++;
        }

        private bool Take(string token)
        {
            Space();
            if (!text.AsSpan(_position).StartsWith(token, StringComparison.Ordinal)) return false;
            _position += token.Length;
            return true;
        }

        public bool Parse()
        {
            Space();
            if (_position == text.Length)
            {
                Fail("missing conditional expression");
                return false;
            }
            Expression = Or();
            Space();
            if (_position != text.Length) Fail("invalid token in conditional expression");
            return Failure is null && Expression.Evaluate(defines);
        }

        private ConditionalExpression Or()
        {
            var operands = new List<ConditionalExpression> { And() };
            while (Take("||")) operands.Add(And());
            return ConditionalExpression.Any(operands);
        }

        private ConditionalExpression And()
        {
            var operands = new List<ConditionalExpression> { Unary() };
            while (Take("&&")) operands.Add(Unary());
            return ConditionalExpression.All(operands);
        }

        private ConditionalExpression Unary()
        {
            if (++_depth > 256)
            {
                Fail("conditional expression nesting is too deep");
                _depth--;
                return ConditionalExpression.False;
            }
            try
            {
                if (Take("!")) return ConditionalExpression.Not(Unary());
                if (Take("("))
                {
                    ConditionalExpression value = Or();
                    if (!Take(")")) Fail("expected ')' in conditional expression");
                    return value;
                }
                Space();
                int start = _position;
                if (_position < text.Length && (char.IsLetter(text[_position]) || text[_position] == '_'))
                {
                    _position++;
                    while (_position < text.Length && (char.IsLetterOrDigit(text[_position]) || text[_position] == '_')) _position++;
                    string name = text[start.._position];
                    UsesTarget |= CompilationTarget.IsTargetDefine(name);
                    return ConditionalExpression.Named(name);
                }
                Fail("expected an identifier, '!' or '(' in conditional expression");
                return ConditionalExpression.False;
            }
            finally
            {
                _depth--;
            }
        }
    }
}
