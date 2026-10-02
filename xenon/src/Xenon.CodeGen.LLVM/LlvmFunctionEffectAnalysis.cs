using System.Collections.Immutable;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

/// <summary>
/// Conservative, whole-module facts used solely to make LLVM IR more precise.
/// A false value is emitted only when the complete MIR implementation proves it.
/// </summary>
internal readonly record struct LlvmFunctionEffects(
    bool MayThrow,
    bool MayAccessMemory,
    bool MayAllocate,
    bool MayFree,
    bool InlineCandidate);

internal static class LlvmFunctionEffectAnalysis
{
    private sealed class LocalEffects
    {
        public bool MayThrow;
        public bool MayAccessMemory;
        public bool MayAllocate;
        public bool MayFree;
        public bool HasLoopOrExceptionRegion;
        public int StructuralCost;
        public HashSet<FunctionSymbol> Calls { get; } = new(ReferenceEqualityComparer.Instance);
    }

    public static IReadOnlyDictionary<FunctionSymbol, LlvmFunctionEffects> Analyze(
        ImmutableArray<MirFunction> functions)
    {
        var bodies = new Dictionary<FunctionSymbol, MirFunction>(ReferenceEqualityComparer.Instance);
        foreach (MirFunction function in functions)
            bodies.Add(function.Symbol, function);
        var threadLocalInitializers = new Dictionary<FieldSymbol, FunctionSymbol>(ReferenceEqualityComparer.Instance);
        foreach (MirFunction function in functions)
        {
            if (function.Symbol is { FunctionKind: FunctionKind.ThreadLocalInitializer, ThreadLocalField: { } field })
                threadLocalInitializers[field] = function.Symbol;
        }
        var locals = new Dictionary<FunctionSymbol, LocalEffects>(ReferenceEqualityComparer.Instance);

        foreach (MirFunction function in functions)
            locals.Add(function.Symbol, AnalyzeLocal(function, bodies, threadLocalInitializers));

        // Start optimistically inside the closed set and monotonically propagate every
        // adverse effect. This computes recursive SCCs without treating harmless recursion
        // as an unknown call.
        var effects = new Dictionary<FunctionSymbol, LlvmFunctionEffects>(ReferenceEqualityComparer.Instance);
        foreach ((FunctionSymbol symbol, LocalEffects local) in locals)
        {
            effects.Add(symbol, new LlvmFunctionEffects(
                local.MayThrow,
                local.MayAccessMemory,
                local.MayAllocate,
                local.MayFree,
                InlineCandidate: false));
        }

        bool changed;
        do
        {
            changed = false;
            foreach ((FunctionSymbol function, LocalEffects local) in locals)
            {
                LlvmFunctionEffects previous = effects[function];
                bool mayThrow = local.MayThrow;
                bool mayAccessMemory = local.MayAccessMemory;
                bool mayAllocate = local.MayAllocate;
                bool mayFree = local.MayFree;
                foreach (FunctionSymbol callee in local.Calls)
                {
                    LlvmFunctionEffects calleeEffects = effects[callee];
                    mayThrow |= calleeEffects.MayThrow;
                    mayAccessMemory |= calleeEffects.MayAccessMemory;
                    mayAllocate |= calleeEffects.MayAllocate;
                    mayFree |= calleeEffects.MayFree;
                }

                LlvmFunctionEffects updated = previous with
                {
                    MayThrow = mayThrow,
                    MayAccessMemory = mayAccessMemory,
                    MayAllocate = mayAllocate,
                    MayFree = mayFree,
                };
                if (updated == previous) continue;
                effects[function] = updated;
                changed = true;
            }
        } while (changed);

        foreach ((FunctionSymbol function, LocalEffects local) in locals)
        {
            LlvmFunctionEffects effect = effects[function];
            effects[function] = effect with
            {
                // A hint, never a command: LLVM remains responsible for the final cost decision.
                InlineCandidate = !local.HasLoopOrExceptionRegion &&
                    local.StructuralCost <= 24 &&
                    function.FunctionKind is not (FunctionKind.Destructor or FunctionKind.DestructorGlue) &&
                    !function.IsAbstract &&
                    !function.IsExtern,
            };
        }

        return effects;
    }

    private static LocalEffects AnalyzeLocal(
        MirFunction function,
        IReadOnlyDictionary<FunctionSymbol, MirFunction> bodies,
        IReadOnlyDictionary<FieldSymbol, FunctionSymbol> threadLocalInitializers)
    {
        var result = new LocalEffects();
        void AddCall(FunctionSymbol? callee, bool dynamic = false)
        {
            if (callee is null) return;
            if (dynamic || callee.IsExtern || !bodies.ContainsKey(callee))
            {
                result.MayThrow = result.MayAccessMemory = true;
                return;
            }
            result.Calls.Add(callee);
        }
        void Place(MirPlace place, IReadOnlyDictionary<MirLocalId, MirLocal> locals)
        {
            if (!place.Projections.IsEmpty || locals[place.Local].Kind == MirLocalKind.Capture)
                result.MayAccessMemory = true;
        }
        void Visit(MirFunction body)
        {
            var graph = new MirControlFlow(body);
            result.HasLoopOrExceptionRegion |= graph.HasCycle();
            var locals = body.Locals.ToDictionary(local => local.Id);
            foreach (var block in body.Blocks.Where(block => graph.Reachable.Contains(block.Id)))
            {

                foreach (var statement in block.Statements)
                {
                    if (statement is MirAssign assign)
                    {
                        if (!locals[assign.Destination.Local].IsCleanupControl) result.StructuralCost++;
                        Place(assign.Destination, locals);
                        foreach (var place in MirOperands.Of(assign.Value).SelectMany(MirOperands.Places)) Place(place, locals);
                        if (assign.Value is MirStaticFieldAddress or MirInterfaceView or MirCurrentException or MirExceptionMatches or MirExceptionReference)
                            result.MayAccessMemory = true;
                        if (assign.Value is MirBorrow borrow) Place(borrow.Place, locals);
                    }
                    else if (statement is MirReleaseException or MirInitializeDispatch or MirSetStorageState)
                        result.MayAccessMemory = true;
                }
                result.StructuralCost++;
                foreach (var place in MirOperands.Of(block.Terminator).SelectMany(MirOperands.Places)) Place(place, locals);
                switch (block.Terminator)
                {
                    case MirCall call:
                        if (call.Callee is MirFunctionOperand direct)
                            AddCall(direct.Function, call.IsVirtual || call.InterfaceType is not null);
                        else result.MayThrow = result.MayAccessMemory = true;
                        break;
                    case MirDrop drop: AddCall(drop.Destructor, drop.IsVirtual); break;
                    case MirThrow:
                        result.MayThrow = result.MayAccessMemory = result.HasLoopOrExceptionRegion = true;
                        break;
                    case MirSuspend:
                        result.MayThrow = result.MayAccessMemory = result.HasLoopOrExceptionRegion = true;
                        result.MayAllocate = result.MayFree = true;
                        break;
                    case MirIntrinsicCall intrinsic:
                        result.MayAccessMemory = true;
                        switch (intrinsic.Intrinsic)
                        {
                            case MirIntrinsicKind.Allocate or MirIntrinsicKind.Malloc or MirIntrinsicKind.Calloc or
                                MirIntrinsicKind.AlignedMalloc or MirIntrinsicKind.AllocateHeapArray or MirIntrinsicKind.AllocateStackArray or
                                MirIntrinsicKind.AdoptShared or MirIntrinsicKind.CoroutineCreate or MirIntrinsicKind.CreateContinuation or
                                MirIntrinsicKind.AsyncRootCreate or MirIntrinsicKind.AsyncRootContinuation:
                                result.MayAllocate = true;
                                break;
                            case MirIntrinsicKind.Free or MirIntrinsicKind.ReleaseWeak or MirIntrinsicKind.CoroutineFree or MirIntrinsicKind.AsyncRootClose:
                                result.MayFree = true;
                                break;
                            case MirIntrinsicKind.MakeCallable:
                                result.MayAllocate |= !intrinsic.Captures.IsEmpty;
                                break;
                            case MirIntrinsicKind.EnsureThreadLocal:
                                if (intrinsic.Field is { } field && threadLocalInitializers.TryGetValue(field, out var initializer)) AddCall(initializer);
                                else result.MayThrow = true;
                                break;
                        }
                        break;
                }
            }
        }
        Visit(function);
        if (function.Resumable is { } resumable)
        {
            Visit(resumable.Initialization);
            result.MayAllocate = result.MayFree = result.MayAccessMemory = result.HasLoopOrExceptionRegion = true;
        }
        return result;
    }
}