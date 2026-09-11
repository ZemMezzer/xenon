using LLVMSharp.Interop;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

/// <summary>Authoritative physical-layout policy for Core atomic storage.</summary>
internal static class LlvmAtomicStorage
{
    public const uint LockFieldIndex = 0;
    public const uint ValueFieldIndex = 1;

    public static bool RequiresLock(TypeSymbol element) =>
        element is StructTypeSymbol or SharedTypeSymbol or WeakTypeSymbol;

    public static LLVMValueRef GetLockAddress(
        LLVMBuilderRef builder,
        LLVMTypeRef wrapperType,
        LLVMValueRef wrapperAddress,
        string name) =>
        builder.BuildStructGEP2(wrapperType, wrapperAddress, LockFieldIndex, name);

    public static LLVMValueRef GetValueAddress(
        LLVMBuilderRef builder,
        LLVMTypeRef wrapperType,
        LLVMValueRef wrapperAddress,
        AtomicTypeSymbol atomic,
        string name) =>
        RequiresLock(atomic.ElementType)
            ? builder.BuildStructGEP2(wrapperType, wrapperAddress, ValueFieldIndex, name)
            : wrapperAddress;
}
