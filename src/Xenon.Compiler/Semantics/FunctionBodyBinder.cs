using System.Collections.Immutable;
using System.Numerics;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed class FunctionBodyBinder
{
    private readonly FunctionSymbol _function;
    private readonly FileSymbolScope _fileScope;
    private readonly DiagnosticBag _diagnostics;
    private readonly ConstantEvaluationContext _constants;
    private readonly Dictionary<BoundExpression, TextLocation> _expressionLocations = new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyDictionary<BoundExpression, TextLocation> ExpressionLocations => _expressionLocations;
    private readonly HashSet<LocalVariableSymbol> _definitelyAssigned = [];
    private BoundScope _scope = new(null);
    private readonly Dictionary<LocalVariableSymbol, BoundScope> _localScopes = [];
    private readonly Dictionary<LocalVariableSymbol, BoundScope> _stackArrayScopes = [];
    private int _loopDepth;
    private int _switchDepth;
    private readonly Stack<(int LoopDepth, List<HashSet<LocalVariableSymbol>> Exits, List<Dictionary<LocalVariableSymbol, ArrayState>> ArrayExits)> _switchExits = [];
    private bool _bindingBaseConstructorArguments;
    private bool _suppressIntegerOperationDiagnostics;

    public FunctionBodyBinder(FunctionSymbol function, FileSymbolScope fileScope, DiagnosticBag diagnostics, ConstantEvaluationContext constants)
    {
        _function = function;
        _fileScope = fileScope;
        _diagnostics = diagnostics;
        _constants = constants;

        foreach (ParameterSymbol parameter in function.Parameters)
        {
            _scope.TryDeclare(parameter);
        }
    }

    public BoundBlockStatement BindBody(BlockStatementSyntax body)
    {
        BoundStatement? baseConstructorCall = null;

        if (_function.FunctionKind == FunctionKind.Constructor && _function.ContainingType?.BaseType is StructTypeSymbol baseType && !baseType.Constructors.IsEmpty)
        {
            ConstructorDeclarationSyntax? syntax = _function.Declaration as ConstructorDeclarationSyntax;
            ImmutableArray<ExpressionSyntax> baseArguments = syntax?.BaseArguments ?? [];
            TextLocation location = syntax?.IdentifierToken.Location ?? _function.ContainingType.Declaration.IdentifierToken.Location;
            _bindingBaseConstructorArguments = true;
            ImmutableArray<BoundExpression> arguments;
            try
            {
                arguments = baseArguments.Select(BindExpression).ToImmutableArray();
            }
            finally
            {
                _bindingBaseConstructorArguments = false;
            }
            FunctionSymbol? baseConstructor = ResolveConstructor(baseType, arguments, baseArguments, location);
            if (baseConstructor is not null)
            {
                if (!baseConstructor.IsPublic)
                {
                    _diagnostics.Report(syntax?.BaseKeyword?.Location ?? location, $"constructor '{baseType.Name}' is private");
                }
                arguments = ValidateFunctionArguments(baseConstructor, arguments, baseArguments, location);
                baseConstructorCall = new BoundExpressionStatement(new BoundBaseLifecycleCallExpression(baseConstructor, arguments));
            }
        }
        else if (_function.FunctionKind == FunctionKind.Constructor &&
                 _function.ContainingType?.BaseType is StructTypeSymbol baseWithoutConstructor)
        {
            ConstructorDeclarationSyntax? syntax = _function.Declaration as ConstructorDeclarationSyntax;
            if (syntax is not null && !syntax.BaseArguments.IsEmpty)
            {
                _diagnostics.Report(syntax.BaseKeyword?.Location ?? syntax.IdentifierToken.Location, $"base struct '{baseWithoutConstructor.Name}' does not declare a constructor");
            }
        }

        BoundBlockStatement boundBody = BindBlockStatement(body, createScope: false);
        if (_function.FunctionKind == FunctionKind.Constructor &&
            _function.ContainingType is StructTypeSymbol constructedType)
        {
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            if (baseConstructorCall is not null)
                statements.Add(baseConstructorCall);
            else if (constructedType.BaseType is StructTypeSymbol defaultBase)
                AddDefaultInstanceInitializerCalls(defaultBase, statements);
            if (constructedType.InstanceInitializer is FunctionSymbol initializer)
            {
                statements.Add(new BoundExpressionStatement(
                    new BoundBaseLifecycleCallExpression(initializer, [])));
            }
            statements.AddRange(boundBody.Statements);
            boundBody = new BoundBlockStatement(statements.ToImmutable());
        }
        else if (_function.FunctionKind == FunctionKind.Destructor && _function.ContainingType?.BaseType?.FindDestructor() is FunctionSymbol baseDestructor)
        {
            // Locals in the destructor body leave scope before inherited cleanup.
            boundBody = new BoundBlockStatement([boundBody, new BoundExpressionStatement(new BoundBaseLifecycleCallExpression(baseDestructor, []))]);
        }

        if (!ReferenceEquals(_function.ReturnType, BuiltinTypes.Void) && !AlwaysReturns(boundBody))
        {
            _diagnostics.Report(
                body.CloseBraceToken.Location,
                $"not all code paths in function '{_function.Name}' return a value");
        }

        return boundBody;
    }

    internal BoundExpression? BindFieldInitializer(FieldSymbol field)
    {
        ExpressionSyntax? syntax = field.Declaration.Initializer;
        if (syntax is null)
            return null;

        BoundExpression initializer = ContextualizeConversion(BindExpression(syntax), field.Type);
        if (!TypeFacts.CanAssign(field.Type, initializer.Type))
            ReportCannotConvert(GetLocation(syntax), initializer.Type, field.Type);

        if (field.Type is ArrayTypeSymbol && GetArrayStorage(initializer) == ArrayStorageKind.Stack)
            _diagnostics.Report(GetLocation(syntax), "stack array cannot escape through this assignment");

        return initializer;
    }

    internal ImmutableArray<BoundStatement> CreateInstanceFieldInitializerStatements(StructTypeSymbol type)
    {
        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        var receiver = new BoundThisExpression(type, BuiltinTypes.PointerTo(type));
        foreach (FieldSymbol field in type.Fields)
        {
            if (field.Initializer is not BoundExpression initializer)
                continue;

            var target = new BoundMemberAccessExpression(receiver, field, IsPointerAccess: true);
            statements.Add(new BoundExpressionStatement(
                new BoundAssignmentExpression(target, SyntaxKind.EqualsToken, initializer)));
        }

        return statements.ToImmutable();
    }

    private static void AddDefaultInstanceInitializerCalls(
        StructTypeSymbol type,
        ImmutableArray<BoundStatement>.Builder statements)
    {
        if (type.BaseType is StructTypeSymbol baseType)
            AddDefaultInstanceInitializerCalls(baseType, statements);
        if (type.InstanceInitializer is FunctionSymbol initializer)
        {
            statements.Add(new BoundExpressionStatement(
                new BoundBaseLifecycleCallExpression(initializer, [])));
        }
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
        SwitchStatementSyntax @switch => BindSwitchStatement(@switch),
        BreakStatementSyntax @break => BindBreakStatement(@break),
        ContinueStatementSyntax @continue => BindContinueStatement(@continue),
        _ => throw new InvalidOperationException($"Unexpected statement syntax '{syntax.Kind}'."),
    };

    private BoundIfStatement BindIfStatement(IfStatementSyntax syntax)
    {
        BoundExpression condition = BindBooleanCondition(syntax.Condition);
        HashSet<LocalVariableSymbol> afterCondition = CloneDefinitelyAssigned();
        var arraysAfterCondition = CloneArrayState();

        RestoreDefinitelyAssigned(afterCondition);
        BoundStatement thenStatement = BindEmbeddedStatement(syntax.ThenStatement);
        HashSet<LocalVariableSymbol> afterThen = CloneDefinitelyAssigned();
        var arraysAfterThen = CloneArrayState();

        RestoreDefinitelyAssigned(afterCondition);
        RestoreArrayState(arraysAfterCondition);
        BoundStatement? elseStatement = syntax.ElseStatement is null
            ? null
            : BindEmbeddedStatement(syntax.ElseStatement);
        HashSet<LocalVariableSymbol> afterElse = syntax.ElseStatement is null
            ? afterCondition
            : CloneDefinitelyAssigned();
        var arraysAfterElse = CloneArrayState();

        if (condition is BoundLiteralExpression { Value: bool constantCondition })
        {
            RestoreDefinitelyAssigned(constantCondition ? afterThen : afterElse);
            RestoreArrayState(constantCondition ? arraysAfterThen : arraysAfterElse);
        }
        else if (AlwaysReturns(thenStatement) && (elseStatement is null || !AlwaysReturns(elseStatement)))
        {
            RestoreDefinitelyAssigned(afterElse);
            RestoreArrayState(arraysAfterElse);
        }
        else if (elseStatement is not null && AlwaysReturns(elseStatement) && !AlwaysReturns(thenStatement))
        {
            RestoreDefinitelyAssigned(afterThen);
            RestoreArrayState(arraysAfterThen);
        }
        else
        {
            afterThen.IntersectWith(afterElse);
            RestoreDefinitelyAssigned(afterThen);
            RestoreArrayState(MergeArrayState(arraysAfterThen, arraysAfterElse));
        }

        return new BoundIfStatement(condition, thenStatement, elseStatement);
    }

    private BoundWhileStatement BindWhileStatement(WhileStatementSyntax syntax)
    {
        BoundExpression condition = BindBooleanCondition(syntax.Condition);
        HashSet<LocalVariableSymbol> afterCondition = CloneDefinitelyAssigned();
        var arraysAfterCondition = CloneArrayState();
        _loopDepth++;
        BoundStatement body = BindEmbeddedStatement(syntax.Body);
        _loopDepth--;
        RestoreDefinitelyAssigned(afterCondition);
        RestoreArrayState(MergeArrayState(arraysAfterCondition, CloneArrayState()));
        return new BoundWhileStatement(condition, body);
    }

    private BoundForStatement BindForStatement(ForStatementSyntax syntax)
    {
        BoundScope previous = _scope;
        _scope = new BoundScope(previous);

        BoundStatement? initializer = syntax.Initializer is null ? null : BindStatement(syntax.Initializer);
        BoundExpression? condition = syntax.Condition is null ? null : BindBooleanCondition(syntax.Condition);
        HashSet<LocalVariableSymbol> afterCondition = CloneDefinitelyAssigned();
        var arraysAfterCondition = CloneArrayState();

        _loopDepth++;
        BoundStatement body = BindEmbeddedStatement(syntax.Body);
        RestoreDefinitelyAssigned(afterCondition);
        RestoreArrayState(MergeArrayState(arraysAfterCondition, CloneArrayState()));
        BoundExpression? increment = syntax.Increment is null ? null : BindExpression(syntax.Increment);
        _loopDepth--;

        RestoreDefinitelyAssigned(afterCondition);
        RestoreArrayState(MergeArrayState(arraysAfterCondition, CloneArrayState()));
        _scope = previous;
        return new BoundForStatement(initializer, condition, increment, body);
    }

    private BoundSwitchStatement BindSwitchStatement(SwitchStatementSyntax syntax)
    {
        BoundExpression expression = BindExpression(syntax.Expression);
        if (!TypeFacts.IsInteger(expression.Type) && expression.Type is not EnumTypeSymbol && !ReferenceEquals(expression.Type, BuiltinTypes.Error))
            _diagnostics.Report(syntax.SwitchKeyword.Location, "switch operand must be an integer or enum");
        var values = new HashSet<System.Numerics.BigInteger>();
        bool hasDefault = false;
        var sections = ImmutableArray.CreateBuilder<BoundSwitchSection>();
        var assignedBefore = new HashSet<LocalVariableSymbol>(_definitelyAssigned);
        var exits = new List<HashSet<LocalVariableSymbol>>();
        var arraysBefore = CloneArrayState();
        var arrayExits = new List<Dictionary<LocalVariableSymbol, ArrayState>>();
        _switchExits.Push((_loopDepth, exits, arrayExits));
        _switchDepth++;
        foreach (SwitchSectionSyntax section in syntax.Sections)
        {
            _definitelyAssigned.Clear();
            _definitelyAssigned.UnionWith(assignedBefore);
            RestoreArrayState(arraysBefore);
            BoundExpression? value = null;
            if (section.Value is null)
            {
                if (hasDefault) _diagnostics.Report(section.Label.Location, "duplicate default label");
                hasDefault = true;
            }
            else
            {
                BoundExpression boundValue = BindExpression(section.Value);
                ConstantFoldStatus status = _constants.Fold(boundValue, out object? constant);
                if (status == ConstantFoldStatus.Invalid ||
                    !(TypeFacts.IsInteger(boundValue.Type) || boundValue.Type is EnumTypeSymbol))
                    _diagnostics.Report(section.Label.Location, "case value must be an integer or enum compile-time constant");
                else if (!ReferenceEquals(expression.Type, boundValue.Type) &&
                         !(expression.Type is PrimitiveTypeSymbol { IsInteger: true } integer && TypeFacts.IsInteger(boundValue.Type) &&
                           (status == ConstantFoldStatus.TargetDependent || SemanticAnalyzer.FitsInteger(SemanticAnalyzer.ToInteger(constant), integer, _constants.TargetLayout))))
                    _diagnostics.Report(section.Label.Location, "case value is not compatible with the switch operand type");
                else if (status == ConstantFoldStatus.TargetDependent)
                    value = boundValue;
                else
                {
                    var number = SemanticAnalyzer.ToInteger(constant);
                    if (expression.Type is PrimitiveTypeSymbol { IsInteger: true } operandType && !SemanticAnalyzer.FitsInteger(number, operandType, _constants.TargetLayout))
                        _diagnostics.Report(section.Label.Location, "case value is not compatible with the switch operand type");
                    if (!values.Add(number)) _diagnostics.Report(section.Label.Location, "duplicate case value");
                    value = new BoundLiteralExpression(constant, expression.Type);
                }
            }
            BoundScope previous = _scope;
            _scope = new BoundScope(previous);
            var body = new BoundBlockStatement(section.Statements.Select(BindStatement).ToImmutableArray());
            _scope = previous;
            if (!body.Statements.IsEmpty && !TerminatesCase(body))
                _diagnostics.Report(section.Label.Location, "implicit fallthrough is not allowed; terminate the case with break, return, or continue");
            sections.Add(new BoundSwitchSection(value, body));
        }
        _switchDepth--;
        _switchExits.Pop();
        if (!hasDefault)
        {
            exits.Add(assignedBefore);
            arrayExits.Add(arraysBefore);
        }
        if (exits.Count > 0)
        {
            assignedBefore = new HashSet<LocalVariableSymbol>(exits[0]);
            foreach (var exit in exits.Skip(1)) assignedBefore.IntersectWith(exit);
        }
        _definitelyAssigned.Clear();
        _definitelyAssigned.UnionWith(assignedBefore);
        RestoreArrayState(arrayExits.Count == 0 ? arraysBefore : arrayExits.Aggregate(MergeArrayState));
        if (sections.Count > 0 && sections[^1].Body.Statements.IsEmpty)
            _diagnostics.Report(syntax.Sections[^1].Label.Location, "final case requires an explicitly terminated body");
        return new BoundSwitchStatement(expression, sections.ToImmutable());
    }

    private static bool TerminatesCase(BoundStatement statement) => BoundControlFlow.TerminatesSection(statement);

    private BoundBreakStatement BindBreakStatement(BreakStatementSyntax syntax)
    {
        if (_switchExits.TryPeek(out var context) && context.LoopDepth == _loopDepth)
        {
            context.Exits.Add(new HashSet<LocalVariableSymbol>(_definitelyAssigned));
            context.ArrayExits.Add(CloneArrayState());
        }
        if (_loopDepth == 0 && _switchDepth == 0)
        {
            _diagnostics.Report(syntax.BreakKeyword.Location, "'break' can only be used inside a loop or switch");
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
        bool isConstant = syntax.Type.IsConst && syntax.Type.PointerDepth == 0 && !syntax.Type.IsReference;
        TypeSymbol type = TypeResolver.Resolve(isConstant ? syntax.Type with { ConstKeyword = null } : syntax.Type, _fileScope, _diagnostics);
        if (ReferenceEquals(type, BuiltinTypes.Void))
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, "local variable type cannot be 'void'");
        }

        var variable = new LocalVariableSymbol(syntax.IdentifierToken.Text, type, isConstant || syntax.Type.IsBindingReadonly);
        _localScopes.Add(variable, _scope);
        bool declared = _scope.TryDeclare(variable);
        if (!declared)
        {
            _diagnostics.Report(
                syntax.IdentifierToken.Location,
                $"variable '{variable.Name}' is already declared in this scope");
        }

        BoundExpression? initializer = syntax.Initializer is null ? null : BindExpression(syntax.Initializer);
        if (type is ReferenceTypeSymbol && initializer is null)
        {
            _diagnostics.Report(syntax.IdentifierToken.Location, "reference variables must be initialized");
        }
        else if (syntax.Type.IsBindingReadonly && initializer is null)
        {
            _diagnostics.Report(syntax.IdentifierToken.Location, "readonly local variables must be initialized");
        }
        if (initializer is not null)
        {
            initializer = ContextualizeConversion(initializer, type);
        }

        if (isConstant)
        {
            if (initializer is not null && TypeFacts.IsNumeric(type) && TypeFacts.IsNumeric(initializer.Type) && !ReferenceEquals(type, initializer.Type))
                initializer = new BoundCastExpression(initializer, type);
            object? constantValue = null;
            ConstantFoldStatus status = initializer is null ? ConstantFoldStatus.Invalid : _constants.Fold(initializer, out constantValue);
            if (status == ConstantFoldStatus.Invalid)
                _diagnostics.Report(syntax.IdentifierToken.Location, "const local requires a compile-time constant initializer");
            else if (status == ConstantFoldStatus.TargetDependent)
                variable.ConstantValue = initializer;
            else
            {
                variable.ConstantValue = initializer = new BoundLiteralExpression(constantValue, initializer!.Type);
            }
        }

        if (initializer is not null && !TypeFacts.CanAssign(type, initializer.Type))
        {
            ReportCannotConvert(GetLocation(syntax.Initializer!), initializer.Type, type);
        }

        if (type is ArrayTypeSymbol && initializer is not null)
        {
            TrackArrayAssignment(variable, initializer, GetLocation(syntax.Initializer!));
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
            expression = ContextualizeConversion(expression, _function.ReturnType);
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

    private BoundExpression BindExpression(ExpressionSyntax syntax)
    {
        BoundExpression expression = syntax switch
        {
            LiteralExpressionSyntax literal => BindLiteralExpression(literal),
            NameExpressionSyntax name => BindNameExpression(name),
            ThisExpressionSyntax @this => BindThisExpression(@this),
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
            TypeLayoutExpressionSyntax layout => BindTypeLayoutExpression(layout),
            CastExpressionSyntax cast => BindCastExpression(cast),
            _ => throw new InvalidOperationException($"Unexpected expression syntax '{syntax.Kind}'."),
        };
        _expressionLocations[expression] = GetLocation(syntax);
        BoundExpression result = DereferenceReference(expression);
        _expressionLocations[result] = GetLocation(syntax);
        return result;
    }

    private static BoundExpression DereferenceReference(BoundExpression expression) =>
        expression.Type is ReferenceTypeSymbol referenceType
            ? new BoundReferenceDereferenceExpression(expression, referenceType)
            : expression;

    private BoundExpression BindThisExpression(ThisExpressionSyntax syntax)
    {
        if (_function.ContainingType is not StructTypeSymbol containingType || _function.IsStatic)
        {
            _diagnostics.Report(syntax.ThisKeyword.Location, "'this' is available only in instance members");
            return new BoundErrorExpression();
        }
        if (_bindingBaseConstructorArguments)
        {
            _diagnostics.Report(syntax.ThisKeyword.Location, "the derived object cannot be used in base constructor arguments");
            return new BoundErrorExpression();
        }
        return new BoundThisExpression(containingType, BuiltinTypes.PointerTo(containingType, isReadonly: _function.IsReadonly));
    }

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
                new BoundLiteralExpression(token.Value, BuiltinTypes.PointerTo(BuiltinTypes.Byte, isReadonly: true)),
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
            if (variable is LocalVariableSymbol { ConstantValue: not null } localConstant) return localConstant.ConstantValue;
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
            ConstantSymbol? associatedConstant = containingType.FindConstant(syntax.IdentifierToken.Text);
            if (associatedConstant?.HasValue == true)
                return associatedConstant.BoundValue!;
            if (associatedConstant is not null)
                return new BoundErrorExpression();

            FieldSymbol? field = containingType.FindField(syntax.IdentifierToken.Text);
            if (field is not null)
            {
                if (_bindingBaseConstructorArguments)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, "the derived object cannot be used in base constructor arguments");
                    return new BoundErrorExpression();
                }
                if (!field.IsPublic && !ReferenceEquals(containingType, field.ContainingType))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"field '{field.Name}' is private in struct '{field.ContainingType.Name}'");
                    return new BoundErrorExpression();
                }
                if (_function.IsStatic)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"static method '{_function.Name}' cannot access instance field '{field.Name}' without an explicit instance");
                    return new BoundErrorExpression();
                }
                PointerTypeSymbol thisType = BuiltinTypes.PointerTo(containingType, isReadonly: _function.IsReadonly);
                return new BoundMemberAccessExpression(
                    new BoundThisExpression(containingType, thisType),
                    field,
                    IsPointerAccess: true);
            }


            PropertySymbol? property = containingType.FindProperty(syntax.IdentifierToken.Text);
            if (property is not null)
            {
                if (_bindingBaseConstructorArguments)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, "the derived object cannot be used in base constructor arguments");
                    return new BoundErrorExpression();
                }
                if (_function.IsStatic)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"static method '{_function.Name}' cannot access instance property '{property.Name}' without an explicit instance");
                    return new BoundErrorExpression();
                }

                PointerTypeSymbol thisType = BuiltinTypes.PointerTo(containingType, isReadonly: _function.IsReadonly);
                return BindPropertyGet(
                    new BoundThisExpression(containingType, thisType),
                    property,
                    isPointerAccess: true,
                    receiverIsReadonly: _function.IsReadonly,
                    syntax.IdentifierToken.Location);
            }
        }

        ConstantSymbol? constant = _fileScope.ResolveConstant(
            syntax.IdentifierToken.Text,
            syntax.IdentifierToken.Location,
            _diagnostics);
        if (constant?.HasValue == true)
            return constant.BoundValue!;
        if (constant is not null)
            return new BoundErrorExpression();

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
            SyntaxKind.AmpersandToken when IsAddressable(operand) => BuiltinTypes.PointerTo(operand.Type, isReadonly: !IsWritable(operand)),
            SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken
                when IsWritable(operand) && (TypeFacts.IsNumeric(operand.Type) ||
                    operand.Type is PointerTypeSymbol pointer && !ReferenceEquals(pointer.ElementType, BuiltinTypes.Void)) => operand.Type,
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
        bool previousSuppression = _suppressIntegerOperationDiagnostics;
        if (syntax.OperatorToken.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken &&
            _constants.TryFold(left, out object? value) && value is bool condition &&
            condition == (syntax.OperatorToken.Kind == SyntaxKind.PipePipeToken))
            _suppressIntegerOperationDiagnostics = true;
        BoundExpression right;
        try { right = BindExpression(syntax.Right); }
        finally { _suppressIntegerOperationDiagnostics = previousSuppression; }

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

        ValidateIntegerOperation(left, syntax.OperatorToken.Kind, right, syntax.OperatorToken.Location);
        return new BoundBinaryExpression(left, syntax.OperatorToken.Kind, right, resultType);
    }

    private void ValidateIntegerOperation(BoundExpression left, SyntaxKind operation, BoundExpression right, TextLocation location)
    {
        if (_suppressIntegerOperationDiagnostics ||
            operation is not (SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken) ||
            left.Type is not PrimitiveTypeSymbol { IsInteger: true } integer ||
            !TypeFacts.IsInteger(right.Type) ||
            !_constants.TryFold(right, out object? rightValue))
            return;
        BigInteger count = SemanticAnalyzer.ToInteger(rightValue);
        int? width = integer.BitWidth ?? _constants.TargetLayout?.GetIntegerBitWidth(integer);
        if (operation is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken)
        {
            if (count < 0 || width is int bits && count >= bits)
                _diagnostics.Report(location, "invalid integer shift: count must be nonnegative and less than the operand bit width");
        }
        else if (operation is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
        {
            if (count == 0)
                _diagnostics.Report(location, "invalid integer division or remainder by zero");
            else if (integer.IsSigned && width is int bits && count == -1 &&
                     _constants.TryFold(left, out object? leftValue) &&
                     SemanticAnalyzer.ToInteger(leftValue) == -(BigInteger.One << (bits - 1)))
                _diagnostics.Report(location, "invalid integer division or remainder: signed minimum with -1");
        }
    }

    private BoundExpression BindAssignmentExpression(AssignmentExpressionSyntax syntax)
    {
        bool isSimpleAssignment = syntax.OperatorToken.Kind == SyntaxKind.EqualsToken;
        BoundExpression? indexerAssignment = TryBindIndexerAssignment(syntax, isSimpleAssignment);
        if (indexerAssignment is not null)
            return indexerAssignment;
        BoundExpression? propertyAssignment = TryBindPropertyAssignment(syntax, isSimpleAssignment);
        if (propertyAssignment is not null)
            return propertyAssignment;

        BoundExpression target = isSimpleAssignment && syntax.Target is NameExpressionSyntax name
            ? BindNameExpression(name, requireDefinitelyAssigned: false)
            : BindExpression(syntax.Target);
        target = DereferenceReference(target);
        BoundExpression expression = BindExpression(syntax.Expression);
        if (isSimpleAssignment)
        {
            expression = ContextualizeConversion(expression, target.Type);
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
            ValidateIntegerOperation(target, binaryOperator, expression, syntax.OperatorToken.Location);
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
                TrackArrayAssignment(local, expression, GetLocation(syntax.Expression));
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
        if (syntax.OperatorToken.Kind == SyntaxKind.DotToken && TryGetDottedName(syntax, out ImmutableArray<SyntaxToken> qualifiedName) && qualifiedName.Length >= 2)
        {
            string[] typeParts = qualifiedName.Take(qualifiedName.Length - 1).Select(token => token.Text).ToArray();
            TypeSymbol? resolved = typeParts.Length == 1
                ? _fileScope.ResolveType(typeParts[0], syntax.Receiver is NameExpressionSyntax name ? name.IdentifierToken.Location : syntax.OperatorToken.Location, _diagnostics)
                : _fileScope.ResolveQualifiedType(typeParts);
            if (resolved is EnumTypeSymbol enumeration)
            {
                ConstantSymbol? member = enumeration.FindMember(qualifiedName[^1].Text);
                if (member?.BoundValue is BoundExpression value) return value;
                _diagnostics.Report(syntax.MemberToken.Location, $"enum '{enumeration.Name}' has no valid member '{syntax.MemberToken.Text}'");
                return new BoundErrorExpression();
            }
            if (resolved is StructTypeSymbol staticType)
            {
                ConstantSymbol? constant = staticType.FindConstant(qualifiedName[^1].Text);
                if (constant?.HasValue == true)
                    return constant.BoundValue!;
                if (constant is not null)
                    return new BoundErrorExpression();

                FieldSymbol? staticField = staticType.FindStaticField(qualifiedName[^1].Text);
                if (staticField is not null)
                {
                    if (!staticField.IsPublic && !ReferenceEquals(_function.ContainingType, staticField.ContainingType))
                    {
                        _diagnostics.Report(qualifiedName[^1].Location, $"static field '{staticField.Name}' is private in struct '{staticField.ContainingType.Name}'");
                        return new BoundErrorExpression();
                    }
                    return new BoundStaticFieldExpression(staticField);
                }
            }
        }

        BoundExpression receiver = BindExpression(syntax.Receiver);
        if (receiver.Type is ArrayTypeSymbol && syntax.OperatorToken.Kind == SyntaxKind.DotToken && syntax.MemberToken.Text is "Length" or "Rank")
            return new BoundArrayMetadataExpression(receiver, syntax.MemberToken.Text);
        bool pointerAccess = syntax.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
        InterfaceTypeSymbol? interfaceType = pointerAccess
            ? (receiver.Type as PointerTypeSymbol)?.ElementType as InterfaceTypeSymbol
            : receiver.Type as InterfaceTypeSymbol;
        if (interfaceType is not null)
        {
            InterfacePropertySymbol? interfaceProperty = interfaceType.FindProperty(syntax.MemberToken.Text);
            if (interfaceProperty is null)
            {
                _diagnostics.Report(
                    syntax.MemberToken.Location,
                    $"interface '{interfaceType.Name}' does not contain property '{syntax.MemberToken.Text}'");
                return new BoundErrorExpression();
            }
            if (interfaceProperty.Getter is not FunctionSymbol getter)
            {
                _diagnostics.Report(syntax.MemberToken.Location, $"property '{interfaceProperty.Name}' does not declare a getter");
                return new BoundErrorExpression();
            }
            if (IsReadonlyReceiver(receiver, pointerAccess) && !getter.IsReadonly)
            {
                _diagnostics.Report(syntax.MemberToken.Location, $"property '{interfaceProperty.Name}' cannot be read through a readonly interface receiver because its getter is mutable");
                return new BoundErrorExpression();
            }
            return new BoundInterfaceMethodCallExpression(receiver, interfaceType, getter, [], pointerAccess);
        }

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
        PropertySymbol? property = structType.FindProperty(syntax.MemberToken.Text);
        if (property is not null)
        {
            bool receiverIsReadonly =
                (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
                (!pointerAccess && IsAddressable(receiver) && !IsWritable(receiver));
            return BindPropertyGet(
                receiver,
                property,
                pointerAccess,
                receiverIsReadonly,
                syntax.MemberToken.Location);
        }

        if (field is null)
        {
            _diagnostics.Report(
                syntax.MemberToken.Location,
                $"struct '{structType.Name}' does not contain field '{syntax.MemberToken.Text}'");
            return new BoundErrorExpression();
        }

        if (!field.IsPublic && !ReferenceEquals(_function.ContainingType, field.ContainingType))
        {
            _diagnostics.Report(
                syntax.MemberToken.Location,
                $"field '{field.Name}' is private in struct '{field.ContainingType.Name}'");
        }

        return new BoundMemberAccessExpression(receiver, field, pointerAccess);
    }

    private BoundExpression? TryBindPropertyAssignment(AssignmentExpressionSyntax syntax, bool isSimpleAssignment)
    {
        BoundExpression receiver;
        PropertySymbol? property;
        bool pointerAccess;
        TextLocation location;

        if (syntax.Target is MemberAccessExpressionSyntax member)
        {
            if (member.OperatorToken.Kind == SyntaxKind.DotToken &&
                TryGetDottedName(member, out ImmutableArray<SyntaxToken> dottedName) &&
                dottedName.Length >= 2 &&
                _scope.Lookup(dottedName[0].Text) is null &&
                (_fileScope.CanStartQualifiedName(dottedName[0].Text) ||
                 _fileScope.ResolveType(dottedName[0].Text, dottedName[0].Location, _diagnostics) is not null))
            {
                return null;
            }

            receiver = BindExpression(member.Receiver);
            pointerAccess = member.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
            InterfaceTypeSymbol? interfaceType = pointerAccess
                ? (receiver.Type as PointerTypeSymbol)?.ElementType as InterfaceTypeSymbol
                : receiver.Type as InterfaceTypeSymbol;
            if (interfaceType is not null)
            {
                InterfacePropertySymbol? interfaceProperty = interfaceType.FindProperty(member.MemberToken.Text);
                if (interfaceProperty is null)
                    return null;
                location = member.MemberToken.Location;
                if (interfaceProperty.Setter is not FunctionSymbol interfaceSetter)
                {
                    _diagnostics.Report(location, $"property '{interfaceProperty.Name}' does not declare a setter");
                    return new BoundErrorExpression();
                }
                bool interfaceReceiverIsReadonly =
                    (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
                    (!pointerAccess && (!IsAddressable(receiver) || !IsWritable(receiver)));
                if (interfaceReceiverIsReadonly)
                {
                    _diagnostics.Report(location, $"property '{interfaceProperty.Name}' cannot be assigned through a readonly receiver");
                    return new BoundErrorExpression();
                }

                if (!isSimpleAssignment)
                {
                    if (interfaceProperty.Getter is not FunctionSymbol interfaceGetter)
                    {
                        _diagnostics.Report(location, $"property '{interfaceProperty.Name}' does not declare a getter");
                        return new BoundErrorExpression();
                    }

                    return BindCompoundAccessorAssignment(
                        receiver,
                        interfaceGetter,
                        interfaceSetter,
                        [],
                        [],
                        syntax,
                        pointerAccess,
                        interfaceType);
                }

                BoundExpression interfaceValue = BindExpression(syntax.Expression);
                ImmutableArray<BoundExpression> interfaceArguments = ValidateFunctionArguments(
                    interfaceSetter,
                    [interfaceValue],
                    [syntax.Expression],
                    location);
                return new BoundInterfacePropertySetExpression(
                    receiver,
                    interfaceType,
                    interfaceProperty,
                    interfaceArguments[0],
                    pointerAccess);
            }

            StructTypeSymbol? structType = pointerAccess
                ? (receiver.Type as PointerTypeSymbol)?.ElementType as StructTypeSymbol
                : receiver.Type as StructTypeSymbol;
            if (structType is null || (property = structType.FindProperty(member.MemberToken.Text)) is null)
                return null;
            location = member.MemberToken.Location;
        }
        else if (syntax.Target is NameExpressionSyntax name && _function.ContainingType is StructTypeSymbol containingType)
        {
            if (_scope.Lookup(name.IdentifierToken.Text) is not null)
                return null;
            property = containingType.FindProperty(name.IdentifierToken.Text);
            if (property is null)
                return null;
            pointerAccess = true;
            receiver = new BoundThisExpression(
                containingType,
                BuiltinTypes.PointerTo(containingType, isReadonly: _function.IsReadonly));
            location = name.IdentifierToken.Location;
        }
        else
        {
            return null;
        }

        if (_bindingBaseConstructorArguments)
        {
            _diagnostics.Report(location, "the derived object cannot be used in base constructor arguments");
            return new BoundErrorExpression();
        }
        if (_function.IsStatic && syntax.Target is NameExpressionSyntax)
        {
            _diagnostics.Report(location, $"static method '{_function.Name}' cannot access instance property '{property.Name}' without an explicit instance");
            return new BoundErrorExpression();
        }
        if (!property.IsPublic && !ReferenceEquals(_function.ContainingType, property.ContainingType))
            _diagnostics.Report(location, $"property '{property.Name}' is private in struct '{property.ContainingType.Name}'");
        if (property.Setter is not FunctionSymbol setter)
        {
            _diagnostics.Report(location, $"property '{property.Name}' does not declare a setter");
            return new BoundErrorExpression();
        }

        bool receiverIsReadonly =
            (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
            (!pointerAccess && (!IsAddressable(receiver) || !IsWritable(receiver)));
        if (receiverIsReadonly)
        {
            _diagnostics.Report(location, $"property '{property.Name}' cannot be assigned through a readonly receiver");
            return new BoundErrorExpression();
        }

        if (!isSimpleAssignment)
        {
            if (property.Getter is not FunctionSymbol getter)
            {
                _diagnostics.Report(location, $"property '{property.Name}' does not declare a getter");
                return new BoundErrorExpression();
            }

            return BindCompoundAccessorAssignment(
                receiver,
                getter,
                setter,
                [],
                [],
                syntax,
                pointerAccess,
                interfaceType: null);
        }

        BoundExpression value = BindExpression(syntax.Expression);
        ImmutableArray<BoundExpression> arguments = ValidateFunctionArguments(
            setter,
            [value],
            [syntax.Expression],
            location);
        return new BoundPropertySetExpression(receiver, property, arguments[0], pointerAccess);
    }

    private BoundExpression BindPropertyGet(
        BoundExpression receiver,
        PropertySymbol property,
        bool isPointerAccess,
        bool receiverIsReadonly,
        TextLocation location)
    {
        if (!property.IsPublic && !ReferenceEquals(_function.ContainingType, property.ContainingType))
            _diagnostics.Report(location, $"property '{property.Name}' is private in struct '{property.ContainingType.Name}'");
        if (property.Getter is not FunctionSymbol getter)
        {
            _diagnostics.Report(location, $"property '{property.Name}' does not declare a getter");
            return new BoundErrorExpression();
        }
        if (receiverIsReadonly && !getter.IsReadonly)
        {
            _diagnostics.Report(location, $"property '{property.Name}' cannot be read through a readonly receiver because its getter is mutable");
            return new BoundErrorExpression();
        }

        return new BoundMethodCallExpression(receiver, getter, [], isPointerAccess);
    }

    private BoundExpression BindTypeLayoutExpression(TypeLayoutExpressionSyntax syntax)
    {
        TypeSymbol type = TypeResolver.Resolve(syntax.Type, _fileScope, _diagnostics);
        if (ReferenceEquals(type, BuiltinTypes.Void) || ReferenceEquals(type, BuiltinTypes.Error))
        {
            if (ReferenceEquals(type, BuiltinTypes.Void)) _diagnostics.Report(syntax.Keyword.Location, "layout intrinsic requires a non-void type");
            return new BoundErrorExpression();
        }
        if (syntax.Keyword.Kind != SyntaxKind.OffsetOfKeyword)
            return new BoundTypeLayoutExpression(syntax.Keyword.Kind, type, null);

        if (type is not StructTypeSymbol structType)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, "offsetof requires a struct type");
            return new BoundErrorExpression();
        }
        FieldSymbol? field = structType.FindField(syntax.FieldToken!.Text);
        if (field is null)
        {
            _diagnostics.Report(syntax.FieldToken.Location, $"struct '{structType.Name}' does not contain field '{syntax.FieldToken.Text}'");
            return new BoundErrorExpression();
        }
        return new BoundTypeLayoutExpression(syntax.Keyword.Kind, type, field);
    }

    private BoundExpression BindCastExpression(CastExpressionSyntax syntax)
    {
        TypeSymbol targetType = TypeResolver.Resolve(syntax.Type, _fileScope, _diagnostics);
        BoundExpression expression = BindExpression(syntax.Expression);
        if (!TypeFacts.CanExplicitlyCast(targetType, expression.Type))
        {
            if (!ReferenceEquals(targetType, BuiltinTypes.Error) && !ReferenceEquals(expression.Type, BuiltinTypes.Error))
                _diagnostics.Report(syntax.CastKeyword.Location, $"cast from '{expression.Type.Name}' to '{targetType.Name}' is not a valid primitive cast");
            return new BoundErrorExpression();
        }
        return new BoundCastExpression(expression, targetType);
    }

    private BoundExpression BindIndexExpression(IndexExpressionSyntax syntax)
    {
        if (TryResolveTypeExpression(syntax.Receiver, out TypeSymbol? arrayElementType, out TextLocation typeLocation) &&
            arrayElementType is not null &&
            !ReferenceEquals(arrayElementType, BuiltinTypes.Error))
        {
            return BindArrayCreation(arrayElementType, syntax.Arguments, typeLocation, syntax.OpenBracketToken.Location, ArrayStorageKind.Stack);
        }

        BoundExpression receiver = BindExpression(syntax.Receiver);
        ImmutableArray<BoundExpression> arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();

        if (receiver.Type is StructTypeSymbol structType)
        {
            bool receiverIsReadonly = IsAddressable(receiver) && !IsWritable(receiver);
            IndexerSymbol? indexer = ResolveIndexer(
                structType.AllIndexers.Where(candidate =>
                    candidate.Getter is not null &&
                    (!receiverIsReadonly || candidate.Getter.IsReadonly)),
                arguments,
                syntax.OpenBracketToken.Location,
                structType.Name);
            if (indexer is null)
                return new BoundErrorExpression();
            if (!indexer.IsPublic && !ReferenceEquals(_function.ContainingType, indexer.ContainingType))
                _diagnostics.Report(syntax.OpenBracketToken.Location, $"indexer is private in struct '{indexer.ContainingType.Name}'");
            if (indexer.Getter is not FunctionSymbol getter)
            {
                _diagnostics.Report(syntax.OpenBracketToken.Location, "indexer does not declare a getter");
                return new BoundErrorExpression();
            }
            arguments = ValidateFunctionArguments(getter, arguments, syntax.Arguments, syntax.OpenBracketToken.Location);
            return new BoundMethodCallExpression(receiver, getter, arguments, IsPointerAccess: false);
        }

        if (receiver.Type is InterfaceTypeSymbol interfaceType)
        {
            bool receiverIsReadonly = IsReadonlyReceiver(receiver, pointerAccess: false);
            InterfaceIndexerSymbol? indexer = ResolveIndexer(
                interfaceType.AllIndexers.Where(candidate =>
                    candidate.Getter is not null &&
                    (!receiverIsReadonly || candidate.Getter.IsReadonly)),
                arguments,
                syntax.OpenBracketToken.Location,
                interfaceType.Name);
            if (indexer is null)
                return new BoundErrorExpression();
            FunctionSymbol getter = indexer.Getter!;
            arguments = ValidateFunctionArguments(getter, arguments, syntax.Arguments, syntax.OpenBracketToken.Location);
            return new BoundInterfaceMethodCallExpression(receiver, interfaceType, getter, arguments, IsPointerAccess: false);
        }

        int requiredRank = receiver.Type is ArrayTypeSymbol rankedArray ? rankedArray.Rank : 1;
        if (arguments.Length != requiredRank)
        {
            _diagnostics.Report(syntax.OpenBracketToken.Location, $"array or pointer indexing requires {requiredRank} index value(s)");
            return new BoundErrorExpression();
        }
        BoundExpression index = arguments[0];

        foreach (BoundExpression argument in arguments)
        {
            if (!TypeFacts.IsInteger(argument.Type) && !ReferenceEquals(argument.Type, BuiltinTypes.Error))
                _diagnostics.Report(GetLocation(syntax.Index), $"array index must be an integer, but has type '{argument.Type.Name}'");
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

        if (ReferenceEquals(elementType, BuiltinTypes.Void))
        {
            _diagnostics.Report(syntax.OpenBracketToken.Location, "cannot index a void pointer");
            return new BoundErrorExpression();
        }
        return new BoundIndexExpression(receiver, index, elementType) { Indices = arguments };
    }

    private BoundExpression? TryBindIndexerAssignment(AssignmentExpressionSyntax syntax, bool isSimpleAssignment)
    {
        if (syntax.Target is not IndexExpressionSyntax target)
            return null;

        BoundExpression receiver = BindExpression(target.Receiver);
        if (receiver.Type is not StructTypeSymbol && receiver.Type is not InterfaceTypeSymbol)
            return null;
        ImmutableArray<BoundExpression> indices = target.Arguments.Select(BindExpression).ToImmutableArray();
        if (receiver.Type is StructTypeSymbol structType)
        {
            IndexerSymbol? indexer = ResolveIndexer(
                structType.AllIndexers.Where(candidate =>
                    candidate.Setter is not null && (isSimpleAssignment || candidate.Getter is not null)),
                indices,
                target.OpenBracketToken.Location,
                structType.Name);
            if (indexer is null)
                return new BoundErrorExpression();
            if (!indexer.IsPublic && !ReferenceEquals(_function.ContainingType, indexer.ContainingType))
                _diagnostics.Report(target.OpenBracketToken.Location, $"indexer is private in struct '{indexer.ContainingType.Name}'");
            if (!IsAddressable(receiver) || !IsWritable(receiver))
            {
                _diagnostics.Report(target.OpenBracketToken.Location, "indexer cannot be assigned through a readonly receiver");
                return new BoundErrorExpression();
            }
            FunctionSymbol setter = indexer.Setter!;
            if (!isSimpleAssignment)
            {
                return BindCompoundAccessorAssignment(
                    receiver,
                    indexer.Getter!,
                    setter,
                    indices,
                    target.Arguments,
                    syntax,
                    isPointerAccess: false,
                    interfaceType: null);
            }

            BoundExpression value = BindExpression(syntax.Expression);
            ImmutableArray<ExpressionSyntax> argumentSyntax = [.. target.Arguments, syntax.Expression];
            ImmutableArray<BoundExpression> arguments = ValidateFunctionArguments(
                setter,
                [.. indices, value],
                argumentSyntax,
                target.OpenBracketToken.Location);
            return new BoundIndexerSetExpression(receiver, indexer, arguments[..^1], arguments[^1]);
        }

        var interfaceType = (InterfaceTypeSymbol)receiver.Type;
        InterfaceIndexerSymbol? interfaceIndexer = ResolveIndexer(
            interfaceType.AllIndexers.Where(candidate =>
                candidate.Setter is not null && (isSimpleAssignment || candidate.Getter is not null)),
            indices,
            target.OpenBracketToken.Location,
            interfaceType.Name);
        if (interfaceIndexer is null)
            return new BoundErrorExpression();
        if (!IsAddressable(receiver) || !IsWritable(receiver))
        {
            _diagnostics.Report(target.OpenBracketToken.Location, "indexer cannot be assigned through a readonly receiver");
            return new BoundErrorExpression();
        }
        FunctionSymbol interfaceSetter = interfaceIndexer.Setter!;
        if (!isSimpleAssignment)
        {
            return BindCompoundAccessorAssignment(
                receiver,
                interfaceIndexer.Getter!,
                interfaceSetter,
                indices,
                target.Arguments,
                syntax,
                isPointerAccess: false,
                interfaceType);
        }

        BoundExpression interfaceValue = BindExpression(syntax.Expression);
        ImmutableArray<ExpressionSyntax> interfaceArgumentSyntax = [.. target.Arguments, syntax.Expression];
        ImmutableArray<BoundExpression> interfaceArguments = ValidateFunctionArguments(
            interfaceSetter,
            [.. indices, interfaceValue],
            interfaceArgumentSyntax,
            target.OpenBracketToken.Location);
        return new BoundInterfaceIndexerSetExpression(
            receiver,
            interfaceType,
            interfaceIndexer,
            interfaceArguments[..^1],
            interfaceArguments[^1]);
    }

    private BoundExpression BindCompoundAccessorAssignment(
        BoundExpression receiver,
        FunctionSymbol getter,
        FunctionSymbol setter,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        AssignmentExpressionSyntax syntax,
        bool isPointerAccess,
        InterfaceTypeSymbol? interfaceType)
    {
        arguments = ValidateFunctionArguments(
            getter,
            arguments,
            argumentSyntax,
            GetLocation(syntax.Target));
        BoundExpression value = BindExpression(syntax.Expression);
        SyntaxKind binaryOperator = GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind);
        ValidateIntegerOperation(new BoundMethodCallExpression(receiver, getter, arguments, isPointerAccess),
            binaryOperator, value, syntax.OperatorToken.Location);
        TypeSymbol? resultType = GetBinaryResultType(getter.ReturnType, binaryOperator, value.Type);
        if (!ReferenceEquals(resultType, getter.ReturnType))
        {
            _diagnostics.Report(
                syntax.OperatorToken.Location,
                $"operator '{syntax.OperatorToken.Text}' is not defined for types '{getter.ReturnType.Name}' and '{value.Type.Name}'");
        }

        return new BoundCompoundAccessorAssignmentExpression(
            receiver,
            getter,
            setter,
            arguments,
            binaryOperator,
            value,
            isPointerAccess,
            interfaceType);
    }

    private bool TryResolveTypeExpression(
        ExpressionSyntax syntax,
        out TypeSymbol? type,
        out TextLocation location)
    {
        type = null;
        location = GetLocation(syntax);

        if (syntax is NameExpressionSyntax name)
        {
            string identifier = name.IdentifierToken.Text;
            if (_scope.Lookup(identifier) is not null ||
                _function.ContainingType?.FindField(identifier) is not null)
            {
                return false;
            }

            type = _fileScope.ResolveType(identifier, name.IdentifierToken.Location, _diagnostics);
            location = name.IdentifierToken.Location;
            return type is not null;
        }

        if (!TryGetDottedName(syntax, out ImmutableArray<SyntaxToken> parts))
        {
            return false;
        }

        string firstName = parts[0].Text;
        if (_scope.Lookup(firstName) is not null ||
            _function.ContainingType?.FindField(firstName) is not null ||
            !_fileScope.CanStartQualifiedName(firstName))
        {
            return false;
        }

        type = _fileScope.ResolveQualifiedType(parts.Select(part => part.Text).ToArray());
        location = parts[^1].Location;
        return type is not null;
    }

    private BoundExpression BindStructPositionalConstructionExpression(StructPositionalConstructionExpressionSyntax syntax)
    {
        TypeSymbol resolvedType = TypeResolver.Resolve(syntax.Type, _fileScope, _diagnostics);
        if (resolvedType is not StructTypeSymbol structType)
        {
            if (!ReferenceEquals(resolvedType, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, $"type '{syntax.Type.Name}' is not a struct");
            }

            return new BoundErrorExpression();
        }

        if (structType.IsAbstract)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated");
        }

        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        arguments = ValidatePositionalArguments(structType, arguments, syntax.Arguments, syntax.Type.NameToken.Location);
        return new BoundStructConstructionExpression(structType, arguments);
    }

    private BoundExpression BindStackArrayCreationExpression(StackArrayCreationExpressionSyntax syntax)
    {
        TypeSymbol elementType = TypeResolver.Resolve(syntax.ElementType, _fileScope, _diagnostics);
        return BindArrayCreation(elementType, syntax.Dimensions, syntax.ElementType.NameToken.Location, syntax.OpenBracketToken.Location, ArrayStorageKind.Stack);
    }

    private BoundExpression BindArrayCreation(
        TypeSymbol elementType,
        ImmutableArray<ExpressionSyntax> dimensionSyntax,
        TextLocation elementLocation,
        TextLocation allocationLocation,
        ArrayStorageKind storage)
    {
        ValidateArrayElementType(elementType, elementLocation);
        if (storage == ArrayStorageKind.Stack)
        {
            _function.HasStackArrays = true;
            if (elementType is ArrayTypeSymbol)
                _diagnostics.Report(elementLocation, "stack arrays cannot contain array elements");
        }

        var dimensions = dimensionSyntax.Select(BindExpression).ToImmutableArray();
        if (dimensions.IsEmpty)
        {
            _diagnostics.Report(allocationLocation, "array allocation requires at least one dimension");
            return new BoundErrorExpression();
        }
        for (int i = 0; i < dimensions.Length; i++) ValidateArrayLength(dimensions[i], dimensionSyntax[i]);

        System.Numerics.BigInteger totalLength = 1;
        bool constantDimensions = true;
        foreach (BoundExpression dimension in dimensions)
        {
            if (TypeFacts.IsInteger(dimension.Type) && _constants.TryFold(dimension, out object? value))
                totalLength *= SemanticAnalyzer.ToInteger(value);
            else
                constantDimensions = false;
        }
        if (constantDimensions && totalLength > int.MaxValue)
            _diagnostics.Report(allocationLocation, "total array length exceeds int.MaxValue");

        ArrayTypeSymbol arrayType = BuiltinTypes.ArrayOf(elementType, dimensions.Length);
        return new BoundArrayCreationExpression(elementType, dimensions[0], arrayType, storage) { Dimensions = dimensions };
    }

    private BoundExpression BindCallExpression(CallExpressionSyntax syntax)
    {
        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();

        if (syntax.Target is MemberAccessExpressionSyntax memberTarget)
        {
            if (TryBindStaticMethodCall(memberTarget, arguments, syntax.Arguments) is BoundExpression staticCall)
                return staticCall;
            BoundExpression? qualifiedCall = TryBindQualifiedCallExpression(
                memberTarget,
                arguments,
                syntax.Arguments);
            return qualifiedCall ?? BindMethodCallExpression(memberTarget, arguments, syntax.Arguments);
        }

        if (syntax.Target is not NameExpressionSyntax name)
        {
            _diagnostics.Report(GetLocation(syntax.Target), "call target must be a function, method, or struct name");
            return new BoundErrorExpression();
        }

        StructTypeSymbol? structType = _fileScope.ResolveType(
            name.IdentifierToken.Text,
            name.IdentifierToken.Location,
            _diagnostics) as StructTypeSymbol;
        if (structType is not null)
        {
            if (structType.IsAbstract)
            {
                _diagnostics.Report(name.IdentifierToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated");
                return new BoundErrorExpression();
            }
            if (structType.Constructors.IsEmpty && arguments.IsEmpty)
                return new BoundStructConstructionExpression(structType, []) { IsDefaultInitialization = true };
            FunctionSymbol? constructor = ResolveConstructor(structType, arguments, syntax.Arguments, name.IdentifierToken.Location);
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
            FunctionSymbol? method = containingType.FindInstanceMethod(
                name.IdentifierToken.Text,
                receiverIsReadonly: _function.IsReadonly);
            if (method is null &&
                _function.IsReadonly &&
                containingType.FindMethod(name.IdentifierToken.Text) is { IsStatic: false } mutableMethod)
            {
                _diagnostics.Report(name.IdentifierToken.Location, $"readonly method '{_function.Name}' cannot call mutable method '{mutableMethod.Name}' through 'this'");
                return new BoundErrorExpression();
            }
            if (method is not null)
            {
                if (_bindingBaseConstructorArguments)
                {
                    _diagnostics.Report(name.IdentifierToken.Location, "the derived object cannot be used in base constructor arguments");
                    return new BoundErrorExpression();
                }
                if (!method.IsPublic && !ReferenceEquals(containingType, method.ContainingType))
                {
                    _diagnostics.Report(name.IdentifierToken.Location, $"method '{method.Name}' is private in struct '{method.ContainingType!.Name}'");
                    return new BoundErrorExpression();
                }
                if (_function.IsStatic)
                {
                    _diagnostics.Report(name.IdentifierToken.Location, $"static method '{_function.Name}' cannot call instance method '{method.Name}' without an explicit instance");
                    return new BoundErrorExpression();
                }
                if (method.IsStatic)
                {
                    _diagnostics.Report(name.IdentifierToken.Location, $"static method '{method.Name}' must be accessed through type '{containingType.Name}'");
                    return new BoundErrorExpression();
                }
                arguments = ValidateFunctionArguments(method, arguments, syntax.Arguments, name.IdentifierToken.Location);
                PointerTypeSymbol thisType = BuiltinTypes.PointerTo(containingType, isReadonly: _function.IsReadonly);
                return new BoundMethodCallExpression(
                    new BoundThisExpression(containingType, thisType),
                    method,
                    arguments,
                    IsPointerAccess: true);
            }
        }

        FunctionSymbol? function = _fileScope.ResolveFunction(
            name.IdentifierToken.Text,
            name.IdentifierToken.Location,
            _diagnostics,
            out bool functionResolutionDiagnostic);
        if (function is null)
        {
            if (!functionResolutionDiagnostic)
            {
                _diagnostics.Report(name.IdentifierToken.Location, $"unknown function '{name.IdentifierToken.Text}'");
            }

            return new BoundErrorExpression();
        }

        arguments = ValidateFunctionArguments(function, arguments, syntax.Arguments, name.IdentifierToken.Location);
        return new BoundCallExpression(function, arguments);
    }

    private BoundExpression? TryBindStaticMethodCall(MemberAccessExpressionSyntax target, ImmutableArray<BoundExpression> arguments, ImmutableArray<ExpressionSyntax> argumentSyntax)
    {
        if (!TryGetDottedName(target, out ImmutableArray<SyntaxToken> parts) || parts.Length < 2)
            return null;
        string[] typeParts = parts.Take(parts.Length - 1).Select(token => token.Text).ToArray();
        TypeSymbol? resolved = typeParts.Length == 1
            ? _fileScope.ResolveType(typeParts[0], parts[0].Location, _diagnostics)
            : _fileScope.ResolveQualifiedType(typeParts);
        if (resolved is not StructTypeSymbol structType)
            return null;
        FunctionSymbol? method = structType.FindMethod(parts[^1].Text);
        if (method is null || !method.IsStatic)
            return null;
        if (!method.IsPublic && !ReferenceEquals(_function.ContainingType, method.ContainingType))
        {
            _diagnostics.Report(parts[^1].Location, $"static method '{method.Name}' is private in struct '{method.ContainingType!.Name}'");
            return new BoundErrorExpression();
        }
        arguments = ValidateFunctionArguments(method, arguments, argumentSyntax, parts[^1].Location);
        return new BoundCallExpression(method, arguments);
    }

    private BoundExpression? TryBindQualifiedCallExpression(
        MemberAccessExpressionSyntax target,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax)
    {
        if (!TryGetDottedName(target, out ImmutableArray<SyntaxToken> nameParts))
        {
            return null;
        }

        string firstName = nameParts[0].Text;
        if (_scope.Lookup(firstName) is not null)
        {
            return null;
        }

        if (_function.ContainingType?.FindField(firstName) is not null)
        {
            return null;
        }

        if (!_fileScope.CanStartQualifiedName(firstName))
        {
            return null;
        }

        string[] parts = nameParts.Select(part => part.Text).ToArray();
        StructTypeSymbol? structType = _fileScope.ResolveQualifiedType(parts) as StructTypeSymbol;
        if (structType is not null)
        {
            if (structType.IsAbstract)
            {
                _diagnostics.Report(target.MemberToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated");
                return new BoundErrorExpression();
            }
            if (structType.Constructors.IsEmpty && arguments.IsEmpty)
                return new BoundStructConstructionExpression(structType, []) { IsDefaultInitialization = true };
            FunctionSymbol? constructor = ResolveConstructor(structType, arguments, argumentSyntax, target.MemberToken.Location);
            if (constructor is null)
            {
                _diagnostics.Report(
                    target.MemberToken.Location,
                    $"struct '{structType.Name}' does not declare a constructor; use '{structType.Name} {{ ... }}' for positional construction");
                return new BoundErrorExpression();
            }

            if (!constructor.IsPublic && !ReferenceEquals(_function.ContainingType, structType))
            {
                _diagnostics.Report(target.MemberToken.Location, $"constructor '{structType.Name}' is private");
            }

            arguments = ValidateFunctionArguments(constructor, arguments, argumentSyntax, target.MemberToken.Location);
            return new BoundConstructorCallExpression(structType, constructor, arguments);
        }

        FunctionSymbol? function = _fileScope.ResolveQualifiedFunction(
            parts,
            target.MemberToken.Location,
            _diagnostics,
            out bool resolutionDiagnostic);
        if (function is not null)
        {
            arguments = ValidateFunctionArguments(function, arguments, argumentSyntax, target.MemberToken.Location);
            return new BoundCallExpression(function, arguments);
        }

        if (!resolutionDiagnostic)
        {
            _diagnostics.Report(
                target.MemberToken.Location,
                $"unknown function or struct '{string.Join('.', parts)}'");
        }

        return new BoundErrorExpression();
    }

    private static bool TryGetDottedName(
        ExpressionSyntax syntax,
        out ImmutableArray<SyntaxToken> parts)
    {
        var builder = ImmutableArray.CreateBuilder<SyntaxToken>();
        if (!CollectDottedNameParts(syntax, builder))
        {
            parts = [];
            return false;
        }

        parts = builder.ToImmutable();
        return parts.Length > 0;
    }

    private static bool CollectDottedNameParts(
        ExpressionSyntax syntax,
        ImmutableArray<SyntaxToken>.Builder parts)
    {
        switch (syntax)
        {
            case NameExpressionSyntax name:
                parts.Add(name.IdentifierToken);
                return true;
            case MemberAccessExpressionSyntax { OperatorToken.Kind: SyntaxKind.DotToken } member:
                if (!CollectDottedNameParts(member.Receiver, parts))
                {
                    return false;
                }

                parts.Add(member.MemberToken);
                return true;
            default:
                return false;
        }
    }

    private BoundExpression BindMethodCallExpression(
        MemberAccessExpressionSyntax target,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax)
    {
        BoundExpression receiver = BindExpression(target.Receiver);
        if (receiver.Type is ArrayTypeSymbol array && target.OperatorToken.Kind == SyntaxKind.DotToken && target.MemberToken.Text == "GetLength")
        {
            if (arguments.Length != 1 || !ReferenceEquals(arguments[0].Type, BuiltinTypes.Int))
            {
                _diagnostics.Report(target.MemberToken.Location, "GetLength requires one int dimension argument");
                return new BoundErrorExpression();
            }
            if (_constants.TryFold(arguments[0], out object? dimension) &&
                (SemanticAnalyzer.ToInteger(dimension) < 0 || SemanticAnalyzer.ToInteger(dimension) >= array.Rank))
                _diagnostics.Report(target.MemberToken.Location, $"GetLength dimension must be between 0 and {array.Rank - 1}");
            return new BoundArrayMetadataExpression(receiver, "GetLength", arguments[0]);
        }
        bool pointerAccess = target.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
        InterfaceTypeSymbol? interfaceType = pointerAccess
            ? (receiver.Type as PointerTypeSymbol)?.ElementType as InterfaceTypeSymbol
            : receiver.Type as InterfaceTypeSymbol;
        if (interfaceType is not null)
        {
            FunctionSymbol? interfaceMethod = ResolveInterfaceMethod(interfaceType, target.MemberToken.Text, arguments,
                IsReadonlyReceiver(receiver, pointerAccess), target.MemberToken.Location);
            if (interfaceMethod is null)
            {
                return new BoundErrorExpression();
            }
            if (IsReadonlyReceiver(receiver, pointerAccess) && !interfaceMethod.IsReadonly)
            {
                _diagnostics.Report(target.MemberToken.Location, $"mutable interface method '{interfaceMethod.Name}' cannot be called on a readonly '{interfaceType.Name}' receiver");
                return new BoundErrorExpression();
            }
            arguments = ValidateFunctionArguments(interfaceMethod, arguments, argumentSyntax, target.MemberToken.Location);
            return new BoundInterfaceMethodCallExpression(receiver, interfaceType, interfaceMethod, arguments, pointerAccess);
        }
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

        bool hasReadonlyReceiver =
            (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
            (!pointerAccess && IsAddressable(receiver) && !IsWritable(receiver));

        FunctionSymbol? method = structType.FindInstanceMethod(target.MemberToken.Text, hasReadonlyReceiver || _function.IsReadonly);
        // Prefer a readonly overload in readonly code, but retain a mutable
        // candidate so the effect checker can report a disallowed call.
        if (method is null && !hasReadonlyReceiver)
            method = structType.FindInstanceMethod(target.MemberToken.Text, receiverIsReadonly: false);
        if (method is null)
        {
            FunctionSymbol? namedMethod = structType.FindMethod(target.MemberToken.Text);
            if (namedMethod?.IsStatic == true)
            {
                _diagnostics.Report(target.MemberToken.Location, $"static method '{namedMethod.Name}' must be accessed through type '{structType.Name}'");
            }
            else if (hasReadonlyReceiver && namedMethod is not null)
            {
                _diagnostics.Report(
                    target.MemberToken.Location,
                    $"mutable method '{namedMethod.Name}' cannot be called on a readonly '{structType.Name}' receiver");
            }
            else
            {
                _diagnostics.Report(
                    target.MemberToken.Location,
                    $"struct '{structType.Name}' does not contain method '{target.MemberToken.Text}'");
            }
            return new BoundErrorExpression();
        }

        if (!method.IsPublic && !ReferenceEquals(_function.ContainingType, method.ContainingType))
        {
            _diagnostics.Report(
                target.MemberToken.Location,
                $"method '{method.Name}' is private in struct '{method.ContainingType!.Name}'");
        }

        arguments = ValidateFunctionArguments(method, arguments, argumentSyntax, target.MemberToken.Location);
        return new BoundMethodCallExpression(receiver, method, arguments, pointerAccess);
    }

    private BoundExpression BindNewExpression(NewExpressionSyntax syntax)
    {
        TypeSymbol type = TypeResolver.Resolve(syntax.Type, _fileScope, _diagnostics);

        if (syntax.IsArrayAllocation)
        {
            return BindArrayCreation(type, syntax.Arguments, syntax.Type.NameToken.Location, syntax.OpenDelimiterToken.Location, ArrayStorageKind.Heap);
        }

        if (type is not StructTypeSymbol structType)
        {
            if (!ReferenceEquals(type, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, $"'new' requires a struct type or array element type, but has type '{type.Name}'");
            }

            return new BoundErrorExpression();
        }

        if (structType.IsAbstract)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated");
        }

        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        FunctionSymbol? constructor = null;
        if (!syntax.IsPositionalInitialization && structType.Constructors.IsEmpty && arguments.IsEmpty)
            return new BoundNewExpression(structType, null, [], true, BuiltinTypes.PointerTo(structType))
                { IsDefaultInitialization = true };
        if (syntax.IsPositionalInitialization)
        {
            arguments = ValidatePositionalArguments(structType, arguments, syntax.Arguments, syntax.NewKeyword.Location);
        }
        else
        {
            constructor = ResolveConstructor(structType, arguments, syntax.Arguments, syntax.NewKeyword.Location);
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
        if (ReferenceEquals(pointer.Type, BuiltinTypes.Null))
            pointer = ContextualizeNull(pointer, BuiltinTypes.PointerTo(BuiltinTypes.Void));
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
                destructor = structType.FindDestructor();
            }
        }
        else if (pointer.Type is ArrayTypeSymbol arrayType)
        {
            if (arrayType.ElementType is StructTypeSymbol structure) destructor = structure.FindDestructor();
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
        ImmutableArray<FieldSymbol> fields = structType.AllInstanceFields;
        bool hasMissingRequiredFields = arguments.Length < fields.Length &&
            fields.Skip(arguments.Length).Any(field => field.Initializer is null);
        if (arguments.Length > fields.Length || hasMissingRequiredFields)
        {
            _diagnostics.Report(
                location,
                $"struct '{structType.Name}' expects {fields.Length} positional value(s), but {arguments.Length} were provided");
        }

        var convertedArguments = arguments.ToBuilder();
        int count = Math.Min(arguments.Length, fields.Length);
        for (int index = 0; index < count; index++)
        {
            FieldSymbol field = fields[index];
            TypeSymbol fieldType = field.Type;
            BoundExpression argument = ContextualizeConversion(arguments[index], fieldType);
            convertedArguments[index] = argument;

            if (!field.IsPublic && !ReferenceEquals(_function.ContainingType, field.ContainingType))
            {
                _diagnostics.Report(
                    GetLocation(argumentSyntax[index]),
                    $"field '{field.Name}' is private in struct '{field.ContainingType.Name}'");
            }

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
            BoundExpression argument = ContextualizeConversion(arguments[index], parameterType);
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

    private FunctionSymbol? ResolveInterfaceMethod(InterfaceTypeSymbol type, string name,
        ImmutableArray<BoundExpression> arguments, bool readonlyReceiver, TextLocation location)
    {
        FunctionSymbol[] candidates = type.FindMethods(name).ToArray();
        if (candidates.Length == 0)
        {
            _diagnostics.Report(location, $"interface '{type.Name}' does not contain method '{name}'");
            return null;
        }
        // Preserve the established argument/readonly diagnostics for a single candidate.
        if (candidates.Length == 1) return candidates[0];
        var matches = candidates.Where(candidate => candidate.Parameters.Length == arguments.Length &&
                (!readonlyReceiver || candidate.IsReadonly))
            .Select(candidate => (Method: candidate, Costs: candidate.Parameters.Zip(arguments)
                .Select(pair => GetArgumentConversionCost(pair.First.Type, pair.Second)).ToArray()))
            .Where(candidate => candidate.Costs.All(cost => cost.HasValue))
            .Select(candidate => (candidate.Method, Costs: candidate.Costs.Select(cost => cost!.Value).ToArray())).ToArray();
        FunctionSymbol[] best = matches.Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other.Method, candidate.Method) && IsBetterConversionSequence(other.Costs, candidate.Costs)))
            .Select(candidate => candidate.Method).ToArray();
        if (best.Length == 1) return best[0];
        _diagnostics.Report(location, best.Length == 0
            ? $"no interface method '{type.Name}.{name}' matches the provided arguments"
            : $"interface method call '{type.Name}.{name}' is ambiguous");
        return null;
    }

    private FunctionSymbol? ResolveConstructor(
        StructTypeSymbol type,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location)
    {
        if (type.Constructors.IsEmpty)
            return null;

        var matches = type.Constructors
            .Where(candidate => candidate.Parameters.Length == arguments.Length)
            .Select(candidate => new
            {
                Constructor = candidate,
                Costs = candidate.Parameters.Zip(arguments)
                    .Select(pair => GetArgumentConversionCost(pair.First.Type, pair.Second))
                    .ToArray(),
            })
            .Where(candidate => candidate.Costs.All(cost => cost.HasValue))
            .Select(candidate => new
            {
                candidate.Constructor,
                Costs = candidate.Costs.Select(cost => cost!.Value).ToArray(),
            })
            .ToArray();
        if (matches.Length == 1)
            return matches[0].Constructor;
        if (matches.Length == 0)
        {
            _diagnostics.Report(location, $"no constructor of struct '{type.Name}' matches the provided arguments");
            return null;
        }

        FunctionSymbol[] bestMatches = matches
            .Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other, candidate) &&
                IsBetterConversionSequence(other.Costs, candidate.Costs)))
            .Select(candidate => candidate.Constructor)
            .ToArray();
        if (bestMatches.Length == 1)
            return bestMatches[0];

        _diagnostics.Report(location, $"constructor call for struct '{type.Name}' is ambiguous");
        return null;
    }

    private IndexerSymbol? ResolveIndexer(
        IEnumerable<IndexerSymbol> candidates,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName) =>
        ResolveIndexerCore(candidates, indexer => indexer.Parameters, arguments, location, ownerName);

    private InterfaceIndexerSymbol? ResolveIndexer(
        IEnumerable<InterfaceIndexerSymbol> candidates,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName) =>
        ResolveIndexerCore(candidates, indexer => indexer.Parameters, arguments, location, ownerName);

    private TIndexer? ResolveIndexerCore<TIndexer>(
        IEnumerable<TIndexer> candidates,
        Func<TIndexer, ImmutableArray<ParameterSymbol>> getParameters,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName)
        where TIndexer : class
    {
        var matches = candidates
            .Where(candidate => getParameters(candidate).Length == arguments.Length)
            .Select(candidate => new
            {
                Indexer = candidate,
                Costs = getParameters(candidate).Zip(arguments)
                    .Select(pair => GetArgumentConversionCost(pair.First.Type, pair.Second))
                    .ToArray(),
            })
            .Where(candidate => candidate.Costs.All(cost => cost.HasValue))
            .Select(candidate => new
            {
                candidate.Indexer,
                Costs = candidate.Costs.Select(cost => cost!.Value).ToArray(),
            })
            .ToArray();
        if (matches.Length == 1)
            return matches[0].Indexer;
        if (matches.Length == 0)
        {
            _diagnostics.Report(location, $"no indexer of type '{ownerName}' matches the provided arguments");
            return null;
        }

        TIndexer[] bestMatches = matches
            .Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other, candidate) &&
                IsBetterConversionSequence(other.Costs, candidate.Costs)))
            .Select(candidate => candidate.Indexer)
            .ToArray();
        if (bestMatches.Length == 1)
            return bestMatches[0];

        _diagnostics.Report(location, $"indexer access on type '{ownerName}' is ambiguous");
        return null;
    }

    private static bool IsBetterConversionSequence(int[] candidate, int[] other)
    {
        bool strictlyBetter = false;
        for (int index = 0; index < candidate.Length; index++)
        {
            if (candidate[index] > other[index])
                return false;
            strictlyBetter |= candidate[index] < other[index];
        }
        return strictlyBetter;
    }

    private static int? GetArgumentConversionCost(TypeSymbol parameterType, BoundExpression argument)
    {
        int? standardCost = TypeFacts.GetImplicitConversionCost(parameterType, argument.Type);
        if (standardCost is not null)
            return standardCost;

        if (parameterType is not ReferenceTypeSymbol referenceType)
            return null;
        if (argument is BoundThisExpression @this)
        {
            if (@this.PointerType.IsReadonly && !referenceType.IsReadonly)
                return null;
            return TypeFacts.GetReferenceBindingCost(referenceType, @this.StructType);
        }
        if (!referenceType.IsReadonly && argument is BoundReferenceDereferenceExpression { ReferenceType.IsReadonly: true })
            return null;
        return TypeFacts.GetReferenceBindingCost(referenceType, argument.Type);
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

    private BoundExpression ContextualizeConversion(BoundExpression expression, TypeSymbol targetType)
    {
        if (targetType is ReferenceTypeSymbol referenceType)
        {
            if (expression is BoundThisExpression @this)
            {
                if (@this.PointerType.IsReadonly && !referenceType.IsReadonly)
                    return expression;
                return TypeFacts.GetReferenceBindingCost(referenceType, @this.StructType) is not null
                    ? new BoundReferenceConversionExpression(expression, referenceType)
                    : expression;
            }
            if (!referenceType.IsReadonly && !IsWritable(expression))
                return expression;
            if (TypeFacts.GetReferenceBindingCost(referenceType, expression.Type) is null)
                return expression;

            if (referenceType.ElementType is InterfaceTypeSymbol referenceInterface &&
                expression.Type is StructTypeSymbol referenceSource &&
                referenceSource.Implements(referenceInterface))
            {
                expression = new BoundInterfaceConversionExpression(expression, referenceSource, referenceInterface);
            }

            if (IsAddressable(expression) || referenceType.IsReadonly || expression is BoundInterfaceConversionExpression)
                return new BoundReferenceConversionExpression(expression, referenceType);
            return expression;
        }

        expression = ContextualizeNull(expression, targetType);
        return targetType is InterfaceTypeSymbol @interface && expression.Type is StructTypeSymbol source && source.Implements(@interface)
            ? new BoundInterfaceConversionExpression(expression, source, @interface)
            : expression;
    }

    private void ValidateArrayElementType(TypeSymbol elementType, TextLocation location)
    {
        if (ReferenceEquals(elementType, BuiltinTypes.Void))
        {
            _diagnostics.Report(location, "array element type cannot be 'void'");
        }

        if (elementType is StructTypeSymbol { IsAbstract: true } structType)
        {
            _diagnostics.Report(
                location,
                $"array element type '{structType.Name}' is abstract");
        }
    }

    private void ValidateArrayLength(BoundExpression length, ExpressionSyntax syntax)
    {
        if (TypeFacts.IsInteger(length.Type) && _constants.TryFold(length, out object? value) &&
            (SemanticAnalyzer.ToInteger(value) < 0 || SemanticAnalyzer.ToInteger(value) > int.MaxValue))
            _diagnostics.Report(GetLocation(syntax), "array length must be between zero and int.MaxValue");
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
        BoundAssignmentExpression assignment => GetArrayStorage(assignment.Expression),
        _ => ArrayStorageKind.Unknown,
    };

    private BoundScope? GetStackArrayScope(BoundExpression expression) => expression switch
    {
        BoundArrayCreationExpression { Storage: ArrayStorageKind.Stack } => _scope,
        BoundVariableExpression { Variable: LocalVariableSymbol local } => _stackArrayScopes.GetValueOrDefault(local),
        BoundAssignmentExpression assignment => GetStackArrayScope(assignment.Expression),
        _ => null,
    };

    private void TrackArrayAssignment(LocalVariableSymbol local, BoundExpression expression, TextLocation location)
    {
        ArrayStorageKind storage = GetArrayStorage(expression);
        local.ArrayStorage = storage;
        if (storage != ArrayStorageKind.Stack)
        {
            _stackArrayScopes.Remove(local);
            return;
        }
        BoundScope origin = GetStackArrayScope(expression) ?? _scope;
        _stackArrayScopes[local] = origin;
        for (BoundScope? scope = _localScopes[local]; scope is not null; scope = scope.Parent)
            if (ReferenceEquals(scope, origin)) return;
        _diagnostics.Report(location, "stack array cannot escape its allocation scope through this assignment");
    }

    private readonly record struct ArrayState(ArrayStorageKind Storage, BoundScope? Scope);

    private Dictionary<LocalVariableSymbol, ArrayState> CloneArrayState() => _localScopes.Keys
        .Where(local => local.Type is ArrayTypeSymbol)
        .ToDictionary(local => local, local => new ArrayState(local.ArrayStorage, _stackArrayScopes.GetValueOrDefault(local)));

    private void RestoreArrayState(Dictionary<LocalVariableSymbol, ArrayState> state)
    {
        foreach (LocalVariableSymbol local in _localScopes.Keys.Where(local => local.Type is ArrayTypeSymbol))
        {
            ArrayState value = state.GetValueOrDefault(local);
            local.ArrayStorage = value.Storage;
            if (value.Scope is not null) _stackArrayScopes[local] = value.Scope;
            else _stackArrayScopes.Remove(local);
        }
    }

    private static Dictionary<LocalVariableSymbol, ArrayState> MergeArrayState(
        Dictionary<LocalVariableSymbol, ArrayState> left, Dictionary<LocalVariableSymbol, ArrayState> right)
    {
        var merged = new Dictionary<LocalVariableSymbol, ArrayState>(left);
        foreach (LocalVariableSymbol local in left.Keys.Union(right.Keys))
        {
            ArrayState a = left.GetValueOrDefault(local);
            ArrayState b = right.GetValueOrDefault(local);
            if (a.Storage == ArrayStorageKind.Stack || b.Storage == ArrayStorageKind.Stack)
            {
                // Preserve the shortest possible lifetime at a control-flow merge.
                BoundScope? scope = a.Scope;
                for (BoundScope? candidate = b.Scope; candidate is not null; candidate = candidate.Parent)
                    if (ReferenceEquals(candidate, scope)) { scope = b.Scope; break; }
                merged[local] = new ArrayState(ArrayStorageKind.Stack, scope ?? b.Scope);
            }
            else
                merged[local] = new ArrayState(a.Storage == b.Storage ? a.Storage : ArrayStorageKind.Unknown, null);
        }
        return merged;
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

            if (left is PointerTypeSymbol lp && !ReferenceEquals(lp.ElementType, BuiltinTypes.Void) && TypeFacts.IsInteger(right) && operatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken)
            {
                return left;
            }

            if (right is PointerTypeSymbol rp && !ReferenceEquals(rp.ElementType, BuiltinTypes.Void) && TypeFacts.IsInteger(left) && operatorKind == SyntaxKind.PlusToken)
            {
                return right;
            }

            if (left is PointerTypeSymbol leftPointer && right is PointerTypeSymbol rightPointer &&
                !ReferenceEquals(leftPointer.ElementType, BuiltinTypes.Void) &&
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
        ThisExpressionSyntax @this => @this.ThisKeyword.Location,
        ParenthesizedExpressionSyntax parenthesized => parenthesized.OpenParenthesisToken.Location,
        UnaryExpressionSyntax unary => unary.OperatorToken.Location,
        PostfixUnaryExpressionSyntax postfix => postfix.OperatorToken.Location,
        BinaryExpressionSyntax binary => binary.OperatorToken.Location,
        AssignmentExpressionSyntax assignment => assignment.OperatorToken.Location,
        CallExpressionSyntax call => call.OpenParenthesisToken.Location,
        MemberAccessExpressionSyntax member => member.OperatorToken.Location,
        IndexExpressionSyntax index => index.OpenBracketToken.Location,
        StructPositionalConstructionExpressionSyntax construction => construction.Type.NameToken.Location,
        StackArrayCreationExpressionSyntax stackArray => stackArray.OpenBracketToken.Location,
        NewExpressionSyntax @new => @new.NewKeyword.Location,
        FreeExpressionSyntax free => free.FreeKeyword.Location,
        TypeLayoutExpressionSyntax layout => layout.Keyword.Location,
        CastExpressionSyntax cast => cast.CastKeyword.Location,
        _ => throw new InvalidOperationException($"Unexpected expression syntax '{syntax.Kind}'."),
    };

    private static bool IsAddressable(BoundExpression expression) => expression switch
    {
        BoundVariableExpression => true,
        BoundStaticFieldExpression => true,
        BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } => true,
        BoundReferenceDereferenceExpression => true,
        BoundMemberAccessExpression { IsPointerAccess: true } => true,
        BoundMemberAccessExpression member => IsAddressable(member.Receiver),
        BoundIndexExpression => true,
        _ => false,
    };

    private bool IsWritable(BoundExpression expression)
    {
        if (!IsAddressable(expression))
        {
            return false;
        }

        return expression switch
        {
            BoundVariableExpression variable => !variable.Variable.IsReadonly,
            BoundStaticFieldExpression field => !field.Field.IsReadonly,
            BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken, Operand.Type: PointerTypeSymbol { IsReadonly: true } } => false,
            BoundReferenceDereferenceExpression { ReferenceType.IsReadonly: true } => false,
            BoundIndexExpression { Receiver.Type: PointerTypeSymbol { IsReadonly: true } } => false,
            BoundMemberAccessExpression { Field.IsReadonly: true } member => CanInitializeReadonlyField(member),
            BoundMemberAccessExpression
            {
                IsPointerAccess: true,
                Receiver.Type: PointerTypeSymbol { IsReadonly: true },
            } => false,
            BoundMemberAccessExpression { IsPointerAccess: true } => true,
            BoundMemberAccessExpression member => IsWritable(member.Receiver),
            _ => true,
        };
    }

    private bool IsReadonlyReceiver(BoundExpression receiver, bool pointerAccess) =>
        (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
        (!pointerAccess && IsAddressable(receiver) && !IsWritable(receiver));

    private bool CanInitializeReadonlyField(BoundMemberAccessExpression member) =>
        _function.FunctionKind == FunctionKind.Constructor &&
        ReferenceEquals(_function.ContainingType, member.Field.ContainingType) &&
        member.Receiver is BoundThisExpression;

    private static bool AlwaysReturns(BoundStatement statement) => BoundControlFlow.AlwaysReturns(statement);
}
