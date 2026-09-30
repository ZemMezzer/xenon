using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir;

public sealed partial class MirVerifier
{
    private void Intrinsic(MirIntrinsicCall operation)
    {
        int arity = operation.Intrinsic switch
        {
            MirIntrinsicKind.AllocateStackArray or MirIntrinsicKind.AllocateHeapArray => operation.ResultType is ArrayTypeSymbol array ? array.Rank : -1,
            MirIntrinsicKind.ArrayDimension or MirIntrinsicKind.ConstructStorage => 2,
            MirIntrinsicKind.ClosureControl or MirIntrinsicKind.CreateContinuation or MirIntrinsicKind.ReleaseClosure or MirIntrinsicKind.EnsureThreadLocal or MirIntrinsicKind.Allocate => 0,
            MirIntrinsicKind.MakeCallable => operation.Captures.Length,
            MirIntrinsicKind.AlignedMalloc or MirIntrinsicKind.Calloc or MirIntrinsicKind.Swap or MirIntrinsicKind.AtomicInitialize or MirIntrinsicKind.AtomicStore or MirIntrinsicKind.AtomicUpdate or MirIntrinsicKind.AtomicExchange => 2,
            MirIntrinsicKind.CompareExchange or MirIntrinsicKind.CompareExchangeOwned => 3,
            _ => 1,
        };
        if (operation.Arguments.Length != arity) { Error($"{operation.Intrinsic} argument count mismatch"); return; }
        MirOperand? first = operation.Arguments.FirstOrDefault();
        switch (operation.Intrinsic)
        {
            case MirIntrinsicKind.ReleaseStrong:
                if (first!.Type is not SharedTypeSymbol) Error("strong release requires shared storage");
                Same(operation.ResultType, BuiltinTypes.Bool, "strong release result");
                break;
            case MirIntrinsicKind.SharedPayload:
                if (first!.Type is SharedTypeSymbol sharedPayload)
                    Same(operation.ResultType, sharedPayload.StorageType, "shared payload");
                else Error("payload requires shared storage");
                break;
            case MirIntrinsicKind.ReleaseWeak:
                if (first!.Type is not (SharedTypeSymbol or WeakTypeSymbol)) Error("weak release requires shared or weak storage");
                Same(operation.ResultType, BuiltinTypes.Void, "weak release result");
                break;
            case MirIntrinsicKind.CallableControl:
                if (first!.Type is not FunctionValueTypeSymbol) Error("callable control requires a function value");
                if (operation.ResultType is not PointerTypeSymbol { ElementType: var controlElement } ||
                    !TypeIdentity.AreSame(controlElement, BuiltinTypes.Byte)) Error("callable control must be byte*");
                break;
            case MirIntrinsicKind.ClosureControl:
                if (operation.ResultType is not PointerTypeSymbol { ElementType: var closureElement } ||
                    !TypeIdentity.AreSame(closureElement, BuiltinTypes.Byte)) Error("closure control must be byte*");
                break;
            case MirIntrinsicKind.ReleaseCallableCount or MirIntrinsicKind.CallableEnvironment or MirIntrinsicKind.CallableDestructor:
                if (first!.Type is not PointerTypeSymbol { ElementType: var elementType } ||
                    !TypeIdentity.AreSame(elementType, BuiltinTypes.Byte)) Error("callable operation requires byte* control");
                if (operation.Intrinsic == MirIntrinsicKind.ReleaseCallableCount)
                    Same(operation.ResultType, BuiltinTypes.Bool, "callable count result");
                else if (operation.Intrinsic == MirIntrinsicKind.CallableEnvironment && operation.ResultType is not PointerTypeSymbol)
                    Error("callable environment must be a pointer");
                else if (operation.Intrinsic == MirIntrinsicKind.CallableDestructor &&
                    (operation.ResultType is not FunctionPointerTypeSymbol signature || signature.ParameterTypes.Length != 1 ||
                    !TypeIdentity.AreSame(signature.ReturnType, BuiltinTypes.Void) ||
                    !TypeIdentity.AreSame(signature.ParameterTypes[0], first.Type)))
                    Error("callable destructor requires void(byte*) signature");
                break;
            case MirIntrinsicKind.CloneValue:
                Same(first!.Type, operation.ResultType, "clone value");
                break;
            case MirIntrinsicKind.AllocateStackArray or MirIntrinsicKind.AllocateHeapArray:
                foreach (MirOperand dimension in operation.Arguments)
                    if (dimension.Type is not PrimitiveTypeSymbol { IsInteger: true }) Error("array dimension must be an integer");
                break;
            case MirIntrinsicKind.ArrayLength or MirIntrinsicKind.ArrayRank or MirIntrinsicKind.ArrayDimension:
                if (first!.Type is not ArrayTypeSymbol) Error("array metadata requires an array");
                Same(operation.ResultType, BuiltinTypes.Int, "array metadata result");
                if (operation.Intrinsic == MirIntrinsicKind.ArrayDimension && operation.Arguments[1].Type is not PrimitiveTypeSymbol { IsInteger: true })
                    Error("array metadata dimension must be an integer");
                break;
            case MirIntrinsicKind.MoveStorage or MirIntrinsicKind.ConstructStorage or MirIntrinsicKind.DestroyStorage or
                MirIntrinsicKind.CheckStorageEmpty or MirIntrinsicKind.CheckStorageInitialized or MirIntrinsicKind.MarkStorageInitialized:
                if (first!.Type is not PointerTypeSymbol { ElementType: StorageTypeSymbol storage })
                { Error("storage intrinsic requires a pointer to storage"); break; }
                Same(operation.ResultType, operation.Intrinsic == MirIntrinsicKind.MoveStorage ? storage.ElementType : BuiltinTypes.Void, "storage intrinsic result");
                if (operation.Intrinsic == MirIntrinsicKind.ConstructStorage)
                    Same(operation.Arguments[1].Type, storage.ElementType, "storage initializer");
                break;
            case MirIntrinsicKind.EnsureThreadLocal:
                if (operation.Field is not { IsThreadLocal: true }) Error("thread-local ensure requires a thread-local field");
                Same(operation.ResultType, BuiltinTypes.Void, "thread-local ensure result");
                break;
            case MirIntrinsicKind.ReleaseClosure:
                Same(operation.ResultType, BuiltinTypes.Void, "closure release result");
                break;
            case MirIntrinsicKind.CreateContinuation:
                if (operation.ResultType is not FunctionValueTypeSymbol callback || !callback.ParameterTypes.IsEmpty ||
                    !TypeIdentity.AreSame(callback.ReturnType, BuiltinTypes.Void)) Error("continuation requires function void()");
                break;
            case MirIntrinsicKind.AdoptUnique or MirIntrinsicKind.AdoptShared:
                if (operation.ResultType is not OwnershipTypeSymbol owner) Error("adoption requires an owner result");
                else Same(first!.Type, owner.StorageType, "adoption storage");
                break;
            case MirIntrinsicKind.ConvertWeak:
                if (first!.Type is not SharedTypeSymbol shared || operation.ResultType is not WeakTypeSymbol weak ||
                    !TypeIdentity.AreSame(shared.ElementType, weak.ElementType)) Error("weak conversion requires matching shared and weak types");
                break;
            case MirIntrinsicKind.LockWeak:
                if (first!.Type is not WeakTypeSymbol source || operation.ResultType is not SharedTypeSymbol target ||
                    !TypeIdentity.AreSame(source.ElementType, target.ElementType)) Error("weak lock requires matching weak and shared types");
                break;
            case MirIntrinsicKind.Allocate:
                if (operation.ResultType is not PointerTypeSymbol pointer || operation.SubjectType is null ||
                    !TypeIdentity.AreSame(pointer.ElementType, operation.SubjectType)) Error("allocation requires its concrete pointee type");
                break;
            case MirIntrinsicKind.Malloc or MirIntrinsicKind.AlignedMalloc or MirIntrinsicKind.Calloc:
                if (operation.ResultType is not PointerTypeSymbol) Error("raw allocation must return a pointer");
                foreach (MirOperand size in operation.Arguments)
                    if (size.Type is not PrimitiveTypeSymbol { IsInteger: true }) Error("allocation size/alignment must be integral");
                break;
            case MirIntrinsicKind.Free or MirIntrinsicKind.Delete:
                if (first!.Type is not (PointerTypeSymbol or ArrayTypeSymbol)) Error("deallocation requires a pointer or array");
                Same(operation.ResultType, BuiltinTypes.Void, "deallocation result");
                break;
            case MirIntrinsicKind.DestroyFields or MirIntrinsicKind.DestroyOwner or MirIntrinsicKind.DestroyCallable:
                if (first!.Type is not PointerTypeSymbol address || operation.SubjectType is null ||
                    !TypeIdentity.AreSame(address.ElementType, operation.SubjectType)) Error("destruction requires its concrete storage address");
                Same(operation.ResultType, BuiltinTypes.Void, "destruction result");
                break;
            case MirIntrinsicKind.MakeCallable:
                if (operation.Function is null || operation.ResultType is not FunctionValueTypeSymbol) Error("callable requires an invoke function and callable type");
                for (int index = 0; index < operation.Captures.Length; index++)
                    Same(operation.Arguments[index].Type, operation.Captures[index].StorageType, "capture initializer");
                break;
            case MirIntrinsicKind.AtomicExchange or MirIntrinsicKind.AtomicLoad or MirIntrinsicKind.AtomicInitialize or MirIntrinsicKind.AtomicStore or MirIntrinsicKind.AtomicUpdate:
                if (first!.Type is not PointerTypeSymbol { ElementType: AtomicTypeSymbol atomic }) { Error("atomic operation requires an atomic address"); break; }
                Same(operation.ResultType, atomic.ElementType, "atomic result");
                if (operation.Arguments.Length == 2) Same(operation.Arguments[1].Type, atomic.ElementType, "atomic value");
                if (operation.Intrinsic == MirIntrinsicKind.AtomicUpdate && operation.Operator is null) Error("atomic update requires an operator");
                break;
            case MirIntrinsicKind.CompareExchange or MirIntrinsicKind.CompareExchangeOwned:
                if (first!.Type is not PointerTypeSymbol exchange)
                { Error("compare exchange requires a target address"); break; }
                TypeSymbol element = exchange.ElementType is AtomicTypeSymbol atomicElement ? atomicElement.ElementType : exchange.ElementType;
                Same(operation.Arguments[1].Type, element, "compare exchange expected");
                Same(operation.Arguments[2].Type, element, "compare exchange desired");
                if (operation.Intrinsic == MirIntrinsicKind.CompareExchange)
                    Same(operation.ResultType, BuiltinTypes.Bool, "compare exchange result");
                else if (operation.ResultType is StructTypeSymbol { Fields.Length: 2 } exchangeResult)
                {
                    Same(exchangeResult.Fields[0].Type, BuiltinTypes.Bool, "compare exchange success");
                    Same(exchangeResult.Fields[1].Type, element, "compare exchange discarded value");
                }
                else Error("owned compare exchange requires success and discarded value fields");
                break;
            case MirIntrinsicKind.Swap:
                if (first!.Type is not PointerTypeSymbol lhs || operation.Arguments[1].Type is not PointerTypeSymbol rhs)
                    Error("swap requires two addresses");
                                else Same(rhs.ElementType is AtomicTypeSymbol rightAtomic ? rightAtomic.ElementType : rhs.ElementType,
                    lhs.ElementType is AtomicTypeSymbol leftAtomic ? leftAtomic.ElementType : lhs.ElementType, "swap element");
                Same(operation.ResultType, BuiltinTypes.Void, "swap result");
                break;
            default: Error($"unknown intrinsic {operation.Intrinsic}"); break;
        }
    }
}
