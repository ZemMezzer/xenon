using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Mir.Analysis;

internal sealed partial class MirReadonlyAnalysis
{
    private HashSet<object> Call(FunctionSymbol callee, HashSet<object>[] arguments, MirEffectSite site)
    {
        CheckCall(callee, site);
        _summaryLocations.Add(Root(site));
        var mutableArguments = new List<(HashSet<object> Storage, TypeSymbol Type)>();
        HashSet<object> available = [Root(site)]; // A readonly callee may allocate fresh storage.
        for (int index = 0; index < arguments.Length; index++)
        {
            HashSet<object> argument = arguments[index];
            if (index >= callee.Parameters.Length || !IsMutableParameter(callee.Parameters[index].Type)) continue;
            if (HasHiddenAccess(argument, callee.Parameters[index].Type))
                Report(site, $"cannot pass a mutable capability obtained from hidden state to parameter '{callee.Parameters[index].Name}' of '{callee.Name}'",
                    DiagnosticIds.MutableCapabilityArgumentEscape);
            available.UnionWith(argument);
            TypeSymbol elementType = callee.Parameters[index].Type switch
            {
                PointerTypeSymbol pointer => pointer.ElementType,
                ReferenceTypeSymbol reference => reference.ElementType,
                _ => throw new InvalidOperationException("Expected mutable pointer/reference parameter."),
            };
            HashSet<object> range = ArrayCapabilityRange(argument);
            CollectReachableStorage(range, elementType, available, []);
            mutableArguments.Add((range, elementType));
        }

        // Calls can store explicit input capabilities or freshly allocated values
        // through mutable outputs. Preserve those aliases for subsequent loads.
        ForgetReceiverTypes(available);
        foreach (var argument in mutableArguments) StoreUnknown(argument.Storage, available, argument.Type, []);
        Store([Root(site)], available);
        if (!ContainsAccess(callee.ReturnType)) return [];
        if (callee.ReturnType is IFieldStorageTypeSymbol)
        {
            StoreUnknown([Root(site)], available, callee.ReturnType, []);
            return [Root(site)];
        }
        // A returned pointer/reference may alias any explicit input, not just
        // fresh storage. Writes through that result must reach the original roots.
        return available;
    }

    private HashSet<object> InvokeMember(FunctionSymbol callee, HashSet<object>[] arguments, HashSet<object> receiver,
        MirEffectSite site, HashSet<StructTypeSymbol>? interfaceTypes = null)
    {
        if (callee.IsReadonly) return Call(callee, arguments, site);
        if (IsAccessor(callee)) return ContextualDispatch(callee, arguments, receiver, site, interfaceTypes);
        if (callee.FunctionKind == FunctionKind.Method && !callee.IsStatic)
        {
            // Gate the receiver storage, not every capability in its fields.
            // A local object may contain an unused hidden pointer alongside an
            // explicit output. The body decides which of those fields is used.
            // Type-level readonly receivers have already been rejected by binding.
            if (receiver.Contains(_hidden))
                Report(site, $"cannot call mutable instance method '{callee.Name}' on hidden state",
                    DiagnosticIds.MutableMethodOnHiddenState);
            return ContextualDispatch(callee, arguments, receiver, site, interfaceTypes);
        }
        return Call(callee, arguments, site);
    }

    private static bool IsAccessor(FunctionSymbol callee) => callee.IsAccessor;

    private HashSet<object> ContextualDispatch(FunctionSymbol callee,
        HashSet<object>[] arguments, HashSet<object> receiver, MirEffectSite site, HashSet<StructTypeSymbol>? interfaceTypes = null)
    {
        if (callee.FunctionKind is FunctionKind.Destructor or FunctionKind.DestructorGlue or FunctionKind.OwnershipDestructor or FunctionKind.StorageDestructor)
            CheckWrite(receiver, site);
        var targets = new HashSet<FunctionSymbol>();
        HashSet<StructTypeSymbol>? known = callee.ContainingInterface is null ? KnownReceiverTypes(receiver) : interfaceTypes;
        IEnumerable<StructTypeSymbol> receiverTypes = known is not null ? known : types;
        if (callee.ContainingInterface is not null)
        {
            foreach (StructTypeSymbol type in receiverTypes)
                if (!type.IsAbstract && type.Implements(callee.ContainingInterface) &&
                    type.FindInterfaceImplementation(callee) is { } implementation)
                    targets.Add(implementation);
        }
        else if (callee.VTableSlot is int slot && callee.ContainingType is StructTypeSymbol declaringType)
        {
            foreach (StructTypeSymbol type in receiverTypes)
            {
                if (type.IsAbstract || !IsDerivedFrom(type, declaringType) || slot >= type.VirtualMethods.Length) continue;
                FunctionSymbol target = type.VirtualMethods[slot];
                targets.Add(target.FunctionKind == FunctionKind.Destructor
                    ? type.CompleteDestructor ?? target
                    : target);
            }
        }
        else targets.Add(callee);

        if (targets.Count == 0)
            Report(site, $"cannot verify effects of member '{callee.Name}' without an implementation",
                DiagnosticIds.MissingDispatchImplementation);
        HashSet<object> result = [];
        MemoryState entry = _memory.Copy();
        MemoryState? exit = null;
        foreach (FunctionSymbol target in targets)
        {
            _memory = entry.Copy();
            result.UnionWith(ContextualCall(target, arguments, receiver, site));
            exit = Join(exit, _memory);
        }
        _memory = exit ?? entry;
        return result;
    }

    private HashSet<object> ContextualCall(FunctionSymbol callee,
        HashSet<object>[] values, HashSet<object> receiver, MirEffectSite site)
    {
        // Mutable instance methods, lifecycle members and accessors use the actual receiver
        // and arguments, without inventing a readonly declaration for the member.
        if (!bodies.TryGetValue(callee, out MirFunction? body))
        {
            Report(site, $"cannot verify effects of member '{callee.Name}' without a body",
                DiagnosticIds.MissingReadonlyCalleeBody);
            return [_hidden];
        }
        // Runtime wrapper destructors expose their receiver as parameter zero.
        if (values.Length == 0 && callee.FunctionKind is FunctionKind.StorageDestructor or
            FunctionKind.OwnershipDestructor or FunctionKind.FunctionValueDestructor)
            values = [new(receiver)];
        if (_activeCalls.ContainsKey(callee))
            return RecursiveCall(callee, values, receiver, site);
        if (!_context.Calls.TryGetValue(callee, out var sites))
            _context.Calls.Add(callee, sites = new(ReferenceEqualityComparer.Instance));
        if (!sites.TryGetValue(site, out EvaluationContext? context))
            sites.Add(site, context = new());
        var frame = new RecursiveFrame(context, _memory.Copy(), new(receiver), values.Select(value => new HashSet<object>(value)).ToArray());
        _activeCalls.Add(callee, frame);
        EvaluationContext previous = _context;
        _context = context;
        try
        {
            for (int iteration = 0; iteration < 128; iteration++)
            {
                MemoryState input = frame.Input.Copy();
                _memory = input.Copy();
                frame.ArgumentsChanged = false;
                context.Receiver.Clear();
                context.Receiver.UnionWith(frame.Receiver);
                context.Returned.Clear();
                context.ReturnSite = null;
                for (int index = 0; index < Math.Min(frame.Arguments.Length, callee.Parameters.Length); index++)
                    StoreValue([Root(callee.Parameters[index])], frame.Arguments[index], callee.Parameters[index].Type);
                Flow flow = Visit(body);
                _memory = Join(flow.Next, flow.Return) ?? _memory;
                if (!frame.Recursive) return new(context.Returned);

                bool newReturns = !context.Returned.IsSubsetOf(frame.Returned);
                frame.Returned.UnionWith(context.Returned);
                frame.Output = Join(frame.Output, _memory);
                frame.Input = Join(frame.Input, _memory)!;
                if (!newReturns && !frame.ArgumentsChanged && SameState(input, frame.Input))
                {
                    _memory = frame.Output!;
                    return new(frame.Returned);
                }
            }
            Report(site, $"cannot verify recursive effects of '{callee.Name}' within the analysis limit",
                DiagnosticIds.RecursiveReadonlyEffectLimit);
            return [_hidden];
        }
        finally
        {
            _context = previous;
            _activeCalls.Remove(callee);
        }
    }

    private static bool IsDerivedFrom(StructTypeSymbol type, StructTypeSymbol candidate)
    {
        for (StructTypeSymbol? current = type; current is not null; current = current.BaseType)
            if (TypeIdentity.AreSame(current, candidate)) return true;
        return false;
    }

    private HashSet<StructTypeSymbol>? KnownReceiverTypes(IEnumerable<object> storage)
    {
        HashSet<StructTypeSymbol> result = [];
        foreach (object location in storage)
        {
            if (!_memory.ReceiverTypes.TryGetValue(Unwrap(location), out var known) || known.Count == 0) return null;
            result.UnionWith(known);
        }
        return result.Count == 0 ? null : result;
    }

    private HashSet<object> InterfaceReceiver(HashSet<object> storage, InterfaceTypeSymbol type,
        out HashSet<StructTypeSymbol>? sourceTypes)
    {
        HashSet<object> result = [];
        HashSet<StructTypeSymbol> known = [];
        bool unknown = false;
        foreach (object value in Read(storage, type))
        {
            if (Unwrap(value) is InterfaceValue view)
            {
                if (KnownReceiverTypes([view]) is { } runtimeTypes) known.UnionWith(runtimeTypes);
                else known.UnionWith(types.Where(candidate => !candidate.IsAbstract && IsDerivedFrom(candidate, view.SourceType)));
                result.UnionWith(Read([view], type));
            }
            else { result.Add(value); unknown = true; }
        }
        sourceTypes = !unknown && known.Count != 0 ? known : null;
        return result;
    }

    private sealed class InterfaceValue(StructTypeSymbol sourceType)
    {
        public StructTypeSymbol SourceType { get; } = sourceType;
    }

    private void StoreReceiverTypes(HashSet<object> storage, HashSet<StructTypeSymbol>? types)
    {
        foreach (object location in storage)
        {
            object origin = Unwrap(location);
            if (types is null) _memory.ReceiverTypes[origin] = [];
            else if ((storage.Count == 1 && IsExact(location)) || !_memory.ReceiverTypes.ContainsKey(origin)) _memory.ReceiverTypes[origin] = new(types);
            else if (_memory.ReceiverTypes.TryGetValue(origin, out var previous) && previous.Count != 0) previous.UnionWith(types);
            // A weak write cannot refine an unknown previous runtime type.
        }
    }

    private void ForgetReceiverTypes(IEnumerable<object> storage)
    {
        var pending = new Stack<object>(storage);
        HashSet<object> visited = [];
        while (pending.TryPop(out object? location))
        {
            location = Unwrap(location);
            if (!visited.Add(location)) continue;
            if (_memory.ReceiverTypes.ContainsKey(location)) _memory.ReceiverTypes[location] = [];
            if (_fields.TryGetValue(location, out var fields))
                foreach (object field in fields.Values) pending.Push(field);
        }
    }

    private sealed class EvaluationContext
    {
        public HashSet<object> Receiver { get; } = [];
        public HashSet<object> Returned { get; } = [];
        public MirEffectSite? ReturnSite { get; set; }
        public Dictionary<object, object> Roots { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<object, object> Snapshots { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<MirEffectSite, InterfaceValue> InterfaceValues { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<FunctionSymbol, Dictionary<MirEffectSite, EvaluationContext>> Calls { get; } = [];
        public bool IsRecursive { get; set; }
    }

    private HashSet<object> Capture(HashSet<object> value, TypeSymbol type, object identity)
    {
        type = UnwrapValueStorage(type);
        if (type is not IFieldStorageTypeSymbol) return value;
        if (!_context.Snapshots.TryGetValue(identity, out object? root))
            _context.Snapshots.Add(identity, root = new object());
        if (_context.IsRecursive) _summaryLocations.Add(root);
        StoreValue([root], value, type);
        return [root];
    }

    private sealed class RecursiveFrame(EvaluationContext context, MemoryState input,
        HashSet<object> receiver, HashSet<object>[] arguments)
    {
        public EvaluationContext Context { get; } = context;
        public MemoryState Input { get; set; } = input;
        public MemoryState? Output { get; set; }
        public HashSet<object> Receiver { get; } = receiver;
        public HashSet<object>[] Arguments { get; } = arguments;
        public HashSet<object> Returned { get; } = [];
        public bool Recursive { get; set; }
        public bool ArgumentsChanged { get; set; }
    }

    private HashSet<object> RecursiveCall(FunctionSymbol callee, HashSet<object>[] arguments,
        HashSet<object> receiver, MirEffectSite site)
    {
        RecursiveFrame frame = _activeCalls[callee];
        frame.Recursive = true;
        frame.ArgumentsChanged |= !receiver.IsSubsetOf(frame.Receiver);
        frame.Receiver.UnionWith(receiver);
        for (int index = 0; index < Math.Min(arguments.Length, frame.Arguments.Length); index++)
        {
            frame.ArgumentsChanged |= !arguments[index].IsSubsetOf(frame.Arguments[index]);
            frame.Arguments[index].UnionWith(arguments[index]);
        }
        // Reuse the ancestor context instead of constructing an infinite call
        // tree. Re-evaluate its body until both recursive inputs and effects
        // converge; fields and by-value argument shapes remain independent.
        MemoryState recursiveInput = _memory.Copy();
        frame.Input = Join(frame.Input, recursiveInput)!;
        foreach (RecursiveFrame active in _activeCalls.Values)
        {
            active.Context.IsRecursive = true;
            _summaryLocations.UnionWith(active.Context.Roots.Values);
            _summaryLocations.UnionWith(active.Context.Snapshots.Values);
        }
        _memory = Join(_memory, frame.Output)!;
        return new(frame.Returned);
    }
}
