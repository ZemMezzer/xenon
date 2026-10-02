using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

[Flags]
public enum MirStorageContent { Empty = 1, Live = 2, Unknown = Empty | Live }

/// <summary>Known storage initialization states; unknown states remain runtime checked.</summary>
public sealed class MirStorageStateAnalysis : IMirDataflowAnalysis<ImmutableDictionary<MirReferenceOrigin, MirStorageContent>>
{
    private readonly MirFunction _function;
    private readonly ImmutableDictionary<MirReferenceOrigin, MirStorageContent> _boundary;
    private readonly MirReferenceOrigins _origins;
    private readonly Dictionary<MirStatement, ImmutableDictionary<MirLocalId, MirReferenceValue>> _before = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MirTerminator, ImmutableDictionary<MirLocalId, MirReferenceValue>> _ends = new(ReferenceEqualityComparer.Instance);
    public MirStorageStateAnalysis(MirFunction function, CancellationToken cancellation = default)
    {
        _function = function;
        _origins = new(function);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(function, cancellation), _origins, cancellation);
        foreach (var block in function.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
        {
            for (int index = 0; index < block.Statements.Length; index++) _before[block.Statements[index]] = flow.Before[new(block.Id, index)];
            _ends[block.Terminator] = flow.Output[block.Id];
        }
        var keys = new HashSet<MirReferenceOrigin>();
        foreach (var (statement, state) in _before)
        {
            if (statement is MirSetStorageState storage) keys.UnionWith(_origins.Address(storage.Place, state).Select(Key));
            if (statement is MirAssign assign)
                foreach (string path in Leaves(assign.Value.Type, [], []))
                    keys.UnionWith(_origins.Address(assign.Destination, state).Select(root => Key(root.Project(path))));
        }
        foreach (var (terminator, state) in _ends)
            if (terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.CheckStorageEmpty or MirIntrinsicKind.CheckStorageInitialized, Arguments.Length: > 0 } check)
                keys.UnionWith(_origins.Operand(check.Arguments[0], state).Roots.Select(Key));
        _boundary = keys.Where(Tracked).ToImmutableDictionary(key => key, _ => MirStorageContent.Unknown);
    }
    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public ImmutableDictionary<MirReferenceOrigin, MirStorageContent> Bottom => ImmutableDictionary<MirReferenceOrigin, MirStorageContent>.Empty;
    public ImmutableDictionary<MirReferenceOrigin, MirStorageContent> Boundary(MirControlFlow graph, MirBasicBlock block) => block.Id == _function.Entry ? _boundary : Bottom;
    public ImmutableDictionary<MirReferenceOrigin, MirStorageContent> Join(IEnumerable<ImmutableDictionary<MirReferenceOrigin, MirStorageContent>> states)
    {
        var inputs = states.ToArray();
        return inputs.SelectMany(state => state.Keys).Distinct().ToImmutableDictionary(key => key,
            key => inputs.Aggregate((MirStorageContent)0, (value, state) => value | state.GetValueOrDefault(key)));
    }
    public bool Same(ImmutableDictionary<MirReferenceOrigin, MirStorageContent> left, ImmutableDictionary<MirReferenceOrigin, MirStorageContent> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static MirReferenceOrigin Key(MirReferenceOrigin origin) => origin with { Authority = MirLifetimeAuthority.Owner, IsReadonly = false };
    private static bool Tracked(MirReferenceOrigin origin) => origin.Kind is MirReferenceOriginKind.Local or MirReferenceOriginKind.Parameter or MirReferenceOriginKind.Receiver;
    private static ImmutableDictionary<MirReferenceOrigin, MirStorageContent> Set(IEnumerable<MirReferenceOrigin> addresses,
        MirStorageContent value, ImmutableDictionary<MirReferenceOrigin, MirStorageContent> state)
    {
        var keys = addresses.Where(Tracked).Select(Key).Distinct().ToArray();
        foreach (var key in keys) state = state.SetItem(key, keys.Length == 1 ? value : value | state.GetValueOrDefault(key, MirStorageContent.Unknown));
        return state;
    }
    public MirStorageContent State(MirIntrinsicCall call, ImmutableDictionary<MirReferenceOrigin, MirStorageContent> state)
    {
        if (!_ends.TryGetValue(call, out var origins) || call.Arguments.Length == 0) return MirStorageContent.Unknown;
        var keys = _origins.Operand(call.Arguments[0], origins).Roots.Select(Key).ToArray();
        if (keys.Length == 0 || keys.Any(key => !Tracked(key))) return MirStorageContent.Unknown;
        return keys.Aggregate((MirStorageContent)0, (value, key) => value | state.GetValueOrDefault(key, MirStorageContent.Unknown));
    }
    public ImmutableDictionary<MirReferenceOrigin, MirStorageContent> Statement(MirStatement statement,
        ImmutableDictionary<MirReferenceOrigin, MirStorageContent> state)
    {
        var origins = _before.GetValueOrDefault(statement, _origins.Bottom);
        if (statement is MirSetStorageState storage)
            return Set(_origins.Address(storage.Place, origins), storage.Initialized ? MirStorageContent.Live : MirStorageContent.Empty, state);
        if (statement is MirStorageLive live)
            return state.RemoveRange(state.Keys.Where(key => key.Kind == MirReferenceOriginKind.Local && key.Ordinal == live.Local.Value));
        if (statement is not MirAssign assign) return state;
        foreach (var path in Leaves(assign.Value.Type, [], []))
        {
            var target = _origins.Address(assign.Destination, origins).Select(root => root.Project(path));
            MirStorageContent value = MirStorageContent.Unknown;
            if (assign.Value is MirDefault) value = MirStorageContent.Empty;
            else if (assign.Value is MirUse use && MirOperands.Places(use.Operand).FirstOrDefault() is { } source)
            {
                var keys = _origins.Address(source, origins).Select(root => Key(root.Project(path))).ToArray();
                if (keys.Length != 0) value = keys.Aggregate((MirStorageContent)0,
                    (result, key) => result | state.GetValueOrDefault(key, MirStorageContent.Unknown));
            }
            state = Set(target, value, state);
        }
        return state;
    }
    public ImmutableDictionary<MirReferenceOrigin, MirStorageContent> Terminator(MirTerminator terminator,
        ImmutableDictionary<MirReferenceOrigin, MirStorageContent> state) => state;
    public ImmutableDictionary<MirReferenceOrigin, MirStorageContent> Edge(MirBasicBlock source, MirEdge edge,
        ImmutableDictionary<MirReferenceOrigin, MirStorageContent> state)
    {
        if (source.Terminator is not MirCall call || !_ends.TryGetValue(call, out var origins)) return state;
        foreach (var argument in call.Arguments)
            if (argument.Type is ReferenceTypeSymbol { IsReadonly: false } reference)
                Invalidate(_origins.Operand(argument, origins).Roots, reference.ElementType);
        if (call.Receiver is { } receiver && call.Callee is MirFunctionOperand { Function: { IsReadonly: false, ContainingType: { } type } })
            Invalidate(_origins.Operand(receiver, origins).Roots, type);
        return state;
        void Invalidate(IEnumerable<MirReferenceOrigin> roots, TypeSymbol type)
        {
            foreach (string path in Leaves(type, [], []))
                state = Set(roots.Select(root => root.Project(path)), MirStorageContent.Unknown, state);
        }
    }
    private static IEnumerable<string> Leaves(TypeSymbol type, ImmutableArray<int> path, HashSet<TypeSymbol> seen)
    {
        if (type is StorageTypeSymbol) { yield return string.Join('/', path); yield break; }
        if (type is PinTypeSymbol pin) { foreach (string leaf in Leaves(pin.ElementType, path, seen)) yield return leaf; yield break; }
        if (type is not IFieldStorageTypeSymbol structure || !seen.Add(type)) yield break;
        foreach (var field in structure.AllInstanceFields)
            foreach (string leaf in Leaves(field.Type, path.Add(field.Ordinal), seen)) yield return leaf;
        seen.Remove(type);
    }
}