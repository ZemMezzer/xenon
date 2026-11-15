using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirReferenceOrigins
{
    private readonly Dictionary<(MirLocalId Site, int Capture), MirLocalId> _captureSlots = [];
    private readonly Dictionary<(FunctionSymbol Function, int Parameter), MirLocalId> _unknownCallableArguments = [];

    private MirLocalId CaptureSlot(MirCallableTarget target, int ordinal)
    {
        if (_captureSlots.TryGetValue((target.Site, ordinal), out var slot)) return slot;
        // Receiver/parameter carriers use -1-ordinal; heap slots grow from
        // int.MinValue and static slots descend from int.MaxValue.
        slot = new(int.MinValue / 2 + _captureSlots.Count + _unknownCallableArguments.Count);
        _captureSlots[(target.Site, ordinal)] = slot;
        _locals[slot] = new(slot, "captured value", target.Function.LambdaCaptures[ordinal].StorageType,
            MirLocalKind.Temporary, MirSourceInfo.Generated);
        return slot;
    }

    private MirOperand UnknownCallableArgument(FunctionSymbol function, int ordinal)
    {
        if (!_unknownCallableArguments.TryGetValue((function, ordinal), out var slot))
        {
            slot = new(int.MinValue / 2 + _captureSlots.Count + _unknownCallableArguments.Count);
            _unknownCallableArguments[(function, ordinal)] = slot;
            _locals[slot] = new(slot, "unknown callable argument", function.Parameters[ordinal].Type,
                MirLocalKind.Temporary, MirSourceInfo.Generated);
        }
        return new MirCopy(new(slot), function.Parameters[ordinal].Type);
    }

    private ImmutableDictionary<MirLocalId, MirReferenceValue> StoreCallableCaptures(MirIntrinsicCall call,
        MirLocalId site, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        if (call.Function is not { } function) return state;
        var target = new MirCallableTarget(function, site);
        for (int ordinal = 0; ordinal < call.Captures.Length; ordinal++)
        {
            var slot = CaptureSlot(target, ordinal);
            // A finite site can have older live closure instances. Retain all
            // reaching snapshots instead of overwriting their captured aliases.
            state = state.SetItem(slot, state.GetValueOrDefault(slot, MirReferenceValue.Empty).Union(Operand(call.Arguments[ordinal], state)));
        }
        return state;
    }

    internal IEnumerable<MirCall> BoundCalls(MirCall call, ImmutableDictionary<MirLocalId, MirReferenceValue> state,
        MirReferenceValue? knownCallable = null)
    {
        if (knownCallable is null && MirPointerLifetimeAnalysis.DirectCallee(call) is not null) { yield return call; yield break; }
        var callable = knownCallable ?? Operand(call.Callee, state);
        var targets = callable.CallableTargets.GetValueOrDefault("", []);
        if (targets.IsEmpty) { yield return call; yield break; }
        foreach (var target in targets.OrderBy(target => target.Function.FullName, StringComparer.Ordinal).ThenBy(target => target.Site.Value))
        {
            var function = target.Function.MirCaptureFunction ?? target.Function;
            var arguments = call.Arguments.ToBuilder();
            // Deferred callable invocation coverage may not retain ordinary
            // argument relationships. Unknown placeholders cannot prune guards.
            while (arguments.Count < target.Function.Parameters.Length)
                arguments.Add(UnknownCallableArgument(target.Function, arguments.Count));
            if (target.Function.MirCaptureFunction is not null)
                for (int index = 0; index < target.Function.LambdaCaptures.Length; index++)
                    arguments.Add(new MirCopy(new(CaptureSlot(target, index)), target.Function.LambdaCaptures[index].StorageType));
            yield return call with { Callee = new MirFunctionOperand(function, call.Callee.Type), Arguments = arguments.ToImmutable(),
                Receiver = null, IsVirtual = false, InterfaceType = null };
        }
    }

    internal MirCall DeferredInvocation(MirCall context, MirOperand callable) => context with { Callee = callable, Arguments = [] };

    internal ImmutableHashSet<MirReferenceOrigin> CallablePayload(MirReferenceValue value,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var roots = ImmutableHashSet.CreateBuilder<MirReferenceOrigin>();
        var seen = new HashSet<MirCallableTarget>();
        Visit(value);
        return roots.ToImmutable();
        void Visit(MirReferenceValue current)
        {
            roots.UnionWith(current.Roots);
            foreach (var field in current.Fields.Values) roots.UnionWith(field);
            foreach (var target in current.CallableTargets.Values.SelectMany(targets => targets))
                if (seen.Add(target))
                    for (int ordinal = 0; ordinal < target.Function.LambdaCaptures.Length; ordinal++)
                        Visit(state.GetValueOrDefault(CaptureSlot(target, ordinal), MirReferenceValue.Empty));
        }
    }
}
