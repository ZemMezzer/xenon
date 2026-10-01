using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirLifetimeDiagnostic(MirSourceInfo Source, string Id, string Message);

/// <summary>Lifetime propagation over executable MIR, including explicit cleanup and unwind edges.</summary>
public sealed partial class MirLifetimeAnalysis : IMirDataflowAnalysis<MirLifetimeState>
{
    private readonly MirFunction _function;
    private readonly Dictionary<MirLocalId, MirLocal> _locals;
    private readonly Dictionary<MirLocalId, Symbol> _owners;
    private readonly Dictionary<MirStatement, MirLocation> _statements = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<MirTerminator, MirLocation> _terminators = new(ReferenceEqualityComparer.Instance);
    private readonly MirStorageOrigins _origins;
    private readonly MirDataflowResult<ImmutableDictionary<MirLocalId, MirStorageOrigin>> _addresses;
    private readonly MirControlFlow _graph;
    private readonly MirFrameAnalysis? _frame;
    private readonly CancellationToken _cancellation;
    private readonly HashSet<LifetimeDependency> _returns = [];
    private readonly HashSet<LifetimeStore> _stores = [];
    private readonly HashSet<MirLifetimeDiagnostic> _diagnostics = [];
    private readonly HashSet<int> _retainedArrayScopes = [];
    public ImmutableHashSet<int> RetainedArrayScopes => _retainedArrayScopes.ToImmutableHashSet();
    private bool _collect;
    private MirLocation _location;
    public bool ReturnsOperation { get; private set; }
    public ImmutableArray<LifetimeDependency> Returns => [.. _returns];
    public ImmutableArray<LifetimeStore> Stores => [.. _stores];
    public ImmutableArray<MirLifetimeDiagnostic> Diagnostics => [.. _diagnostics];
    public MirDataflowDirection Direction => MirDataflowDirection.Forward;
    public MirLifetimeState Bottom => MirLifetimeState.Empty;

    public MirLifetimeAnalysis(MirFunction function,
        IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>>? effects = null, CancellationToken cancellation = default)
    {
        _function = function;
        _cancellation = cancellation;
        _locals = function.Locals.ToDictionary(local => local.Id);
        _owners = function.Locals.ToDictionary(local => local.Id, local => (Symbol?)local.Variable ??
            new LocalVariableSymbol("temporary", local.Type, function.Symbol));
        _graph = MirFlowFacts.Graph(function, cancellation, effects);
        _origins = new(function);
        _addresses = MirDataflow.Solve(_graph, _origins, cancellation);
        if (function.Blocks.Any(block => block.Terminator is MirSuspend)) _frame = new(function, cancellation);
        foreach (MirBasicBlock block in function.Blocks)
        {
            for (int index = 0; index < block.Statements.Length; index++)
                _statements[block.Statements[index]] = new(block.Id, index);
            _terminators[block.Terminator] = new(block.Id, block.Statements.Length);
        }
    }

    public MirDataflowResult<MirLifetimeState> Analyze()
    {
        var flow = MirDataflow.Solve(_graph, this, _cancellation);
        _collect = true;
        foreach (MirBasicBlock block in _function.Blocks.Where(block => _graph.Reachable.Contains(block.Id)))
        {
            for (int index = 0; index < block.Statements.Length; index++)
                _ = Statement(block.Statements[index], flow.Before[new(block.Id, index)]);
            _ = Terminator(block.Terminator, flow.Before[new(block.Id, block.Statements.Length)]);
            foreach (MirEdge edge in _graph.Successors[block.Id]) _ = Edge(block, edge, flow.Output[block.Id]);
        }
        _collect = false;
        return flow;
    }

    public MirLifetimeState Boundary(MirControlFlow graph, MirBasicBlock block)
    {
        if (block.Id != _function.Entry) return Bottom;
        var values = Bottom.Values.ToBuilder();
        foreach (MirLocal local in _function.Locals)
        {
            MirLifetimeValue value = MirLifetimeValue.Empty;
            if (local.Variable is ParameterSymbol parameter)
            {
                if (MirLifetimeValue.Carries(parameter.Type)) value = MirLifetimeValue.Input(LifetimeDependencyKind.ParameterValue, parameter.Ordinal);
                if (parameter.Type is ReferenceTypeSymbol or ArrayTypeSymbol)
                    value = value.Union(MirLifetimeValue.Input(LifetimeDependencyKind.ParameterBorrow, parameter.Ordinal));
            }
            else if (local.Variable is CaptureVariableSymbol capture)
                value = MirLifetimeValue.Input(capture.IsBorrow ? LifetimeDependencyKind.CaptureBorrow : LifetimeDependencyKind.CaptureValue, capture.Ordinal);
            else if (local.Kind == MirLocalKind.Receiver)
                value = MirLifetimeValue.Input(LifetimeDependencyKind.ReceiverValue);
            if (local.Type is OwnershipTypeSymbol) value = value with { StorageOwners = [local.Id] };
            values[local.Id] = value;
        }
        return new(true, values.ToImmutable(), Bottom.Pending);
    }

    public MirLifetimeState Join(IEnumerable<MirLifetimeState> states)
    {
        var values = Bottom.Values.ToBuilder();
        var pending = Bottom.Pending.ToBuilder();
        bool reachable = false;
        foreach (MirLifetimeState state in states.Where(state => state.Reachable))
        {
            reachable = true;
            foreach (var (local, value) in state.Values)
                values[local] = values.TryGetValue(local, out var old) ? old.Union(value) : value;
            foreach (var (operation, value) in state.Pending)
                pending[operation] = pending.TryGetValue(operation, out var old) ? old.Union(value) : value;
        }
        return new(reachable, values.ToImmutable(), pending.ToImmutable());
    }

    public bool Same(MirLifetimeState left, MirLifetimeState right)
    {
        return left.Reachable == right.Reachable &&
        left.Values.Count == right.Values.Count && left.Values.All(pair =>
            right.Values.TryGetValue(pair.Key, out var value) && pair.Value.Same(value)) &&
        left.Pending.Count == right.Pending.Count && left.Pending.All(pair =>
            right.Pending.TryGetValue(pair.Key, out var value) && pair.Value.Same(value));
    }

    private MirLifetimeValue Read(MirPlace place, MirLifetimeState state, bool resolve = true)
    {
        if (resolve)
        {
            MirPlace[] targets = Resolve(place, _location);
            if (targets.Length != 1 || targets[0] != place)
                return MirLifetimeValue.Union(targets.Select(target => Read(target, state, resolve: false)));
        }
        MirLifetimeValue value = state.Values.GetValueOrDefault(place.Local, MirLifetimeValue.Empty);
        TypeSymbol type = _locals[place.Local].Type;
        foreach (MirProjection projection in place.Projections)
        {
            switch (projection)
            {
                case MirFieldProjection field:
                    value = value.Project(field.Field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    type = field.Field.Type;
                    break;
                case MirDerefProjection when type is ReferenceTypeSymbol reference:
                    type = reference.ElementType;
                    value = value.AsValue(type);
                    break;
                case MirDerefProjection when type is PointerTypeSymbol pointer:
                    type = pointer.ElementType;
                    break;
                case MirLifetimeProjection when type is LifetimeModifierTypeSymbol modifier:
                    type = modifier.ElementType;
                    break;
                case MirIndexProjection or MirLinearIndexProjection:
                    value = value.Project("*");
                    type = type is ArrayTypeSymbol array ? array.ElementType : type;
                    break;
            }
        }
        return value;
    }

    private MirLifetimeValue Operand(MirOperand operand, MirLifetimeState state) => operand switch
    {
        MirCopy copy => Read(copy.Place, state),
        MirMove move => Read(move.Place, state).AsValue(move.Type) with { TransfersBacking = move.Type is ArrayTypeSymbol },
        _ => MirLifetimeValue.Empty,
    };

    private MirLifetimeValue Borrow(MirPlace place, MirLifetimeState state, bool resolve = true)
    {
        if (resolve)
        {
            MirPlace[] targets = Resolve(place, _location);
            if (targets.Length != 1 || targets[0] != place)
                return MirLifetimeValue.Union(targets.Select(target => Borrow(target, state, resolve: false)));
        }
        MirLocal local = _locals[place.Local];
        if (local.Kind == MirLocalKind.Receiver) return MirLifetimeValue.Input(LifetimeDependencyKind.ReceiverBorrow);
        MirLifetimeValue value = Read(place, state);
        if (local.Type is PointerTypeSymbol && place.Projections.Any(projection => projection is MirDerefProjection))
            return MirLifetimeValue.Empty;
        if (local.Variable is ParameterSymbol parameter && parameter.Type is ReferenceTypeSymbol or ArrayTypeSymbol)
            return state.Values.GetValueOrDefault(local.Id, MirLifetimeValue.Empty);
        if (local.Variable is CaptureVariableSymbol { IsBorrow: true } capture)
            return MirLifetimeValue.Input(LifetimeDependencyKind.CaptureBorrow, capture.Ordinal);
        if (local.Type is ReferenceTypeSymbol)
        {
            MirLifetimeValue reference = state.Values.GetValueOrDefault(local.Id, MirLifetimeValue.Empty);
            return reference;
        }
        if (local.Type is OwnershipTypeSymbol && !value.StorageOwners.IsEmpty)
            return value.Union(MirLifetimeValue.Union(value.StorageOwners.Select(owner => MirLifetimeValue.Local(_owners[owner]))));
        if (local.Type is ArrayTypeSymbol)
        {
            MirLifetimeValue array = state.Values.GetValueOrDefault(local.Id, MirLifetimeValue.Empty);
            return value.Union(array with { Fields = MirLifetimeValue.Empty.Fields, Operations = [], Callables = MirLifetimeValue.Empty.Callables });
        }
        return value.Union(MirLifetimeValue.Local(_owners[place.Local]));
    }

    private MirLifetimeValue Value(MirRValue value, MirLifetimeState state) => value switch
    {
        MirBorrow borrow => Borrow(borrow.Place, state),
        MirUse use => Operand(use.Operand, state),
        MirCast cast => Operand(cast.Operand, state),
        MirAtomicValue atomic => Operand(atomic.Value, state),
        MirInterfaceView view => Operand(view.Address, state),
        MirAggregate aggregate => MirLifetimeValue.Union(aggregate.Fields.Select(operand => Operand(operand, state))),
        _ => MirLifetimeValue.Empty,
    };

    private MirPlace[] Resolve(MirPlace place, MirLocation location) =>
        _addresses.Before.TryGetValue(location, out var addresses) ? _origins.Resolve(place, addresses).ToArray() : [place];

    private MirLifetimeState Write(MirPlace place, MirLifetimeValue value, MirLifetimeState state,
        MirLocation location, MirSourceInfo source, bool check = true, bool aggregateInitialization = false)
    {
        MirPlace[] targets = Resolve(place, location);
        foreach (MirPlace target in targets)
        {
            MirLocal local = _locals[target.Local];
            string path = string.Join('/', target.Projections.OfType<MirFieldProjection>()
                .Select(field => field.Field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            if (target.Projections.IsEmpty && local.Variable is LocalVariableSymbol { Type: ArrayTypeSymbol } &&
                !value.StackArrayBacking.IsEmpty && (value.TransfersBacking || value.StackArrayBacking.All(backing =>
                    _locals.Values.Any(origin => origin.Kind == MirLocalKind.Temporary && origin.Source.Scope == local.Source.Scope && ReferenceEquals(_owners[origin.Id], backing.LocalOwner)))))
            {
                if (_collect && value.TransfersBacking)
                    foreach (MirLocalId allocation in value.ArrayStorage) RetainArrayScopes(_locals[allocation], local);
                var backing = new ValueLifetimeDependency(_owners[local.Id], null);
                value = value with
                {
                    Dependencies = value.Dependencies.Except(value.StackArrayBacking).Add(backing),
                    BorrowedStorage = value.BorrowedStorage.Except(value.StackArrayBacking),
                    StackArrayBacking = [backing],
                    TransfersBacking = false,
                };
            }
            bool element = target.Projections.Any(projection => projection is MirIndexProjection or MirLinearIndexProjection);
            if (element && path.Length == 0) path = "*";
            bool indirect = target.Projections.Any(projection => projection is MirDerefProjection);
            bool external = local.Kind == MirLocalKind.Receiver || indirect;
            bool stackEscapes = (check || aggregateInitialization) && !value.StackArrayBacking.IsEmpty &&
                (external || !target.Projections.IsEmpty || local.Type is AtomicTypeSymbol { ElementType: ArrayTypeSymbol });
            if (_collect && stackEscapes)
            {
                bool positional = aggregateInitialization;
                Report(source, positional ? DiagnosticIds.StackArrayStoredInAggregate : DiagnosticIds.StackArrayEscape,
                    positional ? "stack array cannot be stored inside a positional struct value" : "stack array cannot escape through this assignment");
            }
            if (check && _collect)
            {
                if (local.Kind is MirLocalKind.Variable or MirLocalKind.Parameter) CheckPending(_owners[local.Id], state, source);
                foreach (ValueLifetimeDependency dependency in value.Dependencies)
                {
                    if (dependency.LocalOwner is { } owner && (external || local.Variable is not null && !Outlives(owner, local)) &&
                        !(stackEscapes && value.StackArrayBacking.Contains(dependency)))
                        Report(source, value.StackArrayBacking.Contains(dependency) ? DiagnosticIds.StackArrayEscape : DiagnosticIds.ValueLifetimeEscape,
                            value.StackArrayBacking.Contains(dependency)
                                ? "stack array cannot escape its allocation scope through this assignment without an explicit move relocation"
                                : $"value assigned to '{(external ? "external storage" : local.Name)}' depends on local '{owner.Name}', which does not outlive the destination");
                    if (external && dependency.Input is { } input)
                    {
                        int destination = local.Kind == MirLocalKind.Receiver ? -1 :
                            local.Variable is ParameterSymbol { Type: ReferenceTypeSymbol } parameter ? parameter.Ordinal : -2;
                        if (destination == -2)
                            foreach (ValueLifetimeDependency address in state.Values.GetValueOrDefault(local.Id, MirLifetimeValue.Empty).BorrowedStorage)
                                if (address.Input is { Kind: LifetimeDependencyKind.ParameterBorrow } borrowedParameter)
                                    destination = borrowedParameter.Ordinal;
                                else if (address.Input is { Kind: LifetimeDependencyKind.ReceiverBorrow }) destination = -1;
                        _stores.Add(new(destination, input, path));
                    }
                }
            }
            if (target.Projections.IsEmpty && local.Type is OwnershipTypeSymbol && local.Variable is not null)
                value = value with { StorageOwners = [local.Id] };
            MirLifetimeValue stored = value;
            if (!target.Projections.IsEmpty || targets.Length != 1)
            {
                MirLifetimeValue previous = state.Values.GetValueOrDefault(local.Id, MirLifetimeValue.Empty);
                stored = path.Length == 0 ? previous.Union(value, includeBacking: false) : previous.WithField(path, value);
            }
            if (element)
            {
                var storage = state.Values.GetValueOrDefault(local.Id, MirLifetimeValue.Empty).ArrayStorage;
                foreach (var (alias, contents) in state.Values.Where(pair => pair.Key != local.Id && pair.Value.ArrayStorage.Overlaps(storage)).ToArray())
                    state = state with { Values = state.Values.SetItem(alias, contents.WithField(path, value)) };
            }
            state = state with { Values = state.Values.SetItem(local.Id, stored) };
        }
        return state;
    }

    private void RetainArrayScopes(MirLocal allocation, MirLocal destination)
    {
        var scopes = new List<int>();
        int? scope = allocation.Source.Scope;
        while (scope is { } current && current != destination.Source.Scope)
        {
            scopes.Add(current);
            scope = _function.Scopes.FirstOrDefault(scope => scope.Id == current)?.Parent;
        }
        if (scope == destination.Source.Scope) _retainedArrayScopes.UnionWith(scopes);
    }
    private bool Outlives(Symbol owner, MirLocal destination)
    {
        if (owner is ParameterSymbol or CaptureVariableSymbol) return true;
        MirLocal? source = _locals.Values.FirstOrDefault(local => ReferenceEquals(_owners[local.Id], owner));
        if (source is null) return false;
        int? scope = destination.Source.Scope;
        while (scope is { } current)
        {
            if (current == source.Source.Scope)
                return current != destination.Source.Scope || source.Id.Value <= destination.Id.Value;
            scope = _function.Scopes.FirstOrDefault(scope => scope.Id == current)?.Parent;
        }
        return false;
    }

    private void CheckPending(Symbol owner, MirLifetimeState state, MirSourceInfo source)
    {
        if (!_collect) return;
        foreach (MirLifetimeValue pending in state.Pending.Values)
            if (pending.Dependencies.Any(dependency => ReferenceEquals(dependency.LocalOwner, owner)))
                Report(source, DiagnosticIds.PendingBorrowedOperation,
                    $"resumable operation borrowing '{owner.Name}' must complete before that owner is destroyed; await the operation on every exit");
    }

    private void Report(MirSourceInfo source, string id, string message)
    {
        if (_collect) _diagnostics.Add(new(source, id, message));
    }
}