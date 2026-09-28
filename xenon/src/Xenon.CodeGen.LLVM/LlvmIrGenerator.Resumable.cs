using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

public sealed partial class LlvmIrGenerator
{
    private sealed unsafe partial class FunctionEmitter
    {
        private bool _isResumable;
        private ResumableFrameAnalysis? _resumableFrame;
        private DeferredCompletion? _deferredCompletion;
        private sealed class DeferredCompletion(BoundCallExpression call)
        {
            public BoundCallExpression Call { get; } = call;
            public LLVMValueRef[] Arguments { get; set; } = [];
            public List<LifecycleValueGuard> Guards { get; } = [];
        }
        private LocalVariableSymbol? _resumableReturnVariable;
        private LLVMValueRef _coroutineId;
        private LLVMValueRef _coroutineHandle;
        private LLVMBasicBlockRef _coroutineSuspend;
        private LLVMBasicBlockRef _coroutineFree;
        private LLVMBasicBlockRef _coroutineComplete;
        private LLVMTypeRef ResumePointer => LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        private LLVMTypeRef ResumeToken => new((nint)LLVMApi.TokenTypeInContext(_context));

        private LLVMValueRef ResumeCall(string name, LLVMTypeRef result, LLVMValueRef[] arguments, string valueName = "")
        {
            LLVMTypeRef signature = LLVMTypeRef.CreateFunction(result, arguments.Select(argument => argument.TypeOf).ToArray(), false);
            return _builder.BuildCall2(signature, GetOrAddFunction(name, signature), arguments, valueName);
        }

        public void EmitResumableWrapper(BoundBlockStatement body)
        {
            ResumableFrameAnalysis frame = ResumableFrameAnalysis.Analyze(_function, body, new TypeFactory());
            if (TypeFacts.CanRelocate(frame.FrameType))
                throw new LlvmCodeGenerationException("Resumable storage must obey pin relocation rules.");
            BoundVariableDeclarationStatement resultDeclaration = (BoundVariableDeclarationStatement)body.Statements[0];
            LLVMValueRef result = EmitExpression(resultDeclaration.Initializer!);
            LLVMValueRef frameResult = EmitCopyValue(result, _function.ReturnType);
            LLVMValueRef state = ResumeCall("__xenon_resume_create", ResumePointer, [], "resumable.control");
            LLVMValueRef[] inputs = Enumerable.Range(0, checked((int)_llvmFunction.ParamsCount))
                .Select(index => _llvmFunction.GetParam((uint)index)).ToArray();
            if (_function.IsCapturingLambda)
                EmitFunctionControlRetain(_builder.BuildLoad2(ResumePointer, _closureEnvironment, "resumable.capture.owner"));
            LLVMTypeRef rampType = LLVMTypeRef.CreateFunction(ResumePointer,
                inputs.Select(input => input.TypeOf).Concat([frameResult.TypeOf, ResumePointer]).ToArray(), false);
            LLVMValueRef ramp = _llvmFunction.GlobalParent.AddFunction(_llvmFunction.Name + ".resumable", rampType);
            ramp.Linkage = LLVMLinkage.LLVMInternalLinkage;
            using (LLVMBuilderRef builder = _context.CreateBuilder())
            {
                builder.PositionAtEnd(ramp.AppendBasicBlock("entry"));
                var emitter = new FunctionEmitter(_context, builder, _function, ramp, _functions, _functionEffects,
                    _cAbiFunctions, _cAbi, _staticFields, _threadLocalEnsures, _virtualTables, _interfaceMaps,
                    _interfaceKeys, _closureEnvironmentDestructors, _getFunctionValueInvokeAddress,
                    _interfaceMapEntryType, _mapType, _getMemoryRuntime, _getExceptionRuntime,
                    _getExceptionTypeKey, _getExceptionTypeDisplayName, _getTrap, _getStringCompare,
                    _getAbiSize, _getAbiAlignment, _getFieldOffset, _getIntegerBitWidth,
                    _enableRuntimeChecks, _isWindowsTarget, _exceptionsEnabled, resumable: true);
                emitter._resumableFrame = frame;
                emitter.Emit(body);
                emitter.EndResumableBody();
            }
            LLVMValueRef handle = _builder.BuildCall2(rampType, ramp, inputs.Concat(new[] { frameResult, state }).ToArray(), "resumable.frame");
            ResumeCall("__xenon_resume_start", _context.VoidType, [state, handle]);
            _builder.BuildRet(result);
            _terminated = true;
        }

        private void BeginResumableBody()
        {
            _isResumable = true;
            ReadOnlySpan<byte> name = "presplitcoroutine"u8;
            fixed (byte* text = name)
            {
                uint kind = LLVMApi.GetEnumAttributeKindForName((sbyte*)text, (nuint)name.Length);
                _llvmFunction.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex,
                    _context.CreateEnumAttribute(kind, 0));
            }
            LLVMValueRef nil = LLVMValueRef.CreateConstPointerNull(ResumePointer);
            _coroutineId = ResumeCall("llvm.coro.id", ResumeToken,
                [LLVMValueRef.CreateConstInt(_context.Int32Type, 0), nil, nil, nil], "resumable.id");
            LLVMTypeRef sizeType = _mapType(BuiltinTypes.NUInt);
            LLVMValueRef size = ResumeCall($"llvm.coro.size.i{_getIntegerBitWidth(BuiltinTypes.NUInt)}", sizeType, [], "resumable.size");
            LLVMValueRef allocation = _builder.BuildCall2(_getMemoryRuntime().MallocType,
                _getMemoryRuntime().Malloc, new[] { size }, "resumable.storage");
            _coroutineHandle = ResumeCall("llvm.coro.begin", ResumePointer, [_coroutineId, allocation], "resumable.handle");
            _coroutineSuspend = _llvmFunction.AppendBasicBlock("resumable.suspend");
            _coroutineFree = _llvmFunction.AppendBasicBlock("resumable.free");
            _coroutineComplete = _llvmFunction.AppendBasicBlock("resumable.complete");
        }

        private void EndResumableBody()
        {
            _builder.PositionAtEnd(_coroutineComplete);
            LLVMBasicBlockRef unreachable = _llvmFunction.AppendBasicBlock("resumable.completed");
            EmitResumableSuspend(unreachable, terminal: true);
            _builder.PositionAtEnd(unreachable);
            _builder.BuildUnreachable();
            _builder.PositionAtEnd(_coroutineFree);
            LLVMValueRef memory = ResumeCall("llvm.coro.free", ResumePointer, [_coroutineId, _coroutineHandle], "resumable.release");
            EmitDeallocation(memory);
            _builder.BuildBr(_coroutineSuspend);
            _builder.PositionAtEnd(_coroutineSuspend);
            ResumeCall("llvm.coro.end", _context.Int1Type,
                [_coroutineHandle, LLVMValueRef.CreateConstInt(_context.Int1Type, 0), LLVMValueRef.CreateConstNull(ResumeToken)], "resumable.end");
            _builder.BuildRet(_coroutineHandle);
        }

        private void EmitResumableSuspend(LLVMBasicBlockRef resume, bool terminal)
        {
            LLVMValueRef suspended = ResumeCall("llvm.coro.suspend", _context.Int8Type,
                [LLVMValueRef.CreateConstNull(ResumeToken), LLVMValueRef.CreateConstInt(_context.Int1Type, terminal ? 1UL : 0UL)],
                "resumable.suspend.state");
            LLVMValueRef selection = _builder.BuildSwitch(suspended, _coroutineSuspend, 2);
            selection.AddCase(LLVMValueRef.CreateConstInt(_context.Int8Type, 0), resume);
            selection.AddCase(LLVMValueRef.CreateConstInt(_context.Int8Type, 1), _coroutineFree);
        }

        private void EmitResumableReturn(BoundReturnStatement statement)
        {
            BoundExpression? value = statement.Expression is BoundFullExpression full ? full.Expression : statement.Expression;
            if (value is not BoundCallExpression call)
                throw new LlvmCodeGenerationException("A resumable return must have a bound completion operator.");
            var completion = new DeferredCompletion(call);
            DeferredCompletion? previous = _deferredCompletion;
            _deferredCompletion = completion;
            try
            {
                EmitExpression(statement.Expression!);
                // A return in finally replaces the pending completion value just as
                // it replaces an ordinary pending return, including its ownership.
                if (previous is not null)
                    foreach (LifecycleValueGuard guard in previous.Guards)
                        DestroyAndCompleteLifecycleValueGuard(guard);
                bool rejecting = call.Function.OperatorKind == OperatorKind.Reject;
                // Preserve the return argument while user cleanup/finally runs. If cleanup
                // throws, existing unwind guards destroy it and the outer handler rejects.
                EmitControlTransferCleanup(0, 0, 0, releaseCatches: !rejecting);
                if (_terminated) return;
                CompleteLifecycleValueGuards(completion.Guards);
                LLVMBasicBlockRef failure = _llvmFunction.AppendBasicBlock("completion.failed");
                _exceptionTargets.Push(new ExceptionTarget(failure, _cleanupScopes.Count,
                    _finalizerScopes.Count, _activeCatchScopes.Count));
                EmitPotentiallyThrowingCall(call.Function, _functions[call.Function], completion.Arguments, string.Empty);
                if (rejecting) ReleaseCatchScopes(0, _ => true, []);
                if (TypeFacts.GetCompleteDestructor(_function.ReturnType) is { } destructor)
                    EmitLifecycleCall(destructor, GetAddress(_resumableReturnVariable!), []);
                if (_function.IsCapturingLambda)
                    EmitFunctionControlRelease(_builder.BuildLoad2(ResumePointer, _closureEnvironment, "resumable.capture.owner"));
                _exceptionTargets.Pop();
                _builder.BuildBr(_coroutineComplete);
                _builder.PositionAtEnd(failure);
                LlvmFunction terminate = _getExceptionRuntime().Terminate;
                _builder.BuildCall2(terminate.Type, terminate.Value, Array.Empty<LLVMValueRef>(), string.Empty);
                _builder.BuildUnreachable();
                _terminated = true;
            }
            finally
            {
                foreach (LifecycleValueGuard guard in completion.Guards) _lifecycleValueGuards.Remove(guard);
                _deferredCompletion = previous;
            }
        }

        private LLVMValueRef DeferCompletionArguments(BoundCallExpression call)
        {
            DeferredCompletion completion = _deferredCompletion!;
            completion.Arguments = EmitTransferredArguments(call.Arguments);
            for (int index = 0; index < call.Arguments.Length; index++)
                if (TypeFacts.GetCompleteDestructor(call.Arguments[index].Type) is { } destructor)
                    completion.Guards.Add(BeginLifecycleValueGuard(completion.Arguments[index],
                        call.Arguments[index].Type, destructor, "completion.argument"));
            return default;
        }

        private LLVMValueRef EmitAwait(BoundAwaitExpression expression)
        {
            LLVMValueRef operandAddress;
            if (IsAddressable(expression.Operand) ||
                _fullExpressionTemporaries?.ContainsKey(expression.Operand) == true)
                operandAddress = EmitAddress(expression.Operand);
            else
            {
                LLVMValueRef operand = EmitExpression(expression.Operand);
                operandAddress = _builder.BuildAlloca(operand.TypeOf, "await.operand");
                _builder.BuildStore(operand, operandAddress);
            }
            LLVMValueRef storageAddress = default;
            LifecycleValueGuard? storageGuard = null;
            StorageTypeSymbol? storageType = expression.ResultStorage?.Type as StorageTypeSymbol;
            if (expression.ResultStorage is { } storage)
            {
                LLVMValueRef empty = DefaultValue(storage.Type, _mapType, _virtualTables);
                if (TypeFacts.GetCompleteDestructor(storage.Type) is { } storageDestructor)
                {
                    storageGuard = BeginLifecycleValueGuard(empty, storage.Type, storageDestructor, "await.result.storage");
                    storageAddress = storageGuard.Address;
                }
                else
                {
                    storageAddress = _builder.BuildAlloca(_mapType(storage.Type), "await.result.storage");
                    _builder.BuildStore(empty, storageAddress);
                }
                _addresses[storage] = storageAddress;
            }
            LLVMBasicBlockRef retry = _llvmFunction.AppendBasicBlock("await.retry");
            LLVMBasicBlockRef ready = _llvmFunction.AppendBasicBlock("await.ready");
            LLVMBasicBlockRef pending = _llvmFunction.AppendBasicBlock("await.pending");
            _builder.BuildBr(retry);
            _builder.PositionAtEnd(retry);
            LLVMValueRef continuationAddress = _builder.BuildAlloca(_mapType(expression.Continuation.Type), "await.continuation");
            _addresses[expression.Continuation] = continuationAddress;

            LLVMValueRef continuation = ResumeCall("__xenon_resume_continuation", _mapType(expression.Continuation.Type),
                [_llvmFunction.GetParam(_llvmFunction.ParamsCount - 1)], "await.resume");
            _builder.BuildStore(continuation, continuationAddress);
            FunctionSymbol continuationDestructor = TypeFacts.GetCompleteDestructor(expression.Continuation.Type)!;
            LifecycleValueGuard guard = BeginLifecycleValueGuard(continuation, expression.Continuation.Type,
                continuationDestructor, "await.continuation");
            _capturedPlaces.Push(operandAddress);
            LLVMValueRef complete;
            try { complete = EmitExpression(expression.Operation); }
            finally { _capturedPlaces.Pop(); }
            DestroyAndCompleteLifecycleValueGuard(guard);
            _builder.BuildCondBr(complete, ready, pending);
            _builder.PositionAtEnd(pending);
            if (storageType is not null) EmitStorageStateCheck(storageAddress, storageType, expectedInitialized: false);
            EmitResumableSuspend(retry, terminal: false);
            _builder.PositionAtEnd(ready);
            if (storageType is null) return default;
            LLVMValueRef result = EmitStorageMove(new BoundStorageMoveExpression(new BoundVariableExpression(expression.ResultStorage!), storageType));
            if (storageGuard is not null) CompleteLifecycleValueGuard(storageGuard);
            return result;
        }
    }
}
