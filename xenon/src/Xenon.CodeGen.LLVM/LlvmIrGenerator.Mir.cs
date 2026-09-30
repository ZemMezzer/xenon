using LLVMSharp.Interop;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.CodeGen.LLVM;

public sealed partial class LlvmIrGenerator
{
    private sealed unsafe partial class FunctionEmitter
    {
        private MirFunction? _mir;
        private readonly Dictionary<MirLocalId, LLVMValueRef> _mirAddresses = [];
        private readonly Dictionary<MirBlockId, LLVMBasicBlockRef> _mirBlocks = [];
        private LLVMBasicBlockRef? _mirUnwind;
        private Action<LLVMValueRef>? _mirReturnOverride;

        public void EmitMir(MirFunction function)
        {
            MirVerifier.VerifyOrThrow(function);
            _mir = function;
            var graph = new MirControlFlow(function, (block, edge) =>
                edge.Kind != MirEdgeKind.Unwind || MirMayUnwind(block.Terminator));
            foreach (MirLocal local in function.Locals)
            {
                LLVMValueRef address;
                if (local.Kind == MirLocalKind.Parameter && local.Variable is { } parameter)
                    address = _addresses[parameter];
                else if (local.Kind == MirLocalKind.Capture && local.Variable is CaptureVariableSymbol capture)
                    address = EmitCaptureAddress(capture);
                else
                {
                    address = _builder.BuildAlloca(_mapType(local.Type), local.Name);
                    if (local.Kind == MirLocalKind.Receiver) _builder.BuildStore(_thisValue, address);
                }
                _mirAddresses.Add(local.Id, address);
            }
            foreach (MirBasicBlock block in function.Blocks.Where(block => graph.Reachable.Contains(block.Id)))
                _mirBlocks.Add(block.Id, _llvmFunction.AppendBasicBlock(block.Id.ToString()));
            if (_isResumable && function.Resumable is { } resumable)
                _builder.BuildStore(_llvmFunction.GetParam(_llvmFunction.ParamsCount - 2), _mirAddresses[resumable.Result]);
            _builder.BuildBr(_mirBlocks[function.Entry]);
            foreach (MirBasicBlock block in function.Blocks.Where(block => graph.Reachable.Contains(block.Id)))
            {
                _builder.PositionAtEnd(_mirBlocks[block.Id]);
                foreach (MirStatement statement in block.Statements) EmitMirStatement(statement);
                EmitMirTerminator(block.Terminator);
            }
        }

        private bool MirMayUnwind(MirTerminator terminator)
        {
            if (!_exceptionsEnabled) return false;
            return terminator switch
            {
                MirCall { IsVirtual: false, InterfaceType: null, Callee: MirFunctionOperand direct } =>
                    !_functionEffects.TryGetValue(direct.Function, out LlvmFunctionEffects effects) || effects.MayThrow,
                MirDrop { IsVirtual: false, Destructor: { } destructor } =>
                    !_functionEffects.TryGetValue(destructor, out LlvmFunctionEffects effects) || effects.MayThrow,
                MirIntrinsicCall intrinsic => intrinsic.Intrinsic is MirIntrinsicKind.EnsureThreadLocal or
                    MirIntrinsicKind.DestroyOwner or MirIntrinsicKind.DestroyCallable or MirIntrinsicKind.ReleaseClosure,
                _ => true,
            };
        }

        private (LLVMValueRef Address, TypeSymbol Type) MirAddress(MirPlace place)
        {
            LLVMValueRef address = _mirAddresses[place.Local];
            TypeSymbol type = _mir!.Locals.Single(local => local.Id == place.Local).Type;
            foreach (MirProjection projection in place.Projections)
            {
                switch (projection)
                {
                    case MirDerefProjection:
                        address = _builder.BuildLoad2(_mapType(type), address, "deref");
                        type = type switch { PointerTypeSymbol pointer => pointer.ElementType, ReferenceTypeSymbol reference => reference.ElementType,
                            _ => throw new LlvmCodeGenerationException("Invalid MIR dereference.") };
                        break;
                    case MirBaseProjection subobject:
                        address = EmitBaseAddress(address, (StructTypeSymbol)type, subobject.BaseType);
                        type = subobject.BaseType;
                        break;
                    case MirFieldProjection field:
                        if (type is StructTypeSymbol structure && !TypeIdentity.AreSame(structure, field.Field.ContainingType))
                            address = EmitBaseAddress(address, structure, (StructTypeSymbol)field.Field.ContainingType!);
                        address = _builder.BuildStructGEP2(_mapType(field.Field.ContainingType!), address, (uint)field.Field.Ordinal, field.Field.Name + ".address");
                        type = field.Field.Type;
                        break;
                    case MirLifetimeProjection:
                        if (type is PinTypeSymbol pin) type = pin.ElementType;
                        else if (type is StorageTypeSymbol storage)
                        { address = GetStorageValueAddress(address, storage); type = storage.ElementType; }
                        else throw new LlvmCodeGenerationException("Invalid MIR lifetime projection.");
                        break;
                    case MirAtomicStorageProjection:
                        address = GetValueStorageAddress(address, type);
                        type = ((AtomicTypeSymbol)type).ElementType;
                        break;
                    case MirOwnerStorageProjection:
                        OwnershipTypeSymbol owner = (OwnershipTypeSymbol)type;
                        address = _builder.BuildLoad2(_mapType(type), address, "owner");
                        if (owner is SharedTypeSymbol or WeakTypeSymbol)
                            address = _builder.BuildLoad2(_mapType(owner.StorageType), OwnershipControlField(address, 2, "owner.data"), "owner.storage");
                        type = owner.StorageType;
                        LLVMValueRef slot = _builder.BuildAlloca(_mapType(type), "owner.storage.address");
                        _builder.BuildStore(address, slot);
                        address = slot;
                        break;
                    case MirIndexProjection index:
                        address = MirIndex(address, type, index.Indices.ToArray(), false);
                        type = type is ArrayTypeSymbol array ? array.ElementType : ((PointerTypeSymbol)type).ElementType;
                        break;
                    case MirLinearIndexProjection index:
                        address = MirIndex(address, type, [index.Index], true);
                        type = ((ArrayTypeSymbol)type).ElementType;
                        break;
                    default: throw new LlvmCodeGenerationException($"Unsupported MIR projection {projection.GetType().Name}.");
                }
            }
            return (address, type);
        }

        private LLVMValueRef MirIndex(LLVMValueRef address, TypeSymbol type, MirOperand[] indices, bool linearOnly)
        {
            LLVMValueRef pointer = _builder.BuildLoad2(_mapType(type), address, "index.base");
            if (type is not ArrayTypeSymbol array)
                return _builder.BuildGEP2(_mapType(((PointerTypeSymbol)type).ElementType), pointer,
                    new[] { MirValue(indices[0]) }, "pointer.index");
            LLVMValueRef linear = SizeConstant(0);
            if (linearOnly) linear = ConvertIntegerToSize(MirValue(indices[0]), indices[0].Type);
            else
            {
                EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pointer,
                    LLVMValueRef.CreateConstPointerNull(pointer.TypeOf), "array.valid"));
                for (int i = 0; i < indices.Length; i++)
                {
                    LLVMValueRef value = MirValue(indices[i]);
                    LLVMValueRef index = ConvertIntegerToSize(value, indices[i].Type);
                    if (_getIntegerBitWidth(indices[i].Type) > _getIntegerBitWidth(BuiltinTypes.NUInt))
                        EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, value,
                            LLVMValueRef.CreateConstInt(value.TypeOf, uint.MaxValue), "index.fits"));
                    LLVMValueRef dimension = ConvertIntegerToSize(ReadDimension(pointer, IntConstant(i)), BuiltinTypes.Int);
                    EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, index, dimension, "index.inrange"));
                    linear = _builder.BuildAdd(_builder.BuildMul(linear, dimension), index, "index.linear");
                }
            }
            return _builder.BuildGEP2(_mapType(array.ElementType), ArrayData(pointer, array), new[] { linear }, "element.address");
        }

        private LLVMValueRef MirValue(MirOperand operand) => operand switch
        {
            MirConstant constant => MirLiteral(constant),
            MirCopy copy => _builder.BuildLoad2(_mapType(copy.Type), MirAddress(copy.Place).Address, "copy"),
            MirMove move => _builder.BuildLoad2(_mapType(move.Type), MirAddress(move.Place).Address, "move"),
            MirFunctionOperand function => GetFunctionAddress(function.Function),
            _ => throw new LlvmCodeGenerationException($"Unsupported MIR operand {operand.GetType().Name}."),
        };

        private LLVMValueRef MirLiteral(MirConstant constant)
        {
            LLVMTypeRef type = _mapType(constant.Type);
            if (constant.Value is null) return LLVMValueRef.CreateConstNull(type);
            if (constant.Value is string text) return _builder.BuildGlobalStringPtr(text, "str");
            if (constant.Value is bool flag) return LLVMValueRef.CreateConstInt(type, flag ? 1UL : 0UL);
            if (constant.Type is PrimitiveTypeSymbol { IsFloatingPoint: true })
                return LLVMValueRef.CreateConstReal(type, Convert.ToDouble(constant.Value, System.Globalization.CultureInfo.InvariantCulture));
            ulong bits = constant.Value switch
            { int value => unchecked((ulong)value), long value => unchecked((ulong)value),
                uint value => value, ulong value => value, char value => value,
                _ => Convert.ToUInt64(constant.Value, System.Globalization.CultureInfo.InvariantCulture) };
            return LLVMValueRef.CreateConstInt(type, bits, true);
        }

        private LLVMValueRef MirRValue(MirRValue value)
        {
            switch (value)
            {
                case MirUse use: return MirValue(use.Operand);
                case MirDefault initial: return DefaultValue(initial.Type, _mapType, _virtualTables);
                case MirBorrow borrow: return MirAddress(borrow.Place).Address;
                case MirCast cast: return MirConvert(MirValue(cast.Operand), cast.Operand.Type, cast.Type);
                case MirUnary unary:
                    LLVMValueRef input = MirValue(unary.Operand);
                    return unary.Operator switch
                    {
                        MirUnaryOperator.Negate when unary.Type is PrimitiveTypeSymbol { IsFloatingPoint: true } => _builder.BuildFNeg(input, "neg"),
                        MirUnaryOperator.Negate => _builder.BuildNeg(input, "neg"),
                        _ => _builder.BuildNot(input, "not"),
                    };
                case MirBinary binary: return MirBinaryValue(binary.Operator, binary.Left.Type, MirValue(binary.Left), binary.Right.Type, MirValue(binary.Right));
                case MirAggregate aggregate:
                    LLVMValueRef result = _mapType(aggregate.Type).Poison;
                    for (int i = 0; i < aggregate.Fields.Length; i++)
                        result = _builder.BuildInsertValue(result, MirValue(aggregate.Fields[i]), (uint)i, "aggregate");
                    return result;
                case MirAtomicValue atomic:
                    LLVMValueRef payload = MirValue(atomic.Value);
                    if (IsLockBackedAtomic(atomic.AtomicType))
                        return _builder.BuildInsertValue(LLVMValueRef.CreateConstNull(_mapType(atomic.Type)), payload, 1, "atomic.value");
                    return TypeIdentity.AreSame(atomic.AtomicType.ElementType, BuiltinTypes.Bool)
                        ? _builder.BuildZExt(payload, _mapType(atomic.Type), "atomic.bool") : payload;
                case MirStorageState state:
                    var storage = MirAddress(state.Place);
                    return _builder.BuildLoad2(_context.Int1Type, GetStorageStateAddress(storage.Address, (StorageTypeSymbol)storage.Type), "storage.state");
                case MirStackSave:
                    return _builder.BuildCall2(LLVMTypeRef.CreateFunction(ResumePointer, [], false), _getMemoryRuntime().StackSave, Array.Empty<LLVMValueRef>(), "stack.save");
                case MirStackAllocation allocation:
                    return _builder.BuildAlloca(_mapType(((PointerTypeSymbol)allocation.Type).ElementType), "stack.node");
                case MirStaticFieldAddress field:

                    return _staticFields[field.Field];
                case MirTypeLayout layout:
                    return SizeConstant(layout.Query switch {
                        MirLayoutQuery.Size => _getAbiSize(layout.SubjectType),
                        MirLayoutQuery.Alignment => _getAbiAlignment(layout.SubjectType),
                        _ => _getFieldOffset((StructTypeSymbol)layout.SubjectType, layout.Field!) });
                case MirInterfaceView view:
                    LLVMValueRef data = MirValue(view.Address);
                    LLVMValueRef dispatch = EmitRuntimeDispatch(view.SourceType, data);
                    LLVMValueRef map = _builder.BuildLoad2(ResumePointer, dispatch, "interface.map");
                    LLVMValueRef table = EmitInterfaceTableLookup(map, view.InterfaceType);
                    return _builder.BuildInsertValue(_builder.BuildInsertValue(_mapType(view.Type).Poison, data, 0, "interface.data"), table, 1, "interface.table");
                case MirCurrentException:
                    return MirRuntime(_getExceptionRuntime().Current, [], "exception.current");
                case MirExceptionMatches matches:
                    return MirRuntime(_getExceptionRuntime().Matches, [MirValue(matches.Record), _getExceptionTypeKey(matches.ExceptionType)], "exception.matches");
                case MirExceptionReference reference:
                    return MirRuntime(_getExceptionRuntime().Object, [MirValue(reference.Record)], "exception.object");
                default: throw new LlvmCodeGenerationException($"Unsupported MIR rvalue {value.GetType().Name}.");
            }
        }

        private LLVMValueRef MirRuntime(LlvmFunction function, LLVMValueRef[] arguments, string name) =>
            _builder.BuildCall2(function.Type, function.Value, arguments, name);

        private void EmitMirStatement(MirStatement statement)
        {
            switch (statement)
            {
                case MirAssign assign: _builder.BuildStore(MirRValue(assign.Value), MirAddress(assign.Destination).Address); break;
                case MirStorageLive or MirStorageDead or MirForget: break;
                case MirSetStorageState state:
                    var storage = MirAddress(state.Place);
                    _builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int1Type, state.Initialized ? 1UL : 0UL),
                        GetStorageStateAddress(storage.Address, (StorageTypeSymbol)storage.Type)); break;
                case MirStackRestore restore:
                    _builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [ResumePointer], false),
                        _getMemoryRuntime().StackRestore, new[] { MirValue(restore.Token) }, ""); break;
                case MirInitializeDispatch dispatch:
                    if (_virtualTables.TryGetValue(dispatch.Type, out LlvmVTable table))
                        _builder.BuildStore(table.Value, EmitDispatchAddress(dispatch.Type, MirAddress(dispatch.Place).Address));
                    break;
                case MirReleaseException release:
                    if (_exceptionsEnabled) MirRuntime(release.Abandon ? _getExceptionRuntime().Abandon : _getExceptionRuntime().Handle,
                        [MirValue(release.Record)], "");
                    break;
                default: throw new LlvmCodeGenerationException($"Unsupported MIR statement {statement.GetType().Name}.");
            }
        }

        private void EmitMirTerminator(MirTerminator terminator)
        {
            switch (terminator)
            {
                case MirGoto jump: _builder.BuildBr(_mirBlocks[jump.Target]); break;
                case MirSwitch branch:
                    LLVMValueRef selection = _builder.BuildSwitch(MirValue(branch.Value), _mirBlocks[branch.Otherwise], (uint)branch.Cases.Length);
                    foreach (MirSwitchCase item in branch.Cases) selection.AddCase(MirLiteral(item.Value), _mirBlocks[item.Target]);
                    break;
                case MirReturn result:
                    if (_mirReturnOverride is { } returnOverride) returnOverride(MirValue(result.Value!));
                    else if (_isResumable) _builder.BuildBr(_coroutineComplete);
                    else if (result.Value is null) _builder.BuildRetVoid();
                    else _builder.BuildRet(MirValue(result.Value));
                    break;
                case MirUnreachable: _builder.BuildUnreachable(); break;
                case MirAbort:
                    if (_exceptionsEnabled) MirRuntime(_getExceptionRuntime().Terminate, [], "");
                    // Without native exceptions no unwind edge can reach this block.
                    _builder.BuildUnreachable(); break;
                case MirResumeUnwind:
                    if (_exceptionsEnabled) MirRuntime(_getExceptionRuntime().Rethrow, [], "");
                    _builder.BuildUnreachable(); break;
                case MirSuspend suspend: EmitResumableSuspend(_mirBlocks[suspend.Resume], false); break;
                case MirThrow thrown: EmitMirThrow(thrown); break;
                case MirCall call:
                    _mirUnwind = _mirBlocks.TryGetValue(call.Unwind, out LLVMBasicBlockRef callUnwind) ? callUnwind : (LLVMBasicBlockRef?)null;
                    LLVMValueRef value = EmitMirCall(call);
                    if (call.Destination is { } destination) _builder.BuildStore(value, MirAddress(destination).Address);
                    _builder.BuildBr(_mirBlocks[call.Normal]); _mirUnwind = null; break;
                case MirDrop drop:
                    _mirUnwind = _mirBlocks.TryGetValue(drop.Unwind, out LLVMBasicBlockRef dropUnwind) ? dropUnwind : (LLVMBasicBlockRef?)null;
                    if (drop.Destructor is { } destructor)
                        MirLifecycle(destructor, MirAddress(drop.Place).Address, drop.IsVirtual);
                    _builder.BuildBr(_mirBlocks[drop.Normal]); _mirUnwind = null; break;
                case MirIntrinsicCall intrinsic:
                    _mirUnwind = _mirBlocks.TryGetValue(intrinsic.Unwind, out LLVMBasicBlockRef intrinsicUnwind) ? intrinsicUnwind : (LLVMBasicBlockRef?)null;
                    LLVMValueRef intrinsicValue = EmitMirIntrinsic(intrinsic);
                    if (intrinsic.Destination is { } target) _builder.BuildStore(intrinsicValue, MirAddress(target).Address);
                    _builder.BuildBr(_mirBlocks[intrinsic.Normal]); _mirUnwind = null; break;
                default: throw new LlvmCodeGenerationException($"Unsupported MIR terminator {terminator.GetType().Name}.");
            }
        }

        private LLVMValueRef MirInvoke(LLVMTypeRef signature, LLVMValueRef callee, LLVMValueRef[] arguments, string name)
        {
            if (!_exceptionsEnabled || _mirUnwind is not { } target) return _builder.BuildCall2(signature, callee, arguments, name);
            LLVMBasicBlockRef normal = _llvmFunction.AppendBasicBlock("mir.invoke.continue");
            LLVMBasicBlockRef unwind = _llvmFunction.AppendBasicBlock("mir.invoke.unwind");
            LLVMValueRef result = _builder.BuildInvoke2(signature, callee, arguments, normal, unwind, name);
            EmitNativeCatch(unwind, () => _builder.BuildBr(target));
            _builder.PositionAtEnd(normal);
            return result;
        }

        private void EmitMirThrow(MirThrow thrown)
        {
            if (thrown.Exception is null) { _builder.BuildBr(_mirBlocks[thrown.Unwind]); return; }
            MirOperand exception = thrown.Exception;
            LLVMValueRef value = MirValue(exception);
            LlvmExceptionRuntime runtime = _getExceptionRuntime();
            LLVMValueRef destructor = TypeFacts.GetCompleteDestructor(exception.Type) is { } cleanup ? _functions[cleanup].Value : LLVMValueRef.CreateConstPointerNull(ResumePointer);
            LLVMValueRef record = MirRuntime(runtime.Allocate, [SizeConstant(_getAbiSize(exception.Type)), SizeConstant(_getAbiAlignment(exception.Type)),
                _getExceptionTypeKey(exception.Type), _builder.BuildGlobalStringPtr(_getExceptionTypeDisplayName(exception.Type), "exception.name"), destructor], "exception.record");
            _builder.BuildStore(value, MirRuntime(runtime.Object, [record], "exception.storage"));
            _mirUnwind = _mirBlocks[thrown.Unwind];
            MirInvoke(runtime.Throw.Type, runtime.Throw.Value, [record], "");
            _builder.BuildUnreachable(); _mirUnwind = null;
        }
    }
}
