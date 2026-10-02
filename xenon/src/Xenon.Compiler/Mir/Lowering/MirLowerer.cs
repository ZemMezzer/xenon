using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Mir.Lowering;

/// <summary>
/// Materializes evaluation order into temporaries and lowers source control flow
/// into basic blocks. Unsupported operations fail explicitly during migration.
/// This pass never calls the legacy backend and stores no bound nodes in MIR.
/// </summary>
public sealed partial class MirLowerer
{
    private sealed class Block(MirBlockId id)
    {
        public MirBlockId Id { get; } = id;
        public List<MirStatement> Statements { get; } = [];
        public MirTerminator? Terminator { get; set; }
    }

    private readonly BoundFunction _bound;
    private readonly bool _hasStackArrays;
    private readonly TypeFactory _types;
    private readonly bool _diagnosticRecovery;
    private readonly IReadOnlySet<BoundExpression>? _invalidExpressions;
    private readonly IReadOnlyDictionary<BoundExpression, TextLocation>? _locations;
    private readonly CancellationToken _cancellation;
    private readonly IReadOnlyDictionary<BoundExpression, int>? _origins;
    private readonly IReadOnlyDictionary<BoundExpression, int> _diagnosticOrigins;
    private readonly List<MirLocal> _locals = [];
    private readonly List<MirScope> _scopes = [new(0, null)];
    private int _scope;
    private readonly List<Block> _blocks = [];
    private readonly Dictionary<VariableSymbol, MirLocalId> _variables = [];
    private readonly Stack<ExitTarget> _breaks = [];
    private readonly Stack<ExitTarget> _continues = [];
    private readonly MirSourceInfo _functionSource;
    private Block _current;
    private readonly Block _unwind;
    private Block _unwindTarget;
    private Block _rethrowTarget;
    private MirPlace? _receiver;
    private MirPlace? _capturedPlace;
    private LocalVariableSymbol? _resumableResultInput;

    private MirLowerer(BoundFunction function, TypeFactory types,
        IReadOnlyDictionary<BoundExpression, TextLocation>? locations, CancellationToken cancellation,
        IReadOnlyDictionary<BoundExpression, int>? origins, bool diagnosticRecovery, IReadOnlySet<BoundExpression>? invalidExpressions, IReadOnlyDictionary<BoundAssignmentExpression, AssignmentState>? ownershipStates, IReadOnlyDictionary<BoundExpression, LifetimeOwner?>? lifetimeOwners)
    {
        _bound = function;
        _hasStackArrays = BoundTree.DescendantsAndSelf(function.Body).Any(node =>
            node is BoundArrayCreationExpression { Storage: ArrayStorageKind.Stack });
        _types = types;
        _diagnosticRecovery = diagnosticRecovery;
        _invalidExpressions = invalidExpressions;
        _ownershipStates = ownershipStates;
        _lifetimeOwners = lifetimeOwners;
        _locations = locations;
        _origins = origins;
        _diagnosticOrigins = origins ?? BoundTree.DescendantsAndSelf(function.Body).OfType<BoundExpression>()
            .Distinct<BoundExpression>(ReferenceEqualityComparer.Instance).Select((expression, id) => (expression, id))
            .ToDictionary(pair => pair.expression, pair => pair.id, (IEqualityComparer<BoundExpression>)ReferenceEqualityComparer.Instance);
        _cancellation = cancellation;
        _functionSource = new(MirDiagnosticSource.Resolve(function.Symbol, TextLocation.None)) { IsFallback = true };
        _current = NewBlock();
        _unwind = NewBlock();
        _unwind.Terminator = function.Body.IsResumable ? new MirAbort(_functionSource) : new MirResumeUnwind(_functionSource);
        _unwindTarget = _rethrowTarget = _unwind;
        foreach (ParameterSymbol parameter in function.Symbol.Parameters) Variable(parameter, MirLocalKind.Parameter);
        foreach (CaptureVariableSymbol capture in function.Symbol.LambdaCaptures) Variable(capture, MirLocalKind.Capture);
    }

    private static MirFunction LowerCore(BoundFunction function, TypeFactory types,
        IReadOnlyDictionary<BoundExpression, TextLocation>? locations, CancellationToken cancellation,
        IReadOnlyDictionary<BoundExpression, int>? origins, bool diagnosticRecovery, IReadOnlySet<BoundExpression>? invalidExpressions,
        IReadOnlyDictionary<BoundAssignmentExpression, AssignmentState>? ownershipStates, IReadOnlyDictionary<BoundExpression, LifetimeOwner?>? lifetimeOwners, out MirLowerer lowering)
    {
        var builder = new MirLowerer(function, types, locations, cancellation, origins, diagnosticRecovery, invalidExpressions, ownershipStates, lifetimeOwners);
        lowering = builder;
        MirFunction? initialization = null;
        if (function.Body.RequiresSuspensionStateMachine)
        {
            var declaration = (BoundVariableDeclarationStatement)function.Body.Statements[0];
            var initializer = new MirLowerer(function, types, locations, cancellation, origins, diagnosticRecovery, invalidExpressions, ownershipStates, lifetimeOwners);
            initialization = initializer.Initialization(declaration);
            lowering._resumableResultInput = declaration.Variable;
        }
        lowering.InitializeLifecycle();
        lowering.Statement(function.Body);
        if (lowering._current.Terminator is null)
            lowering.End(TypeIdentity.AreSame(function.Symbol.ReturnType, BuiltinTypes.Void)
                ? new MirReturn(null, lowering._functionSource) : new MirUnreachable(lowering._functionSource));
        MirFunction result = new(function.Symbol, [.. lowering._locals],
            [.. lowering._blocks.Select(block => new MirBasicBlock(block.Id, [.. block.Statements],
                block.Terminator ?? new MirUnreachable(builder._functionSource)))], new(0), lowering._functionSource) { Scopes = [.. lowering._scopes], UnwindExit = lowering._unwind.Id, ResumableBodyEntry = lowering._resumableBodyEntry, HasDynamicCleanupOrder = lowering._hasDynamicCleanupOrder, HasExceptionRegions = lowering._hasExceptionRegions };
        if (initialization is not null)
            result = result with { ReturnType = BuiltinTypes.Void, Resumable = new(initialization, lowering.Variable(lowering._resumableResultInput!).Local) };
        MirVerifier.VerifyOrThrow(result);
        return result;
    }

    private MirSourceInfo Source(BoundExpression expression)
    {
        TextLocation location = default;
        bool found = _locations is not null && _locations.TryGetValue(expression, out location) ||
            _bound.Symbol.DiagnosticExpressionLocations is { } specializedLocations &&
                specializedLocations.TryGetValue(expression, out location);
        return new(found ? location : _functionSource.Location, Scope: _scope,
            OriginId: _origins is not null && _origins.TryGetValue(expression, out int origin) ? origin : null)
        { IsFallback = !found, DiagnosticOriginId = _diagnosticOrigins.GetValueOrDefault(expression) };
    }
    private Block NewBlock()
    {
        var block = new Block(new(_blocks.Count));
        _blocks.Add(block);
        return block;
    }
    private void End(MirTerminator terminator)
    {
        if (_current.Terminator is not null) throw new InvalidOperationException("Cannot append a second MIR terminator.");
        _current.Terminator = terminator;
    }
    private void Jump(Block block)
    {
        if (_current.Terminator is null) End(new MirGoto(block.Id, _functionSource));
    }
    private MirPlace Temporary(TypeSymbol type, MirSourceInfo source)
    {
        var id = new MirLocalId(_locals.Count);
        _locals.Add(new(id, $"tmp{id.Value}", type, MirLocalKind.Temporary, source));
        return new(id);
    }
    private MirPlace Variable(VariableSymbol variable, MirLocalKind kind = MirLocalKind.Variable)
    {
        if (!_variables.TryGetValue(variable, out MirLocalId id))
        {
            id = new(_locals.Count);
            _locals.Add(new(id, variable.Name, variable.Type, kind, _functionSource with { Scope = _scope }) { Variable = variable });
            _variables.Add(variable, id);
        }
        return new(id);
    }
    private MirOperand Save(MirRValue value, MirSourceInfo source, bool semanticRead = false, bool semanticMove = false, MirPlace? reservedMove = null, MirPlace? transferDestination = null)
    {
        MirPlace place = Temporary(value.Type, source);
        _current.Statements.Add(new MirAssign(place, value, source) { IsSemanticRead = semanticRead, IsMoveRead = semanticMove || value is MirUse { Operand: MirMove }, ReservedMove = reservedMove, TransferDestination = transferDestination });
        return new MirCopy(place, value.Type);
    }
    // Snapshot values before later operand evaluation can mutate their source.
    private MirOperand Snapshot(MirOperand value, MirSourceInfo source) => value is MirConstant or MirFunctionOperand
        ? value : Save(new MirUse(value), source);
    private NotSupportedException Unsupported(BoundNode node) => new(
        $"MIR lowering for {node.Kind} is not implemented in '{_bound.Symbol.FullName}' at {_functionSource.Location.Path}.");

    private void Statement(BoundStatement statement)
    {
        _cancellation.ThrowIfCancellationRequested();
        if (_current.Terminator is not null) return;
        if (_diagnosticRecovery)
        {
            if (statement is BoundExpressionStatement expressionStatement && HasBindingError(expressionStatement.Expression))
            {
                RecoverExpression(expressionStatement.Expression);
                return;
            }
            if (statement is BoundVariableDeclarationStatement declaration)
            {
                if (TypeIdentity.AreSame(declaration.Variable.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(declaration.Variable.Type, BuiltinTypes.Void)) return;
                if (declaration.Initializer is { } initializer && HasBindingError(initializer))
                {
                    RecoverExpression(initializer);
                    statement = declaration with { Initializer = new BoundDefaultValueExpression(declaration.Variable.Type) };
                }
            }
            if (statement is BoundReturnStatement { Expression: null } && !TypeIdentity.AreSame(_bound.Symbol.ReturnType, BuiltinTypes.Void))
            {
                End(new MirUnreachable(_functionSource));
                return;
            }
            if (statement is BoundReturnStatement { Expression: { } result } && HasBindingError(result))
            {
                End(new MirUnreachable(Source(result)));
                return;
            }
        }
        switch (statement)
        {
            case BoundBlockStatement { ExitCleanup: { } cleanup } block:
                Try(new BoundTryStatement(block with { ExitCleanup = null }, [], new BoundBlockStatement([new BoundExpressionStatement(cleanup)])), isSourceRegion: false);
                break;
            case BoundBlockStatement block:
                ScopedBlock(block);
                break;
            case BoundVariableDeclarationStatement declaration:
                MirPlace local = Variable(declaration.Variable);
                _current.Statements.Add(new MirStorageLive(local.Local, _functionSource));
                if (ReferenceEquals(declaration.Variable, _resumableResultInput)) { EnterResumableBody(); break; }
                if (IsStorage(declaration.Variable.Type))
                    Store(new MirAssign(local, new MirDefault(declaration.Variable.Type), _functionSource));
                if (declaration.Initializer is { } voidInitializer && TypeIdentity.AreSame(voidInitializer.Type, BuiltinTypes.Void))
                    _ = Expression(voidInitializer);
                else if (declaration.Initializer is { } valueInitializer)
                {
                    BoundExpression initializer = valueInitializer;
                    MirOperand value = ConvertValue(Value(initializer), declaration.Variable.Type, Source(initializer));
                    Store(new MirAssign(local, new MirUse(value), Source(initializer)) { IsDeclaration = true });
                    TransferArray(local, initializer, Source(initializer));
                }
                if (_bound.Body.IsResumable && ReferenceEquals(declaration, _bound.Body.Statements[0])) EnterResumableBody();
                break;
            case BoundExpressionStatement expression:
                _ = Expression(expression.Expression);
                break;
            case BoundReturnStatement ret when _bound.Body.IsResumable:
                Complete(ret);
                break;
            case BoundReturnStatement ret:
                Return(ret);
                break;
            case BoundIfStatement conditional:
            {
                Block yes = NewBlock(), no = NewBlock(), join = NewBlock();
                Condition(conditional.Condition, yes, no);
                _current = yes;
                Embedded(conditional.ThenStatement);
                Jump(join);
                _current = no;
                if (conditional.ElseStatement is { } other) Embedded(other);
                Jump(join);
                _current = join;
                break;
            }
            case BoundWhileStatement loop:
                Loop(null, loop.Condition, null, loop.Body);
                break;
            case BoundForStatement { Initializer: { } loopInitializer } loop:
                ScopedBlock(new BoundBlockStatement([loopInitializer, loop with { Initializer = null }]));
                break;
            case BoundForStatement loop:
                Loop(null, loop.Condition, loop.Increment, loop.Body);
                break;
            case BoundBreakStatement when _diagnosticRecovery && _breaks.Count == 0: break;
            case BoundContinueStatement when _diagnosticRecovery && _continues.Count == 0: break;
            case BoundBreakStatement:
                Transfer(_breaks.Peek());
                break;
            case BoundContinueStatement:
                Transfer(_continues.Peek());
                break;
            case BoundSwitchStatement selection:
                Switch(selection);
                break;
            case BoundTryStatement region:
                Try(region);
                break;
            case BoundThrowStatement throwing:
                End(new MirThrow(throwing.Expression is null ? null : Value(throwing.Expression), GuardedUnwind(throwing.IsRethrow ? _rethrowTarget : _unwindTarget, _functionSource).Id,
                    throwing.Expression is null ? _functionSource : Source(throwing.Expression)));
                break;
            default: throw Unsupported(statement);
        }
    }

    private void Loop(BoundStatement? initializer, BoundExpression? condition, BoundExpression? increment, BoundStatement body)
    {
        if (initializer is not null) Statement(initializer);
        Block test = NewBlock(), iteration = NewBlock(), step = NewBlock(), exit = NewBlock();
        Jump(test);
        _current = test;
        if (condition is null) Jump(iteration);
        else Condition(condition, iteration, exit);
        _breaks.Push(new(exit.Id, _exits.Count));
        _continues.Push(new(step.Id, _exits.Count));
        _current = iteration;
        Embedded(body);
        Jump(step);
        _current = step;
        if (increment is not null) _ = Expression(increment);
        Jump(test);
        _breaks.Pop();
        _continues.Pop();
        _current = exit;
    }

    private void Switch(BoundSwitchStatement selection)
    {
        MirOperand value = Value(selection.Expression);
        Block exit = NewBlock();
        Block[] sections = selection.Sections.Select(_ => NewBlock()).ToArray();
        MirBlockId otherwise = selection.Sections.Select((section, index) => (section, index))
            .Where(pair => pair.section.Value is null).Select(pair => sections[pair.index].Id).FirstOrDefault(exit.Id);
        if (selection.Sections.All(section => section.Value is null or BoundLiteralExpression))
        {
            var cases = selection.Sections.Select((section, index) => (section, index))
                .Where(pair => pair.section.Value is BoundLiteralExpression)
                .Select(pair => new MirSwitchCase(
                    new(((BoundLiteralExpression)pair.section.Value!).Value, pair.section.Value!.Type), sections[pair.index].Id));
            if (_diagnosticRecovery) cases = cases.DistinctBy(item => item.Value);
            End(new MirSwitch(value, cases.ToImmutableArray(), otherwise, Source(selection.Expression)));
        }
        else
        {
            // Before target binding, case values may still contain layout
            // queries. Keep their typed computations in MIR for analysis.
            for (int index = 0; index < sections.Length; index++)
            {
                if (selection.Sections[index].Value is not { } match) continue;
                Block next = NewBlock();
                MirOperand candidate = Value(match);
                MirOperand equal = Save(new MirBinary(MirBinaryOperator.Equal, value, candidate, BuiltinTypes.Bool), Source(match));
                End(new MirSwitch(equal, [new(new(true, BuiltinTypes.Bool), sections[index].Id)], next.Id, Source(match)));
                _current = next;
            }
            End(new MirGoto(otherwise, Source(selection.Expression)));
        }
        _breaks.Push(new(exit.Id, _exits.Count));
        for (int i = 0; i < sections.Length; i++)
        {
            _current = sections[i];
            Statement(selection.Sections[i].Body);
            // Empty case labels fall through to the next body.
            Jump(selection.Sections[i].Body.Statements.IsEmpty && i + 1 < sections.Length ? sections[i + 1] : exit);
        }
        _breaks.Pop();
        _current = exit;
    }

    private bool HasBindingError(BoundExpression expression) =>
        BoundTree.DescendantsAndSelf(expression).OfType<BoundExpression>()
            .Any(value => value is BoundErrorExpression || TypeIdentity.AreSame(value.Type, BuiltinTypes.Error) || _invalidExpressions?.Contains(value) == true);

    private MirOperand Value(BoundExpression expression) => Expression(expression) ??
        throw new InvalidOperationException($"Void expression used as a value in {_bound.Symbol.FullName}.");

    private MirOperand? ExpressionCore(BoundExpression expression)
    {
        _cancellation.ThrowIfCancellationRequested();
        MirSourceInfo source = Source(expression);
        if (_diagnosticRecovery && InvalidCall(expression))
        {
            RecoverExpression(expression);
            return TypeIdentity.AreSame(expression.Type, BuiltinTypes.Void) ? null : new MirDeferredConstant(expression.Type);
        }
        switch (expression)
        {
            case BoundDeferredConstantExpression deferred: return new MirDeferredConstant(deferred.Type);
            case BoundLiteralExpression literal: return new MirConstant(literal.Value, literal.Type);
            case BoundThisExpression receiver:
                MirPlace receiverValue = Temporary(receiver.Type, source);
                _current.Statements.Add(new MirAssign(receiverValue, new MirUse(new MirCopy(Place(receiver), receiver.Type)), source)
                    { IsSemanticRead = true, IsCompleteReceiverRead = true });
                return new MirCopy(receiverValue, receiver.Type);
            case BoundVariableExpression or BoundMemberAccessExpression or
                BoundReferenceDereferenceExpression or BoundIndexExpression or BoundCapturedPlaceExpression or BoundStaticFieldExpression or BoundLifetimeValueExpression:
                return Save(new MirUse(new MirCopy(Place(expression), expression.Type)), source, semanticRead: true);
            case BoundMoveExpression move:
                MirPlace movedPlace = Place(move.Source);
                MirPlace? ownership = LifetimePlace(move, movedPlace);
                _movedPlaces[move] = movedPlace;
                if (move.Type is ArrayTypeSymbol && ArrayOwnership(move.Source).Active is { } arrayActive)
                    _arrayMoveStates[move] = Snapshot(arrayActive, source);
                if (_deferredArgumentMoves.Contains(move))
                {
                    _pendingMoves.Add(ownership ?? movedPlace);
                    _pendingStorageMoves.Add((movedPlace, move.Type));
                    return Save(new MirUse(new MirCopy(movedPlace, move.Type)), source, semanticRead: true, semanticMove: true, reservedMove: ownership ?? movedPlace, transferDestination: _transferDestinations.GetValueOrDefault(move));
                }
                MirOperand moved = Save(new MirUse(new MirMove(movedPlace, move.Type) { OwnershipPlace = ownership }), source, semanticRead: true, transferDestination: _transferDestinations.GetValueOrDefault(move));
                Activate(ownership ?? movedPlace, false, source);
                EmptyMovedStorage(movedPlace, move.Type, source);
                return moved;
            case BoundCopyExpression copy:
                return TypeFacts.RequiresDestruction(copy.Type)
                    ? Intrinsic(MirIntrinsicKind.CloneValue, [Value(copy.Source)], copy.Type, source)
                    : Save(new MirUse(Value(copy.Source)), source);
            case BoundDefaultValueExpression value: return Save(new MirDefault(value.Type), source);
            case BoundFullExpression full: return FullExpression(full);
            case BoundCastExpression cast when cast.Expression.Type is AtomicTypeSymbol atomic:
                return Intrinsic(MirIntrinsicKind.AtomicLoad, [Address(Place(cast.Expression), atomic, source)], cast.Type, source);
            case BoundCastExpression cast when cast.Type is AtomicTypeSymbol atomic:
                return Save(new MirAtomicValue(Value(cast.Expression), atomic), source);
            case BoundCastExpression cast: return Save(new MirCast(Value(cast.Expression), cast.Type), source);
            case BoundReferenceConversionExpression reference:
                if (reference.Source.Type is ReferenceTypeSymbol existingReference &&
                    TypeIdentity.AreSame(existingReference.ElementType, reference.ReferenceType.ElementType))
                    return ConvertValue(Value(reference.Source), reference.ReferenceType, source);
                if (reference.Source.Type is StructTypeSymbol referenceStructure &&
                    reference.ReferenceType.ElementType is InterfaceTypeSymbol referenceInterface)
                {
                    MirOperand view = Save(new MirInterfaceView(Address(Place(reference.Source), reference.Source.Type, source),
                        referenceStructure, referenceInterface), source);
                    return Save(new MirBorrow(((MirCopy)view).Place,
                        reference.ReferenceType.IsReadonly ? MirBorrowKind.Shared : MirBorrowKind.Exclusive, reference.ReferenceType), source);
                }
                return ConvertValue(Save(new MirBorrow(Place(reference.Source),
                    reference.ReferenceType.IsReadonly ? MirBorrowKind.Shared : MirBorrowKind.Exclusive,
                    _types.ReferenceTo(reference.Source.Type, reference.ReferenceType.IsReadonly)), source, semanticRead: true), reference.Type, source);
            case BoundBinaryExpression binary when binary.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken:
                return ShortCircuit(binary);
            case BoundBinaryExpression binary:
                MirOperand left = Snapshot(Value(binary.Left), source);
                MirOperand right = Value(binary.Right);
                return Save(new MirBinary(BinaryOperator(binary.OperatorKind), left, right, binary.Type), source);
            case BoundUnaryExpression unary: return Unary(unary);
            case BoundAssignmentExpression assignment: return Assignment(assignment);
            case BoundDeferredGenericOperationExpression call when ReferenceEquals(call, _completionExpression):
                _completionArguments = Arguments(call.Arguments);
                foreach (MirOperand argument in _completionArguments)
                    if (GuardValue(argument, source) is { } guard) _completionGuards.Add(guard);
                return null;
            case BoundDeferredGenericOperationExpression deferred:
                return GenericOperation(deferred);
            case BoundDeferredGenericMethodCallExpression deferred:
            {
                MirOperand receiver = deferred.IsPointerAccess
                    ? PointerReceiver(deferred.Receiver, source)
                    : Address(Place(deferred.Receiver), deferred.Receiver.Type, source);
                ImmutableArray<MirOperand> arguments = Arguments(deferred.Arguments);
                var callee = new MirRequirementOperand(deferred.Requirement, MirGenericOperation.MethodCall,
                    _types.FunctionPointer(deferred.Type, arguments.Select(argument => argument.Type)), [],
                    PointerAccess: deferred.IsPointerAccess);
                return CallValues(callee, arguments, source, receiver);
            }
            case BoundCallExpression call when ReferenceEquals(call, _completionExpression):
                _completionArguments = Arguments(call.Arguments);
                foreach (MirOperand argument in _completionArguments)
                    if (GuardValue(argument, source) is { } guard) _completionGuards.Add(guard);
                return null;
            case BoundCallExpression call:
                return Call(new MirFunctionOperand(call.Function, _types.FunctionPointer(call.Type, call.Function.Parameters.Select(p => p.Type))), call.Arguments, source);
            case BoundIndirectCallExpression call: return Call(Snapshot(Value(call.Target), source), call.Arguments, source);
            case BoundFunctionValueCallExpression call:
            {
                MirOperand callable = Intrinsic(MirIntrinsicKind.CloneValue, [Snapshot(Value(call.Target), source)], call.Target.Type, source)!;
                TemporaryGuard? callGuard = GuardValue(callable, source);
                MirOperand? result = Call(callable, call.Arguments, source);
                if (callGuard is not null)
                {
                    TemporaryGuard? resultGuard = result is null ? null : GuardValue(result, source);
                    Block afterCall = NewBlock();
                    Jump(CleanupChain([callGuard], afterCall, GuardedUnwind(_unwindTarget, source), false, source));
                    _current = afterCall;
                    EndValueGuard(callGuard, source);
                    EndValueGuard(resultGuard, source);
                }
                return result;
            }
            case BoundMethodCallExpression method:
                return MethodCall(method.Method, method.Receiver, method.Arguments, method.IsPointerAccess, null, source);
            case BoundInterfaceMethodCallExpression method:
                return MethodCall(method.Method, method.Receiver, method.Arguments, method.IsPointerAccess, method.InterfaceType, source);
            case BoundBaseLifecycleCallExpression lifecycle:
                MirOperand? lifecycleResult = Call(Callee(lifecycle.Function),
                    lifecycle.Arguments, source, ThisAddress(_bound.Symbol.ContainingType!));
                LifecycleCompleted(lifecycle.Function);
                return lifecycleResult;
            case BoundConstructorCallExpression constructor:
                return Construct(constructor, source);
            case BoundArrayCreationExpression array:
                return CreateArray(array, source);
            case BoundArrayMetadataExpression metadata:
            {
                MirOperand array = metadata.Receiver.Type is OwnershipTypeSymbol ownerType
                    ? new MirCopy(PointerStorage(metadata.Receiver), ownerType.StorageType)
                    : Value(metadata.Receiver);
                return Intrinsic(metadata.Member switch
                {
                    "length" or "Length" => MirIntrinsicKind.ArrayLength,
                    "rank" or "Rank" => MirIntrinsicKind.ArrayRank,
                    "GetLength" => MirIntrinsicKind.ArrayDimension,
                    _ => throw Unsupported(metadata),
                }, metadata.Dimension is null ? [array] : [Snapshot(array, source), Value(metadata.Dimension)], metadata.Type, source);
            }
            case BoundStorageMoveExpression storage:
                return MoveStorage(Place(storage.Storage), storage.StorageType, source, semanticRead: true);
            case BoundStorageConstructExpression storage:
                ConstructStorage(storage, source);
                return null;
            case BoundAwaitExpression awaiting: return Await(awaiting);
            case BoundFunctionAddressExpression address: return new MirFunctionOperand(address.Function, address.Type);
            case BoundStructConstructionExpression construction:
                return ConstructAggregate(construction, source);
            default: return ExtendedExpression(expression);
        }
    }

    private MirOperand? Call(MirOperand callee, ImmutableArray<BoundExpression> arguments, MirSourceInfo source,
        MirOperand? receiver = null, InterfaceTypeSymbol? interfaceType = null, bool isVirtual = false, bool indirectReceiver = false)
    {
        ImmutableArray<MirOperand> values = Arguments(arguments);
        return CallValues(callee, values, source, receiver, interfaceType, isVirtual, indirectReceiver);
    }

    private MirOperand? CallValues(MirOperand callee, ImmutableArray<MirOperand> values, MirSourceInfo source,
        MirOperand? receiver = null, InterfaceTypeSymbol? interfaceType = null, bool isVirtual = false, bool indirectReceiver = false)
    {
        TypeSymbol returnType = callee.Type switch
        {
            FunctionPointerTypeSymbol signature => signature.ReturnType,
            FunctionValueTypeSymbol signature => signature.ReturnType,
            _ => throw new InvalidOperationException("Invalid MIR callee type."),
        };
        ImmutableArray<TypeSymbol> parameterTypes = callee.Type switch
        {
            FunctionPointerTypeSymbol signature => signature.ParameterTypes,
            FunctionValueTypeSymbol signature => signature.ParameterTypes,
            _ => throw new InvalidOperationException("Invalid callee."),
        };
        values = [.. values.Select((value, index) => ConvertValue(value, parameterTypes[index], source))];
        MirPlace? result = TypeIdentity.AreSame(returnType, BuiltinTypes.Void) ? null : Temporary(returnType, source);
        Block next = NewBlock();
        End(new MirCall(callee, values, result, next.Id, GuardedUnwind(_unwindTarget, source).Id, source)
        { Receiver = receiver, InterfaceType = interfaceType, IsVirtual = isVirtual, IsIndirectReceiver = indirectReceiver });
        _current = next;
        return result is null ? null : new MirCopy(result, returnType);
    }

    private MirPlace Place(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundVariableExpression variable: return Variable(variable.Variable);
            case BoundThisExpression receiver:
                return _receiver ??= Receiver(receiver.Type);
            case BoundCapturedPlaceExpression: return _capturedPlace ?? throw new InvalidOperationException("No captured MIR place.");
            case BoundMemberAccessExpression field:
                MirPlace parent = field.IsPointerAccess ? PointerStorage(field.Receiver).Project(new MirDerefProjection()) : Place(field.Receiver);
                return parent.Project(new MirFieldProjection(field.Field));
            case BoundStaticFieldExpression field:
                if (field.Field.IsThreadLocal && !ReferenceEquals(_bound.Symbol.ThreadLocalField, field.Field))
                    _ = Intrinsic(MirIntrinsicKind.EnsureThreadLocal, [], BuiltinTypes.Void, Source(field), field: field.Field);
                return ((MirCopy)Save(new MirStaticFieldAddress(field.Field, _types.PointerTo(field.Type)), Source(field))).Place.Project(new MirDerefProjection());
            case BoundLifetimeValueExpression lifetime:
                MirPlace lifetimeStorage = Place(lifetime.Source);
                if (lifetime.ModifierType is StorageTypeSymbol storageType)
                    _ = Intrinsic(MirIntrinsicKind.CheckStorageInitialized, [Address(lifetimeStorage, storageType, Source(lifetime))], BuiltinTypes.Void, Source(lifetime), storageCheck: MirStorageCheckPurpose.Read);
                return lifetimeStorage.Project(new MirLifetimeProjection());
            case BoundReferenceDereferenceExpression reference:
                return Materialize(reference.Reference).Project(new MirDerefProjection());
            case BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } dereference:
                return dereference.Operand.Type is OwnershipTypeSymbol { StorageType: ArrayTypeSymbol } ? PointerStorage(dereference.Operand) : PointerStorage(dereference.Operand).Project(new MirDerefProjection());
            case BoundIndexExpression index:
                MirPlace indexed = PointerStorage(index.Receiver);
                return indexed.Project(new MirIndexProjection([.. index.Indices.Select(dimension => Snapshot(Value(dimension), Source(dimension)))]));
            default: return Materialize(expression);
        }
    }

    private MirPlace Receiver(TypeSymbol type)
    {
        var id = new MirLocalId(_locals.Count);
        _locals.Add(new(id, "this", type, MirLocalKind.Receiver, _functionSource));
        return new(id);
    }

    private MirPlace PointerStorage(BoundExpression expression)
    {
        MirPlace storage = expression is BoundThisExpression receiver ? _receiver ??= Receiver(receiver.Type) : Materialize(expression);
        return expression.Type is OwnershipTypeSymbol ? storage.Project(new MirOwnerStorageProjection()) : storage;
    }

    private MirPlace Materialize(BoundExpression expression)
    {
        MirOperand operand = Value(expression);
        if (operand is MirCopy copy && _locals[copy.Place.Local.Value].Kind == MirLocalKind.Temporary) return copy.Place;
        return ((MirCopy)Save(new MirUse(operand), Source(expression))).Place;
    }

    private MirOperand ShortCircuit(BoundBinaryExpression expression)
    {
        MirSourceInfo source = Source(expression);
        MirOperand left = Value(expression.Left);
        MirPlace result = Temporary(BuiltinTypes.Bool, source);
        Block evaluate = NewBlock(), constant = NewBlock(), join = NewBlock();
        bool shortValue = expression.OperatorKind == SyntaxKind.PipePipeToken;
        End(new MirSwitch(left, [new(new(shortValue, BuiltinTypes.Bool), constant.Id)], evaluate.Id, source));
        _current = constant;
        _current.Statements.Add(new MirAssign(result, new MirUse(new MirConstant(shortValue, BuiltinTypes.Bool)), source));
        Jump(join);
        _current = evaluate;
        MirOperand right = Value(expression.Right);
        _current.Statements.Add(new MirAssign(result, new MirUse(right), source));
        Jump(join);
        _current = join;
        return new MirCopy(result, BuiltinTypes.Bool);
    }

    private MirOperand Unary(BoundUnaryExpression expression)
    {
        MirSourceInfo source = Source(expression);
        switch (expression.OperatorKind)
        {
            case SyntaxKind.AmpersandToken:
                (MirPlace addressed, TypeSymbol addressedType) = Unpin(Place(expression.Operand), expression.Operand.Type);
                if (addressedType is StorageTypeSymbol && expression.Type is PointerTypeSymbol pointer &&
                    !TypeIdentity.AreSame(pointer.ElementType, addressedType))
                    addressed = addressed.Project(new MirLifetimeProjection());
                return Save(new MirBorrow(addressed, MirBorrowKind.Raw, expression.Type), source);
            case SyntaxKind.StarToken:
                return Save(new MirUse(new MirCopy(Place(expression), expression.Type)), source, semanticRead: true);
            case SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken:
                MirPlace target = Place(expression.Operand);
                if (expression.Operand.Type is AtomicTypeSymbol atomic)
                    return Intrinsic(MirIntrinsicKind.AtomicUpdate,
                        [Address(target, atomic, source), new MirConstant(1, atomic.ElementType)], atomic.ElementType, source,
                        op: expression.OperatorKind == SyntaxKind.PlusPlusToken ? MirBinaryOperator.Add : MirBinaryOperator.Subtract,
                        returnsOldValue: expression.IsPostfix)!;
                MirOperand previous = Save(new MirUse(new MirCopy(target, expression.Type)), source, semanticRead: true);
                MirOperand next = Save(new MirBinary(expression.OperatorKind == SyntaxKind.PlusPlusToken ? MirBinaryOperator.Add : MirBinaryOperator.Subtract,
                    previous, new MirConstant(1, expression.Type is PointerTypeSymbol ? BuiltinTypes.Int : expression.Type), expression.Type), source);
                _current.Statements.Add(new MirAssign(target, new MirUse(next), source) { IsSemanticWrite = true });
                return expression.IsPostfix ? previous : next;
            case SyntaxKind.PlusToken: return Value(expression.Operand);
            default:
                MirUnaryOperator op = expression.OperatorKind switch
                {
                    SyntaxKind.MinusToken => MirUnaryOperator.Negate,
                    SyntaxKind.BangToken => MirUnaryOperator.Not,
                    SyntaxKind.TildeToken => MirUnaryOperator.BitwiseNot,
                    _ => throw Unsupported(expression),
                };
                return Save(new MirUnary(op, Value(expression.Operand), expression.Type), source);
        }
    }

    private readonly Dictionary<BoundExpression, MirPlace> _transferDestinations = new(ReferenceEqualityComparer.Instance);

    private void CheckArgumentReservation(MirPlace target, TypeSymbol type, MirSourceInfo source)
    {
        if (_pendingMoves.Count == 0) return;
        var pointer = _types.PointerTo(type);
        _current.Statements.Add(new MirAssign(Temporary(pointer, source), new MirBorrow(target, MirBorrowKind.Raw, pointer), source)
            { IsArgumentReservationCheck = true });
    }

    private MirPlace? DirectScalarPlace(BoundExpression expression) => expression switch
    {
        BoundVariableExpression variable => Variable(variable.Variable),
        BoundMemberAccessExpression { IsPointerAccess: false } member when DirectScalarPlace(member.Receiver) is { } parent =>
            parent.Project(new MirFieldProjection(member.Field)),
        BoundLifetimeValueExpression lifetime when DirectScalarPlace(lifetime.Source) is { } owner =>
            owner.Project(new MirLifetimeProjection()),
        _ => null,
    };

    private MirOperand Assignment(BoundAssignmentExpression expression)
    {
        MirSourceInfo source = Source(expression);
        MirPlace target = Place(expression.Target);
        CheckArgumentReservation(target, expression.Target.Type, source);
        if (expression.OperatorKind == SyntaxKind.EqualsToken && expression.Expression is BoundCopyExpression selfCopy &&
            DirectScalarPlace(selfCopy.Source) is { } copied && copied.Equals(target))
            return Save(new MirUse(new MirCopy(target, expression.Type)), source, semanticRead: true);
        if (target.Projections.LastOrDefault() is MirFieldProjection field && target.Projections.All(projection => projection is MirFieldProjection))
        {
            MirPlace parent = new(target.Local, target.Projections.RemoveAt(target.Projections.Length - 1));
            TypeSymbol parentType = parent.Projections.LastOrDefault() is MirFieldProjection parentField
                ? parentField.Field.Type : _locals[parent.Local.Value].Type;
            MirPlace check = Temporary(_types.PointerTo(parentType), source);
            _current.Statements.Add(new MirAssign(check,
                new MirBorrow(parent, MirBorrowKind.Raw, _types.PointerTo(parentType)), Source(expression.Target))
                { IsSemanticRead = true, IsProjectionBaseRead = true });
        }
        if (expression.Target.Type is not AtomicTypeSymbol && TypeFacts.GetCompleteDestructor(expression.Type) is not null &&
            !(_locals[target.Local.Value].Kind is MirLocalKind.Variable or MirLocalKind.Parameter &&
              target.Projections.All(projection => projection is MirFieldProjection or MirLifetimeProjection)))
        {
            MirPlace check = Temporary(_types.PointerTo(expression.Type), source);
            _current.Statements.Add(new MirAssign(check,
                new MirBorrow(target, MirBorrowKind.Raw, _types.PointerTo(expression.Type)), Source(expression.Target))
                { IsUntrackedReplacementCheck = true });
        }
        MirPlace? previousCapture = _capturedPlace;
        if (expression.CapturesTarget) _capturedPlace = target;
        BoundExpression movedExpression = Unwrap(expression.Expression);
        if (expression.OperatorKind == SyntaxKind.EqualsToken && movedExpression is BoundMoveExpression)
            _transferDestinations[movedExpression] = target;
        try
        {
            if (expression.Target.Type is AtomicTypeSymbol atomic)
                return AtomicAssignment(expression, target, atomic, source);
            MirOperand? previous = expression.OperatorKind == SyntaxKind.EqualsToken ? null :
                Save(new MirUse(new MirCopy(target, expression.Type)), source);
            MirOperand value = Value(expression.Expression);
            if (previous is not null)
                value = Save(new MirBinary(BinaryOperator(expression.OperatorKind), previous, value, expression.Type), source);
            value = Snapshot(ConvertValue(value, expression.Type, source), source);
            TemporaryGuard? incomingArray = null;
            if (Unwrap(expression.Expression) is BoundArrayCreationExpression { Storage: ArrayStorageKind.Heap } allocation &&
                OwnedUnits(target).Any(guard => guard.Count is not null))
            {
                incomingArray = new(((MirCopy)value).Place, Temporary(BuiltinTypes.Bool, source),
                    TypeFacts.GetCompleteDestructor(allocation.ElementType))
                { Count = Temporary(BuiltinTypes.Int, source), FreeAllocation = true, Allocation = value };
                MirOperand length = Intrinsic(MirIntrinsicKind.ArrayLength, [value], BuiltinTypes.Int, source)!;
                _current.Statements.Add(new MirAssign(incomingArray.Count, new MirUse(length), source));
                SetFlag(incomingArray.Active, true, source);
            }
            if (TypeFacts.RequiresDestruction(expression.Type) ||
                _bound.Symbol.FunctionKind is FunctionKind.Constructor or FunctionKind.InstanceInitializer)
                RecordStore(expression, target);
            AssignmentState ownership = Ownership(expression);
            Store(new MirAssign(target, new MirUse(value), source)
            {
                IsSemanticWrite = true,
                WriteKind = expression.IsRawPlacement ? MirWriteKind.RawPlacement : ownership.Initialize ? MirWriteKind.Initialize : MirWriteKind.Replace,
                PreviousValueState = ownership.Previous,
                ConstructorField = expression.Target is BoundMemberAccessExpression member && IsThisRooted(member)
                    ? member.Field : null,
                RequiresRuntimeInitializationCheck = ownership.RuntimeCheck,
            }, _bound.Symbol.FunctionKind is FunctionKind.Method or FunctionKind.Destructor && IsThisRooted(expression.Target), incomingArray);
            EndValueGuard(incomingArray, source);
            TransferArray(target, expression.Expression, source);
            return value;
        }
        finally
        {
            _capturedPlace = previousCapture;
            _transferDestinations.Remove(movedExpression);
        }
    }

    private static MirBinaryOperator BinaryOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken or SyntaxKind.PlusEqualsToken => MirBinaryOperator.Add,
        SyntaxKind.MinusToken or SyntaxKind.MinusEqualsToken => MirBinaryOperator.Subtract,
        SyntaxKind.StarToken or SyntaxKind.StarEqualsToken => MirBinaryOperator.Multiply,
        SyntaxKind.SlashToken or SyntaxKind.SlashEqualsToken => MirBinaryOperator.Divide,
        SyntaxKind.PercentToken or SyntaxKind.PercentEqualsToken => MirBinaryOperator.Remainder,
        SyntaxKind.AmpersandToken or SyntaxKind.AmpersandEqualsToken => MirBinaryOperator.BitwiseAnd,
        SyntaxKind.PipeToken or SyntaxKind.PipeEqualsToken => MirBinaryOperator.BitwiseOr,
        SyntaxKind.CaretToken or SyntaxKind.CaretEqualsToken => MirBinaryOperator.BitwiseXor,
        SyntaxKind.LessLessToken or SyntaxKind.LessLessEqualsToken => MirBinaryOperator.ShiftLeft,
        SyntaxKind.GreaterGreaterToken or SyntaxKind.GreaterGreaterEqualsToken => MirBinaryOperator.ShiftRight,
        SyntaxKind.EqualsEqualsToken => MirBinaryOperator.Equal,
        SyntaxKind.BangEqualsToken => MirBinaryOperator.NotEqual,
        SyntaxKind.LessToken => MirBinaryOperator.Less,
        SyntaxKind.LessOrEqualsToken => MirBinaryOperator.LessOrEqual,
        SyntaxKind.GreaterToken => MirBinaryOperator.Greater,
        SyntaxKind.GreaterOrEqualsToken => MirBinaryOperator.GreaterOrEqual,
        _ => throw new NotSupportedException($"No MIR binary operator for {kind}."),
    };
}
