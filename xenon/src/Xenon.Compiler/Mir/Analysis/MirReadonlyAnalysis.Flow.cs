using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

internal sealed partial class MirReadonlyAnalysis
{
    private sealed class MemoryState : Dictionary<object, HashSet<object>>
    {
        public MemoryState() : base(ReferenceEqualityComparer.Instance) { }
        public Dictionary<object, HashSet<StructTypeSymbol>> ReceiverTypes { get; } = new(ReferenceEqualityComparer.Instance);
        public MemoryState Copy()
        {
            var result = new MemoryState();
            foreach (var (key, value) in this) result[key] = new(value);
            foreach (var (key, value) in ReceiverTypes) result.ReceiverTypes[key] = new(value);
            return result;
        }
    }

    private sealed record Flow(MemoryState? Next = null, MemoryState? Return = null, MemoryState? Throw = null);
    private static MemoryState? Join(params MemoryState?[] states)
    {
        MemoryState? result = null;
        foreach (MemoryState? state in states)
        {
            if (state is null) continue;
            if (result is null) { result = state.Copy(); continue; }
            foreach (var (key, value) in state)
                if (result.TryGetValue(key, out var previous)) previous.UnionWith(value); else result[key] = new(value);
            foreach (var (key, value) in state.ReceiverTypes)
                if (!result.ReceiverTypes.TryGetValue(key, out var previous)) result.ReceiverTypes[key] = new(value);
                else if (value.Count == 0) previous.Clear(); else if (previous.Count != 0) previous.UnionWith(value);
        }
        return result;
    }

    private static bool SameState(MemoryState left, MemoryState right) =>
        left.ReceiverTypes.Count == right.ReceiverTypes.Count && left.ReceiverTypes.All(pair =>
            right.ReceiverTypes.TryGetValue(pair.Key, out var types) && pair.Value.SetEquals(types)) &&
        left.All(pair => right.TryGetValue(pair.Key, out var value) ? pair.Value.SetEquals(value) : pair.Value.Count == 0) &&
        right.All(pair => left.TryGetValue(pair.Key, out var value) ? pair.Value.SetEquals(value) : pair.Value.Count == 0);

    private Flow Visit(MirFunction body)
    {
        MirFunction previous = _body;
        int previousLoop = _loopDepth;
        _body = body;
        try
        {
            foreach (MirLocal receiver in body.Locals.Where(local => local.Kind == MirLocalKind.Receiver))
                StoreValue([LocalRoot(receiver.Id)], _context.Receiver, receiver.Type);
            var graph = MirFlowFacts.Graph(body, cancellationToken);
            var transfer = new Transfer(this, body, _memory.Copy(), graph, previousLoop != 0);
            Func<MirBlockId, MemoryState?> output;
            if (body.HasDynamicCleanupOrder)
            {
                var result = MirDataflow.Solve(graph, new MirScalarPartitions<MemoryState?>(graph, transfer, cancellationToken), cancellationToken);
                output = block => Join(result.Output[block].Select(partition => partition.Value).ToArray());
            }
            else
            {
                var result = MirDataflow.Solve(graph, transfer, cancellationToken);
                output = block => result.Output[block];
            }
            MemoryState? returned = null, thrown = null;
            foreach (MirBasicBlock block in body.Blocks.Where(block => graph.Reachable.Contains(block.Id)))
            {
                if (block.Terminator is MirReturn) returned = Join(returned, output(block.Id));
                if (block.Terminator is MirResumeUnwind) thrown = Join(thrown, output(block.Id));
            }
            return new(Return: returned, Throw: thrown);
        }
        finally { _body = previous; _loopDepth = previousLoop; }
    }

    private sealed class Transfer : IMirDataflowAnalysis<MemoryState?>
    {
        private readonly MirReadonlyAnalysis _owner;
        private readonly MirFunction _body;
        private readonly MemoryState _entry;
        private readonly Dictionary<object, bool> _repeated = new(ReferenceEqualityComparer.Instance);
        public Transfer(MirReadonlyAnalysis owner, MirFunction body, MemoryState entry, MirControlFlow graph, bool repeatedEntry)
        {
            _owner = owner; _body = body; _entry = entry;
            foreach (MirBasicBlock block in body.Blocks)
            {
                var pending = new Stack<MirBlockId>(graph.Successors[block.Id].Select(edge => edge.Target));
                var seen = new HashSet<MirBlockId>();
                bool cycle = false;
                while (pending.TryPop(out var current))
                {
                    if (current == block.Id) { cycle = true; break; }
                    if (!seen.Add(current)) continue;
                    foreach (MirEdge edge in graph.Successors[current]) pending.Push(edge.Target);
                }
                foreach (MirStatement statement in block.Statements) _repeated[statement] = cycle || repeatedEntry;
                _repeated[block.Terminator] = cycle || repeatedEntry;
            }
        }
        public MirDataflowDirection Direction => MirDataflowDirection.Forward;
        public MemoryState? Bottom => null;
        public MemoryState? Boundary(MirControlFlow graph, MirBasicBlock block) => block.Id == _body.Entry ? _entry.Copy() : null;
        public MemoryState? Join(IEnumerable<MemoryState?> states) => MirReadonlyAnalysis.Join(states.ToArray());
        public bool Same(MemoryState? left, MemoryState? right) => left is null || right is null ? left == right : SameState(left, right);
        private bool Begin(object node, MemoryState? state)
        {
            if (state is null) return false;
            _owner._memory = state.Copy();
            _owner._loopDepth = _repeated[node] ? 1 : 0;
            return true;
        }
        public MemoryState? Statement(MirStatement statement, MemoryState? state)
        {
            if (!Begin(statement, state)) return null;
            switch (statement)
            {
                case MirAssign assign:
                    var site = _owner.Site(assign, assign.Source, assign.Value.Type);
                    _owner.Assign(assign.Destination, _owner.Value(assign.Value, site), site, check: true);
                    break;
                case MirInitializeDispatch dispatch:
                    _owner.StoreReceiverTypes(_owner.Address(dispatch.Place).Storage, [dispatch.Type]); break;
                case MirSetStorageState storage:
                    var storageSite = _owner.Site(storage, storage.Source, _owner.PlaceType(storage.Place));
                    var address = _owner.Address(storage.Place);
                    _owner.CheckWrite(address.Storage, storageSite);
                    break;
                case MirStorageLive live:
                    _owner.StoreValue([_owner.LocalRoot(live.Local)], [], _owner.Local(live.Local).Type); break;
            }
            return _owner._memory;
        }
        public MemoryState? Terminator(MirTerminator terminator, MemoryState? state)
        {
            if (!Begin(terminator, state)) return null;
            if (terminator is MirReturn { Value: { } operand })
            {
                var site = _owner.Site(terminator, terminator.Source, operand.Type);
                var returned = _owner.Capture(_owner.Operand(operand), operand.Type, site);
                _owner._context.Returned.UnionWith(returned);
                _owner._context.ReturnSite ??= site;
                if (ReferenceEquals(_owner._context, _owner._rootContext) && _owner.HasHiddenAccess(returned, operand.Type))
                    _owner.Report(site, "cannot return a mutable capability obtained from hidden state", DiagnosticIds.MutableCapabilityReturn);
            }
            return _owner._memory;
        }
        public MemoryState? Edge(MirBasicBlock source, MirEdge edge, MemoryState? state)
        {
            if (!Begin(source.Terminator, state)) return null;
            switch (source.Terminator)
            {
                case MirCall call:
                    TypeSymbol resultType = call.Destination is { } destination ? _owner.PlaceType(destination) : BuiltinTypes.Void;
                    var site = _owner.Site(call, call.Source, resultType);
                    var result = _owner.Invoke(call, site);
                    if (edge.Kind == MirEdgeKind.Normal && call.Destination is { } target) _owner.Assign(target, result, site, false);
                    break;
                case MirIntrinsicCall call:
                    var primitiveSite = _owner.Site(call, call.Source, call.ResultType);
                    var primitive = _owner.Intrinsic(call, primitiveSite);
                    if (edge.Kind == MirEdgeKind.Normal && call.Destination is { } output) _owner.Assign(output, primitive, primitiveSite, false);
                    break;
                case MirDrop { Destructor: { } destructor } drop:
                    _owner.ContextualDispatch(destructor, [], _owner.Address(drop.Place).Storage,
                        _owner.Site(drop, drop.Source, BuiltinTypes.Void));
                    break;
            }
            return _owner._memory;
        }
    }
}