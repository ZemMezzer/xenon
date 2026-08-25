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
    private int _loopDepth;

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
        IfStatementSyntax @if => BindIfStatement(@if),
        WhileStatementSyntax @while => BindWhileStatement(@while),
        ForStatementSyntax @for => BindForStatement(@for),
        BreakStatementSyntax @break => BindBreakStatement(@break),
        ContinueStatementSyntax @continue => BindContinueStatement(@continue),
        _ => throw new InvalidOperationException($"Unexpected statement syntax '{syntax.Kind}'."),
    };

    private BoundIfStatement BindIfStatement(IfStatementSyntax syntax)
    {
        BoundExpression condition = BindBooleanCondition(syntax.Condition);
        BoundStatement thenStatement = BindEmbeddedStatement(syntax.ThenStatement);
        BoundStatement? elseStatement = syntax.ElseStatement is null
            ? null
            : BindEmbeddedStatement(syntax.ElseStatement);
        return new BoundIfStatement(condition, thenStatement, elseStatement);
    }

    private BoundWhileStatement BindWhileStatement(WhileStatementSyntax syntax)
    {
        BoundExpression condition = BindBooleanCondition(syntax.Condition);
        _loopDepth++;
        BoundStatement body = BindEmbeddedStatement(syntax.Body);
        _loopDepth--;
        return new BoundWhileStatement(condition, body);
    }

    private BoundForStatement BindForStatement(ForStatementSyntax syntax)
    {
        BoundScope previous = _scope;
        _scope = new BoundScope(previous);

        BoundStatement? initializer = syntax.Initializer is null ? null : BindStatement(syntax.Initializer);
        BoundExpression? condition = syntax.Condition is null ? null : BindBooleanCondition(syntax.Condition);
        BoundExpression? increment = syntax.Increment is null ? null : BindExpression(syntax.Increment);

        _loopDepth++;
        BoundStatement body = BindEmbeddedStatement(syntax.Body);
        _loopDepth--;

        _scope = previous;
        return new BoundForStatement(initializer, condition, increment, body);
    }

    private BoundBreakStatement BindBreakStatement(BreakStatementSyntax syntax)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Report(syntax.BreakKeyword.Location, "'break' can only be used inside a loop");
        }

        return new BoundBreakStatement();
    }

    private BoundContinueStatement BindContinueStatement(ContinueStatementSyntax syntax)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Report(syntax.ContinueKeyword.Location, "'continue' can only be used inside a loop");
        }

        return new BoundContinueStatement();
    }

    private BoundStatement BindEmbeddedStatement(StatementSyntax syntax)
    {
        if (syntax is BlockStatementSyntax)
        {
            return BindStatement(syntax);
        }

        BoundScope previous = _scope;
        _scope = new BoundScope(previous);
        BoundStatement statement = BindStatement(syntax);
        _scope = previous;
        return statement;
    }

    private BoundExpression BindBooleanCondition(ExpressionSyntax syntax)
    {
        BoundExpression condition = BindExpression(syntax);
        if (!ReferenceEquals(condition.Type, BuiltinTypes.Bool) && !ReferenceEquals(condition.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(GetLocation(syntax), $"condition must have type 'bool', but has type '{condition.Type.Name}'");
        }

        return condition;
    }

    private BoundVariableDeclarationStatement BindVariableDeclaration(VariableDeclarationStatementSyntax syntax)
    {
        TypeSymbol type = TypeResolver.Resolve(syntax.Type, _function.ContainingNamespace, _diagnostics);
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
        PostfixUnaryExpressionSyntax postfix => BindPostfixUnaryExpression(postfix),
        BinaryExpressionSyntax binary => BindBinaryExpression(binary),
        AssignmentExpressionSyntax assignment => BindAssignmentExpression(assignment),
        CallExpressionSyntax call => BindCallExpression(call),
        MemberAccessExpressionSyntax member => BindMemberAccessExpression(member),
        NewExpressionSyntax @new => BindNewExpression(@new),
        FreeExpressionSyntax free => BindFreeExpression(free),
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
        return BindUnaryExpression(syntax.OperatorToken, operand, isPostfix: false);
    }

    private BoundExpression BindPostfixUnaryExpression(PostfixUnaryExpressionSyntax syntax)
    {
        BoundExpression operand = BindExpression(syntax.Operand);
        return BindUnaryExpression(syntax.OperatorToken, operand, isPostfix: true);
    }

    private BoundExpression BindUnaryExpression(
        SyntaxToken operatorToken,
        BoundExpression operand,
        bool isPostfix)
    {
        TypeSymbol? resultType = operatorToken.Kind switch
        {
            SyntaxKind.PlusToken or SyntaxKind.MinusToken when TypeFacts.IsNumeric(operand.Type) => operand.Type,
            SyntaxKind.BangToken when ReferenceEquals(operand.Type, BuiltinTypes.Bool) => BuiltinTypes.Bool,
            SyntaxKind.TildeToken when TypeFacts.IsInteger(operand.Type) => operand.Type,
            SyntaxKind.StarToken when operand.Type is PointerTypeSymbol pointer => pointer.ElementType,
            SyntaxKind.AmpersandToken when IsAddressable(operand) => BuiltinTypes.PointerTo(operand.Type),
            SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken
                when IsWritable(operand) && TypeFacts.IsNumeric(operand.Type) => operand.Type,
            _ => null,
        };

        if (resultType is null)
        {
            if (!ReferenceEquals(operand.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(
                    operatorToken.Location,
                    $"unary operator '{operatorToken.Text}' is not defined for type '{operand.Type.Name}'");
            }

            return new BoundErrorExpression();
        }

        return new BoundUnaryExpression(operatorToken.Kind, operand, resultType, isPostfix);
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
        BoundExpression target = BindExpression(syntax.Target);
        BoundExpression expression = BindExpression(syntax.Expression);
        if (!IsWritable(target))
        {
            if (!ReferenceEquals(target.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(GetLocation(syntax.Target), "left side of assignment must be writable");
            }

            return new BoundErrorExpression();
        }

        if (syntax.OperatorToken.Kind == SyntaxKind.EqualsToken)
        {
            if (!TypeFacts.CanAssign(target.Type, expression.Type))
            {
                ReportCannotConvert(GetLocation(syntax.Expression), expression.Type, target.Type);
            }
        }
        else
        {
            SyntaxKind binaryOperator = GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind);
            TypeSymbol? resultType = GetBinaryResultType(target.Type, binaryOperator, expression.Type);
            if (!ReferenceEquals(resultType, target.Type))
            {
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"operator '{syntax.OperatorToken.Text}' is not defined for types '{target.Type.Name}' and '{expression.Type.Name}'");
            }
        }

        return new BoundAssignmentExpression(target, syntax.OperatorToken.Kind, expression);
    }

    private BoundExpression BindMemberAccessExpression(MemberAccessExpressionSyntax syntax)
    {
        BoundExpression receiver = BindExpression(syntax.Receiver);
        bool pointerAccess = syntax.OperatorToken.Kind == SyntaxKind.ArrowToken;
        StructTypeSymbol? structType = pointerAccess
            ? (receiver.Type as PointerTypeSymbol)?.ElementType as StructTypeSymbol
            : receiver.Type as StructTypeSymbol;

        if (structType is null)
        {
            if (!ReferenceEquals(receiver.Type, BuiltinTypes.Error))
            {
                string expected = pointerAccess ? "pointer to struct" : "struct";
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"operator '{syntax.OperatorToken.Text}' requires a {expected}, but has type '{receiver.Type.Name}'");
            }

            return new BoundErrorExpression();
        }

        FieldSymbol? field = structType.FindField(syntax.MemberToken.Text);
        if (field is null)
        {
            _diagnostics.Report(
                syntax.MemberToken.Location,
                $"struct '{structType.Name}' does not contain field '{syntax.MemberToken.Text}'");
            return new BoundErrorExpression();
        }

        return new BoundMemberAccessExpression(receiver, field, pointerAccess);
    }

    private BoundExpression BindCallExpression(CallExpressionSyntax syntax)
    {
        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        if (syntax.Target is not NameExpressionSyntax name)
        {
            _diagnostics.Report(GetLocation(syntax.Target), "call target must be a function name");
            return new BoundErrorExpression();
        }

        StructTypeSymbol? structType = _function.ContainingNamespace.FindType(name.IdentifierToken.Text);
        if (structType is not null)
        {
            ValidateStructArguments(structType, arguments, syntax.Arguments, name.IdentifierToken.Location);
            return new BoundStructConstructionExpression(structType, arguments);
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

    private BoundExpression BindNewExpression(NewExpressionSyntax syntax)
    {
        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        TypeSymbol type = TypeResolver.Resolve(
            syntax.Type,
            _function.ContainingNamespace,
            _diagnostics);
        if (type is not StructTypeSymbol structType)
        {
            if (!ReferenceEquals(type, BuiltinTypes.Error))
            {
                _diagnostics.Report(
                    syntax.Type.NameToken.Location,
                    $"'new' requires a struct type, but has type '{type.Name}'");
            }

            return new BoundErrorExpression();
        }

        ValidateStructArguments(structType, arguments, syntax.Arguments, syntax.NewKeyword.Location);
        PointerTypeSymbol pointerType = BuiltinTypes.PointerTo(structType);
        return new BoundNewExpression(structType, arguments, pointerType);
    }

    private BoundExpression BindFreeExpression(FreeExpressionSyntax syntax)
    {
        BoundExpression pointer = BindExpression(syntax.Pointer);
        if (pointer.Type is not PointerTypeSymbol && !ReferenceEquals(pointer.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(
                syntax.FreeKeyword.Location,
                $"'free' requires a pointer, but has type '{pointer.Type.Name}'");
            return new BoundErrorExpression();
        }

        return new BoundFreeExpression(pointer);
    }

    private void ValidateStructArguments(
        StructTypeSymbol structType,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location)
    {
        if (arguments.Length != structType.Fields.Length)
        {
            _diagnostics.Report(
                location,
                $"struct '{structType.Name}' expects {structType.Fields.Length} constructor argument(s), but {arguments.Length} were provided");
        }

        int count = Math.Min(arguments.Length, structType.Fields.Length);
        for (int index = 0; index < count; index++)
        {
            if (!TypeFacts.CanAssign(structType.Fields[index].Type, arguments[index].Type))
            {
                ReportCannotConvert(
                    GetLocation(argumentSyntax[index]),
                    arguments[index].Type,
                    structType.Fields[index].Type);
            }
        }
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
        PostfixUnaryExpressionSyntax postfix => postfix.OperatorToken.Location,
        BinaryExpressionSyntax binary => binary.OperatorToken.Location,
        AssignmentExpressionSyntax assignment => assignment.OperatorToken.Location,
        CallExpressionSyntax call => call.OpenParenthesisToken.Location,
        MemberAccessExpressionSyntax member => member.OperatorToken.Location,
        NewExpressionSyntax @new => @new.NewKeyword.Location,
        FreeExpressionSyntax free => free.FreeKeyword.Location,
        _ => throw new InvalidOperationException($"Unexpected expression syntax '{syntax.Kind}'."),
    };

    private static bool IsAddressable(BoundExpression expression) => expression switch
    {
        BoundVariableExpression => true,
        BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } => true,
        BoundMemberAccessExpression { IsPointerAccess: true } => true,
        BoundMemberAccessExpression member => IsAddressable(member.Receiver),
        _ => false,
    };

    private static bool IsWritable(BoundExpression expression)
    {
        if (!IsAddressable(expression))
        {
            return false;
        }

        return expression switch
        {
            BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken, Operand.Type: PointerTypeSymbol { IsConst: true } } => false,
            BoundMemberAccessExpression
            {
                IsPointerAccess: true,
                Receiver.Type: PointerTypeSymbol { IsConst: true },
            } => false,
            BoundMemberAccessExpression member => IsWritable(member.Receiver),
            _ => true,
        };
    }

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
            BoundIfStatement { ElseStatement: not null } @if =>
                AlwaysReturns(@if.ThenStatement) && AlwaysReturns(@if.ElseStatement),
            _ => false,
        };
    }

    private static bool AlwaysReturns(BoundStatement statement) => statement switch
    {
        BoundReturnStatement => true,
        BoundBlockStatement block => AlwaysReturns(block),
        BoundIfStatement { ElseStatement: not null } @if =>
            AlwaysReturns(@if.ThenStatement) && AlwaysReturns(@if.ElseStatement),
        _ => false,
    };
}
