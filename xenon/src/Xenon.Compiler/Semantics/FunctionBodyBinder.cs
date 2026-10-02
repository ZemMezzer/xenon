using System.Collections.Immutable;
using System.Numerics;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed partial class FunctionBodyBinder
{
    private readonly FunctionSymbol _function;
    private readonly FileSymbolScope _fileScope;
    private readonly DiagnosticBag _diagnostics;
    private readonly ConstantEvaluationContext _constants;
    private readonly SemanticInfoStore _semanticInfo;
    private readonly CancellationToken _cancellationToken;
    private readonly GenericFunctionSpecializer? _genericSpecializer;
    private readonly Dictionary<BoundExpression, TextLocation> _expressionLocations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BoundExpression, ExpressionSyntax> _expressionSyntax = new(ReferenceEqualityComparer.Instance);
    internal IReadOnlyDictionary<BoundExpression, TextLocation> ExpressionLocations => _expressionLocations;
    private readonly HashSet<FieldSymbol> _boundReferenceFields = [];
    private BoundScope _scope = new(null);
    private int _loopDepth;
    private int _repeatedEvaluationDepth;
    private int _switchDepth;
    private int _catchDepth;
    private readonly Stack<(int LoopDepth, List<HashSet<FieldSymbol>> Exits)> _switchExits = [];
    private bool _bindingBaseConstructorArguments;
    private bool _suppressIntegerOperationDiagnostics;

    private readonly HashSet<FieldSymbol> _constructorReferenceFields = [];
    private ExpressionSyntax? _initializationTarget;
    private ExpressionSyntax? _fieldReceiverSyntax;
    private ExpressionSyntax? _unconsumedOwnershipExpression;
    private FunctionPointerTypeSymbol? _expectedFunctionPointerType;
    private FunctionValueTypeSymbol? _expectedFunctionValueType;
    private int _memberAccessBindingDepth;
    private sealed class MovePlace(
        object root,
        TypeSymbol rootType,
        string rootName,
        ImmutableArray<FieldSymbol> fields) : IEquatable<MovePlace>
    {
        public MovePlace(VariableSymbol root, ImmutableArray<FieldSymbol> fields)
            : this(root, root.Type, root.Name, fields) { }

        public object Root { get; } = root;
        public VariableSymbol? RootVariable => Root as VariableSymbol;
        public TypeSymbol RootType { get; } = rootType;
        public string RootName { get; } = rootName;
        public ImmutableArray<FieldSymbol> Fields { get; } = fields;
        public string DisplayName => Fields.IsEmpty
            ? RootName
            : $"{RootName}.{string.Join('.', Fields.Select(projectedField => projectedField.Name))}";

        public bool Equals(MovePlace? other) =>
            other is not null &&
            ReferenceEquals(Root, other.Root) &&
            Fields.Length == other.Fields.Length &&
            Fields.SequenceEqual(other.Fields);

        public override bool Equals(object? obj) => obj is MovePlace other && Equals(other);

        public override int GetHashCode()
        {
            HashCode hash = new();
            hash.Add(Root);
            foreach (FieldSymbol field in Fields) hash.Add(field);
            return hash.ToHashCode();
        }
    }

    // Contextual typing of constructor reference fields: the first assignment binds
    // T&, subsequent assignments target T. This context never diagnoses ownership,
    // assignment, borrows, effects or cleanup; all such decisions are MIR analyses.
    private sealed record ReferenceBindingState(
        HashSet<FieldSymbol> Assigned);
    private readonly Dictionary<BoundExpression, (ReferenceBindingState? True, ReferenceBindingState? False)> _booleanReferenceBindings = new(ReferenceEqualityComparer.Instance);
    private sealed record ExceptionalReferenceBinding(ReferenceBindingState State, TypeSymbol? Type);
    private readonly Stack<List<ExceptionalReferenceBinding>> _tryExceptionalReferenceBindings = [];
    private readonly Stack<ImmutableArray<TypeSymbol?>> _caughtExceptionTypes = [];
    private readonly Stack<ArgumentBindingTransaction> _argumentBindingTransactions = [];
    private readonly Dictionary<BoundExpression, ArgumentBindingTransaction> _argumentBindingCandidates =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LambdaExpressionSyntax, ArgumentBindingTransaction>
        _deferredLambdaCaptureTransactions = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<ReferenceFinalizerBindingContext> _referenceFinalizerBindings = [];
    private sealed record ReferenceFinalizerBindingContext(
        int LoopDepth,
        int SwitchDepth,
        List<ReferenceBindingState> Exits);
    private sealed class ArgumentBindingTransaction
    {
        private sealed record DeferredLambdaCapture(
            LambdaExpressionSyntax Syntax,
            ImmutableArray<MovePlace> Moves,
            Action RollbackAuxiliaryState)
        {
            public Dictionary<MovePlace, TextLocation?> SupersedingMutations { get; } = [];
            public HashSet<MovePlace> DeferredDependentMutations { get; } = [];
        }
        private readonly Dictionary<BoundExpression, MovePlace> _candidatePlaces =
            new(ReferenceEqualityComparer.Instance);
        private readonly List<DeferredLambdaCapture> _deferredLambdaCaptures = [];
        private readonly HashSet<LambdaExpressionSyntax> _materializedLambdas =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<LambdaExpressionSyntax, (Action Commit, Action Rollback)> _lambdaArtifacts =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<LambdaExpressionSyntax> _rolledBackLambdaArtifacts =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<LambdaExpressionSyntax> _rolledBackLambdaState =
            new(ReferenceEqualityComparer.Instance);

        private readonly List<Action> _generatedArtifactRollbacks = [];
        private int _rolledBackGeneratedArtifactCount;
        private TypeFactory.Snapshot? _typeFactorySnapshot;
        private GenericStructSpecializer? _transactionalStructSpecializer;
        private GenericFunctionSpecializer? _transactionalFunctionSpecializer;
        private GenericStructSpecializer? _parentStructSpecializer;
        private GenericFunctionSpecializer? _parentFunctionSpecializer;
        private bool _transactionalSpecializersInitialized;
        private bool _generatedArtifactsCommitted;
        private bool _preserveDeferredDependentMoves;
        public void RecordCandidate(BoundExpression expression, MovePlace place) => _candidatePlaces.Add(expression, place);

        public bool IsRejectedRecoveryMove(BoundExpression expression) =>
            !_preserveDeferredDependentMoves &&
            _candidatePlaces.TryGetValue(UnwrapDirectTransferredExpression(expression), out MovePlace? place) &&
            _deferredLambdaCaptures.Any(capture => _rolledBackLambdaState.Contains(capture.Syntax) &&
                capture.DeferredDependentMutations.Contains(place));

        public void RecordDeferredLambdaCapture(LambdaExpressionSyntax syntax,
            ImmutableArray<MovePlace> moves, Action rollbackAuxiliaryState) =>
            _deferredLambdaCaptures.Add(new(syntax, moves, rollbackAuxiliaryState));
        public (GenericStructSpecializer? Struct, GenericFunctionSpecializer? Function)
            GetTransactionalSpecializers(
                GenericStructSpecializer? structSpecializer,
                GenericFunctionSpecializer? functionSpecializer,
                DiagnosticBag diagnostics)
        {
            if (!_transactionalSpecializersInitialized)
            {
                _parentStructSpecializer = structSpecializer;
                _parentFunctionSpecializer = functionSpecializer;
                _transactionalStructSpecializer = structSpecializer?.CreateSpeculative(diagnostics);
                _transactionalFunctionSpecializer = functionSpecializer?.CreateSpeculative(
                    diagnostics, _transactionalStructSpecializer);
                _transactionalSpecializersInitialized = true;
            }
            return (_transactionalStructSpecializer,_transactionalFunctionSpecializer);
        }

        public void RecordSubsequentPlaceMutation(
            MovePlace place,
            TextLocation? moveLocation,
            bool deferredDependent)
        {
            foreach (DeferredLambdaCapture capture in _deferredLambdaCaptures)
            {
                if (_rolledBackLambdaState.Contains(capture.Syntax) ||
                    !capture.Moves
                        .Any(moved => PlacesOverlap(moved, place)))
                    continue;
                capture.SupersedingMutations.Remove(place);
                capture.SupersedingMutations[place] = moveLocation;
                if (deferredDependent) capture.DeferredDependentMutations.Add(place);
                else capture.DeferredDependentMutations.Remove(place);
            }
        }

        public void RecordMaterializedLambda(
            LambdaExpressionSyntax syntax,
            Action commitArtifacts,
            Action rollbackArtifacts) =>
            _lambdaArtifacts.TryAdd(syntax, (commitArtifacts,rollbackArtifacts));

        public void RecordGeneratedArtifactRollback(Action rollback) =>
            _generatedArtifactRollbacks.Add(rollback);

        public void EnsureTypeFactorySnapshot(TypeFactory typeFactory)
        {
            if (_typeFactorySnapshot is not null) return;
            _typeFactorySnapshot = typeFactory.CaptureSnapshot();
            RecordGeneratedArtifactRollback(() => typeFactory.Rollback(_typeFactorySnapshot));
        }

        public void CommitDeferredLambdaCaptures()
        {
            if (!_generatedArtifactsCommitted)
            {
                if (_transactionalStructSpecializer is not null)
                    _parentStructSpecializer?.MergeFrom(_transactionalStructSpecializer);
                if (_transactionalFunctionSpecializer is not null)
                    _parentFunctionSpecializer?.MergeFrom(_transactionalFunctionSpecializer);
                _generatedArtifactsCommitted = true;
            }
            foreach (var entry in _lambdaArtifacts)
                if (_materializedLambdas.Add(entry.Key))
                    entry.Value.Commit();
        }

        public void PreserveDeferredDependentMoves() =>
            _preserveDeferredDependentMoves = true;

        public bool HasDeferredMoveContribution(MovePlace place) =>
            _deferredLambdaCaptures.Any(capture =>
                !_materializedLambdas.Contains(capture.Syntax) &&
                !_rolledBackLambdaState.Contains(capture.Syntax) &&
                capture.Moves.Any(moved => PlacesOverlap(moved, place)) &&
                !capture.SupersedingMutations.Any(entry =>
                    entry.Value is null && PlacesOverlap(entry.Key, place)));

        public bool HasRolledBackLambdaCaptures => _rolledBackLambdaState.Count != 0;

        public void RollbackUnmaterializedLambdaCaptures()
        {
            for (int index = _deferredLambdaCaptures.Count - 1; index >= 0; index--)
            {
                var capture = _deferredLambdaCaptures[index];
                if (_materializedLambdas.Contains(capture.Syntax)) continue;
                if (_rolledBackLambdaState.Add(capture.Syntax))
                {
                    capture.RollbackAuxiliaryState();
                }
                if (_lambdaArtifacts.TryGetValue(capture.Syntax, out var artifacts) &&
                    _rolledBackLambdaArtifacts.Add(capture.Syntax))
                    artifacts.Rollback();
            }
            while (_rolledBackGeneratedArtifactCount < _generatedArtifactRollbacks.Count)
                _generatedArtifactRollbacks[_rolledBackGeneratedArtifactCount++]();

        }

    }

    private ReferenceBindingState CaptureReferenceBindingState() => new(CloneReferenceFieldBindings());
    private void RestoreReferenceBindingState(ReferenceBindingState flow)
    {
        RestoreReferenceFieldBindings(flow.Assigned);
    }
    private static ReferenceBindingState? MergeReferenceBindingState(ReferenceBindingState? a, ReferenceBindingState? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        HashSet<FieldSymbol> assigned = a.Assigned.Intersect(b.Assigned).ToHashSet();

        return new(assigned);
    }

    private static ReferenceBindingState ApplyReferenceBindingStateDelta(
        ReferenceBindingState entry,
        ReferenceBindingState before,
        ReferenceBindingState after) => new(
        ApplySetDelta(entry.Assigned, before.Assigned, after.Assigned));

    private static HashSet<T> ApplySetDelta<T>(
        HashSet<T> entry,
        HashSet<T> before,
        HashSet<T> after)
    {
        var result = new HashSet<T>(entry, entry.Comparer);
        foreach (T value in before.Concat(after).Distinct(before.Comparer))
        {
            bool wasPresent = before.Contains(value);
            bool isPresent = after.Contains(value);
            if (wasPresent == isPresent) continue;
            if (isPresent) result.Add(value);
            else result.Remove(value);
        }
        return result;
    }
    private (ReferenceBindingState? True, ReferenceBindingState? False) BooleanFlow(BoundExpression expression)
    {
        if (expression is BoundFullExpression fullExpression)
            return BooleanFlow(fullExpression.Expression);
        if (_booleanReferenceBindings.TryGetValue(expression, out var flow)) return flow;
        if (expression is BoundUnaryExpression { OperatorKind: SyntaxKind.BangToken } unary)
        {
            var operand = BooleanFlow(unary.Operand);
            return (operand.False,operand.True);
        }
        var current = CaptureReferenceBindingState();
        if (_constants.TryFold(expression, out object? value) && value is bool known)
            return known ? (current,null) : (null,current);
        return (current,current);
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
        foreach (ParameterSymbol parameter in function.Parameters)
        {
            _scope.TryDeclare(parameter);

            if (TypeFacts.GetCompleteDestructor(parameter.Type) is not null)
                _function.HasScalarCleanup = true;
        }
    }

    public BoundBlockStatement BindBody(BlockStatementSyntax body)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        BeginResumableBinding(body);
        foreach (ParameterSymbol parameter in _function.Parameters)
            if (TypeFacts.GetCompleteDestructor(parameter.Type) is not null)
                ValidateDestructorAccessibility(parameter.Type, body.OpenBraceToken.Location);
        if (_function.FunctionKind == FunctionKind.Constructor && _function.ContainingType is StructTypeSymbol owner)
        {
            // Reference-field assignment chooses between binding the reference
            // and writing its referent. This is contextual typing only; MIR owns
            // definite-assignment, move, pin and cleanup validation.
            foreach (FieldSymbol field in owner.Fields.Where(field => field.Type is ReferenceTypeSymbol))
            {
                _constructorReferenceFields.Add(field);
                if (field.HasInitializer) _boundReferenceFields.Add(field);
            }
            if (owner.BaseType is { Constructors.IsEmpty: true } defaultBase)
                ValidateDefaultInitialization(defaultBase, body.OpenBraceToken.Location);
        }
        BoundStatement? baseConstructorCall = null;
        bool callsThisConstructor = false;
        ConstructorDeclarationSyntax? constructorSyntax =
            _function.FunctionKind == FunctionKind.Constructor
                ? _function.ImplementationDeclaration as ConstructorDeclarationSyntax
                : null;

        if (constructorSyntax is { HasThisInitializer: true } &&
            _function.ContainingStruct is StructTypeSymbol thisType)
        {
            ImmutableArray<ExpressionSyntax> initializerArguments = constructorSyntax.BaseArguments;
            _bindingBaseConstructorArguments = true;
            ImmutableArray<BoundExpression> arguments;
            try
            {
                arguments = BindTransferredArguments(initializerArguments,
                    (argument, _) => BindDeferredContextualArgument(argument));
            }
            finally
            {
                _bindingBaseConstructorArguments = false;
            }
            FunctionSymbol? target = ResolveConstructor(thisType, arguments, initializerArguments,
                constructorSyntax.BaseKeyword!.Location);
            if (target is not null)
            {
                _semanticInfo.ExplicitReferences.Add(new ResolvedSymbolReference(target,
                    constructorSyntax.BaseKeyword.Location, ResolvedReferenceKind.Call));
                if (ReferenceEquals(target, _function))
                {
                    _diagnostics.Report(constructorSyntax.BaseKeyword.Location,
                        "a constructor cannot chain directly to itself", DiagnosticIds.MissingConstructor);
                    RollbackUnmaterializedContextualArguments(arguments);
                }
                else
                {
                    arguments = ValidateFunctionArguments(target, arguments, initializerArguments,
                        constructorSyntax.BaseKeyword.Location);
                    baseConstructorCall = new BoundExpressionStatement(CompleteFullExpression(
                        new BoundBaseLifecycleCallExpression(target, arguments), resultConsumed: false));
                    callsThisConstructor = true;

                    _boundReferenceFields.UnionWith(_constructorReferenceFields);
                }
            }
        }
        else if (_function.FunctionKind == FunctionKind.Constructor &&
                 _function.ContainingStruct?.BaseType is StructTypeSymbol baseType &&
                 !baseType.Constructors.IsEmpty)
        {
            ConstructorDeclarationSyntax? syntax = constructorSyntax;
            ImmutableArray<ExpressionSyntax> baseArguments = syntax?.BaseArguments ?? [];
            TextLocation location = syntax?.IdentifierToken.Location ?? body.OpenBraceToken.Location;
            _bindingBaseConstructorArguments = true;
            ImmutableArray<BoundExpression> arguments;
            try
            {
                arguments = BindTransferredArguments(baseArguments,
                    (argument, _) => BindDeferredContextualArgument(argument));
            }
            finally
            {
                _bindingBaseConstructorArguments = false;
            }
            FunctionSymbol? baseConstructor = ResolveConstructor(baseType, arguments, baseArguments, location);
            if (baseConstructor is not null)
            {
                if (syntax?.BaseKeyword is { } baseKeyword)
                    _semanticInfo.ExplicitReferences.Add(new ResolvedSymbolReference(baseConstructor,
                        baseKeyword.Location, ResolvedReferenceKind.Call));
                if (!IsAccessible(baseConstructor))
                {
                    _diagnostics.Report(syntax?.BaseKeyword?.Location ?? location, $"constructor '{baseType.Name}' is private",
                        DiagnosticIds.InaccessibleSymbol);
                    RollbackUnmaterializedContextualArguments(arguments);
                }
                else
                {
                    arguments = ValidateFunctionArguments(baseConstructor, arguments, baseArguments, location);
                    baseConstructorCall = new BoundExpressionStatement(CompleteFullExpression(
                        new BoundBaseLifecycleCallExpression(baseConstructor, arguments), resultConsumed: false));
                }
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
        else if (_function.FunctionKind == FunctionKind.Destructor && _function.ContainingStruct is StructTypeSymbol destroyedType)
        {
            if (destroyedType.BaseType is { } baseType)
                ValidateDestructorAccessibility(baseType, body.OpenBraceToken.Location);
            // Runs after local cleanup on both fallthrough and explicit returns:
            // own fields in reverse declaration order, then the complete base destructor.
            boundBody = boundBody with { ExitCleanup = new BoundDestroyFieldsExpression(destroyedType) };
        }

        if (_resumableResult is not null)
        {
            boundBody = FinishResumableBinding(boundBody, body);
            MirReferenceDiagnostics.Analyze(new BoundFunction(_function, boundBody), _fileScope.TypeFactory,
                _diagnostics, _expressionLocations, _cancellationToken);
            MirReferenceDiagnostics.AnalyzeAggregates(new BoundFunction(_function, boundBody), _fileScope.TypeFactory,
                _diagnostics, _expressionLocations, _cancellationToken);
            return boundBody;
        }

        if (!TypeIdentity.AreSame(_function.ReturnType, BuiltinTypes.Void) && !AlwaysReturns(boundBody))
        {
            _diagnostics.Report(
                body.CloseBraceToken.Location,
                _function.IsLambda ? "not all code paths in lambda return a value" :
                    $"not all code paths in function '{_function.Name}' return a value",
                DiagnosticIds.MissingReturn);
        }

        MirReferenceDiagnostics.Analyze(new BoundFunction(_function, boundBody), _fileScope.TypeFactory,
            _diagnostics, _expressionLocations, _cancellationToken);
        MirReferenceDiagnostics.AnalyzeAggregates(new BoundFunction(_function, boundBody), _fileScope.TypeFactory,
            _diagnostics, _expressionLocations, _cancellationToken);
        MirReferenceDiagnostics.AnalyzeShared(new BoundFunction(_function, boundBody), _fileScope.TypeFactory,
            _expressionLocations, _cancellationToken);
        return boundBody;
    }

    internal BoundExpression? BindFieldInitializer(FieldSymbol field)
        => BindFieldInitializer(field, field.Declaration.Initializer);

    internal BoundExpression? BindFieldInitializer(FieldSymbol field, ExpressionSyntax? syntax)
    {
        if (syntax is null)
            return null;

        BoundExpression initializer = BindExpressionWithExpectedType(syntax, field.Type);
        TypeSymbol? destinationType = TryGetStorageType(field.Type, out StorageTypeSymbol storage)
            ? storage.ElementType
            : TypeFacts.IsPinned(field.Type)
                ? field.Type is PinTypeSymbol pin ? pin.ElementType : field.Type
                : null;
        if (destinationType is not null)
        {
            BoundExpression target = field.IsStatic
                ? new BoundStaticFieldExpression(field)
                : new BoundMemberAccessExpression(
                    new BoundThisExpression(field.ContainingType,
                        _fileScope.TypeFactory.PointerTo(field.ContainingType)),
                    field,
                    IsPointerAccess: true);
            initializer = BindDestinationConstruction(target, destinationType, initializer, syntax,
                GetLocation(syntax));
        }
        else if (field.Type is AtomicTypeSymbol atomic && AtomicTypeRules.SupportsOperations(atomic.ElementType))
            initializer = ContextualizeConversion(ReadAtomicValue(initializer), atomic.ElementType, GetLocation(syntax));
        else
            initializer = ContextualizeConversion(initializer, field.Type, GetLocation(syntax));
        SetConvertedType(syntax, destinationType ?? initializer.Type);
        if (initializer is not BoundStorageConstructExpression &&
            !(field.Type is AtomicTypeSymbol atomicInitializer &&
              TypeIdentity.AreSame(atomicInitializer.ElementType, initializer.Type)) &&
            !TypeFacts.CanAssign(field.Type, initializer.Type))
            ReportCannotConvert(GetLocation(syntax), initializer.Type, field.Type);



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

            if (initializer is BoundStorageConstructExpression construction)
                statements.Add(new BoundExpressionStatement(
                    CompleteFullExpression(construction, resultConsumed: false)));
            else
            {
                var target = new BoundMemberAccessExpression(receiver, field, IsPointerAccess: true);
                statements.Add(new BoundExpressionStatement(
                    CompleteFullExpression(new BoundAssignmentExpression(target, SyntaxKind.EqualsToken, initializer)
                    {
                        IsInitialization = true,
                    }, resultConsumed: false)));
            }
        }

        return statements.ToImmutable();
    }

    internal BoundStatement CreateThreadLocalFieldInitializerStatement(FieldSymbol field)
    {
        BoundExpression initializer = field.Initializer!;
        BoundExpression expression = initializer is BoundStorageConstructExpression
            ? initializer
            : new BoundAssignmentExpression(
                new BoundStaticFieldExpression(field), SyntaxKind.EqualsToken, initializer)
            {
                IsInitialization = true,
            };
        return new BoundExpressionStatement(CompleteFullExpression(expression, resultConsumed: false));
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
        ExpressionStatementSyntax expression => new BoundExpressionStatement(BindDiscardedExpression(expression.Expression)),
        IfStatementSyntax @if => BindIfStatement(@if),
        WhileStatementSyntax @while => BindWhileStatement(@while),
        ForStatementSyntax @for => BindForStatement(@for),
        SwitchStatementSyntax @switch => BindSwitchStatement(@switch),
        BreakStatementSyntax @break => BindBreakStatement(@break),
        ContinueStatementSyntax @continue => BindContinueStatement(@continue),
        TryStatementSyntax @try => BindTryStatement(@try),
        ThrowStatementSyntax @throw => BindThrowStatement(@throw),
        _ => throw new InvalidOperationException($"Unexpected statement syntax '{syntax.Kind}'."),
    };

    private BoundTryStatement BindTryStatement(TryStatementSyntax syntax)
    {
        ReferenceBindingState entry = CaptureReferenceBindingState();
        ReferenceFinalizerBindingContext? finalizerFlow = syntax.FinallyBody is null
            ? null
            : new ReferenceFinalizerBindingContext(_loopDepth, _switchDepth, []);
        if (finalizerFlow is not null) _referenceFinalizerBindings.Push(finalizerFlow);
        var exceptionalFlows = new List<ExceptionalReferenceBinding>();
        _tryExceptionalReferenceBindings.Push(exceptionalFlows);
        BoundBlockStatement body;
        try
        {
            body = BindBlockStatement(syntax.Body);
        }
        finally
        {
            _tryExceptionalReferenceBindings.Pop();
        }
        var fallthrough = new List<ReferenceBindingState>();
        if (!AlwaysReturns(body)) fallthrough.Add(CaptureReferenceBindingState());

        var catches = ImmutableArray.CreateBuilder<BoundCatchClause>();
        var precedingTypes = new List<TypeSymbol?>();
        var remainingExceptionalReferenceBindings = new List<ExceptionalReferenceBinding>(exceptionalFlows);
        var handlerExceptionalReferenceBindings = new List<ExceptionalReferenceBinding>();
        foreach (CatchClauseSyntax clause in syntax.Catches)
        {
            BoundScope previous = _scope;
            _scope = new BoundScope(previous);
            TypeSymbol? catchType = null;
            LocalVariableSymbol? variable = null;
            if (clause.Type is not null)
            {
                TypeSymbol declaredType = TypeResolver.Resolve(clause.Type, _fileScope, _diagnostics);
                if (declaredType is not ReferenceTypeSymbol { IsReadonly: true } reference ||
                    !TypeFacts.IsThrowableType(reference.ElementType))
                {
                    _diagnostics.Report(clause.Type.NameToken.Location,
                        "typed catch parameters must have the form 'readonly T& name', where T is a throwable value type",
                        DiagnosticIds.InvalidCatchType);
                }
                else
                {
                    catchType = reference.ElementType;
                    variable = new LocalVariableSymbol(clause.IdentifierToken!.Text, declaredType,
                        _function, isReadonly: true, clause);
                    _semanticInfo.Declarations[clause] = variable;
                    if (!_scope.TryDeclare(variable))
                        _diagnostics.Report(clause.IdentifierToken.Location,
                            $"variable '{variable.Name}' is already declared in this scope",
                            DiagnosticIds.DuplicateDeclaration);
                }
            }

            if (precedingTypes.Any(previousType => CatchCovers(previousType, catchType)))
                _diagnostics.Report(clause.CatchKeyword.Location,
                    "catch clause is unreachable because an earlier handler already covers its type",
                    DiagnosticIds.UnreachableCatch);
            precedingTypes.Add(catchType);

            bool catchAll = clause.Type is null;
            ExceptionalReferenceBinding[] matchingFlows = remainingExceptionalReferenceBindings
                .Where(flow => catchAll || flow.Type is null ||
                    CatchCovers(catchType, flow.Type))
                .ToArray();
            ReferenceBindingState handlerEntry = matchingFlows.Length == 0
                ? entry
                : matchingFlows.Select(flow => flow.State)
                    .Aggregate((left, right) => MergeReferenceBindingState(left, right)!);
            RestoreReferenceBindingState(handlerEntry);


            _catchDepth++;
            ImmutableArray<TypeSymbol?> caughtTypes = matchingFlows
                .Select(flow => flow.Type)
                .Distinct()
                .ToImmutableArray();
            if (caughtTypes.IsEmpty) caughtTypes = [catchType];
            _caughtExceptionTypes.Push(caughtTypes);
            _tryExceptionalReferenceBindings.Push(handlerExceptionalReferenceBindings);
            BoundBlockStatement catchBody;
            try
            {
                catchBody = BindBlockStatement(clause.Body, createScope: false);
            }
            finally
            {
                _tryExceptionalReferenceBindings.Pop();
                _caughtExceptionTypes.Pop();
                _catchDepth--;
                _scope = previous;
            }
            if (!AlwaysReturns(catchBody)) fallthrough.Add(CaptureReferenceBindingState());
            catches.Add(new BoundCatchClause(catchType, variable, catchBody));
            if (catchAll)
                remainingExceptionalReferenceBindings.Clear();
            else if (catchType is not null)
                remainingExceptionalReferenceBindings.RemoveAll(flow =>
                    flow.Type is not null && CatchCovers(catchType, flow.Type));
        }

        if (finalizerFlow is not null) _referenceFinalizerBindings.Pop();

        var finalizerInputs = new List<ReferenceBindingState>(fallthrough);
        finalizerInputs.AddRange(remainingExceptionalReferenceBindings.Select(flow => flow.State));
        finalizerInputs.AddRange(handlerExceptionalReferenceBindings.Select(flow => flow.State));
        if (finalizerFlow is not null) finalizerInputs.AddRange(finalizerFlow.Exits);
        ReferenceBindingState merged = finalizerInputs.Count == 0
            ? entry
            : finalizerInputs.Aggregate((left, right) => MergeReferenceBindingState(left, right)!);
        ReferenceBindingState normalEntry = fallthrough.Count == 0
            ? entry
            : fallthrough.Aggregate((left, right) => MergeReferenceBindingState(left, right)!);
        RestoreReferenceBindingState(merged);
        BoundBlockStatement? finallyBody = null;
        var escapingFlows = new List<ExceptionalReferenceBinding>(remainingExceptionalReferenceBindings);
        escapingFlows.AddRange(handlerExceptionalReferenceBindings);
        if (syntax.FinallyBody is not null)
        {
            var finallyExceptionalReferenceBindings = new List<ExceptionalReferenceBinding>();
            _tryExceptionalReferenceBindings.Push(finallyExceptionalReferenceBindings);
            try
            {
                finallyBody = BindBlockStatement(syntax.FinallyBody);
            }
            finally
            {
                _tryExceptionalReferenceBindings.Pop();
            }
            if (!AlwaysReturns(finallyBody))
            {
                ReferenceBindingState completedFinally = CaptureReferenceBindingState();
                escapingFlows = escapingFlows
                    .Select(flow => new ExceptionalReferenceBinding(
                        ApplyReferenceBindingStateDelta(flow.State, merged, completedFinally),
                        flow.Type)).ToList();
                if (fallthrough.Count > 0)
                    RestoreReferenceBindingState(ApplyReferenceBindingStateDelta(
                        normalEntry, merged, completedFinally));
                else
                    RestoreReferenceBindingState(entry);
            }
            else if (AlwaysReturns(finallyBody))
            {
                escapingFlows.Clear();
                RestoreReferenceBindingState(entry);
            }
            escapingFlows.AddRange(finallyExceptionalReferenceBindings);
        }
        else
        {
            RestoreReferenceBindingState(normalEntry);
        }
        PropagateExceptionalReferenceBindings(escapingFlows);
        return new BoundTryStatement(body, catches.ToImmutable(), finallyBody);
    }

    private static bool CatchCovers(TypeSymbol? earlier, TypeSymbol? later)
    {
        if (earlier is null) return true;
        if (later is null) return false;
        if (TypeIdentity.AreSame(earlier, later)) return true;
        if (earlier is not StructTypeSymbol earlierStruct || later is not StructTypeSymbol laterStruct)
            return false;
        for (StructTypeSymbol? candidate = laterStruct.BaseType; candidate is not null;
             candidate = candidate.BaseType)
            if (TypeIdentity.AreSame(candidate, earlierStruct)) return true;
        return false;
    }

    private BoundThrowStatement BindThrowStatement(ThrowStatementSyntax syntax)
    {
        if (syntax.Expression is null)
        {
            if (_catchDepth == 0)
                _diagnostics.Report(syntax.ThrowKeyword.Location,
                    "a bare 'throw;' is only valid inside a catch handler",
                    DiagnosticIds.RethrowOutsideCatch);
            if (_caughtExceptionTypes.TryPeek(out ImmutableArray<TypeSymbol?> caughtTypes))
            {
                foreach (TypeSymbol? caughtType in caughtTypes)
                    RecordExceptionalReferenceBinding(caughtType);
            }
            else
            {
                RecordExceptionalReferenceBinding();
            }
            return new BoundThrowStatement(null);
        }

        BoundExpression expression = BindExpression(syntax.Expression);
        if (!TypeFacts.IsThrowableType(expression.Type))
            _diagnostics.Report(GetLocation(syntax.Expression),
                $"type '{expression.Type.ToDisplayString()}' cannot be stored as an exception value",
                DiagnosticIds.InvalidThrownType);
        else
        {
            ValidateDestructorAccessibility(expression.Type, GetLocation(syntax.Expression));
            expression = ContextualizeConversion(expression, expression.Type, GetLocation(syntax.Expression));
        }
        expression = CompleteFullExpression(expression, resultConsumed: true);
        RecordExceptionalReferenceBinding(expression.Type);
        return new BoundThrowStatement(expression);
    }

    private void RecordExceptionalReferenceBinding(TypeSymbol? type = null)
    {
        if (_tryExceptionalReferenceBindings.Count == 0) return;
        ReferenceBindingState flow = CaptureReferenceBindingState();
        _tryExceptionalReferenceBindings.Peek().Add(new ExceptionalReferenceBinding(flow, type));
    }

    private void PropagateExceptionalReferenceBindings(IEnumerable<ExceptionalReferenceBinding> flows)
    {
        if (_tryExceptionalReferenceBindings.Count == 0) return;
        _tryExceptionalReferenceBindings.Peek().AddRange(flows);
    }

    private BoundIfStatement BindIfStatement(IfStatementSyntax syntax)
    {
        BoundExpression condition = BindBooleanCondition(syntax.Condition);
        HashSet<FieldSymbol> afterCondition = CloneReferenceFieldBindings();
        var conditionFlow = BooleanFlow(condition);
        if (conditionFlow.True is { } whenTrue) RestoreReferenceBindingState(whenTrue);
        BoundStatement thenStatement = BindEmbeddedStatement(syntax.ThenStatement);
        HashSet<FieldSymbol> afterThen = CloneReferenceFieldBindings();

        RestoreReferenceFieldBindings(afterCondition);
        if (conditionFlow.False is { } whenFalse) RestoreReferenceBindingState(whenFalse);
        BoundStatement? elseStatement = syntax.ElseStatement is null
            ? null
            : BindEmbeddedStatement(syntax.ElseStatement);
        HashSet<FieldSymbol> afterElse = CloneReferenceFieldBindings();

        if (conditionFlow.True is null || conditionFlow.False is null)
        {
            RestoreReferenceFieldBindings(conditionFlow.False is null ? afterThen : afterElse);
        }
        else if (AlwaysReturns(thenStatement) && (elseStatement is null || !AlwaysReturns(elseStatement)))
        {
            RestoreReferenceFieldBindings(afterElse);
        }
        else if (elseStatement is not null && AlwaysReturns(elseStatement) && !AlwaysReturns(thenStatement))
        {
            RestoreReferenceFieldBindings(afterThen);
        }
        else
        {
            afterThen.IntersectWith(afterElse);
            RestoreReferenceFieldBindings(afterThen);
        }

        if (conditionFlow.True is not null && conditionFlow.False is not null)
        {
            bool thenTerminates = BoundControlFlow.TerminatesSection(thenStatement);
            bool elseTerminates = elseStatement is not null && BoundControlFlow.TerminatesSection(elseStatement);
        }

        return new BoundIfStatement(condition, thenStatement, elseStatement);
    }

    private BoundWhileStatement BindWhileStatement(WhileStatementSyntax syntax)
    {
        _repeatedEvaluationDepth++;
        BoundExpression condition;
        try { condition = BindBooleanCondition(syntax.Condition); }
        finally { _repeatedEvaluationDepth--; }
        HashSet<FieldSymbol> afterCondition = CloneReferenceFieldBindings();
        _loopDepth++;
        BoundStatement body = BindEmbeddedStatement(syntax.Body);
        _loopDepth--;
        HashSet<FieldSymbol> afterBody = CloneReferenceFieldBindings();
        afterCondition.IntersectWith(afterBody);
        RestoreReferenceFieldBindings(afterCondition);
        return new BoundWhileStatement(condition, body);
    }

    private BoundForStatement BindForStatement(ForStatementSyntax syntax)
    {
        BoundScope previous = _scope;
        _scope = new BoundScope(previous);

        BoundStatement? initializer = syntax.Initializer is null ? null : BindStatement(syntax.Initializer);
        _repeatedEvaluationDepth++;
        BoundExpression? condition;
        try { condition = syntax.Condition is null ? null : BindBooleanCondition(syntax.Condition); }
        finally { _repeatedEvaluationDepth--; }
        HashSet<FieldSymbol> afterCondition = CloneReferenceFieldBindings();
        _loopDepth++;
        BoundStatement body = BindEmbeddedStatement(syntax.Body);
        BoundExpression? increment = syntax.Increment is null ? null : BindDiscardedExpression(syntax.Increment);
        _loopDepth--;

        HashSet<FieldSymbol> afterIteration = CloneReferenceFieldBindings();
        afterCondition.IntersectWith(afterIteration);
        RestoreReferenceFieldBindings(afterCondition);
        (int forEnd,bool includeForEnd) = GetStatementEnd(syntax.Body);
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
        BoundExpression expression = ReadAtomicValue(BindExpression(syntax.Expression));
        if (!TypeFacts.IsInteger(expression.Type) && !TypeFacts.IsCharacter(expression.Type) &&
            expression.Type is not EnumTypeSymbol && !TypeIdentity.AreSame(expression.Type, BuiltinTypes.Error))
            _diagnostics.Report(syntax.SwitchKeyword.Location, "switch operand must be an integer, char, or enum",
                DiagnosticIds.InvalidSwitchOperand);
        var values = new HashSet<System.Numerics.BigInteger>();
        bool hasDefault = false;
        var sections = ImmutableArray.CreateBuilder<BoundSwitchSection>();
        var assignedBefore = new HashSet<FieldSymbol>(_boundReferenceFields);
        var exits = new List<HashSet<FieldSymbol>>();
        _switchExits.Push((_loopDepth, exits));
        _switchDepth++;
        for (int sectionIndex = 0; sectionIndex < syntax.Sections.Length; sectionIndex++)
        {
            SwitchSectionSyntax section = syntax.Sections[sectionIndex];
            _boundReferenceFields.Clear();
            _boundReferenceFields.UnionWith(assignedBefore);
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
                    !(TypeFacts.IsInteger(boundValue.Type) || TypeFacts.IsCharacter(boundValue.Type) || boundValue.Type is EnumTypeSymbol))
                    _diagnostics.Report(section.Label.Location, "case value must be an integer, char, or enum compile-time constant",
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
        }
        if (exits.Count > 0)
        {
            assignedBefore = new HashSet<FieldSymbol>(exits[0]);
            foreach (var exit in exits.Skip(1)) assignedBefore.IntersectWith(exit);
        }
        _boundReferenceFields.Clear();
        _boundReferenceFields.UnionWith(assignedBefore);
        return new BoundSwitchStatement(CompleteFullExpression(expression, resultConsumed: false), sections.ToImmutable());
    }

    private static bool TerminatesCase(BoundStatement statement) => BoundControlFlow.TerminatesSection(statement);

    private BoundBreakStatement BindBreakStatement(BreakStatementSyntax syntax)
    {
        bool breaksSwitch = _switchExits.TryPeek(out var context) && context.LoopDepth == _loopDepth;
        if (breaksSwitch)
        {
            context.Exits.Add(new HashSet<FieldSymbol>(_boundReferenceFields));
        }
        if (_loopDepth == 0 && _switchDepth == 0)
        {
            _diagnostics.Report(syntax.BreakKeyword.Location, "'break' can only be used inside a loop or switch",
                DiagnosticIds.BreakOutsideLoopOrSwitch);
        }

        RecordAbruptFinalizerFlow(finalizer => breaksSwitch
            ? _switchDepth <= finalizer.SwitchDepth
            : _loopDepth <= finalizer.LoopDepth);

        return new BoundBreakStatement();
    }

    private BoundContinueStatement BindContinueStatement(ContinueStatementSyntax syntax)
    {
        if (_loopDepth == 0)
        {
            _diagnostics.Report(syntax.ContinueKeyword.Location, "'continue' can only be used inside a loop",
                DiagnosticIds.ContinueOutsideLoop);
        }

        RecordAbruptFinalizerFlow(finalizer => _loopDepth <= finalizer.LoopDepth);

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
        BoundExpression condition = ReadAtomicValue(BindExpression(syntax));
        if (!TypeIdentity.AreSame(condition.Type, BuiltinTypes.Bool) && !TypeIdentity.AreSame(condition.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(GetLocation(syntax), $"condition must have type 'bool', but has type '{condition.Type.ToDisplayString()}'",
                DiagnosticIds.InvalidCondition);
        }

        return CompleteFullExpression(condition, resultConsumed: false);
    }

    private BoundVariableDeclarationStatement BindVariableDeclaration(VariableDeclarationStatementSyntax syntax)
    {
        bool isConstant = syntax.Type.GetQualifier(SyntaxKind.ConstKeyword) is not null && !syntax.Type.Contains<PointerTypeSyntax>() && !syntax.Type.Contains<ReferenceTypeSyntax>();
        TypeSymbol type = TypeResolver.Resolve(isConstant ? syntax.Type.WithoutQualifier(SyntaxKind.ConstKeyword) : syntax.Type, _fileScope, _diagnostics);
        if (ContainsStaticStruct(type))
            _diagnostics.Report(syntax.Type.NameToken.Location,
                "static structs cannot be used as local, pointer, reference, ownership, or array values",
                DiagnosticIds.InvalidLocalType);
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
        if (TypeFacts.GetCompleteDestructor(type) is { } destructor)
        {
            variable.Destructor = destructor;
            _function.HasScalarCleanup = true;
            if ((syntax.Initializer is not null || TypeFacts.IsStorageType(type)) &&
                type is not OwnershipTypeSymbol)
                ValidateDestructorAccessibility(type, syntax.IdentifierToken.Location);
        }
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

        BoundExpression? initializer = syntax.Initializer is null ? null :
            BindExpressionWithExpectedType(syntax.Initializer, type);
        bool isStorageDeclaration = TryGetStorageType(type, out StorageTypeSymbol storageType);
        bool isDirectPinDeclaration = !isStorageDeclaration && TypeFacts.IsPinned(type);
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
            if (isStorageDeclaration)
                initializer = BindDestinationConstruction(new BoundVariableExpression(variable), storageType.ElementType,
                    initializer, syntax.Initializer!, syntax.IdentifierToken.Location);
            else if (isDirectPinDeclaration)
                initializer = BindDestinationConstruction(new BoundVariableExpression(variable),
                    type is PinTypeSymbol pinType ? pinType.ElementType : type,
                    initializer, syntax.Initializer!, syntax.IdentifierToken.Location);
            else if (type is AtomicTypeSymbol atomic && AtomicTypeRules.SupportsOperations(atomic.ElementType))
            {
                initializer = ContextualizeConversion(ReadAtomicValue(initializer), atomic.ElementType,
                    GetLocation(syntax.Initializer!));
            }
            else
                initializer = ContextualizeConversion(initializer, type, GetLocation(syntax.Initializer!));
            SetConvertedType(syntax.Initializer!, isStorageDeclaration ? storageType.ElementType :
                isDirectPinDeclaration && type is PinTypeSymbol convertedPin ? convertedPin.ElementType : type);
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

        if (initializer is not null && initializer is not BoundStorageConstructExpression &&
            !(type is AtomicTypeSymbol atomicInitializer &&
              TypeIdentity.AreSame(atomicInitializer.ElementType, initializer.Type)) &&
            !TypeFacts.CanAssign(type, initializer.Type))
        {
            ReportCannotConvert(GetLocation(syntax.Initializer!), initializer.Type, type);
        }



        if (initializer is not null)
            initializer = CompleteFullExpression(initializer, resultConsumed: true);
        return new BoundVariableDeclarationStatement(variable, initializer);
    }

    private BoundReturnStatement BindReturnStatement(ReturnStatementSyntax syntax)
    {
        if (_resumableResult is not null) return BindResumableReturn(syntax);
        int? contextualCallableConversionCost = null;
        bool hasContextualCallableConversion = false;
        if (_isLambdaCompatibilityProbe && syntax.Expression is { } returnExpression)
        {
            if (TryGetLambdaExpression(returnExpression, out LambdaExpressionSyntax lambda))
            {
                // Binding a nested callable with an expected return type materializes the
                // conversion immediately. Preserve its candidate-specific cost before
                // the resulting bound expression has the exact contextual type.
                contextualCallableConversionCost = GetLambdaArgumentConversionCost(_function.ReturnType, lambda);
                hasContextualCallableConversion = true;
            }
            else if (TryGetNamedFunctionExpression(returnExpression, out ExpressionSyntax namedFunction))
            {
                contextualCallableConversionCost = GetNamedFunctionArgumentConversionCost(
                    _function.ReturnType, namedFunction);
                hasContextualCallableConversion = true;
            }
        }
        BoundExpression? expression = syntax.Expression is null ? null :
            BindExpressionWithExpectedType(syntax.Expression, _function.ReturnType);
        if (_isLambdaCompatibilityProbe)
        {
            if (TypeIdentity.AreSame(_function.ReturnType, BuiltinTypes.Void))
                _lambdaProbeReturnsCompatible &= expression is null;
            else if (expression is null)
                _lambdaProbeReturnsCompatible = false;
            else if (expression is BoundErrorExpression)
                _lambdaProbeReturnsCompatible = false;
            else
            {
                int? conversionCost = hasContextualCallableConversion
                    ? contextualCallableConversionCost
                    : GetArgumentConversionCost(_function.ReturnType, expression);
                _lambdaProbeReturnsCompatible &= conversionCost.HasValue;
                if (conversionCost.HasValue)
                    _lambdaProbeReturnConversionCost = Math.Max(
                        _lambdaProbeReturnConversionCost, conversionCost.Value);
            }
        }
        if (expression is not null)
        {
            expression = ContextualizeConversion(expression, _function.ReturnType, GetLocation(syntax.Expression!));
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
            _diagnostics.Report(syntax.ReturnKeyword.Location,
                $"{(_function.IsLambda ? "lambda" : $"function '{_function.Name}'")} must return a value of type '{_function.ReturnType.ToDisplayString()}'",
                DiagnosticIds.MissingReturnValue);
        }
        else if (!TypeFacts.CanAssign(_function.ReturnType, expression.Type))
        {
            ReportCannotConvert(GetLocation(syntax.Expression!), expression.Type, _function.ReturnType);
        }


        if (expression is not null)
            expression = CompleteFullExpression(expression, resultConsumed: true);
        RecordAbruptFinalizerFlow(_ => true);
        return new BoundReturnStatement(expression);
    }

    private static bool ContainsValueReferenceStorage(TypeSymbol type) =>
        ContainsValueReferenceStorage(type, []);

    private static bool ContainsValueReferenceStorage(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        if (type is ReferenceTypeSymbol or FunctionValueTypeSymbol) return true;
        if (type is StorageTypeSymbol storage)
            return ContainsValueReferenceStorage(storage.ElementType, visited);
        if (type is PinTypeSymbol pin)
            return ContainsValueReferenceStorage(pin.ElementType, visited);
        if (type is ArrayTypeSymbol array)
            return ContainsValueReferenceStorage(array.ElementType, visited);
        if (type is not IFieldStorageTypeSymbol aggregate || !visited.Add(type)) return false;
        bool result = aggregate.AllInstanceFields.Any(field =>
            ContainsValueReferenceStorage(field.Type, visited));
        visited.Remove(type);
        return result;
    }

    private BoundExpression BindExpression(ExpressionSyntax syntax)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        bool validatePlaceUse = syntax switch
        {
            NameExpressionSyntax => _memberAccessBindingDepth == 0,
            MemberAccessExpressionSyntax => _memberAccessBindingDepth == 0,
            _ => false,
        };
        bool isMemberAccess = syntax is MemberAccessExpressionSyntax;
        if (isMemberAccess) _memberAccessBindingDepth++;
        BoundExpression expression;
        try
        {
            expression = syntax switch
            {
                MissingExpressionSyntax => new BoundErrorExpression(),
                LambdaExpressionSyntax lambda => BindLambdaExpression(lambda),
                LiteralExpressionSyntax literal => BindLiteralExpression(literal),
                NameExpressionSyntax name => BindNameExpression(name),
                ThisExpressionSyntax @this => BindThisExpression(@this),
                ParenthesizedExpressionSyntax parenthesized => BindExpression(parenthesized.Expression),
                AwaitExpressionSyntax awaitExpression => BindAwaitExpression(awaitExpression),
                MoveExpressionSyntax move => BindMoveExpression(move),
                LockExpressionSyntax @lock => BindLockExpression(@lock),
                UnaryExpressionSyntax unary => BindUnaryExpression(unary),
                PostfixUnaryExpressionSyntax postfix => BindPostfixUnaryExpression(postfix),
                BinaryExpressionSyntax binary => BindBinaryExpression(binary),
                AssignmentExpressionSyntax assignment => BindAssignmentExpression(assignment),
                CompareExchangeExpressionSyntax compareExchange => BindCompareExchangeExpression(compareExchange),
                SwapExpressionSyntax swap => BindSwapExpression(swap),
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
        }
        finally
        {
            if (isMemberAccess) _memberAccessBindingDepth--;
        }
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
        _expressionSyntax[expression] = syntax;

        BoundExpression result = DereferenceReference(expression);
        _expressionLocations[result] = GetLocation(syntax);
        _expressionSyntax[result] = syntax;
        _semanticInfo.Receivers[syntax] = new ReceiverInfo(
            result.Type,
            IsStatic: false,
            IsReadonly: result.Type is PointerTypeSymbol { IsReadonly: true } or ReferenceTypeSymbol { IsReadonly: true } ||
                IsAddressable(result) && !IsWritable(result),
            IsWritable: IsWritable(result));
        if (CanThrowDuringEvaluation(expression)) RecordExceptionalReferenceBinding();
        return result;
    }

    private static bool CanThrowDuringEvaluation(BoundExpression expression) => expression switch
    {
        BoundCallExpression { Function.IsExtern: false } => true,
        BoundIndirectCallExpression => true,
        BoundMethodCallExpression { Method.IsExtern: false } => true,
        BoundInterfaceMethodCallExpression => true,
        BoundPropertySetExpression => true,
        BoundInterfacePropertySetExpression => true,
        BoundIndexerSetExpression => true,
        BoundInterfaceIndexerSetExpression => true,
        BoundCompoundAccessorAssignmentExpression => true,
        BoundConstructorCallExpression => true,
        BoundBaseLifecycleCallExpression { Function.IsExtern: false } => true,
        BoundStorageConstructExpression { Constructor: not null } => true,
        BoundStructConstructionExpression => true,
        BoundArrayCreationExpression => true,
        BoundNewExpression { Constructor: not null } => true,
        BoundStaticFieldExpression { Field.IsThreadLocal: true } => true,
        BoundDeleteExpression { Destructor: not null } => true,
        BoundExplicitDestructExpression { Destructor: not null } => true,
        BoundCompareExchangeExpression { Target.Type: AtomicTypeSymbol atomic } when
            TypeFacts.GetCompleteDestructor(atomic.ElementType) is not null => true,
        _ => false,
    };

    private void RecordAbruptFinalizerFlow(Func<ReferenceFinalizerBindingContext, bool> leavesProtectedRegion)
    {
        if (_referenceFinalizerBindings.Count == 0) return;
        ReferenceBindingState? flow = null;
        foreach (ReferenceFinalizerBindingContext finalizer in _referenceFinalizerBindings)
        {
            if (!leavesProtectedRegion(finalizer)) continue;
            flow ??= CaptureReferenceBindingState();
            finalizer.Exits.Add(flow);
        }
    }

    private static BoundExpression DereferenceReference(BoundExpression expression) =>
        expression.Type is ReferenceTypeSymbol referenceType
            ? new BoundReferenceDereferenceExpression(expression, referenceType)
            : expression;

    private BoundExpression BindThisExpression(ThisExpressionSyntax syntax)
    {
        if (ReportUnsupportedLambdaCapture(syntax.ThisKeyword)) return new BoundErrorExpression();
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
        if (NumericLiteralBinding.TryBindExplicit(token, _constants, _diagnostics, out BoundExpression explicitLiteral))
            return explicitLiteral;
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
            SyntaxKind.CharacterLiteralToken => new BoundLiteralExpression(token.Value, BuiltinTypes.Char),
            SyntaxKind.TrueKeyword => new BoundLiteralExpression(true, BuiltinTypes.Bool),
            SyntaxKind.FalseKeyword => new BoundLiteralExpression(false, BuiltinTypes.Bool),
            SyntaxKind.NullKeyword => new BoundLiteralExpression(null, BuiltinTypes.Null),
            _ => new BoundErrorExpression(),
        };
    }

    private BoundExpression BindNameExpression(NameExpressionSyntax syntax, bool requireDefinitelyAssigned = true)
    {
        if (ReportUnsupportedLambdaCapture(syntax.IdentifierToken)) return new BoundErrorExpression();
        VariableSymbol? variable = _scope.Lookup(syntax.IdentifierToken.Text);
        if (variable is not null)
        {
            // A capture is a distinct storage slot for lowering, but editor identity remains
            // the source variable so capture-list and body occurrences navigate and rename together.
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(
                variable is CaptureVariableSymbol capture ? capture.CapturedVariable ?? capture : variable);
            if (variable is LocalVariableSymbol { ConstantValue: not null } localConstant) return localConstant.ConstantValue;
            return new BoundVariableExpression(variable);
        }

        if (_expectedFunctionValueType is { } expectedFunctionValue &&
            TryBindNamedFunctionValue(syntax, expectedFunctionValue, out BoundExpression? functionValue))
            return functionValue!;

        if (_function.ContainingType is { } containingType)
        {
            ConstantSymbol? associatedConstant = containingType.FindMember<ConstantSymbol>(syntax.IdentifierToken.Text);
            if (associatedConstant?.BoundValue is { } associatedValue)
            {
                _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(associatedConstant);
                return associatedValue;
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
                if (!IsAccessible(field))
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
        if (constant is not null && !IsAccessible(constant))
        {
            _semanticInfo.Symbols[syntax] = new SymbolInfo(null, [constant], CandidateReason.Inaccessible);
            _diagnostics.Report(syntax.IdentifierToken.Location,
                $"constant '{constant.Name}' is inaccessible from this context",
                DiagnosticIds.InaccessibleSymbol);
            return new BoundErrorExpression();
        }
        if (constant?.BoundValue is { } constantValue)
        {
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(constant);
            return constantValue;
        }
        if (constant is not null)
            return new BoundErrorExpression();

        _diagnostics.Report(syntax.IdentifierToken.Location, $"unknown identifier '{syntax.IdentifierToken.Text}'",
            DiagnosticIds.UnknownIdentifier);
        return new BoundErrorExpression();
    }

    private BoundExpression BindUnaryExpression(UnaryExpressionSyntax syntax)
    {
        if (syntax.OperatorToken.Kind == SyntaxKind.AmpersandToken &&
            TryBindFunctionAddress(syntax.Operand, out BoundExpression? functionAddress))
            return functionAddress!;
        BoundExpression operand = BindExpression(syntax.Operand);
        if (TryBindUserOperator(syntax.OperatorToken.Kind, [operand], [syntax.Operand], syntax,
            syntax.OperatorToken.Location, out BoundExpression? userOperator)) return userOperator!;
        return BindUnaryExpression(syntax.OperatorToken, operand, isPostfix: false);
    }

    private bool TryBindFunctionAddress(ExpressionSyntax syntax, out BoundExpression? address)
    {
        address = null;
        if (syntax is NameExpressionSyntax capturedName && ReportUnsupportedLambdaCapture(capturedName.IdentifierToken))
        {
            address = new BoundErrorExpression();
            return true;
        }
        var lookupDiagnostics = new DiagnosticBag();
        FunctionSymbol[] candidates;
        SyntaxToken nameToken;
        if (syntax is NameExpressionSyntax name)
        {
            if (_scope.Lookup(name.IdentifierToken.Text) is not null ||
                _function.ContainingType?.FindInstanceField(name.IdentifierToken.Text) is not null)
                return false;
            nameToken = name.IdentifierToken;
            candidates = _function.ContainingType is { } containingType
                ? containingType.LookupMethods(nameToken.Text).ToArray()
                : [];
            if (candidates.Length == 0)
                candidates = _fileScope.ResolveFunctions(nameToken.Text).ToArray();
        }
        else if (syntax is MemberAccessExpressionSyntax member &&
                 TryGetDottedName(member, out ImmutableArray<SyntaxToken> parts) && parts.Length >= 2)
        {
            nameToken = parts[^1];
            string[] receiverParts = parts.Take(parts.Length - 1).Select(part => part.Text).ToArray();
            TypeSymbol? receiverType = receiverParts.Length == 1
                ? ResolveUnqualifiedTypeForExpression(receiverParts[0], parts[0].Location, lookupDiagnostics)
                : _fileScope.ResolveQualifiedType(receiverParts);
            candidates = receiverType is DeclaredTypeSymbol declaredType
                ? declaredType.LookupMethods(nameToken.Text).ToArray()
                : _fileScope.ResolveQualifiedFunctions(parts.Select(part => part.Text).ToArray()).ToArray();
        }
        else
        {
            return false;
        }

        if (candidates.Length == 0) return false;
        if (candidates.Length == 1 && candidates[0].HasImplicitThis)
        {
            FunctionSymbol instanceMethod = candidates[0];
            RecordCandidates(syntax, null, candidates, CandidateReason.NotInvocable);
            _diagnostics.Report(nameToken.Location,
                $"cannot take the address of instance method '{instanceMethod.Name}'; bound method pointers are not supported",
                DiagnosticIds.InvalidOperatorOperands);
            address = new BoundErrorExpression();
            return true;
        }
        if (candidates.Length == 1 && candidates[0].IsGenericDefinition)
        {
            FunctionSymbol genericFunction = candidates[0];
            RecordCandidates(syntax, null, candidates, CandidateReason.NotInvocable);
            _diagnostics.Report(nameToken.Location,
                $"cannot take the address of generic function '{genericFunction.Name}' without a concrete specialization",
                DiagnosticIds.GenericSpecializationNotImplemented);
            address = new BoundErrorExpression();
            return true;
        }
        FunctionSymbol[] addressable = candidates.Where(candidate =>
            !candidate.HasImplicitThis && !candidate.IsGenericDefinition).ToArray();
        // A lone function retains its actual pointer type so contextual conversion
        // can issue the established type-mismatch diagnostic. Expected type is an
        // overload discriminator only when there is a set to choose from.
        if (candidates.Length > 1 && _expectedFunctionPointerType is { } expected)
            addressable = addressable.Where(candidate =>
                TypeIdentity.AreSame(candidate.ReturnType, expected.ReturnType) &&
                candidate.Parameters.Length == expected.ParameterTypes.Length &&
                candidate.Parameters.Zip(expected.ParameterTypes).All(pair =>
                    TypeIdentity.AreSame(pair.First.Type, pair.Second))).ToArray();

        if (addressable.Length != 1)
        {
            RecordCandidates(syntax, null, candidates,
                addressable.Length > 1 ? CandidateReason.Ambiguous : CandidateReason.NotInvocable);
            _diagnostics.Report(nameToken.Location,
                addressable.Length > 1
                    ? $"function address '&{nameToken.Text}' is ambiguous between: {FormatCallableCandidates(addressable)}"
                    : $"no overload of '&{nameToken.Text}' matches the expected function-pointer type",
                addressable.Length > 1 ? DiagnosticIds.AmbiguousCall : DiagnosticIds.NoMatchingCandidate);
            address = new BoundErrorExpression();
            return true;
        }

        FunctionSymbol function = addressable[0];
        if (function.HasImplicitThis)
        {
            _diagnostics.Report(nameToken.Location,
                $"cannot take the address of instance method '{function.Name}'; bound method pointers are not supported",
                DiagnosticIds.InvalidOperatorOperands);
            address = new BoundErrorExpression();
            return true;
        }
        if (function.IsGenericDefinition)
        {
            _diagnostics.Report(nameToken.Location,
                $"cannot take the address of generic function '{function.Name}' without a concrete specialization",
                DiagnosticIds.GenericSpecializationNotImplemented);
            address = new BoundErrorExpression();
            return true;
        }

        if (function.ContainingType is not null && !IsAccessible(function))
        {
            _diagnostics.Report(nameToken.Location,
                $"static method '{function.Name}' is private in struct '{function.ContainingType.Name}'",
                DiagnosticIds.InaccessibleSymbol);
            address = new BoundErrorExpression();
            return true;
        }
        if (function.ContainingType is null && !IsAccessible(function))
        {
            _diagnostics.Report(nameToken.Location,
                $"function '{function.Name}' is private in namespace '{function.ContainingNamespace.FullName}'",
                DiagnosticIds.InaccessibleSymbol);
            address = new BoundErrorExpression();
            return true;
        }

        RecordCandidates(syntax, function, candidates, CandidateReason.None);
        FunctionPointerTypeSymbol type = _fileScope.TypeFactory.FunctionPointer(
            function.ReturnType,
            function.Parameters.Select(parameter => parameter.Type));
        address = new BoundFunctionAddressExpression(function, type);
        return true;
    }

    private BoundExpression BindMoveExpression(MoveExpressionSyntax syntax)
    {
        if (ReferenceEquals(syntax, _unconsumedOwnershipExpression))
        {
            BoundExpression operand = BindExpression(syntax.Operand);
            return operand is BoundErrorExpression ? operand : new BoundMoveExpression(operand);
        }

        BoundExpression source = BindLifetimeInvalidationOperand(syntax.Operand);
        MovePlace? trackedOwner = TryGetMovePlace(source, out MovePlace sourceOwner) ? sourceOwner : null;
        if (TryGetStorageType(source.Type, out StorageTypeSymbol storageType) && IsAddressable(source))
        {
            if (!IsWritable(source))
            {
                _diagnostics.Report(syntax.MoveKeyword.Location,
                    "'move' requires a writable local storage location",
                    DiagnosticIds.InvalidMoveSource);
                return new BoundErrorExpression();
            }
            MovePlace? storagePlace = trackedOwner;
            var storageMove = new BoundStorageMoveExpression(source, storageType);
            return storageMove;
        }
        if (trackedOwner is null && IsAddressable(source))
        {
            if (!IsWritable(source))
            {
                _diagnostics.Report(syntax.MoveKeyword.Location,
                    "'move' requires writable storage; readonly raw pointers cannot transfer pointee lifetimes",
                    DiagnosticIds.InvalidMoveSource);
                return new BoundErrorExpression();
            }
            if (TypeFacts.ContainsAtomicStorage(source.Type))
            {
                _diagnostics.Report(syntax.MoveKeyword.Location,
                    $"cannot move '{source.Type.ToDisplayString()}' because it contains atomic storage that cannot be implicitly relocated",
                    DiagnosticIds.AtomicStorageNotRelocatable);
                return new BoundErrorExpression();
            }
            if (!TypeFacts.CanRelocate(source.Type))
            {
                _diagnostics.Report(syntax.MoveKeyword.Location,
                    $"cannot move '{source.Type.ToDisplayString()}' because its address is pinned",
                    DiagnosticIds.PinnedRelocation);
                return new BoundErrorExpression();
            }
            return new BoundMoveExpression(source);
        }
        if (trackedOwner is not MovePlace place ||
            place.RootVariable is null && place.Fields.IsEmpty ||
            source.Type is ReferenceTypeSymbol ||
            !IsWritable(source))
        {
            if (!TypeIdentity.AreSame(source.Type, BuiltinTypes.Error))
                _diagnostics.Report(syntax.MoveKeyword.Location,
                    "'move' requires a writable local storage location",
                    DiagnosticIds.InvalidMoveSource);
            return new BoundErrorExpression();
        }

        if (TypeFacts.ContainsAtomicStorage(source.Type))
        {
            _diagnostics.Report(syntax.MoveKeyword.Location,
                $"cannot move '{source.Type.ToDisplayString()}' because it contains atomic storage that cannot be implicitly relocated",
                DiagnosticIds.AtomicStorageNotRelocatable);
            return new BoundErrorExpression();
        }

        if (!TypeFacts.CanRelocate(source.Type))
        {
            _diagnostics.Report(syntax.MoveKeyword.Location,
                $"cannot move '{source.Type.ToDisplayString()}' because its address is pinned",
                DiagnosticIds.PinnedRelocation);
            return new BoundErrorExpression();
        }

        bool onlyDeferredMoveConflict = IsOnlyDeferredMoveConflict(place);

        foreach (ArgumentBindingTransaction transaction in _argumentBindingTransactions)
            transaction.RecordSubsequentPlaceMutation(
                place, syntax.MoveKeyword.Location, deferredDependent: onlyDeferredMoveConflict);
        var result = new BoundMoveExpression(source)
        {
            TrackedVariable = place.RootVariable,
            TrackedPath = place.Fields,
        };
        if (_argumentBindingTransactions.Count != 0)
        {
            ArgumentBindingTransaction transaction = _argumentBindingTransactions.Peek();
            transaction.RecordCandidate(result, place);
            _argumentBindingCandidates.Add(result, transaction);
        }
        return result;
    }

    private BoundExpression BindLockExpression(LockExpressionSyntax syntax)
    {
        BoundExpression operand = BindExpression(syntax.Operand);
        if (operand.Type is not WeakTypeSymbol weakType)
        {
            if (!TypeIdentity.AreSame(operand.Type, BuiltinTypes.Error))
                _diagnostics.Report(syntax.LockKeyword.Location,
                    $"'lock' requires a weak<T> value, but has type '{operand.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidLockOperand);
            return new BoundErrorExpression();
        }

        SharedTypeSymbol sharedType = _fileScope.TypeFactory.SharedOf(weakType.ElementType);
        _fileScope.TypeFactory.EnsureOwnershipDestructor(
            sharedType, _fileScope.GlobalNamespace, syntax);
        return new BoundLockExpression(operand, sharedType);
    }

    private bool TryGetMovePlace(BoundExpression expression, out MovePlace place)
    {
        if (expression is BoundVariableExpression variable &&
            variable.Variable is LocalVariableSymbol or ParameterSymbol)
        {
            place = new MovePlace(variable.Variable, []);
            return true;
        }
        if (expression is BoundThisExpression @this)
        {
            place = new MovePlace(_function, @this.ContainingType, "this", []);
            return true;
        }
        if (expression is BoundMemberAccessExpression member &&
            (!member.IsPointerAccess || member.Receiver is BoundThisExpression) &&
            TryGetMovePlace(member.Receiver, out MovePlace receiverPlace))
        {
            place = new MovePlace(receiverPlace.Root, receiverPlace.RootType, receiverPlace.RootName,
                receiverPlace.Fields.Add(member.Field));
            return true;
        }
        if (expression is BoundReferenceDereferenceExpression dereference &&
            TryGetMovePlace(dereference.Reference, out place))
            return true;
        if (expression is BoundReferenceConversionExpression conversion &&
            TryGetMovePlace(conversion.Source, out place))
            return true;
        if (expression is BoundLifetimeValueExpression lifetime &&
            TryGetMovePlace(lifetime.Source, out place))
            return true;
        place = null!;
        return false;
    }

    private static bool IsPlacePrefixOf(MovePlace prefix, MovePlace place)
    {
        if (!ReferenceEquals(prefix.Root, place.Root) || prefix.Fields.Length > place.Fields.Length) return false;
        for (int i = 0; i < prefix.Fields.Length; i++)
            if (!ReferenceEquals(prefix.Fields[i], place.Fields[i])) return false;
        return true;
    }

    private static bool PlacesOverlap(MovePlace left, MovePlace right) =>
        IsPlacePrefixOf(left, right) || IsPlacePrefixOf(right, left);

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
        AtomicTypeSymbol? atomic = operand.Type as AtomicTypeSymbol;
        if (operatorToken.Kind is not (SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken or SyntaxKind.AmpersandToken))
            operand = ReadAtomicValue(operand);
        TypeSymbol? resultType = operatorToken.Kind switch
        {
            SyntaxKind.PlusToken or SyntaxKind.MinusToken when TypeFacts.IsNumeric(operand.Type) => operand.Type,
            SyntaxKind.BangToken when TypeIdentity.AreSame(operand.Type, BuiltinTypes.Bool) => BuiltinTypes.Bool,
            SyntaxKind.TildeToken when TypeFacts.IsInteger(operand.Type) => operand.Type,
            SyntaxKind.StarToken when operand.Type is PointerTypeSymbol pointer => pointer.ElementType,
            SyntaxKind.StarToken when operand.Type is UniqueTypeSymbol unique => unique.ElementType,
            SyntaxKind.StarToken when operand.Type is SharedTypeSymbol shared => shared.ElementType,
            SyntaxKind.AmpersandToken when IsAddressable(operand) => _fileScope.TypeFactory.PointerTo(
                GetAddressedValueType(operand.Type), isReadonly: !IsWritable(operand)),
            SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken
                when IsWritable(operand) && (TypeFacts.IsNumeric(operand.Type) ||
                    operand.Type is PointerTypeSymbol pointer && !TypeIdentity.AreSame(pointer.ElementType, BuiltinTypes.Void)) => operand.Type,
            SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken
                when atomic is not null && AtomicTypeRules.SupportsOperations(atomic.ElementType) &&
                     TypeFacts.IsNumeric(atomic.ElementType) && IsWritable(operand) => atomic.ElementType,
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
        if (syntax.OperatorToken.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken)
            return BindBinaryExpressionCore(syntax, null);
        var transaction = new ArgumentBindingTransaction();
        _argumentBindingTransactions.Push(transaction);
        try { return BindBinaryExpressionCore(syntax, transaction); }
        finally
        {
            _argumentBindingTransactions.Pop();
        }
    }

    private BoundExpression BindBinaryExpressionCore(BinaryExpressionSyntax syntax, ArgumentBindingTransaction? transaction)
    {
        BoundExpression left = ReadAtomicValue(BindExpression(syntax.Left));
        bool shortCircuit = syntax.OperatorToken.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken;
        var leftFlow = shortCircuit ? BooleanFlow(left) : default;
        bool isAnd = syntax.OperatorToken.Kind == SyntaxKind.AmpersandAmpersandToken;
        if (shortCircuit && (isAnd ? leftFlow.True : leftFlow.False) is { } rhsEntry)
            RestoreReferenceBindingState(rhsEntry);
        bool previousSuppression = _suppressIntegerOperationDiagnostics;
        if (syntax.OperatorToken.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken &&
            _constants.TryFold(left, out object? value) && value is bool condition &&
            condition == (syntax.OperatorToken.Kind == SyntaxKind.PipePipeToken))
            _suppressIntegerOperationDiagnostics = true;
        BoundExpression right;
        try { right = ReadAtomicValue(BindExpression(syntax.Right)); }
        finally { _suppressIntegerOperationDiagnostics = previousSuppression; }

        var rightFlow = shortCircuit ? BooleanFlow(right) : default;
        (ReferenceBindingState? True, ReferenceBindingState? False) resultFlow = default;
        if (shortCircuit)
        {
            resultFlow = isAnd
                ? (leftFlow.True is null ? null : rightFlow.True,                    MergeReferenceBindingState(leftFlow.False, leftFlow.True is null ? null : rightFlow.False))
                : (MergeReferenceBindingState(leftFlow.True, leftFlow.False is null ? null : rightFlow.True),                    leftFlow.False is null ? null : rightFlow.False);
            RestoreReferenceBindingState(MergeReferenceBindingState(resultFlow.True, resultFlow.False)!);
        }

        if (syntax.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken)
        {
            if (left.Type is PointerTypeSymbol or FunctionPointerTypeSymbol or SharedTypeSymbol)
            {
                right = ContextualizeNull(right, left.Type);
            }
            else if (right.Type is PointerTypeSymbol or FunctionPointerTypeSymbol or SharedTypeSymbol)
            {
                left = ContextualizeNull(left, right.Type);
            }
        }

        if (!shortCircuit && TryBindUserOperator(syntax.OperatorToken.Kind, [left, right],
            [syntax.Left, syntax.Right], syntax, syntax.OperatorToken.Location, out BoundExpression? userOperator))
            return userOperator!;
        TypeSymbol? resultType = GetBinaryResultType(left.Type, syntax.OperatorToken.Kind, right.Type);

        if (resultType is null)
        {
            if ((syntax.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken) &&
                TypeIdentity.AreSame(left.Type, right.Type) &&
                left.Type is StructTypeSymbol &&
                TypeFacts.GetValueEqualityFailure(left.Type) is { } equalityFailure)
            {
                string fieldPath = equalityFailure.FieldPath.IsEmpty
                    ? string.Empty
                    : $" through field '{string.Join('.', equalityFailure.FieldPath.Select(field => field.Name))}'";
                string reason = equalityFailure.ContainsAtomicStorage
                    ? $"it contains atomic storage{fieldPath}, whose wrapper bytes cannot participate in aggregate equality"
                    : $"field type '{equalityFailure.Type.ToDisplayString()}'{fieldPath} does not support equality";
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"struct value equality is not available for '{left.Type.ToDisplayString()}' because {reason}",
                    DiagnosticIds.StructValueEqualityNotSupported);
                return new BoundErrorExpression();
            }
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
        if (shortCircuit) _booleanReferenceBindings.Add(result, resultFlow);
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
        ReferenceBindingState beforeTarget = CaptureReferenceBindingState();
        ExpressionSyntax? speculativePreviousTarget = _initializationTarget;
        _initializationTarget = isSimpleAssignment ? syntax.Target : null;
        BoundExpression? indexerAssignment;
        try { indexerAssignment = TryBindIndexerAssignment(syntax, isSimpleAssignment); }
        finally { _initializationTarget = speculativePreviousTarget; }
        if (indexerAssignment is not null)
            return indexerAssignment;
        RestoreReferenceBindingState(beforeTarget);
        _initializationTarget = isSimpleAssignment ? syntax.Target : null;
        BoundExpression? propertyAssignment;
        try { propertyAssignment = TryBindPropertyAssignment(syntax, isSimpleAssignment); }
        finally { _initializationTarget = speculativePreviousTarget; }
        if (propertyAssignment is not null)
            return propertyAssignment;
        RestoreReferenceBindingState(beforeTarget);

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
        BoundMemberAccessExpression? fieldTarget = rawTarget as BoundMemberAccessExpression;

        bool initializesField = isSimpleAssignment &&
            fieldTarget is { Receiver: BoundThisExpression } &&
            _function.FunctionKind == FunctionKind.Constructor &&
            TypeIdentity.AreSame(fieldTarget.Field.ContainingType, _function.ContainingType) &&
            _constructorReferenceFields.Contains(fieldTarget.Field) &&
            !_boundReferenceFields.Contains(fieldTarget.Field);
        if (isSimpleAssignment &&
            rawTarget is BoundMemberAccessExpression { Receiver: BoundThisExpression } referenceFieldTarget &&
            ContainsValueReferenceStorage(referenceFieldTarget.Field.Type) &&
            _function.FunctionKind == FunctionKind.Method)
        {
            _diagnostics.Report(GetLocation(syntax.Target),
                $"method '{_function.Name}' cannot replace reference-containing field '{referenceFieldTarget.Field.Name}' because caller lifetime provenance would change",
                DiagnosticIds.ReferenceProvenanceMutation);
            return new BoundErrorExpression();
        }
        BoundExpression effectiveTarget = initializesField ? rawTarget : DereferenceReference(target);
        if (isSimpleAssignment && TryGetStorageType(effectiveTarget.Type, out StorageTypeSymbol assignmentStorage))
            return BindStorageAssignment(syntax, effectiveTarget, assignmentStorage);
        if (isSimpleAssignment && TypeFacts.IsPinned(effectiveTarget.Type))
        {
            MovePlace? pinPlace = TryGetMovePlace(effectiveTarget, out MovePlace trackedPin) ? trackedPin : null;
            return BindPinInitializationAssignment(syntax, effectiveTarget,
                effectiveTarget.Type is PinTypeSymbol assignmentPin ? assignmentPin.ElementType : effectiveTarget.Type,
                pinPlace);
        }
        target = effectiveTarget;
        BoundExpression? compoundExpression = isSimpleAssignment ? null : ReadAtomicValue(BindExpressionWithExpectedType(
            syntax.Expression, target.Type is AtomicTypeSymbol targetAtomic ? targetAtomic.ElementType : target.Type));
        bool capturesTarget = false;
        if (!isSimpleAssignment && target.Type is not AtomicTypeSymbol &&
            TryBindUserOperator(GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind),
                [target, compoundExpression!], [syntax.Target, syntax.Expression], syntax,
                syntax.OperatorToken.Location, out BoundExpression? compoundResult))
        {
            compoundExpression = CaptureOperatorOperand(DereferenceReference(compoundResult!), target);
            capturesTarget = true;
            isSimpleAssignment = true;
            RecordExceptionalReferenceBinding();
        }
        AtomicTypeSymbol? atomicTarget = target.Type as AtomicTypeSymbol;
        TypeSymbol assignmentValueType = atomicTarget?.ElementType ?? target.Type;
        MovePlace? assignedPlace = isSimpleAssignment && TryGetMovePlace(target, out MovePlace targetPlace)
            ? targetPlace
            : null;
        BoundExpression expression = compoundExpression ?? ReadAtomicValue(BindExpressionWithExpectedType(
            syntax.Expression, assignmentValueType));
        if (atomicTarget is not null && !AtomicTypeRules.SupportsOperations(atomicTarget.ElementType))
        {
            _diagnostics.Report(
                syntax.OperatorToken.Location,
                $"atomic operations are not yet available for type '{atomicTarget.ElementType.ToDisplayString()}'",
                DiagnosticIds.InvalidOperatorOperands);
        }
        if (isSimpleAssignment)
        {
            expression = ContextualizeConversion(expression, assignmentValueType, GetLocation(syntax.Expression));
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
            if (!TypeFacts.CanAssign(assignmentValueType, expression.Type))
            {
                ReportCannotConvert(GetLocation(syntax.Expression), expression.Type, assignmentValueType);
            }
        }
        else
        {
            SyntaxKind binaryOperator = GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind);
            ValidateIntegerOperation(target, binaryOperator, expression, syntax.OperatorToken.Location);
            bool supportedAtomicOperator = atomicTarget is null ||
                syntax.OperatorToken.Kind is SyntaxKind.PlusEqualsToken or SyntaxKind.MinusEqualsToken &&
                    TypeFacts.IsNumeric(assignmentValueType) ||
                syntax.OperatorToken.Kind is SyntaxKind.AmpersandEqualsToken or SyntaxKind.PipeEqualsToken or SyntaxKind.CaretEqualsToken &&
                    TypeFacts.IsInteger(assignmentValueType);
            TypeSymbol? resultType = supportedAtomicOperator
                ? GetBinaryResultType(assignmentValueType, binaryOperator, expression.Type)
                : null;
            if (!TypeIdentity.AreSame(resultType, assignmentValueType))
            {
                _diagnostics.Report(
                    syntax.OperatorToken.Location,
                    $"operator '{syntax.OperatorToken.Text}' is not defined for types '{target.Type.ToDisplayString()}' and '{expression.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidOperatorOperands);
            }
        }

        if (isSimpleAssignment && assignedPlace is not null)
        {
            if (assignedPlace.Fields.IsEmpty)
                ValidateDestructorAccessibility(assignedPlace.RootType, syntax.OperatorToken.Location);
            MarkPlaceReinitialized(assignedPlace);
        }
        if (isSimpleAssignment) MarkConstructorFieldAssigned(target);

        return new BoundAssignmentExpression(target, capturesTarget ? SyntaxKind.EqualsToken : syntax.OperatorToken.Kind, expression)
        {
            CapturesTarget = capturesTarget,
            IsRawPlacement = isSimpleAssignment && IsRawPointerPlacement(target),
        };
    }

    private static bool IsRawPointerPlacement(BoundExpression expression) => expression switch
    {
        BoundFullExpression full => IsRawPointerPlacement(full.Expression),
        BoundReferenceDereferenceExpression dereference => IsRawPointerPlacement(dereference.Reference),
        BoundLifetimeValueExpression value => IsRawPointerPlacement(value.Source),
        BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken, Operand.Type: PointerTypeSymbol } => true,
        BoundIndexExpression { Receiver.Type: PointerTypeSymbol } => true,
        _ => false,
    };

    private BoundExpression BindSwapExpression(SwapExpressionSyntax syntax)
    {
        BoundExpression left = BindExpression(syntax.Left);
        BoundExpression right = BindExpression(syntax.Right);
        if (left is BoundErrorExpression || right is BoundErrorExpression)
            return new BoundErrorExpression();

        bool valid = true;
        if (!IsAddressable(left) || !IsWritable(left))
        {
            _diagnostics.Report(GetLocation(syntax.Left), "left side of swap must be a writable storage location",
                DiagnosticIds.InvalidAssignmentTarget);
            valid = false;
        }
        if (!IsAddressable(right) || !IsWritable(right))
        {
            _diagnostics.Report(GetLocation(syntax.Right), "right side of swap must be a writable storage location",
                DiagnosticIds.InvalidAssignmentTarget);
            valid = false;
        }

        AtomicTypeSymbol? leftAtomic = left.Type as AtomicTypeSymbol;
        AtomicTypeSymbol? rightAtomic = right.Type as AtomicTypeSymbol;
        if (leftAtomic is not null && rightAtomic is not null)
        {
            _diagnostics.Report(syntax.OperatorToken.Location,
                "swap may contain at most one atomic operand; two atomic locations cannot be exchanged as one transaction",
                DiagnosticIds.AtomicToAtomicSwap);
            valid = false;
        }
        else
        {
            AtomicTypeSymbol? atomic = leftAtomic ?? rightAtomic;
            TypeSymbol leftValueType = leftAtomic?.ElementType ?? left.Type;
            TypeSymbol rightValueType = rightAtomic?.ElementType ?? right.Type;
            if (!TypeIdentity.AreSame(leftValueType, rightValueType) ||
                atomic is not null && !AtomicTypeRules.SupportsOperations(atomic.ElementType))
            {
                _diagnostics.Report(syntax.OperatorToken.Location,
                    $"operator '<->' is not defined for types '{left.Type.ToDisplayString()}' and '{right.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidOperatorOperands);
                valid = false;
            }
            else if (TypeFacts.ContainsAtomicStorage(leftValueType))
            {
                _diagnostics.Report(syntax.OperatorToken.Location,
                    $"cannot swap '{leftValueType.ToDisplayString()}' because it contains atomic storage that cannot be implicitly relocated",
                    DiagnosticIds.AtomicStorageNotRelocatable);
                valid = false;
            }
            else if (!TypeFacts.CanRelocate(leftValueType))
            {
                _diagnostics.Report(syntax.OperatorToken.Location,
                    $"cannot swap '{leftValueType.ToDisplayString()}' because its address is pinned",
                    DiagnosticIds.PinnedRelocation);
                valid = false;
            }
        }

        if (!valid) return new BoundErrorExpression();
        return new BoundSwapExpression(left, right);
    }

    private BoundExpression BindCompareExchangeExpression(CompareExchangeExpressionSyntax syntax)
    {
        BoundExpression target = BindExpression(syntax.Target);
        BoundExpression expected = BindExpression(syntax.Expected);
        BoundExpression desired = BindExpression(syntax.Desired);
        if (target is BoundErrorExpression || expected is BoundErrorExpression || desired is BoundErrorExpression)
            return new BoundErrorExpression();

        if (target.Type is not AtomicTypeSymbol atomic)
        {
            _diagnostics.Report(GetLocation(syntax.Target),
                $"compare-exchange target must be a writable atomic<T> storage location, not '{target.Type.ToDisplayString()}'",
                DiagnosticIds.CompareExchangeRequiresAtomicTarget);
            return new BoundErrorExpression();
        }

        bool valid = true;
        if (!IsAddressable(target) || !IsWritable(target))
        {
            _diagnostics.Report(GetLocation(syntax.Target),
                "compare-exchange target must be a writable atomic<T> storage location",
                DiagnosticIds.CompareExchangeRequiresAtomicTarget);
            valid = false;
        }
        if (!AtomicTypeRules.SupportsOperations(atomic.ElementType))
        {
            _diagnostics.Report(syntax.ColonToken.Location,
                $"compare-exchange is not yet available for atomic<{atomic.ElementType.ToDisplayString()}>",
                DiagnosticIds.InvalidOperatorOperands);
            valid = false;
        }

        expected = ContextualizeConversion(ReadAtomicValue(expected), atomic.ElementType, GetLocation(syntax.Expected));
        desired = ContextualizeConversion(ReadAtomicValue(desired), atomic.ElementType, GetLocation(syntax.Desired));
        SetConvertedType(syntax.Expected, expected.Type);
        SetConvertedType(syntax.Desired, desired.Type);
        if (!TypeFacts.CanAssign(atomic.ElementType, expected.Type) ||
            !TypeFacts.CanAssign(atomic.ElementType, desired.Type))
        {
            _diagnostics.Report(syntax.ArrowToken.Location,
                $"compare-exchange expected and desired values must both have type '{atomic.ElementType.ToDisplayString()}'",
                DiagnosticIds.CompareExchangeOperandTypeMismatch);
            valid = false;
        }
        if (!valid) return new BoundErrorExpression();
        return new BoundCompareExchangeExpression(target, expected, desired);
    }

    private BoundExpression BindStorageAssignment(
        AssignmentExpressionSyntax syntax,
        BoundExpression target,
        StorageTypeSymbol storage)
    {
        TextLocation targetLocation = GetLocation(syntax.Target);
        if (!IsAddressable(target) || !IsWritable(target))
        {
            _diagnostics.Report(targetLocation, "left side of assignment must be writable",
                DiagnosticIds.InvalidAssignmentTarget);
            return new BoundErrorExpression();
        }
        MovePlace? place = TryGetMovePlace(target, out MovePlace storagePlace) ? storagePlace : null;

        BoundExpression source = BindExpression(syntax.Expression);
        BoundExpression construction = BindDestinationConstruction(target, storage.ElementType, source,
            syntax.Expression, syntax.OperatorToken.Location);
        if (construction is BoundErrorExpression) return construction;
        ValidateDestructorAccessibility(storage.ElementType, targetLocation);
        MarkConstructorFieldAssigned(target);
        return construction;
    }

    private BoundExpression BindPinInitializationAssignment(
        AssignmentExpressionSyntax syntax,
        BoundExpression target,
        TypeSymbol valueType,
        MovePlace? place)
    {
        TextLocation targetLocation = GetLocation(syntax.Target);
        if (!IsAddressable(target) || !IsWritable(target))
        {
            _diagnostics.Report(targetLocation, "left side of assignment must be writable",
                DiagnosticIds.InvalidAssignmentTarget);
            return new BoundErrorExpression();
        }
        BoundExpression source = BindExpression(syntax.Expression);
        BoundExpression construction = BindDestinationConstruction(target, valueType, source,
            syntax.Expression, syntax.OperatorToken.Location);
        if (construction is BoundErrorExpression) return construction;
        ValidateDestructorAccessibility(valueType, targetLocation);
        MarkConstructorFieldAssigned(target);
        if (place is not null) MarkPlaceReinitialized(place);
        return construction;
    }

    private void MarkConstructorFieldAssigned(BoundExpression target)
    {
        if (target is BoundMemberAccessExpression { Receiver: BoundThisExpression } assignedField &&
            _constructorReferenceFields.Contains(assignedField.Field))
        {
            _boundReferenceFields.Add(assignedField.Field);
        }
    }

    private static bool IsRuntimeTrackedScalarProjection(BoundExpression expression) => expression switch
    {
        BoundVariableExpression { Variable: LocalVariableSymbol or ParameterSymbol } => true,
        BoundMemberAccessExpression { IsPointerAccess: false } member =>
            IsRuntimeTrackedScalarProjection(member.Receiver),
        BoundLifetimeValueExpression value => IsRuntimeTrackedScalarProjection(value.Source),
        _ => false,
    };

    private HashSet<FieldSymbol> CloneReferenceFieldBindings() => [.. _boundReferenceFields];

    private void RestoreReferenceFieldBindings(IEnumerable<FieldSymbol> variables)
    {
        _boundReferenceFields.Clear();
        _boundReferenceFields.UnionWith(variables);
    }

    private void MarkPlaceReinitialized(MovePlace place)
    {
        foreach (ArgumentBindingTransaction transaction in _argumentBindingTransactions)
            transaction.RecordSubsequentPlaceMutation(
                place, moveLocation: null, deferredDependent: false);
    }

    private bool IsOnlyDeferredMoveConflict(MovePlace place) =>
        _argumentBindingTransactions.Any(transaction => transaction.HasDeferredMoveContribution(place));
    private BoundExpression BindMemberAccessExpression(MemberAccessExpressionSyntax syntax)
    {
        if (syntax.MemberToken.IsMissing)
        {
            if (TryResolveStaticTypeReceiver(syntax, out TypeSymbol? receiverType) &&
                !TypeIdentity.AreSame(receiverType!, BuiltinTypes.Error))
            {
                RecordStaticReceiver(syntax.Receiver, receiverType!);
            }
            else
                _ = BindFieldReceiver(syntax.Receiver);
            _semanticInfo.Symbols[syntax] = new SymbolInfo(null, [], CandidateReason.Incomplete);
            return new BoundErrorExpression();
        }
        if (TryResolveStaticTypeReceiver(syntax, out TypeSymbol? resolvedStaticType))
        {
            if (TypeIdentity.AreSame(resolvedStaticType!, BuiltinTypes.Error))
                return new BoundErrorExpression();
            if (resolvedStaticType is EnumTypeSymbol enumeration)
            {
                ConstantSymbol? member = enumeration.FindMember(syntax.MemberToken.Text);
                if (member?.BoundValue is BoundExpression value)
                {
                    RecordStaticReceiver(syntax.Receiver, enumeration);
                    _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(member);
                    return value;
                }
                FieldSymbol? enumField = enumeration.FindStaticField(syntax.MemberToken.Text);
                if (enumField is not null)
                {
                    if (!IsAccessible(enumField))
                    {
                        RecordCandidates(syntax, null, [enumField], CandidateReason.Inaccessible);
                        _diagnostics.Report(syntax.MemberToken.Location,
                            $"static field '{enumField.Name}' is inaccessible from this context",
                            DiagnosticIds.InaccessibleSymbol);
                        return new BoundErrorExpression();
                    }
                    RecordStaticReceiver(syntax.Receiver, enumeration);
                    RecordSymbolAndType(syntax, enumField, enumField.Type);
                    return new BoundStaticFieldExpression(enumField);
                }
                _diagnostics.Report(syntax.MemberToken.Location, $"enum '{enumeration.Name}' has no valid member '{syntax.MemberToken.Text}'",
                    DiagnosticIds.UnknownEnumMember);
                return new BoundErrorExpression();
            }
            if (resolvedStaticType is DeclaredTypeSymbol staticType)
            {
                ConstantSymbol? constant = staticType.FindMember<ConstantSymbol>(syntax.MemberToken.Text);
                if (constant?.BoundValue is { } constantValue)
                {
                    RecordStaticReceiver(syntax.Receiver, staticType);
                    RecordSymbolAndType(syntax, constant, constant.Type);
                    return constantValue;
                }
                if (constant is not null)
                    return new BoundErrorExpression();

                FieldSymbol? staticField = staticType.FindStaticField(syntax.MemberToken.Text);
                if (staticField is not null)
                {
                    if (!IsAccessible(staticField))
                    {
                        RecordCandidates(syntax, null, [staticField], CandidateReason.Inaccessible);
                        _diagnostics.Report(syntax.MemberToken.Location, $"static field '{staticField.Name}' is private in struct '{staticField.ContainingType.Name}'",
                            DiagnosticIds.InaccessibleSymbol);
                        return new BoundErrorExpression();
                    }
                    RecordStaticReceiver(syntax.Receiver, staticType);
                    RecordSymbolAndType(syntax, staticField, staticField.Type);
                    return new BoundStaticFieldExpression(staticField);
                }
            }
        }

        return BindMemberAccessExpression(syntax, BindFieldReceiver(syntax.Receiver));
    }

    private BoundExpression BindMemberAccessExpression(
        MemberAccessExpressionSyntax syntax,
        BoundExpression receiver)
    {
        ArrayTypeSymbol? receiverArray = receiver.Type as ArrayTypeSymbol ??
            (receiver.Type as OwnershipTypeSymbol)?.ElementType as ArrayTypeSymbol;
        if (receiver.Type is WeakTypeSymbol)
        {
            _diagnostics.Report(syntax.MemberToken.Location,
                $"cannot access a value through '{receiver.Type.ToDisplayString()}' directly; use 'lock value' and check the returned shared owner first",
                DiagnosticIds.WeakDirectAccess);
            return new BoundErrorExpression();
        }

        if (receiverArray is { } array && syntax.OperatorToken.Kind == SyntaxKind.DotToken && syntax.MemberToken.Text is "Length" or "Rank")
        {
            SyntheticMemberSymbol member = _semanticInfo.GetArrayMembers(array)
                .Single(candidate => candidate.Name == syntax.MemberToken.Text);
            RecordSymbolAndType(syntax, member, member.Type);
            return new BoundArrayMetadataExpression(receiver, syntax.MemberToken.Text);
        }
        bool pointerAccess = syntax.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
        if (GetGenericReceiver(receiver.Type, pointerAccess) is GenericParameterSymbol genericParameter)
        {
            return BindGenericMemberGet(syntax, receiver, genericParameter, pointerAccess);
        }
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
            ? receiver.Type switch
            {
                PointerTypeSymbol pointer => pointer.ElementType as DeclaredTypeSymbol,
                UniqueTypeSymbol unique => unique.ElementType as DeclaredTypeSymbol,
                SharedTypeSymbol shared => shared.ElementType as DeclaredTypeSymbol,
                _ => null,
            }
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

        if (!IsAccessible(field, receiver, pointerAccess))
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
                 ResolveUnqualifiedTypeForExpression(dottedName[0].Text, dottedName[0].Location,
                     new DiagnosticBag()) is not null))
            {
                return null;
            }

            receiver = BindFieldReceiver(member.Receiver);
            pointerAccess = member.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
            if (GetGenericReceiver(receiver.Type, pointerAccess) is GenericParameterSymbol genericParameter)
            {
                return TryBindGenericPropertyAssignment(syntax, member, receiver, genericParameter,
                    pointerAccess, isSimpleAssignment);
            }
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

                BoundExpression interfaceValue = BindExpressionWithExpectedType(
                    syntax.Expression, interfaceSetter.Parameters[0].Type);
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
                ? receiver.Type switch
                {
                    PointerTypeSymbol pointer => pointer.ElementType as DeclaredTypeSymbol,
                    UniqueTypeSymbol unique => unique.ElementType as DeclaredTypeSymbol,
                    SharedTypeSymbol shared => shared.ElementType as DeclaredTypeSymbol,
                    _ => null,
                }
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
        if (!IsAccessible(property, receiver, pointerAccess))
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

        BoundExpression value = BindExpressionWithExpectedType(
            syntax.Expression, setter.Parameters[0].Type);
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
        if (!IsAccessible(property, receiver, isPointerAccess))
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
        {
            if (ContainsStaticStruct(type))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location,
                    "layout intrinsics are not defined for static structs",
                    DiagnosticIds.InvalidBaseType);
                return new BoundErrorExpression();
            }
            return new BoundTypeLayoutExpression(syntax.Keyword.Kind, type, null);
        }

        if (type is not StructTypeSymbol structType)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location, "offsetof requires a struct type",
                DiagnosticIds.OffsetOfRequiresStructType);
            return new BoundErrorExpression();
        }
        if (structType.IsStatic)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location,
                "offsetof is not defined for static structs",
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
            if (TryBindUserConversion(expression, targetType, syntax.CastKeyword.Location,
                explicitContext: true, out BoundExpression? conversion, syntax)) return conversion!;
            if (!TypeIdentity.AreSame(targetType, BuiltinTypes.Error) && !TypeIdentity.AreSame(expression.Type, BuiltinTypes.Error))
                _diagnostics.Report(syntax.CastKeyword.Location, $"cast from '{expression.Type.ToDisplayString()}' to '{targetType.Name}' is not a valid primitive cast",
                    DiagnosticIds.InvalidCast);
            return new BoundErrorExpression();
        }
        var result = new BoundCastExpression(expression, targetType);
        if (TypeFacts.IsCharacter(targetType) && TypeFacts.IsInteger(expression.Type) &&
            _constants.Fold(expression, out object? constant) == ConstantFoldStatus.Folded &&
            !UnicodeScalarFacts.IsValid(SemanticAnalyzer.ToInteger(constant)))
        {
            _diagnostics.Report(syntax.CastKeyword.Location,
                "compile-time integer-to-char cast is not a valid Unicode scalar value",
                DiagnosticIds.InvalidUnicodeScalarCast);
            return new BoundErrorExpression();
        }
        return result;
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
        ImmutableArray<BoundExpression> arguments = BindTransferredArguments(syntax.Arguments,
            (argument, _) => BindDeferredContextualArgument(argument));

        if (GetGenericReceiver(receiver.Type, pointerAccess: false) is GenericParameterSymbol genericParameter)
            return BindGenericIndexerGet(syntax, receiver, genericParameter, arguments);

        if (receiver.Type is DeclaredTypeSymbol structType && structType is not InterfaceTypeSymbol)
        {
            bool receiverIsReadonly = IsAddressable(receiver) && !IsWritable(receiver);
            IndexerSymbol[] indexerCandidates = structType.LookupMembers("this").OfType<IndexerSymbol>()
                .DistinctBy(indexer => TypeSignature.Indexer(indexer.Parameters, indexer.IsReadonly))
                .Where(candidate => candidate.Getter is not null)
                .ToArray();
            IndexerSymbol? indexer = ResolveIndexer(
                indexerCandidates,
                arguments,
                syntax.OpenBracketToken.Location,
                structType.Name,
                syntax,
                receiverIsReadonly);
            if (indexer is null)
            {
                if (receiverIsReadonly && indexerCandidates.Length != 0 &&
                    indexerCandidates.All(candidate => !candidate.IsReadonly))
                    _diagnostics.Report(syntax.OpenBracketToken.Location,
                        $"indexer on '{structType.Name}' cannot be read through a readonly receiver because its getter is mutable",
                        DiagnosticIds.MutableGetterOnReadonlyReceiver);
                return new BoundErrorExpression();
            }
            RecordSymbolAndType(syntax, indexer, indexer.Type);
            if (!IsAccessible(indexer, receiver, pointerAccess: false))
            {
                RecordCandidates(syntax, null, [indexer], CandidateReason.Inaccessible);
                _diagnostics.Report(syntax.OpenBracketToken.Location, $"indexer is private in struct '{indexer.ContainingType.Name}'",
                    DiagnosticIds.InaccessibleSymbol);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            if (indexer.Getter is not FunctionSymbol getter)
            {
                _diagnostics.Report(syntax.OpenBracketToken.Location, "indexer does not declare a getter",
                    DiagnosticIds.MissingAccessor);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            arguments = ValidateFunctionArguments(getter, arguments, syntax.Arguments, syntax.OpenBracketToken.Location);
            return new BoundMethodCallExpression(receiver, getter, arguments, IsPointerAccess: false);
        }

        if (receiver.Type is InterfaceTypeSymbol interfaceType)
        {
            bool receiverIsReadonly = IsReadonlyReceiver(receiver, pointerAccess: false);
            InterfaceIndexerSymbol[] indexerCandidates = interfaceType.AllIndexers
                .Where(candidate => candidate.Getter is not null)
                .ToArray();
            InterfaceIndexerSymbol? indexer = ResolveIndexer(
                indexerCandidates,
                arguments,
                syntax.OpenBracketToken.Location,
                interfaceType.Name,
                syntax,
                receiverIsReadonly);
            if (indexer is null)
            {
                if (receiverIsReadonly && indexerCandidates.Length != 0 &&
                    indexerCandidates.All(candidate => !candidate.IsReadonly))
                    _diagnostics.Report(syntax.OpenBracketToken.Location,
                        $"indexer on interface '{interfaceType.Name}' cannot be read through a readonly receiver because its getter is mutable",
                        DiagnosticIds.MutableGetterOnReadonlyReceiver);
                return new BoundErrorExpression();
            }
            RecordSymbolAndType(syntax, indexer, indexer.Type);
            FunctionSymbol getter = indexer.Getter!;
            arguments = ValidateFunctionArguments(getter, arguments, syntax.Arguments, syntax.OpenBracketToken.Location);
            return new BoundInterfaceMethodCallExpression(receiver, interfaceType, getter, arguments, IsPointerAccess: false);
        }

        ArrayTypeSymbol? ownedArray = receiver.Type is OwnershipTypeSymbol { ElementType: ArrayTypeSymbol ownershipArray }
            and not WeakTypeSymbol ? ownershipArray
            : null;
        int requiredRank = receiver.Type is ArrayTypeSymbol rankedArray ? rankedArray.Rank : ownedArray?.Rank ?? 1;
        if (arguments.Length != requiredRank)
        {
            _diagnostics.Report(syntax.OpenBracketToken.Location, $"array or pointer indexing requires {requiredRank} index value(s)",
                DiagnosticIds.IndexArityMismatch);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        if (arguments.Any(argument => argument is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression))
        {
            foreach (int argumentIndex in Enumerable.Range(0, arguments.Length).Where(index =>
                         arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression))
                _diagnostics.Report(GetLocation(syntax.Arguments[argumentIndex]),
                    "array or pointer index requires an integer expression; callable expressions require an indexer parameter context",
                    DiagnosticIds.IndexMustBeInteger);
            RollbackUnmaterializedContextualArguments(arguments);
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
            UniqueTypeSymbol { ElementType: ArrayTypeSymbol array } => array.ElementType,
            SharedTypeSymbol { ElementType: ArrayTypeSymbol array } => array.ElementType,
            _ => null,
        };

        if (elementType is null)
        {
            if (!TypeIdentity.AreSame(receiver.Type, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.OpenBracketToken.Location, $"type '{receiver.Type.ToDisplayString()}' cannot be indexed",
                    DiagnosticIds.TypeNotIndexable);
            }

            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        if (TypeIdentity.AreSame(elementType, BuiltinTypes.Void))
        {
            _diagnostics.Report(syntax.OpenBracketToken.Location, "cannot index a void pointer",
                DiagnosticIds.VoidPointerIndex);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        return new BoundIndexExpression(receiver, index, elementType) { Indices = arguments };
    }

    private BoundExpression? TryBindIndexerAssignment(AssignmentExpressionSyntax syntax, bool isSimpleAssignment)
    {
        if (syntax.Target is not IndexExpressionSyntax target)
            return null;

        BoundExpression receiver = ExposeLifetimeValue(BindExpression(target.Receiver), target.Receiver);
        if (GetGenericReceiver(receiver.Type, pointerAccess: false) is GenericParameterSymbol genericParameter)
            return BindGenericIndexerAssignment(syntax, target, receiver, genericParameter, isSimpleAssignment);
        if (receiver.Type is not DeclaredTypeSymbol)
            return null;
        ImmutableArray<BoundExpression> indices = BindTransferredArguments(target.Arguments,
            (argument, _) => BindDeferredContextualArgument(argument));
        if (receiver.Type is DeclaredTypeSymbol structType && structType is not InterfaceTypeSymbol)
        {
            IndexerSymbol? indexer = ResolveIndexer(
                structType.LookupMembers("this").OfType<IndexerSymbol>()
                    .DistinctBy(indexer => TypeSignature.Indexer(indexer.Parameters, indexer.IsReadonly)).Where(candidate =>
                    candidate.Setter is not null && (isSimpleAssignment || candidate.Getter is not null)),
                indices,
                target.OpenBracketToken.Location,
                structType.Name,
                target,
                receiverIsReadonly: false);
            if (indexer is null)
                return new BoundErrorExpression();
            RecordSymbolAndType(target, indexer, indexer.Type);
            _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(indexer);
            if (!IsAccessible(indexer, receiver, pointerAccess: false))
            {
                RecordCandidates(target, null, [indexer], CandidateReason.Inaccessible);
                _diagnostics.Report(target.OpenBracketToken.Location, $"indexer is private in struct '{indexer.ContainingType.Name}'",
                    DiagnosticIds.InaccessibleSymbol);
                RollbackUnmaterializedContextualArguments(indices);
                return new BoundErrorExpression();
            }
            if (!IsAddressable(receiver) || !IsWritable(receiver))
            {
                _diagnostics.Report(target.OpenBracketToken.Location, "indexer cannot be assigned through a readonly receiver",
                    DiagnosticIds.WriteThroughReadonlyReceiver);
                RollbackUnmaterializedContextualArguments(indices);
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

            BoundExpression value = BindExpressionWithExpectedType(syntax.Expression, setter.Parameters[^1].Type);
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
            target,
            receiverIsReadonly: false);
        if (interfaceIndexer is null)
            return new BoundErrorExpression();
        RecordSymbolAndType(target, interfaceIndexer, interfaceIndexer.Type);
        _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(interfaceIndexer);
        if (!IsAddressable(receiver) || !IsWritable(receiver))
        {
            _diagnostics.Report(target.OpenBracketToken.Location, "indexer cannot be assigned through a readonly receiver",
                DiagnosticIds.WriteThroughReadonlyReceiver);
            RollbackUnmaterializedContextualArguments(indices);
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

        BoundExpression interfaceValue = BindExpressionWithExpectedType(
            syntax.Expression, interfaceSetter.Parameters[^1].Type);
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
        bool hasNoncopyableArgument = false;
        for (int index = 0; index < Math.Min(arguments.Length, getter.Parameters.Length); index++)
        {
            TypeSymbol parameterType = getter.Parameters[index].Type;
            if (parameterType is ReferenceTypeSymbol || TypeFacts.GetCopyabilityFailure(parameterType) is null)
                continue;
            hasNoncopyableArgument = true;
            _diagnostics.Report(GetLocation(argumentSyntax[index]),
                $"compound indexer assignment cannot reuse noncopyable argument type '{parameterType.ToDisplayString()}' for both getter and setter",
                DiagnosticIds.ValueNotCopyable);
        }
        if (hasNoncopyableArgument)
        {
            RollbackUnmaterializedContextualArguments(arguments);
            _ = BindExpression(syntax.Expression);
            return new BoundErrorExpression();
        }
        arguments = ValidateFunctionArguments(
            getter,
            arguments,
            argumentSyntax,
            GetLocation(syntax.Target));
        BoundExpression value = BindExpression(syntax.Expression);
        SyntaxKind binaryOperator = GetBinaryOperatorForCompoundAssignment(syntax.OperatorToken.Kind);
        var current = new BoundMethodCallExpression(receiver, getter, arguments, isPointerAccess);
        if (TryBindUserOperator(binaryOperator, [current, value], [syntax.Target, syntax.Expression], syntax,
            syntax.OperatorToken.Location, out BoundExpression? compoundResult))
        {
            BoundExpression converted = ContextualizeConversion(DereferenceReference(compoundResult!), getter.ReturnType,
                syntax.OperatorToken.Location);
            if (!TypeFacts.CanAssign(getter.ReturnType, converted.Type))
                ReportCannotConvert(syntax.OperatorToken.Location, converted.Type, getter.ReturnType);
            return new BoundCompoundAccessorAssignmentExpression(receiver, getter, setter, arguments,
                binaryOperator, CaptureOperatorOperand(converted, current), isPointerAccess, interfaceType)
                { UsesUserOperator = true };
        }
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

            type = ResolveUnqualifiedTypeForExpression(identifier, name.IdentifierToken.Location, _diagnostics);
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

        if (structType.IsStatic)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location,
                $"static struct '{structType.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);
            return new BoundErrorExpression();
        }

        bool isAbstract = structType.IsAbstract;
        if (isAbstract)
            _diagnostics.Report(syntax.Type.NameToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);

        ImmutableArray<BoundExpression> arguments = BindTransferredArguments(syntax.Arguments,
            (argument, _) => BindDeferredContextualArgument(argument));
        if (isAbstract)
        {
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        arguments = ValidatePositionalArguments(structType, arguments, syntax.Arguments, syntax.Type.NameToken.Location);
        if (arguments.FirstOrDefault(argument => argument is BoundErrorExpression) is { } error)
            return error;
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
        if (ContainsStaticStruct(elementType))
        {
            _diagnostics.Report(elementLocation, "static structs cannot be used as array elements",
                DiagnosticIds.InvalidBaseType);
            return new BoundErrorExpression();
        }
        ValidateArrayElementType(elementType, elementLocation);
        if (storage == ArrayStorageKind.Stack)
        {
            _function.HasStackArrays = true;
            ValidateDestructorAccessibility(elementType, allocationLocation);
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
        if (syntax.Target is NameExpressionSyntax capturedName &&
            ReportUnsupportedLambdaCapture(capturedName.IdentifierToken)) return new BoundErrorExpression();
        string? coreOperation = syntax.TypeArguments is null && syntax.Target is NameExpressionSyntax coreName
            ? coreName.IdentifierToken.Text : null;
        bool isLifetimeOperation = coreOperation is "destruct" or "delete";
        bool isRawAllocation = coreOperation is "malloc" or "calloc";
        BoundExpression? expressionTarget = syntax.TypeArguments is null &&
            syntax.Target is not (NameExpressionSyntax or MemberAccessExpressionSyntax)
                ? BindExpression(syntax.Target) : null;
        ImmutableArray<BoundExpression> arguments = BindTransferredArguments(
            syntax.Arguments,
            (argument, index) =>
            {
                if (isLifetimeOperation && index == 0)
                    return BindLifetimeInvalidationOperand(argument);
                if (isRawAllocation)
                    return BindExpressionWithExpectedType(argument, BuiltinTypes.NUInt);
                if (TryGetLambdaExpression(argument, out LambdaExpressionSyntax lambda))
                    return BindDeferredLambdaExpression(lambda, argument);
                if (TryGetNamedFunctionExpression(argument, out ExpressionSyntax function))
                    return new BoundUnboundFunctionExpression(function);
                TypeSymbol? expectedType = (expressionTarget?.Type switch
                {
                    FunctionPointerTypeSymbol expressionPointer when index < expressionPointer.ParameterTypes.Length => expressionPointer.ParameterTypes[index],
                    FunctionValueTypeSymbol expressionValue when index < expressionValue.ParameterTypes.Length => expressionValue.ParameterTypes[index],
                    _ => null,
                }) ?? GetCallArgumentExpectedType(syntax, index);
                if (expectedType is not null)
                    return BindExpressionWithExpectedType(argument, expectedType);
                return BindExpression(argument);
            });
        ImmutableArray<BoundExpression> originalArguments = arguments;
        BoundExpression? recoveryReceiver = expressionTarget;
        BoundExpression result = BindCore();
        if (result is not BoundErrorExpression && !HasRejectedDeferredContextualArguments(originalArguments) &&
            !BoundTree.DescendantsAndSelf(result).Any(node => node is BoundErrorExpression)) return result;
        IEnumerable<BoundExpression> recovery = originalArguments.Where(argument =>
            !BoundTree.DescendantsAndSelf(argument).Any(node => node is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression ||
                node is BoundFunctionValueExpression { InvokeFunction.IsLambda: true } callable &&
                    !_semanticInfo.LambdaFunctions.Any(function => ReferenceEquals(function.Symbol, callable.InvokeFunction))) &&
            (!_argumentBindingCandidates.TryGetValue(UnwrapDirectTransferredExpression(argument), out ArgumentBindingTransaction? transaction) ||
                !transaction.IsRejectedRecoveryMove(argument)));
        return new BoundErrorExpression
        {
            RecoveryArguments = (recoveryReceiver is null ? recovery : recovery.Prepend(recoveryReceiver)).ToImmutableArray(),
        };

        BoundExpression BindCore()
        {
        if (coreOperation is "resolve" or "reject")
            return BindCompletionOperator(coreOperation == "resolve" ? OperatorKind.Resolve : OperatorKind.Reject,
                arguments, syntax.Arguments, syntax, GetLocation(syntax));
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
            if (qualifiedCall is not null) return qualifiedCall;
            if (IsStaticFunctionMemberTarget(memberTarget))
            {
                BoundExpression member = BindExpression(memberTarget);
                if (member.Type is FunctionPointerTypeSymbol memberPointer)
                    return BindIndirectCall(member, memberPointer, arguments, syntax.Arguments,
                        memberTarget.MemberToken.Location, incomplete, completedArgumentCount);
                return BindFunctionValueCall(member, (FunctionValueTypeSymbol)member.Type, arguments,
                    syntax.Arguments, memberTarget.MemberToken.Location, incomplete, completedArgumentCount);
            }
            BoundExpression receiver = BindFieldReceiver(memberTarget.Receiver);
            recoveryReceiver = receiver;
            if (IsFunctionMemberTarget(receiver, memberTarget))
            {
                BoundExpression member = BindMemberAccessExpression(memberTarget, receiver);
                if (member.Type is FunctionPointerTypeSymbol memberPointer)
                    return BindIndirectCall(member, memberPointer, arguments, syntax.Arguments,
                        memberTarget.MemberToken.Location, incomplete, completedArgumentCount);
                return BindFunctionValueCall(member, (FunctionValueTypeSymbol)member.Type, arguments,
                    syntax.Arguments, memberTarget.MemberToken.Location, incomplete, completedArgumentCount);
            }
            return BindMethodCallExpression(memberTarget, arguments, syntax.Arguments, syntax, receiver);
        }

        if (syntax.Target is NameExpressionSyntax variableName &&
            (_scope.Lookup(variableName.IdentifierToken.Text)?.Type is FunctionPointerTypeSymbol or FunctionValueTypeSymbol ||
             _function.ContainingType?.FindInstanceField(variableName.IdentifierToken.Text)?.Type is FunctionPointerTypeSymbol or FunctionValueTypeSymbol))
        {
            BoundExpression target = BindNameExpression(variableName);
            return target.Type is FunctionPointerTypeSymbol pointer
                ? BindIndirectCall(target, pointer, arguments, syntax.Arguments,
                    variableName.IdentifierToken.Location, incomplete, completedArgumentCount)
                : BindFunctionValueCall(target, (FunctionValueTypeSymbol)target.Type, arguments,
                    syntax.Arguments, variableName.IdentifierToken.Location, incomplete, completedArgumentCount);
        }

        if (syntax.Target is not NameExpressionSyntax name)
        {
            BoundExpression target = expressionTarget!;
            if (target.Type is FunctionPointerTypeSymbol pointer)
                return BindIndirectCall(target, pointer, arguments, syntax.Arguments,
                    GetLocation(syntax.Target), incomplete, completedArgumentCount);
            if (target.Type is FunctionValueTypeSymbol functionValueType)
                return BindFunctionValueCall(target, functionValueType, arguments, syntax.Arguments,
                    GetLocation(syntax.Target), incomplete, completedArgumentCount);
            if (target is BoundErrorExpression)
            {
                RollbackUnmaterializedContextualArguments(arguments);
                return target;
            }
            RecordCandidates(syntax, null, [], CandidateReason.NotInvocable);
            RecordCandidates(syntax.Target, null, [], CandidateReason.NotInvocable);
            _diagnostics.Report(GetLocation(syntax.Target), "call target must be a function, method, or struct name",
                DiagnosticIds.InvalidCallTarget);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        if (name.IdentifierToken.Text == "destruct")
            return BindLifetimeOperation(syntax, name, arguments);
        if (name.IdentifierToken.Text == "delete")
            return BindDeleteExpression(syntax, name, arguments);
        if (name.IdentifierToken.Text is "malloc" or "calloc")
            return BindRawAllocationExpression(syntax, name, arguments);

        TypeSymbol? callTargetType = ResolveUnqualifiedTypeForExpression(
            name.IdentifierToken.Text,
            name.IdentifierToken.Location,
            _diagnostics);
        if (callTargetType is GenericParameterSymbol genericParameter)
            return BindGenericConstructionExpression(syntax, genericParameter, arguments);
        if (callTargetType is StructTypeSymbol structType)
        {
            if (structType.IsStatic)
            {
                _diagnostics.Report(name.IdentifierToken.Location,
                    $"static struct '{structType.Name}' cannot be instantiated",
                    DiagnosticIds.AbstractInstantiation);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            if (structType.IsAbstract)
            {
                _diagnostics.Report(name.IdentifierToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated",
                    DiagnosticIds.AbstractInstantiation);
                RollbackUnmaterializedContextualArguments(arguments);
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

            if (!IsAccessible(constructor))
            {
                RecordCandidates(syntax.Target, null, structType.Constructors, CandidateReason.Inaccessible);
                _diagnostics.Report(name.IdentifierToken.Location, $"constructor '{structType.Name}' is private",
                    DiagnosticIds.InaccessibleSymbol);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }

            arguments = ValidateFunctionArguments(constructor, arguments, syntax.Arguments, name.IdentifierToken.Location,
                incomplete ? completedArgumentCount : null);
            return new BoundConstructorCallExpression(structType, constructor, arguments);
        }

        if (_function.ContainingType is { } containingType)
        {
            FunctionSymbol[] staticMethodCandidates = _function.IsStatic
                ? GetMethodOverloads(containingType, name.IdentifierToken.Text, isStatic: true)
                : [];
            FunctionSymbol[] methodCandidates = staticMethodCandidates.Length != 0
                ? staticMethodCandidates
                : GetMethodOverloads(containingType, name.IdentifierToken.Text, isStatic: false);
            FunctionSymbol? method = methodCandidates.Length == 0 ? null : ResolveCallableOverload(
                methodCandidates, arguments, name.IdentifierToken.Location,
                $"method '{containingType.Name}.{name.IdentifierToken.Text}'", syntax.Target,
                out _, _function.IsReadonly, incomplete ? completedArgumentCount : null);
            if (method is not null)
            {
                if (_function.IsReadonly && !method.IsReadonly)
                {
                    RecordCandidates(syntax.Target, null, methodCandidates, CandidateReason.Inaccessible);
                    _diagnostics.Report(name.IdentifierToken.Location,
                        $"readonly method '{_function.Name}' cannot call mutable method '{method.Name}' through 'this'",
                        DiagnosticIds.MutableMethodOnReadonlyReceiver);
                    RollbackUnmaterializedContextualArguments(arguments);
                    return new BoundErrorExpression();
                }
                if (_bindingBaseConstructorArguments)
                {
                    _diagnostics.Report(name.IdentifierToken.Location, "the derived object cannot be used in base constructor arguments",
                        DiagnosticIds.DerivedInstanceInBaseConstructorArguments);
                    RollbackUnmaterializedContextualArguments(arguments);
                    return new BoundErrorExpression();
                }
                if (!IsAccessible(method))
                {
                    RecordCandidates(syntax.Target, null, methodCandidates, CandidateReason.Inaccessible);
                    _diagnostics.Report(name.IdentifierToken.Location, $"method '{method.Name}' is private in struct '{method.ContainingType!.Name}'",
                        DiagnosticIds.InaccessibleSymbol);
                    RollbackUnmaterializedContextualArguments(arguments);
                    return new BoundErrorExpression();
                }
                if (method.IsStatic)
                {
                    if (!_function.IsStatic)
                    {
                        _diagnostics.Report(name.IdentifierToken.Location,
                            $"static method '{method.Name}' must be accessed through type '{containingType.Name}'",
                            DiagnosticIds.StaticMethodRequiresTypeReceiver);
                        RollbackUnmaterializedContextualArguments(arguments);
                        return new BoundErrorExpression();
                    }
                    arguments = ValidateFunctionArguments(method, arguments, syntax.Arguments,
                        name.IdentifierToken.Location,
                        incomplete ? completedArgumentCount : null);
                    return new BoundCallExpression(method, arguments);
                }
                if (_function.IsStatic)
                {
                    _diagnostics.Report(name.IdentifierToken.Location, $"static method '{_function.Name}' cannot call instance method '{method.Name}' without an explicit instance",
                        DiagnosticIds.StaticContextInstanceMethodCall);
                    RollbackUnmaterializedContextualArguments(arguments);
                    return new BoundErrorExpression();
                }
                arguments = ValidateFunctionArguments(method, arguments, syntax.Arguments, name.IdentifierToken.Location,
                    incomplete ? completedArgumentCount : null);
                PointerTypeSymbol thisType = _fileScope.TypeFactory.PointerTo(containingType, isReadonly: _function.IsReadonly);
                return new BoundMethodCallExpression(
                    new BoundThisExpression(containingType, thisType),
                    method,
                    arguments,
                    IsPointerAccess: true);
            }
            if (methodCandidates.Length != 0)
                return new BoundErrorExpression();
        }

        FunctionSymbol[] functionCandidates = _fileScope.ResolveFunctions(name.IdentifierToken.Text).ToArray();
        FunctionSymbol? function = functionCandidates.Length == 0 ? null : ResolveCallableOverload(
            functionCandidates, arguments, name.IdentifierToken.Location,
            $"function '{name.IdentifierToken.Text}'", syntax.Target, out _,
            completedArgumentCount: incomplete ? completedArgumentCount : null);
        if (function is null)
        {
            if (functionCandidates.Length == 0)
            {
                _diagnostics.Report(name.IdentifierToken.Location, $"unknown function '{name.IdentifierToken.Text}'",
                    DiagnosticIds.UnknownFunction);
            }
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        if (!IsAccessible(function))
        {
            RecordCandidates(syntax.Target, null, functionCandidates, CandidateReason.Inaccessible);
            _diagnostics.Report(name.IdentifierToken.Location,
                $"function '{function.Name}' is private in namespace '{function.ContainingNamespace.FullName}'",
                DiagnosticIds.InaccessibleSymbol);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        if (function.IsGenericDefinition)
        {
            bool inferenceSucceeded = TryInferGenericTypeArguments(function, arguments,
                out ImmutableArray<TypeSymbol> inferredArguments);
            if (inferenceSucceeded &&
                inferredArguments.Any(ContainsGenericParameter))
                return BindOpenGenericCall(syntax, function, inferredArguments, arguments,
                    name.IdentifierToken.Location);
            GenericFunctionSpecializer? callSpecializer =
                GetTransactionalGenericSpecializer(arguments, out _);
            FunctionSymbol? specialized = callSpecializer is null || !inferenceSucceeded
                ? null
                : callSpecializer.GetOrCreate(function, inferredArguments,
                    name.IdentifierToken.Location);
            if (specialized is null)
            {
                if (!inferenceSucceeded)
                    _diagnostics.Report(name.IdentifierToken.Location,
                        $"type arguments for generic function '{function.Name}' could not be inferred",
                        DiagnosticIds.GenericSpecializationNotImplemented);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            RecordCandidates(syntax.Target, specialized, [function], CandidateReason.None);
            RecordTransactionalGeneratedSymbolRollback(arguments, syntax.Target);
            ImmutableArray<BoundExpression> deferredArguments = arguments;
            arguments = ValidateFunctionArguments(specialized, arguments, syntax.Arguments,
                name.IdentifierToken.Location, incomplete ? completedArgumentCount : null);
            if (HasRejectedDeferredContextualArguments(deferredArguments) ||
                HasFailedDeferredMaterialization(deferredArguments, arguments))
            {
                _semanticInfo.RollbackSyntax(syntax.Target);
                return new BoundErrorExpression();
            }
            return new BoundCallExpression(specialized, arguments);
        }

        CandidateReason functionReason = GetCallCandidateReason(function, arguments, incomplete, completedArgumentCount);
        RecordCandidates(syntax.Target, function, [function], functionReason);
        arguments = ValidateFunctionArguments(function, arguments, syntax.Arguments, name.IdentifierToken.Location,
            incomplete ? completedArgumentCount : null);
        return new BoundCallExpression(function, arguments);
        }
    }

    private ImmutableArray<BoundExpression> BindTransferredArguments(
        ImmutableArray<ExpressionSyntax> arguments,
        Func<ExpressionSyntax, int, BoundExpression>? bind = null)
    {
        var transaction = new ArgumentBindingTransaction();
        _argumentBindingTransactions.Push(transaction);
        try
        {
            var bound = ImmutableArray.CreateBuilder<BoundExpression>(arguments.Length);
            for (int index = 0; index < arguments.Length; index++)
            {
                BoundExpression argument = bind is null
                    ? BindExpression(arguments[index])
                    : bind(arguments[index], index);
                bound.Add(argument);
            }
            ImmutableArray<BoundExpression> result = bound.ToImmutable();
            if (result.Any(argument => argument is BoundUnboundLambdaExpression))
                transaction.EnsureTypeFactorySnapshot(_fileScope.TypeFactory);
            return result;
        }
        finally
        {
            _argumentBindingTransactions.Pop();
        }
    }

    private TypeSymbol? GetCallArgumentExpectedType(CallExpressionSyntax syntax, int argumentIndex)
    {
        var candidates = new List<FunctionSymbol>();
        if (syntax.Target is NameExpressionSyntax name)
        {
            if (_scope.Lookup(name.IdentifierToken.Text)?.Type is FunctionPointerTypeSymbol indirect)
                return argumentIndex < indirect.ParameterTypes.Length ? indirect.ParameterTypes[argumentIndex] : null;
            if (_scope.Lookup(name.IdentifierToken.Text)?.Type is FunctionValueTypeSymbol functionValue)
                return argumentIndex < functionValue.ParameterTypes.Length ? functionValue.ParameterTypes[argumentIndex] : null;

            var lookupDiagnostics = new DiagnosticBag();
            TypeSymbol? targetType = syntax.TypeArguments is { } explicitTypeArguments
                ? _fileScope.ResolveType(name.IdentifierToken.Text, explicitTypeArguments.Arguments.Length,
                    name.IdentifierToken.Location, lookupDiagnostics)
                : ResolveUnqualifiedTypeForExpression(name.IdentifierToken.Text,
                    name.IdentifierToken.Location, lookupDiagnostics);
            if (targetType is StructTypeSymbol structure)
                candidates.AddRange(structure.Constructors);
            else if (_function.ContainingType is { } containingType)
            {
                FunctionSymbol[] staticCandidates = _function.IsStatic
                    ? GetMethodOverloads(containingType, name.IdentifierToken.Text, isStatic: true)
                    : [];
                candidates.AddRange(staticCandidates.Length != 0
                    ? staticCandidates
                    : GetMethodOverloads(containingType, name.IdentifierToken.Text, isStatic: false));
            }
            if (candidates.Count == 0)
                candidates.AddRange(_fileScope.ResolveFunctions(name.IdentifierToken.Text));
        }
        else if (syntax.Target is MemberAccessExpressionSyntax member)
        {
            if (TryResolveStaticTypeReceiver(member, out TypeSymbol? staticType) &&
                staticType is DeclaredTypeSymbol staticContainer)
            {
                candidates.AddRange(GetMethodOverloads(staticContainer, member.MemberToken.Text, isStatic: true));
            }
            else if (TryGetCallReceiverType(member.Receiver, out TypeSymbol? receiverType) &&
                     GetCallMemberContainer(receiverType!, member.OperatorToken.Kind) is DeclaredTypeSymbol container)
            {
                candidates.AddRange(GetMethodOverloads(container, member.MemberToken.Text, isStatic: false));
            }
            else if (TryGetDottedName(member, out ImmutableArray<SyntaxToken> parts))
            {
                candidates.AddRange(_fileScope.ResolveQualifiedFunctions(parts.Select(part => part.Text).ToArray()));
            }
        }

        int suppliedCount = syntax.Arguments.Length;
        FunctionSymbol[] viable = candidates.Where(candidate =>
            candidate.Parameters.Length == suppliedCount && argumentIndex < candidate.Parameters.Length).ToArray();
        if (viable.Length == 0) return null;
        ImmutableArray<TypeSymbol> explicitArguments = syntax.TypeArguments is { } typeArguments
            ? typeArguments.Arguments.Select(argument =>
                TypeResolver.Resolve(argument, _fileScope, new DiagnosticBag())).ToImmutableArray()
            : [];
        Dictionary<FunctionSymbol, IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol>> substitutionsByCandidate = [];
        foreach (FunctionSymbol candidate in viable)
        {
            if (!explicitArguments.IsEmpty && candidate.TypeParameters.Length == explicitArguments.Length)
            {
                substitutionsByCandidate[candidate] = candidate.TypeParameters.Zip(explicitArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                continue;
            }
            if (candidate.TypeParameters.IsEmpty) continue;
            var inferred = new Dictionary<GenericParameterSymbol, TypeSymbol>();
            bool compatible = true;
            for (int index = 0; index < syntax.Arguments.Length; index++)
            {
                if (!TryGetLambdaExpression(syntax.Arguments[index], out LambdaExpressionSyntax lambda)) continue;
                if (TryInferLambdaGenericTypes(candidate.Parameters[index].Type, lambda, inferred)) continue;
                compatible = false;
                break;
            }
            if (compatible && candidate.TypeParameters.All(inferred.ContainsKey))
                substitutionsByCandidate[candidate] = inferred;
        }

        TypeSymbol Expected(FunctionSymbol candidate, int index)
        {
            TypeSymbol parameter = candidate.Parameters[index].Type;
            return substitutionsByCandidate.TryGetValue(candidate, out var substitutions)
                ? SubstituteGenericType(parameter, substitutions)
                : parameter;
        }

        viable = viable.Where(candidate => syntax.Arguments.Select((argument, index) => (argument,index))
            .All(pair =>
            {
                if (!TryGetLambdaExpression(pair.argument, out LambdaExpressionSyntax lambda)) return true;
                TypeSymbol contextual = Expected(candidate, pair.index);
                return ContainsGenericParameter(contextual) ||
                       GetLambdaArgumentConversionCost(contextual, lambda) is not null;
            })).ToArray();
        if (viable.Length == 0) return null;
        TypeSymbol expected = Expected(viable[0], argumentIndex);
        return viable.Skip(1).All(candidate =>
            TypeIdentity.AreSame(Expected(candidate, argumentIndex), expected))
                ? expected
                : null;
    }

    private bool TryGetCallReceiverType(ExpressionSyntax syntax, out TypeSymbol? type)
    {
        type = syntax switch
        {
            NameExpressionSyntax name => _scope.Lookup(name.IdentifierToken.Text)?.Type ??
                _function.ContainingType?.FindInstanceField(name.IdentifierToken.Text)?.Type,
            ThisExpressionSyntax => _function.ContainingType,
            MemberAccessExpressionSyntax nested when
                TryGetCallReceiverType(nested.Receiver, out TypeSymbol? nestedReceiver) =>
                GetCallMemberContainer(nestedReceiver!, nested.OperatorToken.Kind)?
                    .FindInstanceField(nested.MemberToken.Text)?.Type,
            _ => null,
        };
        return type is not null;
    }

    private static DeclaredTypeSymbol? GetCallMemberContainer(TypeSymbol type, SyntaxKind operatorKind) =>
        operatorKind == SyntaxKind.ArrowToken ? type switch
        {
            PointerTypeSymbol pointer => pointer.ElementType as DeclaredTypeSymbol,
            UniqueTypeSymbol unique => unique.ElementType as DeclaredTypeSymbol,
            SharedTypeSymbol shared => shared.ElementType as DeclaredTypeSymbol,
            _ => null,
        } : type as DeclaredTypeSymbol;

    private bool IsStaticFunctionMemberTarget(MemberAccessExpressionSyntax member) =>
        TryResolveStaticTypeReceiver(member, out TypeSymbol? type) &&
        type is DeclaredTypeSymbol declaredType &&
        declaredType.FindStaticField(member.MemberToken.Text)?.Type
            is FunctionPointerTypeSymbol or FunctionValueTypeSymbol;

    private bool IsFunctionMemberTarget(
        BoundExpression receiver,
        MemberAccessExpressionSyntax member)
    {
        bool pointerAccess = member.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
        if (GetGenericReceiver(receiver.Type, pointerAccess) is GenericParameterSymbol genericParameter)
        {
            return GenericConstraintMemberLookup.GetFields(genericParameter, member.MemberToken.Text)
                    .Any(field => !field.IsStatic &&
                        field.Type is FunctionPointerTypeSymbol or FunctionValueTypeSymbol) ||
                GenericConstraintMemberLookup.GetProperties(genericParameter, member.MemberToken.Text,
                        _fileScope.TypeFactory, _fileScope.GenericStructSpecializer)
                    .Any(property => !property.IsStatic && property.HasGetter &&
                        property.Type is FunctionPointerTypeSymbol or FunctionValueTypeSymbol);
        }

        DeclaredTypeSymbol? container = pointerAccess ? receiver.Type switch
        {
            PointerTypeSymbol pointer => pointer.ElementType as DeclaredTypeSymbol,
            UniqueTypeSymbol unique => unique.ElementType as DeclaredTypeSymbol,
            SharedTypeSymbol shared => shared.ElementType as DeclaredTypeSymbol,
            _ => receiver.Type as DeclaredTypeSymbol,
        } : receiver.Type as DeclaredTypeSymbol;
        TypeSymbol? memberType = container switch
        {
            InterfaceTypeSymbol interfaceType => interfaceType.FindProperty(member.MemberToken.Text)?.Type,
            null => null,
            _ => container.FindInstanceField(member.MemberToken.Text)?.Type ??
                GetInstancePropertyType(container, member.MemberToken.Text),
        };
        return memberType is FunctionPointerTypeSymbol or FunctionValueTypeSymbol;

        static TypeSymbol? GetInstancePropertyType(DeclaredTypeSymbol type, string name) =>
            type.FindMember<PropertySymbol>(name) is { IsStatic: false, Getter: not null } property
                ? property.Type
                : null;
    }

    private GenericFunctionSpecializer? GetTransactionalGenericSpecializer(
        ImmutableArray<BoundExpression> arguments,
        out FileSymbolScope scope)
    {
        scope = _fileScope;
        ArgumentBindingTransaction? transaction = GetDeferredArgumentTransactions(arguments, reverse: false)
            .FirstOrDefault();
        if (transaction is null) return _genericSpecializer;
        transaction.EnsureTypeFactorySnapshot(_fileScope.TypeFactory);
        (GenericStructSpecializer? structSpecializer,            GenericFunctionSpecializer? functionSpecializer) = transaction.GetTransactionalSpecializers(
                _fileScope.GenericStructSpecializer, _genericSpecializer, _diagnostics);
        if (structSpecializer is not null)
            scope = _fileScope.WithGenericStructSpecializer(structSpecializer);
        return functionSpecializer;
    }

    private void RecordTransactionalGeneratedSymbolRollback(
        ImmutableArray<BoundExpression> arguments,
        SyntaxNode syntax)
    {
        foreach (ArgumentBindingTransaction transaction in
                 GetDeferredArgumentTransactions(arguments, reverse: false))
            transaction.RecordGeneratedArtifactRollback(() => _semanticInfo.RollbackSyntax(syntax));
    }

    private bool HasRejectedDeferredContextualArguments(ImmutableArray<BoundExpression> arguments) =>
        GetDeferredArgumentTransactions(arguments, reverse: false)
            .Any(transaction => transaction.HasRolledBackLambdaCaptures);

    private static bool HasFailedDeferredMaterialization(
        ImmutableArray<BoundExpression> original,
        ImmutableArray<BoundExpression> converted) =>
        original.Zip(converted).Any(pair =>
            pair.First is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression &&
            pair.Second is BoundErrorExpression);

    private BoundExpression BindIndirectCall(
        BoundExpression target,
        FunctionPointerTypeSymbol functionPointer,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location,
        bool incomplete,
        int completedArgumentCount)
    {
        PreserveDeferredDependentMoves(arguments);
        if (_function.IsReadonly)
            _diagnostics.Report(location,
                "readonly code cannot invoke a raw function pointer because it has no readonly effect contract",
                DiagnosticIds.NonReadonlyCallFromReadonlyFunction);
        int suppliedCount = incomplete ? completedArgumentCount : arguments.Length;
        bool wrongArity = (!incomplete && suppliedCount != functionPointer.ParameterTypes.Length) ||
            incomplete && suppliedCount > functionPointer.ParameterTypes.Length;
        if (wrongArity)
        {
            _diagnostics.Report(location,
                $"function pointer expects {functionPointer.ParameterTypes.Length} argument(s), but {suppliedCount} were provided",
                DiagnosticIds.WrongArity);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        for (int index = 0; index < suppliedCount; index++)
            if (arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression &&
                GetArgumentConversionCost(functionPointer.ParameterTypes[index], arguments[index]) is null)
            {
                ReportDeferredContextualMismatch(arguments[index], functionPointer.ParameterTypes[index]);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }

        var converted = arguments.ToBuilder();
        int count = Math.Min(suppliedCount, functionPointer.ParameterTypes.Length);
        bool valid = !incomplete && suppliedCount == functionPointer.ParameterTypes.Length;
        for (int index = 0; index < count; index++)
        {
            TypeSymbol parameterType = functionPointer.ParameterTypes[index];
            BoundExpression argument = MaterializeContextualArgument(
                arguments[index], argumentSyntax[index], parameterType);
            argument = ContextualizeConversion(argument, parameterType,
                GetLocation(argumentSyntax[index]));
            converted[index] = argument;
            if (argument is BoundErrorExpression &&
                arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression)
                valid = false;
            SetConvertedType(argumentSyntax[index], argument.Type);
            if (!TypeFacts.CanAssign(parameterType, argument.Type))
            {
                valid = false;
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, parameterType);
            }
        }
        CompleteDeferredContextualArguments(arguments, valid);
        return new BoundIndirectCallExpression(target, functionPointer, converted.ToImmutable());
    }

    private BoundExpression BindDeferredContextualArgument(ExpressionSyntax syntax)
    {
        if (TryGetLambdaExpression(syntax, out LambdaExpressionSyntax lambda))
            return BindDeferredLambdaExpression(lambda, syntax);
        if (TryGetNamedFunctionExpression(syntax, out ExpressionSyntax function))
            return new BoundUnboundFunctionExpression(function);
        return BindExpression(syntax);
    }

    private void RollbackUnmaterializedContextualArguments(ImmutableArray<BoundExpression> arguments)
    {
        ArgumentBindingTransaction[] transactions = GetDeferredArgumentTransactions(arguments, reverse: true);
        if (transactions.Length == 0) return;
        foreach (ArgumentBindingTransaction transaction in transactions)
            transaction.RollbackUnmaterializedLambdaCaptures();
    }

    private void CommitDeferredContextualArguments(ImmutableArray<BoundExpression> arguments)
    {
        foreach (ArgumentBindingTransaction transaction in GetDeferredArgumentTransactions(arguments, reverse: false))
            transaction.CommitDeferredLambdaCaptures();
    }

    private void PreserveDeferredDependentMoves(ImmutableArray<BoundExpression> arguments)
    {
        foreach (ArgumentBindingTransaction transaction in
                 GetDeferredArgumentTransactions(arguments, reverse: false))
            transaction.PreserveDeferredDependentMoves();
    }

    private void CompleteDeferredContextualArguments(
        ImmutableArray<BoundExpression> arguments,
        bool success)
    {
        if (success) CommitDeferredContextualArguments(arguments);
        else RollbackUnmaterializedContextualArguments(arguments);
    }

    private ArgumentBindingTransaction[] GetDeferredArgumentTransactions(
        ImmutableArray<BoundExpression> arguments,
        bool reverse)
    {
        IEnumerable<BoundExpression> ordered = reverse ? arguments.Reverse() : arguments;
        return ordered
            .OfType<BoundUnboundLambdaExpression>()
            .Select(lambda => _deferredLambdaCaptureTransactions.GetValueOrDefault(lambda.Syntax))
            .OfType<ArgumentBindingTransaction>()
            .Distinct()
            .ToArray();
    }

    private BoundExpression BindFunctionValueCall(
        BoundExpression target,
        FunctionValueTypeSymbol functionValue,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location,
        bool incomplete,
        int completedArgumentCount)
    {
        PreserveDeferredDependentMoves(arguments);
        if (_function.IsReadonly)
            _diagnostics.Report(location,
                "readonly code cannot invoke a function value because its closure may mutate captured state",
                DiagnosticIds.NonReadonlyCallFromReadonlyFunction);
        int suppliedCount = incomplete ? completedArgumentCount : arguments.Length;
        bool wrongArity = (!incomplete && suppliedCount != functionValue.ParameterTypes.Length) ||
            incomplete && suppliedCount > functionValue.ParameterTypes.Length;
        if (wrongArity)
        {
            _diagnostics.Report(location,
                $"function value expects {functionValue.ParameterTypes.Length} argument(s), but {suppliedCount} were provided",
                DiagnosticIds.WrongArity);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        for (int index = 0; index < suppliedCount; index++)
            if (arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression &&
                GetArgumentConversionCost(functionValue.ParameterTypes[index], arguments[index]) is null)
            {
                ReportDeferredContextualMismatch(arguments[index], functionValue.ParameterTypes[index]);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }

        var converted = arguments.ToBuilder();
        int count = Math.Min(suppliedCount, functionValue.ParameterTypes.Length);
        bool valid = !incomplete && suppliedCount == functionValue.ParameterTypes.Length;
        for (int index = 0; index < count; index++)
        {
            TypeSymbol parameterType = functionValue.ParameterTypes[index];
            BoundExpression argument = MaterializeContextualArgument(
                arguments[index], argumentSyntax[index], parameterType);
            argument = _function.IsGenericDefinition &&
                parameterType is GenericParameterSymbol && TypeIdentity.AreSame(parameterType, arguments[index].Type)
                    ? argument
                    : ContextualizeConversion(argument, parameterType,
                        GetLocation(argumentSyntax[index]));
            converted[index] = argument;
            if (argument is BoundErrorExpression &&
                arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression)
                valid = false;
            SetConvertedType(argumentSyntax[index], argument.Type);
            if (!TypeFacts.CanAssign(parameterType, argument.Type))
            {
                valid = false;
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, parameterType);
            }
        }
        CompleteDeferredContextualArguments(arguments, valid);
        return new BoundFunctionValueCallExpression(target, functionValue, converted.ToImmutable());
    }

    private BoundExpression BindLifetimeInvalidationOperand(ExpressionSyntax syntax) => BindExpression(syntax);

    private BoundExpression BindExplicitGenericCall(CallExpressionSyntax syntax,
        TypeArgumentListSyntax typeArguments, ImmutableArray<BoundExpression> arguments)
    {
        FunctionSymbol[] candidates = [];
        TextLocation location = typeArguments.LessToken.Location;
        if (syntax.Target is NameExpressionSyntax name)
        {
            location = name.IdentifierToken.Location;
            TypeSymbol? possibleType = _fileScope.ResolveType(name.IdentifierToken.Text,
                typeArguments.Arguments.Length, location, new DiagnosticBag());
            if (possibleType is StructTypeSymbol { IsGenericDefinition: true })
                return BindExplicitGenericStructConstruction(syntax, name, typeArguments, arguments);
            candidates = _fileScope.ResolveFunctions(name.IdentifierToken.Text).ToArray();
        }
        else if (syntax.Target is MemberAccessExpressionSyntax member &&
                 TryGetDottedName(member, out ImmutableArray<SyntaxToken> parts))
        {
            location = member.MemberToken.Location;
            if (!HasValueSymbol(parts[0].Text, parts[0].Location))
                candidates = _fileScope.ResolveQualifiedFunctions(parts.Select(part => part.Text).ToArray()).ToArray();
        }
        if (candidates.Length == 0)
        {
            _diagnostics.Report(location, "generic call target must name a function",
                DiagnosticIds.InvalidCallTarget);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        _ = GetTransactionalGenericSpecializer(arguments, out FileSymbolScope resolutionScope);
        ImmutableArray<TypeSymbol> resolvedArguments = typeArguments.Arguments
            .Select(argument => TypeResolver.Resolve(argument, resolutionScope, _diagnostics)).ToImmutableArray();
        foreach (ArgumentBindingTransaction transaction in
                 GetDeferredArgumentTransactions(arguments, reverse: false))
            transaction.RecordGeneratedArtifactRollback(() => _semanticInfo.RollbackSyntax(typeArguments));
        FunctionSymbol? definition = ResolveExplicitGenericOverload(candidates, resolvedArguments,
            arguments, location, syntax.Target,
            syntax.CloseParenthesisToken.IsMissing ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        if (definition is null) return new BoundErrorExpression();
        if (!IsAccessible(definition))
        {
            _diagnostics.Report(location,
                $"function '{definition.Name}' is private in namespace '{definition.ContainingNamespace.FullName}'",
                DiagnosticIds.InaccessibleSymbol);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        if (resolvedArguments.Any(ContainsGenericParameter))
            return BindOpenGenericCall(syntax, definition, resolvedArguments, arguments, location);
        GenericFunctionSpecializer? callSpecializer =
            GetTransactionalGenericSpecializer(arguments, out _);
        FunctionSymbol? specialized = callSpecializer?.GetOrCreate(definition, resolvedArguments, location);
        if (specialized is null)
        {
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        SetSelectedSymbolPreservingCandidates(syntax.Target, specialized);
        RecordTransactionalGeneratedSymbolRollback(arguments, syntax.Target);
        ImmutableArray<BoundExpression> deferredArguments = arguments;
        arguments = ValidateFunctionArguments(specialized, arguments, syntax.Arguments, location,
            syntax.CloseParenthesisToken.IsMissing ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        if (HasRejectedDeferredContextualArguments(deferredArguments) ||
            HasFailedDeferredMaterialization(deferredArguments, arguments))
        {
            _semanticInfo.RollbackSyntax(syntax.Target);
            return new BoundErrorExpression();
        }
        return new BoundCallExpression(specialized, arguments);
    }

    private BoundExpression BindExplicitGenericStructConstruction(CallExpressionSyntax syntax,
        NameExpressionSyntax name, TypeArgumentListSyntax typeArguments,
        ImmutableArray<BoundExpression> arguments)
    {
        var typeSyntax = new NamedTypeSyntax([name.IdentifierToken], [], typeArguments);
        _ = GetTransactionalGenericSpecializer(arguments, out FileSymbolScope resolutionScope);
        TypeSymbol resolved = TypeResolver.Resolve(typeSyntax, resolutionScope, _diagnostics);
        foreach (ArgumentBindingTransaction transaction in
                 GetDeferredArgumentTransactions(arguments, reverse: false))
            transaction.RecordGeneratedArtifactRollback(() => _semanticInfo.RollbackSyntax(typeArguments));
        if (resolved is not StructTypeSymbol structure)
        {
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        if (resolutionScope.GenericStructSpecializer is { } structSpecializer &&
            !structSpecializer.AreConstraintsSatisfied(structure))
        {
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        if (structure.IsStatic)
        {
            _diagnostics.Report(name.IdentifierToken.Location,
                $"static struct '{structure.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        if (structure.IsAbstract)
        {
            _diagnostics.Report(name.IdentifierToken.Location,
                $"abstract struct '{structure.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);
            RollbackUnmaterializedContextualArguments(arguments);
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
        if (!IsAccessible(constructor))
        {
            _diagnostics.Report(name.IdentifierToken.Location,
                $"constructor '{structure.Name}' is private", DiagnosticIds.InaccessibleSymbol);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        arguments = ValidateFunctionArguments(constructor, arguments, syntax.Arguments,
            name.IdentifierToken.Location, incomplete ? completedArgumentCount : null);
        if (structure is { IsOpenGenericType: true, GenericDefinition: not null })
            return new BoundDeferredGenericOperationExpression(BoundDeferredGenericOperationKind.Construction,
                null, _fileScope.GenericStructSpecializer!.GetFunctionDefinition(constructor), arguments, null,
                SyntaxKind.EqualsToken, false, structure);
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
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        var substitutions = definition.TypeParameters.Zip(typeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        TypeSymbol SubstituteConstraint(TypeSymbol type) => SubstituteGenericType(type, substitutions);
        for (int index = 0; index < typeArguments.Length; index++)
        {
            if (typeArguments[index] is not GenericParameterSymbol argumentParameter) continue;
            foreach (GenericConstraintSymbol required in definition.TypeParameters[index].Constraints)
            {
                GenericConstraintSymbol effective =
                    GenericConstraintGuarantees.Substitute(required, SubstituteConstraint);
                if (GenericConstraintGuarantees.IsGuaranteed(argumentParameter, effective)) continue;
                _diagnostics.Report(location,
                    $"constraints for '{argumentParameter.Name}' do not guarantee '{effective.Target.Name}' required by '{definition.Name}'" +
                    GenericConstraintGuarantees.GetFailureDetail(argumentParameter, effective),
                    DiagnosticIds.GenericConstraintNotSatisfied);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
        }
        ImmutableArray<TypeSymbol> parameterTypes = definition.Parameters
            .Select(parameter => SubstituteGenericType(parameter.Type, substitutions)).ToImmutableArray();
        arguments = ValidateGenericArguments(definition.Name, parameterTypes, arguments, syntax.Arguments, location,
            syntax.CloseParenthesisToken.IsMissing ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        RecordCandidates(syntax.Target, definition, [definition], CandidateReason.None);
        return new BoundDeferredGenericOperationExpression(
            BoundDeferredGenericOperationKind.FunctionCall, null, definition, arguments, null,
            SyntaxKind.EqualsToken, false,
            SubstituteGenericType(definition.ReturnType, substitutions), typeArguments);
    }

    private BoundExpression? TryBindStaticMethodCall(MemberAccessExpressionSyntax target, ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax, CallExpressionSyntax callSyntax)
    {
        if (!TryResolveStaticTypeReceiver(target, out TypeSymbol? resolved))
            return null;
        if (TypeIdentity.AreSame(resolved!, BuiltinTypes.Error))
        {
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        if (resolved is not DeclaredTypeSymbol structType)
            return null;
        FunctionSymbol[] candidates = GetMethodOverloads(structType, target.MemberToken.Text, isStatic: true);
        if (candidates.Length == 0)
            return null;
        bool incomplete = callSyntax.CloseParenthesisToken.IsMissing;
        int completedArgumentCount = GetCompletedArgumentCount(argumentSyntax, incomplete);
        RecordStaticReceiver(target.Receiver, structType);
        FunctionSymbol? method = ResolveCallableOverload(candidates, arguments, target.MemberToken.Location,
            $"static method '{structType.Name}.{target.MemberToken.Text}'", target, out _,
            completedArgumentCount: incomplete ? completedArgumentCount : null);
        if (method is null) return new BoundErrorExpression();
        if (!IsAccessible(method))
        {
            RecordCandidates(target, null, candidates, CandidateReason.Inaccessible);
            _diagnostics.Report(target.MemberToken.Location, $"static method '{method.Name}' is private in struct '{method.ContainingType!.Name}'",
                DiagnosticIds.InaccessibleSymbol);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        arguments = ValidateFunctionArguments(method, arguments, argumentSyntax, target.MemberToken.Location,
            incomplete ? completedArgumentCount : null);
        return new BoundCallExpression(method, arguments);
    }

    private bool TryResolveStaticTypeReceiver(
        MemberAccessExpressionSyntax syntax,
        out TypeSymbol? type)
    {
        type = null;
        if (syntax.OperatorToken.Kind != SyntaxKind.DotToken ||
            !TryGetDottedName(syntax.Receiver, out ImmutableArray<SyntaxToken> parts) ||
            parts.IsEmpty)
            return false;

        string firstName = parts[0].Text;
        if (HasValueSymbol(firstName, parts[0].Location))
            return false;

        if (syntax.ReceiverTypeArguments is { } typeArguments)
        {
            type = TypeResolver.Resolve(new NamedTypeSyntax(parts, [], typeArguments),
                _fileScope, _diagnostics);
        }
        else
        {
            if (parts.Length > 1 && !_fileScope.CanStartQualifiedName(firstName))
                return false;
            type = parts.Length == 1
                ? ResolveUnqualifiedTypeForExpression(firstName, parts[0].Location, new DiagnosticBag())
                : _fileScope.ResolveQualifiedType(parts.Select(part => part.Text).ToArray());
        }

        if (type is StructTypeSymbol definition &&
            _function.ContainingStruct is StructTypeSymbol { GenericDefinition: not null } specialization &&
            ReferenceEquals(specialization.GenericDefinition, definition))
            type = specialization;
        return type is not null;
    }

    private TypeSymbol? ResolveUnqualifiedTypeForExpression(string name, TextLocation location,
        DiagnosticBag diagnostics)
    {
        if (_function.ContainingStruct is { } containingStruct)
        {
            StructTypeSymbol definition = containingStruct.GenericDefinition ?? containingStruct;
            if (definition.IsGenericDefinition && string.Equals(definition.Name, name, StringComparison.Ordinal))
                return containingStruct;
        }
        return _fileScope.ResolveType(name, location, diagnostics);
    }

    private bool HasValueSymbol(string name, TextLocation location) =>
        _scope.Lookup(name) is not null ||
        _function.ContainingType?.FindMember<ConstantSymbol>(name) is not null ||
        _function.ContainingType?.FindInstanceField(name) is not null ||
        _function.ContainingType?.FindMember<PropertySymbol>(name) is not null ||
        _fileScope.ResolveConstant(name, location, new DiagnosticBag()) is not null;

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
        if (HasValueSymbol(firstName, nameParts[0].Location))
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
            if (structType.IsStatic)
            {
                _diagnostics.Report(target.MemberToken.Location,
                    $"static struct '{structType.Name}' cannot be instantiated",
                    DiagnosticIds.AbstractInstantiation);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            if (structType.IsAbstract)
            {
                _diagnostics.Report(target.MemberToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated",
                    DiagnosticIds.AbstractInstantiation);
                RollbackUnmaterializedContextualArguments(arguments);
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

            if (!IsAccessible(constructor))
            {
                RecordCandidates(target, null, structType.Constructors, CandidateReason.Inaccessible);
                _diagnostics.Report(target.MemberToken.Location, $"constructor '{structType.Name}' is private",
                    DiagnosticIds.InaccessibleSymbol);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }

            arguments = ValidateFunctionArguments(constructor, arguments, argumentSyntax, target.MemberToken.Location,
                incomplete ? completedArgumentCount : null);
            return new BoundConstructorCallExpression(structType, constructor, arguments);
        }

        FunctionSymbol[] functionCandidates = _fileScope.ResolveQualifiedFunctions(parts).ToArray();
        FunctionSymbol? function = functionCandidates.Length == 0 ? null : ResolveCallableOverload(
            functionCandidates, arguments, target.MemberToken.Location,
            $"function '{string.Join('.', parts)}'", target, out _,
            completedArgumentCount: incomplete ? completedArgumentCount : null);
        if (function is not null)
        {
            if (!IsAccessible(function))
            {
                RecordCandidates(target, null, functionCandidates, CandidateReason.Inaccessible);
                _diagnostics.Report(target.MemberToken.Location,
                    $"function '{function.Name}' is private in namespace '{function.ContainingNamespace.FullName}'",
                    DiagnosticIds.InaccessibleSymbol);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            if (function.IsGenericDefinition)
            {
                bool inferenceSucceeded = TryInferGenericTypeArguments(function, arguments,
                    out ImmutableArray<TypeSymbol> inferredArguments);
                if (inferenceSucceeded && inferredArguments.Any(ContainsGenericParameter))
                    return BindOpenGenericCall(callSyntax, function, inferredArguments, arguments,
                        target.MemberToken.Location);
                GenericFunctionSpecializer? callSpecializer =
                    GetTransactionalGenericSpecializer(arguments, out _);
                FunctionSymbol? specialized = callSpecializer is null || !inferenceSucceeded
                    ? null
                    : callSpecializer.GetOrCreate(function, inferredArguments,
                        target.MemberToken.Location);
                if (specialized is null)
                {
                    if (!inferenceSucceeded)
                        _diagnostics.Report(target.MemberToken.Location,
                            $"type arguments for generic function '{function.Name}' could not be inferred",
                            DiagnosticIds.GenericSpecializationNotImplemented);
                    RollbackUnmaterializedContextualArguments(arguments);
                    return new BoundErrorExpression();
                }
                function = specialized;
                SetSelectedSymbolPreservingCandidates(target, specialized);
                RecordTransactionalGeneratedSymbolRollback(arguments, target);
            }
            ImmutableArray<BoundExpression> deferredArguments = arguments;
            arguments = ValidateFunctionArguments(function, arguments, argumentSyntax, target.MemberToken.Location,
                incomplete ? completedArgumentCount : null);
            if (HasRejectedDeferredContextualArguments(deferredArguments) ||
                HasFailedDeferredMaterialization(deferredArguments, arguments))
            {
                if (function.IsGenericSpecialization)
                    _semanticInfo.RollbackSyntax(target);
                return new BoundErrorExpression();
            }
            return new BoundCallExpression(function, arguments);
        }

        if (functionCandidates.Length == 0)
        {
            // A namespace-qualified static field begins a value receiver chain.
            // Let normal member binding resolve the rest and enforce accessibility
            // and readonly rules instead of treating every segment as a namespace.
            for (ExpressionSyntax receiver = target;
                 receiver is MemberAccessExpressionSyntax member;
                 receiver = member.Receiver)
                if (TryResolveStaticTypeReceiver(member, out TypeSymbol? owner) &&
                    owner is DeclaredTypeSymbol declared &&
                    declared.FindStaticField(member.MemberToken.Text) is not null)
                    return null;

            _diagnostics.Report(
                target.MemberToken.Location,
                $"unknown function or struct '{string.Join('.', parts)}'",
                DiagnosticIds.UnknownQualifiedCallable);
        }

        RollbackUnmaterializedContextualArguments(arguments);

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
        BoundExpression receiver = ExposeLifetimeValue(BindExpression(target.Receiver), target.Receiver);
        return BindMethodCallExpression(target, arguments, argumentSyntax, callSyntax, receiver);
    }

    private BoundExpression BindMethodCallExpression(
        MemberAccessExpressionSyntax target,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        CallExpressionSyntax callSyntax,
        BoundExpression receiver)
    {
        bool incomplete = callSyntax.CloseParenthesisToken.IsMissing;
        int completedArgumentCount = GetCompletedArgumentCount(argumentSyntax, incomplete);
        ArrayTypeSymbol? receiverArray = receiver.Type as ArrayTypeSymbol ??
            (receiver.Type as OwnershipTypeSymbol)?.ElementType as ArrayTypeSymbol;
        if (receiverArray is { } array && target.OperatorToken.Kind == SyntaxKind.DotToken && target.MemberToken.Text == "GetLength")
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
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            if (completedArgumentCount == 1 && _constants.TryFold(arguments[0], out object? dimension) &&
                (SemanticAnalyzer.ToInteger(dimension) < 0 || SemanticAnalyzer.ToInteger(dimension) >= array.Rank))
                _diagnostics.Report(target.MemberToken.Location, $"GetLength dimension must be between 0 and {array.Rank - 1}",
                    DiagnosticIds.GetLengthDimensionOutOfRange);
            return new BoundArrayMetadataExpression(receiver, "GetLength", arguments[0]);
        }
        bool pointerAccess = target.OperatorToken.Kind == SyntaxKind.ArrowToken || receiver is BoundThisExpression;
        if (receiver.Type is WeakTypeSymbol)
        {
            _diagnostics.Report(target.MemberToken.Location,
                $"cannot access a value through '{receiver.Type.ToDisplayString()}' directly; use 'lock value' and check the returned shared owner first",
                DiagnosticIds.WeakDirectAccess);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
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
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            if (IsReadonlyReceiver(receiver, pointerAccess) && !interfaceMethod.IsReadonly)
            {
                _diagnostics.Report(target.MemberToken.Location, $"mutable interface method '{interfaceMethod.Name}' cannot be called on a readonly '{interfaceType.Name}' receiver",
                    DiagnosticIds.MutableMethodOnReadonlyReceiver);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            arguments = ValidateFunctionArguments(interfaceMethod, arguments, argumentSyntax, target.MemberToken.Location,
                incomplete ? completedArgumentCount : null);
            return new BoundInterfaceMethodCallExpression(receiver, interfaceType, interfaceMethod, arguments, pointerAccess);
        }
        DeclaredTypeSymbol? structType = pointerAccess
            ? receiver.Type switch
            {
                PointerTypeSymbol pointer => pointer.ElementType as DeclaredTypeSymbol,
                UniqueTypeSymbol unique => unique.ElementType as DeclaredTypeSymbol,
                SharedTypeSymbol shared => shared.ElementType as DeclaredTypeSymbol,
                _ => null,
            }
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

            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        bool hasReadonlyReceiver =
            (pointerAccess && receiver.Type is PointerTypeSymbol { IsReadonly: true }) ||
            (!pointerAccess && IsAddressable(receiver) && !IsWritable(receiver));

        FunctionSymbol[] methodCandidates = GetMethodOverloads(structType, target.MemberToken.Text,
            isStatic: false);
        bool filterMutableCandidates = hasReadonlyReceiver && methodCandidates.Any(candidate => candidate.IsReadonly);
        FunctionSymbol? method = methodCandidates.Length == 0 ? null : ResolveCallableOverload(
            methodCandidates, arguments, target.MemberToken.Location,
            $"method '{structType.Name}.{target.MemberToken.Text}'", target, out _, filterMutableCandidates,
            incomplete ? completedArgumentCount : null,
            preferReadonly: _function.IsReadonly && !hasReadonlyReceiver);
        if (method is null)
        {
            if (hasReadonlyReceiver && methodCandidates.Length != 0 &&
                methodCandidates.All(candidate => !candidate.IsReadonly))
            {
                RecordCandidates(target, null, methodCandidates, CandidateReason.Inaccessible);
                _diagnostics.Report(target.MemberToken.Location,
                    $"mutable method '{methodCandidates[0].Name}' cannot be called on a readonly '{structType.Name}' receiver",
                    DiagnosticIds.MutableMethodOnReadonlyReceiver);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            FunctionSymbol? namedMethod = structType.FindMember<FunctionSymbol>(target.MemberToken.Text);
            if (methodCandidates.Length != 0)
            {
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
            }
            RecordCandidates(target, null, methodCandidates,
                namedMethod is not null && (hasReadonlyReceiver || !IsAccessible(namedMethod, receiver, pointerAccess))
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
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        if (hasReadonlyReceiver && !method.IsReadonly)
        {
            RecordCandidates(target, null, methodCandidates, CandidateReason.Inaccessible);
            _diagnostics.Report(target.MemberToken.Location,
                $"mutable method '{method.Name}' cannot be called on a readonly '{structType.Name}' receiver",
                DiagnosticIds.MutableMethodOnReadonlyReceiver);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        if (!IsAccessible(method, receiver, pointerAccess))
        {
            RecordCandidates(target, null, methodCandidates, CandidateReason.Inaccessible);
            _diagnostics.Report(
                target.MemberToken.Location,
                $"method '{method.Name}' is private in struct '{method.ContainingType!.Name}'",
                DiagnosticIds.InaccessibleSymbol);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }

        arguments = ValidateFunctionArguments(method, arguments, argumentSyntax, target.MemberToken.Location,
            incomplete ? completedArgumentCount : null);
        return new BoundMethodCallExpression(receiver, method, arguments, pointerAccess);
    }

    private BoundExpression BindLifetimeOperation(
        CallExpressionSyntax syntax,
        NameExpressionSyntax name,
        ImmutableArray<BoundExpression> arguments)
    {
        if (arguments.IsEmpty)
        {
            _diagnostics.Report(name.IdentifierToken.Location,
                $"'{name.IdentifierToken.Text}' expects a lifetime-managed instance as its first argument",
                DiagnosticIds.WrongArity);
            return new BoundErrorExpression();
        }

        BoundExpression target = arguments[0];
        TextLocation targetLocation = GetLocation(syntax.Arguments[0]);
        if (!IsAddressable(target) || !IsWritable(target))
        {
            _diagnostics.Report(targetLocation,
                $"'{name.IdentifierToken.Text}' requires writable addressable storage",
                DiagnosticIds.InvalidAssignmentTarget);
            return new BoundErrorExpression();
        }

        if (arguments.Length != 1)
            _diagnostics.Report(name.IdentifierToken.Location,
                "'destruct' expects exactly one argument", DiagnosticIds.WrongArity);
        TypeSymbol valueType;
        MovePlace? trackedPlace = TryGetMovePlace(target, out MovePlace targetPlace) ? targetPlace : null;
        if (TryGetStorageType(target.Type, out StorageTypeSymbol storageType))
        {
            valueType = UnwrapExplicitDestructionType(storageType.ElementType);
        }
        else if (target.Type is PinTypeSymbol pinType)
        {
            valueType = UnwrapExplicitDestructionType(pinType.ElementType);
        }
        else
        {
            valueType = target.Type;
        }
        ValidateDestructorAccessibility(valueType, targetLocation);
        return new BoundExplicitDestructExpression(target, valueType, TypeFacts.GetCompleteDestructor(valueType))
        {
            TrackedVariable = trackedPlace?.RootVariable,
            TrackedPath = trackedPlace?.Fields ?? [],
        };
    }

    private BoundExpression BindDestinationConstruction(
        BoundExpression destination,
        TypeSymbol type,
        BoundExpression source,
        ExpressionSyntax sourceSyntax,
        TextLocation location)
    {
        if (source is BoundConstructorCallExpression constructorCall &&
            TypeIdentity.AreSame(type, constructorCall.StructType))
            return new BoundStorageConstructExpression(destination, type, Value: null,
                constructorCall.Constructor, constructorCall.Arguments, IsDefaultInitialization: false);
        if (source is BoundStructConstructionExpression positional &&
            TypeIdentity.AreSame(type, positional.StructType))
            return new BoundStorageConstructExpression(destination, type, Value: null,
                Constructor: null, positional.Arguments, positional.IsDefaultInitialization);

        BoundExpression converted = ContextualizeConversion(source, type, GetLocation(sourceSyntax));
        if (!TypeFacts.CanAssign(type, converted.Type))
        {
            ReportCannotConvert(GetLocation(sourceSyntax), converted.Type, type);
            return new BoundErrorExpression();
        }
        return new BoundStorageConstructExpression(destination, type, converted,
            Constructor: null, Arguments: [], IsDefaultInitialization: false);
    }

    private static FunctionSymbol[] GetMethodOverloads(DeclaredTypeSymbol type, string name, bool isStatic)
    {
        var methods = new List<FunctionSymbol>();
        foreach (FunctionSymbol method in type.LookupMethods(name).Where(candidate => candidate.IsStatic == isStatic))
        {
            // Lookup is derived-first. An override replaces only its matching inherited slot;
            // unrelated inherited overloads remain candidates.
            if (!methods.Any(existing => existing.HasSameSignature(method))) methods.Add(method);
        }
        return methods.ToArray();
    }

    private BoundExpression BindExpressionWithExpectedType(ExpressionSyntax syntax, TypeSymbol expectedType)
    {
        bool hasLambda = TryGetLambdaExpression(syntax, out LambdaExpressionSyntax lambda);
        bool hasNamedFunction = TryGetNamedFunctionExpression(syntax, out ExpressionSyntax function);
        if ((hasLambda || hasNamedFunction) && !AreContextualTypeConstraintsSatisfied(expectedType))
        {
            if (hasLambda)
                RollbackRejectedDeferredLambda(lambda);
            return new BoundErrorExpression();
        }
        if (hasLambda &&
            expectedType is not (FunctionValueTypeSymbol or FunctionPointerTypeSymbol) &&
            TryBindLambdaUserConversion(lambda, expectedType, out BoundExpression? convertedLambda))
        {
            for (ExpressionSyntax wrapper = syntax;
                 wrapper is ParenthesizedExpressionSyntax parenthesized;
                 wrapper = parenthesized.Expression)
                _semanticInfo.Types[wrapper] = new TypeInfo(convertedLambda!.Type, convertedLambda.Type);
            return convertedLambda!;
        }
        if (hasNamedFunction &&
            expectedType is FunctionValueTypeSymbol expectedFunctionValue &&
            TryBindNamedFunctionValue(function, expectedFunctionValue, out BoundExpression? namedFunctionValue))
            return namedFunctionValue!;
        if (hasNamedFunction &&
            expectedType is not (FunctionValueTypeSymbol or FunctionPointerTypeSymbol) &&
            TryBindNamedFunctionUserConversion(function, expectedType, out BoundExpression? convertedFunction))
        {
            for (ExpressionSyntax wrapper = syntax;
                 wrapper is ParenthesizedExpressionSyntax parenthesized;
                 wrapper = parenthesized.Expression)
                _semanticInfo.Types[wrapper] = new TypeInfo(convertedFunction!.Type, convertedFunction.Type);
            return convertedFunction!;
        }

        FunctionPointerTypeSymbol? previous = _expectedFunctionPointerType;
        FunctionValueTypeSymbol? previousValue = _expectedFunctionValueType;
        _expectedFunctionPointerType = expectedType as FunctionPointerTypeSymbol;
        _expectedFunctionValueType = expectedType as FunctionValueTypeSymbol;
        try { return BindExpression(syntax); }
        finally
        {
            _expectedFunctionPointerType = previous;
            _expectedFunctionValueType = previousValue;
        }
    }

    private bool AreContextualTypeConstraintsSatisfied(TypeSymbol type) => type switch
    {
        StructTypeSymbol { GenericDefinition: not null } structure =>
            (_fileScope.GenericStructSpecializer?.AreConstraintsSatisfied(structure) ?? true) &&
            structure.TypeArguments.All(AreContextualTypeConstraintsSatisfied),
        InterfaceTypeSymbol { GenericDefinition: not null } @interface =>
            @interface.TypeArguments.All(AreContextualTypeConstraintsSatisfied),
        FunctionPointerTypeSymbol function =>
            AreContextualTypeConstraintsSatisfied(function.ReturnType) &&
            function.ParameterTypes.All(AreContextualTypeConstraintsSatisfied),
        FunctionValueTypeSymbol function =>
            AreContextualTypeConstraintsSatisfied(function.ReturnType) &&
            function.ParameterTypes.All(AreContextualTypeConstraintsSatisfied),
        PointerTypeSymbol pointer => AreContextualTypeConstraintsSatisfied(pointer.ElementType),
        ReferenceTypeSymbol reference => AreContextualTypeConstraintsSatisfied(reference.ElementType),
        ArrayTypeSymbol array => AreContextualTypeConstraintsSatisfied(array.ElementType),
        AtomicTypeSymbol atomic => AreContextualTypeConstraintsSatisfied(atomic.ElementType),
        UniqueTypeSymbol unique => AreContextualTypeConstraintsSatisfied(unique.ElementType),
        SharedTypeSymbol shared => AreContextualTypeConstraintsSatisfied(shared.ElementType),
        WeakTypeSymbol weak => AreContextualTypeConstraintsSatisfied(weak.ElementType),
        StorageTypeSymbol storage => AreContextualTypeConstraintsSatisfied(storage.ElementType),
        PinTypeSymbol pin => AreContextualTypeConstraintsSatisfied(pin.ElementType),
        _ => true,
    };

    private BoundExpression BindNewExpression(NewExpressionSyntax syntax)
    {
        TypeSymbol type = TypeResolver.Resolve(syntax.Type, _fileScope, _diagnostics);

        if (syntax.IsArrayAllocation)
        {
            return BindArrayCreation(type, syntax.Arguments, syntax.Type.NameToken.Location, syntax.OpenDelimiterToken.Location, ArrayStorageKind.Heap);
        }

        if (type is GenericParameterSymbol genericParameter)
            return BindGenericNewExpression(syntax, genericParameter);

        if (type is StorageTypeSymbol storage)
        {
            ImmutableArray<BoundExpression> storageArguments = BindTransferredArguments(syntax.Arguments);
            if (!storageArguments.IsEmpty)
                _diagnostics.Report(syntax.OpenDelimiterToken.Location,
                    $"storage allocation expects no initializer values, but {storageArguments.Length} were provided",
                    DiagnosticIds.WrongArity);
            ValidateDestructorAccessibility(storage.ElementType, syntax.Type.NameToken.Location);
            return new BoundNewExpression(storage, null, [], true,
                _fileScope.TypeFactory.PointerTo(storage))
            {
                IsDefaultInitialization = true,
            };
        }

        if (type is PrimitiveTypeSymbol primitive && !TypeIdentity.AreSame(type, BuiltinTypes.Void))
        {
            ImmutableArray<BoundExpression> primitiveArguments = BindTransferredArguments(syntax.Arguments);
            if (primitiveArguments.Length > 1)
                _diagnostics.Report(syntax.OpenDelimiterToken.Location,
                    $"primitive allocation of '{primitive.Name}' expects zero or one initializer value, but {primitiveArguments.Length} were provided",
                    DiagnosticIds.WrongArity);
            if (primitiveArguments.Length > 0)
            {
                BoundExpression initializer = ContextualizeConversion(primitiveArguments[0], primitive,
                    GetLocation(syntax.Arguments[0]));
                primitiveArguments = [initializer];
                SetConvertedType(syntax.Arguments[0], initializer.Type);
                if (!TypeFacts.CanAssign(primitive, initializer.Type))
                    ReportCannotConvert(GetLocation(syntax.Arguments[0]), initializer.Type, primitive);
            }
            return new BoundNewExpression(primitive, null, primitiveArguments, true,
                _fileScope.TypeFactory.PointerTo(primitive))
            {
                IsDefaultInitialization = primitiveArguments.IsEmpty,
            };
        }

        if (type is not StructTypeSymbol structType)
        {
            if (!TypeIdentity.AreSame(type, BuiltinTypes.Error))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, $"'new' requires a struct type or array element type, but has type '{type.Name}'",
                    DiagnosticIds.NewRequiresStructType);
            }

            return new BoundErrorExpression();
        }

        if (structType.IsStatic)
        {
            _diagnostics.Report(syntax.Type.NameToken.Location,
                $"static struct '{structType.Name}' cannot be allocated",
                DiagnosticIds.AbstractInstantiation);
            return new BoundErrorExpression();
        }

        bool isAbstract = structType.IsAbstract;
        if (isAbstract)
            _diagnostics.Report(syntax.Type.NameToken.Location, $"abstract struct '{structType.Name}' cannot be instantiated",
                DiagnosticIds.AbstractInstantiation);

        ImmutableArray<BoundExpression> arguments = BindTransferredArguments(syntax.Arguments,
            (argument, _) => BindDeferredContextualArgument(argument));
        if (isAbstract)
        {
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        if (_fileScope.GenericStructSpecializer is { } structSpecializer &&
            !structSpecializer.AreConstraintsSatisfied(structType))
        {
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
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
            if (arguments.Any(argument => argument is BoundErrorExpression))
                return new BoundErrorExpression();
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

            if (!IsAccessible(constructor))
            {
                RecordCandidates(syntax, null, structType.Constructors, CandidateReason.Inaccessible);
                _diagnostics.Report(syntax.Type.NameToken.Location, $"constructor '{structType.Name}' is private",
                    DiagnosticIds.InaccessibleSymbol);
                RollbackUnmaterializedContextualArguments(arguments);
                return new BoundErrorExpression();
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

        if (pointer.Type is not (PointerTypeSymbol or ArrayTypeSymbol) &&
            !TypeIdentity.AreSame(pointer.Type, BuiltinTypes.Error))
        {
            _diagnostics.Report(
                syntax.FreeKeyword.Location,
                $"'free' requires a heap pointer or heap array, but has type '{pointer.Type.ToDisplayString()}'",
                DiagnosticIds.InvalidFreeOperand);
            return new BoundErrorExpression();
        }


        return new BoundFreeExpression(pointer);
    }

    private BoundExpression BindDeleteExpression(
        CallExpressionSyntax syntax,
        NameExpressionSyntax name,
        ImmutableArray<BoundExpression> arguments)
    {
        if (arguments.Length != 1)
        {
            _diagnostics.Report(name.IdentifierToken.Location,
                "'delete' expects exactly one argument", DiagnosticIds.WrongArity);
            return new BoundErrorExpression();
        }

        BoundExpression pointer = arguments[0];
        bool nullLiteral = TypeIdentity.AreSame(pointer.Type, BuiltinTypes.Null);
        if (nullLiteral)
            pointer = ContextualizeNull(pointer, _fileScope.TypeFactory.PointerTo(BuiltinTypes.Byte));

        FunctionSymbol? destructor;
        TypeSymbol destroyedType;
        if (pointer.Type is PointerTypeSymbol pointerType)
        {
            if (pointerType.IsReadonly)
            {
                _diagnostics.Report(name.IdentifierToken.Location,
                    "cannot delete through a readonly pointer because deletion ends the pointee lifetime",
                    DiagnosticIds.FreeThroughReadonlyPointer);
                return new BoundErrorExpression();
            }
            if (TypeIdentity.AreSame(pointerType.ElementType, BuiltinTypes.Void))
            {
                _diagnostics.Report(name.IdentifierToken.Location,
                    "'delete' cannot destroy a void pointer; use 'free(pointer)' for raw memory",
                    DiagnosticIds.InvalidFreeOperand);
                return new BoundErrorExpression();
            }
            destroyedType = pointerType.ElementType;
            destructor = nullLiteral ? null : TypeFacts.GetCompleteDestructor(destroyedType);
        }
        else if (pointer.Type is ArrayTypeSymbol arrayType)
        {
            destroyedType = arrayType.ElementType;
            destructor = TypeFacts.GetCompleteDestructor(destroyedType);
        }
        else
        {
            if (!TypeIdentity.AreSame(pointer.Type, BuiltinTypes.Error))
                _diagnostics.Report(name.IdentifierToken.Location,
                    $"'delete' requires a heap pointer or heap array, but has type '{pointer.Type.ToDisplayString()}'",
                    DiagnosticIds.InvalidFreeOperand);
            return new BoundErrorExpression();
        }


        ValidateDestructorAccessibility(destroyedType, name.IdentifierToken.Location);
        return new BoundDeleteExpression(pointer, destructor);
    }

    private BoundExpression BindRawAllocationExpression(
        CallExpressionSyntax syntax,
        NameExpressionSyntax name,
        ImmutableArray<BoundExpression> arguments)
    {
        string operation = name.IdentifierToken.Text;
        bool validArity = operation == "malloc" ? arguments.Length is 1 or 2 : arguments.Length == 2;
        if (!validArity)
        {
            string expected = operation == "malloc" ? "one or two arguments" : "exactly two arguments";
            _diagnostics.Report(name.IdentifierToken.Location,
                $"'{operation}' expects {expected}", DiagnosticIds.WrongArity);
            return new BoundErrorExpression();
        }
        var converted = arguments.ToBuilder();
        for (int index = 0; index < arguments.Length; index++)
        {
            if (TypeFacts.CanAssign(BuiltinTypes.NUInt, arguments[index].Type)) continue;
            if (TypeFacts.IsInteger(arguments[index].Type) &&
                _constants.TryFold(arguments[index], out object? constant) &&
                SemanticAnalyzer.ToInteger(constant) is { Sign: >= 0 } integer &&
                integer <= uint.MaxValue)
            {
                converted[index] = new BoundCastExpression(arguments[index], BuiltinTypes.NUInt);
                continue;
            }
            ReportCannotConvert(GetLocation(syntax.Arguments[index]), arguments[index].Type, BuiltinTypes.NUInt);
            return new BoundErrorExpression();
        }

        RawAllocationKind kind = operation == "calloc"
            ? RawAllocationKind.Calloc
            : arguments.Length == 2 ? RawAllocationKind.AlignedMalloc : RawAllocationKind.Malloc;
        return new BoundRawAllocationExpression(kind, converted.ToImmutable(),
            _fileScope.TypeFactory.PointerTo(BuiltinTypes.Void));
    }

    private void ValidateDestructorAccess(FunctionSymbol? destructor, TextLocation location)
    {
        if (destructor?.FunctionKind is FunctionKind.OwnershipDestructor or FunctionKind.StorageDestructor) return;
        if (destructor is not null && !IsAccessible(destructor))
            _diagnostics.Report(location, $"destructor '{destructor.ContainingType!.Name}' is private",
                DiagnosticIds.InaccessibleSymbol);
    }

    private void ValidateDestructorAccessibility(TypeSymbol type, TextLocation location) =>
        ValidateDestructorAccessibility(type, location, []);

    private void ValidateDestructorAccessibility(TypeSymbol type, TextLocation location, HashSet<TypeSymbol> visited)
    {
        if (type is AtomicTypeSymbol atomic)
        {
            ValidateDestructorAccessibility(atomic.ElementType, location, visited);
            return;
        }
        if (type is LifetimeModifierTypeSymbol modifier)
        {
            ValidateDestructorAccessibility(modifier.ElementType, location, visited);
            return;
        }
        if (type is WeakTypeSymbol) return;
        if (type is UniqueTypeSymbol unique)
        {
            ValidateDestructorAccessibility(unique.ElementType, location, visited);
            return;
        }
        if (type is SharedTypeSymbol shared)
        {
            ValidateDestructorAccessibility(shared.ElementType, location, visited);
            return;
        }
        if (type is ArrayTypeSymbol array)
        {
            ValidateDestructorAccessibility(array.ElementType, location, visited);
            return;
        }
        if (type is not StructTypeSymbol structure || !visited.Add(type)) return;

        ValidateDestructorAccess(structure.Destructor, location);
        if (structure.BaseType is { } baseType)
            ValidateDestructorAccessibility(baseType, location, visited);
        foreach (FieldSymbol field in structure.Fields)
            ValidateDestructorAccessibility(field.Type, location, visited);
    }

    private ImmutableArray<BoundExpression> ValidatePositionalArguments(
        StructTypeSymbol structType,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location)
    {
        PreserveDeferredDependentMoves(arguments);
        ImmutableArray<FieldSymbol> fields = structType.AllInstanceFields;
        bool hasMissingRequiredFields = arguments.Length < fields.Length &&
            fields.Skip(arguments.Length).Any(field => field.Initializer is null);
        bool valid = arguments.Length <= fields.Length && !hasMissingRequiredFields;
        if (!valid)
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
            BoundExpression argument = arguments[index] switch
            {
                BoundUnboundLambdaExpression or BoundUnboundFunctionExpression =>
                    BindExpressionWithExpectedType(argumentSyntax[index], fieldType),
                _ => arguments[index],
            };
            argument = ContextualizeConversion(argument, fieldType, GetLocation(argumentSyntax[index]));
            convertedArguments[index] = argument;
            if (argument is BoundErrorExpression &&
                arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression)
                valid = false;
            SetConvertedType(argumentSyntax[index], argument.Type);

            if (!AccessibilityRules.IsAccessible(field, _function, structType))
            {
                valid = false;
                _diagnostics.Report(
                    GetLocation(argumentSyntax[index]),
                    $"field '{field.Name}' is private in struct '{field.ContainingType.Name}'",
                    DiagnosticIds.InaccessibleSymbol);
            }


            if (!TypeFacts.CanAssign(fieldType, argument.Type))
            {
                valid = false;
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, fieldType);
            }
        }
        CompleteDeferredContextualArguments(arguments, valid);
        if (!valid)
        {
            bool typed = convertedArguments.Count == fields.Length &&
                convertedArguments.Zip(fields).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type)) &&
                convertedArguments.All(argument => !BoundTree.DescendantsAndSelf(argument).Any(node => node is
                    BoundErrorExpression or BoundUnboundLambdaExpression or BoundUnboundFunctionExpression or BoundFunctionValueExpression));
            return [new BoundErrorExpression() { RecoveryArguments = typed
                ? [new BoundStructConstructionExpression(structType, convertedArguments.ToImmutable())] : [] }];
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

    private FunctionSymbol? ResolveCallableOverload(
        IEnumerable<FunctionSymbol> source,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string description,
        SyntaxNode syntax,
        out CandidateReason reason,
        bool? receiverIsReadonly = null,
        int? completedArgumentCount = null,
        bool preferReadonly = false)
    {
        FunctionSymbol[] candidates = source.Distinct().ToArray();
        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        // Preserve detailed arity/conversion diagnostics when there is no overload
        // decision to make. Validation after selection owns those messages.
        if (candidates.Length == 1 && candidates[0].TypeParameters.IsEmpty)
        {
            FunctionSymbol single = candidates[0];
            reason = receiverIsReadonly == true && !single.IsReadonly
                ? CandidateReason.Inaccessible
                : GetCallCandidateReason(single, arguments, incomplete, suppliedCount);
            bool hasDeferredArgument = arguments.Take(suppliedCount).Any(argument =>
                argument is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression);
            if (hasDeferredArgument && reason is CandidateReason.None or CandidateReason.Incomplete &&
                single.Parameters.Take(suppliedCount).Zip(arguments.Take(suppliedCount)).Any(pair =>
                    GetArgumentConversionCost(pair.First.Type, pair.Second) is null))
                reason = CandidateReason.NotInvocable;
            if (hasDeferredArgument && reason is CandidateReason.NotInvocable or CandidateReason.WrongArity)
            {
                if (reason == CandidateReason.NotInvocable)
                    for (int index = 0; index < suppliedCount; index++)
                        if (GetArgumentConversionCost(single.Parameters[index].Type, arguments[index]) is null)
                            ReportDeferredContextualMismatch(arguments[index], single.Parameters[index].Type);
                RecordCandidates(syntax, null, candidates, reason);
                if (!incomplete)
                    _diagnostics.Report(location,
                        $"no overload of {description} matches the provided arguments; candidates: {FormatCallableCandidates(candidates)}",
                        reason == CandidateReason.WrongArity ? DiagnosticIds.WrongArity : DiagnosticIds.NoMatchingCandidate);
                if (reason == CandidateReason.NotInvocable)
                    PreserveDeferredDependentMoves(arguments);
                RollbackUnmaterializedContextualArguments(arguments);
                return null;
            }
            RecordCandidates(syntax, reason == CandidateReason.Inaccessible ? null : single,
                candidates, reason);
            return single;
        }
        var matches = new List<(FunctionSymbol Function, int[] Costs)>();
        var constraintValidator = new GenericConstraintValidator();

        foreach (FunctionSymbol candidate in candidates)
        {
            if (receiverIsReadonly == true && !candidate.IsReadonly) continue;
            if (incomplete ? candidate.Parameters.Length < suppliedCount : candidate.Parameters.Length != suppliedCount)
                continue;

            ImmutableArray<TypeSymbol> parameterTypes = candidate.Parameters.Select(parameter => parameter.Type)
                .ToImmutableArray();
            if (candidate.TypeParameters.Length != 0)
            {
                if (incomplete)
                {
                    // Keep generic signatures available to tooling while the argument list is incomplete.
                    // Concrete non-generic parameter positions can still disqualify the candidate.
                    int[] partialCosts = parameterTypes.Take(suppliedCount).Zip(arguments.Take(suppliedCount))
                        .Select(pair => ContainsGenericParameter(pair.First)
                            ? 0
                            : GetArgumentConversionCost(pair.First, pair.Second) ?? int.MaxValue)
                        .ToArray();
                    if (partialCosts.All(cost => cost != int.MaxValue)) matches.Add((candidate,partialCosts));
                    continue;
                }

                if (!TryInferGenericTypeArguments(candidate, arguments, out ImmutableArray<TypeSymbol> inferred))
                    continue;
                var substitutions = candidate.TypeParameters.Zip(inferred)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                TypeSymbol SubstituteConstraint(TypeSymbol type) => SubstituteGenericType(type, substitutions);
                bool constraintsSatisfied = true;
                for (int index = 0; index < inferred.Length; index++)
                {
                    if (inferred[index] is GenericParameterSymbol inferredParameter)
                    {
                        if (candidate.TypeParameters[index].Constraints.All(required =>
                                GenericConstraintGuarantees.IsGuaranteed(
                                    inferredParameter, required, SubstituteConstraint)))
                            continue;
                        constraintsSatisfied = false;
                        break;
                    }
                    if (constraintValidator.Validate(candidate.TypeParameters[index], inferred[index],
                            SubstituteConstraint).IsValid) continue;
                    constraintsSatisfied = false;
                    break;
                }
                if (!constraintsSatisfied) continue;
                parameterTypes = parameterTypes.Select(type => SubstituteGenericType(type, substitutions))
                    .ToImmutableArray();
            }

            int?[] classified = parameterTypes.Take(suppliedCount).Zip(arguments.Take(suppliedCount))
                .Select(pair => GetArgumentConversionCost(pair.First, pair.Second)).ToArray();
            if (classified.All(cost => cost.HasValue))
                matches.Add((candidate,classified.Select(cost => cost!.Value).ToArray()));
        }

        if (matches.Count == 0)
        {
            reason = candidates.Length == 0 ? CandidateReason.NotFound
                : candidates.All(candidate => incomplete
                    ? candidate.Parameters.Length < suppliedCount
                    : candidate.Parameters.Length != suppliedCount)
                    ? CandidateReason.WrongArity
                    : CandidateReason.NotInvocable;
            RecordCandidates(syntax, null, candidates, reason);
            if (!incomplete && candidates.Length != 0)
                _diagnostics.Report(location,
                    $"no overload of {description} matches the provided arguments; candidates: {FormatCallableCandidates(candidates)}",
                    reason == CandidateReason.WrongArity ? DiagnosticIds.WrongArity : DiagnosticIds.NoMatchingCandidate);
            RollbackUnmaterializedContextualArguments(arguments);
            return null;
        }

        List<(FunctionSymbol Function, int[] Costs)> best = matches.Where(candidate => !matches.Any(other =>
            !ReferenceEquals(other.Function, candidate.Function) &&
            IsBetterConversionSequence(other.Costs, candidate.Costs))).ToList();

        if (best.Count > 1 && receiverIsReadonly == false)
        {
            var preferred = best.Where(candidate => candidate.Function.IsReadonly == preferReadonly).ToList();
            if (preferred.Count == 1 && best.All(candidate =>
                    candidate.Function.HasSameOverloadSignature(preferred[0].Function)))
                best = preferred;
        }
        if (best.Count > 1)
        {
            var nonGeneric = best.Where(candidate => candidate.Function.TypeParameters.IsEmpty).ToList();
            if (nonGeneric.Count == 1 && best.All(candidate => candidate.Costs.SequenceEqual(nonGeneric[0].Costs)))
                best = nonGeneric;
        }

        if (best.Count == 1)
        {
            reason = incomplete ? CandidateReason.Incomplete : CandidateReason.None;
            RecordCandidates(syntax, best[0].Function, candidates, reason);
            return best[0].Function;
        }

        reason = incomplete ? CandidateReason.Incomplete : CandidateReason.Ambiguous;
        RecordCandidates(syntax, null, candidates, reason);
        if (!incomplete)
        {
            bool duplicateIdentity = best.Select(item => item.Function).All(candidate =>
                candidate.HasSameOverloadSignature(best[0].Function));
            _diagnostics.Report(location,
                duplicateIdentity
                    ? $"function name '{best[0].Function.Name}' is ambiguous between {string.Join(" and ", best.Select(item => $"'{item.Function.FullName}'"))}"
                    : $"call to {description} is ambiguous between: {FormatCallableCandidates(best.Select(item => item.Function))}",
                duplicateIdentity ? DiagnosticIds.AmbiguousName : DiagnosticIds.AmbiguousCall);
        }
        RollbackUnmaterializedContextualArguments(arguments);
        return null;
    }

    private static string FormatCallableCandidates(IEnumerable<FunctionSymbol> candidates) =>
        string.Join(", ", candidates.Select(candidate =>
            $"'{candidate.ToDisplayString(SymbolDisplayFormat.Signature)}'"));

    private FunctionSymbol? ResolveExplicitGenericOverload(
        IEnumerable<FunctionSymbol> source,
        ImmutableArray<TypeSymbol> typeArguments,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        SyntaxNode syntax,
        int? completedArgumentCount)
    {
        FunctionSymbol[] named = source.Where(candidate => candidate.ContainingType is null).ToArray();
        FunctionSymbol[] candidates = named.Where(candidate =>
            candidate.TypeParameters.Length == typeArguments.Length && candidate.TypeParameters.Length != 0).ToArray();
        if (candidates.Length == 0)
        {
            RecordCandidates(syntax, null, named, CandidateReason.WrongArity);
            _diagnostics.Report(location,
                $"no generic overload accepts {typeArguments.Length} type argument(s); candidates: {FormatCallableCandidates(named)}",
                DiagnosticIds.GenericArityMismatch);
            RollbackUnmaterializedContextualArguments(arguments);
            return null;
        }

        // With one generic declaration there is no overload decision. Preserve
        // the dedicated specialization diagnostics for invalid constraints and
        // the ordinary argument diagnostics for a bad call.
        if (candidates.Length == 1)
        {
            int singleSuppliedCount = completedArgumentCount ?? arguments.Length;
            bool singleIncomplete = completedArgumentCount.HasValue;
            bool hasDeferredArgument = arguments.Take(singleSuppliedCount).Any(argument =>
                argument is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression);
            if (hasDeferredArgument)
            {
                FunctionSymbol candidate = candidates[0];
                var substitutions = candidate.TypeParameters.Zip(typeArguments)
                    .ToDictionary(pair => pair.First, pair => pair.Second);
                ImmutableArray<TypeSymbol> parameterTypes = candidate.Parameters.Select(parameter =>
                    SubstituteGenericType(parameter.Type, substitutions)).ToImmutableArray();
                CandidateReason failure = singleIncomplete
                    ? parameterTypes.Length < singleSuppliedCount ? CandidateReason.WrongArity : CandidateReason.None
                    : parameterTypes.Length != singleSuppliedCount ? CandidateReason.WrongArity : CandidateReason.None;
                if (failure == CandidateReason.None && parameterTypes.Take(singleSuppliedCount)
                        .Zip(arguments.Take(singleSuppliedCount)).Any(pair =>
                            GetArgumentConversionCost(pair.First, pair.Second) is null))
                    failure = CandidateReason.NotInvocable;
                if (failure != CandidateReason.None)
                {
                    if (failure == CandidateReason.NotInvocable)
                        for (int index = 0; index < singleSuppliedCount; index++)
                            if (GetArgumentConversionCost(parameterTypes[index], arguments[index]) is null)
                                ReportDeferredContextualMismatch(arguments[index], parameterTypes[index]);
                    RecordCandidates(syntax, null, candidates,
                        singleIncomplete ? CandidateReason.Incomplete : failure);
                    if (!singleIncomplete)
                        _diagnostics.Report(location,
                            $"no generic overload matches the provided arguments; candidates: {FormatCallableCandidates(candidates)}",
                            failure == CandidateReason.WrongArity ? DiagnosticIds.WrongArity : DiagnosticIds.NoMatchingCandidate);
                    if (failure == CandidateReason.NotInvocable)
                        PreserveDeferredDependentMoves(arguments);
                    RollbackUnmaterializedContextualArguments(arguments);
                    return null;
                }
            }
            RecordCandidates(syntax, candidates[0], candidates,
                completedArgumentCount.HasValue ? CandidateReason.Incomplete : CandidateReason.None);
            return candidates[0];
        }

        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        var validator = new GenericConstraintValidator();
        var matches = new List<(FunctionSymbol Function, int[] Costs)>();
        foreach (FunctionSymbol candidate in candidates)
        {
            var substitutions = candidate.TypeParameters.Zip(typeArguments)
                .ToDictionary(pair => pair.First, pair => pair.Second);
            TypeSymbol SubstituteConstraint(TypeSymbol type) => SubstituteGenericType(type, substitutions);
            bool validConstraints = candidate.TypeParameters.Zip(typeArguments).All(pair =>
                pair.Second is GenericParameterSymbol generic
                    ? pair.First.Constraints.All(required =>
                        GenericConstraintGuarantees.IsGuaranteed(generic, required, SubstituteConstraint))
                    : validator.Validate(pair.First, pair.Second, SubstituteConstraint).IsValid);
            if (!validConstraints) continue;
            if (incomplete ? candidate.Parameters.Length < suppliedCount : candidate.Parameters.Length != suppliedCount)
                continue;
            TypeSymbol[] parameterTypes = candidate.Parameters.Select(parameter =>
                SubstituteGenericType(parameter.Type, substitutions)).ToArray();
            int?[] costs = parameterTypes.Take(suppliedCount).Zip(arguments.Take(suppliedCount))
                .Select(pair => GetArgumentConversionCost(pair.First, pair.Second)).ToArray();
            if (costs.All(cost => cost.HasValue))
                matches.Add((candidate,costs.Select(cost => cost!.Value).ToArray()));
        }

        var best = matches.Where(candidate => !matches.Any(other =>
            !ReferenceEquals(candidate.Function, other.Function) &&
            IsBetterConversionSequence(other.Costs, candidate.Costs))).ToArray();
        if (best.Length == 1)
        {
            RecordCandidates(syntax, best[0].Function, candidates,
                incomplete ? CandidateReason.Incomplete : CandidateReason.None);
            return best[0].Function;
        }

        CandidateReason reason = best.Length > 1 ? CandidateReason.Ambiguous : CandidateReason.NotInvocable;
        RecordCandidates(syntax, null, candidates, incomplete ? CandidateReason.Incomplete : reason);
        if (!incomplete)
            _diagnostics.Report(location, best.Length > 1
                    ? $"generic call is ambiguous between: {FormatCallableCandidates(best.Select(item => item.Function))}"
                    : $"no generic overload matches the provided arguments; candidates: {FormatCallableCandidates(candidates)}",
                best.Length > 1 ? DiagnosticIds.AmbiguousCall : DiagnosticIds.NoMatchingCandidate);
        RollbackUnmaterializedContextualArguments(arguments);
        return null;
    }

    private ImmutableArray<BoundExpression> ValidateFunctionArguments(
        FunctionSymbol function,
        ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax,
        TextLocation location,
        int? completedArgumentCount = null)
    {
        PreserveDeferredDependentMoves(arguments);
        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        bool valid = !incomplete && suppliedCount == function.Parameters.Length;
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
            TextLocation argumentLocation = argumentSyntax.IsEmpty
                ? location
                : GetLocation(argumentSyntax[index]);
            ExpressionSyntax syntax = argumentSyntax.IsEmpty
                ? arguments[index] switch
                {
                    BoundUnboundLambdaExpression lambda => lambda.Syntax,
                    BoundUnboundFunctionExpression deferredFunction => deferredFunction.Syntax,
                    _ => null!,
                }
                : argumentSyntax[index];
            BoundExpression argument = MaterializeContextualArgument(arguments[index], syntax, parameterType);
            argument = ContextualizeConversion(argument, parameterType, argumentLocation);
            convertedArguments[index] = argument;
            if (argument is BoundErrorExpression &&
                arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression)
                valid = false;
            if (!argumentSyntax.IsEmpty) SetConvertedType(argumentSyntax[index], argument.Type);


            if (!TypeFacts.CanAssign(parameterType, argument.Type))
            {
                valid = false;
                ReportCannotConvert((argumentSyntax.IsEmpty ? location : GetLocation(argumentSyntax[index])), argument.Type, parameterType);
            }

        }
        CompleteDeferredContextualArguments(arguments, valid);
        return convertedArguments.ToImmutable();
    }

    private BoundExpression MaterializeContextualArgument(
        BoundExpression argument,
        ExpressionSyntax syntax,
        TypeSymbol expectedType) => argument switch
    {
        BoundUnboundLambdaExpression or BoundUnboundFunctionExpression =>
            BindExpressionWithExpectedType(syntax, expectedType),
        _ => argument,
    };

    private void ReportDeferredContextualMismatch(BoundExpression argument, TypeSymbol expectedType)
    {
        if (argument is BoundUnboundLambdaExpression lambda)
        {
            bool rawCapture = expectedType is FunctionPointerTypeSymbol && !lambda.Syntax.Captures.IsEmpty;
            _diagnostics.Report(rawCapture ? lambda.Syntax.OpenBracketToken!.Location : lambda.Syntax.IntroducerToken.Location,
                rawCapture
                    ? "capturing lambda cannot convert to raw function pointer"
                    : $"lambda is not compatible with contextual type '{expectedType.ToDisplayString()}'",
                rawCapture ? DiagnosticIds.LambdaCaptureNotSupported : DiagnosticIds.TypeMismatch);
        }
        else if (argument is BoundUnboundFunctionExpression function)
        {
            SyntaxToken token = GetNamedFunctionToken(function.Syntax);
            _diagnostics.Report(token.Location,
                expectedType is FunctionPointerTypeSymbol
                    ? $"function '{token.Text}' requires explicit address-of '&{token.Text}' in a raw function-pointer context"
                    : $"function '{token.Text}' is not compatible with contextual type '{expectedType.ToDisplayString()}'",
                DiagnosticIds.TypeMismatch);
        }
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
        return ResolveCallableOverload(candidates, arguments, location,
            $"interface method '{type.Name}.{name}'", syntax, out _, readonlyReceiver,
            completedArgumentCount);
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
            RollbackUnmaterializedContextualArguments(arguments);
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
            RollbackUnmaterializedContextualArguments(arguments);
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
            RollbackUnmaterializedContextualArguments(arguments);
            return null;
        }

        reason = CandidateReason.Ambiguous;
        _diagnostics.Report(location, $"constructor call for struct '{type.Name}' is ambiguous",
            DiagnosticIds.AmbiguousCall);
        RollbackUnmaterializedContextualArguments(arguments);
        return null;
    }

    private IndexerSymbol? ResolveIndexer(
        IEnumerable<IndexerSymbol> candidates,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName,
        SyntaxNode syntax,
        bool receiverIsReadonly) =>
        ResolveIndexerCore(candidates, indexer => indexer.Parameters, indexer => indexer.IsReadonly,
            arguments, location, ownerName, syntax, receiverIsReadonly);

    private InterfaceIndexerSymbol? ResolveIndexer(
        IEnumerable<InterfaceIndexerSymbol> candidates,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName,
        SyntaxNode syntax,
        bool receiverIsReadonly) =>
        ResolveIndexerCore(candidates, indexer => indexer.Parameters, indexer => indexer.IsReadonly,
            arguments, location, ownerName, syntax, receiverIsReadonly);

    private TIndexer? ResolveIndexerCore<TIndexer>(
        IEnumerable<TIndexer> candidates,
        Func<TIndexer, ImmutableArray<ParameterSymbol>> getParameters,
        Func<TIndexer, bool> getIsReadonly,
        ImmutableArray<BoundExpression> arguments,
        TextLocation location,
        string ownerName,
        SyntaxNode syntax,
        bool receiverIsReadonly)
        where TIndexer : Symbol
    {
        TIndexer[] candidateArray = candidates.ToArray();
        var matches = candidateArray
            .Where(candidate => !receiverIsReadonly || getIsReadonly(candidate))
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
            bool readonlyBlocked = receiverIsReadonly && candidateArray.Length != 0 &&
                candidateArray.All(candidate => !getIsReadonly(candidate));
            CandidateReason reason = readonlyBlocked ? CandidateReason.Inaccessible
                : candidateArray.Length == 0 ? CandidateReason.NotFound
                : candidateArray.All(candidate => getParameters(candidate).Length != arguments.Length)
                    ? CandidateReason.WrongArity : CandidateReason.NotInvocable;
            RecordCandidates(syntax, null, candidateArray, reason);
            if (!readonlyBlocked)
                _diagnostics.Report(location, $"no indexer of type '{ownerName}' matches the provided arguments",
                    reason == CandidateReason.WrongArity ? DiagnosticIds.WrongArity : DiagnosticIds.NoMatchingCandidate);
            RollbackUnmaterializedContextualArguments(arguments);
            return null;
        }

        TIndexer[] bestMatches = matches
            .Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other, candidate) &&
                IsBetterConversionSequence(other.Costs, candidate.Costs)))
            .Select(candidate => candidate.Indexer)
            .ToArray();
        if (bestMatches.Length > 1 && !receiverIsReadonly)
        {
            TIndexer[] mutable = bestMatches.Where(candidate => !getIsReadonly(candidate)).ToArray();
            if (mutable.Length == 1 && bestMatches.All(candidate =>
                    TypeSignature.Parameters(getParameters(candidate)) ==
                    TypeSignature.Parameters(getParameters(mutable[0]))))
                bestMatches = mutable;
        }
        if (bestMatches.Length == 1)
        {
            RecordCandidates(syntax, bestMatches[0], candidateArray, CandidateReason.None);
            return bestMatches[0];
        }

        RecordCandidates(syntax, null, candidateArray, CandidateReason.Ambiguous);
        _diagnostics.Report(location, $"indexer access on type '{ownerName}' is ambiguous",
            DiagnosticIds.AmbiguousCall);
        RollbackUnmaterializedContextualArguments(arguments);
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

    private int? GetArgumentConversionCost(TypeSymbol parameterType, BoundExpression argument)
    {
        if (argument is BoundUnboundLambdaExpression lambda)
            return GetLambdaArgumentConversionCost(parameterType, lambda.Syntax);
        if (argument is BoundUnboundFunctionExpression function)
            return GetNamedFunctionArgumentConversionCost(parameterType, function.Syntax);
        int? standard = GetStandardArgumentConversionCost(parameterType, argument);
        if (standard is not null || _userConversionDepth != 0) return standard;
        return FindUserConversions(argument, parameterType, explicitContext: false).Length != 0 ? int.MaxValue - 1 : null;
    }

    private static int? GetStandardArgumentConversionCost(TypeSymbol parameterType, BoundExpression argument)
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
        int? directCost = TypeFacts.GetReferenceBindingCost(referenceType, argument.Type);
        if (directCost is not null) return directCost;
        return TryGetStorageType(argument.Type, out StorageTypeSymbol storage)
            ? TypeFacts.GetReferenceBindingCost(referenceType, storage.ElementType)
            : null;
    }

    private static BoundExpression ContextualizeNull(BoundExpression expression, TypeSymbol targetType)
    {
        if (expression is BoundLiteralExpression { Value: null } &&
            TypeIdentity.AreSame(expression.Type, BuiltinTypes.Null) &&
            targetType is PointerTypeSymbol or FunctionPointerTypeSymbol or SharedTypeSymbol)
        {
            return new BoundLiteralExpression(null, targetType);
        }

        return expression;
    }

    private BoundExpression ContextualizeConversion(
        BoundExpression expression,
        TypeSymbol targetType,
        TextLocation copyLocation)
    {
        if (_userConversionDepth == 0 && GetStandardArgumentConversionCost(targetType, expression) is null &&
            TryBindUserConversion(expression, targetType, copyLocation, explicitContext: false,
                out BoundExpression? conversion))
            return conversion!;
        if (targetType is UniqueTypeSymbol uniqueType)
        {
            ValidateDestructorAccessibility(uniqueType, copyLocation);
            if (TypeIdentity.AreSame(expression.Type, uniqueType))
                return ApplyCopySemantics(expression, copyLocation);

            bool compatibleFreshAllocation = expression switch
            {
                BoundNewExpression @new => TypeIdentity.AreSame(@new.Type, uniqueType.StorageType),
                BoundArrayCreationExpression { Storage: ArrayStorageKind.Heap } array =>
                    TypeIdentity.AreSame(array.Type, uniqueType.StorageType),
                _ => false,
            };
            if (compatibleFreshAllocation)
                return new BoundUniqueAdoptionExpression(expression, uniqueType);

            if (TypeIdentity.AreSame(expression.Type, uniqueType.StorageType) &&
                !TypeIdentity.AreSame(expression.Type, BuiltinTypes.Error))
                _diagnostics.Report(copyLocation,
                    $"a raw value of type '{expression.Type.ToDisplayString()}' cannot be adopted by '{uniqueType.ToDisplayString()}'; only a fresh 'new' allocation may be adopted",
                    DiagnosticIds.UniqueRequiresFreshAllocation);
            return expression;
        }

        if (targetType is SharedTypeSymbol sharedType)
        {
            ValidateDestructorAccessibility(sharedType, copyLocation);
            expression = ReadAtomicValue(expression);
            expression = ContextualizeNull(expression, targetType);
            if (TypeIdentity.AreSame(expression.Type, sharedType))
                return ApplyCopySemantics(expression, copyLocation);

            bool compatibleFreshAllocation = expression switch
            {
                BoundNewExpression @new => TypeIdentity.AreSame(@new.Type, sharedType.StorageType),
                BoundArrayCreationExpression { Storage: ArrayStorageKind.Heap } array =>
                    TypeIdentity.AreSame(array.Type, sharedType.StorageType),
                _ => false,
            };
            if (compatibleFreshAllocation)
                return new BoundSharedAdoptionExpression(expression, sharedType);

            if (TypeIdentity.AreSame(expression.Type, sharedType.StorageType) &&
                !TypeIdentity.AreSame(expression.Type, BuiltinTypes.Error))
                _diagnostics.Report(copyLocation,
                    $"a raw value of type '{expression.Type.ToDisplayString()}' cannot be adopted by '{sharedType.ToDisplayString()}'; only a fresh 'new' allocation may be adopted",
                    DiagnosticIds.SharedRequiresFreshAllocation);
            return expression;
        }

        if (targetType is WeakTypeSymbol weakType)
        {
            expression = ReadAtomicValue(expression);
            if (TypeIdentity.AreSame(expression.Type, weakType))
                return ApplyCopySemantics(expression, copyLocation);
            if (expression.Type is SharedTypeSymbol shared &&
                TypeIdentity.AreSame(shared.ElementType, weakType.ElementType) &&
                expression is not BoundMoveExpression)
                return new BoundWeakConversionExpression(expression, weakType);
            if (!TypeIdentity.AreSame(expression.Type, BuiltinTypes.Error))
                _diagnostics.Report(copyLocation,
                    $"'{weakType.ToDisplayString()}' can only be created from a live matching shared owner",
                    DiagnosticIds.WeakRequiresSharedOwner);
            return expression;
        }

        if (targetType is ReferenceTypeSymbol referenceType)
        {
            if (expression is BoundMoveExpression)
                return expression;
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
            if (TypeFacts.GetReferenceBindingCost(referenceType, expression.Type) is null &&
                TryGetStorageType(expression.Type, out StorageTypeSymbol storage) &&
                TypeFacts.GetReferenceBindingCost(referenceType, storage.ElementType) is not null)
                expression = ExposeLifetimeValue(expression, copyLocation);
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

        expression = ReadAtomicValue(expression);
        expression = ContextualizeNull(expression, targetType);
        if (targetType is InterfaceTypeSymbol @interface &&
            expression.Type is StructTypeSymbol source && source.Implements(@interface))
        {
            expression = new BoundInterfaceConversionExpression(expression, source, @interface);
        }
        return ApplyCopySemantics(expression, copyLocation);
    }

    private static BoundExpression ReadAtomicValue(BoundExpression expression) =>
        expression.Type is AtomicTypeSymbol atomic && AtomicTypeRules.SupportsOperations(atomic.ElementType)
            ? new BoundCastExpression(expression, atomic.ElementType)
            : expression;

    private BoundExpression ApplyCopySemantics(BoundExpression expression, TextLocation location)
    {
        if (expression is BoundMoveExpression || !IsValueCopySource(expression)) return expression;
        CopyabilityFailure? failure = TypeFacts.GetCopyabilityFailure(expression.Type);
        if (failure is not null)
        {
            string path = failure.FieldPath.IsEmpty ? string.Empty
                : $" through field '{string.Join('.', failure.FieldPath.Select(field => field.Name))}'";
            if (failure.Type is AtomicTypeSymbol || TypeFacts.ContainsAtomicStorage(expression.Type))
            {
                _diagnostics.Report(location,
                    $"type '{expression.Type.ToDisplayString()}' contains location-bound atomic storage{path} and cannot be implicitly copied or relocated",
                    DiagnosticIds.AtomicStorageNotRelocatable);
                return expression;
            }
            string reason = failure.Kind == Copyability.NotGuaranteed
                ? $"copyability of generic type '{failure.Type.ToDisplayString()}' is not guaranteed{path}"
                : $"type '{expression.Type.ToDisplayString()}' cannot be copied{path}";
            _diagnostics.Report(location, $"{reason}; use 'move' when ownership transfer is intended",
                DiagnosticIds.ValueNotCopyable);
            return expression;
        }

        return expression.Type is StructTypeSymbol or SharedTypeSymbol or WeakTypeSymbol or FunctionValueTypeSymbol
            ? new BoundCopyExpression(expression)
            : expression;
    }

    private static bool IsValueCopySource(BoundExpression expression) => expression switch
    {
        BoundVariableExpression => true,
        BoundMemberAccessExpression => true,
        BoundStaticFieldExpression => true,
        BoundIndexExpression => true,
        BoundReferenceDereferenceExpression => true,
        BoundLifetimeValueExpression value => IsAddressable(value.Source),
        BoundAssignmentExpression => true,
        BoundInterfaceConversionExpression conversion => IsValueCopySource(conversion.Source),
        _ => false,
    };

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
            {
                if (!field.HasInitializer && TypeFacts.ContainsReferenceStorage(field.Type))
                    _diagnostics.Report(location, $"field '{field.Name}' contains a reference and requires explicit initialization",
                        DiagnosticIds.ReferenceFieldRequiresExplicitInitialization);
                if (!field.HasInitializer && field.Type is PinTypeSymbol)
                    _diagnostics.Report(location, $"pinned field '{field.Name}' requires final-destination initialization",
                        DiagnosticIds.PinnedRelocation);
            }
        }
    }

    private BoundExpression BindFieldReceiver(ExpressionSyntax syntax)
    {
        ExpressionSyntax? previous = _fieldReceiverSyntax;
        _fieldReceiverSyntax = syntax;
        try
        {
            BoundExpression receiver = ExposeLifetimeValue(BindExpression(syntax), syntax);
            _semanticInfo.Receivers[syntax] = new ReceiverInfo(
                receiver.Type,
                IsStatic: false,
                IsReadonly: receiver.Type is PointerTypeSymbol { IsReadonly: true } or
                    ReferenceTypeSymbol { IsReadonly: true } ||
                    IsAddressable(receiver) && !IsWritable(receiver),
                IsWritable: IsWritable(receiver));
            return receiver;
        }
        finally { _fieldReceiverSyntax = previous; }
    }

    private BoundExpression ExposeLifetimeValue(BoundExpression expression, ExpressionSyntax syntax) =>
        ExposeLifetimeValue(expression, GetLocation(syntax));

    private BoundExpression ExposeLifetimeValue(BoundExpression expression, TextLocation location)
    {
        while (expression.Type is LifetimeModifierTypeSymbol modifier)
        {
            expression = new BoundLifetimeValueExpression(expression, modifier);
        }
        return expression;
    }

    private BoundExpression BindDiscardedExpression(ExpressionSyntax syntax)
    {
        ExpressionSyntax? ownershipExpression = GetStandaloneOwnershipExpression(syntax);
        ExpressionSyntax? previous = _unconsumedOwnershipExpression;
        _unconsumedOwnershipExpression = ownershipExpression;
        BoundExpression expression;
        try { expression = BindExpression(syntax); }
        finally { _unconsumedOwnershipExpression = previous; }

        if (ownershipExpression is not null && expression is not BoundErrorExpression)
        {
            (SyntaxToken keyword,string operation) = ownershipExpression switch
            {
                MoveExpressionSyntax move => (move.MoveKeyword,"move"),
                LockExpressionSyntax @lock => (@lock.LockKeyword,"lock"),
                NewExpressionSyntax @new => (@new.NewKeyword,"new"),
                _ => throw new InvalidOperationException(),
            };
            _diagnostics.Report(keyword.Location,
                $"result of '{operation}' must be consumed",
                DiagnosticIds.UnconsumedOwnershipExpression);
            return new BoundErrorExpression();
        }

        return CompleteFullExpression(expression, resultConsumed: false);
    }

    private static ExpressionSyntax? GetStandaloneOwnershipExpression(ExpressionSyntax syntax)
    {
        while (syntax is ParenthesizedExpressionSyntax parenthesized)
            syntax = parenthesized.Expression;
        return syntax is MoveExpressionSyntax or LockExpressionSyntax or NewExpressionSyntax ? syntax : null;
    }

    private BoundExpression CompleteFullExpression(BoundExpression expression, bool resultConsumed)
    {
        var temporaries = ImmutableArray.CreateBuilder<BoundFullExpressionTemporary>();
        var registered = new HashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        var transferred = new HashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        CollectFullExpressionTemporaries(expression, resultConsumed, temporaries, registered, transferred);
        if (temporaries.Any(temporary => !transferred.Contains(temporary.Value)))
        {
            // These temporaries remain live after the expression's normal value has
            // been computed. Their destructors therefore observe the final flow state.
            RecordExceptionalReferenceBinding();
        }
        if (temporaries.Count == 0 && !RequiresFullExpressionStackFrame(expression)) return expression;
        var fullExpression = new BoundFullExpression(expression, temporaries.ToImmutable());
        if (_expressionLocations.TryGetValue(expression, out TextLocation location))
            _expressionLocations[fullExpression] = location;
        return fullExpression;
    }

    private static bool RequiresFullExpressionStackFrame(BoundExpression expression) =>
        expression switch
        {
            BoundAssignmentExpression assignment when
                TypeFacts.GetCompleteDestructor(assignment.Target.Type) is not null => true,
            BoundCompareExchangeExpression { Target.Type: AtomicTypeSymbol atomic } when
                TypeFacts.GetCompleteDestructor(atomic.ElementType) is not null => true,
            _ => false,
        };

    private void CollectFullExpressionTemporaries(
        BoundExpression expression,
        bool consumed,
        ImmutableArray<BoundFullExpressionTemporary>.Builder temporaries,
        HashSet<BoundExpression> registered,
        HashSet<BoundExpression> transferred)
    {
        void Visit(BoundExpression child, bool childConsumed = false) =>
            CollectFullExpressionTemporaries(child, childConsumed, temporaries, registered, transferred);
        void TransferAll(IEnumerable<BoundExpression> children)
        {
            // A call accepts its arguments only after every argument was evaluated.
            // Keep destructible argument values guarded until codegen reaches that
            // normal edge; a later argument may still throw.
            foreach (BoundExpression child in children)
            {
                transferred.Add(UnwrapDirectTransferredExpression(child));
                Visit(child);
            }
        }

        switch (expression)
        {
            case BoundAwaitExpression awaitExpression:
                Visit(awaitExpression.Operand);
                break;
            case BoundFullExpression full:
                Visit(full.Expression, consumed);
                break;
            case BoundUnaryExpression unary:
                Visit(unary.Operand);
                break;
            case BoundMoveExpression move:
                Visit(move.Source);
                break;
            case BoundCopyExpression copy:
                Visit(copy.Source);
                break;
            case BoundUniqueAdoptionExpression adoption:
                Visit(adoption.Allocation, childConsumed: true);
                break;
            case BoundSharedAdoptionExpression adoption:
                Visit(adoption.Allocation, childConsumed: true);
                break;
            case BoundWeakConversionExpression conversion:
                Visit(conversion.Shared);
                break;
            case BoundLockExpression @lock:
                Visit(@lock.Weak);
                break;
            case BoundBinaryExpression binary:
                Visit(binary.Left);
                Visit(binary.Right);
                break;
            case BoundAssignmentExpression assignment:
                Visit(assignment.Target);
                Visit(assignment.Expression, childConsumed: true);
                break;
            case BoundCompareExchangeExpression compareExchange:
                Visit(compareExchange.Target);
                Visit(compareExchange.Expected, childConsumed: true);
                Visit(compareExchange.Desired, childConsumed: true);
                break;
            case BoundSwapExpression swap:
                Visit(swap.Left);
                Visit(swap.Right);
                break;
            case BoundCompoundAccessorAssignmentExpression assignment:
                Visit(assignment.Receiver);
                TransferAll(assignment.Arguments);
                Visit(assignment.Value, childConsumed: true);
                break;
            case BoundCallExpression call:
                TransferAll(call.Arguments);
                break;
            case BoundDeferredGenericOperationExpression { Operation: BoundDeferredGenericOperationKind.OperatorCall } call:
                TransferAll(call.Arguments);
                break;
            case BoundIndirectCallExpression call:
                Visit(call.Target);
                TransferAll(call.Arguments);
                break;
            case BoundFunctionAddressExpression:
                break;
            case BoundMethodCallExpression call:
                Visit(call.Receiver);
                TransferAll(call.Arguments);
                break;
            case BoundInterfaceMethodCallExpression call:
                Visit(call.Receiver);
                TransferAll(call.Arguments);
                break;
            case BoundPropertySetExpression set:
                Visit(set.Receiver);
                Visit(set.Value, childConsumed: true);
                break;
            case BoundInterfacePropertySetExpression set:
                Visit(set.Receiver);
                Visit(set.Value, childConsumed: true);
                break;
            case BoundIndexerSetExpression set:
                Visit(set.Receiver);
                TransferAll(set.Arguments);
                Visit(set.Value, childConsumed: true);
                break;
            case BoundInterfaceIndexerSetExpression set:
                Visit(set.Receiver);
                TransferAll(set.Arguments);
                Visit(set.Value, childConsumed: true);
                break;
            case BoundMemberAccessExpression member:
                Visit(member.Receiver);
                break;
            case BoundCastExpression cast:
                Visit(cast.Expression);
                break;
            case BoundInterfaceConversionExpression conversion:
                Visit(conversion.Source);
                break;
            case BoundReferenceConversionExpression conversion:
                Visit(conversion.Source);
                break;
            case BoundReferenceDereferenceExpression dereference:
                Visit(dereference.Reference);
                break;
            case BoundLifetimeValueExpression value:
                Visit(value.Source);
                break;
            case BoundStorageConstructExpression construction:
                Visit(construction.Storage);
                if (construction.Value is { } constructedValue) Visit(constructedValue, childConsumed: true);
                TransferAll(construction.Arguments);
                break;
            case BoundExplicitDestructExpression destruction:
                Visit(destruction.Target);
                break;
            case BoundStorageMoveExpression move:
                Visit(move.Storage);
                break;
            case BoundIndexExpression index:
                Visit(index.Receiver);
                foreach (BoundExpression argument in index.Indices) Visit(argument);
                break;
            case BoundStructConstructionExpression construction:
                TransferAll(construction.Arguments);
                break;
            case BoundConstructorCallExpression construction:
                TransferAll(construction.Arguments);
                break;
            case BoundBaseLifecycleCallExpression call:
                TransferAll(call.Arguments);
                break;
            case BoundArrayCreationExpression array:
                foreach (BoundExpression dimension in array.Dimensions) Visit(dimension);
                break;
            case BoundArrayMetadataExpression metadata:
                Visit(metadata.Receiver);
                if (metadata.Dimension is { } metadataDimension) Visit(metadataDimension);
                break;
            case BoundNewExpression allocation:
                TransferAll(allocation.Arguments);
                break;
            case BoundFreeExpression free:
                Visit(free.Pointer);
                break;
            case BoundDeleteExpression deletion:
                Visit(deletion.Pointer);
                break;
            case BoundRawAllocationExpression allocation:
                TransferAll(allocation.Arguments);
                break;
        }

        if (consumed || !IsTemporaryValue(expression) || !registered.Add(expression) ||
            TypeFacts.GetCompleteDestructor(expression.Type) is not { } destructor)
            return;
        TextLocation location = _expressionLocations.GetValueOrDefault(expression);
        ValidateDestructorAccessibility(expression.Type, location);
        temporaries.Add(new BoundFullExpressionTemporary(expression, destructor));
    }

    private static BoundExpression UnwrapDirectTransferredExpression(BoundExpression expression) =>
        expression switch
        {
            BoundFullExpression full => UnwrapDirectTransferredExpression(full.Expression),
            _ => expression,
        };

    private static bool IsTemporaryValue(BoundExpression expression) => expression is
        BoundCapturedPlaceExpression { OwnsValue: true } or
        BoundMoveExpression or
        BoundAwaitExpression or
        BoundCopyExpression or
        BoundUniqueAdoptionExpression or
        BoundSharedAdoptionExpression or
        BoundWeakConversionExpression or
        BoundLockExpression or
        BoundCallExpression or
        BoundDeferredGenericOperationExpression { Operation: BoundDeferredGenericOperationKind.OperatorCall } or
        BoundIndirectCallExpression or
        BoundMethodCallExpression or
        BoundInterfaceMethodCallExpression or
        BoundStorageMoveExpression or
        BoundStructConstructionExpression or
        BoundConstructorCallExpression;

    private static bool TryGetStorageType(TypeSymbol type, out StorageTypeSymbol storage)
    {
        while (type is PinTypeSymbol pin) type = pin.ElementType;
        storage = type as StorageTypeSymbol ?? null!;
        return type is StorageTypeSymbol;
    }

    private static TypeSymbol UnwrapExplicitDestructionType(TypeSymbol type)
    {
        while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
        return type;
    }

    private static TypeSymbol GetAddressedValueType(TypeSymbol type)
    {
        while (type is LifetimeModifierTypeSymbol modifier)
            type = modifier.ElementType;
        return type;
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
            SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken && sameType &&
            (TypeFacts.IsNumeric(left) || TypeFacts.IsCharacter(left)))
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
        LambdaExpressionSyntax lambda => lambda.IntroducerToken.Location,
        LiteralExpressionSyntax literal => literal.LiteralToken.Location,
        NameExpressionSyntax name => name.IdentifierToken.Location,
        ThisExpressionSyntax @this => @this.ThisKeyword.Location,
        ParenthesizedExpressionSyntax parenthesized => parenthesized.OpenParenthesisToken.Location,
        AwaitExpressionSyntax awaitExpression => awaitExpression.AwaitKeyword.Location,
        MoveExpressionSyntax move => move.MoveKeyword.Location,
        LockExpressionSyntax @lock => @lock.LockKeyword.Location,
        UnaryExpressionSyntax unary => unary.OperatorToken.Location,
        PostfixUnaryExpressionSyntax postfix => postfix.OperatorToken.Location,
        BinaryExpressionSyntax binary => binary.OperatorToken.Location,
        AssignmentExpressionSyntax assignment => assignment.OperatorToken.Location,
        CompareExchangeExpressionSyntax compareExchange => compareExchange.ColonToken.Location,
        SwapExpressionSyntax swap => swap.OperatorToken.Location,
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
        BlockStatementSyntax block => (block.CloseBraceToken.Location.Span.End,block.CloseBraceToken.IsMissing),
        VariableDeclarationStatementSyntax variable => (variable.SemicolonToken.Location.Span.End,variable.SemicolonToken.IsMissing),
        ReturnStatementSyntax @return => (@return.SemicolonToken.Location.Span.End,@return.SemicolonToken.IsMissing),
        ExpressionStatementSyntax expression => (expression.SemicolonToken.Location.Span.End,expression.SemicolonToken.IsMissing),
        IfStatementSyntax @if when @if.ElseStatement is not null => GetStatementEnd(@if.ElseStatement),
        IfStatementSyntax @if => GetStatementEnd(@if.ThenStatement),
        WhileStatementSyntax @while => GetStatementEnd(@while.Body),
        ForStatementSyntax @for => GetStatementEnd(@for.Body),
        BreakStatementSyntax @break => (@break.SemicolonToken.Location.Span.End,@break.SemicolonToken.IsMissing),
        ContinueStatementSyntax @continue => (@continue.SemicolonToken.Location.Span.End,@continue.SemicolonToken.IsMissing),
        ThrowStatementSyntax @throw => (@throw.SemicolonToken.Location.Span.End,@throw.SemicolonToken.IsMissing),
        TryStatementSyntax @try when @try.FinallyBody is not null => GetStatementEnd(@try.FinallyBody),
        TryStatementSyntax @try when !@try.Catches.IsEmpty => GetStatementEnd(@try.Catches[^1].Body),
        TryStatementSyntax @try => GetStatementEnd(@try.Body),
        SwitchStatementSyntax @switch when @switch.CloseBraceToken is { } close => (close.Location.Span.End,close.IsMissing),
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
        BoundCopyExpression copy => GetReferencedSymbol(copy.Source),
        BoundMoveExpression move => GetReferencedSymbol(move.Source),
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
            BoundExpression fieldValue = isSimpleAssignment
                ? BindExpressionWithExpectedType(syntax.Expression, field.Type)
                : BindExpression(syntax.Expression);
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
            return new BoundDeferredGenericOperationExpression(
                BoundDeferredGenericOperationKind.FieldSet, receiver, field.Symbol, [], fieldValue,
                syntax.OperatorToken.Kind, pointerAccess, field.Type);
        }

        GenericPropertyMember[] candidates = GenericConstraintMemberLookup
            .GetProperties(parameter, target.MemberToken.Text, _fileScope.TypeFactory,
                _fileScope.GenericStructSpecializer)
            .Where(property => !property.IsStatic)
            .DistinctBy(property => (property.Type.ToDisplayString(TypeDisplayFormat.FullyQualified),                property.HasGetter,property.HasSetter,property.IsReadonly))
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
        BoundExpression value = isSimpleAssignment
            ? BindExpressionWithExpectedType(syntax.Expression, property.Type)
            : BindExpression(syntax.Expression);
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
        return new BoundDeferredGenericOperationExpression(
            BoundDeferredGenericOperationKind.PropertySet, receiver, property.Symbol, [], value,
            syntax.OperatorToken.Kind, pointerAccess, property.Type);
    }

    private BoundExpression BindGenericIndexerAssignment(AssignmentExpressionSyntax syntax,
        IndexExpressionSyntax target, BoundExpression receiver, GenericParameterSymbol parameter,
        bool isSimpleAssignment)
    {
        ImmutableArray<BoundExpression> indices = BindTransferredArguments(target.Arguments,
            (argument, _) => BindDeferredContextualArgument(argument));
        GenericIndexerMember[] candidates = GenericConstraintMemberLookup.GetIndexers(parameter,
                _fileScope.TypeFactory, _fileScope.GenericStructSpecializer)
            .Where(indexer => indexer.HasSetter && (isSimpleAssignment || indexer.HasGetter))
            .DistinctBy(indexer =>
                $"{indexer.Type.ToDisplayString(TypeDisplayFormat.FullyQualified)}({string.Join(",", indexer.ParameterTypes.Select(type => type.ToDisplayString(TypeDisplayFormat.FullyQualified)))})/{indexer.IsReadonly}")
            .ToArray();
        GenericIndexerMember? indexer = ResolveGenericCandidate(candidates, candidate => candidate.Symbol,
            candidate => candidate.ParameterTypes, indices, target.OpenBracketToken.Location,
            $"writable indexer on '{parameter.Name}'", target,
            getIsReadonly: candidate => candidate.IsReadonly, receiverIsReadonly: false);
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
            RollbackUnmaterializedContextualArguments(indices);
            return new BoundErrorExpression();
        }
        if (!isSimpleAssignment)
        {
            bool hasNoncopyableIndex = false;
            for (int index = 0; index < Math.Min(indices.Length, indexer.ParameterTypes.Length); index++)
            {
                TypeSymbol parameterType = indexer.ParameterTypes[index];
                if (parameterType is ReferenceTypeSymbol || TypeFacts.GetCopyabilityFailure(parameterType) is null)
                    continue;
                hasNoncopyableIndex = true;
                _diagnostics.Report(GetLocation(target.Arguments[index]),
                    $"compound indexer assignment cannot reuse noncopyable argument type '{parameterType.ToDisplayString()}' for both getter and setter",
                    DiagnosticIds.ValueNotCopyable);
            }
            if (hasNoncopyableIndex)
            {
                RollbackUnmaterializedContextualArguments(indices);
                _ = BindExpression(syntax.Expression);
                return new BoundErrorExpression();
            }
        }
        indices = ValidateGenericArguments("this", indexer.ParameterTypes, indices, target.Arguments,
            target.OpenBracketToken.Location);
        BoundExpression value = isSimpleAssignment
            ? BindExpressionWithExpectedType(syntax.Expression, indexer.Type)
            : BindExpression(syntax.Expression);
        if (isSimpleAssignment)
            value = ValidateGenericArguments("this", [indexer.Type], [value], [syntax.Expression],
                target.OpenBracketToken.Location)[0];
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
        return new BoundDeferredGenericOperationExpression(
            BoundDeferredGenericOperationKind.IndexerSet, receiver, indexer.Symbol, indices, value,
            syntax.OperatorToken.Kind, false, indexer.Type);
    }

    private BoundExpression BindGenericMemberGet(MemberAccessExpressionSyntax syntax, BoundExpression receiver,
        GenericParameterSymbol parameter, bool pointerAccess)
    {
        GenericFieldMember? field = GenericConstraintMemberLookup.GetFields(parameter, syntax.MemberToken.Text)
            .FirstOrDefault(candidate => !candidate.IsStatic);
        if (field is not null)
        {
            RecordSymbolAndType(syntax, field.Symbol, field.Type);
            return new BoundDeferredGenericOperationExpression(
                BoundDeferredGenericOperationKind.FieldGet, receiver, field.Symbol, [], null,
                SyntaxKind.EqualsToken, pointerAccess, field.Type);
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
            .DistinctBy(property => (property.Type.ToDisplayString(TypeDisplayFormat.FullyQualified),property.HasGetter,                property.HasSetter,property.IsReadonly))
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
        return new BoundDeferredGenericOperationExpression(
            BoundDeferredGenericOperationKind.PropertyGet, receiver, property.Symbol, [], null,
            SyntaxKind.EqualsToken, pointerAccess, property.Type);
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
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        arguments = ValidateGenericArguments(method.Symbol.Name, method.ParameterTypes, arguments, argumentSyntax,
            target.MemberToken.Location, incomplete ? completedArgumentCount : null);
        RecordSymbolAndType(target, method.Symbol, method.ReturnType);
        return new BoundDeferredGenericMethodCallExpression(receiver, method.Symbol, arguments,
            pointerAccess, method.ReturnType);
    }

    private BoundExpression BindGenericIndexerGet(IndexExpressionSyntax syntax, BoundExpression receiver,
        GenericParameterSymbol parameter, ImmutableArray<BoundExpression> arguments)
    {
        bool receiverIsReadonly = IsReadonlyReceiver(receiver, pointerAccess: false);
        GenericIndexerMember[] candidates = GenericConstraintMemberLookup.GetIndexers(parameter,
                _fileScope.TypeFactory, _fileScope.GenericStructSpecializer)
            .Where(indexer => indexer.HasGetter)
            .DistinctBy(indexer => $"{indexer.Type.ToDisplayString(TypeDisplayFormat.FullyQualified)}({string.Join(",", indexer.ParameterTypes.Select(type => type.ToDisplayString(TypeDisplayFormat.FullyQualified)))})/{indexer.IsReadonly}")
            .ToArray();
        GenericIndexerMember? indexer = ResolveGenericCandidate(candidates, candidate => candidate.Symbol,
            candidate => candidate.ParameterTypes, arguments, syntax.OpenBracketToken.Location,
            $"indexer on '{parameter.Name}'", syntax,
            getIsReadonly: candidate => candidate.IsReadonly, receiverIsReadonly: receiverIsReadonly);
        if (indexer is null)
        {
            if (candidates.Length == 0)
                _diagnostics.Report(syntax.OpenBracketToken.Location,
                    $"constraints for '{parameter.Name}' do not guarantee a readable indexer",
                    DiagnosticIds.GenericMemberNotGuaranteed);
            else if (receiverIsReadonly && candidates.All(candidate => !candidate.IsReadonly))
                _diagnostics.Report(syntax.OpenBracketToken.Location,
                    $"indexer is not guaranteed readonly for '{parameter.Name}'",
                    DiagnosticIds.MutableGetterOnReadonlyReceiver);
            return new BoundErrorExpression();
        }
        arguments = ValidateGenericArguments("this", indexer.ParameterTypes, arguments, syntax.Arguments,
            syntax.OpenBracketToken.Location);
        RecordSymbolAndType(syntax, indexer.Symbol, indexer.Type);
        return new BoundDeferredGenericOperationExpression(
            BoundDeferredGenericOperationKind.IndexerGet, receiver, indexer.Symbol, arguments, null,
            SyntaxKind.EqualsToken, false, indexer.Type);
    }

    private BoundExpression BindGenericNewExpression(NewExpressionSyntax syntax, GenericParameterSymbol parameter)
    {
        ImmutableArray<BoundExpression> arguments = BindTransferredArguments(syntax.Arguments,
            (argument, _) => BindDeferredContextualArgument(argument));
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
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        arguments = ValidateGenericArguments(parameter.Name, constructor.ParameterTypes, arguments, syntax.Arguments,
            syntax.NewKeyword.Location,
            syntax.CloseDelimiterToken.IsMissing ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        RecordSymbolAndType(syntax, constructor.Symbol, _fileScope.TypeFactory.PointerTo(parameter));
        return new BoundDeferredGenericOperationExpression(
            BoundDeferredGenericOperationKind.Allocation, null, constructor.Symbol, arguments, null,
            SyntaxKind.EqualsToken, false, _fileScope.TypeFactory.PointerTo(parameter));
    }

    private BoundExpression BindGenericConstructionExpression(CallExpressionSyntax syntax,
        GenericParameterSymbol parameter, ImmutableArray<BoundExpression> arguments)
    {
        GenericConstructorMember[] candidates = GenericConstraintMemberLookup
            .GetConstructors(parameter, _fileScope.TypeFactory, _fileScope.GenericStructSpecializer)
            .DistinctBy(constructor => string.Join(",", constructor.ParameterTypes.Select(type =>
                type.ToDisplayString(TypeDisplayFormat.FullyQualified))))
            .ToArray();
        bool incomplete = syntax.CloseParenthesisToken.IsMissing;
        GenericConstructorMember? constructor = ResolveGenericCandidate(candidates,
            candidate => candidate.Symbol, candidate => candidate.ParameterTypes, arguments,
            GetLocation(syntax.Target), $"constructor for '{parameter.Name}'", syntax.Target,
            incomplete ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        if (constructor is null)
        {
            if (candidates.Length == 0)
                _diagnostics.Report(GetLocation(syntax.Target),
                    $"constraints for '{parameter.Name}' do not guarantee this construction",
                    DiagnosticIds.GenericConstructorNotGuaranteed);
            RollbackUnmaterializedContextualArguments(arguments);
            return new BoundErrorExpression();
        }
        arguments = ValidateGenericArguments(parameter.Name, constructor.ParameterTypes, arguments, syntax.Arguments,
            GetLocation(syntax.Target),
            incomplete ? GetCompletedArgumentCount(syntax.Arguments, true) : null);
        RecordSymbolAndType(syntax.Target, constructor.Symbol, parameter);
        return new BoundDeferredGenericOperationExpression(
            BoundDeferredGenericOperationKind.Construction, null, constructor.Symbol, arguments, null,
            SyntaxKind.EqualsToken, false, parameter);
    }

    private T? ResolveGenericCandidate<T>(IEnumerable<T> source, Func<T, Symbol> getSymbol,
        Func<T, ImmutableArray<TypeSymbol>> getParameterTypes, ImmutableArray<BoundExpression> arguments,
        TextLocation location, string description, SyntaxNode syntax, int? completedArgumentCount = null,
        Func<T, bool>? getIsReadonly = null, bool? receiverIsReadonly = null)
        where T : class
    {
        T[] candidates = source.ToArray();
        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        var matches = candidates.Where(candidate =>
                receiverIsReadonly != true || getIsReadonly?.Invoke(candidate) != false)
            .Where(candidate =>
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
        T[] bestMatches = matches
            .Where(candidate => !matches.Any(other =>
                !ReferenceEquals(other.Candidate, candidate.Candidate) &&
                IsBetterConversionSequence(other.Costs, candidate.Costs)))
            .Select(candidate => candidate.Candidate)
            .ToArray();
        if (bestMatches.Length > 1 && receiverIsReadonly == false && getIsReadonly is not null)
        {
            T[] mutable = bestMatches.Where(candidate => !getIsReadonly(candidate)).ToArray();
            if (mutable.Length == 1 && bestMatches.All(candidate =>
                    HaveSameTypes(getParameterTypes(candidate), getParameterTypes(mutable[0]))))
                bestMatches = mutable;
        }
        if (bestMatches.Length == 1)
        {
            RecordCandidates(syntax, getSymbol(bestMatches[0]), candidates.Select(getSymbol),
                incomplete ? CandidateReason.Incomplete : CandidateReason.None);
            return bestMatches[0];
        }
        bool readonlyBlocked = receiverIsReadonly == true && candidates.Length != 0 &&
            getIsReadonly is not null && candidates.All(candidate => !getIsReadonly(candidate));
        CandidateReason reason = readonlyBlocked ? CandidateReason.Inaccessible
            : candidates.Length == 0 ? CandidateReason.NotFound
            : bestMatches.Length > 1 ? CandidateReason.Ambiguous
            : candidates.All(candidate => getParameterTypes(candidate).Length != suppliedCount)
                ? CandidateReason.WrongArity : CandidateReason.NotInvocable;
        RecordCandidates(syntax, null, candidates.Select(getSymbol), reason);
        if (candidates.Length != 0 && !incomplete && !readonlyBlocked)
            _diagnostics.Report(location, bestMatches.Length > 1 ? $"{description} is ambiguous" :
                $"no {description} matches the provided arguments",
                bestMatches.Length > 1 ? DiagnosticIds.AmbiguousCall :
                reason == CandidateReason.WrongArity ? DiagnosticIds.WrongArity : DiagnosticIds.NoMatchingCandidate);
        RollbackUnmaterializedContextualArguments(arguments);
        return null;

        static bool HaveSameTypes(ImmutableArray<TypeSymbol> left, ImmutableArray<TypeSymbol> right) =>
            left.Length == right.Length && left.Zip(right).All(pair => TypeIdentity.AreSame(pair.First, pair.Second));
    }

    private ImmutableArray<BoundExpression> ValidateGenericArguments(string name,
        ImmutableArray<TypeSymbol> parameterTypes, ImmutableArray<BoundExpression> arguments,
        ImmutableArray<ExpressionSyntax> argumentSyntax, TextLocation location,
        int? completedArgumentCount = null)
    {
        PreserveDeferredDependentMoves(arguments);
        int suppliedCount = completedArgumentCount ?? arguments.Length;
        bool incomplete = completedArgumentCount.HasValue;
        bool valid = !incomplete && suppliedCount == parameterTypes.Length;
        if ((!incomplete && suppliedCount != parameterTypes.Length) ||
            incomplete && suppliedCount > parameterTypes.Length)
            _diagnostics.Report(location,
                $"'{name}' expects {parameterTypes.Length} argument(s), but {suppliedCount} were provided",
                DiagnosticIds.WrongArity);
        var converted = arguments.ToBuilder();
        for (int index = 0; index < Math.Min(suppliedCount, parameterTypes.Length); index++)
        {
            BoundExpression argument = arguments[index] switch
            {
                BoundUnboundLambdaExpression lambda => BindExpressionWithExpectedType(
                    argumentSyntax[index], parameterTypes[index]),
                BoundUnboundFunctionExpression deferredFunction => BindExpressionWithExpectedType(
                    argumentSyntax[index], parameterTypes[index]),
                _ => arguments[index],
            };
            argument = ContextualizeConversion(argument, parameterTypes[index],
                GetLocation(argumentSyntax[index]));
            converted[index] = argument;
            if (argument is BoundErrorExpression &&
                arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression)
                valid = false;
            SetConvertedType(argumentSyntax[index], argument.Type);
            if (!TypeFacts.CanAssign(parameterTypes[index], argument.Type))
            {
                valid = false;
                ReportCannotConvert(GetLocation(argumentSyntax[index]), argument.Type, parameterTypes[index]);
            }
        }
        CompleteDeferredContextualArguments(arguments, valid);
        return converted.ToImmutable();
    }

    private static GenericParameterSymbol? GetGenericReceiver(TypeSymbol type, bool pointerAccess) => type switch
    {
        GenericParameterSymbol parameter when !pointerAccess => parameter,
        PointerTypeSymbol { ElementType: GenericParameterSymbol parameter } when pointerAccess => parameter,
        UniqueTypeSymbol { ElementType: GenericParameterSymbol parameter } when pointerAccess => parameter,
        SharedTypeSymbol { ElementType: GenericParameterSymbol parameter } when pointerAccess => parameter,
        _ => null,
    };

    private TypeSymbol SubstituteGenericType(TypeSymbol type,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions)
    {
        if (_fileScope.GenericStructSpecializer is not null)
            return _fileScope.GenericStructSpecializer.Substitute(type, substitutions);
        return type switch
        {
            GenericParameterSymbol parameter when substitutions.TryGetValue(parameter, out TypeSymbol? replacement) =>
                replacement,
            PointerTypeSymbol pointer => _fileScope.TypeFactory.PointerTo(
                SubstituteGenericType(pointer.ElementType, substitutions), pointer.IsReadonly),
            ReferenceTypeSymbol reference => _fileScope.TypeFactory.ReferenceTo(
                SubstituteGenericType(reference.ElementType, substitutions), reference.IsReadonly),
            FunctionPointerTypeSymbol function => _fileScope.TypeFactory.FunctionPointer(
                SubstituteGenericType(function.ReturnType, substitutions),
                function.ParameterTypes.Select(parameter => SubstituteGenericType(parameter, substitutions))),
            FunctionValueTypeSymbol function => _fileScope.TypeFactory.FunctionValue(
                SubstituteGenericType(function.ReturnType, substitutions),
                function.ParameterTypes.Select(parameter => SubstituteGenericType(parameter, substitutions))),
            ArrayTypeSymbol array => _fileScope.TypeFactory.ArrayOf(
                SubstituteGenericType(array.ElementType, substitutions), array.Rank),
            AtomicTypeSymbol atomic => _fileScope.TypeFactory.AtomicOf(
                SubstituteGenericType(atomic.ElementType, substitutions)),
            UniqueTypeSymbol unique => _fileScope.TypeFactory.UniqueOf(
                SubstituteGenericType(unique.ElementType, substitutions)),
            SharedTypeSymbol shared => _fileScope.TypeFactory.SharedOf(
                SubstituteGenericType(shared.ElementType, substitutions)),
            WeakTypeSymbol weak => _fileScope.TypeFactory.WeakOf(
                SubstituteGenericType(weak.ElementType, substitutions)),
            StorageTypeSymbol storage => _fileScope.TypeFactory.StorageOf(
                SubstituteGenericType(storage.ElementType, substitutions)),
            PinTypeSymbol pin => _fileScope.TypeFactory.PinOf(
                SubstituteGenericType(pin.ElementType, substitutions)),
            _ => type,
        };
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
        var deferred = new List<(TypeSymbol Pattern, BoundExpression Argument)>();
        for (int index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] is BoundUnboundLambdaExpression or BoundUnboundFunctionExpression)
            {
                deferred.Add((definition.Parameters[index].Type,arguments[index]));
                continue;
            }
            if (!TryInferGenericType(definition.Parameters[index].Type, arguments[index].Type, inferred))
            {
                typeArguments = [];
                return false;
            }
        }

        while (deferred.Count != 0)
        {
            bool resolvedAny = false;
            for (int index = deferred.Count - 1; index >= 0; index--)
            {
                (TypeSymbol pattern,BoundExpression argument) = deferred[index];
                var candidateInference = new Dictionary<GenericParameterSymbol, TypeSymbol>(inferred);
                bool succeeded = argument switch
                {
                    BoundUnboundLambdaExpression lambda =>
                        TryInferLambdaGenericTypes(pattern, lambda.Syntax, candidateInference),
                    BoundUnboundFunctionExpression function =>
                        TryInferNamedFunctionGenericTypes(pattern, function.Syntax, candidateInference),
                    _ => false,
                };
                if (!succeeded) continue;
                inferred = candidateInference;
                deferred.RemoveAt(index);
                resolvedAny = true;
            }
            if (!resolvedAny)
            {
                typeArguments = [];
                return false;
            }
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
        return (pattern,actual) switch
        {
            (PointerTypeSymbol left, PointerTypeSymbol right) when left.IsReadonly == right.IsReadonly =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (FunctionPointerTypeSymbol left, FunctionPointerTypeSymbol right)
                when left.ParameterTypes.Length == right.ParameterTypes.Length =>
                TryInferGenericType(left.ReturnType, right.ReturnType, inferred) &&
                left.ParameterTypes.Zip(right.ParameterTypes).All(pair =>
                    TryInferGenericType(pair.First, pair.Second, inferred)),
            (FunctionValueTypeSymbol left, FunctionValueTypeSymbol right)
                when left.ParameterTypes.Length == right.ParameterTypes.Length =>
                TryInferGenericType(left.ReturnType, right.ReturnType, inferred) &&
                left.ParameterTypes.Zip(right.ParameterTypes).All(pair =>
                    TryInferGenericType(pair.First, pair.Second, inferred)),
            (ReferenceTypeSymbol left, ReferenceTypeSymbol right) when left.IsReadonly == right.IsReadonly =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (ReferenceTypeSymbol left, _) =>
                TryInferGenericType(left.ElementType, actual, inferred),
            (ArrayTypeSymbol left, ArrayTypeSymbol right) when left.Rank == right.Rank =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (AtomicTypeSymbol left, AtomicTypeSymbol right) =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (UniqueTypeSymbol left, UniqueTypeSymbol right) =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (SharedTypeSymbol left, SharedTypeSymbol right) =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (WeakTypeSymbol left, WeakTypeSymbol right) =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (StorageTypeSymbol left, StorageTypeSymbol right) =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (PinTypeSymbol left, PinTypeSymbol right) =>
                TryInferGenericType(left.ElementType, right.ElementType, inferred),
            (StructTypeSymbol { GenericDefinition: not null } left,
                StructTypeSymbol { GenericDefinition: not null } right)
                when ReferenceEquals(left.GenericDefinition, right.GenericDefinition) &&
                     left.TypeArguments.Length == right.TypeArguments.Length =>
                left.TypeArguments.Zip(right.TypeArguments).All(pair =>
                    TryInferGenericType(pair.First, pair.Second, inferred)),
            (InterfaceTypeSymbol { GenericDefinition: not null } left,
                InterfaceTypeSymbol { GenericDefinition: not null } right)
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
        BoundLifetimeValueExpression value => IsAddressable(value.Source),
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
            BoundLifetimeValueExpression value => IsWritable(value.Source),
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

    private bool IsAccessible(Symbol symbol) => AccessibilityRules.IsAccessible(symbol, LexicalAccessContext);

    private static bool ContainsStaticStruct(TypeSymbol type) => type switch
    {
        StructTypeSymbol { IsStatic: true } => true,
        StructTypeSymbol { IsGenericSpecialization: true } structure =>
            structure.TypeArguments.Any(ContainsStaticStruct),
        PointerTypeSymbol pointer => ContainsStaticStruct(pointer.ElementType),
        ReferenceTypeSymbol reference => ContainsStaticStruct(reference.ElementType),
        ArrayTypeSymbol array => ContainsStaticStruct(array.ElementType),
        AtomicTypeSymbol atomic => ContainsStaticStruct(atomic.ElementType),
        OwnershipTypeSymbol ownership => ContainsStaticStruct(ownership.ElementType),
        LifetimeModifierTypeSymbol modifier => ContainsStaticStruct(modifier.ElementType),
        FunctionPointerTypeSymbol function => ContainsStaticStruct(function.ReturnType) ||
            function.ParameterTypes.Any(ContainsStaticStruct),
        FunctionValueTypeSymbol function => ContainsStaticStruct(function.ReturnType) ||
            function.ParameterTypes.Any(ContainsStaticStruct),
        _ => false,
    };

    private bool IsAccessible(Symbol symbol, BoundExpression receiver, bool pointerAccess)
    {
        DeclaredTypeSymbol? receiverType = pointerAccess ? receiver.Type switch
        {
            PointerTypeSymbol { ElementType: DeclaredTypeSymbol type } => type,
            OwnershipTypeSymbol { ElementType: DeclaredTypeSymbol type } => type,
            ReferenceTypeSymbol { ElementType: DeclaredTypeSymbol type } => type,
            _ => receiver is BoundThisExpression @this ? @this.ContainingType : null,
        } : receiver.Type as DeclaredTypeSymbol;
        return AccessibilityRules.IsAccessible(symbol, LexicalAccessContext, receiverType);
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
