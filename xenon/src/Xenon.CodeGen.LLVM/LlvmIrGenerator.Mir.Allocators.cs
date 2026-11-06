using LLVMSharp.Interop;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

public sealed partial class LlvmIrGenerator
{
    private sealed unsafe partial class FunctionEmitter
    {
        private LLVMValueRef EmitCustomAllocation(MirIntrinsicCall operation, LLVMValueRef allocator, LLVMValueRef size, uint requestedAlignment)
        {
            FunctionSymbol allocate = operation.Function!;
            StructTypeSymbol provider = (StructTypeSymbol)((PointerTypeSymbol)operation.Arguments[^1].Type).ElementType;
            FunctionSymbol deallocate = provider.DeallocationOperator!;
            LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef sizeType = _getMemoryRuntime().SizeType;
            ulong word = (ulong)_getIntegerBitWidth(BuiltinTypes.NUInt) / 8;
            ulong alignment = Math.Max(word, requestedAlignment);
            ulong overhead = word * 3 + alignment - 1;
            ulong maximum = word == 4 ? uint.MaxValue : ulong.MaxValue;
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, allocator, LLVMValueRef.CreateConstPointerNull(pointer), "allocator.nonnull"));
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, size, SizeConstant(maximum - overhead), "allocator.size.valid"));
            // Capture the matching dynamic release operation before entering user code.
            LLVMValueRef release = ResolveAllocationOperation(deallocate, allocator);
            LLVMValueRef raw = EmitInstanceAccessorCall(allocate, allocate.ContainingStruct!, allocator,
                [_builder.BuildAdd(size, SizeConstant(overhead)), SizeConstant(alignment)], "allocator.block");
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, raw, LLVMValueRef.CreateConstPointerNull(pointer), "allocator.succeeded"));
            LLVMValueRef rounded = _builder.BuildAdd(_builder.BuildPtrToInt(raw, sizeType), SizeConstant(overhead));
            LLVMValueRef aligned = _builder.BuildAnd(rounded, SizeConstant(~(alignment - 1)), "allocator.aligned");
            LLVMValueRef result = _builder.BuildIntToPtr(aligned, pointer, "allocator.object");
            void Header(ulong index, LLVMValueRef value) => _builder.BuildStore(value,
                _builder.BuildIntToPtr(_builder.BuildSub(aligned, SizeConstant(index * word)), pointer));
            Header(1, raw);
            Header(2, allocator);
            Header(3, release);
            return result;
        }

        private LLVMValueRef ResolveAllocationOperation(FunctionSymbol operation, LLVMValueRef receiver)
        {
            if (operation.VTableSlot is not int slot) return _functions[operation].Value;
            StructTypeSymbol owner = operation.ContainingStruct!;
            LlvmVTable table = _virtualTables[owner];
            LLVMValueRef address = _builder.BuildGEP2(table.Type, EmitRuntimeDispatch(owner, receiver),
                new[] { IntConstant(0), IntConstant(slot + 1) }, "allocator.release.slot");
            return _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), address, "allocator.release");
        }
    }
}
