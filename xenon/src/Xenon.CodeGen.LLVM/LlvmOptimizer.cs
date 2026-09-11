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
                return;

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
}
