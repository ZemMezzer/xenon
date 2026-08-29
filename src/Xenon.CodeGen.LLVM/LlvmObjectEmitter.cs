using LLVMSharp.Interop;
using Xenon.Compiler;

namespace Xenon.CodeGen.LLVM;

public sealed class LlvmObjectEmitter
{
    public LlvmObjectFile Emit(
        Compilation compilation,
        string outputPath,
        LlvmTargetOptions targetOptions,
        string moduleName = "xenon",
        LlvmCodeGenerationOptions? codeGenerationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(targetOptions);

        string fullOutputPath = Path.GetFullPath(outputPath);
        string? outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (outputDirectory is not null)
        {
            Directory.CreateDirectory(outputDirectory);
        }

        string temporaryPath = Path.Combine(
            outputDirectory ?? Directory.GetCurrentDirectory(),
            $".{Path.GetFileName(fullOutputPath)}.{Guid.NewGuid():N}.tmp");
        using NativeTargetMachine targetMachine = NativeTargetMachine.Create(targetOptions);
        try
        {
            var generator = new LlvmIrGenerator();
            generator.GenerateModule(
                compilation,
                moduleName,
                targetMachine,
                codeGenerationOptions,
                module =>
                {
                    targetMachine.Handle.EmitToFile(
                        module,
                        temporaryPath,
                        LLVMCodeGenFileType.LLVMObjectFile);
                    return true;
                });

            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                throw new LlvmCodeGenerationException(
                    $"LLVM did not produce the expected object file '{fullOutputPath}'.");
            }

            File.Move(temporaryPath, fullOutputPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new LlvmObjectFile(
            fullOutputPath,
            targetMachine.Triple,
            targetMachine.DataLayout);
    }
}
