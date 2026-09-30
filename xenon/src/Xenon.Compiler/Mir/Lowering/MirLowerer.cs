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
    private readonly TypeFactory _types;
    private readonly IReadOnlyDictionary<BoundExpression, TextLocation>? _locations;
    private readonly CancellationToken _cancellation;
    private readonly List<MirLocal> _locals = [];
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

    private MirLowerer(BoundFunction function, TypeFactory types,
        IReadOnlyDictionary<BoundExpression, TextLocation>? locations, CancellationToken cancellation)
    {
        _bound = function;
        _types = types;
        _locations = locations;
        _cancellation = cancellation;
        _functionSource = new(function.Symbol.Locations.FirstOrDefault(TextLocation.None));
        _current = NewBlock();
        _unwind = NewBlock();
        _unwind.Terminator = new MirResumeUnwind(_functionSource);
        _unwindTarget = _rethrowTarget = _unwind;
        foreach (ParameterSymbol parameter in function.Symbol.Parameters) Variable(parameter, MirLocalKind.Parameter);
        foreach (CaptureVariableSymbol capture in function.Symbol.LambdaCaptures) Variable(capture, MirLocalKind.Capture);
    }

    public static MirFunction Lower(BoundFunction function, TypeFactory types,
        IReadOnlyDictionary<BoundExpression, TextLocation>? locations = null, CancellationToken cancellation = default)
    {
        var lowering = new MirLowerer(function, types, locations, cancellation);
        lowering.Statement(function.Body);
        if (lowering._current.Terminator is null)
            lowering.End(TypeIdentity.AreSame(function.Symbol.ReturnType, BuiltinTypes.Void)
                ? new MirReturn(null, lowering._functionSource) : new MirUnreachable(lowering._functionSource));
        MirFunction result = new(function.Symbol, [.. lowering._locals],
            [.. lowering._blocks.Select(block => new MirBasicBlock(block.Id, [.. block.Statements],
                block.Terminator ?? new MirUnreachable(lowering._functionSource)))], new(0), lowering._functionSource);
        MirVerifier.VerifyOrThrow(result);
        return result;
    }

    private MirSourceInfo Source(BoundExpression expression) =>
        _locations is not null && _locations.TryGetValue(expression, out TextLocation location)
            ? new(location) : _functionSource;
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
            _locals.Add(new(id, variable.Name, variable.Type, kind, _functionSource) { Variable = variable });
            _variables.Add(variable, id);
        }
        return new(id);
    }
    private MirOperand Save(MirRValue value, MirSourceInfo source)
    {
        MirPlace place = Temporary(value.Type, source);
        _current.Statements.Add(new MirAssign(place, value, source));
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
        switch (statement)
        {
            case BoundBlockStatement { ExitCleanup: { } cleanup } block:
                Try(new BoundTryStatement(block with { ExitCleanup = null }, [], new BoundBlockStatement([new BoundExpressionStatement(cleanup)])));
                break;
            case BoundBlockStatement block:
                foreach (BoundStatement child in block.Statements) Statement(child);
                break;
            case BoundVariableDeclarationStatement declaration:
                MirPlace local = Variable(declaration.Variable);
                _current.Statements.Add(new MirStorageLive(local.Local, _functionSource));
                if (declaration.Initializer is { } initializer)
                {
                    MirOperand value = ConvertValue(Value(initializer), declaration.Variable.Type, Source(initializer));
                    _current.Statements.Add(new MirAssign(local, new MirUse(value), Source(initializer)));
                }
                break;
            case BoundExpressionStatement expression:
                _ = Expression(expression.Expression);
                break;
            case BoundReturnStatement ret when _bound.Body.IsResumable:
                Complete(ret);
                break;
            case BoundReturnStatement ret:
                MirOperand? returned = ret.Expression is null ? null : Snapshot(ConvertValue(Value(ret.Expression), _bound.Symbol.ReturnType, Source(ret.Expression)), Source(ret.Expression));
                ExitActions(0);
                if (_current.Terminator is null)
                    End(new MirReturn(returned, ret.Expression is null ? _functionSource : Source(ret.Expression)));
                break;
            case BoundIfStatement conditional:
            {
                MirOperand condition = Value(conditional.Condition);
                Block yes = NewBlock(), no = NewBlock(), join = NewBlock();
                End(new MirSwitch(condition, [new(new(true, BuiltinTypes.Bool), yes.Id)], no.Id, Source(conditional.Condition)));
                _current = yes;
                Statement(conditional.ThenStatement);
                Jump(join);
                _current = no;
                if (conditional.ElseStatement is { } other) Statement(other);
                Jump(join);
                _current = join;
                break;
            }
            case BoundWhileStatement loop:
                Loop(null, loop.Condition, null, loop.Body);
                break;
            case BoundForStatement loop:
                Loop(loop.Initializer, loop.Condition, loop.Increment, loop.Body);
                break;
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
                End(new MirThrow(throwing.Expression is null ? null : Value(throwing.Expression), (throwing.IsRethrow ? _rethrowTarget : _unwindTarget).Id,
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
        else End(new MirSwitch(Value(condition), [new(new(true, BuiltinTypes.Bool), iteration.Id)], exit.Id, Source(condition)));
        _breaks.Push(new(exit.Id, _exits.Count));
        _continues.Push(new(step.Id, _exits.Count));
        _current = iteration;
        Statement(body);
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
        var cases = ImmutableArray.CreateBuilder<MirSwitchCase>();
        MirBlockId otherwise = exit.Id;
        for (int i = 0; i < sections.Length; i++)
            if (selection.Sections[i].Value is { } match)
            {
                if (match is not BoundLiteralExpression literal) throw Unsupported(match);
                cases.Add(new(new(literal.Value, literal.Type), sections[i].Id));
            }
            else otherwise = sections[i].Id;
        End(new MirSwitch(value, cases.ToImmutable(), otherwise, Source(selection.Expression)));
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

    private MirOperand Value(BoundExpression expression) => Expression(expression) ??
        throw new InvalidOperationException($"Void expression used as a value in {_bound.Symbol.FullName}.");

    private MirOperand? ExpressionCore(BoundExpression expression)
    {
        _cancellation.ThrowIfCancellationRequested();
        MirSourceInfo source = Source(expression);
        switch (expression)
        {
            case BoundLiteralExpression literal: return new MirConstant(literal.Value, literal.Type);
            case BoundVariableExpression or BoundThisExpression or BoundMemberAccessExpression or
                BoundReferenceDereferenceExpression or BoundIndexExpression or BoundCapturedPlaceExpression or BoundStaticFieldExpression or BoundLifetimeValueExpression:
                return Save(new MirUse(new MirCopy(Place(expression), expression.Type)), source);
            case BoundMoveExpression move:
                MirPlace? ownership = move.TrackedVariable is null ? null : Variable(move.TrackedVariable);
                foreach (FieldSymbol field in move.TrackedPath)
                    ownership = ownership?.Project(new MirFieldProjection(field));
                MirPlace movedPlace = Place(move.Source);
                if (_deferredArgumentMoves.Contains(move))
                {
                    _pendingMoves.Add(ownership ?? movedPlace);
                    return Save(new MirUse(new MirCopy(movedPlace, move.Type)), source);
                }
                return Save(new MirUse(new MirMove(movedPlace, move.Type) { OwnershipPlace = ownership }), source);
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
                                return ConvertValue(Save(new MirBorrow(Place(reference.Source),
                    reference.ReferenceType.IsReadonly ? MirBorrowKind.Shared : MirBorrowKind.Exclusive,
                    _types.ReferenceTo(reference.Source.Type, reference.ReferenceType.IsReadonly)), source), reference.Type, source);
            case BoundBinaryExpression binary when binary.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken:
                return ShortCircuit(binary);
            case BoundBinaryExpression binary:
                MirOperand left = Snapshot(Value(binary.Left), source);
                MirOperand right = Value(binary.Right);
                return Save(new MirBinary(BinaryOperator(binary.OperatorKind), left, right, binary.Type), source);
            case BoundUnaryExpression unary: return Unary(unary);
            case BoundAssignmentExpression assignment: return Assignment(assignment);
            case BoundCallExpression call when ReferenceEquals(call, _completionExpression):
                _completionArguments = Arguments(call.Arguments);
                return null;
            case BoundCallExpression call:
                return Call(new MirFunctionOperand(call.Function, _types.FunctionPointer(call.Type, call.Function.Parameters.Select(p => p.Type))), call.Arguments, source);
            case BoundIndirectCallExpression call: return Call(Snapshot(Value(call.Target), source), call.Arguments, source);
            case BoundFunctionValueCallExpression call: return Call(Snapshot(Value(call.Target), source), call.Arguments, source);
            case BoundMethodCallExpression method:
                return MethodCall(method.Method, method.Receiver, method.Arguments, method.IsPointerAccess, null, source);
            case BoundInterfaceMethodCallExpression method:
                return MethodCall(method.Method, method.Receiver, method.Arguments, method.IsPointerAccess, method.InterfaceType, source);
            case BoundBaseLifecycleCallExpression lifecycle:
                return Call(new MirFunctionOperand(lifecycle.Function, _types.FunctionPointer(lifecycle.Function.ReturnType, lifecycle.Function.Parameters.Select(p => p.Type))),
                    lifecycle.Arguments, source, new MirCopy(_receiver ??= Receiver(_types.PointerTo(_bound.Symbol.ContainingType!)), _types.PointerTo(_bound.Symbol.ContainingType!)));
            case BoundConstructorCallExpression constructor:
                return Construct(constructor, source);
            case BoundArrayCreationExpression array:
                return Intrinsic(array.Storage == ArrayStorageKind.Stack ? MirIntrinsicKind.CreateStackArray : MirIntrinsicKind.CreateHeapArray,
                    [.. array.Dimensions.Select(d => Snapshot(Value(d), Source(d)))], array.Type, source);
            case BoundArrayMetadataExpression metadata:
                return Intrinsic(metadata.Member switch
                {
                    "length" or "Length" => MirIntrinsicKind.ArrayLength,
                    "rank" or "Rank" => MirIntrinsicKind.ArrayRank,
                    "GetLength" => MirIntrinsicKind.ArrayDimension,
                    _ => throw Unsupported(metadata),
                }, metadata.Dimension is null ? [Value(metadata.Receiver)] : [Snapshot(Value(metadata.Receiver), source), Value(metadata.Dimension)], metadata.Type, source);
            case BoundStorageMoveExpression storage:
                return Intrinsic(MirIntrinsicKind.MoveStorage, [Address(Place(storage.Storage), storage.Storage.Type, source)], storage.Type, source);
            case BoundStorageConstructExpression storage when storage.Constructor is null:
                return Intrinsic(MirIntrinsicKind.ConstructStorage,
                    [Address(Place(storage.Storage), storage.Storage.Type, source), storage.Value is null ? Save(new MirDefault(storage.ValueType), source) : Value(storage.Value)],
                    BuiltinTypes.Void, source);
            case BoundAwaitExpression awaiting: return Await(awaiting);
            case BoundFunctionAddressExpression address: return new MirFunctionOperand(address.Function, address.Type);
            case BoundStructConstructionExpression construction:
                return ConstructAggregate(construction, source);
            default: return ExtendedExpression(expression);
        }
    }

    private MirOperand? Call(MirOperand callee, ImmutableArray<BoundExpression> arguments, MirSourceInfo source,
        MirOperand? receiver = null, InterfaceTypeSymbol? interfaceType = null, bool isVirtual = false)
    {
        ImmutableArray<MirOperand> values = Arguments(arguments);
        return CallValues(callee, values, source, receiver, interfaceType, isVirtual);
    }

    private MirOperand? CallValues(MirOperand callee, ImmutableArray<MirOperand> values, MirSourceInfo source,
        MirOperand? receiver = null, InterfaceTypeSymbol? interfaceType = null, bool isVirtual = false)
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
        End(new MirCall(callee, values, result, next.Id, _unwindTarget.Id, source)
        { Receiver = receiver, InterfaceType = interfaceType, IsVirtual = isVirtual });
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
                return ((MirCopy)Save(new MirStaticFieldAddress(field.Field, _types.PointerTo(field.Type)), Source(field))).Place.Project(new MirDerefProjection());
            case BoundLifetimeValueExpression lifetime:
                return Place(lifetime.Source).Project(new MirLifetimeProjection());
            case BoundReferenceDereferenceExpression reference:
                return Materialize(reference.Reference).Project(new MirDerefProjection());
            case BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } dereference:
                return PointerStorage(dereference.Operand).Project(new MirDerefProjection());
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
        MirPlace storage = Materialize(expression);
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
                return Save(new MirBorrow(Place(expression.Operand), MirBorrowKind.Raw, expression.Type), source);
            case SyntaxKind.StarToken:
                return Save(new MirUse(new MirCopy(Place(expression), expression.Type)), source);
            case SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken:
                MirPlace target = Place(expression.Operand);
                if (expression.Operand.Type is AtomicTypeSymbol atomic)
                    return Intrinsic(MirIntrinsicKind.AtomicUpdate,
                        [Address(target, atomic, source), new MirConstant(1, atomic.ElementType)], atomic.ElementType, source,
                        op: expression.OperatorKind == SyntaxKind.PlusPlusToken ? MirBinaryOperator.Add : MirBinaryOperator.Subtract,
                        returnsOldValue: expression.IsPostfix)!;
                MirOperand previous = Save(new MirUse(new MirCopy(target, expression.Type)), source);
                MirOperand next = Save(new MirBinary(expression.OperatorKind == SyntaxKind.PlusPlusToken ? MirBinaryOperator.Add : MirBinaryOperator.Subtract,
                    previous, new MirConstant(1, expression.Type is PointerTypeSymbol ? BuiltinTypes.Int : expression.Type), expression.Type), source);
                _current.Statements.Add(new MirAssign(target, new MirUse(next), source));
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

    private MirOperand Assignment(BoundAssignmentExpression expression)
    {
        MirSourceInfo source = Source(expression);
        MirPlace target = Place(expression.Target);
        MirPlace? previousCapture = _capturedPlace;
        if (expression.CapturesTarget) _capturedPlace = target;
        try
        {
            if (expression.Target.Type is AtomicTypeSymbol atomic)
                return Intrinsic(expression.OperatorKind == SyntaxKind.EqualsToken ? MirIntrinsicKind.AtomicStore : MirIntrinsicKind.AtomicUpdate,
                    [Address(target, atomic, source), Value(expression.Expression)], atomic.ElementType, source,
                    op: expression.OperatorKind == SyntaxKind.EqualsToken ? null : BinaryOperator(expression.OperatorKind))!;
            MirOperand? previous = expression.OperatorKind == SyntaxKind.EqualsToken ? null :
                Save(new MirUse(new MirCopy(target, expression.Type)), source);
            MirOperand value = Value(expression.Expression);
            if (previous is not null)
                value = Save(new MirBinary(BinaryOperator(expression.OperatorKind), previous, value, expression.Type), source);
            value = Snapshot(ConvertValue(value, expression.Type, source), source);
            _current.Statements.Add(new MirAssign(target, new MirUse(value), source)
            {
                WriteKind = expression.IsRawPlacement ? MirWriteKind.RawPlacement : expression.IsInitialization ? MirWriteKind.Initialize : MirWriteKind.Replace,
                PreviousValueState = expression.MovedPlaceReinitialization switch
                {
                    MovedPlaceReinitializationState.Live => MirPreviousValueState.Live,
                    MovedPlaceReinitializationState.DefinitelyMoved => MirPreviousValueState.DefinitelyMoved,
                    MovedPlaceReinitializationState.MaybeMoved => MirPreviousValueState.MaybeMoved,
                    _ => throw Unsupported(expression),
                },
                ConstructorField = expression.ConstructorField,
                RequiresRuntimeInitializationCheck = expression.RequiresRuntimeInitializationCheck,
            });
            return value;
        }
        finally { _capturedPlace = previousCapture; }
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
