using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;

namespace Xenon.CodeGen.LLVM;

internal static unsafe class LlvmOptimizer
{
    public static void Run(LLVMModuleRef module, NativeTargetMachine targetMachine)
    {
        ArgumentNullException.ThrowIfNull(targetMachine);
        if (targetMachine.OptimizationLevel == 0)
            return;

        string pipeline = $"default<O{targetMachine.OptimizationLevel}>";
        IntPtr pipelineText = Marshal.StringToCoTaskMemUTF8(pipeline);
        LLVMPassBuilderOptionsRef options = LLVMPassBuilderOptionsRef.Create();
        try
        {
            LLVMErrorRef error = LLVMApi.RunPasses(
                module,
                (sbyte*)pipelineText,
                targetMachine.Handle,
                options);
            if (error == default)
            {
                ImproveSwitchLoopLayout(module);
                return;
            }

            sbyte* message = LLVMApi.GetErrorMessage(error);
            try
            {
                string detail = Marshal.PtrToStringUTF8((IntPtr)message) ?? "unknown LLVM pass error";
                throw new LlvmCodeGenerationException(
                    $"LLVM optimization pipeline '{pipeline}' failed: {detail}");
            }
            finally
            {
                LLVMApi.DisposeErrorMessage(message);
            }
        }
        finally
        {
            options.Dispose();
            Marshal.FreeCoTaskMem(pipelineText);
        }
    }

    private static void ImproveSwitchLoopLayout(LLVMModuleRef module)
    {
        for (LLVMValueRef function = module.FirstFunction;
             function != default;
             function = function.NextFunction)
        {
            LLVMBasicBlockRef[] blocks = function.GetBasicBlocks();
            if (blocks.Length < 4)
                continue;

            LLVMValueRef entryTerminator = blocks[0].LastInstruction;
            if (entryTerminator == default ||
                entryTerminator.InstructionOpcode != LLVMOpcode.LLVMBr ||
                entryTerminator.SuccessorsCount != 1)
                continue;

            LLVMBasicBlockRef loopHeader = entryTerminator.GetSuccessor(0);
            bool hasSwitch = false;
            bool hasBackEdge = false;
            LLVMBasicBlockRef returnBlock = default;
            int returnCount = 0;

            for (int blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
            {
                LLVMValueRef terminator = blocks[blockIndex].LastInstruction;
                if (terminator == default)
                    continue;

                if (terminator.InstructionOpcode == LLVMOpcode.LLVMSwitch)
                    hasSwitch = true;
                if (terminator.InstructionOpcode == LLVMOpcode.LLVMRet)
                {
                    returnBlock = blocks[blockIndex];
                    returnCount++;
                }

                if (blockIndex == 0)
                    continue;
                for (uint successorIndex = 0;
                     successorIndex < terminator.SuccessorsCount;
                     successorIndex++)
                    if (terminator.GetSuccessor(successorIndex).Equals(loopHeader))
                        hasBackEdge = true;
            }

            if (hasSwitch && hasBackEdge && returnCount == 1 &&
                !returnBlock.Equals(blocks[0]))
                returnBlock.MoveAfter(blocks[0]);
        }
    }
}
