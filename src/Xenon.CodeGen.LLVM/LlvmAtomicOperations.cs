using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;

namespace Xenon.CodeGen.LLVM;

/// <summary>
/// Centralizes the LLVM concurrency primitives used by Core lowering.
/// Source-level memory ordering is intentionally not exposed by Xenon.
/// </summary>
internal sealed unsafe class LlvmAtomicOperations(LLVMBuilderRef builder)
{
    public LLVMValueRef Load(
        LLVMTypeRef type,
        LLVMValueRef address,
        LLVMAtomicOrdering ordering,
        string name)
    {
        LLVMValueRef value = builder.BuildLoad2(type, address, name);
        LLVMApi.SetOrdering(value, ordering);
        return value;
    }

    public void Store(
        LLVMValueRef value,
        LLVMValueRef address,
        LLVMAtomicOrdering ordering)
    {
        LLVMValueRef store = builder.BuildStore(value, address);
        LLVMApi.SetOrdering(store, ordering);
    }

    public LLVMValueRef Fetch(
        LLVMAtomicRMWBinOp operation,
        LLVMValueRef address,
        LLVMValueRef value,
        LLVMAtomicOrdering ordering,
        string name) => AtomicRmw(operation, address, value, ordering, name);

    public LLVMValueRef Exchange(
        LLVMValueRef address,
        LLVMValueRef value,
        LLVMAtomicOrdering ordering,
        string name) => AtomicRmw(
            LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpXchg,
            address,
            value,
            ordering,
            name);

    public LLVMValueRef FetchAdd(
        LLVMValueRef address,
        LLVMValueRef value,
        LLVMAtomicOrdering ordering,
        string name) =>
        AtomicRmw(LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpAdd, address, value, ordering, name);

    public LLVMValueRef FetchSub(
        LLVMValueRef address,
        LLVMValueRef value,
        LLVMAtomicOrdering ordering,
        string name) =>
        AtomicRmw(LLVMAtomicRMWBinOp.LLVMAtomicRMWBinOpSub, address, value, ordering, name);

    public LlvmCompareExchangeResult CompareExchange(
        LLVMValueRef address,
        LLVMValueRef expected,
        LLVMValueRef desired,
        LLVMAtomicOrdering successOrdering,
        LLVMAtomicOrdering failureOrdering,
        string name)
    {
        LLVMValueRef result = LLVMApi.BuildAtomicCmpXchg(
            builder,
            address,
            expected,
            desired,
            successOrdering,
            failureOrdering,
            0);
        result.Name = name;
        return new LlvmCompareExchangeResult(
            builder.BuildExtractValue(result, 0, $"{name}.observed"),
            builder.BuildExtractValue(result, 1, $"{name}.succeeded"));
    }

    public LLVMValueRef TryAcquireLock(LLVMValueRef address, LLVMTypeRef lockType, string name) =>
        CompareExchange(
            address,
            LLVMValueRef.CreateConstInt(lockType, 0, false),
            LLVMValueRef.CreateConstInt(lockType, 1, false),
            LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
            LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic,
            name).Succeeded;

    public void ReleaseLock(LLVMValueRef address, LLVMTypeRef lockType) =>
        Store(
            LLVMValueRef.CreateConstInt(lockType, 0, false),
            address,
            LLVMAtomicOrdering.LLVMAtomicOrderingRelease);

    public void AcquireFence() =>
        builder.BuildFence(
            LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
            singleThread: false,
            string.Empty);

    private LLVMValueRef AtomicRmw(
        LLVMAtomicRMWBinOp operation,
        LLVMValueRef address,
        LLVMValueRef value,
        LLVMAtomicOrdering ordering,
        string name)
    {
        LLVMValueRef result = builder.BuildAtomicRMW(
            operation,
            address,
            value,
            ordering,
            singleThread: false);
        result.Name = name;
        return result;
    }
}

internal readonly record struct LlvmCompareExchangeResult(
    LLVMValueRef Observed,
    LLVMValueRef Succeeded);
