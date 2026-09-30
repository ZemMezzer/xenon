using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>Infers value lifetime contracts to a fixed point, then checks destination regions.</summary>
internal sealed class ValueLifetimeAnalyzer
{
    private sealed record Region(Region? Parent, int Order);
    private sealed class Value
    {
        public HashSet<ValueLifetimeDependency> Dependencies { get; } = [];
        public Dictionary<string, Value> Fields { get; } = [];
        public HashSet<ValueLifetimeDependency> BorrowedStorage { get; } = [];
        // Backing allocation lifetime is transferable; element borrows are not.
        public HashSet<ValueLifetimeDependency> StackArrayBacking { get; } = [];
        public HashSet<BoundExpression> Operations { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<FunctionSymbol, ImmutableArray<Value>> Callables { get; } = new(ReferenceEqualityComparer.Instance);
        public Value Copy() { var value = new Value(); value.Add(this); return value; }
        public void Add(Value value, bool includeStackArrayBacking = true)
        {
            foreach (var field in value.Fields)
                Fields[field.Key] = Fields.TryGetValue(field.Key, out var previousField) ? Union([previousField, field.Value]) : field.Value;
            Dependencies.UnionWith(value.Dependencies); BorrowedStorage.UnionWith(value.BorrowedStorage); Operations.UnionWith(value.Operations);
            if (includeStackArrayBacking) StackArrayBacking.UnionWith(value.StackArrayBacking);
            foreach (var pair in value.Callables)
                Callables[pair.Key] = Callables.TryGetValue(pair.Key, out var previous)
                    ? previous.Zip(pair.Value, (left, right) => Union([left, right])).ToImmutableArray() : pair.Value;
        }
        public static Value Union(IEnumerable<Value> values) { var result = new Value(); foreach (var value in values) result.Add(value); return result; }
        public static Value From(LifetimeDependencyKind kind, int ordinal = -1)
        {
            var result = new Value(); var origin = new ValueLifetimeDependency(null, new(kind, ordinal));
            result.Dependencies.Add(origin);
            if (kind is LifetimeDependencyKind.ParameterBorrow or LifetimeDependencyKind.ReceiverBorrow or LifetimeDependencyKind.CaptureBorrow)
                result.BorrowedStorage.Add(origin);
            return result;
        }
        public static Value Local(Symbol owner) => new() { Dependencies = { new(owner, null) }, BorrowedStorage = { new(owner, null) } };
        public Value AsValue(TypeSymbol type)
        {
            var result = Copy();
            if (!ResumableFrameAnalysis.CarriesBorrow(type)) result.Dependencies.ExceptWith(result.BorrowedStorage);
            result.BorrowedStorage.Clear();
            return result;
        }
    }
    private sealed class Annotation { public ValueLifetimeDependencies Value = new([]); }
    private static readonly ConditionalWeakTable<BoundExpression, Annotation> Annotations = new();
    internal static ValueLifetimeDependencies GetDependencies(BoundExpression expression) =>
        Annotations.TryGetValue(expression, out var dependencies) ? dependencies.Value : new([]);

    private readonly IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>> _effects;
    private readonly List<TypeSymbol?> _handlers = [];
    private readonly FunctionSymbol _function;
    private readonly BoundBlockStatement _body;
    private readonly ResumableFrameAnalysis? _frame;
    private readonly DiagnosticBag? _diagnostics;
    private readonly IReadOnlyDictionary<BoundExpression, TextLocation> _locations;
    private readonly Dictionary<VariableSymbol, Region> _regions = [];
    private readonly Dictionary<BoundExpression, LocalVariableSymbol> _temporaries = new(ReferenceEqualityComparer.Instance);
    private Dictionary<VariableSymbol, Value> _values = [];
    private HashSet<BoundExpression> _completed = new(ReferenceEqualityComparer.Instance);
    private HashSet<BoundExpression> _started = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BoundExpression, Value> _pending = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<LifetimeDependency> _returns = [];
    private readonly HashSet<LifetimeStore> _stores = [];
    private readonly HashSet<(BoundExpression, string)> _reported = [];
    private readonly CancellationToken _cancellation;
    private bool _returnsOperation;
    private int _order;

    private ValueLifetimeAnalyzer(BoundFunction function, TypeFactory types, DiagnosticBag? diagnostics,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation,
        IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>> effects, ResumableFrameAnalysis? frame)
    {
        _effects = effects;
        _function = function.Symbol; _body = function.Body; _diagnostics = diagnostics;
        _locations = locations; _cancellation = cancellation;
        _frame = frame;
        Register(_body, new Region(null, 0));
        foreach (ParameterSymbol parameter in _function.Parameters)
        {
            Value value = Carries(parameter.Type) ? Value.From(LifetimeDependencyKind.ParameterValue, parameter.Ordinal) : new();
            if (parameter.Type is ReferenceTypeSymbol or ArrayTypeSymbol) value.Add(Value.From(LifetimeDependencyKind.ParameterBorrow, parameter.Ordinal));
            _values[parameter] = value;
        }
        foreach (CaptureVariableSymbol capture in _function.LambdaCaptures)
            _values[capture] = Value.From(capture.IsBorrow ? LifetimeDependencyKind.CaptureBorrow : LifetimeDependencyKind.CaptureValue, capture.Ordinal);
    }

    public static void Analyze(ImmutableArray<BoundFunction> functions, GenericImplementationStore generics,
        TypeFactory types, DiagnosticBag diagnostics, IReadOnlyDictionary<BoundExpression, TextLocation> locations,
        CancellationToken cancellation)
    {
        BoundFunction[] bodies = functions.Concat(generics.Functions.Where(pair => pair.Key.IsSourceDefined && pair.Value.PortableBody is not null)
            .Select(pair => new BoundFunction(pair.Key, pair.Value.PortableBody!))).DistinctBy(function => function.Symbol).ToArray();
        // Only finalized bodies enter MIR; preliminary summary binding can
        // contain intentionally unresolved contextual/generic expressions.
        var frames = bodies.Where(body => body.Body.IsResumable).ToDictionary(body => body.Symbol,
            body => ResumableFrameAnalysis.Analyze(body.Symbol, body.Body, types));
        foreach (var frame in frames.Values)
            foreach (BoundArrayCreationExpression array in frame.RetainedArrays)
                if (array.Storage == ArrayStorageKind.Stack && !ResumableFrameAnalysis.TryGetConstantLength(array, out _))
                    diagnostics.Report(locations.GetValueOrDefault(array),
                        "runtime-sized array backing storage is live across await and has no fixed inline frame layout; use owning heap storage or finish its lifetime before suspension",
                        DiagnosticIds.BorrowAcrossAwait);
        var localFunctions = bodies.Select(body => body.Symbol).ToHashSet();
        IEnumerable<FunctionSymbol> BaseMethods(FunctionSymbol method) => method.IsOverride && method.ContainingSymbol is StructTypeSymbol { BaseType: { } parent }
            ? parent.FindMethods(method.Name).Where(method.Overrides) : [];
        var effects = ResumableExceptionAnalyzer.InferEffects(bodies, cancellation);
        bool changed;
        do
        {
            changed = false;
            foreach (BoundFunction body in bodies)
            {
                cancellation.ThrowIfCancellationRequested();
                var analysis = new ValueLifetimeAnalyzer(body, types, null, locations, cancellation, effects, frames.GetValueOrDefault(body.Symbol));
                analysis.Run();
                var returns = body.Symbol.ResultLifetimeDependencies.Union(analysis._returns).OrderBy(x => x.Kind).ThenBy(x => x.Ordinal).ThenBy(x => x.FieldPath).ToImmutableArray();
                var stores = body.Symbol.LifetimeStores.Union(analysis._stores).OrderBy(x => x.Destination).ThenBy(x => x.Source.Kind).ThenBy(x => x.Source.Ordinal).ThenBy(x => x.Source.FieldPath).ThenBy(x => x.FieldPath).ToImmutableArray();
                bool operation = body.Body.IsResumable || analysis._returnsOperation || body.Symbol.ReturnsResumableOperation;
                changed |= !returns.SequenceEqual(body.Symbol.ResultLifetimeDependencies) || !stores.SequenceEqual(body.Symbol.LifetimeStores) || operation != body.Symbol.ReturnsResumableOperation;
                body.Symbol.ResultLifetimeDependencies = returns;
                body.Symbol.LifetimeStores = stores;
                body.Symbol.ReturnsResumableOperation = operation;
                body.Symbol.CreatesResumableOperation = body.Body.IsResumable;
            }
            foreach (BoundFunction body in bodies)
                foreach (FunctionSymbol inherited in BaseMethods(body.Symbol).Where(localFunctions.Contains))
                {
                    var merged = inherited.ResultLifetimeDependencies.Union(body.Symbol.ResultLifetimeDependencies).OrderBy(x => x.Kind).ThenBy(x => x.Ordinal).ThenBy(x => x.FieldPath).ToImmutableArray();
                    var stores = inherited.LifetimeStores.Union(body.Symbol.LifetimeStores).OrderBy(x => x.Destination).ThenBy(x => x.Source.Kind).ThenBy(x => x.Source.Ordinal).ThenBy(x => x.Source.FieldPath).ThenBy(x => x.FieldPath).ToImmutableArray();
                    bool operation = inherited.ReturnsResumableOperation || body.Symbol.ReturnsResumableOperation;
                    changed |= !merged.SequenceEqual(inherited.ResultLifetimeDependencies) || !stores.SequenceEqual(inherited.LifetimeStores) || operation != inherited.ReturnsResumableOperation;
                    inherited.ResultLifetimeDependencies = merged; inherited.LifetimeStores = stores; inherited.ReturnsResumableOperation = operation;
                    inherited.CreatesResumableOperation |= body.Symbol.CreatesResumableOperation;
                }
        } while (changed);
        foreach (BoundFunction body in bodies)
        {
            cancellation.ThrowIfCancellationRequested();
            var analysis = new ValueLifetimeAnalyzer(body, types, diagnostics, locations, cancellation, effects, frames.GetValueOrDefault(body.Symbol));
            analysis.Run();
            foreach (FunctionSymbol inherited in BaseMethods(body.Symbol).Where(method => !localFunctions.Contains(method)))
                if (body.Symbol.ResultLifetimeDependencies.Except(inherited.ResultLifetimeDependencies).Any() ||
                    body.Symbol.LifetimeStores.Except(inherited.LifetimeStores).Any())
                    analysis.Report(new BoundVariableExpression(new LocalVariableSymbol("override", body.Symbol.ReturnType, body.Symbol)),
                        "override introduces a lifetime dependency absent from the imported base method contract", DiagnosticIds.ValueLifetimeEscape);
        }
    }

    private void Run()
    {
        if (_body.IsResumable && _body.Statements.Length > 1 && _body.Statements[1] is BoundTryStatement wrapper)
        {
            Statement(_body.Statements[0]);
            Statement(wrapper.Body);
        }
        else Statement(_body);
        foreach (LocalVariableSymbol temporary in _temporaries.Values) CheckPending(temporary);
        // Discarding a handle does not stop its operation. Export that escape as
        // an ordinary store effect so wrappers cannot hide a detached borrow.
        foreach (var pending in _pending.Where(pair => _started.Contains(pair.Key) && !_completed.Contains(pair.Key)))
            foreach (var dependency in pending.Value.Dependencies)
                if (dependency.Input is { } input) _stores.Add(new(-2, input));
    }

    private void Register(BoundStatement statement, Region region)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                var inner = new Region(region, ++_order);
                foreach (BoundStatement child in block.Statements) Register(child, inner);
                break;
            case BoundVariableDeclarationStatement local: _regions[local.Variable] = new Region(region, ++_order); break;
            default:
                foreach (BoundStatement child in BoundTree.Children(statement).OfType<BoundStatement>()) Register(child, region);
                break;
        }
    }
    private static bool Carries(TypeSymbol type) => type is not PointerTypeSymbol &&
        (type is GenericParameterSymbol or StructTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol or FunctionValueTypeSymbol or InterfaceTypeSymbol or LifetimeModifierTypeSymbol or OwnershipTypeSymbol);
    private Value Read(VariableSymbol variable) => _values.TryGetValue(variable, out Value? value) ? value.Copy() : new();
    private static BoundExpression Unwrap(BoundExpression expression) => expression switch
    {
        BoundFullExpression full => Unwrap(full.Expression), BoundCopyExpression copy => Unwrap(copy.Source),
        BoundMoveExpression move => Unwrap(move.Source), BoundLifetimeValueExpression value => Unwrap(value.Source),
        BoundReferenceConversionExpression reference => Unwrap(reference.Source),
        BoundReferenceDereferenceExpression reference => Unwrap(reference.Reference),
        BoundCastExpression cast => Unwrap(cast.Expression), _ => expression,
    };
    private VariableSymbol? Root(BoundExpression expression) => Unwrap(expression) switch
    {
        BoundVariableExpression variable => variable.Variable,
        BoundMemberAccessExpression member => Root(member.Receiver),
        BoundIndexExpression index => Root(index.Receiver),
        BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } pointer => Root(pointer.Operand),
        _ => null,
    };
    private Value Borrow(BoundExpression expression)
    {
        expression = Unwrap(expression);
        if (expression.Type is PointerTypeSymbol && expression is not BoundThisExpression) return new();
        switch (expression)
        {
            case BoundThisExpression: return Value.From(LifetimeDependencyKind.ReceiverBorrow);
            case BoundInterfaceConversionExpression conversion: return Borrow(conversion.Source);
            case BoundStaticFieldExpression: return new();
            case BoundVariableExpression variable:
                if (variable.Variable is ParameterSymbol parameter)
                    return parameter.Type is ReferenceTypeSymbol or ArrayTypeSymbol
                        ? Read(parameter) : Value.Local(parameter);
                if (variable.Variable is CaptureVariableSymbol capture)
                    return capture.IsBorrow ? Value.From(LifetimeDependencyKind.CaptureBorrow, capture.Ordinal) : Value.Local(capture);
                Value value = Read(variable.Variable);
                if (variable.Type is ReferenceTypeSymbol or ArrayTypeSymbol && value.Dependencies.Count > 0) return value;
                value.Add(Value.Local(variable.Variable)); return value;
            case BoundMemberAccessExpression member:
                return member.IsPointerAccess && member.Receiver is not BoundThisExpression && member.Receiver.Type is PointerTypeSymbol
                    ? new() : Borrow(member.Receiver);
            case BoundIndexExpression index: return index.Receiver.Type is PointerTypeSymbol ? new() : Borrow(index.Receiver);
            case BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } dereference:
                return dereference.Operand.Type is PointerTypeSymbol ? new() : Borrow(dereference.Operand);
            case BoundCallExpression call when call.Type is ReferenceTypeSymbol: return ReferenceResult(call.Function, call.Arguments, null);
            case BoundMethodCallExpression call when call.Type is ReferenceTypeSymbol: return ReferenceResult(call.Method, call.Arguments, call.Receiver);
            default:
                Value temporary = Eval(expression);
                if (!_temporaries.TryGetValue(expression, out var symbol))
                    _temporaries[expression] = symbol = new LocalVariableSymbol("temporary", expression.Type, _function);
                temporary.Add(Value.Local(symbol));
                return temporary;
        }
    }
    private Value ReferenceResult(FunctionSymbol function, ImmutableArray<BoundExpression> arguments, BoundExpression? receiver)
    {
        var result = new Value();
        foreach (ReferenceReturnOrigin origin in function.ReferenceReturnOrigins)
            if (origin.Kind == ReferenceReturnOriginKind.Parameter && origin.ParameterOrdinal >= 0 && origin.ParameterOrdinal < arguments.Length)
                result.Add(Borrow(arguments[origin.ParameterOrdinal]));
            else if (origin.Kind == ReferenceReturnOriginKind.Receiver && receiver is not null) result.Add(Borrow(receiver));
        return result;
    }
    private Value Map(LifetimeDependency source, ImmutableArray<BoundExpression> arguments, ImmutableArray<Value> values,
        BoundExpression? receiver, Value? receiverValue, ImmutableArray<Value> captures)
    {
        Value result = source.Kind switch
        {
            LifetimeDependencyKind.ParameterValue when source.Ordinal >= 0 && source.Ordinal < values.Length => values[source.Ordinal].AsValue(arguments[source.Ordinal].Type is ReferenceTypeSymbol reference ? reference.ElementType : arguments[source.Ordinal].Type),
            LifetimeDependencyKind.ParameterBorrow when source.Ordinal >= 0 && source.Ordinal < arguments.Length => Borrow(arguments[source.Ordinal]),
            LifetimeDependencyKind.ReceiverValue => receiverValue?.Copy() ?? new(),
            LifetimeDependencyKind.ReceiverBorrow when receiver is not null => Borrow(receiver),
            LifetimeDependencyKind.CaptureValue or LifetimeDependencyKind.CaptureBorrow when source.Ordinal >= 0 && source.Ordinal < captures.Length => captures[source.Ordinal].Copy(),
            _ => new(),
        };
        if (source.FieldPath.Length > 0)
            foreach (string field in source.FieldPath.Split('/'))
                result = Project(result, field);
        return result;
    }
    private static Value Project(Value source, string field)
    {
        if (source.Fields.TryGetValue(field, out var known)) return known.Copy();
        Value projected = source.Copy(); projected.Operations.Clear(); projected.Fields.Clear(); projected.StackArrayBacking.Clear();
        projected.Dependencies.Clear();
        foreach (var dependency in source.Dependencies)
        {
            if (dependency.Input is { Kind: LifetimeDependencyKind.ParameterValue or LifetimeDependencyKind.ReceiverValue or LifetimeDependencyKind.CaptureValue } input)
            {
                string path = input.FieldPath;
                path = field == "*" || path == "*" || path.Count(c => c == '/') >= 7 ? "*" : path.Length == 0 ? field : path + "/" + field;
                projected.Dependencies.Add(dependency with { Input = input with { FieldPath = path } });
            }
            else projected.Dependencies.Add(dependency);
        }
        return projected;
    }
    private Value Call(BoundExpression site, FunctionSymbol function, ImmutableArray<BoundExpression> arguments,
        BoundExpression? receiver = null, ImmutableArray<Value> captures = default)
    {
        Value? receiverValue = receiver is null ? null : Eval(receiver);
        ImmutableArray<Value> values = arguments.Select(Eval).ToImmutableArray();
        CheckExceptionalExit(function, site);
        var result = Value.Union(function.ResultLifetimeDependencies.Select(source => Map(source, arguments, values, receiver, receiverValue, captures)));
        if (function.ReturnType is ReferenceTypeSymbol) result.Add(ReferenceResult(function, arguments, receiver));
        foreach (LifetimeStore store in function.LifetimeStores)
        {
            Value value = Map(store.Source, arguments, values, receiver, receiverValue, captures);
            BoundExpression? destination = store.Destination == -1 ? receiver : store.Destination >= 0 && store.Destination < arguments.Length ? arguments[store.Destination] : null;
            if (function.FunctionKind == FunctionKind.Constructor && store.Destination == -1)
            { result.Add(value); if (store.FieldPath.Length > 0) result.Fields[store.FieldPath] = value; }
            else Store(destination, value, site, replace: false, fieldPath: store.FieldPath);
        }
        if (function.ReturnType is not ReferenceTypeSymbol) result.BorrowedStorage.Clear();
        // A newly started coroutine is a distinct operation; retaining another
        // handle does not prove that the new coroutine completes that handle.
        if (function.CreatesResumableOperation) { result.Operations.Clear(); result.Fields.Clear(); }
        if (function.ReturnsResumableOperation && result.Dependencies.Count > 0)
        {
            result.Operations.Add(site); _started.Add(site); _completed.Remove(site); _pending[site] = result.Copy();
        }
        return result;
    }
    private void CheckExceptionalExit(FunctionSymbol? function, BoundExpression site)
    {
        if (function is null || function.IsExtern) return;
        IEnumerable<TypeSymbol> errors = _effects.TryGetValue(function, out var effects) ? effects : [BuiltinTypes.Error];
        bool Handled(TypeSymbol error) => _handlers.Any(caught => caught is null || Matches(error, caught));
        static bool Matches(TypeSymbol error, TypeSymbol caught)
        {
            for (TypeSymbol? type = error; type is not null; type = (type as StructTypeSymbol)?.BaseType)
                if (TypeIdentity.Equals(type, caught)) return true;
            return false;
        }
        if (errors.All(Handled)) return;
        foreach (var pending in _pending.Where(pair => _started.Contains(pair.Key) && !_completed.Contains(pair.Key)))
            foreach (var dependency in pending.Value.Dependencies)
                if (dependency.LocalOwner is { } owner)
                    Report(site, $"exceptional exit can destroy '{owner.Name}' while a resumable operation still borrows it; complete that operation before this call or handle the exception", DiagnosticIds.PendingBorrowedOperation);
                else if (dependency.Input is { } input) _stores.Add(new(-2, input));
    }

    private Value Allocation(BoundExpression expression) => expression is BoundNewExpression allocation
        ? allocation.Constructor is { } constructor ? Call(expression, constructor, allocation.Arguments)
            : Value.Union(allocation.Arguments.Select(Eval))
        : Eval(expression);

    private Value Eval(BoundExpression expression)
    {
        _cancellation.ThrowIfCancellationRequested();
        Value result;
        switch (expression)
        {
            case BoundVariableExpression variable: result = Read(variable.Variable); break;
            case BoundThisExpression: result = Value.From(LifetimeDependencyKind.ReceiverValue); break;
            case BoundFullExpression full:
                var previousTemporaries = _temporaries.Values.ToHashSet();
                result = Eval(full.Expression);
                foreach (var temporary in _temporaries.Values.Where(value => !previousTemporaries.Contains(value))) CheckPending(temporary, expression);
                break;
            case BoundReferenceConversionExpression reference: result = Borrow(reference.Source); break;
            case BoundReferenceDereferenceExpression reference: result = Eval(reference.Reference).AsValue(reference.Type); break;
            case BoundMoveExpression move: CheckPending(move.TrackedVariable ?? Root(move.Source), expression); result = Eval(move.Source).AsValue(move.Type); break;
            case BoundCopyExpression copy: result = Eval(copy.Source); break;
            case BoundLifetimeValueExpression value: result = Eval(value.Source); break;
            case BoundStorageMoveExpression move: result = Eval(move.Storage); break;
            case BoundUniqueAdoptionExpression adoption: result = Allocation(adoption.Allocation); break;
            case BoundSharedAdoptionExpression adoption: result = Allocation(adoption.Allocation); break;
            case BoundCallExpression call: result = Call(expression, call.Function, call.Arguments); break;
            case BoundMethodCallExpression call: result = Call(expression, call.Method, call.Arguments, call.Receiver); break;
            case BoundInterfaceMethodCallExpression call:
                result = Call(expression, call.Method, call.Arguments, call.Receiver);
                result.Add(Value.Union(call.Arguments.Select(Eval))); result.Add(Eval(call.Receiver));
                // An erased implementation may retain its receiver. Its operation
                // result must carry that constraint even without an available body.
                if (call.Type is StructTypeSymbol operationType && operationType.Methods.Any(method => method.OperatorKind == OperatorKind.Resolve))
                {
                    result.Add(Borrow(call.Receiver));
                    foreach (var argument in call.Arguments.Where(argument => argument.Type is ReferenceTypeSymbol)) result.Add(Borrow(argument));
                    result.BorrowedStorage.Clear(); result.Operations.Clear(); result.Fields.Clear();
                    if (result.Dependencies.Count > 0)
                    { result.Operations.Add(expression); _started.Add(expression); _completed.Remove(expression); _pending[expression] = result.Copy(); }
                }
                break;
            case BoundConstructorCallExpression construction:
                result = Call(expression, construction.Constructor, construction.Arguments); break;
            case BoundFunctionValueExpression closure:
                ImmutableArray<Value> captures = closure.Captures.Select(capture => capture.Variable.IsBorrow ? Borrow(capture.Initializer) : Eval(capture.Initializer)).ToImmutableArray();
                result = Value.Union(captures); result.Callables[closure.InvokeFunction] = captures; break;
            case BoundFunctionValueCallExpression call:
                Value callable = Eval(call.Target); result = new();
                if (callable.Callables.Count > 0)
                    foreach (var target in callable.Callables) result.Add(Call(expression, target.Key, call.Arguments, captures: target.Value));
                else
                {
                    result.Add(callable); result.Add(Value.Union(call.Arguments.Select(Eval)));
                    if (Carries(call.Type) && result.Dependencies.Count > 0) { result.Operations.Add(expression); _started.Add(expression); _completed.Remove(expression); _pending[expression] = result.Copy(); }
                }
                break;
            case BoundAwaitExpression suspension:
                Value awaited = Eval(suspension.Operand);
                if (_frame is not null)
                {
                    var live = _frame.LiveValues.GetValueOrDefault(suspension, []);
                    foreach (VariableSymbol variable in live.OfType<VariableSymbol>()) AddExternal(Read(variable));
                    if (live.Contains(_function)) _returns.Add(new(LifetimeDependencyKind.ReceiverBorrow));
                    AddExternal(awaited);
                }
                _completed.UnionWith(awaited.Operations);
                result = awaited.Copy(); result.Operations.Clear();
                // A borrowed await result inherits provenance of its awaitable. The
                // storage-out protocol cannot erase the dependency at extraction.
                if (!ResumableFrameAnalysis.CarriesBorrow(suspension.Type)) result.Dependencies.Clear();
                break;
            case BoundAssignmentExpression assignment:
                result = Eval(assignment.Expression);
                TransferStackArrayBacking(assignment.Target, assignment.Expression, result);
                Store(assignment.Target, result, expression, assignment.Target is BoundVariableExpression);
                break;
            case BoundMemberAccessExpression member:
                result = Project(Eval(member.Receiver), member.Field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (member.Type is ReferenceTypeSymbol) result.Add(Borrow(member.Receiver));
                break;
            case BoundIndexExpression index:
                result = Eval(index.Receiver); result.Operations.Clear(); result.Fields.Clear(); result.StackArrayBacking.Clear();
                foreach (var item in index.Indices) _ = Eval(item); break;
            case BoundPropertySetExpression setter:
                result = Call(expression, setter.Property.Setter!, [setter.Value], setter.Receiver); break;
            case BoundIndexerSetExpression setter:
                result = Call(expression, setter.Indexer.Setter!, setter.Arguments.Add(setter.Value), setter.Receiver); break;
            case BoundStorageConstructExpression storage:
                result = storage.Value is null ? Value.Union(storage.Arguments.Select(Eval)) : Eval(storage.Value);
                Store(storage.Storage, result, expression, replace: true); break;
            case BoundExplicitDestructExpression destruction:
                CheckPending(Root(destruction.Target), expression); result = new(); break;
            case BoundArrayCreationExpression array:
                foreach (var dimension in array.Dimensions) _ = Eval(dimension);
                result = new(); break;
            case BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken, Operand: BoundThisExpression }:
                result = Value.From(LifetimeDependencyKind.ReceiverValue); break;
            case BoundUnaryExpression unary: result = Eval(unary.Operand); break;
            case BoundCastExpression cast: result = Eval(cast.Expression); break;
            case BoundDeferredGenericOperationExpression deferred when deferred.Requirement is FunctionSymbol function:
                result = Call(expression, function, deferred.Arguments, deferred.Receiver); break;
            default:
                result = Value.Union(BoundTree.Children(expression).OfType<BoundExpression>().Select(Eval)); break;
        }
        if (!Carries(expression.Type) && expression is not BoundThisExpression) result = new();
        if (_diagnostics is not null)
        {
            Annotations.GetValue(expression, _ => new()).Value = new(result.Dependencies.ToImmutableArray());
        }
        return result;
    }
    private static void TransferStackArrayBacking(BoundExpression target, BoundExpression source, Value value)
    {
        while (source is BoundFullExpression full) source = full.Expression;
        if (target is not BoundVariableExpression { Variable: LocalVariableSymbol { Type: ArrayTypeSymbol } destination } ||
            source is not BoundMoveExpression { Type: ArrayTypeSymbol } || value.StackArrayBacking.Count == 0)
            return;

        // The binder validates relocation and lowering transfers the cleanup node.
        // Retain a dependency on the new owner so subsequent borrows/async calls
        // cannot outlive it. Do not discard dependencies of borrowed elements.
        value.Dependencies.ExceptWith(value.StackArrayBacking);
        value.BorrowedStorage.ExceptWith(value.StackArrayBacking);
        value.StackArrayBacking.Clear();
        var backing = new ValueLifetimeDependency(destination, null);
        value.Dependencies.Add(backing);
        value.StackArrayBacking.Add(backing);
    }

    private void AddExternal(Value value)
    {
        foreach (var dependency in value.Dependencies)
            if (dependency.Input is { } input) _returns.Add(input);
    }
    private void Return(Value value, BoundExpression site)
    {
        if (!Carries(_function.ReturnType)) return;
        foreach (var dependency in value.Dependencies)
            if (dependency.Input is { } input) _returns.Add(input);
            else if (dependency.LocalOwner is { } local)
                Report(site, $"returned value retains a borrow of local '{local.Name}', which dies before the returned operation", DiagnosticIds.ValueLifetimeEscape);
        _returnsOperation |= value.Operations.Count > 0;
        _completed.UnionWith(value.Operations);
    }
    private bool Outlives(VariableSymbol owner, VariableSymbol destination)
    {
        if (owner is ParameterSymbol or CaptureVariableSymbol) return true;
        if (!_regions.TryGetValue(owner, out Region? source) || !_regions.TryGetValue(destination, out Region? target)) return false;
        for (Region? region = target.Parent; region is not null; region = region.Parent)
            if (region == source.Parent) return source.Order <= target.Order;
        return false;
    }
    private int Destination(BoundExpression? target)
    {
        if (target is null) return -2;
        if (Unwrap(target) is BoundThisExpression || target is BoundMemberAccessExpression { Receiver: BoundThisExpression }) return -1;
        return Root(target) is ParameterSymbol parameter && parameter.Type is ReferenceTypeSymbol ? parameter.Ordinal : -2;
    }
    private void Store(BoundExpression? target, Value value, BoundExpression site, bool replace, bool declaration = false, string fieldPath = "")
    {
        VariableSymbol? root = target is null ? null : Root(target);
        int destination = Destination(target);
        if (fieldPath.Length == 0 && target is BoundMemberAccessExpression member)
            fieldPath = member.Field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!declaration && root is LocalVariableSymbol { Type: ReferenceTypeSymbol })
        {
            var referents = Read(root).BorrowedStorage.ToArray();
            var referent = referents.Length == 1 ? referents[0] : default;
            root = referent.LocalOwner as VariableSymbol;
            if (referent.Input is { Kind: LifetimeDependencyKind.ParameterBorrow } parameter)
            { root = _function.Parameters[parameter.Ordinal]; destination = parameter.Ordinal; }
            else if (referent.Input is { Kind: LifetimeDependencyKind.ReceiverBorrow }) destination = -1;
        }
        if (!declaration) CheckPending(root, site);
        bool local = root is LocalVariableSymbol || root is ParameterSymbol { Type: not ReferenceTypeSymbol };
        foreach (var dependency in value.Dependencies)
        {
            if (dependency.LocalOwner is VariableSymbol owner && (!local || !Outlives(owner, root!)))
                Report(site, $"value assigned to '{root?.Name ?? "external storage"}' depends on local '{owner.Name}', which does not outlive the destination", DiagnosticIds.ValueLifetimeEscape);
            if (!local && dependency.Input is { } input) _stores.Add(new(destination, input, fieldPath));
        }
        if (root is not null)
        {
            if (replace) _values[root] = value.Copy();
            else
            {
                Value combined = Read(root);
                // A stored field/element can borrow another array, but that
                // array's allocation is not the destination's backing storage.
                combined.Add(value, includeStackArrayBacking: false);
                if (fieldPath.Length > 0) combined.Fields[fieldPath] = value.Copy();
                _values[root] = combined;
            }
        }
    }
    private void CheckPending(VariableSymbol? owner, BoundExpression? site = null)
    {
        if (owner is null) return;
        foreach (var pair in _pending)
            if (_started.Contains(pair.Key) && !_completed.Contains(pair.Key) && pair.Value.Dependencies.Any(dependency => dependency.LocalOwner == owner))
                Report(site ?? pair.Key, $"resumable operation borrowing '{owner.Name}' must complete before that owner is destroyed; await the operation on every exit", DiagnosticIds.PendingBorrowedOperation);
    }
    private Dictionary<VariableSymbol, Value> Snapshot() => _values.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
    private void Join(Dictionary<VariableSymbol, Value> values)
    {
        foreach (var pair in values)
        {
            if (!_values.TryGetValue(pair.Key, out Value? value)) _values[pair.Key] = pair.Value.Copy();
            else value.Add(pair.Value);
        }
    }
    private void Statement(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                foreach (BoundStatement child in block.Statements)
                {
                    Statement(child);
                    if (BoundControlFlow.TerminatesSection(child)) break;
                }
                if (block.ExitCleanup is not null) _ = Eval(block.ExitCleanup);
                foreach (var local in block.Statements.OfType<BoundVariableDeclarationStatement>().Reverse())
                {
                    CheckExceptionalExit(TypeFacts.GetCompleteDestructor(local.Variable.Type), local.Initializer ?? new BoundVariableExpression(local.Variable));
                    CheckPending(local.Variable);
                }
                break;
            case BoundVariableDeclarationStatement declaration:
                Value initialized = declaration.Initializer is null ? new() : Eval(declaration.Initializer);
                if (declaration.Variable.Type is ArrayTypeSymbol && declaration.Initializer is not null &&
                    BoundTree.DescendantsAndSelf(declaration.Initializer).OfType<BoundArrayCreationExpression>().Any(array => array.Storage == ArrayStorageKind.Stack))
                {
                    initialized.Add(Value.Local(declaration.Variable));
                    initialized.StackArrayBacking.Add(new(declaration.Variable, null));
                }
                if (declaration.Initializer is not null)
                    TransferStackArrayBacking(new BoundVariableExpression(declaration.Variable), declaration.Initializer, initialized);
                // Binding a readonly local reference materializes and extends a
                // temporary to that local's lexical lifetime under existing rules.
                if (declaration.Variable.Type is ReferenceTypeSymbol)
                    foreach (var owner in initialized.BorrowedStorage.Select(origin => origin.LocalOwner).OfType<LocalVariableSymbol>())
                        if (_temporaries.Values.Contains(owner)) _regions[owner] = _regions[declaration.Variable];
                Store(new BoundVariableExpression(declaration.Variable), initialized, declaration.Initializer ?? new BoundVariableExpression(declaration.Variable), true, declaration: true);
                break;
            case BoundExpressionStatement expression: _ = Eval(expression.Expression); break;
            case BoundReturnStatement { Expression: { } expression }:
                Value returned = Eval(expression);
                if (!_body.IsResumable) Return(returned, expression);
                else
                    foreach (BoundCallExpression completion in BoundTree.DescendantsAndSelf(expression).OfType<BoundCallExpression>().Where(call => call.Function.OperatorKind == OperatorKind.Resolve))
                        if (completion.Arguments.Length == 2) Return(Eval(completion.Arguments[1]), expression);
                foreach (VariableSymbol owner in _regions.Keys) CheckPending(owner, expression);
                break;
            case BoundThrowStatement thrown:
                if (thrown.Expression is not null) _ = Eval(thrown.Expression);
                foreach (VariableSymbol owner in _regions.Keys) CheckPending(owner, thrown.Expression);
                break;
            case BoundIfStatement conditional:
                _ = Eval(conditional.Condition);
                Branches([() => Statement(conditional.ThenStatement), () => { if (conditional.ElseStatement is not null) Statement(conditional.ElseStatement); }]);
                break;
            case BoundWhileStatement loop: Loop(loop.Condition, loop.Body, null); break;
            case BoundForStatement loop:
                if (loop.Initializer is not null) Statement(loop.Initializer);
                Loop(loop.Condition, loop.Body, loop.Increment); break;
            case BoundTryStatement region:
                var entry = Snapshot(); var entryCompleted = new HashSet<BoundExpression>(_completed, ReferenceEqualityComparer.Instance);
                int handlerCount = _handlers.Count;
                _handlers.AddRange(region.Catches.Select(handler => handler.Type));
                Statement(region.Body);
                _handlers.RemoveRange(handlerCount, _handlers.Count - handlerCount);
                var exits = Snapshot(); var exitCompleted = _completed;
                foreach (var handler in region.Catches)
                {
                    _values = entry.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
                    Join(exits); _completed = new(entryCompleted, ReferenceEqualityComparer.Instance);
                    Statement(handler.Body); Join(exits); exits = Snapshot(); exitCompleted.IntersectWith(_completed);
                }
                _values = exits; _completed = exitCompleted;
                if (region.FinallyBody is not null) Statement(region.FinallyBody); break;
            case BoundSwitchStatement selection:
                _ = Eval(selection.Expression);
                var paths = selection.Sections.Select(section => (Action)(() => Statement(section.Body))).ToList();
                if (!selection.Sections.Any(section => section.Value is null)) paths.Add(() => { });
                Branches(paths); break;
        }
    }
    private void Branches(IEnumerable<Action> paths)
    {
        var initial = Snapshot();
        var initiallyCompleted = new HashSet<BoundExpression>(_completed, ReferenceEqualityComparer.Instance);
        var initiallyStarted = new HashSet<BoundExpression>(_started, ReferenceEqualityComparer.Instance);
        Dictionary<VariableSymbol, Value>? joined = null;
        var done = new HashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        var started = new HashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        foreach (Action path in paths)
        {
            _values = initial.ToDictionary(pair => pair.Key, pair => pair.Value.Copy());
            _completed = new(initiallyCompleted, ReferenceEqualityComparer.Instance);
            _started = new(initiallyStarted, ReferenceEqualityComparer.Instance);
            path();
            if (joined is null) { joined = Snapshot(); done = new(_completed, ReferenceEqualityComparer.Instance); started = new(_started, ReferenceEqualityComparer.Instance); }
            else
            {
                Join(joined); joined = Snapshot();
                done.UnionWith(_started.Except(started));
                _completed.UnionWith(started.Except(_started));
                done.IntersectWith(_completed); started.UnionWith(_started);
            }
        }
        _values = joined ?? initial; _completed = done; _started = started;
    }

    private void Loop(BoundExpression? condition, BoundStatement body, BoundExpression? increment)
    {
        var completed = new HashSet<BoundExpression>(_completed, ReferenceEqualityComparer.Instance);
        var started = new HashSet<BoundExpression>(_started, ReferenceEqualityComparer.Instance);
        string before;
        do
        {
            _cancellation.ThrowIfCancellationRequested();
            before = Fingerprint(); var entry = Snapshot();
            if (condition is not null) _ = Eval(condition);
            Statement(body); if (increment is not null) _ = Eval(increment);
            Join(entry);
        } while (before != Fingerprint());
        _completed.ExceptWith(started.Except(completed));
    }
    private string Fingerprint() => string.Join(";", _values.OrderBy(pair => RuntimeHelpers.GetHashCode(pair.Key)).Select(pair =>
        $"{RuntimeHelpers.GetHashCode(pair.Key)}:{string.Join(',', pair.Value.Dependencies.OrderBy(x => x.Input?.Kind).ThenBy(x => x.Input?.Ordinal).ThenBy(x => x.LocalOwner?.Name))}"));
    private void Report(BoundExpression site, string message, string id)
    {
        if (_diagnostics is null || !_reported.Add((site, message))) return;
        TextLocation location = _locations.GetValueOrDefault(site);
        if (location.Source is null)
            foreach (BoundExpression child in BoundTree.DescendantsAndSelf(site).OfType<BoundExpression>())
                if (_locations.TryGetValue(child, out location)) break;
        if (location.Source is null) location = _function.Locations.FirstOrDefault();
        if (location.Source is null) location = _locations.Values.FirstOrDefault();
        if (location.Source is null) location = new TextLocation(SourceText.From("", "<library specialization>"), new TextSpan(0, 0));
        _diagnostics.Report(location, message, id);
    }
}
