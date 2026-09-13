using System.Runtime.InteropServices;
using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;

namespace Xenon.CodeGen.LLVM;

internal static unsafe class LlvmOptimizer
{
    private static readonly object RemarksInitializationLock = new();
    private static readonly LLVMDiagnosticHandler DiagnosticHandler = HandleDiagnostic;
    private static volatile bool _remarksEnabled;

    public static void Run(
        LLVMModuleRef module,
        NativeTargetMachine targetMachine,
        Action<string>? remarkSink = null,
        string? pipelineOverride = null)
    {
        ArgumentNullException.ThrowIfNull(targetMachine);
        if (targetMachine.OptimizationLevel == 0)
            return;

        if (remarkSink is not null)
            EnableOptimizationRemarks();

        delegate* unmanaged[Cdecl]<LLVMOpaqueDiagnosticInfo*, void*, void> previousHandler =
            LLVMApi.ContextGetDiagnosticHandler(module.Context);
        void* previousContext = LLVMApi.ContextGetDiagnosticContext(module.Context);
        string pipeline = pipelineOverride ?? $"default<O{targetMachine.OptimizationLevel}>";
        GCHandle diagnosticStateHandle = default;
        IntPtr pipelineText = IntPtr.Zero;
        LLVMPassBuilderOptionsRef options = default;
        try
        {
            if (remarkSink is not null || _remarksEnabled)
            {
                var diagnosticState = new DiagnosticState(
                    (nint)previousHandler,
                    (nint)previousContext,
                    remarkSink);
                diagnosticStateHandle = GCHandle.Alloc(diagnosticState);
                module.Context.SetDiagnosticHandler(
                    DiagnosticHandler,
                    (void*)GCHandle.ToIntPtr(diagnosticStateHandle));
            }

            pipelineText = Marshal.StringToCoTaskMemUTF8(pipeline);
            options = LLVMPassBuilderOptionsRef.Create();
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
            if (diagnosticStateHandle.IsAllocated)
            {
                module.Context.SetDiagnosticHandler(previousHandler, previousContext);
                diagnosticStateHandle.Free();
            }
            if (options.Handle != IntPtr.Zero)
                options.Dispose();
            if (pipelineText != IntPtr.Zero)
                Marshal.FreeCoTaskMem(pipelineText);
        }
    }

    private static void EnableOptimizationRemarks()
    {
        // LLVM 20's C API exposes no context/module/pass-manager scoped remark
        // configuration. ParseCommandLineOptions mutates process-global LLVM state,
        // so initialize it once and scope collection/filtering with the context's
        // diagnostic handler in Run.
        lock (RemarksInitializationLock)
        {
            if (_remarksEnabled) return;
            string[] arguments =
            {
                "xenon-llvm",
                "-pass-remarks=.*",
                "-pass-remarks-missed=.*",
                "-pass-remarks-analysis=.*",
            };
            nint[] storage = new nint[arguments.Length];
            try
            {
                for (int index = 0; index < arguments.Length; index++)
                    storage[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                sbyte** pointers = stackalloc sbyte*[storage.Length];
                for (int index = 0; index < storage.Length; index++)
                    pointers[index] = (sbyte*)storage[index];
                LLVMApi.ParseCommandLineOptions(storage.Length, pointers, null);
                _remarksEnabled = true;
            }
            finally
            {
                foreach (nint pointer in storage)
                    if (pointer != 0)
                        Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    private static void HandleDiagnostic(LLVMOpaqueDiagnosticInfo* info, void* context)
    {
        if (context is null) return;
        if (GCHandle.FromIntPtr((nint)context).Target is not DiagnosticState state)
            return;

        if (LLVMApi.GetDiagInfoSeverity(info) != LLVMDiagnosticSeverity.LLVMDSRemark)
        {
            if (state.PreviousHandler != 0)
            {
                try
                {
                    ((delegate* unmanaged[Cdecl]<LLVMOpaqueDiagnosticInfo*, void*, void>)state.PreviousHandler)(
                        info,
                        (void*)state.PreviousContext);
                }
                catch
                {
                    // A managed handler supplied by a host must not unwind through LLVM.
                }
                return;
            }

            WriteDiagnosticToStandardError(info);
            return;
        }

        if (state.RemarkSink is null)
            return;

        sbyte* message = LLVMApi.GetDiagInfoDescription(info);
        if (message is null) return;
        try
        {
            if (Marshal.PtrToStringUTF8((nint)message) is { } text)
                try { state.RemarkSink(text); }
                catch
                {
                    // Managed exceptions must never cross LLVM's unmanaged callback boundary.
                    // Diagnostics are observational and cannot invalidate generated code.
                }
        }
        finally
        {
            LLVMApi.DisposeMessage(message);
        }
    }

    private static void WriteDiagnosticToStandardError(LLVMOpaqueDiagnosticInfo* info)
    {
        sbyte* message = LLVMApi.GetDiagInfoDescription(info);
        if (message is null) return;
        try
        {
            if (Marshal.PtrToStringUTF8((nint)message) is { } text)
                try { Console.Error.WriteLine(text); }
                catch
                {
                    // Diagnostics must not make optimization fail merely because stderr
                    // is unavailable (for example, in a hosted compiler process).
                }
        }
        finally
        {
            LLVMApi.DisposeMessage(message);
        }
    }

    private sealed record DiagnosticState(
        nint PreviousHandler,
        nint PreviousContext,
        Action<string>? RemarkSink);

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
