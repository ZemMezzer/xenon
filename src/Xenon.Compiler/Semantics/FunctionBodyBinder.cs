using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed class FunctionBodyBinder
{
    private readonly FunctionSymbol _function;
    private readonly DiagnosticBag _diagnostics;
    private BoundScope _scope = new(null);

    public FunctionBodyBinder(FunctionSymbol function, DiagnosticBag diagnostics)
    {
        _function = function;
        _diagnostics = diagnostics;

        foreach (ParameterSymbol parameter in function.Parameters)
        {
            _scope.TryDeclare(parameter);
        }
    }

    public BoundBlockStatement BindBody(BlockStatementSyntax body)
    {
        BoundBlockStatement boundBody = BindBlockStatement(body, createScope: false);

        if (!ReferenceEquals(_function.ReturnType, BuiltinTypes.Void) && !AlwaysReturns(boundBody))
        {
            _diagnostics.Report(
                body.CloseBraceToken.Location,
                $"not all code paths in function '{_function.Name}' return a value");
        }

        return boundBody;
    }

    private BoundBlockStatement BindBlockStatement(BlockStatementSyntax syntax, bool createScope = true)
    {
        BoundScope? previous = null;
        if (createScope)
        {
            previous = _scope;
            _scope = new BoundScope(previous);
        }

        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        foreach (StatementSyntax statement in syntax.Statements)
        {
            statements.Add(BindStatement(statement));
        }

        if (previous is not null)
        {
            _scope = previous;
        }

        return new BoundBlockStatement(statements.ToImmutable());
    }

    private BoundStatement BindStatement(StatementSyntax syntax) => syntax switch
    {
        BlockStatementSyntax block => BindBlockStatement(block),
        VariableDeclarationStatementSyntax variable => BindVariableDeclaration(variable),
        ReturnStatementSyntax @return => BindReturnStatement(@return),
        ExpressionStatementSyntax expression => new BoundExpressionStatement(BindExpression(expression.Expression)),
        _ => throw new InvalidOperationException($"Unexpected statement syntax '{syntax.Kind}'."),
    };

    private BoundVariableDeclarationStatement BindVariableDeclaration(VariableDeclarationStatementSyntax syntax)
    {
        TypeSymbol type = TypeResolver.Resolve(syntax.Type, _diagnostics);
        if (ReferenceEquals(type, BuiltinTypes.Void))
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, "local variable type cannot be 'void'");
        }

        BoundExpression? initializer = syntax.Initializer is null ? null : BindExpression(syntax.Initializer);
        if (initializer is not null && !TypeFacts.CanAssign(type, initializer.Type))
        {
            ReportCannotConvert(GetLocation(syntax.Initializer!), initializer.Type, type);
        }

        var variable = new LocalVariableSymbol(syntax.IdentifierToken.Text, type);
        if (!_scope.TryDeclare(variable))
        {
            _diagnostics.Report(
                syntax.IdentifierToken.Location,
                $"variable '{variable.Name}' is already declared in this scope");
        }

        return new BoundVariableDeclarationStatement(variable, initializer);
    }

    private BoundReturnStatement BindReturnStatement(ReturnStatementSyntax syntax)
    {
        BoundExpression? expression = syntax.Expression is null ? null : BindExpression(syntax.Expression);

        if (ReferenceEquals(_function.ReturnType, BuiltinTypes.Void))
        {
            if (expression is not null)
            {
                _diagnostics.Report(GetLocation(syntax.Expression!), "a void function cannot return a value");
            }
        }
        else if (expression is null)
        {
            _diagnostics.Report(syntax.ReturnKeyword.Location, $"function '{_function.Name}' must return a value of type '{_function.ReturnType.Name}'");
        }
        else if (!TypeFacts.CanAssign(_function.ReturnType, expression.Type))
        {
            ReportCannotConvert(GetLocation(syntax.Expression!), expression.Type, _function.ReturnType);
        }

        return new BoundReturnStatement(expression);
    }

    private BoundExpression BindExpression(ExpressionSyntax syntax) => syntax switch
    {
        LiteralExpressionSyntax literal => BindLiteralExpression(literal),
        NameExpressionSyntax name => BindNameExpression(name),
        ParenthesizedExpressionSyntax parenthesized => BindExpression(parenthesized.Expression),
        UnaryExpressionSyntax unary => BindUnaryExpression(unary),
        BinaryExpressionSyntax binary => BindBinaryExpression(binary),
        AssignmentExpressionSyntax assignment => BindAssignmentExpression(assignment),
        CallExpressionSyntax call => BindCallExpression(call),
        _ => throw new InvalidOperationException($"Unexpected expression syntax '{syntax.Kind}'."),
    };

    private static BoundExpression BindLiteralExpression(LiteralExpressionSyntax syntax)
    {
        SyntaxToken token = syntax.LiteralToken;
        return token.Kind switch
        {
            SyntaxKind.IntegerLiteralToken when token.Value is ulong value && value <= int.MaxValue =>
                new BoundLiteralExpression((int)value, BuiltinTypes.Int),
            SyntaxKind.IntegerLiteralToken when token.Value is ulong value && value <= long.MaxValue =>
                new BoundLiteralExpression((long)value, BuiltinTypes.Long),
            SyntaxKind.IntegerLiteralToken => new BoundLiteralExpression(token.Value, BuiltinTypes.ULong),
            SyntaxKind.FloatingPointLiteralToken when token.Value is float =>
                new BoundLiteralExpression(token.Value, BuiltinTypes.Float),
            SyntaxKind.FloatingPointLiteralToken => new BoundLiteralExpression(token.Value, BuiltinTypes.Double),
            SyntaxKind.StringLiteralToken =>
                new BoundLiteralExpression(token.Value, BuiltinTypes.PointerTo(BuiltinTypes.Byte, isConst: true)),
            SyntaxKind.TrueKeyword => new BoundLiteralExpression(true, BuiltinTypes.Bool),
            SyntaxKind.FalseKeyword => new BoundLiteralExpression(false, BuiltinTypes.Bool),
            SyntaxKind.NullKeyword => new BoundLiteralExpression(null, BuiltinTypes.Null),
            _ => new BoundErrorExpression(),
        };
    }

    private BoundExpression BindNameExpression(NameExpressionSyntax syntax)
    {
        VariableSymbol? variable = _scope.Lookup(syntax.IdentifierToken.Text);
        if (variable is not null)
        {
            return new BoundVariableExpression(variable);
        }

        _diagnostics.Report(syntax.IdentifierToken.Location, $"unknown identifier '{syntax.IdentifierToken.Text}'");
        return new BoundErrorExpression();
    }

    private BoundExpression BindUnaryExpression(UnaryExpressionSyntax syntax)
    {
        BoundExpression operand = BindExpression(syntax.Operand);
        TypeSymbol? resultType = syntax.OperatorToken.Kind switch
        {
            SyntaxKind.PlusToken or SyntaxKind.MinusToken when TypeFacts.IsNumeric(operand.Type) => operand.Type,
            SyntaxKind.BangToken when ReferenceEquals(operand.Type, BuiltinTypes.Bool) => BuiltinTypes.Bool,
            SyntaxKind.TildeToken when TypeFacts.IsInteger(operand.Type) => operand.Type,
            SyntaxKind.StarToken when operand.Type is PointerTypeSymbol pointer => pointer.ElementType,
            SyntaxKind.AmpersandToken when operand is BoundVariableExpression variable =>
                BuiltinTypes.PointerTo(variable.Type),
            SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken
                when operand is BoundVariableExpression && TypeFacts.IsNumeric(operand.Type) => operand.Type,
            _ => null,
        };

        if (resultType is null)
        {
            if (!ReferenceEquals(operand.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"unary operator '{syntax.OperatorToken.Text}' is not defined for type '{operand.Type.Name}'");
            }

            return new BoundErrorExpression();
        }

        return new BoundUnaryExpression(syntax.OperatorToken.Kind, operand, resultType);
    }

    private BoundExpression BindBinaryExpression(BinaryExpressionSyntax syntax)
    {
        BoundExpression left = BindExpression(syntax.Left);
        BoundExpression right = BindExpression(syntax.Right);
        TypeSymbol? resultType = GetBinaryResultType(left.Type, syntax.OperatorToken.Kind, right.Type);

        if (resultType is null)
        {
            if (!ReferenceEquals(left.Type, BuiltinTypes.Error) && !ReferenceEquals(right.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"binary operator '{syntax.OperatorToken.Text}' is not defined for types '{left.Type.Name}' and '{right.Type.Name}'");
            }

            return new BoundErrorExpression();
        }

        return new BoundBinaryExpression(left, syntax.OperatorToken.Kind, right, resultType);
    }

    private BoundExpression BindAssignmentExpression(AssignmentExpressionSyntax syntax)
    {
        if (syntax.Target is not NameExpressionSyntax name)
        {
            _diagnostics.Report(GetLocation(syntax.Target), "left side of assignment must be a variable");
            BindExpression(syntax.Expression);
            return new BoundErrorExpression();
        }

        VariableSymbol? variable = _scope.Lookup(name.IdentifierToken.Text);
        BoundExpression expression = BindExpression(syntax.Expression);
        if (variable is null)
        {
            _diagnostics.Report(name.IdentifierToken.Location, $"unknown identifier '{name.IdentifierToken.Text}'");
            return new BoundErrorExpression();
        }

        if (syntax.OperatorToken.Kind == SyntaxKind.EqualsToken)
        {
            if (!TypeFacts.CanAssign(variable.Type, expression.Type))
            {
                ReportCannotConvert(GetLocation(syntax.Expression), expression.Type, variable.Type);
            }
        }
        else
        {
            SyntaxKind binaryOperator = GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind);
            TypeSymbol? resultType = GetBinaryResultType(variable.Type, binaryOperator, expression.Type);
            if (!ReferenceEquals(resultType, variable.Type))
            {
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"operator '{syntax.OperatorToken.Text}' is not defined for types '{variable.Type.Name}' and '{expression.Type.Name}'");
            }
        }

        return new BoundAssignmentExpression(variable, syntax.OperatorToken.Kind, expression);
    }

    private BoundExpression BindCallExpression(CallExpressionSyntax syntax)
    {
        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        if (syntax.Target is not NameExpressionSyntax name)
        {
            _diagnostics.Report(GetLocation(syntax.Target), "call target must be a function name");
            return new BoundErrorExpression();
        }

        FunctionSymbol? function = _function.ContainingNamespace.FindFunction(name.IdentifierToken.Text);
        if (function is null)
        {
            _diagnostics.Report(name.IdentifierToken.Location, $"unknown function '{name.IdentifierToken.Text}'");
            return new BoundErrorExpression();
        }

        if (arguments.Length != function.Parameters.Length)
        {
            _diagnostics.Report(
                name.IdentifierToken.Location,
                $"function '{function.Name}' expects {function.Parameters.Length} argument(s), but {arguments.Length} were provided");
        }

        int count = Math.Min(arguments.Length, function.Parameters.Length);
        for (int index = 0; index < count; index++)
        {
            if (!TypeFacts.CanAssign(function.Parameters[index].Type, arguments[index].Type))
            {
                ReportCannotConvert(GetLocation(syntax.Arguments[index]), arguments[index].Type, function.Parameters[index].Type);
            }
        }

        return new BoundCallExpression(function, arguments);
    }

    private static TypeSymbol? GetBinaryResultType(TypeSymbol left, SyntaxKind operatorKind, TypeSymbol right)
    {
        if (ReferenceEquals(left, BuiltinTypes.Error) || ReferenceEquals(right, BuiltinTypes.Error))
        {
            return BuiltinTypes.Error;
        }

        bool sameType = ReferenceEquals(left, right);
        if (operatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or
            SyntaxKind.SlashToken or SyntaxKind.PercentToken)
        {
            if (sameType && TypeFacts.IsNumeric(left))
            {
                return left;
            }

            if (left is PointerTypeSymbol && TypeFacts.IsInteger(right) && operatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken)
            {
                return left;
            }

            if (right is PointerTypeSymbol && TypeFacts.IsInteger(left) && operatorKind == SyntaxKind.PlusToken)
            {
                return right;
            }

            if (left is PointerTypeSymbol leftPointer && right is PointerTypeSymbol rightPointer &&
                ReferenceEquals(leftPointer.ElementType, rightPointer.ElementType) && operatorKind == SyntaxKind.MinusToken)
            {
                return BuiltinTypes.NInt;
            }
        }

        if (operatorKind is SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or
            SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken && sameType && TypeFacts.IsNumeric(left))
        {
            return BuiltinTypes.Bool;
        }

        if (operatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
        {
            if (sameType || left is PointerTypeSymbol && ReferenceEquals(right, BuiltinTypes.Null) ||
                right is PointerTypeSymbol && ReferenceEquals(left, BuiltinTypes.Null))
            {
                return BuiltinTypes.Bool;
            }
        }

        if (operatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken &&
            ReferenceEquals(left, BuiltinTypes.Bool) && ReferenceEquals(right, BuiltinTypes.Bool))
        {
            return BuiltinTypes.Bool;
        }

        if (operatorKind is SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.CaretToken &&
            sameType && TypeFacts.IsInteger(left))
        {
            return left;
        }

        if (operatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken &&
            TypeFacts.IsInteger(left) && TypeFacts.IsInteger(right))
        {
            return left;
        }

        return null;
    }

    private static SyntaxKind GetBinaryOperatorForCompoundAssignment(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusEqualsToken => SyntaxKind.PlusToken,
        SyntaxKind.MinusEqualsToken => SyntaxKind.MinusToken,
        SyntaxKind.StarEqualsToken => SyntaxKind.StarToken,
        SyntaxKind.SlashEqualsToken => SyntaxKind.SlashToken,
        SyntaxKind.PercentEqualsToken => SyntaxKind.PercentToken,
        SyntaxKind.AmpersandEqualsToken => SyntaxKind.AmpersandToken,
        SyntaxKind.PipeEqualsToken => SyntaxKind.PipeToken,
        SyntaxKind.CaretEqualsToken => SyntaxKind.CaretToken,
        SyntaxKind.LessLessEqualsToken => SyntaxKind.LessLessToken,
        SyntaxKind.GreaterGreaterEqualsToken => SyntaxKind.GreaterGreaterToken,
        _ => SyntaxKind.BadToken,
    };

    private void ReportCannotConvert(TextLocation location, TypeSymbol source, TypeSymbol destination) =>
        _diagnostics.Report(location, $"cannot implicitly convert '{source.Name}' to '{destination.Name}'");

    private static TextLocation GetLocation(ExpressionSyntax syntax) => syntax switch
    {
        LiteralExpressionSyntax literal => literal.LiteralToken.Location,
        NameExpressionSyntax name => name.IdentifierToken.Location,
        ParenthesizedExpressionSyntax parenthesized => parenthesized.OpenParenthesisToken.Location,
        UnaryExpressionSyntax unary => unary.OperatorToken.Location,
        BinaryExpressionSyntax binary => binary.OperatorToken.Location,
        AssignmentExpressionSyntax assignment => assignment.OperatorToken.Location,
        CallExpressionSyntax call => call.OpenParenthesisToken.Location,
        _ => throw new InvalidOperationException($"Unexpected expression syntax '{syntax.Kind}'."),
    };

    private static bool AlwaysReturns(BoundBlockStatement block)
    {
        if (block.Statements.Length == 0)
        {
            return false;
        }

        return block.Statements[^1] switch
        {
            BoundReturnStatement => true,
            BoundBlockStatement nested => AlwaysReturns(nested),
            _ => false,
        };
    }
}
