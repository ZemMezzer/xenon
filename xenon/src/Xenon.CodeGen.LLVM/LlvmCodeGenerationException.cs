namespace Xenon.CodeGen.LLVM;

public sealed class LlvmCodeGenerationException : Exception
{
    public LlvmCodeGenerationException(string message)
        : base(message)
    {
    }

    public LlvmCodeGenerationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
