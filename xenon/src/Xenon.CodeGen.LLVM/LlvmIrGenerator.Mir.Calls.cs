using LLVMSharp.Interop;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
namespace Xenon.CodeGen.LLVM;
public sealed partial class LlvmIrGenerator
{
    private sealed unsafe partial class FunctionEmitter
    {
        private LLVMValueRef MirLifecycle(FunctionSymbol function, LLVMValueRef address, bool virtualDispatch = false)
        {
            if (virtualDispatch && function.VTableSlot is not null && function.ContainingStruct is { } type)
                return EmitInstanceAccessorCall(function, type, address, [], "");
            LlvmFunction target = _functions.TryGetValue(function, out LlvmFunction declared) ? declared :
                throw new LlvmCodeGenerationException($"MIR function {_function.FullName} references undeclared lifecycle/call {function.FullName} ({function.FunctionKind}).");
            return MirInvoke(target.Type, target.Value, [address], "");
        }

        private LLVMValueRef EmitMirCall(MirCall call)
        {
            LLVMValueRef[] arguments = call.Arguments.Select(MirValue).ToArray();
            if (call.Callee is MirFunctionOperand direct)
            {
                FunctionSymbol function = direct.Function;
                string name = TypeIdentity.AreSame(function.ReturnType, BuiltinTypes.Void) ? "" : "call";
                if (call.Receiver is { } receiver)
                {
                    LLVMValueRef address = MirValue(receiver);
                    if (call.InterfaceType is { } contract)
                    {
                        LLVMValueRef view = _builder.BuildLoad2(_mapType(contract), address, "interface");
                        return EmitInterfaceAccessorCall(contract, function, _builder.BuildExtractValue(view, 0, "interface.data"),
                            _builder.BuildExtractValue(view, 1, "interface.table"), arguments, name);
                    }
                    if (call.IsVirtual && function.ContainingStruct is { } owner)
                        return EmitInstanceAccessorCall(function, owner, address, arguments, name);
                    arguments = [address, .. arguments];
                }
                if (function.IsExtern && _cAbiFunctions.TryGetValue(function, out LlvmCAbiFunction abi))
                    return new LlvmCAbiMarshaller(_context, _builder).EmitCall(abi.Value, abi.Plan, arguments, name, MirInvoke);
                LlvmFunction target = _functions.TryGetValue(function, out LlvmFunction declared) ? declared :
                throw new LlvmCodeGenerationException($"MIR function {_function.FullName} references undeclared lifecycle/call {function.FullName} ({function.FunctionKind}).");
                return _functionEffects.TryGetValue(function, out LlvmFunctionEffects effects) && !effects.MayThrow
                    ? _builder.BuildCall2(target.Type, target.Value, arguments, name)
                    : MirInvoke(target.Type, target.Value, arguments, name);
            }
            LLVMValueRef callee = MirValue(call.Callee);
            if (call.Callee.Type is FunctionValueTypeSymbol callable)
                return MirCallable(callee, callable, arguments);
            FunctionPointerTypeSymbol pointer = (FunctionPointerTypeSymbol)call.Callee.Type;
            if (LlvmCAbi.RequiresLowering(pointer.ReturnType, pointer.ParameterTypes))
            {
                LlvmCAbiFunctionPlan plan = (_cAbi ?? throw new LlvmCodeGenerationException("Struct function pointer requires an LLVM target."))
                    .Classify(pointer.ReturnType, pointer.ParameterTypes);
                return new LlvmCAbiMarshaller(_context, _builder).EmitCall(callee, plan, arguments, "indirect.call", MirInvoke);
            }
            return MirInvoke(LLVMTypeRef.CreateFunction(_mapType(pointer.ReturnType), pointer.ParameterTypes.Select(_mapType).ToArray(), false),
                callee, arguments, TypeIdentity.AreSame(pointer.ReturnType, BuiltinTypes.Void) ? "" : "indirect.call");
        }

        private LLVMValueRef MirCallable(LLVMValueRef value, FunctionValueTypeSymbol callable, LLVMValueRef[] arguments)
        {
            LLVMValueRef invoke = _builder.BuildExtractValue(value, 0, "function.invoke");
            LLVMValueRef control = _builder.BuildExtractValue(value, 1, "function.control");
            if (_enableRuntimeChecks)
                EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, invoke,
                    LLVMValueRef.CreateConstPointerNull(invoke.TypeOf), "function.valid"));
            bool returnsVoid = TypeIdentity.AreSame(callable.ReturnType, BuiltinTypes.Void);
            LLVMValueRef resultAddress = returnsVoid ? default : _builder.BuildAlloca(_mapType(callable.ReturnType), "function.result");
            LLVMBasicBlockRef direct = _llvmFunction.AppendBasicBlock("function.direct");
            LLVMBasicBlockRef closure = _llvmFunction.AppendBasicBlock("function.closure");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("function.end");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, control,
                LLVMValueRef.CreateConstPointerNull(control.TypeOf)), direct, closure);
            _builder.PositionAtEnd(direct);
            LLVMValueRef directResult = MirInvoke(LLVMTypeRef.CreateFunction(_mapType(callable.ReturnType),
                callable.ParameterTypes.Select(_mapType).ToArray(), false), invoke, arguments, returnsVoid ? "" : "function.direct.result");
            if (!returnsVoid) _builder.BuildStore(directResult, resultAddress);
            _builder.BuildBr(end);
            _builder.PositionAtEnd(closure);
            LLVMValueRef environment = _builder.BuildLoad2(ResumePointer, FunctionControlField(control, 1, "environment.address"), "environment");
            LLVMValueRef closureResult = MirInvoke(LLVMTypeRef.CreateFunction(_mapType(callable.ReturnType),
                [ResumePointer, .. callable.ParameterTypes.Select(_mapType)], false), invoke, [environment, .. arguments], returnsVoid ? "" : "function.closure.result");
            if (!returnsVoid) _builder.BuildStore(closureResult, resultAddress);
            _builder.BuildBr(end);
            _builder.PositionAtEnd(end);
            return returnsVoid ? default : _builder.BuildLoad2(_mapType(callable.ReturnType), resultAddress, "function.result");
        }
    }
}
