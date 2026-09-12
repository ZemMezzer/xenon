using System.Collections.Immutable;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.CodeGen.LLVM;

/// <summary>
/// Conservative, whole-module facts used solely to make LLVM IR more precise.
/// A false value is emitted only when the complete bound implementation proves it.
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
        ImmutableArray<BoundFunction> functions)
    {
        var bodies = new Dictionary<FunctionSymbol, BoundFunction>(ReferenceEqualityComparer.Instance);
        foreach (BoundFunction function in functions)
            bodies.Add(function.Symbol, function);
        var threadLocalInitializers = new Dictionary<FieldSymbol, FunctionSymbol>(ReferenceEqualityComparer.Instance);
        foreach (BoundFunction function in functions)
        {
            if (function.Symbol is { FunctionKind: FunctionKind.ThreadLocalInitializer, ThreadLocalField: { } field })
                threadLocalInitializers[field] = function.Symbol;
        }
        var locals = new Dictionary<FunctionSymbol, LocalEffects>(ReferenceEqualityComparer.Instance);

        foreach (BoundFunction function in functions)
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
        BoundFunction function,
        IReadOnlyDictionary<FunctionSymbol, BoundFunction> bodies,
        IReadOnlyDictionary<FieldSymbol, FunctionSymbol> threadLocalInitializers)
    {
        var result = new LocalEffects();
        bool hasFinalizer = false;

        void AddCall(FunctionSymbol? callee, bool dynamicallyDispatched = false)
        {
            if (callee is null) return;
            if (dynamicallyDispatched || callee.IsExtern || !bodies.ContainsKey(callee))
            {
                result.MayThrow = true;
                result.MayAccessMemory = true;
                return;
            }

            result.Calls.Add(callee);
        }

        void AddDefaultInitializers(StructTypeSymbol? type)
        {
            if (type is null) return;
            AddDefaultInitializers(type.BaseType);
            AddCall(type.InstanceInitializer);
        }

        // Owned by-value parameters are registered in the function's outer cleanup
        // scope and destroyed on every exit. Those calls are synthesized by codegen,
        // not represented in the bound body.
        foreach (ParameterSymbol parameter in function.Symbol.Parameters)
            AddCall(TypeFacts.GetCompleteDestructor(parameter.Type));

        XelibBodyCodec.Collect(function.Body, _ => { }, _ => { }, node =>
        {
            result.StructuralCost++;

            // Some nodes also carry a direct lifecycle call. Record the intrinsic
            // allocation/free effect before the call-shaped switch cases below.
            if (node is BoundNewExpression or BoundArrayCreationExpression or
                BoundSharedAdoptionExpression or BoundFunctionValueExpression { Captures.IsEmpty: false })
            {
                result.MayAllocate = true;
                result.MayAccessMemory = true;
            }

            if (node is BoundFreeExpression or BoundDeleteExpression)
            {
                result.MayFree = true;
                result.MayAccessMemory = true;
            }

            switch (node)
            {
                case BoundThrowStatement:
                    result.MayThrow = true;
                    result.MayAccessMemory = true;
                    result.HasLoopOrExceptionRegion = true;
                    break;
                case BoundTryStatement @try:
                    result.MayAccessMemory = true;
                    result.HasLoopOrExceptionRegion = true;
                    hasFinalizer |= @try.FinallyBody is not null;
                    break;
                case BoundWhileStatement or BoundForStatement:
                    result.HasLoopOrExceptionRegion = true;
                    break;

                case BoundCallExpression call:
                    AddCall(call.Function);
                    break;
                case BoundMethodCallExpression call:
                    AddCall(call.Method, call.Method.VTableSlot is not null);
                    break;
                case BoundConstructorCallExpression call:
                    AddCall(call.Constructor);
                    break;
                case BoundBaseLifecycleCallExpression call:
                    AddCall(call.Function);
                    break;
                case BoundStorageConstructExpression { Constructor: { } constructor }:
                    AddCall(constructor);
                    break;
                case BoundStorageConstructExpression { Constructor: null, ValueType: StructTypeSymbol type }:
                    AddDefaultInitializers(type);
                    break;
                case BoundNewExpression { Constructor: { } constructor }:
                    AddCall(constructor);
                    break;
                case BoundNewExpression { Constructor: null, StructType: { } type }:
                    AddDefaultInitializers(type);
                    AddCall(TypeFacts.GetCompleteDestructor(type));
                    break;
                case BoundStructConstructionExpression construction:
                    AddDefaultInitializers(construction.StructType);
                    break;
                case BoundArrayCreationExpression array:
                    StructTypeSymbol? elementType = array.ElementType switch
                    {
                        StructTypeSymbol direct => direct,
                        AtomicTypeSymbol { ElementType: StructTypeSymbol atomic } => atomic,
                        _ => null,
                    };
                    AddDefaultInitializers(elementType);
                    AddCall(TypeFacts.GetCompleteDestructor(array.ElementType));
                    break;
                case BoundExplicitDestructExpression { Destructor: { } destructor }:
                    AddCall(destructor, destructor.VTableSlot is not null);
                    break;
                case BoundDeleteExpression { Destructor: { } destructor }:
                    AddCall(destructor, destructor.VTableSlot is not null);
                    break;
                case BoundOwnershipDestructionExpression { ElementDestructor: { } destructor }:
                    AddCall(destructor);
                    break;
                case BoundStorageDestructionExpression { ElementDestructor: { } destructor }:
                    AddCall(destructor);
                    break;
                case BoundFullExpression full:
                    foreach (BoundFullExpressionTemporary temporary in full.Temporaries)
                        AddCall(temporary.Destructor);
                    break;
                case BoundAssignmentExpression assignment:
                    AddCall(TypeFacts.GetCompleteDestructor(assignment.Target.Type));
                    if (assignment.Target.Type is ArrayTypeSymbol arrayTarget)
                        AddCall(TypeFacts.GetCompleteDestructor(arrayTarget.ElementType));
                    if (assignment.Target.Type is AtomicTypeSymbol atomicTarget)
                        AddCall(TypeFacts.GetCompleteDestructor(atomicTarget.ElementType));
                    break;
                case BoundCompareExchangeExpression compareExchange
                    when compareExchange.Target.Type is AtomicTypeSymbol atomic:
                    AddCall(TypeFacts.GetCompleteDestructor(atomic.ElementType));
                    break;
                case BoundVariableDeclarationStatement declaration
                    when (declaration.Initializer is not null || declaration.Variable.Type is StorageTypeSymbol) &&
                         declaration.Variable.Destructor is { } destructor:
                    AddCall(destructor);
                    break;
                case BoundPropertySetExpression set:
                    AddCall(set.Property.Setter,
                        set.Property.Setter?.VTableSlot is not null);
                    break;
                case BoundIndexerSetExpression set:
                    AddCall(set.Indexer.Setter,
                        set.Indexer.Setter?.VTableSlot is not null);
                    break;
                case BoundCompoundAccessorAssignmentExpression compound when compound.InterfaceType is null:
                    AddCall(compound.Getter, compound.Getter.VTableSlot is not null);
                    AddCall(compound.Setter, compound.Setter.VTableSlot is not null);
                    break;

                // The concrete target is deliberately unavailable at these sites.
                case BoundIndirectCallExpression or
                     BoundFunctionValueCallExpression or
                     BoundInterfaceMethodCallExpression or
                     BoundInterfacePropertySetExpression or
                     BoundInterfaceIndexerSetExpression:
                    result.MayThrow = true;
                    result.MayAccessMemory = true;
                    break;
                case BoundCompoundAccessorAssignmentExpression { InterfaceType: not null }:
                    result.MayThrow = true;
                    result.MayAccessMemory = true;
                    break;

                // Field/base destructor calls are compiler-generated and are not children
                // of this bound node, so add their exact semantic targets explicitly.
                case BoundDestroyFieldsExpression destruction:
                    result.MayAccessMemory = true;
                    foreach (FieldSymbol field in destruction.StructType.Fields)
                        AddCall(TypeFacts.GetCompleteDestructor(field.Type));
                    AddCall(destruction.StructType.BaseType?.CompleteDestructor);
                    break;

                case BoundRawAllocationExpression:
                    result.MayAllocate = true;
                    result.MayAccessMemory = true;
                    break;
                // These operations either access caller-visible storage or use ownership/
                // synchronization runtime state. Local alloca traffic alone is intentionally
                // not classified as a caller-visible memory effect.
                case BoundStaticFieldExpression { Field.IsThreadLocal: true } field:
                    if (!ReferenceEquals(function.Symbol.ThreadLocalField, field.Field) &&
                        threadLocalInitializers.TryGetValue(field.Field, out FunctionSymbol? initializer))
                        AddCall(initializer);
                    result.MayAccessMemory = true;
                    break;
                case BoundMemberAccessExpression or
                     BoundStaticFieldExpression or
                     BoundIndexExpression or
                     BoundArrayMetadataExpression or
                     BoundReferenceDereferenceExpression or
                     BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } or
                     BoundVariableExpression { Variable: CaptureVariableSymbol } or
                     BoundCompareExchangeExpression or
                     BoundSwapExpression or
                     BoundOwnershipDestructionExpression or
                     BoundStorageDestructionExpression or
                     BoundWeakConversionExpression or
                     BoundLockExpression or
                     BoundCopyExpression { Type: SharedTypeSymbol or WeakTypeSymbol or FunctionValueTypeSymbol or StructTypeSymbol }:
                    result.MayAccessMemory = true;
                    break;
                case BoundFunctionValueDestructionExpression:
                    result.MayThrow = true;
                    result.MayAccessMemory = true;
                    break;
            }
        });

        // A return value remains live while enclosing finally blocks execute. A
        // control transfer from such a finally supersedes that pending value and
        // codegen invokes its destructor even though the call is not represented
        // by a child bound node.
        if (hasFinalizer)
            AddCall(TypeFacts.GetCompleteDestructor(function.Symbol.ReturnType));

        return result;
    }
}
