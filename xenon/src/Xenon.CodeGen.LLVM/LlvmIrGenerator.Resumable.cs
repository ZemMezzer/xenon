using Xenon.Compiler;
using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

public sealed partial class LlvmIrGenerator
{
    private sealed unsafe partial class FunctionEmitter
    {
        private bool _isResumable;
        private LLVMValueRef _coroutineId;
        private LLVMTypeRef ResumePointer => LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        private LLVMTypeRef ResumeToken => new((nint)LLVMApi.TokenTypeInContext(_context));

        private LLVMValueRef ResumeCall(string name, LLVMTypeRef result, LLVMValueRef[] arguments, string valueName = "")
        {
            LLVMTypeRef signature = LLVMTypeRef.CreateFunction(result, arguments.Select(argument => argument.TypeOf).ToArray(), false);
            return _builder.BuildCall2(signature, GetOrAddFunction(name, signature), arguments, valueName);
        }

        private LLVMValueRef MirAsyncRootContinuation(LLVMValueRef root, TypeSymbol callbackTypeSymbol)
        {
            // function void() uses the ordinary ref-counted function control ABI.
            LLVMTypeRef word = _mapType(BuiltinTypes.NUInt);
            LLVMTypeRef controlType = _context.GetStructType([word, ResumePointer, ResumePointer], false);
            LLVMValueRef control = _builder.BuildCall2(_getMemoryRuntime().MallocType, _getMemoryRuntime().Malloc,
                new[] { LLVMValueRef.CreateConstInt(word, (ulong)(3 * (_getIntegerBitWidth(BuiltinTypes.NUInt) / 8))) }, "root.continuation.control");
            _builder.BuildStore(LLVMValueRef.CreateConstInt(word, 1), _builder.BuildStructGEP2(controlType, control, 0, "root.refs"));
            _builder.BuildStore(root, _builder.BuildStructGEP2(controlType, control, 1, "root.environment"));
            LLVMTypeRef callbackType = LLVMTypeRef.CreateFunction(_context.VoidType, [ResumePointer], false);
            _builder.BuildStore(GetOrAddFunction(RuntimeAbiNames.AsyncRootRelease, callbackType),
                _builder.BuildStructGEP2(controlType, control, 2, "root.drop"));
            ResumeCall(RuntimeAbiNames.AsyncRootRetain, _context.VoidType, [root]);
            LLVMValueRef continuation = LLVMValueRef.CreateConstNull(_mapType(callbackTypeSymbol));
            continuation = _builder.BuildInsertValue(continuation, GetOrAddFunction(RuntimeAbiNames.AsyncRootNotify, callbackType), 0, "root.notify");
            continuation = _builder.BuildInsertValue(continuation, control, 1, "root.continuation");
            return continuation;
        }
    }
}
