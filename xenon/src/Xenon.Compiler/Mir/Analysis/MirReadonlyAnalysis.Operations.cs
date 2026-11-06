using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

internal sealed partial class MirReadonlyAnalysis
{
    private HashSet<object> Invoke(MirCall call, MirEffectSite site)
    {
        HashSet<object>[] arguments = call.Arguments.Select(argument => Capture(Operand(argument), argument.Type, argument)).ToArray();
        FunctionSymbol? callee = call.Callee switch
        {
            MirFunctionOperand direct => direct.Function,
            MirRequirementOperand { Requirement: FunctionSymbol requirement } => requirement,
            _ => null,
        };
        if (callee is null)
        {
            HashSet<object> capabilities = Operand(call.Callee);
            foreach (var argument in arguments) capabilities.UnionWith(argument);
            return ContainsAccess(site.Type) ? Uncertain(capabilities) : [];
        }
        if (call.Receiver is { } receiver)
        {
            HashSet<object> storage = Operand(receiver);
            HashSet<StructTypeSymbol>? interfaceTypes = null;
            if (call.InterfaceType is { } interfaceType)
                storage = InterfaceReceiver(storage, interfaceType, out interfaceTypes);
            if (callee.FunctionKind is FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer or
                FunctionKind.Destructor or FunctionKind.DestructorGlue or FunctionKind.OwnershipDestructor or FunctionKind.StorageDestructor)
                return ContextualDispatch(callee, arguments, storage, site, interfaceTypes);
            return InvokeMember(callee, arguments, storage, site, interfaceTypes);
        }
        return IsAccessor(callee) && !callee.IsReadonly ? ContextualDispatch(callee, arguments, [], site) : Call(callee, arguments, site);
    }

    private readonly HashSet<object> _knownAllocations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Dictionary<FunctionSymbol, object>> _allocationProviders = new(ReferenceEqualityComparer.Instance);

    private void RecordAllocation(MirIntrinsicCall call, MirEffectSite site, HashSet<object>[] arguments)
    {
        object allocation = Root(site);
        _knownAllocations.Add(allocation);
        if (call.Function?.AllocationOperation != AllocationOperation.Allocate) return;
        HashSet<object> receiver = arguments[^1];
        _ = ContextualDispatch(call.Function, [[], []], receiver, site);
        StructTypeSymbol provider = (StructTypeSymbol)((PointerTypeSymbol)call.Arguments[^1].Type).ElementType;
        if (!_allocationProviders.TryGetValue(allocation, out var providers))
            _allocationProviders.Add(allocation, providers = []);
        FunctionSymbol release = provider.DeallocationOperator!;
        if (!providers.TryGetValue(release, out object? context)) providers.Add(release, context = new object());
        _summaryLocations.Add(context);
        Store([context], receiver);
    }

    private void ReleaseAllocation(MirIntrinsicCall call, MirEffectSite site, HashSet<object> allocation)
    {
        bool unknown = false;
        foreach (object root in allocation.Select(Unwrap))
        {
            if (_allocationProviders.TryGetValue(root, out var providers))
                foreach (var (release, context) in providers)
                    _ = ContextualDispatch(release, [allocation], Read([context], release.ContainingStruct is { } owner ? new PointerTypeSymbol(owner, false) : call.Arguments[0].Type), site);
            else if (!_knownAllocations.Contains(root)) unknown = true;
        }
        if (unknown)
            foreach (FunctionSymbol release in call.AllocationReleases)
                _ = ContextualCall(release, [allocation], [_external], site);
    }

    private HashSet<object> Intrinsic(MirIntrinsicCall call, MirEffectSite site)
    {
        HashSet<object>[] arguments = call.Arguments.Select(Operand).ToArray();
        HashSet<object> All() => new(arguments.SelectMany(argument => argument));
        switch (call.Intrinsic)
        {
            case MirIntrinsicKind.AllocateStackArray or MirIntrinsicKind.AllocateHeapArray:
                RecordAllocation(call, site, arguments);
                object array = Root(site);
                InitializeArrayElements(call, site);
                return [array];
            case MirIntrinsicKind.Allocate or MirIntrinsicKind.Malloc or MirIntrinsicKind.AlignedMalloc or MirIntrinsicKind.Calloc:
                RecordAllocation(call, site, arguments);
                object allocated = Root(site);
                _summaryLocations.Add(allocated);
                return [allocated];
            case MirIntrinsicKind.CloneValue or MirIntrinsicKind.AdoptUnique or MirIntrinsicKind.AdoptShared or
                MirIntrinsicKind.ConvertWeak or MirIntrinsicKind.LockWeak or MirIntrinsicKind.SharedPayload or
                MirIntrinsicKind.CallableControl or MirIntrinsicKind.CallableEnvironment or MirIntrinsicKind.MakeCallable:
                return All();
            case MirIntrinsicKind.AtomicLoad:
                return Read(arguments[0], call.ResultType);
            case MirIntrinsicKind.AtomicInitialize or MirIntrinsicKind.AtomicStore or MirIntrinsicKind.AtomicExchange or
                MirIntrinsicKind.AtomicUpdate or MirIntrinsicKind.CompareExchange or MirIntrinsicKind.CompareExchangeOwned:
                if (arguments.Length == 0) return [];
                CheckWrite(arguments[0], site);
                TypeSymbol valueType = call.SubjectType is AtomicTypeSymbol atomic ? atomic.ElementType :
                    call.Arguments[0].Type is PointerTypeSymbol pointer ? UnwrapValueStorage(pointer.ElementType) : call.ResultType;
                HashSet<object> before = Read(arguments[0], valueType);
                HashSet<object> replacement = arguments.Length > 1 ? new(arguments[^1]) : [];
                if (call.Intrinsic is MirIntrinsicKind.CompareExchange or MirIntrinsicKind.CompareExchangeOwned or MirIntrinsicKind.AtomicUpdate)
                    replacement.UnionWith(before);
                StoreValue(arguments[0], replacement, valueType);
                return call.Intrinsic == MirIntrinsicKind.AtomicExchange || call.ReturnsOldValue ? before : replacement;
            case MirIntrinsicKind.Swap when arguments.Length == 2:
                CheckWrite(arguments[0], site); CheckWrite(arguments[1], site);
                TypeSymbol swapped = ElementType(call.Arguments[0].Type) ?? call.Arguments[0].Type;
                HashSet<object> left = Capture(Read(arguments[0], swapped), swapped, site);
                HashSet<object> right = Read(arguments[1], swapped);
                StoreValue(arguments[0], right, swapped); StoreValue(arguments[1], left, swapped);
                return [];
            case MirIntrinsicKind.Free:
                if (arguments.Length != 0)
                {
                    CheckWrite(arguments[0], site);
                    if (call.RestoresAllocator) ReleaseAllocation(call, site, arguments[0]);
                }
                return [];
            default: return [];
        }
    }
}