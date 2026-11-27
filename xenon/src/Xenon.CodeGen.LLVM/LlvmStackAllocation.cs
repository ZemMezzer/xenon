using LLVMSharp.Interop;

namespace Xenon.CodeGen.LLVM;

internal static class LlvmStackAllocation
{
    // Compiler scratch slots have function lifetime. Allocating at the use site
    // consumes another stack slot on every loop iteration at optimization level 0.
    internal static LLVMValueRef BuildEntryAlloca(this LLVMBuilderRef builder, LLVMTypeRef type, string name)
    {
        LLVMBasicBlockRef entry = builder.InsertBlock.Parent.EntryBasicBlock;
        using LLVMBuilderRef allocationBuilder = type.Context.CreateBuilder();
        LLVMValueRef first = entry.FirstInstruction;
        if (first.Handle == IntPtr.Zero)
            allocationBuilder.PositionAtEnd(entry);
        else
            allocationBuilder.PositionBefore(first);
        return allocationBuilder.BuildAlloca(type, name);
    }
}
