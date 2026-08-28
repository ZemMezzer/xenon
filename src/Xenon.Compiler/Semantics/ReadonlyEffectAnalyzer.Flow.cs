using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

internal sealed partial class ReadonlyEffectAnalyzer
{
    private readonly Stack<List<Cleanup>> _cleanupScopes = [];
    private readonly Dictionary<object, (List<Cleanup> Scope, BoundVariableExpression Site)> _scalarOwners = new(ReferenceEqualityComparer.Instance);
    private int _loopDepth;

    private sealed class MemoryState : Dictionary<object, HashSet<object>>
    {
        public MemoryState() : base(ReferenceEqualityComparer.Instance) { }
        public HashSet<object> Allocations { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<object> DefiniteAllocations { get; } = new(ReferenceEqualityComparer.Instance);
        // A null order means paths disagree about construction order. Keep
        // this in flow state: visiting one branch must not order another one.
        public Dictionary<List<Cleanup>, List<object>?> CleanupOrders { get; } = new(ReferenceEqualityComparer.Instance);
        // Empty sets mean unknown runtime type. Missing entries are storage not
        // yet initialized on this flow path (important at loop/branch joins).
        public Dictionary<object, HashSet<StructTypeSymbol>> ReceiverTypes { get; } = new(ReferenceEqualityComparer.Instance);
        public MemoryState Copy()
        {
            var copy = new MemoryState();
            foreach (var entry in this) copy.Add(entry.Key, new(entry.Value, ReferenceEqualityComparer.Instance));
            copy.Allocations.UnionWith(Allocations);
            copy.DefiniteAllocations.UnionWith(DefiniteAllocations);
            foreach (var entry in CleanupOrders) copy.CleanupOrders.Add(entry.Key, entry.Value is { } order ? new(order) : null);
            foreach (var entry in ReceiverTypes) copy.ReceiverTypes.Add(entry.Key, new(entry.Value));
            return copy;
        }
    }

    private sealed record Flow(MemoryState? Next = null, MemoryState? Break = null,
        MemoryState? Continue = null, MemoryState? Return = null);
    private sealed record Cleanup(object Root, FunctionSymbol Destructor, BoundExpression Site, bool IsArray = true);

    private static MemoryState? Join(params MemoryState?[] states)
    {
        MemoryState? result = null;
        foreach (MemoryState? state in states)
        {
            if (state is null) continue;
            if (result is null) { result = state.Copy(); continue; }
            foreach (var entry in state.ReceiverTypes)
            {
                if (!result.ReceiverTypes.TryGetValue(entry.Key, out var receiverTypes))
                    result.ReceiverTypes.Add(entry.Key, new(entry.Value));
                else if (entry.Value.Count == 0) receiverTypes.Clear();
                else if (receiverTypes.Count != 0) receiverTypes.UnionWith(entry.Value);
            }
            result.Allocations.UnionWith(state.Allocations);
            result.DefiniteAllocations.IntersectWith(state.DefiniteAllocations);
            foreach (var entry in state.CleanupOrders)
            {
                if (!result.CleanupOrders.TryGetValue(entry.Key, out var order))
                    result.CleanupOrders.Add(entry.Key, entry.Value is { } incoming ? new(incoming) : null);
                else
                    result.CleanupOrders[entry.Key] = MergeCleanupOrder(order, entry.Value);
            }
            foreach (var entry in state)
            {
                if (!result.TryGetValue(entry.Key, out var values)) result.Add(entry.Key, values = []);
                values.UnionWith(entry.Value);
            }
        }
        return result;
    }

    private static bool SameState(MemoryState left, MemoryState right) =>
        left.Allocations.SetEquals(right.Allocations) &&
        left.DefiniteAllocations.SetEquals(right.DefiniteAllocations) &&
        left.CleanupOrders.Count == right.CleanupOrders.Count &&
        left.CleanupOrders.All(entry => right.CleanupOrders.TryGetValue(entry.Key, out var order) &&
            (entry.Value is null ? order is null : order is not null && entry.Value.SequenceEqual(order))) &&
        left.ReceiverTypes.Count == right.ReceiverTypes.Count &&
        left.ReceiverTypes.All(entry => right.ReceiverTypes.TryGetValue(entry.Key, out var types) && entry.Value.SetEquals(types)) &&
        left.All(entry => right.TryGetValue(entry.Key, out var values) ? entry.Value.SetEquals(values) : entry.Value.Count == 0) &&
        right.All(entry => left.TryGetValue(entry.Key, out var values) ? entry.Value.SetEquals(values) : entry.Value.Count == 0);

    private static List<object>? MergeCleanupOrder(List<object>? left, List<object>? right)
    {
        if (left is null || right is null) return null;
        // A conditional registration may omit entries without changing the
        // relative order. Allocation sets separately track those skipped paths.
        if (IsSubsequence(left, right)) return new(right);
        return IsSubsequence(right, left) ? left : null;

        static bool IsSubsequence(List<object> shorter, List<object> longer)
        {
            int index = 0;
            foreach (object root in longer)
                if (index < shorter.Count && ReferenceEquals(shorter[index], root)) index++;
            return index == shorter.Count;
        }
    }

    private static Flow JoinFlow(Flow left, Flow right) => new(
        Join(left.Next, right.Next), Join(left.Break, right.Break),
        Join(left.Continue, right.Continue), Join(left.Return, right.Return));

    private Flow Visit(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                return InScope(() =>
                {
                    Flow result = new(_memory);
                    foreach (BoundStatement child in block.Statements)
                    {
                        if (result.Next is null) break;
                        _memory = result.Next;
                        result = JoinFlow(result with { Next = null }, Visit(child));
                    }
                    return result;
                }, block.ExitCleanup);
            case BoundVariableDeclarationStatement declaration:
                if (declaration.Variable.Destructor is not null)
                {
                    object root = Root(declaration.Variable);
                    BoundVariableExpression site = _scalarOwners.TryGetValue(root, out var previous) ? previous.Site : new(declaration.Variable);
                    _scalarOwners[root] = (_cleanupScopes.Peek(), site);
                    _memory.Allocations.Remove(root);
                    _memory.DefiniteAllocations.Remove(root);
                }
                StoreValue([Root(declaration.Variable)], declaration.Initializer is { } initializer ? Evaluate(initializer) : [], declaration.Variable.Type);
                if (declaration.Initializer is not null) RegisterScalarCleanup(declaration.Variable);
                return new(_memory);
            case BoundExpressionStatement expression:
                Evaluate(expression.Expression);
                return new(_memory);
            case BoundReturnStatement returned:
                if (returned.Expression is { } value)
                {
                    HashSet<object> origins = Capture(Evaluate(value), value.Type, value);
                    _context.Returned.UnionWith(origins);
                    _context.ReturnSite ??= value;
                    if (ReferenceEquals(_context, _rootContext) && HasHiddenAccess(origins, function.ReturnType))
                        Report(value, "cannot return a mutable capability obtained from hidden state");
                }
                return new(Return: _memory);
            case BoundBreakStatement:
                return new(Break: _memory);
            case BoundContinueStatement:
                return new(Continue: _memory);
            case BoundIfStatement conditional:
            {
                Evaluate(conditional.Condition);
                MemoryState entry = _memory.Copy();
                bool? known = BooleanConstant(conditional.Condition);
                Flow then = known == false ? new() : VisitEmbedded(conditional.ThenStatement);
                _memory = entry.Copy();
                Flow alternative = known == true ? new() : conditional.ElseStatement is { } other ? VisitEmbedded(other) : new(_memory);
                return JoinFlow(then, alternative);
            }
            case BoundWhileStatement loop:
                return VisitLoop(loop.Condition, loop.Body, null);
            case BoundForStatement loop:
                return InScope(() =>
                {
                    Flow initial = loop.Initializer is { } initializer ? Visit(initializer) : new(_memory);
                    if (initial.Next is null) return initial;
                    _memory = initial.Next;
                    return JoinFlow(initial with { Next = null }, VisitLoop(loop.Condition, loop.Body, loop.Increment));
                });
            case BoundSwitchStatement selection:
            {
                Evaluate(selection.Expression);
                MemoryState entry = _memory.Copy();
                bool canSkipBodies = !selection.Sections.Any(section => section.Value is null) ||
                    selection.Sections.IsEmpty || selection.Sections[^1].Body.Statements.IsEmpty;
                Flow result = canSkipBodies ? new(entry) : new();
                // Consecutive empty labels share the next non-empty body.
                foreach (BoundSwitchSection section in selection.Sections)
                {
                    if (section.Body.Statements.IsEmpty) continue;
                    _memory = entry.Copy();
                    Flow branch = Visit(section.Body);
                    result = JoinFlow(result, branch with { Next = Join(branch.Next, branch.Break), Break = null });
                }
                return result;
            }
            default:
                throw new InvalidOperationException($"Missing readonly flow analysis for '{statement.Kind}'.");
        }
    }

    private Flow VisitEmbedded(BoundStatement statement) => statement is BoundBlockStatement
        ? Visit(statement) : InScope(() => Visit(statement));

    private Flow VisitLoop(BoundExpression? condition, BoundStatement body, BoundExpression? increment)
    {
        MemoryState entry = _memory.Copy();
        MemoryState head = entry.Copy();
        Flow exits = new();
        _loopDepth++;
        try
        {
            while (true)
            {
                _memory = head.Copy();
                if (condition is not null) Evaluate(condition);
                bool? known = condition is null ? true : BooleanConstant(condition);
                MemoryState? falseExit = known == true ? null : _memory.Copy();
                Flow iteration = known == false ? new() : VisitEmbedded(body);
                exits = JoinFlow(exits, new(Join(falseExit, iteration.Break), Return: iteration.Return));
                MemoryState? back = Join(iteration.Next, iteration.Continue);
                if (back is not null && increment is not null)
                {
                    _memory = back;
                    Evaluate(increment);
                    back = _memory;
                }
                MemoryState nextHead = Join(entry, back)!;
                if (SameState(head, nextHead)) return exits;
                head = nextHead;
            }
        }
        finally { _loopDepth--; }
    }

    private static bool? BooleanConstant(BoundExpression expression) =>
        expression is BoundLiteralExpression { Value: bool value } ? value : null;

    private Flow InScope(Func<Flow> visit, BoundExpression? exitCleanup = null)
    {
        var cleanups = new List<Cleanup>();
        _memory.CleanupOrders.Add(cleanups, []);
        _cleanupScopes.Push(cleanups);
        Flow flow;
        try { flow = visit(); }
        finally { _cleanupScopes.Pop(); }
        var result = new Flow(Clean(flow.Next), Clean(flow.Break), Clean(flow.Continue), Clean(flow.Return));
        // A non-returning body has no exit state to clean, but its scope identity
        // must not escape into a caller's recursive effect fixed point.
        _memory.CleanupOrders.Remove(cleanups);
        return result;

        MemoryState? Clean(MemoryState? state)
        {
            if (state is null) return null;
            _memory = state.Copy();
            if (_memory.CleanupOrders[cleanups] is { } order)
            {
                for (int index = order.Count - 1; index >= 0; index--)
                {
                    Cleanup cleanup = cleanups.Single(item => ReferenceEquals(item.Root, order[index]));
                    if (!_memory.Allocations.Remove(cleanup.Root)) continue;
                    MemoryState? skipped = _memory.DefiniteAllocations.Remove(cleanup.Root) ? null : _memory.Copy();
                    Destroy(cleanup);
                    _memory = Join(skipped, _memory)!;
                }
            }
            else
            {
                Cleanup[] possible = cleanups.Where(cleanup => _memory.Allocations.Contains(cleanup.Root)).ToArray();
                foreach (Cleanup cleanup in possible)
                {
                    _memory.Allocations.Remove(cleanup.Root);
                    _memory.DefiniteAllocations.Remove(cleanup.Root);
                }
                // Conflicting orders must not let one destructor erase an
                // origin another can still observe. Close over all possible
                // effects without enumerating branch/order permutations.
                MemoryState head = _memory.Copy();
                while (true)
                {
                    MemoryState previous = head.Copy();
                    foreach (Cleanup cleanup in possible)
                    {
                        _memory = head.Copy();
                        Destroy(cleanup);
                        _memory = Join(head, _memory)!;
                        head = _memory;
                    }
                    if (SameState(previous, head)) break;
                }
            }
            _memory.CleanupOrders.Remove(cleanups);
            if (exitCleanup is not null) Evaluate(exitCleanup);
            return _memory;
        }
    }

    private void Destroy(Cleanup cleanup)
    {
        if (cleanup.IsArray) DestroyElements(cleanup.Destructor, [cleanup.Root], cleanup.Site);
        else ContextualCall(cleanup.Destructor, Array.Empty<HashSet<object>>(), [cleanup.Root], cleanup.Site);
    }

    private void RegisterCleanup(List<Cleanup> scope, Cleanup cleanup)
    {
        if (!scope.Any(item => ReferenceEquals(item.Root, cleanup.Root))) scope.Add(cleanup);
        if (_memory.CleanupOrders[scope] is { } order)
        {
            if (!_memory.Allocations.Contains(cleanup.Root)) order.Add(cleanup.Root);
            else if (cleanup.IsArray || !_memory.DefiniteAllocations.Contains(cleanup.Root))
                _memory.CleanupOrders[scope] = null;
        }
        _memory.Allocations.Add(cleanup.Root);
        _memory.DefiniteAllocations.Add(cleanup.Root);
    }

    private void RegisterScalarCleanup(LocalVariableSymbol local)
    {
        if (local.Destructor is not { } destructor) return;
        object root = Root(local);
        var owner = _scalarOwners[root];
        RegisterCleanup(owner.Scope, new(root, destructor, owner.Site, IsArray: false));
    }

    private void DestroyElements(FunctionSymbol destructor, HashSet<object> receiver, BoundExpression site)
    {
        if (receiver.Count == 1 && _arrays.TryGetValue(receiver.Single(), out var array))
        {
            for (int index = array.Elements.Length - 1; index >= 0; index--)
                ContextualDispatch(destructor, Array.Empty<HashSet<object>>(), [array.Elements[index]], site);
            return;
        }
        receiver = ArrayElements(receiver);
        // Array length and indices are summarized: cleanup can run zero or more
        // times, so it must not strongly erase an origin in some other object.
        MemoryState entry = _memory.Copy();
        MemoryState head = entry.Copy();
        _loopDepth++;
        try
        {
            while (true)
            {
                _memory = head.Copy();
                ContextualDispatch(destructor, Array.Empty<HashSet<object>>(), receiver, site);
                MemoryState next = Join(entry, _memory)!;
                if (SameState(head, next)) { _memory = next; return; }
                head = next;
            }
        }
        finally { _loopDepth--; }
    }

    private HashSet<object> Capture(HashSet<object> value, TypeSymbol type, object identity)
    {
        if (type is not IFieldStorageTypeSymbol) return value;
        if (!_context.Snapshots.TryGetValue(identity, out object? root))
            _context.Snapshots.Add(identity, root = new object());
        if (_context.IsRecursive) _summaryLocations.Add(root);
        StoreValue([root], value, type);
        return [root];
    }

    private void ResetConstruction(BoundExpression site, TypeSymbol type)
    {
        object root = Root(site);
        if (_loopDepth != 0 || site is BoundArrayCreationExpression) _summaryLocations.Add(root);
        bool initialized = _memory.ReceiverTypes.TryGetValue(root, out var previous);
        HashSet<StructTypeSymbol>? previousTypes = previous is null ? null : new(previous);
        StoreValue([root], [], type);
        if (type is StructTypeSymbol structure)
        {
            if (!initialized || IsExact(root)) _memory.ReceiverTypes[root] = [structure];
            else if (previousTypes is { Count: > 0 })
            {
                previousTypes.Add(structure);
                _memory.ReceiverTypes[root] = previousTypes;
            }
        }
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
        HashSet<object> receiver, BoundExpression site)
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
        // Current activation scopes are not caller scopes of the ancestor.
        // Carrying their identities into its next iteration would grow the
        // recursive state forever and incorrectly share local lifetimes.
        foreach (var scope in recursiveInput.CleanupOrders.Keys.ToArray())
            if (!frame.Input.CleanupOrders.ContainsKey(scope)) recursiveInput.CleanupOrders.Remove(scope);
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
