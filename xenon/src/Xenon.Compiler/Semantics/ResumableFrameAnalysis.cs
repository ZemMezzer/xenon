using System.Collections.Immutable;
using System.Numerics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

/// <summary>Source-independent suspension liveness. Recomputed after XELIB specialization.</summary>
public sealed class ResumableFrameAnalysis
{
    private sealed class Point
    {
        public HashSet<Symbol> Uses { get; } = [];
        public HashSet<Symbol> Defines { get; } = [];
        public HashSet<Symbol> Live { get; } = [];
        public List<Point> Next { get; } = [];
        public VariableSymbol? AssignedVariable { get; set; }
        public BoundExpression? AssignedValue { get; set; }
        public Dictionary<Symbol, HashSet<BoundArrayCreationExpression>> Arrays { get; } = [];
    }
    private sealed record Flow(Point Return, Point Throw, Point? Break = null, Point? Continue = null);
    private readonly FunctionSymbol _function;
    private readonly List<Point> _points = [];
    private readonly Dictionary<BoundAwaitExpression, Point> _awaits = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Symbol, HashSet<Symbol>> _owners = [];
    private readonly Dictionary<BoundExpression, LocalVariableSymbol> _arrayTemporaries = new(ReferenceEqualityComparer.Instance);
    public PinTypeSymbol FrameType { get; }
    public IReadOnlyDictionary<BoundAwaitExpression, ImmutableHashSet<Symbol>> Suspensions { get; private set; } =
        new Dictionary<BoundAwaitExpression, ImmutableHashSet<Symbol>>(ReferenceEqualityComparer.Instance);
    public IReadOnlyDictionary<BoundAwaitExpression, ImmutableHashSet<Symbol>> LiveValues { get; private set; } =
        new Dictionary<BoundAwaitExpression, ImmutableHashSet<Symbol>>(ReferenceEqualityComparer.Instance);
    public ImmutableHashSet<Symbol> LiveAcross { get; private set; } = [];
    public ImmutableHashSet<BoundArrayCreationExpression> RetainedArrays { get; private set; } = [];

    private ResumableFrameAnalysis(FunctionSymbol function, TypeFactory types)
    {
        _function = function;
        // This is the semantic storage type of the LLVM-split frame. It is constructed
        // directly in its final allocation and never copied or relocated thereafter.
        var frame = new StructTypeSymbol(function.Name + ".frame", function.ContainingNamespace,
            false, SymbolOrigin.CompilerGenerated, accessibility: Accessibility.Private);
        FrameType = types.PinOf(frame);
        if (TypeFacts.CanRelocate(FrameType)) throw new InvalidOperationException("A pinned frame cannot relocate.");
    }

    public static ResumableFrameAnalysis Analyze(FunctionSymbol function, BoundBlockStatement body, TypeFactory types)
    {
        var result = new ResumableFrameAnalysis(function, types);
        var exit = result.New();
        foreach (VariableSymbol parameter in function.Parameters.Cast<VariableSymbol>().Concat(function.LambdaCaptures))
            if (parameter is not CaptureVariableSymbol { IsBorrow: true } && TypeFacts.RequiresDestruction(parameter.Type))
                exit.Uses.Add(parameter);
        Point entry = result.Statement(body, exit, new Flow(exit, exit));
        bool changed;
        do
        {
            changed = false;
            for (int i = result._points.Count - 1; i >= 0; i--)
            {
                Point point = result._points[i];
                var live = new HashSet<Symbol>(point.Next.SelectMany(next => next.Live));
                live.ExceptWith(point.Defines);
                live.UnionWith(point.Uses);
                if (!point.Live.SetEquals(live)) { point.Live.UnionWith(live); changed = true; }
            }
        } while (changed);
        result.LiveValues = result._awaits.ToDictionary(pair => pair.Key, pair => pair.Value.Live.ToImmutableHashSet());
        result.Suspensions = result._awaits.ToDictionary(pair => pair.Key,
            pair => result.ExpandOwners(pair.Value.Live).ToImmutableHashSet(), (IEqualityComparer<BoundAwaitExpression>)ReferenceEqualityComparer.Instance);
        result.LiveAcross = result.Suspensions.Values.SelectMany(value => value).ToImmutableHashSet();
        // Array descriptors can be reassigned between suspension regions. Track reaching
        // backing allocations, rather than retaining every array ever assigned to a local.
        var predecessors = result._points.ToDictionary(point => point, _ => new List<Point>());
        var reachable = new HashSet<Point>();
        var pending = new Stack<Point>(); pending.Push(entry);
        while (pending.TryPop(out Point? point))
        {
            if (!reachable.Add(point)) continue;
            foreach (Point successor in point.Next) { predecessors[successor].Add(point); pending.Push(successor); }
        }
        do
        {
            changed = false;
            foreach (Point point in result._points.Where(reachable.Contains))
            {
                var state = new Dictionary<Symbol, HashSet<BoundArrayCreationExpression>>();
                void Add(Symbol symbol, IEnumerable<BoundArrayCreationExpression> arrays)
                {
                    if (!state.TryGetValue(symbol, out var set)) state[symbol] = set = new(ReferenceEqualityComparer.Instance);
                    set.UnionWith(arrays);
                }
                foreach (Point predecessor in predecessors[point])
                    foreach (var pair in predecessor.Arrays) Add(pair.Key, pair.Value);
                if (point.AssignedVariable is { } variable)
                {
                    var backing = new HashSet<BoundArrayCreationExpression>(ReferenceEqualityComparer.Instance);
                    if ((CarriesBorrow(variable.Type) || variable.Type is PointerTypeSymbol or StructTypeSymbol) && point.AssignedValue is { } value)
                    {
                        backing.UnionWith(BoundTree.DescendantsAndSelf(value).OfType<BoundArrayCreationExpression>());
                        foreach (Symbol source in result.Uses(value))
                            if (state.TryGetValue(source, out var arrays)) backing.UnionWith(arrays);
                    }
                    state[variable] = backing;
                }
                foreach (var pair in state)
                {
                    if (!point.Arrays.TryGetValue(pair.Key, out var existing))
                        point.Arrays[pair.Key] = existing = new(ReferenceEqualityComparer.Instance);
                    int before = existing.Count;
                    existing.UnionWith(pair.Value);
                    changed |= before != existing.Count;
                }
            }
        } while (changed);
        var retained = ImmutableHashSet.CreateBuilder<BoundArrayCreationExpression>(ReferenceEqualityComparer.Instance);
        foreach (Point point in result._awaits.Values.Where(reachable.Contains))
            foreach (Symbol symbol in point.Live)
                if (point.Arrays.TryGetValue(symbol, out var arrays)) retained.UnionWith(arrays);
        result.RetainedArrays = retained.ToImmutable();
        var structure = (StructTypeSymbol)result.FrameType.ElementType;
        structure.SetFields(result.LiveAcross.OfType<VariableSymbol>().Select((variable, ordinal) =>
            new FieldSymbol(variable.Name, structure, variable.Type, ordinal, Accessibility.Private,
                false, false, false, false, null)).ToImmutableArray());
        return result;
    }

    public static bool TryGetConstantLength(BoundArrayCreationExpression array, out ulong length)
    {
        BigInteger count = 1;
        var constants = new ConstantEvaluationContext(null);
        foreach (BoundExpression dimension in array.Dimensions)
        {
            if (!constants.TryFold(dimension, out object? value)) { length = 0; return false; }
            BigInteger size = SemanticAnalyzer.ToInteger(value);
            if (size < 0) { length = 0; return false; }
            count *= size;
        }
        length = count <= int.MaxValue ? (ulong)count : 0;
        return count <= int.MaxValue;
    }

    private HashSet<Symbol> ExpandOwners(IEnumerable<Symbol> live)
    {
        var result = live.ToHashSet();
        var queue = new Queue<Symbol>(result);
        while (queue.TryDequeue(out Symbol? symbol))
            if (_owners.TryGetValue(symbol, out HashSet<Symbol>? owners))
                foreach (Symbol owner in owners) if (result.Add(owner)) queue.Enqueue(owner);
        return result;
    }
    private Point New(params Point[] next)
    {
        var point = new Point(); point.Next.AddRange(next); _points.Add(point); return point;
    }
    private IEnumerable<Symbol> Uses(BoundNode node) => BoundTree.DescendantsAndSelf(node).SelectMany(item => item switch
    {
        BoundVariableExpression variable => new Symbol[] { variable.Variable },
        BoundThisExpression => new Symbol[] { _function },
        _ => Array.Empty<Symbol>(),
    });
    internal static bool CarriesBorrow(TypeSymbol type) => CarriesBorrow(type, []);
    private static bool CarriesBorrow(TypeSymbol type, HashSet<TypeSymbol> visited) =>
        type is ReferenceTypeSymbol or ArrayTypeSymbol or FunctionValueTypeSymbol ||
        type is LifetimeModifierTypeSymbol modifier && CarriesBorrow(modifier.ElementType, visited) ||
        type is IFieldStorageTypeSymbol structure && visited.Add(type) &&
            structure.AllInstanceFields.Any(field => CarriesBorrow(field.Type, visited));
    private void Owners(VariableSymbol variable, BoundExpression expression)
    {
        var dependencies = expression.LifetimeDependencies.Origins;
        if (!CarriesBorrow(variable.Type) && dependencies.IsEmpty) return;
        if (!_owners.TryGetValue(variable, out HashSet<Symbol>? owners)) _owners[variable] = owners = [];
        owners.UnionWith(dependencies.Where(origin => origin.LocalOwner is not null).Select(origin => origin.LocalOwner!));
        owners.UnionWith(Uses(expression));
    }
    private Point Cleanup(IEnumerable<Symbol> locals, Point next)
    {
        var point = New(next); point.Uses.UnionWith(locals); return point;
    }
    private Point Statement(BoundStatement statement, Point next, Flow flow)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
            {
                Symbol[] locals = block.Statements.OfType<BoundVariableDeclarationStatement>()
                    .Where(item => TypeFacts.RequiresDestruction(item.Variable.Type) ||
                        item.Variable.Type is ArrayTypeSymbol array && TypeFacts.RequiresDestruction(array.ElementType))
                    .Select(item => (Symbol)item.Variable).ToArray();
                Point tail = block.ExitCleanup is null ? next : Expression(block.ExitCleanup, next, flow);
                tail = Cleanup(locals, tail);
                for (int i = block.Statements.Length - 1; i >= 0; i--)
                {
                    HashSet<Symbol> active = block.Statements.Take(i).OfType<BoundVariableDeclarationStatement>()
                        .Select(item => (Symbol)item.Variable).Intersect(locals).ToHashSet();
                    var inner = new Flow(Cleanup(active, flow.Return), Cleanup(active, flow.Throw),
                        flow.Break is null ? null : Cleanup(active, flow.Break),
                        flow.Continue is null ? null : Cleanup(active, flow.Continue));
                    tail = Statement(block.Statements[i], tail, inner);
                }
                return tail;
            }
            case BoundVariableDeclarationStatement declaration:
            {
                Point define = New(next); define.Defines.Add(declaration.Variable);
                define.AssignedVariable = declaration.Variable; define.AssignedValue = declaration.Initializer;
                if (declaration.Initializer is null) return define;
                Owners(declaration.Variable, declaration.Initializer);
                return Expression(declaration.Initializer, define, flow);
            }
            case BoundExpressionStatement expression: return Expression(expression.Expression, next, flow);
            case BoundReturnStatement returned: return returned.Expression is null ? flow.Return : Expression(returned.Expression, flow.Return, flow);
            case BoundThrowStatement thrown: return thrown.Expression is null ? flow.Throw : Expression(thrown.Expression, flow.Throw, flow);
            case BoundBreakStatement: return flow.Break ?? next;
            case BoundContinueStatement: return flow.Continue ?? next;
            case BoundIfStatement conditional:
                return Expression(conditional.Condition, New(Statement(conditional.ThenStatement, next, flow),
                    conditional.ElseStatement is null ? next : Statement(conditional.ElseStatement, next, flow)), flow);
            case BoundWhileStatement loop:
            {
                Point header = New();
                Point body = Statement(loop.Body, header, flow with { Break = next, Continue = header });
                header.Next.Add(Expression(loop.Condition, New(body, next), flow));
                return header;
            }
            case BoundForStatement loop:
            {
                Point header = New();
                Point increment = loop.Increment is null ? header : Expression(loop.Increment, header, flow);
                Point body = Statement(loop.Body, increment, flow with { Break = next, Continue = increment });
                header.Next.Add(loop.Condition is null ? body : Expression(loop.Condition, New(body, next), flow));
                return loop.Initializer is null ? header : Statement(loop.Initializer, header, flow);
            }
            case BoundSwitchStatement selection:
            {
                Point dispatch = New(next);
                foreach (BoundSwitchSection section in selection.Sections)
                    dispatch.Next.Add(Statement(section.Body, next, flow with { Break = next }));
                return Expression(selection.Expression, dispatch, flow);
            }
            case BoundTryStatement region:
            {
                Point Finalize(Point destination) => region.FinallyBody is null ? destination : Statement(region.FinallyBody, destination, flow);
                var inner = new Flow(Finalize(flow.Return), Finalize(flow.Throw),
                    flow.Break is null ? null : Finalize(flow.Break), flow.Continue is null ? null : Finalize(flow.Continue));
                Point end = Finalize(next);
                Point dispatch = New(inner.Throw);
                foreach (BoundCatchClause handler in region.Catches) dispatch.Next.Add(Statement(handler.Body, end, inner));
                return Statement(region.Body, end, inner with { Throw = dispatch });
            }
            default: return next;
        }
    }
    private Point Expression(BoundExpression expression, Point next, Flow flow)
    {
        if (expression is BoundVariableExpression variable) { var read = New(next); read.Uses.Add(variable.Variable); return read; }
        if (expression is BoundThisExpression) { var read = New(next); read.Uses.Add(_function); return read; }
        if (expression is BoundAwaitExpression suspension)
        {
            Point retry = New(next, flow.Throw);
            if (suspension.Operand is BoundVariableExpression or BoundReferenceDereferenceExpression or BoundMemberAccessExpression ||
                CarriesBorrow(suspension.Operand.Type)) retry.Uses.UnionWith(Uses(suspension.Operand));
            _awaits[suspension] = retry;
            return Expression(suspension.Operand, retry, flow);
        }
        if (expression is BoundAssignmentExpression { Target: BoundVariableExpression target, OperatorKind: SyntaxKind.EqualsToken } assignment &&
            target.Type is not ReferenceTypeSymbol)
        {
            Owners(target.Variable, assignment.Expression);
            Point define = New(next); define.Defines.Add(target.Variable);
            define.AssignedVariable = target.Variable; define.AssignedValue = assignment.Expression;
            return Expression(assignment.Expression, define, flow);
        }
        Point operation = New(next);
        if (_arrayTemporaries.TryGetValue(expression, out LocalVariableSymbol? temporary))
        {
            operation.Defines.Add(temporary);
            operation.AssignedVariable = temporary;
            operation.AssignedValue = expression;
        }
        if (expression is BoundCallExpression or BoundMethodCallExpression or BoundInterfaceMethodCallExpression or
            BoundFunctionValueCallExpression or BoundIndirectCallExpression or BoundConstructorCallExpression or BoundNewExpression or
            BoundStorageConstructExpression or BoundArrayCreationExpression or BoundExplicitDestructExpression or
            BoundPropertySetExpression or BoundIndexerSetExpression or BoundCompoundAccessorAssignmentExpression or
            BoundDeferredGenericOperationExpression or BoundDeferredGenericMethodCallExpression)
        {
            operation.Next.Add(flow.Throw);
            foreach (BoundExpression child in BoundTree.Children(expression).OfType<BoundExpression>())
                if (CarriesBorrow(child.Type)) operation.Uses.UnionWith(Uses(child));
            if (expression is BoundMethodCallExpression method) operation.Uses.UnionWith(Uses(method.Receiver));
        }
        IEnumerable<BoundNode> children = expression is BoundFullExpression full ? new[] { full.Expression } : BoundTree.Children(expression);
        BoundExpression[] operands = children.OfType<BoundExpression>().ToArray();
        foreach (BoundExpression child in operands)
            if (child.Type is ArrayTypeSymbol && child is not BoundVariableExpression)
            {
                if (!_arrayTemporaries.TryGetValue(child, out LocalVariableSymbol? value))
                    _arrayTemporaries[child] = value = new LocalVariableSymbol($"__array_temporary_{_arrayTemporaries.Count}", child.Type, _function);
                operation.Uses.Add(value);
            }
        // Full-expression temporaries remain alive through later operand evaluation.
        if (expression is BoundFullExpression lifetime)
            foreach (BoundFullExpressionTemporary value in lifetime.Temporaries)
                if (CarriesBorrow(value.Value.Type)) operation.Uses.UnionWith(Uses(value.Value));
        foreach (BoundExpression child in operands.Reverse()) operation = Expression(child, operation, flow);
        return operation;
    }
}
