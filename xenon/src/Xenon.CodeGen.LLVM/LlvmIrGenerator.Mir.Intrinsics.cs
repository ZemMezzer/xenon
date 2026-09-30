using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;
using Xenon.Compiler;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
namespace Xenon.CodeGen.LLVM;
public sealed partial class LlvmIrGenerator
{
    private sealed unsafe partial class FunctionEmitter
    {
        private LLVMValueRef EmitMirIntrinsic(MirIntrinsicCall operation)
        {
            if (operation.Intrinsic is MirIntrinsicKind.AllocateStackArray or MirIntrinsicKind.AllocateHeapArray)
                return MirArrayAllocation(operation);
            LLVMValueRef[] args = operation.Arguments.Select(MirValue).ToArray();
            TypeSymbol Pointee(int index) => ((PointerTypeSymbol)operation.Arguments[index].Type).ElementType;
            switch (operation.Intrinsic)
            {
                case MirIntrinsicKind.CloneValue: return EmitCopyValue(args[0], operation.ResultType);
                case MirIntrinsicKind.ArrayLength:
                    EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, args[0], LLVMValueRef.CreateConstPointerNull(args[0].TypeOf), "array.valid"));
                    return _builder.BuildLoad2(_context.Int32Type, args[0], "array.length");
                case MirIntrinsicKind.ArrayRank: return IntConstant(((ArrayTypeSymbol)operation.Arguments[0].Type).Rank);
                case MirIntrinsicKind.ArrayDimension:
                    EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, args[1],
                        LLVMValueRef.CreateConstInt(args[1].TypeOf, (ulong)((ArrayTypeSymbol)operation.Arguments[0].Type).Rank), "dimension.valid"));
                    return ReadDimension(args[0], args[1]);
                case MirIntrinsicKind.Allocate: return EmitAllocation(SizeConstant(Math.Max(1UL, _getAbiSize(operation.SubjectType!))), "allocate", SizeConstant(_getAbiAlignment(operation.SubjectType!)));
                case MirIntrinsicKind.Free: return EmitDeallocation(args[0]);
                case MirIntrinsicKind.Malloc:
                    return _builder.BuildCall2(_getMemoryRuntime().MallocType, _getMemoryRuntime().Malloc, args, "malloc");
                case MirIntrinsicKind.AlignedMalloc:
                    return _builder.BuildCall2(_getMemoryRuntime().AlignedMallocType, _getMemoryRuntime().AlignedMalloc, args, "aligned.malloc");
                case MirIntrinsicKind.Calloc:
                    return _builder.BuildCall2(_getMemoryRuntime().CallocType, _getMemoryRuntime().Calloc, args, "calloc");
                case MirIntrinsicKind.AdoptUnique: return args[0];
                case MirIntrinsicKind.AdoptShared: return MirSharedAdoption(args[0]);
                case MirIntrinsicKind.ConvertWeak: return EmitOwnershipRetain(args[0], 1, "weak.create");
                case MirIntrinsicKind.LockWeak: return MirWeakLock(args[0]);
                case MirIntrinsicKind.ReleaseStrong:
                    return MirReleaseCount(args[0], false);
                case MirIntrinsicKind.ReleaseCallableCount:
                    return MirReleaseCount(args[0], true);
                case MirIntrinsicKind.SharedPayload:
                    return _builder.BuildLoad2(_mapType(operation.ResultType),
                        OwnershipControlField(args[0], 2, "shared.payload.address"), "shared.payload");
                case MirIntrinsicKind.ReleaseWeak:
                    return EmitWeakRelease(args[0]);
                case MirIntrinsicKind.CallableControl:
                    return _builder.BuildExtractValue(args[0], 1, "function.control");
                case MirIntrinsicKind.ClosureControl:
                    return _builder.BuildLoad2(ResumePointer, _closureEnvironment, "resumable.capture.owner");
                case MirIntrinsicKind.CallableEnvironment:
                    return _builder.BuildLoad2(ResumePointer, FunctionControlField(args[0], 1, "function.environment.address"), "function.environment");
                case MirIntrinsicKind.CallableDestructor:
                    return _builder.BuildLoad2(ResumePointer, FunctionControlField(args[0], 2, "function.destructor.address"), "function.destructor");
                case MirIntrinsicKind.DestroyOwner:
                    LLVMValueRef owner = _builder.BuildLoad2(_mapType(operation.SubjectType!), args[0], "owner");
                    return operation.SubjectType is SharedTypeSymbol shared ? EmitSharedRelease(owner, shared, operation.Function) : EmitWeakRelease(owner);
                case MirIntrinsicKind.DestroyCallable:
                    return EmitFunctionControlRelease(_builder.BuildExtractValue(_builder.BuildLoad2(_mapType(operation.SubjectType!), args[0], "callable"), 1, "callable.control"));
                case MirIntrinsicKind.MakeCallable: return MirMakeCallable(operation, args);
                case MirIntrinsicKind.CheckStorageEmpty or MirIntrinsicKind.CheckStorageInitialized:
                    EmitStorageStateCheck(args[0], (StorageTypeSymbol)Pointee(0), operation.Intrinsic == MirIntrinsicKind.CheckStorageInitialized);
                    return default;
                case MirIntrinsicKind.EnsureThreadLocal:
                    if (_threadLocalEnsures.TryGetValue(operation.Field!, out LlvmFunction ensure))
                        MirInvoke(ensure.Type, ensure.Value, [], "");
                    return default;
                case MirIntrinsicKind.ReleaseClosure:
                    return EmitFunctionControlRelease(_builder.BuildLoad2(ResumePointer, _closureEnvironment, "resumable.capture.owner"));
                case MirIntrinsicKind.CreateContinuation:
                    return ResumeCall(RuntimeAbiNames.ResumeContinuation, _mapType(operation.ResultType),
                        [_llvmFunction.GetParam(_llvmFunction.ParamsCount - 1)], "await.continuation");
                case MirIntrinsicKind.AtomicExchange:
                    return EmitAtomicExchange(args[1], args[0], (AtomicTypeSymbol)Pointee(0));
                case MirIntrinsicKind.CompareExchangeOwned:
                    AtomicTypeSymbol exchangedAtomic = (AtomicTypeSymbol)Pointee(0);
                    AcquireAtomicLock(args[0], exchangedAtomic, "compare.exchange");
                    LLVMValueRef exchangedAddress = AtomicValueAddress(args[0], exchangedAtomic);
                    LLVMValueRef old = _builder.BuildLoad2(_mapType(exchangedAtomic.ElementType), exchangedAddress, "compare.exchange.current");
                    LLVMValueRef succeeded = EmitValueEquality(old, args[1], exchangedAtomic.ElementType);
                    _builder.BuildStore(_builder.BuildSelect(succeeded, args[2], old, "compare.exchange.selected"), exchangedAddress);
                    ReleaseAtomicLock(args[0], exchangedAtomic);
                    LLVMValueRef discarded = _builder.BuildSelect(succeeded, old, args[2], "compare.exchange.discarded");
                    return _builder.BuildInsertValue(_builder.BuildInsertValue(_mapType(operation.ResultType).Poison,
                        succeeded, 0), discarded, 1);
                case MirIntrinsicKind.AtomicLoad: return EmitLoad(Pointee(0), args[0], "atomic.load");
                case MirIntrinsicKind.AtomicInitialize or MirIntrinsicKind.AtomicStore:
                    AtomicTypeSymbol atomic = (AtomicTypeSymbol)Pointee(0);
                    if (operation.Intrinsic == MirIntrinsicKind.AtomicStore && IsLockBackedAtomic(atomic))
                        EmitAtomicReplacement(args[1], args[0], atomic);
                    else EmitAtomicStore(args[1], args[0], atomic, operation.Intrinsic == MirIntrinsicKind.AtomicInitialize);
                    return args[1];
                case MirIntrinsicKind.AtomicUpdate:
                    AtomicTypeSymbol updated = (AtomicTypeSymbol)Pointee(0);
                    LLVMAtomicRMWBinOp op = operation.Operator switch {
                        MirBinaryOperator.Add when updated.ElementType is PrimitiveTypeSymbol { IsFloatingPoint: true } => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpFAdd,
                        MirBinaryOperator.Subtract when updated.ElementType is PrimitiveTypeSymbol { IsFloatingPoint: true } => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpFSub,
                        MirBinaryOperator.Add => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAdd,
                        MirBinaryOperator.Subtract => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpSub,
                        MirBinaryOperator.BitwiseAnd => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAnd,
                        MirBinaryOperator.BitwiseOr => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpOr,
                        MirBinaryOperator.BitwiseXor => LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpXor,
                        _ => throw new LlvmCodeGenerationException("Invalid MIR atomic update.") };
                    LLVMValueRef previous = _atomics.Fetch(op, args[0], args[1], LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent, "atomic.previous");
                    return operation.ReturnsOldValue ? previous : MirBinaryValue(operation.Operator!.Value, updated.ElementType, previous, operation.Arguments[1].Type, args[1]);
                case MirIntrinsicKind.CompareExchange: return MirCompareExchange(args[0], (AtomicTypeSymbol)Pointee(0), args[1], args[2]);
                case MirIntrinsicKind.Swap:
                    TypeSymbol leftType = Pointee(0), rightType = Pointee(1);
                    if (leftType is AtomicTypeSymbol leftAtomic)
                        _builder.BuildStore(EmitAtomicExchange(_builder.BuildLoad2(_mapType(rightType), args[1], "swap.right"), args[0], leftAtomic), args[1]);
                    else if (rightType is AtomicTypeSymbol rightAtomic)
                        _builder.BuildStore(EmitAtomicExchange(_builder.BuildLoad2(_mapType(leftType), args[0], "swap.left"), args[1], rightAtomic), args[0]);
                    else
                    {
                        LLVMValueRef left = _builder.BuildLoad2(_mapType(leftType), args[0], "swap.left");
                        LLVMValueRef right = _builder.BuildLoad2(_mapType(rightType), args[1], "swap.right");
                        _builder.BuildStore(right, args[0]); _builder.BuildStore(left, args[1]);
                    }
                    return default;
                default: throw new LlvmCodeGenerationException($"Unsupported MIR intrinsic {operation.Intrinsic}.");
            }
        }

        // Counter primitives return the last-owner decision to MIR. All user
        // destruction and deallocation edges are elaborated in MIR.
        private LLVMValueRef MirReleaseCount(LLVMValueRef control, bool callable)
        {
            string name = callable ? "function.release" : "shared.release";
            LLVMBasicBlockRef entry = _builder.InsertBlock;
            LLVMBasicBlockRef decrement = _llvmFunction.AppendBasicBlock(name + ".count");
            LLVMBasicBlockRef acquire = _llvmFunction.AppendBasicBlock(name + ".acquire");
            LLVMBasicBlockRef done = _llvmFunction.AppendBasicBlock(name + ".end");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, control,
                LLVMValueRef.CreateConstPointerNull(control.TypeOf), name + ".valid"), decrement, done);
            _builder.PositionAtEnd(decrement);
            LLVMValueRef address = callable ? FunctionControlField(control, 0, name + ".address")
                : OwnershipControlField(control, 0, name + ".address");
            LLVMValueRef previous = _atomics.FetchSub(address, SizeConstant(1),
                LLVMAtomicOrdering.LLVMAtomicOrderingRelease, name);
            LLVMValueRef last = _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, previous, SizeConstant(1), name + ".final");
            _builder.BuildCondBr(last, acquire, done);
            _builder.PositionAtEnd(acquire);
            _atomics.AcquireFence();
            _builder.BuildBr(done);
            _builder.PositionAtEnd(done);
            LLVMValueRef result = _builder.BuildPhi(_context.Int1Type, name + ".last");
            result.AddIncoming([LLVMValueRef.CreateConstInt(_context.Int1Type, 0), last,
                LLVMValueRef.CreateConstInt(_context.Int1Type, 1)], [entry, decrement, acquire], 3);
            return result;
        }

        private LLVMValueRef MirMakeCallable(MirIntrinsicCall operation, LLVMValueRef[] captures)
        {
            LLVMValueRef control = LLVMValueRef.CreateConstPointerNull(ResumePointer);
            FunctionSymbol function = operation.Function!;
            if (captures.Length > 0)
            {
                ulong offset = (ulong)_getIntegerBitWidth(BuiltinTypes.NUInt) / 8;
                foreach (CaptureVariableSymbol capture in operation.Captures)
                {
                    uint alignment = Math.Max(1u, _getAbiAlignment(capture.StorageType));
                    offset = (offset + alignment - 1) / alignment * alignment;
                    offset += Math.Max(1UL, _getAbiSize(capture.StorageType));
                }
                LLVMValueRef environment = EmitAllocation(SizeConstant(Math.Max(1UL, offset)), "closure.environment");
                LLVMTypeRef environmentType = ClosureEnvironmentType(function);
                for (int i = 0; i < captures.Length; i++)
                    _builder.BuildStore(captures[i], _builder.BuildStructGEP2(environmentType, environment, (uint)i + 1, "capture.init"));
                control = EmitAllocation(SizeConstant((ulong)_getIntegerBitWidth(BuiltinTypes.NUInt) / 8 * 3), "closure.control");
                _builder.BuildStore(SizeConstant(1), FunctionControlField(control, 0, "closure.count"));
                _builder.BuildStore(environment, FunctionControlField(control, 1, "closure.environment"));
                _builder.BuildStore(_closureEnvironmentDestructors[function].Value, FunctionControlField(control, 2, "closure.destructor"));
                _builder.BuildStore(control, _builder.BuildStructGEP2(environmentType, environment, 0, "closure.owner"));
            }
            LLVMValueRef invoke = function.IsLambda ? _functions[function].Value : _getFunctionValueInvokeAddress(function);
            LLVMValueRef value = _builder.BuildInsertValue(_mapType(operation.ResultType).Poison, invoke, 0, "function.invoke");
            return _builder.BuildInsertValue(value, control, 1, "function.control");
        }

        private LLVMValueRef MirCompareExchange(LLVMValueRef address, AtomicTypeSymbol atomic, LLVMValueRef expected, LLVMValueRef desired)
        {
            if (IsLockBackedAtomic(atomic))
            {
                AcquireAtomicLock(address, atomic, "compare.exchange");
                LLVMValueRef payload = AtomicValueAddress(address, atomic);
                LLVMValueRef current = _builder.BuildLoad2(_mapType(atomic.ElementType), payload, "compare.exchange.current");
                LLVMValueRef succeeded = EmitValueEquality(current, expected, atomic.ElementType);
                _builder.BuildStore(_builder.BuildSelect(succeeded, desired, current, "compare.exchange.selected"), payload);
                ReleaseAtomicLock(address, atomic);
                return succeeded;
            }
            if (TypeIdentity.AreSame(atomic.ElementType, BuiltinTypes.Bool))
            {
                expected = _builder.BuildZExt(expected, _mapType(atomic), "expected.bool");
                desired = _builder.BuildZExt(desired, _mapType(atomic), "desired.bool");
            }
            else if (atomic.ElementType is PrimitiveTypeSymbol { IsFloatingPoint: true })
                return EmitFloatingCompareExchange(address, atomic.ElementType, expected, desired);
            return _atomics.CompareExchange(address, expected, desired, LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent, "compare.exchange").Succeeded;
        }
        private LLVMValueRef MirWeakLock(LLVMValueRef control)
        {

            LLVMValueRef nullControl = LLVMValueRef.CreateConstPointerNull(control.TypeOf);
            LLVMBasicBlockRef entry = _builder.InsertBlock;
            LLVMBasicBlockRef inspect = _llvmFunction.AppendBasicBlock("weak.lock.inspect");
            LLVMBasicBlockRef retain = _llvmFunction.AppendBasicBlock("weak.lock.retain");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("weak.lock.end");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, control, nullControl,
                "weak.lock.control.valid"), inspect, end);
            _builder.PositionAtEnd(inspect);
            LLVMValueRef strongAddress = OwnershipControlField(control, 0, "weak.lock.strong.address");
            LLVMValueRef strong = _atomics.Load(
                _mapType(BuiltinTypes.NUInt),
                strongAddress,
                LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic,
                "weak.lock.strong");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, strong, SizeConstant(0),
                "weak.lock.alive"), retain, end);
            _builder.PositionAtEnd(retain);
            LlvmCompareExchangeResult exchange = _atomics.CompareExchange(
                strongAddress,
                strong,
                _builder.BuildAdd(strong, SizeConstant(1), "weak.lock.next"),
                LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
                LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic,
                "weak.lock.exchange");
            LLVMBasicBlockRef retained = _llvmFunction.AppendBasicBlock("weak.lock.retained");
            _builder.BuildCondBr(exchange.Succeeded, retained, inspect);
            _builder.PositionAtEnd(retained);
            _builder.BuildBr(end);
            _builder.PositionAtEnd(end);
            LLVMValueRef result = _builder.BuildPhi(control.TypeOf, "weak.lock.result");
            result.AddIncoming([nullControl, nullControl, control], [entry, inspect, retained], 3);
            return result;
        }

        private LLVMValueRef MirSharedAdoption(LLVMValueRef storage)
        {

            ulong pointerBytes = checked((ulong)_getIntegerBitWidth(BuiltinTypes.NUInt) / 8);
            LLVMValueRef control = EmitAllocation(SizeConstant(checked(pointerBytes * 3)), "shared.control");
            _builder.BuildStore(SizeConstant(1), OwnershipControlField(control, 0, "shared.strong.address"));
            // One implicit weak reference is held while the strong count is non-zero.
            _builder.BuildStore(SizeConstant(1), OwnershipControlField(control, 1, "shared.weak.address"));
            _builder.BuildStore(storage, OwnershipControlField(control, 2, "shared.storage.address"));
            return control;
        }

        private LLVMValueRef MirArrayAllocation(MirIntrinsicCall expression)
        {
            LLVMValueRef[] dimensions = expression.Arguments.Select(dimension =>
            {
                LLVMValueRef value = MirValue(dimension);
                LLVMTypeRef sourceType = _mapType(dimension.Type);
                int width = _getIntegerBitWidth(dimension.Type);
                ulong max = width < 32 ? (1UL << width) - 1 : int.MaxValue;
                LLVMValueRef valid = _builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, value, LLVMValueRef.CreateConstInt(sourceType, max), "array.dimension.valid");
                if (dimension.Type is PrimitiveTypeSymbol { IsSigned: true })
                    valid = _builder.BuildAnd(valid, _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, value, LLVMValueRef.CreateConstInt(sourceType, 0)), "array.dimension.nonnegative");
                EmitRuntimeCheck(valid);
                return ConvertIntegerToSize(value, dimension.Type);
            }).ToArray();
            LLVMValueRef[] storedDimensions = dimensions.Select(ToInt32).ToArray();
            
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef hasZeroDimension = LLVMValueRef.CreateConstInt(_context.Int1Type, 0);
            foreach (LLVMValueRef dimension in dimensions)
                hasZeroDimension = _builder.BuildOr(hasZeroDimension, _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, dimension, SizeConstant(0)));
            LLVMValueRef length = _builder.BuildSelect(hasZeroDimension, SizeConstant(0), SizeConstant(1), "array.initial.length");
            foreach (LLVMValueRef dimension in dimensions)
            {
                LLVMValueRef divisor = _builder.BuildSelect(_builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, dimension, SizeConstant(0)), SizeConstant(1), dimension, "array.product.divisor");
                EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, length, _builder.BuildUDiv(SizeConstant(int.MaxValue), divisor), "array.product.valid"));
                length = _builder.BuildMul(length, dimension, "array.length");
            }
            ulong headerSize = ArrayHeaderSize(((ArrayTypeSymbol)expression.ResultType));
            ulong elementBytes = _getAbiSize(((ArrayTypeSymbol)expression.ResultType).ElementType);
            ulong maxSize = _getIntegerBitWidth(BuiltinTypes.NUInt) == 32 ? uint.MaxValue : ulong.MaxValue;
            if (elementBytes > 0)
                EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, length, SizeConstant((maxSize - headerSize) / elementBytes), "array.bytes.valid"));
            LLVMValueRef elementSize = SizeConstant(elementBytes);
            LLVMValueRef byteCount = _builder.BuildMul(length, elementSize, "array.bytes");
            LLVMValueRef allocationSize = _builder.BuildAdd(byteCount, SizeConstant(headerSize), "array.allocation.bytes");
            LLVMValueRef address;
            if (expression.Intrinsic == MirIntrinsicKind.AllocateStackArray)
            {
                if (_isResumable && expression.FixedArrayLength is { } fixedLength)
                    allocationSize = SizeConstant(checked(headerSize + fixedLength * elementBytes));
                address = _builder.BuildArrayAlloca(_context.Int8Type, allocationSize, $"{((ArrayTypeSymbol)expression.ResultType).ElementType.Name}.stack.array");
                address.Alignment = Math.Max(4, _getAbiAlignment(((ArrayTypeSymbol)expression.ResultType).ElementType));
            }
            else
            {
                address = EmitZeroedAllocation(allocationSize,
                    $"{((ArrayTypeSymbol)expression.ResultType).ElementType.Name}.heap.array",
                    SizeConstant(Math.Max(4u, _getAbiAlignment(((ArrayTypeSymbol)expression.ResultType).ElementType))));
            }
            _builder.BuildStore(ToInt32(length), address);
            for (int i = 0; i < dimensions.Length; i++)
                _builder.BuildStore(storedDimensions[i], MetadataAddress(address, IntConstant(i + 1)));
            LLVMValueRef data = ArrayData(address, ((ArrayTypeSymbol)expression.ResultType));
            if (expression.Intrinsic == MirIntrinsicKind.AllocateStackArray)
            {
                LLVMApi.BuildMemSet(
                    _builder,
                    data,
                    LLVMValueRef.CreateConstInt(_context.Int8Type, 0),
                    byteCount,
                    checked((uint)Math.Max(1, _getAbiAlignment(((ArrayTypeSymbol)expression.ResultType).ElementType))));
            }
            return address;
        }

    }
}
