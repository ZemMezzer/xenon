using LLVMSharp.Interop;
using Xenon.Compiler;
using Xenon.Compiler.Mir;
namespace Xenon.CodeGen.LLVM;
public sealed partial class LlvmIrGenerator
{
    private sealed unsafe partial class FunctionEmitter
    {
        private LLVMValueRef MirCreateCoroutine()
        {
            ReadOnlySpan<byte> name = "presplitcoroutine"u8;
            fixed (byte* text = name)
            {
                uint kind = LLVMSharp.Interop.LLVM.GetEnumAttributeKindForName((sbyte*)text, (nuint)name.Length);
                _llvmFunction.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex,
                    _context.CreateEnumAttribute(kind, 0));
            }
            LLVMValueRef nil = LLVMValueRef.CreateConstPointerNull(ResumePointer);
            _coroutineId = ResumeCall("llvm.coro.id", ResumeToken,
                [LLVMValueRef.CreateConstInt(_context.Int32Type, 0), nil, nil, nil], "resumable.id");
            LLVMValueRef size = ResumeCall($"llvm.coro.size.i{_getIntegerBitWidth(Xenon.Compiler.Semantics.Symbols.BuiltinTypes.NUInt)}",
                _mapType(Xenon.Compiler.Semantics.Symbols.BuiltinTypes.NUInt), [], "resumable.size");
            LLVMValueRef allocation = _builder.BuildCall2(_getMemoryRuntime().MallocType,
                _getMemoryRuntime().Malloc, new[] { size }, "resumable.storage");
            return ResumeCall("llvm.coro.begin", ResumePointer, [_coroutineId, allocation], "resumable.handle");
        }
        public void EmitMirResumable(MirFunction body)
        {
            if (body.Coroutine is null) throw new LlvmCodeGenerationException("Coroutine transformation is required before LLVM emission.");
            _mirReturnOverride = result =>
            {
                LLVMValueRef frameResult = EmitCopyValue(result, body.Symbol.ReturnType);
                LLVMValueRef state = ResumeCall(RuntimeAbiNames.ResumeCreate, ResumePointer, [], "resumable.control");
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
                    emitter.EmitMir(body);

                }
                LLVMValueRef handle = _builder.BuildCall2(rampType, ramp, inputs.Concat(new[] { frameResult, state }).ToArray(), "resumable.frame");
                ResumeCall(RuntimeAbiNames.ResumeStart, _context.VoidType, [state, handle]);
                _builder.BuildRet(result);
            };
            EmitMir(body.Resumable!.Initialization);
            _mirReturnOverride = null;
        }
    }
}
