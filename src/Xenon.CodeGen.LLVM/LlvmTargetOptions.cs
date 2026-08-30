using LLVMSharp.Interop;
using System.Runtime.InteropServices;
using LLVMApi = LLVMSharp.Interop.LLVM;

namespace Xenon.CodeGen.LLVM;

public sealed record LlvmTargetOptions(
    string Triple,
    int OptimizationLevel = 0,
    string Cpu = "",
    string Features = "",
    bool PositionIndependentCode = false)
{
    public static LlvmTargetOptions CreateHost(
        int optimizationLevel = 0,
        bool positionIndependentCode = false) =>
        new(
            LlvmTargetPlatform.HostTriple,
            optimizationLevel,
            PositionIndependentCode: positionIndependentCode);
}

public sealed record LlvmObjectFile(
    string Path,
    string TargetTriple,
    string DataLayout);

public static class LlvmTargetPlatform
{
    public static string HostTriple => NativeTargetMachine.GetHostTriple();

    public static string GetObjectFileExtension(string triple) =>
        triple.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
        triple.Contains("win32", StringComparison.OrdinalIgnoreCase)
            ? ".obj"
            : ".o";
}

internal sealed unsafe class NativeTargetMachine : IDisposable
{
    private static readonly object InitializationLock = new();
    private static bool _targetsInitialized;
    private bool _disposed;

    private NativeTargetMachine(
        LLVMTargetMachineRef handle,
        LLVMTargetDataRef targetData,
        string triple)
    {
        Handle = handle;
        TargetData = targetData;
        Triple = triple;
        DataLayout = GetDataLayoutString(targetData);
        PointerBitWidth = checked((int)(LLVMApi.PointerSizeForAS(targetData, 0) * 8u));
    }

    public LLVMTargetMachineRef Handle { get; }

    public LLVMTargetDataRef TargetData { get; }

    public string Triple { get; }

    public string DataLayout { get; }

    public int PointerBitWidth { get; }

    public static string GetHostTriple()
    {
        EnsureNativeTargetInitialized();
        return LLVMTargetRef.DefaultTriple;
    }

    public static NativeTargetMachine Create(LlvmTargetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Triple))
        {
            throw new LlvmCodeGenerationException("LLVM target triple cannot be empty.");
        }

        if (options.OptimizationLevel is < 0 or > 3)
        {
            throw new LlvmCodeGenerationException("LLVM optimization level must be between 0 and 3.");
        }

        EnsureNativeTargetInitialized();

        try
        {
            LLVMTargetRef target = LLVMTargetRef.GetTargetFromTriple(options.Triple);
            LLVMTargetMachineRef machine = target.CreateTargetMachine(
                options.Triple,
                options.Cpu,
                options.Features,
                MapOptimizationLevel(options.OptimizationLevel),
                options.PositionIndependentCode ? LLVMRelocMode.LLVMRelocPIC : LLVMRelocMode.LLVMRelocDefault,
                LLVMCodeModel.LLVMCodeModelDefault);

            if (machine.Handle == IntPtr.Zero)
            {
                throw new LlvmCodeGenerationException(
                    $"LLVM could not create a target machine for '{options.Triple}'.");
            }

            LLVMTargetDataRef targetData = machine.CreateTargetDataLayout();
            if (targetData.Handle == IntPtr.Zero)
            {
                LLVMApi.DisposeTargetMachine(machine);
                throw new LlvmCodeGenerationException(
                    $"LLVM could not create a data layout for '{options.Triple}'.");
            }

            try
            {
                return new NativeTargetMachine(machine, targetData, options.Triple);
            }
            catch
            {
                LLVMApi.DisposeTargetData(targetData);
                LLVMApi.DisposeTargetMachine(machine);
                throw;
            }
        }
        catch (LlvmCodeGenerationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LlvmCodeGenerationException(
                $"LLVM could not initialize target '{options.Triple}'.",
                exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // The data layout is owned by this wrapper and must not outlive its target machine.
        LLVMApi.DisposeTargetData(TargetData);
        LLVMApi.DisposeTargetMachine(Handle);
        _disposed = true;
    }

    private static void EnsureNativeTargetInitialized()
    {
        lock (InitializationLock)
        {
            if (_targetsInitialized)
            {
                return;
            }

            LLVMApi.InitializeAllTargetInfos();
            LLVMApi.InitializeAllTargets();
            LLVMApi.InitializeAllTargetMCs();
            LLVMApi.InitializeAllAsmPrinters();
            _targetsInitialized = true;
        }
    }

    private static LLVMCodeGenOptLevel MapOptimizationLevel(int level) => level switch
    {
        0 => LLVMCodeGenOptLevel.LLVMCodeGenLevelNone,
        1 => LLVMCodeGenOptLevel.LLVMCodeGenLevelLess,
        2 => LLVMCodeGenOptLevel.LLVMCodeGenLevelDefault,
        3 => LLVMCodeGenOptLevel.LLVMCodeGenLevelAggressive,
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    private static string GetDataLayoutString(LLVMTargetDataRef targetData)
    {
        sbyte* message = LLVMApi.CopyStringRepOfTargetData(targetData);
        if (message is null)
        {
            throw new LlvmCodeGenerationException("LLVM returned an empty target data layout.");
        }

        try
        {
            return Marshal.PtrToStringUTF8((IntPtr)message)
                ?? throw new LlvmCodeGenerationException("LLVM returned an invalid target data layout.");
        }
        finally
        {
            LLVMApi.DisposeMessage(message);
        }
    }
}
