using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Syntax;

internal sealed class Parser
{
    private readonly ImmutableArray<SyntaxToken> _tokens;
    private int _position;

    public Parser(ImmutableArray<SyntaxToken> tokens)
    {
        _tokens = tokens.Where(token => token.Kind != SyntaxKind.BadToken).ToImmutableArray();
    }

    public DiagnosticBag Diagnostics { get; } = new();

    private SyntaxToken Current => Peek(0);

    public CompilationUnitSyntax ParseCompilationUnit()
    {
        NamespaceDeclarationSyntax @namespace = ParseNamespaceDeclaration();
        var members = ImmutableArray.CreateBuilder<MemberDeclarationSyntax>();

        while (Current.Kind != SyntaxKind.EndOfFileToken)
        {
            int start = _position;
            members.Add(ParseFunctionDeclaration());

            if (_position == start)
            {
                NextToken();
            }
        }

        SyntaxToken endOfFile = MatchToken(SyntaxKind.EndOfFileToken);
        return new CompilationUnitSyntax(@namespace, members.ToImmutable(), endOfFile);
    }

    private NamespaceDeclarationSyntax ParseNamespaceDeclaration()
    {
        SyntaxToken namespaceKeyword = MatchToken(SyntaxKind.NamespaceKeyword);
        var nameParts = ImmutableArray.CreateBuilder<SyntaxToken>();
        var dotTokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        nameParts.Add(MatchToken(SyntaxKind.IdentifierToken));
        while (Current.Kind == SyntaxKind.DotToken)
        {
            dotTokens.Add(NextToken());
            nameParts.Add(MatchToken(SyntaxKind.IdentifierToken));
        }

        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new NamespaceDeclarationSyntax(
            namespaceKeyword,
            nameParts.ToImmutable(),
            dotTokens.ToImmutable(),
            semicolon);
    }

    private FunctionDeclarationSyntax ParseFunctionDeclaration()
    {
        SyntaxToken? modifier = Current.Kind is SyntaxKind.ExternKeyword or SyntaxKind.ExportKeyword
            ? NextToken()
            : null;
        TypeSyntax returnType = ParseType();
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        var parameters = ImmutableArray.CreateBuilder<ParameterSyntax>();
        var commaTokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        while (Current.Kind is not SyntaxKind.CloseParenthesisToken and not SyntaxKind.EndOfFileToken)
        {
            parameters.Add(ParseParameter());
            if (Current.Kind != SyntaxKind.CommaToken)
            {
                break;
            }

            commaTokens.Add(NextToken());
        }

        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        BlockStatementSyntax? body = null;
        SyntaxToken? semicolon = null;

        if (modifier?.Kind == SyntaxKind.ExternKeyword)
        {
            semicolon = MatchToken(SyntaxKind.SemicolonToken);
        }
        else
        {
            body = ParseBlockStatement();
        }

        return new FunctionDeclarationSyntax(
            modifier,
            returnType,
            identifier,
            openParenthesis,
            parameters.ToImmutable(),
            commaTokens.ToImmutable(),
            closeParenthesis,
            body,
            semicolon);
    }

    private ParameterSyntax ParseParameter()
    {
        TypeSyntax type = ParseType();
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        return new ParameterSyntax(type, identifier);
    }

    private TypeSyntax ParseType()
    {
        SyntaxToken? constKeyword = Current.Kind == SyntaxKind.ConstKeyword ? NextToken() : null;
        SyntaxToken name = SyntaxFacts.IsTypeName(Current.Kind)
            ? NextToken()
            : MatchToken(SyntaxKind.IdentifierToken);

        var pointerTokens = ImmutableArray.CreateBuilder<SyntaxToken>();
        while (Current.Kind == SyntaxKind.StarToken)
        {
            pointerTokens.Add(NextToken());
        }

        return new TypeSyntax(constKeyword, name, pointerTokens.ToImmutable());
    }

    private BlockStatementSyntax ParseBlockStatement()
    {
        SyntaxToken openBrace = MatchToken(SyntaxKind.OpenBraceToken);
        var statements = ImmutableArray.CreateBuilder<StatementSyntax>();

        while (Current.Kind is not SyntaxKind.CloseBraceToken and not SyntaxKind.EndOfFileToken)
        {
            int start = _position;
            statements.Add(ParseStatement());

            if (_position == start)
            {
                NextToken();
            }
        }

        SyntaxToken closeBrace = MatchToken(SyntaxKind.CloseBraceToken);
        return new BlockStatementSyntax(openBrace, statements.ToImmutable(), closeBrace);
    }

    private StatementSyntax ParseStatement()
    {
        if (Current.Kind == SyntaxKind.OpenBraceToken)
        {
            return ParseBlockStatement();
        }

        if (Current.Kind == SyntaxKind.ReturnKeyword)
        {
            return ParseReturnStatement();
        }

        if (Current.Kind == SyntaxKind.IfKeyword)
        {
            return ParseIfStatement();
        }

        if (Current.Kind == SyntaxKind.WhileKeyword)
        {
            return ParseWhileStatement();
        }

        if (Current.Kind == SyntaxKind.ForKeyword)
        {
            return ParseForStatement();
        }

        if (Current.Kind == SyntaxKind.BreakKeyword)
        {
            return ParseBreakStatement();
        }

        if (Current.Kind == SyntaxKind.ContinueKeyword)
        {
            return ParseContinueStatement();
        }

        if (IsVariableDeclaration())
        {
            return ParseVariableDeclarationStatement();
        }

        return ParseExpressionStatement();
    }

    private ReturnStatementSyntax ParseReturnStatement()
    {
        SyntaxToken returnKeyword = MatchToken(SyntaxKind.ReturnKeyword);
        ExpressionSyntax? expression = Current.Kind == SyntaxKind.SemicolonToken
            ? null
            : ParseExpression();
        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new ReturnStatementSyntax(returnKeyword, expression, semicolon);
    }

    private IfStatementSyntax ParseIfStatement()
    {
        SyntaxToken ifKeyword = MatchToken(SyntaxKind.IfKeyword);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        ExpressionSyntax condition = ParseExpression();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        StatementSyntax thenStatement = ParseStatement();
        SyntaxToken? elseKeyword = null;
        StatementSyntax? elseStatement = null;

        if (Current.Kind == SyntaxKind.ElseKeyword)
        {
            elseKeyword = NextToken();
            elseStatement = ParseStatement();
        }

        return new IfStatementSyntax(
            ifKeyword,
            openParenthesis,
            condition,
            closeParenthesis,
            thenStatement,
            elseKeyword,
            elseStatement);
    }

    private WhileStatementSyntax ParseWhileStatement()
    {
        SyntaxToken whileKeyword = MatchToken(SyntaxKind.WhileKeyword);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        ExpressionSyntax condition = ParseExpression();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        StatementSyntax body = ParseStatement();
        return new WhileStatementSyntax(whileKeyword, openParenthesis, condition, closeParenthesis, body);
    }

    private ForStatementSyntax ParseForStatement()
    {
        SyntaxToken forKeyword = MatchToken(SyntaxKind.ForKeyword);
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        StatementSyntax? initializer = null;
        SyntaxToken firstSemicolon;

        if (Current.Kind == SyntaxKind.SemicolonToken)
        {
            firstSemicolon = NextToken();
        }
        else if (IsVariableDeclaration())
        {
            var declaration = ParseVariableDeclarationStatement();
            initializer = declaration;
            firstSemicolon = declaration.SemicolonToken;
        }
        else
        {
            ExpressionSyntax expression = ParseExpression();
            firstSemicolon = MatchToken(SyntaxKind.SemicolonToken);
            initializer = new ExpressionStatementSyntax(expression, firstSemicolon);
        }

        ExpressionSyntax? condition = Current.Kind == SyntaxKind.SemicolonToken
            ? null
            : ParseExpression();
        SyntaxToken secondSemicolon = MatchToken(SyntaxKind.SemicolonToken);
        ExpressionSyntax? increment = Current.Kind == SyntaxKind.CloseParenthesisToken
            ? null
            : ParseExpression();
        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        StatementSyntax body = ParseStatement();

        return new ForStatementSyntax(
            forKeyword,
            openParenthesis,
            initializer,
            firstSemicolon,
            condition,
            secondSemicolon,
            increment,
            closeParenthesis,
            body);
    }

    private BreakStatementSyntax ParseBreakStatement()
    {
        SyntaxToken keyword = MatchToken(SyntaxKind.BreakKeyword);
        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new BreakStatementSyntax(keyword, semicolon);
    }

    private ContinueStatementSyntax ParseContinueStatement()
    {
        SyntaxToken keyword = MatchToken(SyntaxKind.ContinueKeyword);
        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new ContinueStatementSyntax(keyword, semicolon);
    }

    private VariableDeclarationStatementSyntax ParseVariableDeclarationStatement()
    {
        TypeSyntax type = ParseType();
        SyntaxToken identifier = MatchToken(SyntaxKind.IdentifierToken);
        SyntaxToken? equals = null;
        ExpressionSyntax? initializer = null;

        if (Current.Kind == SyntaxKind.EqualsToken)
        {
            equals = NextToken();
            initializer = ParseExpression();
        }

        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new VariableDeclarationStatementSyntax(type, identifier, equals, initializer, semicolon);
    }

    private ExpressionStatementSyntax ParseExpressionStatement()
    {
        ExpressionSyntax expression = ParseExpression();
        SyntaxToken semicolon = MatchToken(SyntaxKind.SemicolonToken);
        return new ExpressionStatementSyntax(expression, semicolon);
    }

    private ExpressionSyntax ParseExpression() => ParseAssignmentExpression();

    private ExpressionSyntax ParseAssignmentExpression()
    {
        ExpressionSyntax left = ParseBinaryExpression();

        if (!SyntaxFacts.IsAssignmentOperator(Current.Kind))
        {
            return left;
        }

        SyntaxToken operatorToken = NextToken();
        ExpressionSyntax right = ParseAssignmentExpression();
        return new AssignmentExpressionSyntax(left, operatorToken, right);
    }

    private ExpressionSyntax ParseBinaryExpression(int parentPrecedence = 0)
    {
        ExpressionSyntax left;
        int unaryPrecedence = SyntaxFacts.GetUnaryOperatorPrecedence(Current.Kind);

        if (unaryPrecedence != 0 && unaryPrecedence >= parentPrecedence)
        {
            SyntaxToken operatorToken = NextToken();
            ExpressionSyntax operand = ParseBinaryExpression(unaryPrecedence);
            left = new UnaryExpressionSyntax(operatorToken, operand);
        }
        else
        {
            left = ParsePostfixExpression();
        }

        while (true)
        {
            int precedence = SyntaxFacts.GetBinaryOperatorPrecedence(Current.Kind);
            if (precedence == 0 || precedence <= parentPrecedence)
            {
                break;
            }

            SyntaxToken operatorToken = NextToken();
            ExpressionSyntax right = ParseBinaryExpression(precedence);
            left = new BinaryExpressionSyntax(left, operatorToken, right);
        }

        return left;
    }

    private ExpressionSyntax ParsePostfixExpression()
    {
        ExpressionSyntax expression = ParsePrimaryExpression();

        while (Current.Kind == SyntaxKind.OpenParenthesisToken)
        {
            expression = ParseCallExpression(expression);
        }

        if (Current.Kind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
        {
            expression = new PostfixUnaryExpressionSyntax(expression, NextToken());
        }

        return expression;
    }

    private CallExpressionSyntax ParseCallExpression(ExpressionSyntax target)
    {
        SyntaxToken openParenthesis = MatchToken(SyntaxKind.OpenParenthesisToken);
        var arguments = ImmutableArray.CreateBuilder<ExpressionSyntax>();
        var commaTokens = ImmutableArray.CreateBuilder<SyntaxToken>();

        while (Current.Kind is not SyntaxKind.CloseParenthesisToken and not SyntaxKind.EndOfFileToken)
        {
            arguments.Add(ParseExpression());
            if (Current.Kind != SyntaxKind.CommaToken)
            {
                break;
            }

            commaTokens.Add(NextToken());
        }

        SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
        return new CallExpressionSyntax(
            target,
            openParenthesis,
            arguments.ToImmutable(),
            commaTokens.ToImmutable(),
            closeParenthesis);
    }

    private ExpressionSyntax ParsePrimaryExpression()
    {
        if (Current.Kind == SyntaxKind.OpenParenthesisToken)
        {
            SyntaxToken openParenthesis = NextToken();
            ExpressionSyntax expression = ParseExpression();
            SyntaxToken closeParenthesis = MatchToken(SyntaxKind.CloseParenthesisToken);
            return new ParenthesizedExpressionSyntax(openParenthesis, expression, closeParenthesis);
        }

        if (Current.Kind is
            SyntaxKind.IntegerLiteralToken or
            SyntaxKind.FloatingPointLiteralToken or
            SyntaxKind.StringLiteralToken or
            SyntaxKind.TrueKeyword or
            SyntaxKind.FalseKeyword or
            SyntaxKind.NullKeyword)
        {
            return new LiteralExpressionSyntax(NextToken());
        }

        if (Current.Kind == SyntaxKind.IdentifierToken)
        {
            return new NameExpressionSyntax(NextToken());
        }

        SyntaxToken unexpected = NextToken();
        Diagnostics.ReportUnexpectedToken(
            unexpected.Location,
            unexpected.Kind,
            SyntaxKind.IdentifierToken);
        var missing = new SyntaxToken(
            SyntaxKind.IdentifierToken,
            new TextLocation(unexpected.Location.Source, new TextSpan(unexpected.Location.Span.Start, 0)),
            string.Empty,
            IsMissing: true);
        return new NameExpressionSyntax(missing);
    }

    private bool IsVariableDeclaration()
    {
        int offset = Current.Kind == SyntaxKind.ConstKeyword ? 1 : 0;
        if (!SyntaxFacts.IsTypeName(Peek(offset).Kind))
        {
            return false;
        }

        offset++;
        while (Peek(offset).Kind == SyntaxKind.StarToken)
        {
            offset++;
        }

        return Peek(offset).Kind == SyntaxKind.IdentifierToken;
    }

    private SyntaxToken MatchToken(SyntaxKind kind)
    {
        if (Current.Kind == kind)
        {
            return NextToken();
        }

        Diagnostics.ReportUnexpectedToken(Current.Location, Current.Kind, kind);
        return new SyntaxToken(
            kind,
            new TextLocation(Current.Location.Source, new TextSpan(Current.Location.Span.Start, 0)),
            string.Empty,
            IsMissing: true);
    }

    private SyntaxToken NextToken()
    {
        SyntaxToken current = Current;
        _position++;
        return current;
    }

    private SyntaxToken Peek(int offset)
    {
        int index = _position + offset;
        return index >= _tokens.Length ? _tokens[^1] : _tokens[index];
    }
}
