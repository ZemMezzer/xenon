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
    private readonly HashSet<LocalVariableSymbol> _definitelyAssigned = [];
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
        HashSet<LocalVariableSymbol> afterCondition = CloneDefinitelyAssigned();

        RestoreDefinitelyAssigned(afterCondition);
        BoundStatement thenStatement = BindEmbeddedStatement(syntax.ThenStatement);
        HashSet<LocalVariableSymbol> afterThen = CloneDefinitelyAssigned();

        RestoreDefinitelyAssigned(afterCondition);
        BoundStatement? elseStatement = syntax.ElseStatement is null
            ? null
            : BindEmbeddedStatement(syntax.ElseStatement);
        HashSet<LocalVariableSymbol> afterElse = syntax.ElseStatement is null
            ? afterCondition
            : CloneDefinitelyAssigned();

        if (condition is BoundLiteralExpression { Value: bool constantCondition })
        {
            RestoreDefinitelyAssigned(constantCondition ? afterThen : afterElse);
        }
        else if (AlwaysReturns(thenStatement) && (elseStatement is null || !AlwaysReturns(elseStatement)))
        {
            RestoreDefinitelyAssigned(afterElse);
        }
        else if (elseStatement is not null && AlwaysReturns(elseStatement) && !AlwaysReturns(thenStatement))
        {
            RestoreDefinitelyAssigned(afterThen);
        }
        else
        {
            afterThen.IntersectWith(afterElse);
            RestoreDefinitelyAssigned(afterThen);
        }

        return new BoundIfStatement(condition, thenStatement, elseStatement);
    }

    private BoundWhileStatement BindWhileStatement(WhileStatementSyntax syntax)
    {
        BoundExpression condition = BindBooleanCondition(syntax.Condition);
        HashSet<LocalVariableSymbol> afterCondition = CloneDefinitelyAssigned();
        _loopDepth++;
        BoundStatement body = BindEmbeddedStatement(syntax.Body);
        _loopDepth--;
        RestoreDefinitelyAssigned(afterCondition);
        return new BoundWhileStatement(condition, body);
    }

    private BoundForStatement BindForStatement(ForStatementSyntax syntax)
    {
        BoundScope previous = _scope;
        _scope = new BoundScope(previous);

        BoundStatement? initializer = syntax.Initializer is null ? null : BindStatement(syntax.Initializer);
        BoundExpression? condition = syntax.Condition is null ? null : BindBooleanCondition(syntax.Condition);
        HashSet<LocalVariableSymbol> afterCondition = CloneDefinitelyAssigned();

        _loopDepth++;
        BoundStatement body = BindEmbeddedStatement(syntax.Body);
        RestoreDefinitelyAssigned(afterCondition);
        BoundExpression? increment = syntax.Increment is null ? null : BindExpression(syntax.Increment);
        _loopDepth--;

        RestoreDefinitelyAssigned(afterCondition);
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

        var variable = new LocalVariableSymbol(syntax.IdentifierToken.Text, type);
        bool declared = _scope.TryDeclare(variable);
        if (!declared)
        {
            _diagnostics.Report(
                syntax.IdentifierToken.Location,
                $"variable '{variable.Name}' is already declared in this scope");
        }

        BoundExpression? initializer = syntax.Initializer is null ? null : BindExpression(syntax.Initializer);
        if (initializer is not null)
        {
            initializer = ContextualizeNull(initializer, type);
        }

        if (initializer is not null && !TypeFacts.CanAssign(type, initializer.Type))
        {
            ReportCannotConvert(GetLocation(syntax.Initializer!), initializer.Type, type);
        }

        if (type is ArrayTypeSymbol && initializer is not null)
        {
            variable.ArrayStorage = GetArrayStorage(initializer);
        }

        if (declared && initializer is not null)
        {
            _definitelyAssigned.Add(variable);
        }

        return new BoundVariableDeclarationStatement(variable, initializer);
    }

    private BoundReturnStatement BindReturnStatement(ReturnStatementSyntax syntax)
    {
        BoundExpression? expression = syntax.Expression is null ? null : BindExpression(syntax.Expression);
        if (expression is not null)
        {
            expression = ContextualizeNull(expression, _function.ReturnType);
        }

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

        if (expression is not null && GetArrayStorage(expression) == ArrayStorageKind.Stack)
        {
            _diagnostics.Report(GetLocation(syntax.Expression!), "stack array cannot be returned from a function");
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
        IndexExpressionSyntax index => BindIndexExpression(index),
        StructPositionalConstructionExpressionSyntax construction => BindStructPositionalConstructionExpression(construction),
        StackArrayCreationExpressionSyntax stackArray => BindStackArrayCreationExpression(stackArray),
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

    private BoundExpression BindNameExpression(NameExpressionSyntax syntax, bool requireDefinitelyAssigned = true)
    {
        VariableSymbol? variable = _scope.Lookup(syntax.IdentifierToken.Text);
        if (variable is not null)
        {
            if (requireDefinitelyAssigned &&
                variable is LocalVariableSymbol local &&
                !_definitelyAssigned.Contains(local))
            {
                _diagnostics.Report(
                    syntax.IdentifierToken.Location,
                    $"local variable '{local.Name}' is used before it is initialized");
            }

            return new BoundVariableExpression(variable);
        }

        if (_function.ContainingType is StructTypeSymbol containingType)
        {
            FieldSymbol? field = containingType.FindField(syntax.IdentifierToken.Text);
            if (field is not null)
            {
                PointerTypeSymbol thisType = BuiltinTypes.PointerTo(containingType);
                return new BoundMemberAccessExpression(
                    new BoundThisExpression(containingType, thisType),
                    field,
                    IsPointerAccess: true);
            }
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

        if (syntax.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
        {
            if (left.Type is PointerTypeSymbol)
            {
                right = ContextualizeNull(right, left.Type);
            }
            else if (right.Type is PointerTypeSymbol)
            {
                left = ContextualizeNull(left, right.Type);
            }
        }

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
        bool isSimpleAssignment = syntax.OperatorToken.Kind == SyntaxKind.EqualsToken;
        BoundExpression target = isSimpleAssignment && syntax.Target is NameExpressionSyntax name
            ? BindNameExpression(name, requireDefinitelyAssigned: false)
            : BindExpression(syntax.Target);
        BoundExpression expression = BindExpression(syntax.Expression);
        if (isSimpleAssignment)
        {
            expression = ContextualizeNull(expression, target.Type);
        }

        if (!IsWritable(target))
        {
            if (!ReferenceEquals(target.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(GetLocation(syntax.Target), "left side of assignment must be writable");
            }

            return new BoundErrorExpression();
        }

        if (isSimpleAssignment)
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

        if (isSimpleAssignment && target.Type is ArrayTypeSymbol)
        {
            ArrayStorageKind storage = GetArrayStorage(expression);
            if (target is BoundVariableExpression { Variable: LocalVariableSymbol local })
            {
                local.ArrayStorage = storage;
            }
            else if (storage == ArrayStorageKind.Stack)
            {
                _diagnostics.Report(GetLocation(syntax.Expression), "stack array cannot escape through this assignment");
            }
        }

        if (isSimpleAssignment && target is BoundVariableExpression { Variable: LocalVariableSymbol assignedLocal })
        {
            _definitelyAssigned.Add(assignedLocal);
        }

        return new BoundAssignmentExpression(target, syntax.OperatorToken.Kind, expression);
    }

    private HashSet<LocalVariableSymbol> CloneDefinitelyAssigned() => [.. _definitelyAssigned];

    private void RestoreDefinitelyAssigned(IEnumerable<LocalVariableSymbol> variables)
    {
        _definitelyAssigned.Clear();
        _definitelyAssigned.UnionWith(variables);
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

        if (!field.IsPublic && !ReferenceEquals(_function.ContainingType, structType))
        {
            _diagnostics.Report(
                syntax.MemberToken.Location,
                $"field '{field.Name}' is private in struct '{structType.Name}'");
        }

        return new BoundMemberAccessExpression(receiver, field, pointerAccess);
    }

    private BoundExpression BindIndexExpression(IndexExpressionSyntax syntax)
    {
        if (syntax.Receiver is NameExpressionSyntax typeName &&
            _scope.Lookup(typeName.IdentifierToken.Text) is null &&
            _function.ContainingNamespace.FindType(typeName.IdentifierToken.Text) is TypeSymbol arrayElementType)
        {
            return BindStackArrayCreation(arrayElementType, syntax.Index, typeName.IdentifierToken.Location);
        }

        BoundExpression receiver = BindExpression(syntax.Receiver);
        BoundExpression index = BindExpression(syntax.Index);

        if (!TypeFacts.IsInteger(index.Type) && !ReferenceEquals(index.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(GetLocation(syntax.Index), $"array index must be an integer, but has type '{index.Type.Name}'");
        }

        TypeSymbol? elementType = receiver.Type switch
        {
            ArrayTypeSymbol array => array.ElementType,
            PointerTypeSymbol pointer => pointer.ElementType,
            _ => null,
        };

        if (elementType is null)
        {
            if (!ReferenceEquals(receiver.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.OpenBracketToken.Location, $"type '{receiver.Type.Name}' cannot be indexed");
            }

            return new BoundErrorExpression();
        }

        return new BoundIndexExpression(receiver, index, elementType);
    }

    private BoundExpression BindStructPositionalConstructionExpression(StructPositionalConstructionExpressionSyntax syntax)
    {
        StructTypeSymbol? structType = _function.ContainingNamespace.FindType(syntax.TypeNameToken.Text);
        if (structType is null)
        {
            _diagnostics.Report(syntax.TypeNameToken.Location, $"unknown struct '{syntax.TypeNameToken.Text}'");
            return new BoundErrorExpression();
        }

        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        arguments = ValidatePositionalArguments(structType, arguments, syntax.Arguments, syntax.TypeNameToken.Location);
        return new BoundStructConstructionExpression(structType, arguments);
    }

    private BoundExpression BindStackArrayCreationExpression(StackArrayCreationExpressionSyntax syntax)
    {
        TypeSymbol elementType = TypeResolver.Resolve(syntax.ElementType, _function.ContainingNamespace, _diagnostics);
        return BindStackArrayCreation(elementType, syntax.Length, syntax.ElementType.NameToken.Location);
    }

    private BoundExpression BindStackArrayCreation(
        TypeSymbol elementType,
        ExpressionSyntax lengthSyntax,
        TextLocation elementLocation)
    {
        ValidateArrayElementType(elementType, elementLocation);
        BoundExpression length = BindExpression(lengthSyntax);
        ValidateArrayLength(length, lengthSyntax);
        ArrayTypeSymbol arrayType = BuiltinTypes.ArrayOf(elementType);
        return new BoundArrayCreationExpression(elementType, length, arrayType, ArrayStorageKind.Stack);
    }

    private BoundExpression BindCallExpression(CallExpressionSyntax syntax)
    {
        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();

        if (syntax.Target is MemberAccessExpressionSyntax memberTarget)
        {
            return BindMethodCallExpression(memberTarget, arguments, syntax.Arguments);
        }

        if (syntax.Target is not NameExpressionSyntax name)
        {
            _diagnostics.Report(GetLocation(syntax.Target), "call target must be a function, method, or struct name");
            return new BoundErrorExpression();
        }

        StructTypeSymbol? structType = _function.ContainingNamespace.FindType(name.IdentifierToken.Text);
        if (structType is not null)
        {
            FunctionSymbol? constructor = structType.Constructor;
            if (constructor is null)
            {
                _diagnostics.Report(
                    name.IdentifierToken.Location,
                    $"struct '{structType.Name}' does not declare a constructor; use '{structType.Name} {{ ... }}' for positional construction");
                return new BoundErrorExpression();
            }

            if (!constructor.IsPublic && !ReferenceEquals(_function.ContainingType, structType))
            {
                _diagnostics.Report(name.IdentifierToken.Location, $"constructor '{structType.Name}' is private");
            }

            arguments = ValidateFunctionArguments(constructor, arguments, syntax.Arguments, name.IdentifierToken.Location);
            return new BoundConstructorCallExpression(structType, constructor, arguments);
        }

        if (_function.ContainingType is StructTypeSymbol containingType)
        {
            FunctionSymbol? method = containingType.FindMethod(name.IdentifierToken.Text);
            if (method is not null)
            {
                arguments = ValidateFunctionArguments(method, arguments, syntax.Arguments, name.IdentifierToken.Location);
                PointerTypeSymbol thisType = BuiltinTypes.PointerTo(containingType);
                return new BoundMethodCallExpression(
                    new BoundThisExpression(containingType, thisType),
                    method,
                    arguments,
                    IsPointerAccess: true);
            }
        }

        FunctionSymbol? function = _function.ContainingNamespace.FindFunction(name.IdentifierToken.Text);
        if (function is null)
        {
            _diagnostics.Report(name.IdentifierToken.Location, $"unknown function '{name.IdentifierToken.Text}'");
            return new BoundErrorExpression();
        }

        arguments = ValidateFunctionArguments(function, arguments, syntax.Arguments, name.IdentifierToken.Location);
        return new BoundCallExpression(function, arguments);
    }

    private BoundExpression BindMethodCallExpression(
        MemberAccessExpressionSyntax target,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax)
    {
        BoundExpression receiver = BindExpression(target.Receiver);
        bool pointerAccess = target.OperatorToken.Kind == SyntaxKind.ArrowToken;
        StructTypeSymbol? structType = pointerAccess
            ? (receiver.Type as PointerTypeSymbol)?.ElementType as StructTypeSymbol
            : receiver.Type as StructTypeSymbol;

        if (structType is null)
        {
            if (!ReferenceEquals(receiver.Type, BuiltinTypes.Error))
            {
                string expected = pointerAccess ? "pointer to struct" : "struct";
                _diagnostics.Report(
                    target.OperatorToken.Location,
                    $"operator '{target.OperatorToken.Text}' requires a {expected}, but has type '{receiver.Type.Name}'");
            }

            return new BoundErrorExpression();
        }

        FunctionSymbol? method = structType.FindMethod(target.MemberToken.Text);
        if (method is null)
        {
            _diagnostics.Report(
                target.MemberToken.Location,
                $"struct '{structType.Name}' does not contain method '{target.MemberToken.Text}'");
            return new BoundErrorExpression();
        }

        if (!method.IsPublic && !ReferenceEquals(_function.ContainingType, structType))
        {
            _diagnostics.Report(
                target.MemberToken.Location,
                $"method '{method.Name}' is private in struct '{structType.Name}'");
        }

        if (pointerAccess && receiver.Type is PointerTypeSymbol { IsConst: true })
        {
            _diagnostics.Report(
                target.MemberToken.Location,
                $"method '{method.Name}' cannot be called through 'const {structType.Name}*' because readonly methods are not supported yet");
        }
        else if (!pointerAccess && IsAddressable(receiver) && !IsWritable(receiver))
        {
            _diagnostics.Report(
                target.MemberToken.Location,
                $"method '{method.Name}' cannot be called on a readonly '{structType.Name}' value because readonly methods are not supported yet");
        }

        arguments = ValidateFunctionArguments(method, arguments, argumentSyntax, target.MemberToken.Location);
        return new BoundMethodCallExpression(receiver, method, arguments, pointerAccess);
    }

    private BoundExpression BindNewExpression(NewExpressionSyntax syntax)
    {
        TypeSymbol type = TypeResolver.Resolve(syntax.Type, _function.ContainingNamespace, _diagnostics);

        if (syntax.IsArrayAllocation)
        {
            ValidateArrayElementType(type, syntax.Type.NameToken.Location);

            BoundExpression length = syntax.Arguments.Length == 0
                ? new BoundErrorExpression()
                : BindExpression(syntax.Arguments[0]);
            if (syntax.Arguments.Length != 1)
            {
                _diagnostics.Report(syntax.OpenDelimiterToken.Location, "array allocation requires exactly one length expression");
            }
            else
            {
                ValidateArrayLength(length, syntax.Arguments[0]);
            }

            ArrayTypeSymbol arrayType = BuiltinTypes.ArrayOf(type);
            return new BoundArrayCreationExpression(type, length, arrayType, ArrayStorageKind.Heap);
        }

        if (type is not StructTypeSymbol structType)
        {
            if (!ReferenceEquals(type, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, $"'new' requires a struct type or array element type, but has type '{type.Name}'");
            }

            return new BoundErrorExpression();
        }

        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        FunctionSymbol? constructor = null;
        if (syntax.IsPositionalInitialization)
        {
            arguments = ValidatePositionalArguments(structType, arguments, syntax.Arguments, syntax.NewKeyword.Location);
        }
        else
        {
            constructor = structType.Constructor;
            if (constructor is null)
            {
                _diagnostics.Report(
                    syntax.Type.NameToken.Location,
                    $"struct '{structType.Name}' does not declare a constructor; use 'new {structType.Name} {{ ... }}' for positional construction");
                return new BoundErrorExpression();
            }

            if (!constructor.IsPublic && !ReferenceEquals(_function.ContainingType, structType))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, $"constructor '{structType.Name}' is private");
            }

            arguments = ValidateFunctionArguments(constructor, arguments, syntax.Arguments, syntax.NewKeyword.Location);
        }

        PointerTypeSymbol pointerType = BuiltinTypes.PointerTo(structType);
        return new BoundNewExpression(structType, constructor, arguments, syntax.IsPositionalInitialization, pointerType);
    }

    private BoundExpression BindFreeExpression(FreeExpressionSyntax syntax)
    {
        BoundExpression pointer = BindExpression(syntax.Pointer);
        if (GetArrayStorage(pointer) == ArrayStorageKind.Stack)
        {
            _diagnostics.Report(syntax.FreeKeyword.Location, "stack array cannot be freed");
            return new BoundErrorExpression();
        }

        FunctionSymbol? destructor = null;
        if (pointer.Type is PointerTypeSymbol pointerType)
        {
            if (pointerType.ElementType is StructTypeSymbol structType)
            {
                destructor = structType.Destructor;
            }
        }
        else if (pointer.Type is ArrayTypeSymbol arrayType)
        {
            if (arrayType.ElementType is StructTypeSymbol { Destructor: not null })
            {
                _diagnostics.Report(
                    syntax.FreeKeyword.Location,
                    "freeing arrays of structs with destructors is not supported yet because T[] does not carry an element count");
            }
        }
        else if (!ReferenceEquals(pointer.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(
                syntax.FreeKeyword.Location,
                $"'free' requires a heap pointer or heap array, but has type '{pointer.Type.Name}'");
            return new BoundErrorExpression();
        }

        return new BoundFreeExpression(pointer, destructor);
    }

    private ImmutableArray<BoundExpression> ValidatePositionalArguments(
        StructTypeSymbol structType,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location)
    {
        if (arguments.Length != structType.Fields.Length)
        {
            _diagnostics.Report(
                location,
                $"struct '{structType.Name}' expects {structType.Fields.Length} positional value(s), but {arguments.Length} were provided");
        }

        var convertedArguments = arguments.ToBuilder();
        int count = Math.Min(arguments.Length, structType.Fields.Length);
        for (int index = 0; index < count; index++)
        {
            TypeSymbol fieldType = structType.Fields[index].Type;
            BoundExpression argument = ContextualizeNull(arguments[index], fieldType);
            convertedArguments[index] = argument;

            if (GetArrayStorage(argument) == ArrayStorageKind.Stack)
            {
                _diagnostics.Report(
                    GetLocation(argumentSyntax[index]),
                    "stack array cannot be stored inside a positional struct value");
            }

            if (!TypeFacts.CanAssign(fieldType, argument.Type))
            {
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, fieldType);
            }
        }

        return convertedArguments.ToImmutable();
    }

    private ImmutableArray<BoundExpression> ValidateFunctionArguments(
        FunctionSymbol function,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location)
    {
        if (arguments.Length != function.Parameters.Length)
        {
            _diagnostics.Report(
                location,
                $"function '{function.Name}' expects {function.Parameters.Length} argument(s), but {arguments.Length} were provided");
        }

        var convertedArguments = arguments.ToBuilder();
        int count = Math.Min(arguments.Length, function.Parameters.Length);
        for (int index = 0; index < count; index++)
        {
            TypeSymbol parameterType = function.Parameters[index].Type;
            BoundExpression argument = ContextualizeNull(arguments[index], parameterType);
            convertedArguments[index] = argument;

            if (GetArrayStorage(argument) == ArrayStorageKind.Stack)
            {
                _diagnostics.Report(GetLocation(argumentSyntax[index]), "stack array cannot be passed to another function");
            }

            if (!TypeFacts.CanAssign(parameterType, argument.Type))
            {
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, parameterType);
            }
        }

        return convertedArguments.ToImmutable();
    }

    private static BoundExpression ContextualizeNull(BoundExpression expression, TypeSymbol targetType)
    {
        if (expression is BoundLiteralExpression { Value: null } &&
            ReferenceEquals(expression.Type, BuiltinTypes.Null) &&
            targetType is PointerTypeSymbol pointerType)
        {
            return new BoundLiteralExpression(null, pointerType);
        }

        return expression;
    }

    private void ValidateArrayElementType(TypeSymbol elementType, TextLocation location)
    {
        if (ReferenceEquals(elementType, BuiltinTypes.Void))
        {
            _diagnostics.Report(location, "array element type cannot be 'void'");
        }

        if (elementType is StructTypeSymbol { Destructor: not null } structType)
        {
            _diagnostics.Report(
                location,
                $"arrays of struct '{structType.Name}' are not supported while the element type declares a destructor");
        }
    }

    private void ValidateArrayLength(BoundExpression length, ExpressionSyntax syntax)
    {
        if (!TypeFacts.IsInteger(length.Type) && !ReferenceEquals(length.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(GetLocation(syntax), $"array length must be an integer, but has type '{length.Type.Name}'");
        }

        if (length is BoundLiteralExpression { Value: int intValue } && intValue < 0)
        {
            _diagnostics.Report(GetLocation(syntax), "array length cannot be negative");
        }
        else if (length is BoundLiteralExpression { Value: long longValue } && longValue < 0)
        {
            _diagnostics.Report(GetLocation(syntax), "array length cannot be negative");
        }
    }

    private static ArrayStorageKind GetArrayStorage(BoundExpression expression) => expression switch
    {
        BoundArrayCreationExpression array => array.Storage,
        BoundVariableExpression { Variable: LocalVariableSymbol local } => local.ArrayStorage,
        _ => ArrayStorageKind.Unknown,
    };

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
        IndexExpressionSyntax index => index.OpenBracketToken.Location,
        StructPositionalConstructionExpressionSyntax construction => construction.TypeNameToken.Location,
        StackArrayCreationExpressionSyntax stackArray => stackArray.OpenBracketToken.Location,
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
        BoundIndexExpression => true,
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
            BoundMemberAccessExpression { IsPointerAccess: true } => true,
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
