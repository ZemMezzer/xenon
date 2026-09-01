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
    private readonly SemanticInfoStore _semanticInfo;
    private readonly CancellationToken _cancellationToken;
    private readonly GenericFunctionSpecializer? _genericSpecializer;
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
    private readonly Dictionary<FieldSymbol, LocalVariableSymbol> _requiredFields = [];
    private ExpressionSyntax? _initializationTarget;
    private ExpressionSyntax? _fieldReceiverSyntax;
    private sealed record ExpressionFlow(HashSet<LocalVariableSymbol> Assigned, Dictionary<LocalVariableSymbol, ArrayState> Arrays);
    private readonly Dictionary<BoundExpression, (ExpressionFlow? True, ExpressionFlow? False)> _booleanFlows = new(ReferenceEqualityComparer.Instance);

    private ExpressionFlow CaptureExpressionFlow() => new(CloneDefinitelyAssigned(), CloneArrayState());
    private void RestoreExpressionFlow(ExpressionFlow flow)
    {
        RestoreDefinitelyAssigned(flow.Assigned);
        RestoreArrayState(flow.Arrays);
    }
    private static ExpressionFlow? MergeExpressionFlow(ExpressionFlow? a, ExpressionFlow? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new(a.Assigned.Intersect(b.Assigned).ToHashSet(), MergeArrayState(a.Arrays, b.Arrays));
    }
    private (ExpressionFlow? True, ExpressionFlow? False) BooleanFlow(BoundExpression expression)
    {
        if (_booleanFlows.TryGetValue(expression, out var flow)) return flow;
        if (expression is BoundUnaryExpression { OperatorKind: SyntaxKind.BangToken } unary)
        {
            var operand = BooleanFlow(unary.Operand);
            return (operand.False, operand.True);
        }
        var current = CaptureExpressionFlow();
        if (_constants.TryFold(expression, out object? value) && value is bool known)
            return known ? (current, null) : (null, current);
        return (current, current);
    }

    public FunctionBodyBinder(FunctionSymbol function, FileSymbolScope fileScope, DiagnosticBag diagnostics,
        ConstantEvaluationContext constants, SemanticInfoStore semanticInfo, CancellationToken cancellationToken = default)
        : this(function, fileScope, diagnostics, constants, semanticInfo, null, cancellationToken)
    {
    }

    internal FunctionBodyBinder(FunctionSymbol function, FileSymbolScope fileScope, DiagnosticBag diagnostics,
        ConstantEvaluationContext constants, SemanticInfoStore semanticInfo,
        GenericFunctionSpecializer? genericSpecializer, CancellationToken cancellationToken = default)
    {
        _function = function;
        _fileScope = fileScope;
        _diagnostics = diagnostics;
        _constants = constants;
        _semanticInfo = semanticInfo;
        _genericSpecializer = genericSpecializer;
        _cancellationToken = cancellationToken;
        if (function.FunctionKind == FunctionKind.InstanceInitializer && function.ContainingType is StructTypeSymbol initializedType)
            foreach (FieldSymbol field in initializedType.Fields.Where(field => TypeFacts.ContainsReferenceStorage(field.Type)))
                _requiredFields.Add(field, new LocalVariableSymbol(field.Name, field.Type, _function, false));

        foreach (ParameterSymbol parameter in function.Parameters)
        {
            _scope.TryDeclare(parameter);
        }
    }

    public BoundBlockStatement BindBody(BlockStatementSyntax body)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_function.FunctionKind == FunctionKind.Constructor && _function.ContainingType is StructTypeSymbol owner)
        {
            foreach (FieldSymbol field in owner.Fields.Where(field => field.Declaration.Initializer is null && TypeFacts.ContainsReferenceStorage(field.Type)))
                _requiredFields.Add(field, new LocalVariableSymbol(field.Name, field.Type, _function, false));
            if (owner.BaseType is { Constructors.IsEmpty: true } defaultBase)
                ValidateDefaultInitialization(defaultBase, body.OpenBraceToken.Location);
        }
        BoundStatement? baseConstructorCall = null;
        bool callsThisConstructor = false;
        ConstructorDeclarationSyntax? constructorSyntax =
            _function.FunctionKind == FunctionKind.Constructor
                ? _function.Declaration as ConstructorDeclarationSyntax
                : null;

        if (constructorSyntax is { HasThisInitializer: true } &&
            _function.ContainingStruct is StructTypeSymbol thisType)
        {
            ImmutableArray<ExpressionSyntax> initializerArguments = constructorSyntax.BaseArguments;
            _bindingBaseConstructorArguments = true;
            ImmutableArray<BoundExpression> arguments;
            try
            {
                arguments = initializerArguments.Select(BindExpression).ToImmutableArray();
            }
            finally
            {
                _bindingBaseConstructorArguments = false;
            }
            FunctionSymbol? target = ResolveConstructor(thisType, arguments, initializerArguments,
                constructorSyntax.BaseKeyword!.Location);
            if (target is not null)
            {
                if (ReferenceEquals(target, _function))
                {
                    _diagnostics.Report(constructorSyntax.BaseKeyword.Location,
                        "a constructor cannot chain directly to itself", DiagnosticIds.MissingConstructor);
                }
                else
                {
                    arguments = ValidateFunctionArguments(target, arguments, initializerArguments,
                        constructorSyntax.BaseKeyword.Location);
                    baseConstructorCall = new BoundExpressionStatement(
                        new BoundBaseLifecycleCallExpression(target, arguments));
                    callsThisConstructor = true;
                    _requiredFields.Clear();
                }
            }
        }
        else if (_function.FunctionKind == FunctionKind.Constructor &&
                 _function.ContainingStruct?.BaseType is StructTypeSymbol baseType &&
                 !baseType.Constructors.IsEmpty)
        {
            ConstructorDeclarationSyntax? syntax = constructorSyntax;
            ImmutableArray<ExpressionSyntax> baseArguments = syntax?.BaseArguments ?? [];
            TextLocation location = syntax?.IdentifierToken.Location ?? _function.ContainingType!.Declaration.IdentifierToken.Location;
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
                    _diagnostics.Report(syntax?.BaseKeyword?.Location ?? location, $"constructor '{baseType.Name}' is private",
                        DiagnosticIds.InaccessibleSymbol);
                }
                arguments = ValidateFunctionArguments(baseConstructor, arguments, baseArguments, location);
                baseConstructorCall = new BoundExpressionStatement(new BoundBaseLifecycleCallExpression(baseConstructor, arguments));
            }
        }
        else if (_function.FunctionKind == FunctionKind.Constructor &&
                 _function.ContainingStruct?.BaseType is StructTypeSymbol baseWithoutConstructor)
        {
            ConstructorDeclarationSyntax? syntax = constructorSyntax;
            if (syntax is not null && !syntax.BaseArguments.IsEmpty)
            {
                _diagnostics.Report(syntax.BaseKeyword?.Location ?? syntax.IdentifierToken.Location, $"base struct '{baseWithoutConstructor.Name}' does not declare a constructor",
                    DiagnosticIds.MissingConstructor);
            }
        }

        BoundBlockStatement boundBody = BindBlockStatement(body, createScope: false);
        RecordScope(body, _scope);
        if (!AlwaysReturns(boundBody)) ValidateRequiredFields(body.CloseBraceToken.Location);
        if (_function.FunctionKind == FunctionKind.Constructor &&
            _function.ContainingType is StructTypeSymbol constructedType)
        {
            var statements = ImmutableArray.CreateBuilder<BoundStatement>();
            if (baseConstructorCall is not null)
                statements.Add(baseConstructorCall);
            else if (constructedType.BaseType is StructTypeSymbol defaultBase)
                AddDefaultInstanceInitializerCalls(defaultBase, statements);
            if (!callsThisConstructor && constructedType.InstanceInitializer is FunctionSymbol initializer)
            {
                statements.Add(new BoundExpressionStatement(
                    new BoundBaseLifecycleCallExpression(initializer, [])));
            }
            statements.AddRange(boundBody.Statements);
            boundBody = new BoundBlockStatement(statements.ToImmutable());
        }
        else if (_function.FunctionKind == FunctionKind.Destructor && _function.ContainingStruct?.BaseType?.FindDestructor() is FunctionSymbol baseDestructor)
        {
            ValidateDestructorAccess(baseDestructor, body.OpenBraceToken.Location);
            // Runs after local cleanup on both fallthrough and explicit returns.
            boundBody = boundBody with { ExitCleanup = new BoundBaseLifecycleCallExpression(baseDestructor, []) };
        }

        if (!TypeIdentity.AreSame(_function.ReturnType, BuiltinTypes.Void) && !AlwaysReturns(boundBody))
        {
            _diagnostics.Report(
                body.CloseBraceToken.Location,
                $"not all code paths in function '{_function.Name}' return a value",
                DiagnosticIds.MissingReturn);
        }

        return boundBody;
    }

    internal BoundExpression? BindFieldInitializer(FieldSymbol field)
    {
        ExpressionSyntax? syntax = field.Declaration.Initializer;
        if (syntax is null)
            return null;

        BoundExpression initializer = ContextualizeConversion(BindExpression(syntax), field.Type);
        SetConvertedType(syntax, initializer.Type);
        if (!TypeFacts.CanAssign(field.Type, initializer.Type))
            ReportCannotConvert(GetLocation(syntax), initializer.Type, field.Type);

        if (field.Type is ArrayTypeSymbol && GetArrayStorage(initializer) == ArrayStorageKind.Stack)
            _diagnostics.Report(GetLocation(syntax), "stack array cannot escape through this assignment",
                DiagnosticIds.StackArrayEscape);

        if (_requiredFields.TryGetValue(field, out var required)) _definitelyAssigned.Add(required);

        return initializer;
    }

    internal ImmutableArray<BoundStatement> CreateInstanceFieldInitializerStatements(StructTypeSymbol type)
    {
        var statements = ImmutableArray.CreateBuilder<BoundStatement>();
        var receiver = new BoundThisExpression(type, _fileScope.TypeFactory.PointerTo(type));
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
            _cancellationToken.ThrowIfCancellationRequested();
            statements.Add(BindStatement(statement));
        }

        RecordScope(syntax, _scope);

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
        var conditionFlow = BooleanFlow(condition);
        if (conditionFlow.True is { } whenTrue) RestoreExpressionFlow(whenTrue);
        BoundStatement thenStatement = BindEmbeddedStatement(syntax.ThenStatement);
        HashSet<LocalVariableSymbol> afterThen = CloneDefinitelyAssigned();
        var arraysAfterThen = CloneArrayState();

        RestoreDefinitelyAssigned(afterCondition);
        RestoreArrayState(arraysAfterCondition);
        if (conditionFlow.False is { } whenFalse) RestoreExpressionFlow(whenFalse);
        BoundStatement? elseStatement = syntax.ElseStatement is null
            ? null
            : BindEmbeddedStatement(syntax.ElseStatement);
        HashSet<LocalVariableSymbol> afterElse = CloneDefinitelyAssigned();
        var arraysAfterElse = CloneArrayState();

        if (conditionFlow.True is null || conditionFlow.False is null)
        {
            RestoreDefinitelyAssigned(conditionFlow.False is null ? afterThen : afterElse);
            RestoreArrayState(conditionFlow.False is null ? arraysAfterThen : arraysAfterElse);
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
        (int forEnd, bool includeForEnd) = GetStatementEnd(syntax.Body);
        RecordScope(
            syntax.ForKeyword.Location.Source,
            TextSpan.FromBounds(syntax.ForKeyword.Location.Span.Start,
                Math.Max(syntax.ForKeyword.Location.Span.Start, forEnd)),
            _scope,
            includeForEnd);
        _scope = previous;
        return new BoundForStatement(initializer, condition, increment, body);
    }

    private BoundSwitchStatement BindSwitchStatement(SwitchStatementSyntax syntax)
    {
        BoundExpression expression = BindExpression(syntax.Expression);
        if (!TypeFacts.IsInteger(expression.Type) && expression.Type is not EnumTypeSymbol && !TypeIdentity.AreSame(expression.Type, BuiltinTypes.Error))
            _diagnostics.Report(syntax.SwitchKeyword.Location, "switch operand must be an integer or enum",
                DiagnosticIds.InvalidSwitchOperand);
        var values = new HashSet<System.Numerics.BigInteger>();
        bool hasDefault = false;
        var sections = ImmutableArray.CreateBuilder<BoundSwitchSection>();
        var assignedBefore = new HashSet<LocalVariableSymbol>(_definitelyAssigned);
        var exits = new List<HashSet<LocalVariableSymbol>>();
        var arraysBefore = CloneArrayState();
        var arrayExits = new List<Dictionary<LocalVariableSymbol, ArrayState>>();
        _switchExits.Push((_loopDepth, exits, arrayExits));
        _switchDepth++;
        for (int sectionIndex = 0; sectionIndex < syntax.Sections.Length; sectionIndex++)
        {
            SwitchSectionSyntax section = syntax.Sections[sectionIndex];
            _definitelyAssigned.Clear();
            _definitelyAssigned.UnionWith(assignedBefore);
            RestoreArrayState(arraysBefore);
            BoundExpression? value = null;
            if (section.Value is null)
            {
                if (hasDefault) _diagnostics.Report(section.Label.Location, "duplicate default label",
                    DiagnosticIds.DuplicateSwitchLabel);
                hasDefault = true;
            }
            else
            {
                BoundExpression boundValue = BindExpression(section.Value);
                ConstantFoldStatus status = _constants.Fold(boundValue, out object? constant);
                if (status == ConstantFoldStatus.Invalid ||
                    !(TypeFacts.IsInteger(boundValue.Type) || boundValue.Type is EnumTypeSymbol))
                    _diagnostics.Report(section.Label.Location, "case value must be an integer or enum compile-time constant",
                        DiagnosticIds.SwitchCaseConstantRequired);
                else if (!TypeIdentity.AreSame(expression.Type, boundValue.Type) &&
                         !(expression.Type is PrimitiveTypeSymbol { IsInteger: true } integer && TypeFacts.IsInteger(boundValue.Type) &&
                           (status == ConstantFoldStatus.TargetDependent || SemanticAnalyzer.FitsInteger(SemanticAnalyzer.ToInteger(constant), integer, _constants.TargetLayout))))
                    _diagnostics.Report(section.Label.Location, "case value is not compatible with the switch operand type",
                        DiagnosticIds.SwitchCaseTypeMismatch);
                else if (status == ConstantFoldStatus.TargetDependent)
                    value = boundValue;
                else
                {
                    var number = SemanticAnalyzer.ToInteger(constant);
                    if (expression.Type is PrimitiveTypeSymbol { IsInteger: true } operandType && !SemanticAnalyzer.FitsInteger(number, operandType, _constants.TargetLayout))
                        _diagnostics.Report(section.Label.Location, "case value is not compatible with the switch operand type",
                            DiagnosticIds.SwitchCaseTypeMismatch);
                    if (!values.Add(number)) _diagnostics.Report(section.Label.Location, "duplicate case value",
                        DiagnosticIds.DuplicateSwitchLabel);
                    value = new BoundLiteralExpression(constant, expression.Type);
                }
            }
            BoundScope previous = _scope;
            _scope = new BoundScope(previous);
            var body = new BoundBlockStatement(section.Statements.Select(BindStatement).ToImmutableArray());
            int sectionEnd = sectionIndex + 1 < syntax.Sections.Length
                ? syntax.Sections[sectionIndex + 1].Label.Location.Span.Start
                : syntax.CloseBraceToken?.Location.Span.Start ??
                  (section.Statements.IsEmpty ? section.Label.Location.Span.End : GetStatementEnd(section.Statements[^1]).End);
            bool includeSectionEnd = sectionIndex == syntax.Sections.Length - 1 && syntax.CloseBraceToken?.IsMissing == true;
            RecordScope(
                section.Label.Location.Source,
                TextSpan.FromBounds(section.Label.Location.Span.Start,
                    Math.Max(section.Label.Location.Span.Start, sectionEnd)),
                _scope,
                includeSectionEnd);
            _scope = previous;
            if (!body.Statements.IsEmpty && !TerminatesCase(body))
                _diagnostics.Report(section.Label.Location, "implicit fallthrough is not allowed; terminate the case with break, return, or continue",
                    DiagnosticIds.SwitchFallthrough);
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
            _diagnostics.Report(syntax.Sections[^1].Label.Location, "final case requires an explicitly terminated body",
                DiagnosticIds.FinalSwitchCaseRequiresTermination);
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
            _diagnostics.Report(syntax.BreakKeyword.Location, "'break' can only be used inside a loop or switch",
                DiagnosticIds.BreakOutsideLoopOrSwitch);
        }

        return new BoundBreakStatement();
    }

    private BoundContinueStatement BindContinueStatement(ContinueStatementSyntax syntax)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Report(syntax.ContinueKeyword.Location, "'continue' can only be used inside a loop",
                DiagnosticIds.ContinueOutsideLoop);
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
        if (!TypeIdentity.AreSame(condition.Type, BuiltinTypes.Bool) && !TypeIdentity.AreSame(condition.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(GetLocation(syntax), $"condition must have type 'bool', but has type '{condition.Type.ToDisplayString()}'",
                DiagnosticIds.InvalidCondition);
        }

        return condition;
    }

    private BoundVariableDeclarationStatement BindVariableDeclaration(VariableDeclarationStatementSyntax syntax)
    {
        bool isConstant = syntax.Type.GetQualifier(SyntaxKind.ConstKeyword) is not null && !syntax.Type.Contains<PointerTypeSyntax>() && !syntax.Type.Contains<ReferenceTypeSyntax>();
        TypeSymbol type = TypeResolver.Resolve(isConstant ? syntax.Type.WithoutQualifier(SyntaxKind.ConstKeyword) : syntax.Type, _fileScope, _diagnostics);
        if (type is StructTypeSymbol { IsAbstract: true } abstractType)
            _diagnostics.Report(syntax.Type.NameToken.Location, $"abstract struct '{abstractType.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);
        if (TypeIdentity.AreSame(type, BuiltinTypes.Void))
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, "local variable type cannot be 'void'",
                DiagnosticIds.InvalidLocalType);
        }

        var variable = new LocalVariableSymbol(syntax.IdentifierToken.Text, type, _function, isConstant || syntax.Type.IsBindingReadonly(), syntax);
        _semanticInfo.Declarations[syntax] = variable;
        if (type is StructTypeSymbol structure && structure.FindDestructor() is { } destructor)
        {
            variable.Destructor = destructor;
            _function.HasScalarCleanup = true;
            if (syntax.Initializer is not null) ValidateDestructorAccess(destructor, syntax.IdentifierToken.Location);
        }
        _localScopes.Add(variable, _scope);
        bool declared = _scope.TryDeclare(variable);
        if (!declared)
        {
            VariableSymbol? previousVariable = _scope.LookupCurrent(variable.Name);
            _diagnostics.Report(
                syntax.IdentifierToken.Location,
                $"variable '{variable.Name}' is already declared in this scope",
                DiagnosticIds.DuplicateDeclaration,
                previousVariable?.Locations.Select(location => new RelatedDiagnosticLocation(location, "previous declaration")));
        }

        BoundExpression? initializer = syntax.Initializer is null ? null : BindExpression(syntax.Initializer);
        if (type is ReferenceTypeSymbol && initializer is null)
        {
            _diagnostics.Report(syntax.IdentifierToken.Location, "reference variables must be initialized",
                DiagnosticIds.ReferenceRequiresInitializer);
        }
        else if (syntax.Type.IsBindingReadonly() && initializer is null)
        {
            _diagnostics.Report(syntax.IdentifierToken.Location, "readonly local variables must be initialized",
                DiagnosticIds.ReadonlyRequiresInitializer);
        }
        if (initializer is not null)
        {
            initializer = ContextualizeConversion(initializer, type);
            SetConvertedType(syntax.Initializer!, initializer.Type);
        }

        if (isConstant)
        {
            if (initializer is not null && TypeFacts.IsNumeric(type) && TypeFacts.IsNumeric(initializer.Type) && !TypeIdentity.AreSame(type, initializer.Type))
                initializer = new BoundCastExpression(initializer, type);
            object? constantValue = null;
            ConstantFoldStatus status = initializer is null ? ConstantFoldStatus.Invalid : _constants.Fold(initializer, out constantValue);
            if (status == ConstantFoldStatus.Invalid)
                _diagnostics.Report(syntax.IdentifierToken.Location, "const local requires a compile-time constant initializer",
                    DiagnosticIds.ConstantValueRequired);
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
            SetConvertedType(syntax.Expression!, expression.Type);
        }

        if (TypeIdentity.AreSame(_function.ReturnType, BuiltinTypes.Void))
        {
            if (expression is not null)
            {
                _diagnostics.Report(GetLocation(syntax.Expression!), "a void function cannot return a value",
                    DiagnosticIds.ReturnValueFromVoid);
            }
        }
        else if (expression is null)
        {
            _diagnostics.Report(syntax.ReturnKeyword.Location, $"function '{_function.Name}' must return a value of type '{_function.ReturnType.ToDisplayString()}'",
                DiagnosticIds.MissingReturnValue);
        }
        else if (!TypeFacts.CanAssign(_function.ReturnType, expression.Type))
        {
            ReportCannotConvert(GetLocation(syntax.Expression!), expression.Type, _function.ReturnType);
        }

        if (expression is not null && GetArrayStorage(expression) == ArrayStorageKind.Stack)
        {
            _diagnostics.Report(GetLocation(syntax.Expression!), "stack array cannot be returned from a function",
                DiagnosticIds.StackArrayReturn);
        }

        ValidateRequiredFields(syntax.ReturnKeyword.Location);
        return new BoundReturnStatement(expression);
    }

    private BoundExpression BindExpression(ExpressionSyntax syntax)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        BoundExpression expression = syntax switch
        {
            MissingExpressionSyntax => new BoundErrorExpression(),
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
        _semanticInfo.Types[syntax] = new TypeInfo(expression.Type, expression.Type);
        if (GetReferencedSymbol(expression) is { } referenced)
        {
            if (!_semanticInfo.Symbols.ContainsKey(syntax))
                SetSelectedSymbolPreservingCandidates(syntax, referenced);
            if (syntax is CallExpressionSyntax call)
            {
                if (!_semanticInfo.Symbols.ContainsKey(call.Target))
                    SetSelectedSymbolPreservingCandidates(call.Target, referenced);
                if (_semanticInfo.Symbols.TryGetValue(call.Target, out SymbolInfo targetInfo))
                    _semanticInfo.Symbols[syntax] = targetInfo;
            }
        }
        else if (syntax is CallExpressionSyntax failedCall &&
                 _semanticInfo.Symbols.TryGetValue(failedCall.Target, out SymbolInfo failedTargetInfo))
            _semanticInfo.Symbols[syntax] = failedTargetInfo;
        else if (syntax is MissingExpressionSyntax || expression is BoundErrorExpression)
            _semanticInfo.Symbols.TryAdd(syntax, new SymbolInfo(null, [],
                syntax is MissingExpressionSyntax ? CandidateReason.Incomplete : CandidateReason.NotFound));
        _expressionLocations[expression] = GetLocation(syntax);
        if (expression is BoundThisExpression && !ReferenceEquals(syntax, _fieldReceiverSyntax))
            ValidateRequiredFields(GetLocation(syntax));
        if (!ReferenceEquals(syntax, _initializationTarget) && expression is BoundMemberAccessExpression { Receiver: BoundThisExpression } fieldRead &&
            _requiredFields.TryGetValue(fieldRead.Field, out var required) && !_definitelyAssigned.Contains(required))
            _diagnostics.Report(GetLocation(syntax), $"field '{fieldRead.Field.Name}' is used before it is initialized",
                DiagnosticIds.DefiniteAssignment);
        BoundExpression result = DereferenceReference(expression);
        _expressionLocations[result] = GetLocation(syntax);
        _semanticInfo.Receivers[syntax] = new ReceiverInfo(
            result.Type,
            IsStatic: false,
            IsReadonly: result.Type is PointerTypeSymbol { IsReadonly: true } or ReferenceTypeSymbol { IsReadonly: true } ||
                IsAddressable(result) && !IsWritable(result),
            IsWritable: IsWritable(result));
        return result;
    }

    private static BoundExpression DereferenceReference(BoundExpression expression) =>
        expression.Type is ReferenceTypeSymbol referenceType
            ? new BoundReferenceDereferenceExpression(expression, referenceType)
            : expression;

    private BoundExpression BindThisExpression(ThisExpressionSyntax syntax)
    {
        if (_function.ContainingType is not { } containingType || _function.IsStatic)
        {
            _diagnostics.Report(syntax.ThisKeyword.Location, "'this' is available only in instance members",
                DiagnosticIds.ThisOutsideInstanceMember);
            return new BoundErrorExpression();
        }
        if (_bindingBaseConstructorArguments)
        {
            _diagnostics.Report(syntax.ThisKeyword.Location, "the derived object cannot be used in base constructor arguments",
                DiagnosticIds.DerivedInstanceInBaseConstructorArguments);
            return new BoundErrorExpression();
        }
        return new BoundThisExpression(containingType, _fileScope.TypeFactory.PointerTo(containingType, isReadonly: _function.IsReadonly));
    }

    private BoundExpression BindLiteralExpression(LiteralExpressionSyntax syntax)
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
                new BoundLiteralExpression(token.Value, _fileScope.TypeFactory.PointerTo(BuiltinTypes.Byte, isReadonly: true)),
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
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(variable);
            if (variable is LocalVariableSymbol { ConstantValue: not null } localConstant) return localConstant.ConstantValue;
            if (requireDefinitelyAssigned &&
                variable is LocalVariableSymbol local &&
                !_definitelyAssigned.Contains(local))
            {
                _diagnostics.Report(
                    syntax.IdentifierToken.Location,
                    $"local variable '{local.Name}' is used before it is initialized",
                    DiagnosticIds.DefiniteAssignment);
            }

            return new BoundVariableExpression(variable);
        }

        if (_function.ContainingType is { } containingType)
        {
            ConstantSymbol? associatedConstant = containingType.FindMember<ConstantSymbol>(syntax.IdentifierToken.Text);
            if (associatedConstant?.HasValue == true)
            {
                _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(associatedConstant);
                return associatedConstant.BoundValue!;
            }
            if (associatedConstant is not null)
                return new BoundErrorExpression();

            FieldSymbol? field = containingType.FindInstanceField(syntax.IdentifierToken.Text);
            if (field is not null)
            {
                _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(field);
                if (_bindingBaseConstructorArguments)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, "the derived object cannot be used in base constructor arguments",
                        DiagnosticIds.DerivedInstanceInBaseConstructorArguments);
                    return new BoundErrorExpression();
                }
                if (!field.IsPublic && !TypeIdentity.AreSame(containingType, field.ContainingType))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"field '{field.Name}' is private in struct '{field.ContainingType.Name}'",
                        DiagnosticIds.InaccessibleSymbol);
                    return new BoundErrorExpression();
                }
                if (_function.IsStatic)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"static method '{_function.Name}' cannot access instance field '{field.Name}' without an explicit instance",
                        DiagnosticIds.StaticContextInstanceFieldAccess);
                    return new BoundErrorExpression();
                }
                PointerTypeSymbol thisType = _fileScope.TypeFactory.PointerTo(containingType, isReadonly: _function.IsReadonly);
                return new BoundMemberAccessExpression(
                    new BoundThisExpression(containingType, thisType),
                    field,
                    IsPointerAccess: true);
            }


            PropertySymbol? property = containingType.FindMember<PropertySymbol>(syntax.IdentifierToken.Text);
            if (property is not null)
            {
                _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(property);
                if (_bindingBaseConstructorArguments)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, "the derived object cannot be used in base constructor arguments",
                        DiagnosticIds.DerivedInstanceInBaseConstructorArguments);
                    return new BoundErrorExpression();
                }
                if (_function.IsStatic)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"static method '{_function.Name}' cannot access instance property '{property.Name}' without an explicit instance",
                        DiagnosticIds.StaticContextInstancePropertyAccess);
                    return new BoundErrorExpression();
                }

                PointerTypeSymbol thisType = _fileScope.TypeFactory.PointerTo(containingType, isReadonly: _function.IsReadonly);
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
        {
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(constant);
            return constant.BoundValue!;
        }
        if (constant is not null)
            return new BoundErrorExpression();

        _diagnostics.Report(syntax.IdentifierToken.Location, $"unknown identifier '{syntax.IdentifierToken.Text}'",
            DiagnosticIds.UnknownIdentifier);
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
            SyntaxKind.BangToken when TypeIdentity.AreSame(operand.Type, BuiltinTypes.Bool) => BuiltinTypes.Bool,
            SyntaxKind.TildeToken when TypeFacts.IsInteger(operand.Type) => operand.Type,
            SyntaxKind.StarToken when operand.Type is PointerTypeSymbol pointer => pointer.ElementType,
            SyntaxKind.AmpersandToken when IsAddressable(operand) => _fileScope.TypeFactory.PointerTo(operand.Type, isReadonly: !IsWritable(operand)),
            SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken
                when IsWritable(operand) && (TypeFacts.IsNumeric(operand.Type) ||
                    operand.Type is PointerTypeSymbol pointer && !TypeIdentity.AreSame(pointer.ElementType, BuiltinTypes.Void)) => operand.Type,
            _ => null,
        };

        if (resultType is null)
        {
            if (!TypeIdentity.AreSame(operand.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(
                    operatorToken.Location,
                    $"unary operator '{operatorToken.Text}' is not defined for type '{operand.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidOperatorOperands);
            }

            return new BoundErrorExpression();
        }

        return new BoundUnaryExpression(operatorToken.Kind, operand, resultType, isPostfix);
    }

    private BoundExpression BindBinaryExpression(BinaryExpressionSyntax syntax)
    {
        BoundExpression left = BindExpression(syntax.Left);
        bool shortCircuit = syntax.OperatorToken.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken;
        var leftFlow = shortCircuit ? BooleanFlow(left) : default;
        bool isAnd = syntax.OperatorToken.Kind == SyntaxKind.AmpersandAmpersandToken;
        if (shortCircuit && (isAnd ? leftFlow.True : leftFlow.False) is { } rhsEntry)
            RestoreExpressionFlow(rhsEntry);
        bool previousSuppression = _suppressIntegerOperationDiagnostics;
        if (syntax.OperatorToken.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken &&
            _constants.TryFold(left, out object? value) && value is bool condition &&
            condition == (syntax.OperatorToken.Kind == SyntaxKind.PipePipeToken))
            _suppressIntegerOperationDiagnostics = true;
        BoundExpression right;
        try { right = BindExpression(syntax.Right); }
        finally { _suppressIntegerOperationDiagnostics = previousSuppression; }

        var rightFlow = shortCircuit ? BooleanFlow(right) : default;
        (ExpressionFlow? True, ExpressionFlow? False) resultFlow = default;
        if (shortCircuit)
        {
            resultFlow = isAnd
                ? (leftFlow.True is null ? null : rightFlow.True,
                    MergeExpressionFlow(leftFlow.False, leftFlow.True is null ? null : rightFlow.False))
                : (MergeExpressionFlow(leftFlow.True, leftFlow.False is null ? null : rightFlow.True),
                    leftFlow.False is null ? null : rightFlow.False);
            RestoreExpressionFlow(MergeExpressionFlow(resultFlow.True, resultFlow.False)!);
        }

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
            if (!TypeIdentity.AreSame(left.Type, BuiltinTypes.Error) && !TypeIdentity.AreSame(right.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"binary operator '{syntax.OperatorToken.Text}' is not defined for types '{left.Type.ToDisplayString()}' and '{right.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidOperatorOperands);
            }

            return new BoundErrorExpression();
        }

        ValidateIntegerOperation(left, syntax.OperatorToken.Kind, right, syntax.OperatorToken.Location);
        var result = new BoundBinaryExpression(left, syntax.OperatorToken.Kind, right, resultType);
        if (shortCircuit) _booleanFlows.Add(result, resultFlow);
        return result;
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
                _diagnostics.Report(location, "invalid integer shift: count must be nonnegative and less than the operand bit width",
                    DiagnosticIds.InvalidShift);
        }
        else if (operation is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
        {
            if (count == 0)
                _diagnostics.Report(location, "invalid integer division or remainder by zero",
                    DiagnosticIds.DivisionByZero);
            else if (integer.IsSigned && width is int bits && count == -1 &&
                     _constants.TryFold(left, out object? leftValue) &&
                     SemanticAnalyzer.ToInteger(leftValue) == -(BigInteger.One << (bits - 1)))
                _diagnostics.Report(location, "invalid integer division or remainder: signed minimum with -1",
                    DiagnosticIds.SignedDivisionOverflow);
        }
    }

    private BoundExpression BindAssignmentExpression(AssignmentExpressionSyntax syntax)
    {
        bool isSimpleAssignment = syntax.OperatorToken.Kind == SyntaxKind.EqualsToken;
        ExpressionFlow beforeTarget = CaptureExpressionFlow();
        BoundExpression? indexerAssignment = TryBindIndexerAssignment(syntax, isSimpleAssignment);
        if (indexerAssignment is not null)
            return indexerAssignment;
        RestoreExpressionFlow(beforeTarget);
        BoundExpression? propertyAssignment = TryBindPropertyAssignment(syntax, isSimpleAssignment);
        if (propertyAssignment is not null)
            return propertyAssignment;
        RestoreExpressionFlow(beforeTarget);

        ExpressionSyntax? previousTarget = _initializationTarget;
        _initializationTarget = isSimpleAssignment ? syntax.Target : null;
        BoundExpression target;
        try
        {
            target = isSimpleAssignment && syntax.Target is NameExpressionSyntax name
                ? BindNameExpression(name, requireDefinitelyAssigned: false)
                : BindExpression(syntax.Target);
        }
        finally { _initializationTarget = previousTarget; }
        BoundExpression rawTarget = target is BoundReferenceDereferenceExpression reference ? reference.Reference : target;
        bool initializesField = isSimpleAssignment && rawTarget is BoundMemberAccessExpression { Receiver: BoundThisExpression } fieldTarget &&
            _function.FunctionKind == FunctionKind.Constructor && TypeIdentity.AreSame(fieldTarget.Field.ContainingType, _function.ContainingType);
        target = initializesField ? rawTarget : DereferenceReference(target);
        BoundExpression expression = BindExpression(syntax.Expression);
        if (isSimpleAssignment)
        {
            expression = ContextualizeConversion(expression, target.Type);
            SetConvertedType(syntax.Expression, expression.Type);
        }

        if (!IsWritable(target))
        {
            if (!TypeIdentity.AreSame(target.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(GetLocation(syntax.Target), "left side of assignment must be writable",
                    DiagnosticIds.InvalidAssignmentTarget);
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
            if (!TypeIdentity.AreSame(resultType, target.Type))
            {
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"operator '{syntax.OperatorToken.Text}' is not defined for types '{target.Type.ToDisplayString()}' and '{expression.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidOperatorOperands);
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
                _diagnostics.Report(GetLocation(syntax.Expression), "stack array cannot escape through this assignment",
                    DiagnosticIds.StackArrayEscape);
            }
        }

        if (isSimpleAssignment && target is BoundVariableExpression { Variable: LocalVariableSymbol assignedLocal })
        {
            ValidateDestructorAccess(assignedLocal.Destructor, syntax.OperatorToken.Location);
            _definitelyAssigned.Add(assignedLocal);
        }
        if (initializesField && target is BoundMemberAccessExpression assignedField && _requiredFields.TryGetValue(assignedField.Field, out var requiredField))
            _definitelyAssigned.Add(requiredField);

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
        if (syntax.MemberToken.IsMissing)
        {
            bool recordedStaticReceiver = false;
            if (syntax.OperatorToken.Kind == SyntaxKind.DotToken &&
                TryGetDottedName(syntax.Receiver, out ImmutableArray<SyntaxToken> receiverParts) &&
                receiverParts.Length > 0 &&
                _scope.Lookup(receiverParts[0].Text) is null &&
                _function.ContainingType?.FindInstanceField(receiverParts[0].Text) is null)
            {
                string[] receiverName = receiverParts.Select(part => part.Text).ToArray();
                TypeSymbol? receiverType = _fileScope.ResolveQualifiedType(receiverName);
                if (receiverType is null && receiverParts.Length == 1)
                    receiverType = _fileScope.ResolveType(receiverParts[0].Text, receiverParts[0].Location,
                        new DiagnosticBag());
                if (receiverType is not null)
                {
                    RecordStaticReceiver(syntax.Receiver, receiverType);
                    recordedStaticReceiver = true;
                }
            }
            if (!recordedStaticReceiver)
                _ = BindFieldReceiver(syntax.Receiver);
            _semanticInfo.Symbols[syntax] = new SymbolInfo(null, [], CandidateReason.Incomplete);
            return new BoundErrorExpression();
        }
        if (syntax.OperatorToken.Kind == SyntaxKind.DotToken && TryGetDottedName(syntax, out ImmutableArray<SyntaxToken> qualifiedName) && qualifiedName.Length >= 2)
        {
            string[] typeParts = qualifiedName.Take(qualifiedName.Length - 1).Select(token => token.Text).ToArray();
            TypeSymbol? resolved = typeParts.Length == 1
                ? _fileScope.ResolveType(typeParts[0], syntax.Receiver is NameExpressionSyntax name ? name.IdentifierToken.Location : syntax.OperatorToken.Location, _diagnostics)
                : _fileScope.ResolveQualifiedType(typeParts);
            if (resolved is EnumTypeSymbol enumeration)
            {
                ConstantSymbol? member = enumeration.FindMember(qualifiedName[^1].Text);
                if (member?.BoundValue is BoundExpression value)
                {
                    RecordStaticReceiver(syntax.Receiver, enumeration);
                    _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(member);
                    return value;
                }
                _diagnostics.Report(syntax.MemberToken.Location, $"enum '{enumeration.Name}' has no valid member '{syntax.MemberToken.Text}'",
                    DiagnosticIds.UnknownEnumMember);
                return new BoundErrorExpression();
            }
            if (resolved is DeclaredTypeSymbol staticType)
            {
                if (staticType is StructTypeSymbol definition &&
                    _function.ContainingStruct is StructTypeSymbol { GenericDefinition: not null } specialization &&
                    ReferenceEquals(specialization.GenericDefinition, definition))
                    staticType = specialization;
                ConstantSymbol? constant = staticType.FindMember<ConstantSymbol>(qualifiedName[^1].Text);
                if (constant?.HasValue == true)
                {
                    RecordStaticReceiver(syntax.Receiver, staticType);
                    RecordSymbolAndType(syntax, constant, constant.Type);
                    return constant.BoundValue!;
                }
                if (constant is not null)
                    return new BoundErrorExpression();

                FieldSymbol? staticField = staticType.FindStaticField(qualifiedName[^1].Text);
                if (staticField is not null)
                {
                    if (!staticField.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, staticField.ContainingType))
                    {
                        _diagnostics.Report(qualifiedName[^1].Location, $"static field '{staticField.Name}' is private in struct '{staticField.ContainingType.Name}'",
                            DiagnosticIds.InaccessibleSymbol);
                        return new BoundErrorExpression();
                    }
                    RecordStaticReceiver(syntax.Receiver, staticType);
                    return new BoundStaticFieldExpression(staticField);
                }
            }
        }

        BoundExpression receiver = BindFieldReceiver(syntax.Receiver);
        if (receiver.Type is ArrayTypeSymbol array && syntax.OperatorToken.Kind == SyntaxKind.DotToken && syntax.MemberToken.Text is "Length" or "Rank")
        {
            SyntheticMemberSymbol member = _semanticInfo.GetArrayMembers(array)
                .Single(candidate => candidate.Name == syntax.MemberToken.Text);
            RecordSymbolAndType(syntax, member, member.Type);
            return new BoundArrayMetadataExpression(receiver, syntax.MemberToken.Text);
        }
        bool pointerAccess = syntax.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
        if (GetGenericReceiver(receiver.Type, pointerAccess) is GenericParameterSymbol genericParameter)
            return BindGenericMemberGet(syntax, receiver, genericParameter, pointerAccess);
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
                    $"interface '{interfaceType.Name}' does not contain property '{syntax.MemberToken.Text}'",
                    DiagnosticIds.MissingInterfaceProperty);
                return new BoundErrorExpression();
            }
            RecordSymbolAndType(syntax, interfaceProperty, interfaceProperty.Type);
            if (interfaceProperty.Getter is not FunctionSymbol getter)
            {
                _diagnostics.Report(syntax.MemberToken.Location, $"property '{interfaceProperty.Name}' does not declare a getter",
                    DiagnosticIds.MissingAccessor);
                return new BoundErrorExpression();
            }
            if (IsReadonlyReceiver(receiver, pointerAccess) && !getter.IsReadonly)
            {
                _diagnostics.Report(syntax.MemberToken.Location, $"property '{interfaceProperty.Name}' cannot be read through a readonly interface receiver because its getter is mutable",
                    DiagnosticIds.MutableGetterOnReadonlyReceiver);
                return new BoundErrorExpression();
            }
            return new BoundInterfaceMethodCallExpression(receiver, interfaceType, getter, [], pointerAccess);
        }

        DeclaredTypeSymbol? structType = pointerAccess
            ? (receiver.Type as PointerTypeSymbol)?.ElementType as DeclaredTypeSymbol
            : receiver.Type as DeclaredTypeSymbol;

        if (structType is null)
        {
            if (!TypeIdentity.AreSame(receiver.Type, BuiltinTypes.Error))
            {
                string expected = pointerAccess ? "pointer to struct" : "struct";
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"operator '{syntax.OperatorToken.Text}' requires a {expected}, but has type '{receiver.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidMemberReceiver);
            }

            return new BoundErrorExpression();
        }

        FieldSymbol? field = structType.FindInstanceField(syntax.MemberToken.Text);
        PropertySymbol? property = structType.FindMember<PropertySymbol>(syntax.MemberToken.Text);
        if (property is not null)
        {
            RecordSymbolAndType(syntax, property, property.Type);
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
                $"struct '{structType.Name}' does not contain field '{syntax.MemberToken.Text}'",
                DiagnosticIds.MissingStructField);
            return new BoundErrorExpression();
        }

        if (!field.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, field.ContainingType))
        {
            _diagnostics.Report(
                syntax.MemberToken.Location,
                $"field '{field.Name}' is private in struct '{field.ContainingType.Name}'",
                DiagnosticIds.InaccessibleSymbol);
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

            receiver = BindFieldReceiver(member.Receiver);
            pointerAccess = member.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
            if (GetGenericReceiver(receiver.Type, pointerAccess) is GenericParameterSymbol genericParameter)
                return TryBindGenericPropertyAssignment(syntax, member, receiver, genericParameter,
                    pointerAccess, isSimpleAssignment);
            InterfaceTypeSymbol? interfaceType = pointerAccess
                ? (receiver.Type as PointerTypeSymbol)?.ElementType as InterfaceTypeSymbol
                : receiver.Type as InterfaceTypeSymbol;
            if (interfaceType is not null)
            {
                InterfacePropertySymbol? interfaceProperty = interfaceType.FindProperty(member.MemberToken.Text);
                if (interfaceProperty is null)
                    return null;
                RecordSymbolAndType(syntax.Target, interfaceProperty, interfaceProperty.Type);
                _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(interfaceProperty);
                location = member.MemberToken.Location;
                if (interfaceProperty.Setter is not FunctionSymbol interfaceSetter)
                {
                    _diagnostics.Report(location, $"property '{interfaceProperty.Name}' does not declare a setter",
                        DiagnosticIds.MissingAccessor);
                    return new BoundErrorExpression();
                }
                bool interfaceReceiverIsReadonly =
                    (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
                    (!pointerAccess && (!IsAddressable(receiver) || !IsWritable(receiver)));
                if (interfaceReceiverIsReadonly)
                {
                    _diagnostics.Report(location, $"property '{interfaceProperty.Name}' cannot be assigned through a readonly receiver",
                        DiagnosticIds.WriteThroughReadonlyReceiver);
                    return new BoundErrorExpression();
                }

                if (!isSimpleAssignment)
                {
                    if (interfaceProperty.Getter is not FunctionSymbol interfaceGetter)
                    {
                        _diagnostics.Report(location, $"property '{interfaceProperty.Name}' does not declare a getter",
                            DiagnosticIds.MissingAccessor);
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

            DeclaredTypeSymbol? structType = pointerAccess
                ? (receiver.Type as PointerTypeSymbol)?.ElementType as DeclaredTypeSymbol
                : receiver.Type as DeclaredTypeSymbol;
            if (structType is null || (property = structType.FindMember<PropertySymbol>(member.MemberToken.Text)) is null)
                return null;
            RecordSymbolAndType(syntax.Target, property, property.Type);
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(property);
            location = member.MemberToken.Location;
        }
        else if (syntax.Target is NameExpressionSyntax name && _function.ContainingType is { } containingType)
        {
            if (_scope.Lookup(name.IdentifierToken.Text) is not null)
                return null;
            property = containingType.FindMember<PropertySymbol>(name.IdentifierToken.Text);
            if (property is null)
                return null;
            RecordSymbolAndType(syntax.Target, property, property.Type);
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(property);
            pointerAccess = true;
            receiver = new BoundThisExpression(
                containingType,
                _fileScope.TypeFactory.PointerTo(containingType, isReadonly: _function.IsReadonly));
            location = name.IdentifierToken.Location;
        }
        else
        {
            return null;
        }

        if (_bindingBaseConstructorArguments)
        {
            _diagnostics.Report(location, "the derived object cannot be used in base constructor arguments",
                DiagnosticIds.DerivedInstanceInBaseConstructorArguments);
            return new BoundErrorExpression();
        }
        if (_function.IsStatic && syntax.Target is NameExpressionSyntax)
        {
            _diagnostics.Report(location, $"static method '{_function.Name}' cannot access instance property '{property.Name}' without an explicit instance",
                DiagnosticIds.StaticContextInstancePropertyAccess);
            return new BoundErrorExpression();
        }
        if (receiver is BoundThisExpression) ValidateRequiredFields(location);
        if (!property.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, property.ContainingType))
            _diagnostics.Report(location, $"property '{property.Name}' is private in struct '{property.ContainingType.Name}'",
                DiagnosticIds.InaccessibleSymbol);
        if (property.Setter is not FunctionSymbol setter)
        {
            _diagnostics.Report(location, $"property '{property.Name}' does not declare a setter",
                DiagnosticIds.MissingAccessor);
            return new BoundErrorExpression();
        }

        bool receiverIsReadonly =
            (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
            (!pointerAccess && (!IsAddressable(receiver) || !IsWritable(receiver)));
        if (receiverIsReadonly)
        {
            _diagnostics.Report(location, $"property '{property.Name}' cannot be assigned through a readonly receiver",
                DiagnosticIds.WriteThroughReadonlyReceiver);
            return new BoundErrorExpression();
        }

        if (!isSimpleAssignment)
        {
            if (property.Getter is not FunctionSymbol getter)
            {
                _diagnostics.Report(location, $"property '{property.Name}' does not declare a getter",
                    DiagnosticIds.MissingAccessor);
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
        if (receiver is BoundThisExpression) ValidateRequiredFields(location);
        if (!property.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, property.ContainingType))
            _diagnostics.Report(location, $"property '{property.Name}' is private in struct '{property.ContainingType.Name}'",
                DiagnosticIds.InaccessibleSymbol);
        if (property.Getter is not FunctionSymbol getter)
        {
            _diagnostics.Report(location, $"property '{property.Name}' does not declare a getter",
                DiagnosticIds.MissingAccessor);
            return new BoundErrorExpression();
        }
        if (receiverIsReadonly && !getter.IsReadonly)
        {
            _diagnostics.Report(location, $"property '{property.Name}' cannot be read through a readonly receiver because its getter is mutable",
                DiagnosticIds.MutableGetterOnReadonlyReceiver);
            return new BoundErrorExpression();
        }

        return new BoundMethodCallExpression(receiver, getter, [], isPointerAccess);
    }

    private BoundExpression BindTypeLayoutExpression(TypeLayoutExpressionSyntax syntax)
    {
        TypeSymbol type = TypeResolver.Resolve(syntax.Type, _fileScope, _diagnostics);
        if (TypeIdentity.AreSame(type, BuiltinTypes.Void) || TypeIdentity.AreSame(type, BuiltinTypes.Error))
        {
            if (TypeIdentity.AreSame(type, BuiltinTypes.Void)) _diagnostics.Report(syntax.Keyword.Location, "layout intrinsic requires a non-void type",
                DiagnosticIds.LayoutRequiresNonVoidType);
            return new BoundErrorExpression();
        }
        if (syntax.Keyword.Kind != SyntaxKind.OffsetOfKeyword)
            return new BoundTypeLayoutExpression(syntax.Keyword.Kind, type, null);

        if (type is not StructTypeSymbol structType)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, "offsetof requires a struct type",
                DiagnosticIds.OffsetOfRequiresStructType);
            return new BoundErrorExpression();
        }
        FieldSymbol? field = structType.FindField(syntax.FieldToken!.Text);
        if (field is null)
        {
            _diagnostics.Report(syntax.FieldToken.Location, $"struct '{structType.Name}' does not contain field '{syntax.FieldToken.Text}'",
                DiagnosticIds.OffsetOfUnknownField);
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
            if (!TypeIdentity.AreSame(targetType, BuiltinTypes.Error) && !TypeIdentity.AreSame(expression.Type, BuiltinTypes.Error))
                _diagnostics.Report(syntax.CastKeyword.Location, $"cast from '{expression.Type.ToDisplayString()}' to '{targetType.Name}' is not a valid primitive cast",
                    DiagnosticIds.InvalidCast);
            return new BoundErrorExpression();
        }
        return new BoundCastExpression(expression, targetType);
    }

    private BoundExpression BindIndexExpression(IndexExpressionSyntax syntax)
    {
        if (TryResolveTypeExpression(syntax.Receiver, out TypeSymbol? arrayElementType, out TextLocation typeLocation) &&
            arrayElementType is not null &&
            !TypeIdentity.AreSame(arrayElementType, BuiltinTypes.Error))
        {
            return BindArrayCreation(arrayElementType, syntax.Arguments, typeLocation, syntax.OpenBracketToken.Location, ArrayStorageKind.Stack);
        }

        BoundExpression receiver = BindExpression(syntax.Receiver);
        ImmutableArray<BoundExpression> arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();

        if (GetGenericReceiver(receiver.Type, pointerAccess: false) is GenericParameterSymbol genericParameter)
            return BindGenericIndexerGet(syntax, receiver, genericParameter, arguments);

        if (receiver.Type is DeclaredTypeSymbol structType && structType is not InterfaceTypeSymbol)
        {
            bool receiverIsReadonly = IsAddressable(receiver) && !IsWritable(receiver);
            IndexerSymbol? indexer = ResolveIndexer(
                structType.LookupMembers("this").OfType<IndexerSymbol>().DistinctBy(indexer => TypeSignature.Parameters(indexer.Parameters)).Where(candidate =>
                    candidate.Getter is not null &&
                    (!receiverIsReadonly || candidate.Getter.IsReadonly)),
                arguments,
                syntax.OpenBracketToken.Location,
                structType.Name,
                syntax);
            if (indexer is null)
                return new BoundErrorExpression();
            RecordSymbolAndType(syntax, indexer, indexer.Type);
            if (!indexer.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, indexer.ContainingType))
            {
                RecordCandidates(syntax, null, [indexer], CandidateReason.Inaccessible);
                _diagnostics.Report(syntax.OpenBracketToken.Location, $"indexer is private in struct '{indexer.ContainingType.Name}'",
                    DiagnosticIds.InaccessibleSymbol);
            }
            if (indexer.Getter is not FunctionSymbol getter)
            {
                _diagnostics.Report(syntax.OpenBracketToken.Location, "indexer does not declare a getter",
                    DiagnosticIds.MissingAccessor);
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
                interfaceType.Name,
                syntax);
            if (indexer is null)
                return new BoundErrorExpression();
            RecordSymbolAndType(syntax, indexer, indexer.Type);
            FunctionSymbol getter = indexer.Getter!;
            arguments = ValidateFunctionArguments(getter, arguments, syntax.Arguments, syntax.OpenBracketToken.Location);
            return new BoundInterfaceMethodCallExpression(receiver, interfaceType, getter, arguments, IsPointerAccess: false);
        }

        int requiredRank = receiver.Type is ArrayTypeSymbol rankedArray ? rankedArray.Rank : 1;
        if (arguments.Length != requiredRank)
        {
            _diagnostics.Report(syntax.OpenBracketToken.Location, $"array or pointer indexing requires {requiredRank} index value(s)",
                DiagnosticIds.IndexArityMismatch);
            return new BoundErrorExpression();
        }
        BoundExpression index = arguments[0];

        foreach (BoundExpression argument in arguments)
        {
            if (!TypeFacts.IsInteger(argument.Type) && !TypeIdentity.AreSame(argument.Type, BuiltinTypes.Error))
                _diagnostics.Report(GetLocation(syntax.Index), $"array index must be an integer, but has type '{argument.Type.ToDisplayString()}'",
                    DiagnosticIds.IndexMustBeInteger);
        }

        TypeSymbol? elementType = receiver.Type switch
        {
            ArrayTypeSymbol array => array.ElementType,
            PointerTypeSymbol pointer => pointer.ElementType,
            _ => null,
        };

        if (elementType is null)
        {
            if (!TypeIdentity.AreSame(receiver.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.OpenBracketToken.Location, $"type '{receiver.Type.ToDisplayString()}' cannot be indexed",
                    DiagnosticIds.TypeNotIndexable);
            }

            return new BoundErrorExpression();
        }

        if (TypeIdentity.AreSame(elementType, BuiltinTypes.Void))
        {
            _diagnostics.Report(syntax.OpenBracketToken.Location, "cannot index a void pointer",
                DiagnosticIds.VoidPointerIndex);
            return new BoundErrorExpression();
        }
        return new BoundIndexExpression(receiver, index, elementType) { Indices = arguments };
    }

    private BoundExpression? TryBindIndexerAssignment(AssignmentExpressionSyntax syntax, bool isSimpleAssignment)
    {
        if (syntax.Target is not IndexExpressionSyntax target)
            return null;

        BoundExpression receiver = BindExpression(target.Receiver);
        if (GetGenericReceiver(receiver.Type, pointerAccess: false) is GenericParameterSymbol genericParameter)
            return BindGenericIndexerAssignment(syntax, target, receiver, genericParameter, isSimpleAssignment);
        if (receiver.Type is not DeclaredTypeSymbol)
            return null;
        ImmutableArray<BoundExpression> indices = target.Arguments.Select(BindExpression).ToImmutableArray();
        if (receiver.Type is DeclaredTypeSymbol structType && structType is not InterfaceTypeSymbol)
        {
            IndexerSymbol? indexer = ResolveIndexer(
                structType.LookupMembers("this").OfType<IndexerSymbol>().DistinctBy(indexer => TypeSignature.Parameters(indexer.Parameters)).Where(candidate =>
                    candidate.Setter is not null && (isSimpleAssignment || candidate.Getter is not null)),
                indices,
                target.OpenBracketToken.Location,
                structType.Name,
                target);
            if (indexer is null)
                return new BoundErrorExpression();
            RecordSymbolAndType(target, indexer, indexer.Type);
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(indexer);
            if (!indexer.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, indexer.ContainingType))
            {
                RecordCandidates(target, null, [indexer], CandidateReason.Inaccessible);
                _diagnostics.Report(target.OpenBracketToken.Location, $"indexer is private in struct '{indexer.ContainingType.Name}'",
                    DiagnosticIds.InaccessibleSymbol);
            }
            if (!IsAddressable(receiver) || !IsWritable(receiver))
            {
                _diagnostics.Report(target.OpenBracketToken.Location, "indexer cannot be assigned through a readonly receiver",
                    DiagnosticIds.WriteThroughReadonlyReceiver);
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
            interfaceType.Name,
            target);
        if (interfaceIndexer is null)
            return new BoundErrorExpression();
        RecordSymbolAndType(target, interfaceIndexer, interfaceIndexer.Type);
        _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(interfaceIndexer);
        if (!IsAddressable(receiver) || !IsWritable(receiver))
        {
            _diagnostics.Report(target.OpenBracketToken.Location, "indexer cannot be assigned through a readonly receiver",
                DiagnosticIds.WriteThroughReadonlyReceiver);
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
        if (!TypeIdentity.AreSame(resultType, getter.ReturnType))
        {
            _diagnostics.Report(
                syntax.OperatorToken.Location,
                $"operator '{syntax.OperatorToken.Text}' is not defined for types '{getter.ReturnType.ToDisplayString()}' and '{value.Type.ToDisplayString()}'",
                DiagnosticIds.InvalidOperatorOperands);
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
                _function.ContainingType?.FindInstanceField(identifier) is not null)
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
            _function.ContainingType?.FindInstanceField(firstName) is not null ||
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
            if (!TypeIdentity.AreSame(resolvedType, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, $"type '{syntax.Type.Name}' is not a struct",
                    DiagnosticIds.TypeIsNotStruct);
            }

            return new BoundErrorExpression();
        }

        if (structType.IsAbstract)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);
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
            if (elementType is StructTypeSymbol structure)
                ValidateDestructorAccess(structure.FindDestructor(), allocationLocation);
            if (elementType is ArrayTypeSymbol)
                _diagnostics.Report(elementLocation, "stack arrays cannot contain array elements",
                    DiagnosticIds.NestedStackArrayElement);
        }

        var dimensions = dimensionSyntax.Select(BindExpression).ToImmutableArray();
        if (dimensions.IsEmpty)
        {
            _diagnostics.Report(allocationLocation, "array allocation requires at least one dimension",
                DiagnosticIds.ArrayDimensionRequired);
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
            _diagnostics.Report(allocationLocation, "total array length exceeds int.MaxValue",
                DiagnosticIds.TotalArrayLengthOverflow);

        ArrayTypeSymbol arrayType = _fileScope.TypeFactory.ArrayOf(elementType, dimensions.Length);
        return new BoundArrayCreationExpression(elementType, dimensions[0], arrayType, storage) { Dimensions = dimensions };
    }

    private BoundExpression BindCallExpression(CallExpressionSyntax syntax)
    {
        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        if (syntax.TypeArguments is { } typeArguments)
            return BindExplicitGenericCall(syntax, typeArguments, arguments);
        bool incomplete = syntax.CloseParenthesisToken.IsMissing;
        int completedArgumentCount = GetCompletedArgumentCount(syntax.Arguments, incomplete);

        if (syntax.Target is MemberAccessExpressionSyntax memberTarget)
        {
            if (TryBindStaticMethodCall(memberTarget, arguments, syntax.Arguments, syntax) is BoundExpression staticCall)
                return staticCall;
            BoundExpression? qualifiedCall = TryBindQualifiedCallExpression(
                memberTarget,
                arguments,
                syntax.Arguments,
                syntax);
            return qualifiedCall ?? BindMethodCallExpression(memberTarget, arguments, syntax.Arguments, syntax);
        }

        if (syntax.Target is not NameExpressionSyntax name)
        {
            RecordCandidates(syntax, null, [], CandidateReason.NotInvocable);
            RecordCandidates(syntax.Target, null, [], CandidateReason.NotInvocable);
            _diagnostics.Report(GetLocation(syntax.Target), "call target must be a function, method, or struct name",
                DiagnosticIds.InvalidCallTarget);
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
                _diagnostics.Report(name.IdentifierToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated",
                    DiagnosticIds.AbstractInstantiation);
                return new BoundErrorExpression();
            }
            if (structType.Constructors.IsEmpty && completedArgumentCount == 0)
            {
                RecordCandidates(syntax.Target, structType, [], incomplete ? CandidateReason.Incomplete : CandidateReason.None);
                ValidateDefaultInitialization(structType, name.IdentifierToken.Location);
                return new BoundStructConstructionExpression(structType, []) { IsDefaultInitialization = true };
            }
            FunctionSymbol? constructor = ResolveConstructor(structType, arguments, syntax.Arguments,
                name.IdentifierToken.Location, out CandidateReason constructorReason,
                incomplete ? completedArgumentCount : null);
            RecordCandidates(syntax.Target, constructor, structType.Constructors, constructorReason);
            if (constructor is null)
            {
                if (constructorReason == CandidateReason.Incomplete)
                    return new BoundErrorExpression();
                if (structType.Constructors.IsEmpty)
                    _diagnostics.Report(
                        name.IdentifierToken.Location,
                        $"struct '{structType.Name}' does not declare a constructor; use '{structType.Name} {{ ... }}' for positional construction",
                        DiagnosticIds.MissingConstructor);
                return new BoundErrorExpression();
            }

            if (!constructor.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, structType))
            {
                RecordCandidates(syntax.Target, null, structType.Constructors, CandidateReason.Inaccessible);
                _diagnostics.Report(name.IdentifierToken.Location, $"constructor '{structType.Name}' is private",
                    DiagnosticIds.InaccessibleSymbol);
            }

            arguments = ValidateFunctionArguments(constructor, arguments, syntax.Arguments, name.IdentifierToken.Location,
                incomplete ? completedArgumentCount : null);
            return new BoundConstructorCallExpression(structType, constructor, arguments);
        }

        if (_function.ContainingType is { } containingType)
        {
            FunctionSymbol[] methodCandidates = containingType.LookupMethods(name.IdentifierToken.Text)
                .Where(candidate => !candidate.IsStatic).ToArray();
            FunctionSymbol? method = FindInstanceMethod(containingType,
                name.IdentifierToken.Text,
                receiverIsReadonly: _function.IsReadonly);
            if (method is null &&
                _function.IsReadonly &&
                containingType.FindMember<FunctionSymbol>(name.IdentifierToken.Text) is { IsStatic: false } mutableMethod)
            {
                RecordCandidates(syntax.Target, null, [mutableMethod], CandidateReason.Inaccessible);
                _diagnostics.Report(name.IdentifierToken.Location, $"readonly method '{_function.Name}' cannot call mutable method '{mutableMethod.Name}' through 'this'",
                    DiagnosticIds.MutableMethodOnReadonlyReceiver);
                return new BoundErrorExpression();
            }
            if (method is not null)
            {
                CandidateReason methodReason = GetCallCandidateReason(method, arguments, incomplete, completedArgumentCount);
                RecordCandidates(syntax.Target, method, methodCandidates, methodReason);
                if (_bindingBaseConstructorArguments)
                {
                    _diagnostics.Report(name.IdentifierToken.Location, "the derived object cannot be used in base constructor arguments",
                        DiagnosticIds.DerivedInstanceInBaseConstructorArguments);
                    return new BoundErrorExpression();
                }
                if (!method.IsPublic && !TypeIdentity.AreSame(containingType, method.ContainingType))
                {
                    RecordCandidates(syntax.Target, null, methodCandidates, CandidateReason.Inaccessible);
                    _diagnostics.Report(name.IdentifierToken.Location, $"method '{method.Name}' is private in struct '{method.ContainingType!.Name}'",
                        DiagnosticIds.InaccessibleSymbol);
                    return new BoundErrorExpression();
                }
                if (_function.IsStatic)
                {
                    _diagnostics.Report(name.IdentifierToken.Location, $"static method '{_function.Name}' cannot call instance method '{method.Name}' without an explicit instance",
                        DiagnosticIds.StaticContextInstanceMethodCall);
                    return new BoundErrorExpression();
                }
                if (method.IsStatic)
                {
                    _diagnostics.Report(name.IdentifierToken.Location, $"static method '{method.Name}' must be accessed through type '{containingType.Name}'",
                        DiagnosticIds.StaticMethodRequiresTypeReceiver);
                    return new BoundErrorExpression();
                }
                arguments = ValidateFunctionArguments(method, arguments, syntax.Arguments, name.IdentifierToken.Location,
                    incomplete ? completedArgumentCount : null);
                ValidateRequiredFields(name.IdentifierToken.Location);
                PointerTypeSymbol thisType = _fileScope.TypeFactory.PointerTo(containingType, isReadonly: _function.IsReadonly);
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
                _diagnostics.Report(name.IdentifierToken.Location, $"unknown function '{name.IdentifierToken.Text}'",
                    DiagnosticIds.UnknownFunction);
            }

            return new BoundErrorExpression();
        }

        if (function.IsGenericDefinition)
        {
            if (TryInferGenericTypeArguments(function, arguments, out ImmutableArray<TypeSymbol> inferredArguments) &&
                inferredArguments.Any(ContainsGenericParameter))
                return BindOpenGenericCall(syntax, function, inferredArguments, arguments,
                    name.IdentifierToken.Location);
            bool inferenceSucceeded = false;
            FunctionSymbol? specialized = _genericSpecializer is null ? null :
                _genericSpecializer.InferAndCreate(function, arguments,
                    name.IdentifierToken.Location, out inferenceSucceeded);
            if (specialized is null)
            {
                if (!inferenceSucceeded)
                    _diagnostics.Report(name.IdentifierToken.Location,
                        $"type arguments for generic function '{function.Name}' could not be inferred",
                        DiagnosticIds.GenericSpecializationNotImplemented);
                return new BoundErrorExpression();
            }
            RecordCandidates(syntax.Target, specialized, [function], CandidateReason.None);
            arguments = ValidateFunctionArguments(specialized, arguments, syntax.Arguments,
                name.IdentifierToken.Location, incomplete ? completedArgumentCount : null);
            return new BoundCallExpression(specialized, arguments);
        }

        CandidateReason functionReason = GetCallCandidateReason(function, arguments, incomplete, completedArgumentCount);
        RecordCandidates(syntax.Target, function, [function], functionReason);
        arguments = ValidateFunctionArguments(function, arguments, syntax.Arguments, name.IdentifierToken.Location,
            incomplete ? completedArgumentCount : null);
        return new BoundCallExpression(function, arguments);
    }

    private BoundExpression BindExplicitGenericCall(CallExpressionSyntax syntax,
        TypeArgumentListSyntax typeArguments, ImmutableArray<BoundExpression> arguments)
    {
        FunctionSymbol? definition = null;
        bool diagnosticReported = false;
        TextLocation location = typeArguments.LessToken.Location;
        if (syntax.Target is NameExpressionSyntax name)
        {
            location = name.IdentifierToken.Location;
            TypeSymbol? possibleType = _fileScope.ResolveType(name.IdentifierToken.Text, location,
                new DiagnosticBag());
            if (possibleType is StructTypeSymbol { IsGenericDefinition: true })
                return BindExplicitGenericStructConstruction(syntax, name, typeArguments, arguments);
            definition = _fileScope.ResolveFunction(name.IdentifierToken.Text, location, _diagnostics,
                out diagnosticReported);
        }
        else if (syntax.Target is MemberAccessExpressionSyntax member &&
                 TryGetDottedName(member, out ImmutableArray<SyntaxToken> parts))
        {
            location = member.MemberToken.Location;
            definition = _fileScope.ResolveQualifiedFunction(parts.Select(part => part.Text).ToArray(),
                location, _diagnostics, out diagnosticReported);
        }
        if (definition is null)
        {
            if (!diagnosticReported)
                _diagnostics.Report(location, "generic call target must name a function",
                    DiagnosticIds.InvalidCallTarget);
            return new BoundErrorExpression();
        }
        if (definition.TypeParameters.IsEmpty || definition.ContainingType is not null)
        {
            _diagnostics.Report(location, $"function '{definition.Name}' is not generic",
                DiagnosticIds.GenericSpecializationNotImplemented);
            return new BoundErrorExpression();
        }

        ImmutableArray<TypeSymbol> resolvedArguments = typeArguments.Arguments
            .Select(argument => TypeResolver.Resolve(argument, _fileScope, _diagnostics)).ToImmutableArray();
        if (resolvedArguments.Any(ContainsGenericParameter))
            return BindOpenGenericCall(syntax, definition, resolvedArguments, arguments, location);
        FunctionSymbol? specialized = _genericSpecializer?.GetOrCreate(definition, resolvedArguments, location);
        if (specialized is null) return new BoundErrorExpression();
        RecordCandidates(syntax.Target, specialized, [definition], CandidateReason.None);
        arguments = ValidateFunctionArguments(specialized, arguments, syntax.Arguments, location,
            syntax.CloseParenthesisToken.IsMissing ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        return new BoundCallExpression(specialized, arguments);
    }

    private BoundExpression BindExplicitGenericStructConstruction(CallExpressionSyntax syntax,
        NameExpressionSyntax name, TypeArgumentListSyntax typeArguments,
        ImmutableArray<BoundExpression> arguments)
    {
        var typeSyntax = new NamedTypeSyntax([name.IdentifierToken], [], typeArguments);
        TypeSymbol resolved = TypeResolver.Resolve(typeSyntax, _fileScope, _diagnostics);
        if (resolved is not StructTypeSymbol structure) return new BoundErrorExpression();
        if (structure.IsAbstract)
        {
            _diagnostics.Report(name.IdentifierToken.Location,
                $"abstract struct '{structure.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);
            return new BoundErrorExpression();
        }
        bool incomplete = syntax.CloseParenthesisToken.IsMissing;
        int completedArgumentCount = GetCompletedArgumentCount(syntax.Arguments, incomplete);
        if (structure.Constructors.IsEmpty && completedArgumentCount == 0)
        {
            RecordCandidates(syntax.Target, structure, [], incomplete ? CandidateReason.Incomplete : CandidateReason.None);
            ValidateDefaultInitialization(structure, name.IdentifierToken.Location);
            return new BoundStructConstructionExpression(structure, []) { IsDefaultInitialization = true };
        }
        FunctionSymbol? constructor = ResolveConstructor(structure, arguments, syntax.Arguments,
            name.IdentifierToken.Location, out CandidateReason reason,
            incomplete ? completedArgumentCount : null);
        RecordCandidates(syntax.Target, constructor, structure.Constructors, reason);
        if (constructor is null)
        {
            if (!incomplete && structure.Constructors.IsEmpty)
                _diagnostics.Report(name.IdentifierToken.Location,
                    $"struct '{structure.Name}' does not declare a constructor",
                    DiagnosticIds.MissingConstructor);
            return new BoundErrorExpression();
        }
        if (!constructor.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, structure))
            _diagnostics.Report(name.IdentifierToken.Location,
                $"constructor '{structure.Name}' is private", DiagnosticIds.InaccessibleSymbol);
        arguments = ValidateFunctionArguments(constructor, arguments, syntax.Arguments,
            name.IdentifierToken.Location, incomplete ? completedArgumentCount : null);
        return new BoundConstructorCallExpression(structure, constructor, arguments);
    }

    private BoundExpression BindOpenGenericCall(CallExpressionSyntax syntax, FunctionSymbol definition,
        ImmutableArray<TypeSymbol> typeArguments, ImmutableArray<BoundExpression> arguments, TextLocation location)
    {
        if (typeArguments.Length != definition.TypeParameters.Length)
        {
            _diagnostics.Report(location,
                $"generic function '{definition.Name}' expects {definition.TypeParameters.Length} type argument(s), but {typeArguments.Length} were provided",
                DiagnosticIds.GenericArityMismatch);
            return new BoundErrorExpression();
        }
        for (int index = 0; index < typeArguments.Length; index++)
        {
            if (typeArguments[index] is not GenericParameterSymbol argumentParameter) continue;
            foreach (GenericConstraintSymbol required in definition.TypeParameters[index].Constraints)
            {
                if (GenericConstraintGuarantees.IsGuaranteed(argumentParameter, required)) continue;
                _diagnostics.Report(location,
                    $"constraints for '{argumentParameter.Name}' do not guarantee '{required.Target.Name}' required by '{definition.Name}'" +
                    GenericConstraintGuarantees.GetFailureDetail(argumentParameter, required),
                    DiagnosticIds.GenericConstraintNotSatisfied);
                return new BoundErrorExpression();
            }
        }
        var substitutions = definition.TypeParameters.Zip(typeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        ImmutableArray<TypeSymbol> parameterTypes = definition.Parameters
            .Select(parameter => SubstituteGenericType(parameter.Type, substitutions)).ToImmutableArray();
        _ = ValidateGenericArguments(definition.Name, parameterTypes, arguments, syntax.Arguments, location,
            syntax.CloseParenthesisToken.IsMissing ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        RecordCandidates(syntax.Target, definition, [definition], CandidateReason.None);
        return new BoundDeferredConstantExpression(SubstituteGenericType(definition.ReturnType, substitutions));
    }

    private BoundExpression? TryBindStaticMethodCall(MemberAccessExpressionSyntax target, ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax, CallExpressionSyntax callSyntax)
    {
        if (!TryGetDottedName(target, out ImmutableArray<SyntaxToken> parts) || parts.Length < 2)
            return null;
        string[] typeParts = parts.Take(parts.Length - 1).Select(token => token.Text).ToArray();
        TypeSymbol? resolved = typeParts.Length == 1
            ? _fileScope.ResolveType(typeParts[0], parts[0].Location, _diagnostics)
            : _fileScope.ResolveQualifiedType(typeParts);
        if (resolved is not DeclaredTypeSymbol structType)
            return null;
        FunctionSymbol? method = structType.FindMember<FunctionSymbol>(parts[^1].Text);
        if (method is null || !method.IsStatic)
            return null;
        bool incomplete = callSyntax.CloseParenthesisToken.IsMissing;
        int completedArgumentCount = GetCompletedArgumentCount(argumentSyntax, incomplete);
        RecordStaticReceiver(target.Receiver, structType);
        RecordCandidates(target, method, [method],
            GetCallCandidateReason(method, arguments, incomplete, completedArgumentCount));
        if (!method.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, method.ContainingType))
        {
            RecordCandidates(target, null, [method], CandidateReason.Inaccessible);
            _diagnostics.Report(parts[^1].Location, $"static method '{method.Name}' is private in struct '{method.ContainingType!.Name}'",
                DiagnosticIds.InaccessibleSymbol);
            return new BoundErrorExpression();
        }
        arguments = ValidateFunctionArguments(method, arguments, argumentSyntax, parts[^1].Location,
            incomplete ? completedArgumentCount : null);
        return new BoundCallExpression(method, arguments);
    }

    private BoundExpression? TryBindQualifiedCallExpression(
        MemberAccessExpressionSyntax target,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        CallExpressionSyntax callSyntax)
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

        if (_function.ContainingType?.FindInstanceField(firstName) is not null)
        {
            return null;
        }

        if (!_fileScope.CanStartQualifiedName(firstName))
        {
            return null;
        }

        string[] parts = nameParts.Select(part => part.Text).ToArray();
        bool incomplete = callSyntax.CloseParenthesisToken.IsMissing;
        int completedArgumentCount = GetCompletedArgumentCount(argumentSyntax, incomplete);
        StructTypeSymbol? structType = _fileScope.ResolveQualifiedType(parts) as StructTypeSymbol;
        if (structType is not null)
        {
            if (structType.IsAbstract)
            {
                _diagnostics.Report(target.MemberToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated",
                    DiagnosticIds.AbstractInstantiation);
                return new BoundErrorExpression();
            }
            if (structType.Constructors.IsEmpty && completedArgumentCount == 0)
            {
                ValidateDefaultInitialization(structType, target.MemberToken.Location);
                return new BoundStructConstructionExpression(structType, []) { IsDefaultInitialization = true };
            }
            FunctionSymbol? constructor = ResolveConstructor(structType, arguments, argumentSyntax,
                target.MemberToken.Location, out CandidateReason constructorReason,
                incomplete ? completedArgumentCount : null);
            RecordCandidates(target, constructor, structType.Constructors, constructorReason);
            if (constructor is null)
            {
                if (constructorReason == CandidateReason.Incomplete)
                    return new BoundErrorExpression();
                if (structType.Constructors.IsEmpty)
                    _diagnostics.Report(
                        target.MemberToken.Location,
                        $"struct '{structType.Name}' does not declare a constructor; use '{structType.Name} {{ ... }}' for positional construction",
                        DiagnosticIds.MissingConstructor);
                return new BoundErrorExpression();
            }

            if (!constructor.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, structType))
            {
                RecordCandidates(target, null, structType.Constructors, CandidateReason.Inaccessible);
                _diagnostics.Report(target.MemberToken.Location, $"constructor '{structType.Name}' is private",
                    DiagnosticIds.InaccessibleSymbol);
            }

            arguments = ValidateFunctionArguments(constructor, arguments, argumentSyntax, target.MemberToken.Location,
                incomplete ? completedArgumentCount : null);
            return new BoundConstructorCallExpression(structType, constructor, arguments);
        }

        FunctionSymbol? function = _fileScope.ResolveQualifiedFunction(
            parts,
            target.MemberToken.Location,
            _diagnostics,
            out bool resolutionDiagnostic);
        if (function is not null)
        {
            RecordCandidates(target, function, [function],
                GetCallCandidateReason(function, arguments, incomplete, completedArgumentCount));
            arguments = ValidateFunctionArguments(function, arguments, argumentSyntax, target.MemberToken.Location,
                incomplete ? completedArgumentCount : null);
            return new BoundCallExpression(function, arguments);
        }

        if (!resolutionDiagnostic)
        {
            _diagnostics.Report(
                target.MemberToken.Location,
                $"unknown function or struct '{string.Join('.', parts)}'",
                DiagnosticIds.UnknownQualifiedCallable);
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
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        CallExpressionSyntax callSyntax)
    {
        BoundExpression receiver = BindExpression(target.Receiver);
        bool incomplete = callSyntax.CloseParenthesisToken.IsMissing;
        int completedArgumentCount = GetCompletedArgumentCount(argumentSyntax, incomplete);
        if (receiver.Type is ArrayTypeSymbol array && target.OperatorToken.Kind == SyntaxKind.DotToken && target.MemberToken.Text == "GetLength")
        {
            SyntheticMemberSymbol member = _semanticInfo.GetArrayMembers(array)
                .Single(candidate => candidate.Name == "GetLength");
            CandidateReason reason = incomplete
                ? completedArgumentCount > member.Parameters.Length ? CandidateReason.WrongArity
                    : completedArgumentCount == 1 && !TypeIdentity.AreSame(arguments[0].Type, BuiltinTypes.Int)
                        ? CandidateReason.NotInvocable
                        : CandidateReason.Incomplete
                : arguments.Length != member.Parameters.Length ? CandidateReason.WrongArity
                : !TypeIdentity.AreSame(arguments[0].Type, BuiltinTypes.Int) ? CandidateReason.NotInvocable
                : CandidateReason.None;
            RecordCandidates(target, member, [member], reason);
            if ((!incomplete && arguments.Length != 1) || completedArgumentCount > 1 ||
                completedArgumentCount == 1 && !TypeIdentity.AreSame(arguments[0].Type, BuiltinTypes.Int))
            {
                _diagnostics.Report(target.MemberToken.Location, "GetLength requires one int dimension argument",
                    DiagnosticIds.InvalidGetLengthArguments);
                return new BoundErrorExpression();
            }
            if (completedArgumentCount == 1 && _constants.TryFold(arguments[0], out object? dimension) &&
                (SemanticAnalyzer.ToInteger(dimension) < 0 || SemanticAnalyzer.ToInteger(dimension) >= array.Rank))
                _diagnostics.Report(target.MemberToken.Location, $"GetLength dimension must be between 0 and {array.Rank - 1}",
                    DiagnosticIds.GetLengthDimensionOutOfRange);
            return new BoundArrayMetadataExpression(receiver, "GetLength", arguments[0]);
        }
        bool pointerAccess = target.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
        if (GetGenericReceiver(receiver.Type, pointerAccess) is GenericParameterSymbol genericParameter)
            return BindGenericMethodCall(target, receiver, genericParameter, arguments, argumentSyntax,
                pointerAccess, incomplete, completedArgumentCount);
        InterfaceTypeSymbol? interfaceType = pointerAccess
            ? (receiver.Type as PointerTypeSymbol)?.ElementType as InterfaceTypeSymbol
            : receiver.Type as InterfaceTypeSymbol;
        if (interfaceType is not null)
        {
            FunctionSymbol? interfaceMethod = ResolveInterfaceMethod(interfaceType, target.MemberToken.Text, arguments,
                IsReadonlyReceiver(receiver, pointerAccess), target.MemberToken.Location, target,
                incomplete ? completedArgumentCount : null);
            if (interfaceMethod is null)
            {
                return new BoundErrorExpression();
            }
            if (IsReadonlyReceiver(receiver, pointerAccess) && !interfaceMethod.IsReadonly)
            {
                _diagnostics.Report(target.MemberToken.Location, $"mutable interface method '{interfaceMethod.Name}' cannot be called on a readonly '{interfaceType.Name}' receiver",
                    DiagnosticIds.MutableMethodOnReadonlyReceiver);
                return new BoundErrorExpression();
            }
            arguments = ValidateFunctionArguments(interfaceMethod, arguments, argumentSyntax, target.MemberToken.Location,
                incomplete ? completedArgumentCount : null);
            return new BoundInterfaceMethodCallExpression(receiver, interfaceType, interfaceMethod, arguments, pointerAccess);
        }
        DeclaredTypeSymbol? structType = pointerAccess
            ? (receiver.Type as PointerTypeSymbol)?.ElementType as DeclaredTypeSymbol
            : receiver.Type as DeclaredTypeSymbol;

        if (structType is null)
        {
            if (!TypeIdentity.AreSame(receiver.Type, BuiltinTypes.Error))
            {
                string expected = pointerAccess ? "pointer to struct" : "struct";
                _diagnostics.Report(
                    target.OperatorToken.Location,
                    $"operator '{target.OperatorToken.Text}' requires a {expected}, but has type '{receiver.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidMemberReceiver);
            }

            return new BoundErrorExpression();
        }

        bool hasReadonlyReceiver =
            (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
            (!pointerAccess && IsAddressable(receiver) && !IsWritable(receiver));

        FunctionSymbol[] methodCandidates = structType.LookupMethods(target.MemberToken.Text)
            .Where(candidate => !candidate.IsStatic).ToArray();
        FunctionSymbol? method = FindInstanceMethod(structType, target.MemberToken.Text, hasReadonlyReceiver || _function.IsReadonly);
        // Prefer a readonly overload in readonly code, but retain a mutable
        // candidate so the effect checker can report a disallowed call.
        if (method is null && !hasReadonlyReceiver)
            method = FindInstanceMethod(structType, target.MemberToken.Text, receiverIsReadonly: false);
        if (method is null)
        {
            FunctionSymbol? namedMethod = structType.FindMember<FunctionSymbol>(target.MemberToken.Text);
            RecordCandidates(target, null, methodCandidates,
                namedMethod is not null && (hasReadonlyReceiver || !namedMethod.IsPublic)
                    ? CandidateReason.Inaccessible
                    : namedMethod?.IsStatic == true ? CandidateReason.NotInvocable : CandidateReason.NotFound);
            if (namedMethod?.IsStatic == true)
            {
                _diagnostics.Report(target.MemberToken.Location, $"static method '{namedMethod.Name}' must be accessed through type '{structType.Name}'",
                    DiagnosticIds.StaticMethodRequiresTypeReceiver);
            }
            else if (hasReadonlyReceiver && namedMethod is not null)
            {
                _diagnostics.Report(
                    target.MemberToken.Location,
                    $"mutable method '{namedMethod.Name}' cannot be called on a readonly '{structType.Name}' receiver",
                    DiagnosticIds.MutableMethodOnReadonlyReceiver);
            }
            else
            {
                _diagnostics.Report(
                    target.MemberToken.Location,
                    $"struct '{structType.Name}' does not contain method '{target.MemberToken.Text}'",
                    DiagnosticIds.MissingStructMethod);
            }
            return new BoundErrorExpression();
        }

        RecordCandidates(target, method, methodCandidates,
            GetCallCandidateReason(method, arguments, incomplete, completedArgumentCount));

        if (!method.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, method.ContainingType))
        {
            RecordCandidates(target, null, methodCandidates, CandidateReason.Inaccessible);
            _diagnostics.Report(
                target.MemberToken.Location,
                $"method '{method.Name}' is private in struct '{method.ContainingType!.Name}'",
                DiagnosticIds.InaccessibleSymbol);
        }

        arguments = ValidateFunctionArguments(method, arguments, argumentSyntax, target.MemberToken.Location,
            incomplete ? completedArgumentCount : null);
        return new BoundMethodCallExpression(receiver, method, arguments, pointerAccess);
    }

    private static FunctionSymbol? FindInstanceMethod(DeclaredTypeSymbol type, string name, bool receiverIsReadonly)
    {
        FunctionSymbol? Find(bool isReadonly) => type.LookupMethods(name)
            .FirstOrDefault(method => method.IsReadonly == isReadonly);
        if (!receiverIsReadonly && Find(false) is { IsStatic: false } mutable) return mutable;
        return Find(true) is { IsStatic: false } readOnly ? readOnly : null;
    }

    private BoundExpression BindNewExpression(NewExpressionSyntax syntax)
    {
        TypeSymbol type = TypeResolver.Resolve(syntax.Type, _fileScope, _diagnostics);

        if (syntax.IsArrayAllocation)
        {
            return BindArrayCreation(type, syntax.Arguments, syntax.Type.NameToken.Location, syntax.OpenDelimiterToken.Location, ArrayStorageKind.Heap);
        }

        if (type is GenericParameterSymbol genericParameter)
            return BindGenericNewExpression(syntax, genericParameter);

        if (type is not StructTypeSymbol structType)
        {
            if (!TypeIdentity.AreSame(type, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, $"'new' requires a struct type or array element type, but has type '{type.Name}'",
                    DiagnosticIds.NewRequiresStructType);
            }

            return new BoundErrorExpression();
        }

        if (structType.IsAbstract)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);
        }

        var arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        bool incomplete = syntax.CloseDelimiterToken.IsMissing;
        int completedArgumentCount = GetCompletedArgumentCount(syntax.Arguments, incomplete);
        FunctionSymbol? constructor = null;
        if (!syntax.IsPositionalInitialization && structType.Constructors.IsEmpty && completedArgumentCount == 0)
        {
            RecordCandidates(syntax, structType, [], syntax.CloseDelimiterToken.IsMissing
                ? CandidateReason.Incomplete : CandidateReason.None);
            ValidateDefaultInitialization(structType, syntax.Type.NameToken.Location);
            return new BoundNewExpression(structType, null, [], true, _fileScope.TypeFactory.PointerTo(structType))
                { IsDefaultInitialization = true };
        }
        if (syntax.IsPositionalInitialization)
        {
            arguments = ValidatePositionalArguments(structType, arguments, syntax.Arguments, syntax.NewKeyword.Location);
        }
        else
        {
            constructor = ResolveConstructor(structType, arguments, syntax.Arguments,
                syntax.NewKeyword.Location, out CandidateReason constructorReason,
                incomplete ? completedArgumentCount : null);
            RecordCandidates(syntax, constructor, structType.Constructors, constructorReason);
            if (constructor is null)
            {
                if (constructorReason == CandidateReason.Incomplete)
                    return new BoundErrorExpression();
                if (structType.Constructors.IsEmpty)
                    _diagnostics.Report(
                        syntax.Type.NameToken.Location,
                        $"struct '{structType.Name}' does not declare a constructor; use 'new {structType.Name} {{ ... }}' for positional construction",
                        DiagnosticIds.MissingConstructor);
                return new BoundErrorExpression();
            }

            if (!constructor.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, structType))
            {
                RecordCandidates(syntax, null, structType.Constructors, CandidateReason.Inaccessible);
                _diagnostics.Report(syntax.Type.NameToken.Location, $"constructor '{structType.Name}' is private",
                    DiagnosticIds.InaccessibleSymbol);
            }

            arguments = ValidateFunctionArguments(constructor, arguments, syntax.Arguments, syntax.NewKeyword.Location,
                incomplete ? completedArgumentCount : null);
        }

        PointerTypeSymbol pointerType = _fileScope.TypeFactory.PointerTo(structType);
        return new BoundNewExpression(structType, constructor, arguments, syntax.IsPositionalInitialization, pointerType);
    }

    private BoundExpression BindFreeExpression(FreeExpressionSyntax syntax)
    {
        BoundExpression pointer = BindExpression(syntax.Pointer);
        if (TypeIdentity.AreSame(pointer.Type, BuiltinTypes.Null))
            pointer = ContextualizeNull(pointer, _fileScope.TypeFactory.PointerTo(BuiltinTypes.Void));
        if (GetArrayStorage(pointer) == ArrayStorageKind.Stack)
        {
            _diagnostics.Report(syntax.FreeKeyword.Location, "stack array cannot be freed",
                DiagnosticIds.StackArrayFree);
            return new BoundErrorExpression();
        }

        FunctionSymbol? destructor = null;
        if (pointer.Type is PointerTypeSymbol pointerType)
        {
            if (pointerType.IsReadonly)
                _diagnostics.Report(syntax.FreeKeyword.Location, "cannot free memory through a readonly pointer",
                    DiagnosticIds.FreeThroughReadonlyPointer);
            if (pointerType.ElementType is StructTypeSymbol structType)
            {
                destructor = structType.FindDestructor();
            }
        }
        else if (pointer.Type is ArrayTypeSymbol arrayType)
        {
            if (arrayType.ElementType is StructTypeSymbol structure) destructor = structure.FindDestructor();
        }
        else if (!TypeIdentity.AreSame(pointer.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(
                syntax.FreeKeyword.Location,
                $"'free' requires a heap pointer or heap array, but has type '{pointer.Type.ToDisplayString()}'",
                DiagnosticIds.InvalidFreeOperand);
            return new BoundErrorExpression();
        }

        ValidateDestructorAccess(destructor, syntax.FreeKeyword.Location);
        return new BoundFreeExpression(pointer, destructor);
    }

    private void ValidateDestructorAccess(FunctionSymbol? destructor, TextLocation location)
    {
        if (destructor is { IsPublic: false } && !TypeIdentity.AreSame(_function.ContainingType, destructor.ContainingType))
            _diagnostics.Report(location, $"destructor '{destructor.ContainingType!.Name}' is private",
                DiagnosticIds.InaccessibleSymbol);
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
                $"struct '{structType.Name}' expects {fields.Length} positional value(s), but {arguments.Length} were provided",
                DiagnosticIds.PositionalValueCountMismatch);
        }

        var convertedArguments = arguments.ToBuilder();
        int count = Math.Min(arguments.Length, fields.Length);
        for (int index = 0; index < count; index++)
        {
            FieldSymbol field = fields[index];
            TypeSymbol fieldType = field.Type;
            BoundExpression argument = ContextualizeConversion(arguments[index], fieldType);
            convertedArguments[index] = argument;
            SetConvertedType(argumentSyntax[index], argument.Type);

            if (!field.IsPublic && !TypeIdentity.AreSame(_function.ContainingType, field.ContainingType))
            {
                _diagnostics.Report(
                    GetLocation(argumentSyntax[index]),
                    $"field '{field.Name}' is private in struct '{field.ContainingType.Name}'",
                    DiagnosticIds.InaccessibleSymbol);
            }

            if (GetArrayStorage(argument) == ArrayStorageKind.Stack)
            {
                _diagnostics.Report(
                    GetLocation(argumentSyntax[index]),
                    "stack array cannot be stored inside a positional struct value",
                    DiagnosticIds.StackArrayStoredInAggregate);
            }

            if (!TypeFacts.CanAssign(fieldType, argument.Type))
            {
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, fieldType);
            }
        }

        return convertedArguments.ToImmutable();
    }

    private static int GetCompletedArgumentCount(
        ImmutableArray<ExpressionSyntax> arguments,
        bool incomplete)
    {
        if (!incomplete) return arguments.Length;
        int count = arguments.Length;
        while (count > 0 && arguments[count - 1] is MissingExpressionSyntax) count--;
        return count;
    }

    private CandidateReason GetCallCandidateReason(
        FunctionSymbol function,
        ImmutableArray<BoundExpression> arguments,
        bool incomplete,
        int completedArgumentCount)
    {
        if (!incomplete)
            return function.Parameters.Length == arguments.Length
                ? CandidateReason.None
                : CandidateReason.WrongArity;
        if (completedArgumentCount > function.Parameters.Length)
            return CandidateReason.WrongArity;
        for (int index = 0; index < completedArgumentCount; index++)
            if (GetArgumentConversionCost(function.Parameters[index].Type, arguments[index]) is null)
                return CandidateReason.NotInvocable;
        return CandidateReason.Incomplete;
    }

    private ImmutableArray<BoundExpression> ValidateFunctionArguments(
        FunctionSymbol function,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location,
        int? completedArgumentCount = null)
    {
        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        if ((!incomplete && suppliedCount != function.Parameters.Length) ||
            incomplete && suppliedCount > function.Parameters.Length)
        {
            _diagnostics.Report(
                location,
                $"function '{function.Name}' expects {function.Parameters.Length} argument(s), but {suppliedCount} were provided",
                DiagnosticIds.WrongArity);
        }

        var convertedArguments = arguments.ToBuilder();
        int count = Math.Min(suppliedCount, function.Parameters.Length);
        for (int index = 0; index < count; index++)
        {
            TypeSymbol parameterType = function.Parameters[index].Type;
            BoundExpression argument = ContextualizeConversion(arguments[index], parameterType);
            convertedArguments[index] = argument;
            SetConvertedType(argumentSyntax[index], argument.Type);

            if (GetArrayStorage(argument) == ArrayStorageKind.Stack)
            {
                _diagnostics.Report(GetLocation(argumentSyntax[index]), "stack array cannot be passed to another function",
                    DiagnosticIds.StackArrayPassedAsArgument);
            }

            if (!TypeFacts.CanAssign(parameterType, argument.Type))
            {
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, parameterType);
            }
        }

        return convertedArguments.ToImmutable();
    }

    private FunctionSymbol? ResolveInterfaceMethod(InterfaceTypeSymbol type, string name,
        ImmutableArray<BoundExpression> arguments, bool readonlyReceiver, TextLocation location, SyntaxNode syntax,
        int? completedArgumentCount = null)
    {
        FunctionSymbol[] candidates = type.FindMethods(name).ToArray();
        if (candidates.Length == 0)
        {
            RecordCandidates(syntax, null, [], CandidateReason.NotFound);
            _diagnostics.Report(location, $"interface '{type.Name}' does not contain method '{name}'",
                DiagnosticIds.MissingInterfaceMethod);
            return null;
        }
        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        // Preserve the established argument/readonly diagnostics for a single candidate.
        if (candidates.Length == 1)
        {
            CandidateReason singleReason = readonlyReceiver && !candidates[0].IsReadonly
                ? CandidateReason.Inaccessible
                : incomplete
                    ? GetCallCandidateReason(candidates[0], arguments, true, suppliedCount)
                    : candidates[0].Parameters.Length != arguments.Length ? CandidateReason.WrongArity
                    : CandidateReason.None;
            RecordCandidates(syntax, singleReason == CandidateReason.Inaccessible ? null : candidates[0], candidates, singleReason);
            return candidates[0];
        }
        var matches = candidates.Where(candidate =>
                (incomplete ? candidate.Parameters.Length >= suppliedCount : candidate.Parameters.Length == arguments.Length) &&
                (!readonlyReceiver || candidate.IsReadonly))
            .Select(candidate => (Method: candidate, Costs: candidate.Parameters.Take(suppliedCount).Zip(arguments.Take(suppliedCount))
                .Select(pair => GetArgumentConversionCost(pair.First.Type, pair.Second)).ToArray()))
            .Where(candidate => candidate.Costs.All(cost => cost.HasValue))
            .Select(candidate => (candidate.Method, Costs: candidate.Costs.Select(cost => cost!.Value).ToArray())).ToArray();
        FunctionSymbol[] best = matches.Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other.Method, candidate.Method) && IsBetterConversionSequence(other.Costs, candidate.Costs)))
            .Select(candidate => candidate.Method).ToArray();
        if (best.Length == 1)
        {
            RecordCandidates(syntax, best[0], candidates,
                incomplete ? CandidateReason.Incomplete : CandidateReason.None);
            return best[0];
        }
        if (incomplete && best.Length > 1)
        {
            RecordCandidates(syntax, null, candidates, CandidateReason.Incomplete);
            return null;
        }
        CandidateReason reason = best.Length == 0
            ? candidates.All(candidate => incomplete
                ? candidate.Parameters.Length < suppliedCount
                : candidate.Parameters.Length != arguments.Length)
                ? CandidateReason.WrongArity : CandidateReason.NotInvocable
            : CandidateReason.Ambiguous;
        RecordCandidates(syntax, null, candidates, reason);
        _diagnostics.Report(location, best.Length == 0
            ? $"no interface method '{type.Name}.{name}' matches the provided arguments"
            : $"interface method call '{type.Name}.{name}' is ambiguous",
            reason switch
            {
                CandidateReason.WrongArity => DiagnosticIds.WrongArity,
                CandidateReason.Ambiguous => DiagnosticIds.AmbiguousCall,
                _ => DiagnosticIds.NoMatchingCandidate,
            });
        return null;
    }

    private FunctionSymbol? ResolveConstructor(
        StructTypeSymbol type,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location) => ResolveConstructor(type, arguments, argumentSyntax, location, out _);

    private FunctionSymbol? ResolveConstructor(
        StructTypeSymbol type,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location,
        out CandidateReason reason,
        int? completedArgumentCount = null)
    {
        if (type.Constructors.IsEmpty)
        {
            reason = CandidateReason.NotFound;
            return null;
        }

        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        var matches = type.Constructors
            .Where(candidate => incomplete
                ? candidate.Parameters.Length >= suppliedCount
                : candidate.Parameters.Length == arguments.Length)
            .Select(candidate => new
            {
                Constructor = candidate,
                Costs = candidate.Parameters.Take(suppliedCount).Zip(arguments.Take(suppliedCount))
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
        {
            reason = incomplete ? CandidateReason.Incomplete : CandidateReason.None;
            return matches[0].Constructor;
        }
        if (matches.Length == 0)
        {
            reason = type.Constructors.Any(candidate => incomplete
                    ? candidate.Parameters.Length >= suppliedCount
                    : candidate.Parameters.Length == arguments.Length)
                ? CandidateReason.NotInvocable : CandidateReason.WrongArity;
            _diagnostics.Report(location, $"no constructor of struct '{type.Name}' matches the provided arguments",
                reason == CandidateReason.WrongArity ? DiagnosticIds.WrongArity : DiagnosticIds.NoMatchingCandidate);
            return null;
        }

        FunctionSymbol[] bestMatches = matches
            .Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other, candidate) &&
                IsBetterConversionSequence(other.Costs, candidate.Costs)))
            .Select(candidate => candidate.Constructor)
            .ToArray();
        if (bestMatches.Length == 1)
        {
            reason = incomplete ? CandidateReason.Incomplete : CandidateReason.None;
            return bestMatches[0];
        }

        if (incomplete)
        {
            reason = CandidateReason.Incomplete;
            return null;
        }

        reason = CandidateReason.Ambiguous;
        _diagnostics.Report(location, $"constructor call for struct '{type.Name}' is ambiguous",
            DiagnosticIds.AmbiguousCall);
        return null;
    }

    private IndexerSymbol? ResolveIndexer(
        IEnumerable<IndexerSymbol> candidates,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName,
        SyntaxNode syntax) =>
        ResolveIndexerCore(candidates, indexer => indexer.Parameters, arguments, location, ownerName, syntax);

    private InterfaceIndexerSymbol? ResolveIndexer(
        IEnumerable<InterfaceIndexerSymbol> candidates,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName,
        SyntaxNode syntax) =>
        ResolveIndexerCore(candidates, indexer => indexer.Parameters, arguments, location, ownerName, syntax);

    private TIndexer? ResolveIndexerCore<TIndexer>(
        IEnumerable<TIndexer> candidates,
        Func<TIndexer, ImmutableArray<ParameterSymbol>> getParameters,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName,
        SyntaxNode syntax)
        where TIndexer : Symbol
    {
        TIndexer[] candidateArray = candidates.ToArray();
        var matches = candidateArray
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
        {
            RecordCandidates(syntax, matches[0].Indexer, candidateArray, CandidateReason.None);
            return matches[0].Indexer;
        }
        if (matches.Length == 0)
        {
            CandidateReason reason = candidateArray.Length == 0 ? CandidateReason.NotFound
                : candidateArray.All(candidate => getParameters(candidate).Length != arguments.Length)
                    ? CandidateReason.WrongArity : CandidateReason.NotInvocable;
            RecordCandidates(syntax, null, candidateArray, reason);
            _diagnostics.Report(location, $"no indexer of type '{ownerName}' matches the provided arguments",
                reason == CandidateReason.WrongArity ? DiagnosticIds.WrongArity : DiagnosticIds.NoMatchingCandidate);
            return null;
        }

        TIndexer[] bestMatches = matches
            .Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other, candidate) &&
                IsBetterConversionSequence(other.Costs, candidate.Costs)))
            .Select(candidate => candidate.Indexer)
            .ToArray();
        if (bestMatches.Length == 1)
        {
            RecordCandidates(syntax, bestMatches[0], candidateArray, CandidateReason.None);
            return bestMatches[0];
        }

        RecordCandidates(syntax, null, candidateArray, CandidateReason.Ambiguous);
        _diagnostics.Report(location, $"indexer access on type '{ownerName}' is ambiguous",
            DiagnosticIds.AmbiguousCall);
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
            return TypeFacts.GetReferenceBindingCost(referenceType, @this.ContainingType);
        }
        if (!referenceType.IsReadonly && argument is BoundReferenceDereferenceExpression { ReferenceType.IsReadonly: true })
            return null;
        return TypeFacts.GetReferenceBindingCost(referenceType, argument.Type);
    }

    private static BoundExpression ContextualizeNull(BoundExpression expression, TypeSymbol targetType)
    {
        if (expression is BoundLiteralExpression { Value: null } &&
            TypeIdentity.AreSame(expression.Type, BuiltinTypes.Null) &&
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
                return TypeFacts.GetReferenceBindingCost(referenceType, @this.ContainingType) is not null
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
        ValidateDefaultInitialization(elementType, location);
        if (TypeIdentity.AreSame(elementType, BuiltinTypes.Void))
        {
            _diagnostics.Report(location, "array element type cannot be 'void'",
                DiagnosticIds.VoidArrayElementType);
        }

        if (elementType is StructTypeSymbol { IsAbstract: true } structType)
        {
            _diagnostics.Report(
                location,
                $"array element type '{structType.Name}' is abstract",
                DiagnosticIds.AbstractArrayElementType);
        }
    }

    private void ValidateDefaultInitialization(TypeSymbol type, TextLocation location)
    {
        if (type is ReferenceTypeSymbol)
            _diagnostics.Report(location, $"reference type '{type.Name}' cannot be default-initialized",
                DiagnosticIds.ReferenceCannotDefaultInitialize);
        if (type is StructTypeSymbol structure)
        {
            foreach (FieldSymbol field in structure.AllInstanceFields)
                if (field.Declaration.Initializer is null && TypeFacts.ContainsReferenceStorage(field.Type))
                    _diagnostics.Report(location, $"field '{field.Name}' contains a reference and requires explicit initialization",
                        DiagnosticIds.ReferenceFieldRequiresExplicitInitialization);
        }
    }

    private void ValidateRequiredFields(TextLocation location)
    {
        foreach (var (field, state) in _requiredFields)
            if (!_definitelyAssigned.Contains(state))
                _diagnostics.Report(location, $"field '{field.Name}' contains a reference and must be initialized before the object is used or its constructor exits",
                    DiagnosticIds.ReferenceFieldNotInitialized);
    }

    private BoundExpression BindFieldReceiver(ExpressionSyntax syntax)
    {
        ExpressionSyntax? previous = _fieldReceiverSyntax;
        _fieldReceiverSyntax = syntax;
        try { return BindExpression(syntax); }
        finally { _fieldReceiverSyntax = previous; }
    }

    private void ValidateArrayLength(BoundExpression length, ExpressionSyntax syntax)
    {
        if (TypeFacts.IsInteger(length.Type) && _constants.TryFold(length, out object? value) &&
            (SemanticAnalyzer.ToInteger(value) < 0 || SemanticAnalyzer.ToInteger(value) > int.MaxValue))
            _diagnostics.Report(GetLocation(syntax), "array length must be between zero and int.MaxValue",
                DiagnosticIds.ArrayLengthOutOfRange);
        if (!TypeFacts.IsInteger(length.Type) && !TypeIdentity.AreSame(length.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(GetLocation(syntax), $"array length must be an integer, but has type '{length.Type.ToDisplayString()}'",
                DiagnosticIds.ArrayLengthMustBeInteger);
        }

        if (length is BoundLiteralExpression { Value: int intValue } && intValue < 0)
        {
            _diagnostics.Report(GetLocation(syntax), "array length cannot be negative",
                DiagnosticIds.ArrayLengthOutOfRange);
        }
        else if (length is BoundLiteralExpression { Value: long longValue } && longValue < 0)
        {
            _diagnostics.Report(GetLocation(syntax), "array length cannot be negative",
                DiagnosticIds.ArrayLengthOutOfRange);
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
        _diagnostics.Report(location, "stack array cannot escape its allocation scope through this assignment",
            DiagnosticIds.StackArrayEscape);
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
        if (TypeIdentity.AreSame(left, BuiltinTypes.Error) || TypeIdentity.AreSame(right, BuiltinTypes.Error))
        {
            return BuiltinTypes.Error;
        }

        bool sameType = TypeIdentity.AreSame(left, right);
        if (operatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or
            SyntaxKind.SlashToken or SyntaxKind.PercentToken)
        {
            if (sameType && TypeFacts.IsNumeric(left))
            {
                return left;
            }

            if (left is PointerTypeSymbol lp && !TypeIdentity.AreSame(lp.ElementType, BuiltinTypes.Void) && TypeFacts.IsInteger(right) && operatorKind is SyntaxKind.PlusToken or SyntaxKind.MinusToken)
            {
                return left;
            }

            if (right is PointerTypeSymbol rp && !TypeIdentity.AreSame(rp.ElementType, BuiltinTypes.Void) && TypeFacts.IsInteger(left) && operatorKind == SyntaxKind.PlusToken)
            {
                return right;
            }

            if (left is PointerTypeSymbol leftPointer && right is PointerTypeSymbol rightPointer &&
                !TypeIdentity.AreSame(leftPointer.ElementType, BuiltinTypes.Void) &&
                TypeIdentity.AreSame(leftPointer.ElementType, rightPointer.ElementType) && operatorKind == SyntaxKind.MinusToken)
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
            if (TypeFacts.CanCompareEquality(left, right))
            {
                return BuiltinTypes.Bool;
            }
        }

        if (operatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken &&
            TypeIdentity.AreSame(left, BuiltinTypes.Bool) && TypeIdentity.AreSame(right, BuiltinTypes.Bool))
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
        _diagnostics.Report(location, $"cannot implicitly convert '{source.ToDisplayString()}' to '{destination.ToDisplayString()}'",
            DiagnosticIds.TypeMismatch);

    private static TextLocation GetLocation(ExpressionSyntax syntax) => syntax switch
    {
        MissingExpressionSyntax missing => missing.MissingToken.Location,
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

    private void RecordScope(BlockStatementSyntax syntax, BoundScope scope)
    {
        int start = syntax.OpenBraceToken.Location.Span.Start;
        int end = syntax.CloseBraceToken.Location.Span.End;
        if (end < start) end = start;
        _semanticInfo.Scopes.Add(new PositionScope(
            syntax.OpenBraceToken.Location.Source,
            TextSpan.FromBounds(start, end),
            _function,
            scope.Variables.ToArray(),
            syntax.CloseBraceToken.IsMissing));
    }

    private void RecordScope(SourceText source, TextSpan span, BoundScope scope, bool includeEnd = false) =>
        _semanticInfo.Scopes.Add(new PositionScope(source, span, _function, scope.Variables.ToArray(), includeEnd));

    private static (int End, bool IncludeEnd) GetStatementEnd(StatementSyntax syntax) => syntax switch
    {
        BlockStatementSyntax block => (block.CloseBraceToken.Location.Span.End, block.CloseBraceToken.IsMissing),
        VariableDeclarationStatementSyntax variable => (variable.SemicolonToken.Location.Span.End, variable.SemicolonToken.IsMissing),
        ReturnStatementSyntax @return => (@return.SemicolonToken.Location.Span.End, @return.SemicolonToken.IsMissing),
        ExpressionStatementSyntax expression => (expression.SemicolonToken.Location.Span.End, expression.SemicolonToken.IsMissing),
        IfStatementSyntax @if when @if.ElseStatement is not null => GetStatementEnd(@if.ElseStatement),
        IfStatementSyntax @if => GetStatementEnd(@if.ThenStatement),
        WhileStatementSyntax @while => GetStatementEnd(@while.Body),
        ForStatementSyntax @for => GetStatementEnd(@for.Body),
        BreakStatementSyntax @break => (@break.SemicolonToken.Location.Span.End, @break.SemicolonToken.IsMissing),
        ContinueStatementSyntax @continue => (@continue.SemicolonToken.Location.Span.End, @continue.SemicolonToken.IsMissing),
        SwitchStatementSyntax @switch when @switch.CloseBraceToken is { } close => (close.Location.Span.End, close.IsMissing),
        _ => throw new InvalidOperationException($"Unexpected statement syntax '{syntax.Kind}'."),
    };

    private void SetConvertedType(ExpressionSyntax syntax, TypeSymbol convertedType)
    {
        TypeInfo current = _semanticInfo.Types.GetValueOrDefault(syntax, new TypeInfo(convertedType, convertedType));
        _semanticInfo.Types[syntax] = current with { ConvertedType = convertedType };
    }

    private void RecordSymbolAndType(SyntaxNode syntax, Symbol symbol, TypeSymbol type)
    {
        SetSelectedSymbolPreservingCandidates(syntax, symbol);
        _semanticInfo.Types[syntax] = new TypeInfo(type, type);
    }

    private void RecordCandidates(SyntaxNode syntax, Symbol? selected, IEnumerable<Symbol> candidates, CandidateReason reason)
    {
        ImmutableArray<Symbol> candidateArray = candidates.Distinct().ToImmutableArray();
        _semanticInfo.Symbols[syntax] = new SymbolInfo(selected, candidateArray, reason);
    }

    private void SetSelectedSymbolPreservingCandidates(SyntaxNode syntax, Symbol selected)
    {
        if (_semanticInfo.Symbols.TryGetValue(syntax, out SymbolInfo existing) && !existing.CandidateSymbols.IsEmpty)
            _semanticInfo.Symbols[syntax] = existing with { Symbol = selected };
        else
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(selected);
    }

    private void RecordStaticReceiver(ExpressionSyntax syntax, TypeSymbol type)
    {
        _semanticInfo.Types[syntax] = new TypeInfo(type, type);
        _semanticInfo.Receivers[syntax] = new ReceiverInfo(type, IsStatic: true, IsReadonly: true, IsWritable: false);
        _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(type);
    }

    private static Symbol? GetReferencedSymbol(BoundExpression expression) => expression switch
    {
        BoundVariableExpression variable => variable.Variable,
        BoundMemberAccessExpression member => member.Field,
        BoundStaticFieldExpression field => field.Field,
        BoundCallExpression call => call.Function,
        BoundMethodCallExpression call => call.Method,
        BoundInterfaceMethodCallExpression call => call.Method,
        BoundConstructorCallExpression call => call.Constructor,
        BoundPropertySetExpression property => property.Property,
        BoundInterfacePropertySetExpression property => property.Property,
        BoundIndexerSetExpression indexer => indexer.Indexer,
        BoundInterfaceIndexerSetExpression indexer => indexer.Indexer,
        _ => null,
    };

    private BoundExpression? TryBindGenericPropertyAssignment(AssignmentExpressionSyntax syntax,
        MemberAccessExpressionSyntax target, BoundExpression receiver, GenericParameterSymbol parameter,
        bool pointerAccess, bool isSimpleAssignment)
    {
        GenericFieldMember? field = GenericConstraintMemberLookup.GetFields(parameter, target.MemberToken.Text)
            .FirstOrDefault(candidate => !candidate.IsStatic);
        if (field is not null)
        {
            if (field.IsReadonly || IsReadonlyReceiver(receiver, pointerAccess))
            {
                _diagnostics.Report(target.MemberToken.Location,
                    $"field '{target.MemberToken.Text}' cannot be assigned through this receiver",
                    DiagnosticIds.WriteThroughReadonlyReceiver);
                return new BoundErrorExpression();
            }
            BoundExpression fieldValue = BindExpression(syntax.Expression);
            if (isSimpleAssignment)
                _ = ValidateGenericArguments(field.Symbol.Name, [field.Type], [fieldValue], [syntax.Expression],
                    target.MemberToken.Location);
            else
            {
                SyntaxKind binaryOperator = GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind);
                ValidateIntegerOperation(new BoundDeferredConstantExpression(field.Type), binaryOperator, fieldValue,
                    syntax.OperatorToken.Location);
            }
            RecordSymbolAndType(target, field.Symbol, field.Type);
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(field.Symbol);
            return new BoundDeferredConstantExpression(field.Type);
        }

        GenericPropertyMember[] candidates = GenericConstraintMemberLookup
            .GetProperties(parameter, target.MemberToken.Text, _fileScope.TypeFactory,
                _fileScope.GenericStructSpecializer)
            .Where(property => !property.IsStatic)
            .DistinctBy(property => (property.Type.ToDisplayString(TypeDisplayFormat.FullyQualified),
                property.HasGetter, property.HasSetter, property.IsReadonly))
            .ToArray();
        if (candidates.Length == 0) return null;
        GenericPropertyMember? property = candidates.FirstOrDefault(property =>
            property.HasSetter && (isSimpleAssignment || property.HasGetter));
        if (property is null)
        {
            RecordCandidates(target, null, candidates.Select(candidate => candidate.Symbol), CandidateReason.Inaccessible);
            _diagnostics.Report(target.MemberToken.Location,
                $"property '{target.MemberToken.Text}' is not guaranteed to provide the required accessor",
                DiagnosticIds.MissingAccessor);
            return new BoundErrorExpression();
        }
        if (IsReadonlyReceiver(receiver, pointerAccess))
        {
            _diagnostics.Report(target.MemberToken.Location,
                $"property '{target.MemberToken.Text}' cannot be assigned through a readonly receiver",
                DiagnosticIds.WriteThroughReadonlyReceiver);
            return new BoundErrorExpression();
        }
        BoundExpression value = BindExpression(syntax.Expression);
        if (isSimpleAssignment)
        {
            _ = ValidateGenericArguments(property.Symbol.Name, [property.Type], [value], [syntax.Expression],
                target.MemberToken.Location);
        }
        else
        {
            SyntaxKind binaryOperator = GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind);
            ValidateIntegerOperation(new BoundDeferredConstantExpression(property.Type), binaryOperator, value,
                syntax.OperatorToken.Location);
            if (!TypeIdentity.AreSame(GetBinaryResultType(property.Type, binaryOperator, value.Type), property.Type))
                _diagnostics.Report(syntax.OperatorToken.Location,
                    $"operator '{syntax.OperatorToken.Text}' is not defined for types '{property.Type.ToDisplayString()}' and '{value.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidOperatorOperands);
        }
        RecordSymbolAndType(target, property.Symbol, property.Type);
        _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(property.Symbol);
        return new BoundDeferredConstantExpression(property.Type);
    }

    private BoundExpression BindGenericIndexerAssignment(AssignmentExpressionSyntax syntax,
        IndexExpressionSyntax target, BoundExpression receiver, GenericParameterSymbol parameter,
        bool isSimpleAssignment)
    {
        ImmutableArray<BoundExpression> indices = target.Arguments.Select(BindExpression).ToImmutableArray();
        GenericIndexerMember[] candidates = GenericConstraintMemberLookup.GetIndexers(parameter,
                _fileScope.TypeFactory, _fileScope.GenericStructSpecializer)
            .Where(indexer => indexer.HasSetter && (isSimpleAssignment || indexer.HasGetter))
            .ToArray();
        GenericIndexerMember? indexer = ResolveGenericCandidate(candidates, candidate => candidate.Symbol,
            candidate => candidate.ParameterTypes, indices, target.OpenBracketToken.Location,
            $"writable indexer on '{parameter.Name}'", target);
        if (indexer is null)
        {
            if (candidates.Length == 0)
                _diagnostics.Report(target.OpenBracketToken.Location,
                    $"constraints for '{parameter.Name}' do not guarantee a writable indexer",
                    DiagnosticIds.GenericMemberNotGuaranteed);
            return new BoundErrorExpression();
        }
        if (!IsAddressable(receiver) || !IsWritable(receiver))
        {
            _diagnostics.Report(target.OpenBracketToken.Location,
                "indexer cannot be assigned through a readonly receiver",
                DiagnosticIds.WriteThroughReadonlyReceiver);
            return new BoundErrorExpression();
        }
        _ = ValidateGenericArguments("this", indexer.ParameterTypes, indices, target.Arguments,
            target.OpenBracketToken.Location);
        BoundExpression value = BindExpression(syntax.Expression);
        if (isSimpleAssignment)
            _ = ValidateGenericArguments("this", [indexer.Type], [value], [syntax.Expression],
                target.OpenBracketToken.Location);
        else
        {
            SyntaxKind binaryOperator = GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind);
            ValidateIntegerOperation(new BoundDeferredConstantExpression(indexer.Type), binaryOperator, value,
                syntax.OperatorToken.Location);
            if (!TypeIdentity.AreSame(GetBinaryResultType(indexer.Type, binaryOperator, value.Type), indexer.Type))
                _diagnostics.Report(syntax.OperatorToken.Location,
                    $"operator '{syntax.OperatorToken.Text}' is not defined for types '{indexer.Type.ToDisplayString()}' and '{value.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidOperatorOperands);
        }
        RecordSymbolAndType(target, indexer.Symbol, indexer.Type);
        _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(indexer.Symbol);
        return new BoundDeferredConstantExpression(indexer.Type);
    }

    private BoundExpression BindGenericMemberGet(MemberAccessExpressionSyntax syntax, BoundExpression receiver,
        GenericParameterSymbol parameter, bool pointerAccess)
    {
        GenericFieldMember? field = GenericConstraintMemberLookup.GetFields(parameter, syntax.MemberToken.Text)
            .FirstOrDefault(candidate => !candidate.IsStatic);
        if (field is not null)
        {
            RecordSymbolAndType(syntax, field.Symbol, field.Type);
            return new BoundDeferredConstantExpression(field.Type);
        }
        return BindGenericPropertyGet(syntax, receiver, parameter, pointerAccess);
    }

    private BoundExpression BindGenericPropertyGet(MemberAccessExpressionSyntax syntax, BoundExpression receiver,
        GenericParameterSymbol parameter, bool pointerAccess)
    {
        GenericPropertyMember[] named = GenericConstraintMemberLookup
            .GetProperties(parameter, syntax.MemberToken.Text, _fileScope.TypeFactory,
                _fileScope.GenericStructSpecializer)
            .Where(property => !property.IsStatic)
            .DistinctBy(property => (property.Type.ToDisplayString(TypeDisplayFormat.FullyQualified), property.HasGetter,
                property.HasSetter, property.IsReadonly))
            .ToArray();
        GenericPropertyMember? property = named.FirstOrDefault(property => property.HasGetter);
        if (property is null)
        {
            RecordCandidates(syntax, null, named.Select(candidate => candidate.Symbol), CandidateReason.NotFound);
            _diagnostics.Report(syntax.MemberToken.Location,
                $"constraints for '{parameter.Name}' do not guarantee readable property '{syntax.MemberToken.Text}'",
                DiagnosticIds.GenericMemberNotGuaranteed);
            return new BoundErrorExpression();
        }
        if (IsReadonlyReceiver(receiver, pointerAccess) && !property.IsReadonly)
        {
            RecordCandidates(syntax, null, named.Select(candidate => candidate.Symbol), CandidateReason.Inaccessible);
            _diagnostics.Report(syntax.MemberToken.Location,
                $"property '{property.Symbol.Name}' is not guaranteed readonly for '{parameter.Name}'",
                DiagnosticIds.MutableGetterOnReadonlyReceiver);
            return new BoundErrorExpression();
        }
        RecordSymbolAndType(syntax, property.Symbol, property.Type);
        return new BoundDeferredConstantExpression(property.Type);
    }

    private BoundExpression BindGenericMethodCall(MemberAccessExpressionSyntax target, BoundExpression receiver,
        GenericParameterSymbol parameter, ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax, bool pointerAccess, bool incomplete,
        int completedArgumentCount)
    {
        GenericMethodMember[] candidates = GenericConstraintMemberLookup
            .GetMethods(parameter, target.MemberToken.Text, _fileScope.TypeFactory,
                _fileScope.GenericStructSpecializer)
            .Where(method => !method.IsStatic)
            .DistinctBy(method => $"{method.ReturnType.ToDisplayString(TypeDisplayFormat.FullyQualified)}({string.Join(",", method.ParameterTypes.Select(type => type.ToDisplayString(TypeDisplayFormat.FullyQualified)))})/{method.IsReadonly}")
            .ToArray();
        GenericMethodMember? method = ResolveGenericCandidate(candidates, candidate => candidate.Symbol,
            candidate => candidate.ParameterTypes, arguments, target.MemberToken.Location,
            $"method '{target.MemberToken.Text}' on '{parameter.Name}'", target,
            incomplete ? completedArgumentCount : null);
        if (method is null)
        {
            if (candidates.Length == 0)
                _diagnostics.Report(target.MemberToken.Location,
                    $"constraints for '{parameter.Name}' do not guarantee method '{target.MemberToken.Text}'",
                    DiagnosticIds.GenericMemberNotGuaranteed);
            return new BoundErrorExpression();
        }
        if (IsReadonlyReceiver(receiver, pointerAccess) && !method.IsReadonly)
        {
            RecordCandidates(target, null, candidates.Select(candidate => candidate.Symbol), CandidateReason.Inaccessible);
            _diagnostics.Report(target.MemberToken.Location,
                $"method '{method.Symbol.Name}' is not guaranteed readonly for '{parameter.Name}'",
                DiagnosticIds.MutableMethodOnReadonlyReceiver);
            return new BoundErrorExpression();
        }
        _ = ValidateGenericArguments(method.Symbol.Name, method.ParameterTypes, arguments, argumentSyntax,
            target.MemberToken.Location, incomplete ? completedArgumentCount : null);
        RecordSymbolAndType(target, method.Symbol, method.ReturnType);
        return new BoundDeferredConstantExpression(method.ReturnType);
    }

    private BoundExpression BindGenericIndexerGet(IndexExpressionSyntax syntax, BoundExpression receiver,
        GenericParameterSymbol parameter, ImmutableArray<BoundExpression> arguments)
    {
        bool receiverIsReadonly = IsReadonlyReceiver(receiver, pointerAccess: false);
        GenericIndexerMember[] candidates = GenericConstraintMemberLookup.GetIndexers(parameter,
                _fileScope.TypeFactory, _fileScope.GenericStructSpecializer)
            .Where(indexer => indexer.HasGetter && (!receiverIsReadonly || indexer.IsReadonly))
            .DistinctBy(indexer => $"{indexer.Type.ToDisplayString(TypeDisplayFormat.FullyQualified)}({string.Join(",", indexer.ParameterTypes.Select(type => type.ToDisplayString(TypeDisplayFormat.FullyQualified)))})")
            .ToArray();
        GenericIndexerMember? indexer = ResolveGenericCandidate(candidates, candidate => candidate.Symbol,
            candidate => candidate.ParameterTypes, arguments, syntax.OpenBracketToken.Location,
            $"indexer on '{parameter.Name}'", syntax);
        if (indexer is null)
        {
            if (candidates.Length == 0)
                _diagnostics.Report(syntax.OpenBracketToken.Location,
                    $"constraints for '{parameter.Name}' do not guarantee a readable indexer",
                    DiagnosticIds.GenericMemberNotGuaranteed);
            return new BoundErrorExpression();
        }
        _ = ValidateGenericArguments("this", indexer.ParameterTypes, arguments, syntax.Arguments,
            syntax.OpenBracketToken.Location);
        RecordSymbolAndType(syntax, indexer.Symbol, indexer.Type);
        return new BoundDeferredConstantExpression(indexer.Type);
    }

    private BoundExpression BindGenericNewExpression(NewExpressionSyntax syntax, GenericParameterSymbol parameter)
    {
        ImmutableArray<BoundExpression> arguments = syntax.Arguments.Select(BindExpression).ToImmutableArray();
        GenericConstructorMember[] candidates = GenericConstraintMemberLookup
            .GetConstructors(parameter, _fileScope.TypeFactory, _fileScope.GenericStructSpecializer)
            .DistinctBy(constructor => string.Join(",", constructor.ParameterTypes.Select(type => type.ToDisplayString(TypeDisplayFormat.FullyQualified))))
            .ToArray();
        GenericConstructorMember? constructor = syntax.IsPositionalInitialization ? null :
            ResolveGenericCandidate(candidates, candidate => candidate.Symbol, candidate => candidate.ParameterTypes,
                arguments, syntax.NewKeyword.Location, $"constructor for '{parameter.Name}'", syntax,
                syntax.CloseDelimiterToken.IsMissing ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        if (constructor is null)
        {
            if (syntax.IsPositionalInitialization || candidates.Length == 0)
                _diagnostics.Report(syntax.NewKeyword.Location,
                    $"constraints for '{parameter.Name}' do not guarantee this construction",
                    DiagnosticIds.GenericConstructorNotGuaranteed);
            return new BoundErrorExpression();
        }
        _ = ValidateGenericArguments(parameter.Name, constructor.ParameterTypes, arguments, syntax.Arguments,
            syntax.NewKeyword.Location,
            syntax.CloseDelimiterToken.IsMissing ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        RecordSymbolAndType(syntax, constructor.Symbol, _fileScope.TypeFactory.PointerTo(parameter));
        return new BoundDeferredConstantExpression(_fileScope.TypeFactory.PointerTo(parameter));
    }

    private T? ResolveGenericCandidate<T>(IEnumerable<T> source, Func<T, Symbol> getSymbol,
        Func<T, ImmutableArray<TypeSymbol>> getParameterTypes, ImmutableArray<BoundExpression> arguments,
        TextLocation location, string description, SyntaxNode syntax, int? completedArgumentCount = null)
        where T : class
    {
        T[] candidates = source.ToArray();
        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        var matches = candidates.Where(candidate =>
                (incomplete ? getParameterTypes(candidate).Length >= suppliedCount :
                    getParameterTypes(candidate).Length == suppliedCount))
            .Select(candidate => new
            {
                Candidate = candidate,
                Costs = getParameterTypes(candidate).Take(suppliedCount).Zip(arguments.Take(suppliedCount))
                    .Select(pair => GetArgumentConversionCost(pair.First, pair.Second)).ToArray(),
            })
            .Where(candidate => candidate.Costs.All(cost => cost.HasValue))
            .Select(candidate => new
            {
                candidate.Candidate,
                Costs = candidate.Costs.Select(cost => cost!.Value).ToArray(),
            }).ToArray();
        if (matches.Length == 1)
        {
            RecordCandidates(syntax, getSymbol(matches[0].Candidate), candidates.Select(getSymbol),
                incomplete ? CandidateReason.Incomplete : CandidateReason.None);
            return matches[0].Candidate;
        }
        CandidateReason reason = candidates.Length == 0 ? CandidateReason.NotFound
            : matches.Length > 1 ? CandidateReason.Ambiguous
            : candidates.All(candidate => getParameterTypes(candidate).Length != suppliedCount)
                ? CandidateReason.WrongArity : CandidateReason.NotInvocable;
        RecordCandidates(syntax, null, candidates.Select(getSymbol), reason);
        if (candidates.Length != 0 && !incomplete)
            _diagnostics.Report(location, matches.Length > 1 ? $"{description} is ambiguous" :
                $"no {description} matches the provided arguments",
                matches.Length > 1 ? DiagnosticIds.AmbiguousCall :
                reason == CandidateReason.WrongArity ? DiagnosticIds.WrongArity : DiagnosticIds.NoMatchingCandidate);
        return null;
    }

    private ImmutableArray<BoundExpression> ValidateGenericArguments(string name,
        ImmutableArray<TypeSymbol> parameterTypes, ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax, TextLocation location,
        int? completedArgumentCount = null)
    {
        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        if ((!incomplete && suppliedCount != parameterTypes.Length) ||
            incomplete && suppliedCount > parameterTypes.Length)
            _diagnostics.Report(location,
                $"'{name}' expects {parameterTypes.Length} argument(s), but {suppliedCount} were provided",
                DiagnosticIds.WrongArity);
        var converted = arguments.ToBuilder();
        for (int index = 0; index < Math.Min(suppliedCount, parameterTypes.Length); index++)
        {
            BoundExpression argument = ContextualizeConversion(arguments[index], parameterTypes[index]);
            converted[index] = argument;
            SetConvertedType(argumentSyntax[index], argument.Type);
            if (!TypeFacts.CanAssign(parameterTypes[index], argument.Type))
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, parameterTypes[index]);
        }
        return converted.ToImmutable();
    }

    private static GenericParameterSymbol? GetGenericReceiver(TypeSymbol type, bool pointerAccess) => type switch
    {
        GenericParameterSymbol parameter when !pointerAccess => parameter,
        PointerTypeSymbol { ElementType: GenericParameterSymbol parameter } when pointerAccess => parameter,
        _ => null,
    };

    private TypeSymbol SubstituteGenericType(TypeSymbol type,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions)
    {
        if (_fileScope.GenericStructSpecializer is not null)
            return _fileScope.GenericStructSpecializer.Substitute(type, substitutions);
        return type;
    }

    private bool TryInferGenericTypeArguments(FunctionSymbol definition,
        ImmutableArray<BoundExpression> arguments, out ImmutableArray<TypeSymbol> typeArguments)
    {
        var inferred = new Dictionary<GenericParameterSymbol, TypeSymbol>();
        if (arguments.Length != definition.Parameters.Length)
        {
            typeArguments = [];
            return false;
        }
        for (int index = 0; index < arguments.Length; index++)
            if (!TryInferGenericType(definition.Parameters[index].Type, arguments[index].Type, inferred))
            {
                typeArguments = [];
                return false;
            }
        if (definition.TypeParameters.Any(parameter => !inferred.ContainsKey(parameter)))
        {
            typeArguments = [];
            return false;
        }
        typeArguments = definition.TypeParameters.Select(parameter => inferred[parameter]).ToImmutableArray();
        return true;
    }

    private bool TryInferGenericType(TypeSymbol pattern, TypeSymbol actual,
        IDictionary<GenericParameterSymbol, TypeSymbol> inferred)
    {
        if (pattern is GenericParameterSymbol parameter)
        {
            actual = _fileScope.TypeFactory.Intern(actual);
            if (!inferred.TryGetValue(parameter, out TypeSymbol? previous))
            {
                inferred.Add(parameter, actual);
                return true;
            }
            return TypeIdentity.AreSame(previous, actual);
        }
        return (pattern, actual) switch
        {
            (PointerTypeSymbol left, PointerTypeSymbol right) when left.IsReadonly == right.IsReadonly =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (ReferenceTypeSymbol left, ReferenceTypeSymbol right) when left.IsReadonly == right.IsReadonly =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (ArrayTypeSymbol left, ArrayTypeSymbol right) when left.Rank == right.Rank =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (StructTypeSymbol { GenericDefinition: not null } left,
                StructTypeSymbol { GenericDefinition: not null } right)
                when ReferenceEquals(left.GenericDefinition, right.GenericDefinition) &&
                     left.TypeArguments.Length == right.TypeArguments.Length =>
                left.TypeArguments.Zip(right.TypeArguments).All(pair =>
                    TryInferGenericType(pair.First, pair.Second, inferred)),
            _ => TypeIdentity.AreSame(pattern, actual),
        };
    }

    private static bool ContainsGenericParameter(TypeSymbol type) =>
        GenericTypeFacts.ContainsGenericParameter(type);

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
        TypeIdentity.AreSame(_function.ContainingType, member.Field.ContainingType) &&
        member.Receiver is BoundThisExpression;

    private static bool AlwaysReturns(BoundStatement statement) => BoundControlFlow.AlwaysReturns(statement);
}
