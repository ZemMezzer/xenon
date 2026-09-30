using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir;

public sealed record MirVerificationError(string Function, MirBlockId? Block, int? Statement,
    MirSourceInfo Source, string Message)
{
    public override string ToString() =>
        $"{Function}/{Block?.ToString() ?? "entry"}/{(Statement is { } index ? $"statement {index}" : "terminator")}: " +
        $"{Message} ({Source.Location.Path}:{Source.Location.Start.Line + 1}:{Source.Location.Start.Character + 1})";
}

public sealed class MirVerificationException(ImmutableArray<MirVerificationError> errors)
    : InvalidOperationException(string.Join(Environment.NewLine, errors))
{
    public ImmutableArray<MirVerificationError> Errors { get; } = errors;
}

/// <summary>Structural and type validation; flow-sensitive checks are separate MIR passes.</summary>
public sealed partial class MirVerifier
{
    private readonly MirFunction _function;
    private readonly Dictionary<MirLocalId, MirLocal> _locals = [];
    private readonly HashSet<MirBlockId> _blocks = [];
    private readonly ImmutableArray<MirVerificationError>.Builder _errors = ImmutableArray.CreateBuilder<MirVerificationError>();
    private MirBlockId? _block;
    private int? _statement;
    private MirSourceInfo _source;

    private MirVerifier(MirFunction function)
    {
        _function = function;
        _source = function.Source;
    }

    public static ImmutableArray<MirVerificationError> Verify(MirFunction function) => new MirVerifier(function).Run();

    public static void VerifyOrThrow(MirFunction function)
    {
        ImmutableArray<MirVerificationError> errors = Verify(function);
        if (!errors.IsEmpty) throw new MirVerificationException(errors);
    }

    private void Error(string message) => _errors.Add(new(_function.Symbol.Name, _block, _statement, _source, message));
    private void Same(TypeSymbol? actual, TypeSymbol expected, string subject)
    {
        if (actual is not null && !TypeIdentity.AreSame(actual, expected))
            Error($"{subject}: expected {expected}, got {actual}");
    }

    private ImmutableArray<MirVerificationError> Run()
    {
        if (_function.Locals.IsDefault || _function.Blocks.IsDefault)
        {
            Error("local and block tables must be initialized");
            return _errors.ToImmutable();
        }
        foreach (MirLocal local in _function.Locals)
        {
            _source = local.Source;
            if (local.Id.Value < 0 || !_locals.TryAdd(local.Id, local)) Error($"invalid or duplicate local {local.Id}");
            if (TypeIdentity.AreSame(local.Type, BuiltinTypes.Void) || TypeIdentity.AreSame(local.Type, BuiltinTypes.Error))
                Error($"local {local.Id} has non-storable type {local.Type}");
        }
        _source = _function.Source;
        foreach (MirBasicBlock block in _function.Blocks)
            if (block.Id.Value < 0 || !_blocks.Add(block.Id)) Error($"invalid or duplicate block {block.Id}");
        if (!_blocks.Contains(_function.Entry)) Error($"entry {_function.Entry} does not exist");
        foreach (MirBasicBlock block in _function.Blocks)
        {
            _block = block.Id;
            _statement = null;
            _source = _function.Source;
            if (block.Statements.IsDefault) Error("statement list must be initialized");
            else for (int index = 0; index < block.Statements.Length; index++)
            {
                _statement = index;
                MirStatement statement = block.Statements[index];
                _source = statement.Source;
                switch (statement)
                {
                    case MirAssign assign:
                        TypeSymbol? destinationType = Place(assign.Destination);
                        RValue(assign.Value);
                        if (destinationType is not null) Same(assign.Value.Type, destinationType, "assignment");
                        break;
                    case MirInitializeDispatch dispatch: Same(Place(dispatch.Place), dispatch.Type, "dispatch initialization"); break;
                    case MirReleaseException release: Operand(release.Record); break;
                    case MirStackRestore restore: Operand(restore.Token); if (restore.Token.Type is not PointerTypeSymbol) Error("stack token requires a pointer"); break;
                    case MirSetStorageState state: if (Place(state.Place) is not StorageTypeSymbol) Error("storage state requires a wrapper"); break;
                    case MirForget forget: Place(forget.Place); break;
                    case MirStorageLive live: Local(live.Local); break;
                    case MirStorageDead dead: Local(dead.Local); break;
                    default: Error($"unknown statement {statement.GetType().Name}"); break;
                }
            }
            _statement = null;
            if (block.Terminator is null) { Error("block has no terminator"); continue; }
            _source = block.Terminator.Source;
            foreach (MirEdge successor in block.Terminator.Successors)
                if (!_blocks.Contains(successor.Target)) Error($"successor {successor.Target} does not exist");
            Terminator(block.Terminator);
        }
        return _errors.ToImmutable();
    }

    private TypeSymbol? Local(MirLocalId id)
    {
        if (_locals.TryGetValue(id, out MirLocal? local)) return local.Type;
        Error($"local {id} does not exist");
        return null;
    }

    private TypeSymbol? Place(MirPlace place)
    {
        TypeSymbol? type = Local(place.Local);
        foreach (MirProjection projection in place.Projections)
        {
            switch (projection)
            {
                case MirFieldProjection field:
                    if (type is not null && (type is not IFieldStorageTypeSymbol structure ||
                        !structure.AllInstanceFields.Contains(field.Field)))
                        Error($"field {field.Field.Name} is not an instance field of {type}");
                    type = field.Field.Type;
                    break;
                case MirAtomicStorageProjection:
                    if (type is AtomicTypeSymbol atomicStorage) type = atomicStorage.ElementType;
                    else { Error("atomic storage projection requires an atomic wrapper"); type = null; }
                    break;
                case MirOwnerStorageProjection:
                    if (type is OwnershipTypeSymbol owner) type = owner.StorageType;
                    else { Error($"cannot unwrap ownership storage {type}"); type = null; }
                    break;
                case MirLifetimeProjection:
                    if (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
                    else { Error($"cannot unwrap lifetime storage {type}"); type = null; }
                    break;
                case MirBaseProjection parent:
                    if (type is not StructTypeSymbol derived || !TypeIdentity.AreSame(derived.BaseType, parent.BaseType))
                        Error("base projection requires the immediate base subobject");
                    type = parent.BaseType;
                    break;
                case MirLinearIndexProjection linear:
                    Operand(linear.Index);
                    if (type is ArrayTypeSymbol linearArray) type = linearArray.ElementType;
                    else { Error("linear element projection requires an array"); type = null; }
                    if (linear.Index.Type is not PrimitiveTypeSymbol { IsInteger: true }) Error("linear index must be integral");
                    break;
                case MirDerefProjection:
                    TypeSymbol? element = type switch
                    {
                        PointerTypeSymbol pointer => pointer.ElementType,
                        ReferenceTypeSymbol reference => reference.ElementType,
                        _ => null,
                    };
                    if (type is not null && element is null) Error($"cannot dereference {type}");
                    type = element;
                    break;
                case MirIndexProjection index:
                    foreach (MirOperand dimension in index.Indices)
                    {
                        Operand(dimension);
                        if (dimension is not (MirCopy or MirConstant)) Error("place index must be a copy or constant");
                        if (dimension.Type is not PrimitiveTypeSymbol { IsInteger: true }) Error("place index must be an integer");
                    }
                    int rank = type is ArrayTypeSymbol ranked ? ranked.Rank : 1;
                    if (index.Indices.Length != rank) Error($"index rank mismatch: expected {rank}, got {index.Indices.Length}");
                    TypeSymbol? indexed = type switch
                    {
                        ArrayTypeSymbol array => array.ElementType,
                        PointerTypeSymbol pointer => pointer.ElementType,
                        _ => null,
                    };
                    if (type is not null && indexed is null) Error($"cannot index {type}");
                    type = indexed;
                    break;
                default: Error($"unknown projection {projection.GetType().Name}"); break;
            }
        }
        return type;
    }

    private void Operand(MirOperand operand)
    {
        switch (operand)
        {
            case MirCopy copy: Same(Place(copy.Place), copy.Type, "copy operand"); break;
            case MirMove move: Same(Place(move.Place), move.Type, "move operand"); break;
            case MirConstant: break;
            case MirFunctionOperand function:
                if (function.Type is not FunctionPointerTypeSymbol signature) Error("function operand requires a function pointer type");
                else
                {
                    Same(signature.ReturnType, function.Function.ReturnType, "function result");
                    if (signature.ParameterTypes.Length != function.Function.Parameters.Length) Error("function signature arity mismatch");
                    else for (int i = 0; i < signature.ParameterTypes.Length; i++)
                        Same(signature.ParameterTypes[i], function.Function.Parameters[i].Type, "function parameter");
                }
                break;
            default: Error($"unknown operand {operand.GetType().Name}"); break;
        }
        if (TypeIdentity.AreSame(operand.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(operand.Type, BuiltinTypes.Void))
            Error($"invalid operand type {operand.Type}");
    }

    private void RValue(MirRValue value)
    {
        switch (value)
        {
            case MirAtomicValue atomic:
                Operand(atomic.Value);
                Same(atomic.Value.Type, atomic.AtomicType.ElementType, "atomic initializer");
                break;
            case MirStackSave or MirStackAllocation: break;
            case MirStorageState state: if (Place(state.Place) is not StorageTypeSymbol) Error("storage state requires a wrapper"); break;
            case MirUse use: Operand(use.Operand); break;
            case MirUnary unary:
                Operand(unary.Operand);
                Same(unary.Type, unary.Operand.Type, "unary result");
                break;
            case MirBinary binary:
                Operand(binary.Left); Operand(binary.Right);
                bool pointerOffset = binary.Left.Type is PointerTypeSymbol &&
                    binary.Right.Type is PrimitiveTypeSymbol { IsInteger: true } &&
                    binary.Operator is MirBinaryOperator.Add or MirBinaryOperator.Subtract;
                bool pointerDifference = binary.Left.Type is PointerTypeSymbol && binary.Right.Type is PointerTypeSymbol &&
                    binary.Operator == MirBinaryOperator.Subtract;
                if (!pointerOffset && binary.Operator is not (MirBinaryOperator.ShiftLeft or MirBinaryOperator.ShiftRight))
                    Same(binary.Right.Type, binary.Left.Type, "binary operand");
                bool comparison = binary.Operator is MirBinaryOperator.Equal or MirBinaryOperator.NotEqual or
                    MirBinaryOperator.Less or MirBinaryOperator.LessOrEqual or MirBinaryOperator.Greater or MirBinaryOperator.GreaterOrEqual;
                if (pointerDifference) { if (binary.Type is not PrimitiveTypeSymbol { IsInteger: true }) Error("pointer difference must be integral"); } else Same(binary.Type, comparison ? BuiltinTypes.Bool : binary.Left.Type, "binary result");
                break;
            case MirCast cast: Operand(cast.Operand); break;
            case MirBorrow borrow:
                TypeSymbol? borrowed = Place(borrow.Place);
                if (borrow.Type is ReferenceTypeSymbol reference)
                {
                    Same(borrowed, reference.ElementType, "borrow referent");
                    if (borrow.Kind == MirBorrowKind.Raw || reference.IsReadonly != (borrow.Kind == MirBorrowKind.Shared))
                        Error("borrow kind does not match reference mutability");
                }
                else if (borrow.Type is PointerTypeSymbol pointer && borrow.Kind == MirBorrowKind.Raw)
                    Same(borrowed, pointer.ElementType, "pointer referent");
                else Error("borrow must produce a matching reference or raw pointer");
                break;
            case MirAggregate aggregate:
                if (aggregate.Fields.IsDefault) { Error("aggregate operands must be initialized"); break; }
                foreach (MirOperand field in aggregate.Fields) Operand(field);
                if (aggregate.Type is IFieldStorageTypeSymbol storage)
                {
                    FieldSymbol[] fields = storage.AllInstanceFields.ToArray();
                    if (fields.Length != aggregate.Fields.Length) Error("aggregate field count mismatch");
                    else for (int i = 0; i < fields.Length; i++) Same(aggregate.Fields[i].Type, fields[i].Type, "aggregate field");
                }
                else Error("aggregate requires a field-storage type");
                break;
            case MirTypeLayout layout:
                if (layout.Query == MirLayoutQuery.FieldOffset && layout.Field is null) Error("field offset requires a field");
                break;
            case MirInterfaceView view:
                Operand(view.Address);
                if (view.Address.Type is not PointerTypeSymbol) Error("interface view requires an address");
                break;
            case MirStaticFieldAddress address:
                if (!address.Field.IsStatic) Error("static address requires a static field");
                Same(address.PointerType.ElementType, address.Field.Type, "static field address");
                break;
            case MirCurrentException: break;
            case MirExceptionMatches match: Operand(match.Record); break;
            case MirExceptionReference exceptionReference: Operand(exceptionReference.Record); break;
            case MirDefault: break;
            default: Error($"unknown rvalue {value.GetType().Name}"); break;
        }
    }

    private void Terminator(MirTerminator terminator)
    {
        switch (terminator)
        {
            case MirGoto or MirResumeUnwind or MirUnreachable or MirAbort: break;
            case MirSwitch selection:
                Operand(selection.Value);
                if (selection.Cases.IsDefault) { Error("switch cases must be initialized"); break; }
                var values = new HashSet<object?>();
                foreach (MirSwitchCase item in selection.Cases)
                {
                    Operand(item.Value);
                    Same(item.Value.Type, selection.Value.Type, "switch case");
                    if (!values.Add(item.Value.Value)) Error("duplicate switch case");
                }
                break;
            case MirIntrinsicCall intrinsic:
                if (intrinsic.Arguments.IsDefault) { Error("intrinsic arguments must be initialized"); break; }
                foreach (MirOperand argument in intrinsic.Arguments) Operand(argument);
                Intrinsic(intrinsic);
                if (intrinsic.Destination is not null) Same(Place(intrinsic.Destination), intrinsic.ResultType, "intrinsic destination");
                if (TypeIdentity.AreSame(intrinsic.ResultType, BuiltinTypes.Void) && intrinsic.Destination is not null)
                    Error("void intrinsic has a destination");
                break;
            case MirCall call:
                Operand(call.Callee);
                if (call.Receiver is not null) Operand(call.Receiver);
                if (call.Callee is MirFunctionOperand direct && direct.Function.HasImplicitThis && call.Receiver is null)
                    Error("instance call requires a receiver");
                if (call.Arguments.IsDefault) { Error("call arguments must be initialized"); break; }
                foreach (MirOperand argument in call.Arguments) Operand(argument);
                TypeSymbol? destination = call.Destination is null ? null : Place(call.Destination);
                (TypeSymbol? result, ImmutableArray<TypeSymbol> parameters) = call.Callee.Type switch
                {
                    FunctionPointerTypeSymbol pointer => (pointer.ReturnType, pointer.ParameterTypes),
                    FunctionValueTypeSymbol callable => (callable.ReturnType, callable.ParameterTypes),
                    _ => (null, []),
                };
                if (result is null) Error("callee is not callable");
                else
                {
                    if (parameters.Length != call.Arguments.Length) Error("call argument count mismatch");
                    else for (int i = 0; i < parameters.Length; i++) Same(call.Arguments[i].Type, parameters[i], "call argument");
                    if (call.Destination is not null)
                    {
                        if (TypeIdentity.AreSame(result, BuiltinTypes.Void)) Error("void call has a destination");
                        else Same(destination, result, "call destination");
                    }
                }
                break;
            case MirReturn ret:
                if (ret.Value is not null)
                {
                    Operand(ret.Value);
                    Same(ret.Value.Type, _function.ReturnType, "return");
                }
                else if (!TypeIdentity.AreSame(_function.ReturnType, BuiltinTypes.Void)) Error("non-void return requires a value");
                break;
            case MirThrow { Exception: { } exception }: Operand(exception); break;
            case MirThrow: break;
            case MirSuspend { Payload: { } payload }: Operand(payload); break;
            case MirSuspend: break;
            case MirDrop drop: Place(drop.Place); break;
            default: Error($"unknown terminator {terminator.GetType().Name}"); break;
        }
    }
}
