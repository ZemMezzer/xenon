using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

// Readonly is an effect contract, independent of pointer/reference qualifiers.
// Track the storage reachable through values so copying a hidden pointer, taking
// its address, or storing it in a local aggregate cannot grant mutation rights.
// Exact locations use strong updates. Control-flow joins and summary locations
// retain all possible origins, including loop back-edges and ambiguous aliases.
internal sealed partial class ReadonlyEffectAnalyzer(
    FunctionSymbol function,
    DiagnosticBag diagnostics,
    IReadOnlyDictionary<BoundExpression, TextLocation> locations,
    TextLocation fallbackLocation,
    IReadOnlyDictionary<FunctionSymbol, BoundBlockStatement> bodies,
    ImmutableArray<StructTypeSymbol> types,
    CancellationToken cancellationToken)
{
    private readonly object _hidden = new();
    private readonly object _external = new();
    private MemoryState _memory = new();
    private readonly HashSet<object> _summaryLocations = new(ReferenceEqualityComparer.Instance);
    // Intern each (parent location, field symbol) so aliases share the same
    // field path, while siblings and fields in separate value copies do not.
    private readonly Dictionary<object, Dictionary<FieldSymbol, object>> _fields = new(ReferenceEqualityComparer.Instance);
    private readonly EvaluationContext _rootContext = new();
    private EvaluationContext _context = null!;
    private readonly Dictionary<object, UncertainLocation> _uncertainLocations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionSymbol, RecursiveFrame> _activeCalls = [];
    private readonly HashSet<(TextLocation Location, string Message)> _reported = [];
    private readonly HashSet<StructTypeSymbol> _initializing = [];
    private object? _initializerReceiver;
    private TextLocation _location = fallbackLocation;

    public void Analyze(BoundBlockStatement body)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _context = _rootContext;
        _context.Receiver.Add(_hidden);
        foreach (ParameterSymbol parameter in function.Parameters)
        {
            if (ContainsAccess(parameter.Type) || parameter.Type is IFieldStorageTypeSymbol)
                StoreValue([Root(parameter)], [IsMutableParameter(parameter.Type) ? _external : _hidden], parameter.Type);
        }

        Flow result = Visit(body);
        if (result.Return is { } returnedState && _context.ReturnSite is { } site)
        {
            _memory = returnedState;
            if (HasHiddenAccess(_context.Returned, function.ReturnType))
                Report(site, "cannot return a mutable capability obtained from hidden state",
                    DiagnosticIds.MutableCapabilityReturn);
        }
    }

    private HashSet<object> Evaluate(BoundExpression expression)
    {
        TextLocation previous = _location;
        if (locations.TryGetValue(expression, out TextLocation location)) _location = location;
        try { return EvaluateCore(expression); }
        finally { _location = previous; }
    }

    private HashSet<object> EvaluateCore(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundErrorExpression or BoundTypeLayoutExpression or BoundDeferredConstantExpression:
                return [];
            case BoundLiteralExpression literal:
                return literal.Value is not null && ContainsAccess(literal.Type) ? [_hidden] : [];
            case BoundVariableExpression variable:
                return Read([Root(variable.Variable)], variable.Type);
            case BoundMoveExpression move:
                return Evaluate(move.Source);
            case BoundCopyExpression copy:
                return Evaluate(copy.Source);
            case BoundThisExpression:
                return _initializerReceiver is { } receiver ? [receiver] : new(_context.Receiver);
            case BoundStaticFieldExpression field:
                return ContainsAccess(field.Type) ? [_hidden] : [];
            case BoundMemberAccessExpression member:
                return Read(Address(member), member.Type);
            case BoundReferenceConversionExpression conversion:
                return conversion.Source is BoundThisExpression
                    ? Evaluate(conversion.Source) : Address(conversion.Source);
            case BoundReferenceDereferenceExpression dereference:
                return Read(Evaluate(dereference.Reference), dereference.Type);
            case BoundInterfaceConversionExpression conversion:
            {
                // The runtime object selects the interface map at conversion.
                if (!_context.InterfaceValues.TryGetValue(conversion, out var value))
                    _context.InterfaceValues.Add(conversion, value = new(conversion.SourceType));
                if (_loopDepth != 0) _summaryLocations.Add(value);
                HashSet<object> source = Address(conversion.Source);
                StoreReceiverTypes([value], KnownReceiverTypes(source));
                Store([value], source, strong: true);
                return [value];
            }
            case BoundCastExpression cast:
                return Evaluate(cast.Expression);
            case BoundUnaryExpression unary:
            {
                if (unary.OperatorKind == SyntaxKind.AmpersandToken) return Address(unary.Operand);
                if (unary.OperatorKind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
                {
                    HashSet<object> address = Address(unary.Operand);
                    CheckWrite(address, unary);
                    HashSet<object> value = Read(address, unary.Type);
                    if (unary.Type is PointerTypeSymbol)
                    {
                        value = Uncertain(value);
                        StoreValue(address, value, unary.Type);
                    }
                    return value;
                }
                HashSet<object> operand = Evaluate(unary.Operand);
                if (unary.OperatorKind == SyntaxKind.StarToken) return Read(operand, unary.Type);
                return ContainsAccess(unary.Type) ? operand : [];
            }
            case BoundBinaryExpression binary:
            {
                HashSet<object> left = Evaluate(binary.Left);
                if (binary.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken)
                {
                    bool? known = BooleanConstant(binary.Left);
                    bool skip = binary.OperatorKind == SyntaxKind.AmpersandAmpersandToken ? known == false : known == true;
                    if (skip) return [];
                    MemoryState skipped = _memory.Copy();
                    Evaluate(binary.Right);
                    if (known is null) _memory = Join(skipped, _memory)!;
                    return [];
                }
                left.UnionWith(Evaluate(binary.Right));
                return binary.Type is PointerTypeSymbol ? Uncertain(left) : ContainsAccess(binary.Type) ? left : [];
            }
            case BoundAssignmentExpression assignment:
            {
                HashSet<object> target = Address(assignment.Target);
                HashSet<object> current = assignment.OperatorKind == SyntaxKind.EqualsToken ? [] : Read(target, assignment.Target.Type);
                HashSet<object> value = Capture(Evaluate(assignment.Expression), assignment.Expression.Type, assignment);
                if (assignment.OperatorKind != SyntaxKind.EqualsToken)
                {
                    value.UnionWith(current);
                    if (assignment.Target.Type is PointerTypeSymbol) value = Uncertain(value);
                }
                CheckWrite(target, assignment);
                if (target.Contains(_external) && ExposesWritableAccess(assignment.Target.Type) && HasHiddenAccess(value, assignment.Target.Type))
                    Report(assignment, "cannot store a mutable capability obtained from hidden state through an output parameter",
                        DiagnosticIds.MutableCapabilityOutputEscape);
                StoreValue(target, value, assignment.Target.Type);
                if (assignment.Target is BoundVariableExpression { Variable: LocalVariableSymbol local })
                    RegisterScalarCleanup(local);
                return value;
            }
            case BoundIndexExpression index:
                return Read(Address(index), index.Type);
            case BoundArrayMetadataExpression metadata:
                Evaluate(metadata.Receiver);
                if (metadata.Dimension is { } dimension) Evaluate(dimension);
                return [];
            case BoundCallExpression call:
                return IsAccessor(call.Function) && !call.Function.IsReadonly
                    ? ContextualDispatch(call.Function, call.Arguments, [], call)
                    : Call(call.Function, call.Arguments, call);
            case BoundMethodCallExpression call:
                return AccessorOrMethodCall(call.Method, call.Arguments, call.Receiver, call.IsPointerAccess, call);
            case BoundInterfaceMethodCallExpression call:
                return AccessorOrMethodCall(call.Method, call.Arguments, call.Receiver, call.IsPointerAccess, call);
            case BoundPropertySetExpression set:
                return AccessorOrMethodCall(set.Property.Setter!, [set.Value], set.Receiver, set.IsPointerAccess, set);
            case BoundInterfacePropertySetExpression set:
                return AccessorOrMethodCall(set.Property.Setter!, [set.Value], set.Receiver, set.IsPointerAccess, set);
            case BoundIndexerSetExpression set:
                return AccessorOrMethodCall(set.Indexer.Setter!, set.Arguments.Add(set.Value), set.Receiver, false, set);
            case BoundInterfaceIndexerSetExpression set:
                return AccessorOrMethodCall(set.Indexer.Setter!, set.Arguments.Add(set.Value), set.Receiver, false, set);
            case BoundCompoundAccessorAssignmentExpression set:
            {
                HashSet<object> targetReceiver = set.IsPointerAccess ? Evaluate(set.Receiver) : Address(set.Receiver);
                HashSet<StructTypeSymbol>? interfaceTypes = null;
                if (set.InterfaceType is { } interfaceType) targetReceiver = InterfaceReceiver(targetReceiver, interfaceType, out interfaceTypes);
                HashSet<object>[] arguments = EvaluateArguments(set.Arguments);
                HashSet<object> value = InvokeMember(set.Getter, arguments, targetReceiver, set, interfaceTypes);
                value.UnionWith(Evaluate(set.Value));
                return InvokeMember(set.Setter, [.. arguments, value], targetReceiver, set, interfaceTypes);
            }
            case BoundConstructorCallExpression construction:
                ResetConstruction(construction, construction.Type);
                ContextualCall(construction.Constructor, construction.Arguments, [Root(construction)], construction);
                return Read([Root(construction)], construction.Type);
            case BoundBaseLifecycleCallExpression call:
                return ContextualCall(call.Function, call.Arguments, _context.Receiver, call);
            case BoundDropFieldsExpression drop:
            {
                foreach (FieldSymbol field in drop.StructType.Fields.Reverse())
                    if (TypeFacts.GetDropFunction(field.Type) is { } fieldDrop)
                        ContextualCall(fieldDrop, Array.Empty<HashSet<object>>(),
                            Project(_context.Receiver, field), drop);
                if (drop.StructType.BaseType?.DropFunction is { } baseDrop)
                    ContextualCall(baseDrop, Array.Empty<HashSet<object>>(), _context.Receiver, drop);
                return [];
            }
            case BoundOwnershipDropExpression:
                return [];
            case BoundUniqueAdoptionExpression adoption:
                return Evaluate(adoption.Allocation);
            case BoundSharedAdoptionExpression adoption:
                return Evaluate(adoption.Allocation);
            case BoundWeakConversionExpression conversion:
                return Evaluate(conversion.Shared);
            case BoundWeakLockExpression weakLock:
                return Evaluate(weakLock.Weak);
            case BoundStructConstructionExpression construction:
                Initialize(construction.StructType, construction.Arguments, construction);
                return Read([Root(construction)], construction.Type);
            case BoundNewExpression allocation:
                ResetConstruction(allocation, allocation.StructType);
                if (allocation.Constructor is { } constructor)
                    ContextualCall(constructor, allocation.Arguments, [Root(allocation)], allocation);
                else
                    Initialize(allocation.StructType, allocation.Arguments, allocation);
                return [Root(allocation)];
            case BoundArrayCreationExpression allocation:
                foreach (BoundExpression length in allocation.Dimensions) Evaluate(length);
                _summaryLocations.Add(Root(allocation));
                if (allocation.ElementType is StructTypeSymbol element)
                {
                    // Each element runs its initializer. Allocations made by an
                    // initializer must not be mistaken for one exact object.
                    _loopDepth++;
                    try { Initialize(element, [], allocation); }
                    finally { _loopDepth--; }
                }
                if (allocation.Storage == ArrayStorageKind.Stack &&
                    TypeFacts.GetDropFunction(allocation.ElementType) is { } elementDestructor)
                {
                    object root = Root(allocation);
                    if (_cleanupScopes.TryPeek(out var cleanups))
                        RegisterCleanup(cleanups, new(root, elementDestructor, allocation));
                }
                InitializeArrayElements(allocation);
                return [Root(allocation)];
            case BoundFreeExpression free:
            {
                HashSet<object> pointer = Evaluate(free.Pointer);
                CheckWrite(pointer, free);
                if (free.Destructor is { } destructor)
                {
                    if (free.Pointer.Type is ArrayTypeSymbol) DestroyElements(destructor, pointer, free);
                    else ContextualDispatch(destructor, Array.Empty<HashSet<object>>(), pointer, free);
                }
                return [];
            }
            default:
                throw new InvalidOperationException($"Missing readonly effect analysis for '{expression.Kind}'.");
        }
    }

    private HashSet<object> Address(BoundExpression expression)
    {
        switch (expression)
        {
            case BoundThisExpression:
                return Evaluate(expression);
            case BoundVariableExpression variable:
                return [Root(variable.Variable)];
            case BoundStaticFieldExpression:
                return [_hidden];
            case BoundReferenceDereferenceExpression reference:
                return Evaluate(reference.Reference);
            case BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } pointer:
                return Evaluate(pointer.Operand);
            case BoundMemberAccessExpression member:
                return Project(member.IsPointerAccess ? Evaluate(member.Receiver) : Address(member.Receiver), member.Field);
            case BoundIndexExpression index:
            {
                HashSet<object> receiver = Evaluate(index.Receiver);
                foreach (BoundExpression argument in index.Indices) Evaluate(argument);
                return index.Receiver.Type is ArrayTypeSymbol or
                    UniqueTypeSymbol { ElementType: ArrayTypeSymbol } or
                    SharedTypeSymbol { ElementType: ArrayTypeSymbol }
                    ? ArrayElements(receiver, index.Indices)
                    : Uncertain(receiver);
            }
            default:
                // A reference may bind to a materialized struct/interface value.
                StoreValue([Root(expression)], Evaluate(expression), expression.Type);
                return [Root(expression)];
        }
    }

    private HashSet<object> Call(FunctionSymbol callee, ImmutableArray<BoundExpression> arguments, BoundExpression site)
        => Call(callee, EvaluateArguments(arguments), site);

    private HashSet<object>[] EvaluateArguments(ImmutableArray<BoundExpression> arguments) =>
        arguments.Select(argument => Capture(Evaluate(argument), argument.Type, argument)).ToArray();

    private HashSet<object> Call(FunctionSymbol callee, HashSet<object>[] arguments, BoundExpression site)
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

    private HashSet<object> AccessorOrMethodCall(FunctionSymbol callee,
        ImmutableArray<BoundExpression> arguments, BoundExpression receiver, bool pointerAccess, BoundExpression site)
    {
        HashSet<object> storage = pointerAccess ? Evaluate(receiver) : Address(receiver);
        HashSet<StructTypeSymbol>? interfaceTypes = null;
        if (callee.ContainingInterface is { } interfaceType) storage = InterfaceReceiver(storage, interfaceType, out interfaceTypes);
        return InvokeMember(callee, EvaluateArguments(arguments), storage, site, interfaceTypes);
    }

    private HashSet<object> InvokeMember(FunctionSymbol callee, HashSet<object>[] arguments, HashSet<object> receiver,
        BoundExpression site, HashSet<StructTypeSymbol>? interfaceTypes = null)
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

    private static bool IsAccessor(FunctionSymbol callee) =>
        callee.ContainingProperty is not null || callee.ContainingIndexer is not null ||
        callee.ContainingInterfaceProperty is not null || callee.ContainingInterfaceIndexer is not null;

    private HashSet<object> ContextualDispatch(FunctionSymbol callee,
        ImmutableArray<BoundExpression> arguments, HashSet<object> receiver, BoundExpression site)
        => ContextualDispatch(callee, EvaluateArguments(arguments), receiver, site);

    private HashSet<object> ContextualDispatch(FunctionSymbol callee,
        HashSet<object>[] arguments, HashSet<object> receiver, BoundExpression site, HashSet<StructTypeSymbol>? interfaceTypes = null)
    {
        if (callee.FunctionKind is FunctionKind.Destructor or FunctionKind.DropGlue or FunctionKind.OwnershipDrop)
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
                    ? type.DropFunction ?? target
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
        ImmutableArray<BoundExpression> arguments, HashSet<object> receiver, BoundExpression site)
        => ContextualCall(callee, EvaluateArguments(arguments), receiver, site);

    private HashSet<object> ContextualCall(FunctionSymbol callee,
        HashSet<object>[] values, HashSet<object> receiver, BoundExpression site)
    {
        // Mutable instance methods, lifecycle members and accessors use the actual receiver
        // and arguments, without inventing a readonly declaration for the member.
        if (!bodies.TryGetValue(callee, out BoundBlockStatement? body))
        {
            Report(site, $"cannot verify effects of member '{callee.Name}' without a body",
                DiagnosticIds.MissingReadonlyCalleeBody);
            return [_hidden];
        }
        if (_activeCalls.ContainsKey(callee))
            return RecursiveCall(callee, values, receiver, site);
        if (!_context.Calls.TryGetValue(callee, out var sites))
            _context.Calls.Add(callee, sites = new(ReferenceEqualityComparer.Instance));
        if (!sites.TryGetValue(site, out EvaluationContext? context))
            sites.Add(site, context = new());
        var frame = new RecursiveFrame(context, _memory.Copy(), new(receiver), values.Select(value => new HashSet<object>(value)).ToArray());
        _activeCalls.Add(callee, frame);
        EvaluationContext previous = _context;
        object? initializerReceiver = _initializerReceiver;
        _context = context;
        _initializerReceiver = null;
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
            _initializerReceiver = initializerReceiver;
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
        public BoundExpression? ReturnSite { get; set; }
        public Dictionary<object, object> Roots { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<object, object> Snapshots { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<BoundInterfaceConversionExpression, InterfaceValue> InterfaceValues { get; } = new(ReferenceEqualityComparer.Instance);
        public Dictionary<FunctionSymbol, Dictionary<BoundExpression, EvaluationContext>> Calls { get; } = [];
        public bool IsRecursive { get; set; }
    }

    private void Initialize(StructTypeSymbol type, ImmutableArray<BoundExpression> arguments, BoundExpression site)
    {
        ResetConstruction(site, type);
        if (!_initializing.Add(type))
        {
            StoreUnknown([Root(site)], [_hidden], type, []);
            return;
        }
        object? previous = _initializerReceiver;
        _initializerReceiver = Root(site);
        try
        {
            // Field initializers execute even when positional values overwrite them.
            foreach (FieldSymbol field in type.AllInstanceFields)
            {
                if (field.Initializer is { } initializer)
                    StoreValue(Project([Root(site)], field), Evaluate(initializer), field.Type);
            }
        }
        finally
        {
            _initializerReceiver = previous;
            _initializing.Remove(type);
        }
        // Positional arguments belong to the caller, not the object being built.
        for (int index = 0; index < arguments.Length; index++)
        {
            HashSet<object> value = Evaluate(arguments[index]);
            if (index < type.AllInstanceFields.Length)
            {
                FieldSymbol field = type.AllInstanceFields[index];
                StoreValue(Project([Root(site)], field), value, field.Type);
            }
        }
    }

    private HashSet<object> Read(IEnumerable<object> storage, TypeSymbol type)
    {
        HashSet<object> result = new(ReferenceEqualityComparer.Instance);
        // Aggregate values retain their field shape. StoreValue performs the
        // field-by-field copy; pointer/reference reads still load capabilities.
        if (type is IFieldStorageTypeSymbol) return new(storage, ReferenceEqualityComparer.Instance);
        if (!ContainsAccess(type)) return result;
        foreach (object location in storage)
        {
            object origin = Unwrap(location);
            if (ReferenceEquals(origin, _hidden) || ReferenceEquals(origin, _external)) result.Add(origin);
            else if (_memory.TryGetValue(origin, out HashSet<object>? contents)) result.UnionWith(contents);
        }
        return result;
    }

    private HashSet<object> Project(IEnumerable<object> storage, FieldSymbol field)
    {
        HashSet<object> result = new(ReferenceEqualityComparer.Instance);
        foreach (object parent in storage)
        {
            if (parent is UncertainLocation uncertain)
            {
                result.UnionWith(Uncertain(Project([uncertain.Origin], field)));
                continue;
            }
            // Unknown external objects keep their receiver provenance. Local
            // objects, including local aliases, have distinct storage per field.
            if (ReferenceEquals(parent, _hidden) || ReferenceEquals(parent, _external))
            {
                result.Add(parent);
                continue;
            }
            if (!_fields.TryGetValue(parent, out var fields))
                _fields.Add(parent, fields = []);
            if (!fields.TryGetValue(field, out object? location))
            {
                // A legal by-value field path cannot repeat the same field
                // symbol (that would require an infinite struct layout). Coarse
                // call summaries can form such recursive aliases; widen those
                // paths to the earlier location to keep the fixed point finite.
                for (object current = parent; current is FieldLocation ancestor; current = ancestor.Parent)
                {
                    if (!ReferenceEquals(ancestor.Field, field)) continue;
                    location = ancestor;
                    _summaryLocations.Add(location);
                    break;
                }
                fields.Add(field, location ??= new FieldLocation(parent, field));
            }
            result.Add(location);
        }
        return result;
    }

    private sealed class FieldLocation(object parent, FieldSymbol field)
    {
        public object Parent { get; } = parent;
        public FieldSymbol Field { get; } = field;
    }

    private void StoreValue(HashSet<object> storage, HashSet<object> values, TypeSymbol type) =>
        StoreValue(storage, values, type, []);

    private void StoreValue(HashSet<object> storage, HashSet<object> values, TypeSymbol type, HashSet<TypeSymbol> path)
    {
        if (type is not IFieldStorageTypeSymbol structure)
        {
            if (ContainsAccess(type)) Store(storage, values, strong: true);
            return;
        }
        if (type is StructTypeSymbol) StoreReceiverTypes(storage, KnownReceiverTypes(values));
        // Invalid recursive value layouts already have a binding diagnostic.
        if (!path.Add(type)) return;
        foreach (FieldSymbol field in structure.AllInstanceFields)
        {
            StoreValue(Project(storage, field), Read(Project(values, field), field.Type), field.Type, path);
        }
        path.Remove(type);
    }

    private void StoreUnknown(HashSet<object> storage, HashSet<object> capabilities, TypeSymbol type, HashSet<TypeSymbol> path)
    {
        ForgetReceiverTypes(storage);
        if (!ContainsAccess(type)) return;
        if (type is not IFieldStorageTypeSymbol structure)
        {
            Store(storage, capabilities);
            return;
        }
        if (!path.Add(type)) return;
        foreach (FieldSymbol field in structure.AllInstanceFields)
            StoreUnknown(Project(storage, field), capabilities, field.Type, path);
        path.Remove(type);
    }

    private void CollectReachableStorage(HashSet<object> storage, TypeSymbol type, HashSet<object> result,
        HashSet<(object, TypeSymbol)> visited, HashSet<TypeSymbol>? valuePath = null)
    {
        HashSet<object> local = [];
        foreach (object origin in storage)
        {
            // A valid readonly callee cannot export hidden writable access.
            if (ReferenceEquals(origin, _hidden)) continue;
            result.Add(origin);
            if (!ReferenceEquals(origin, _external) && visited.Add((origin, type))) local.Add(origin);
        }
        if (local.Count == 0) return;
        if (type is IFieldStorageTypeSymbol structure)
        {
            valuePath ??= [];
            if (!valuePath.Add(type)) return;
            foreach (FieldSymbol field in structure.AllInstanceFields)
                CollectReachableStorage(Project(local, field), field.Type, result, visited, valuePath);
            valuePath.Remove(type);
        }
        else if (type is ArrayTypeSymbol array)
            CollectReachableStorage(ArrayElements(Read(local, type)), array.ElementType, result, visited);
        else if (ElementType(type) is { } element)
            CollectReachableStorage(Read(local, type), element, result, visited);
        else if (type is InterfaceTypeSymbol)
        {
            foreach (object value in Read(local, type))
            {
                if (Unwrap(value) is InterfaceValue view)
                {
                    result.Add(view);
                    CollectReachableStorage(Read([view], type), view.SourceType, result, visited);
                }
                else CollectReachableStorage([value], type, result, visited);
            }
        }
    }

    private static TypeSymbol? ElementType(TypeSymbol type) => type switch
    {
        PointerTypeSymbol pointer => pointer.ElementType,
        ReferenceTypeSymbol reference => reference.ElementType,
        ArrayTypeSymbol array => array.ElementType,
        UniqueTypeSymbol unique => unique.ElementType,
        SharedTypeSymbol shared => shared.ElementType,
        WeakTypeSymbol weak => weak.ElementType,
        _ => null,
    };

    private object Root(object identity)
    {
        if (!_context.Roots.TryGetValue(identity, out object? root))
            _context.Roots.Add(identity, root = new object());
        if (_context.IsRecursive) _summaryLocations.Add(root);
        return root;
    }

    private void Store(IEnumerable<object> storage, HashSet<object> values, bool strong = false)
    {
        object[] destinations = storage.ToArray();
        bool replace = strong && destinations.Length == 1 && IsExact(destinations[0]);
        foreach (object destination in destinations)
        {
            object origin = Unwrap(destination);
            if (ReferenceEquals(origin, _hidden) || ReferenceEquals(origin, _external)) continue;
            if (replace)
            {
                _memory[origin] = new(values, ReferenceEqualityComparer.Instance);
                continue;
            }
            if (values.Count == 0) continue;
            if (!_memory.TryGetValue(origin, out HashSet<object>? contents))
                _memory.Add(origin, contents = new(ReferenceEqualityComparer.Instance));
            contents.UnionWith(values);
        }
    }

    private bool IsExact(object location)
    {
        if (location is UncertainLocation) return false;
        if (ReferenceEquals(location, _hidden) || ReferenceEquals(location, _external)) return false;
        for (object current = location; ;)
        {
            if (_summaryLocations.Contains(current)) return false;
            if (current is ArrayElement element) return !_arrays[element.Array].Repeated;
            if (current is not FieldLocation field) return true;
            current = field.Parent;
        }
    }

    private sealed class UncertainLocation(object origin)
    {
        public object Origin { get; } = origin;
    }

    private static object Unwrap(object location) => location is UncertainLocation uncertain ? uncertain.Origin : location;

    private HashSet<object> Uncertain(IEnumerable<object> origins)
    {
        HashSet<object> result = [];
        foreach (object origin in origins)
        {
            if (ArrayAliasLocations(origin) is { } locations)
            {
                foreach (object possible in locations)
                {
                    if (!_uncertainLocations.TryGetValue(possible, out var uncertain))
                        _uncertainLocations.Add(possible, uncertain = new(possible));
                    result.Add(uncertain);
                }
                continue;
            }
            if (origin is UncertainLocation || ReferenceEquals(origin, _hidden) || ReferenceEquals(origin, _external))
                result.Add(origin);
            else
            {
                if (!_uncertainLocations.TryGetValue(origin, out var uncertain))
                    _uncertainLocations.Add(origin, uncertain = new(origin));
                result.Add(uncertain);
            }
        }
        return result;
    }

    private void CheckWrite(HashSet<object> storage, BoundExpression site)
    {
        if (storage.Contains(_hidden))
            Report(site, "cannot mutate hidden state; use an explicitly mutable pointer/reference parameter",
                DiagnosticIds.HiddenStateMutation);
    }

    private bool HasHiddenAccess(HashSet<object> origins, TypeSymbol type) =>
        HasHiddenAccess(origins, type, [], []);

    private bool HasHiddenAccess(HashSet<object> origins, TypeSymbol type,
        HashSet<(object, TypeSymbol)> visited, HashSet<TypeSymbol> valuePath)
    {
        if (!ExposesWritableAccess(type)) return false;
        if (origins.Contains(_hidden)) return true;
        HashSet<object> fresh = new(origins.Where(origin => visited.Add((origin, type))));
        if (fresh.Count == 0) return false;
        if (type is IFieldStorageTypeSymbol structure)
        {
            if (!valuePath.Add(type)) return false;
            foreach (FieldSymbol field in structure.AllInstanceFields)
            {
                if (HasHiddenAccess(Read(Project(fresh, field), field.Type), field.Type, visited, valuePath))
                    return true;
            }
            valuePath.Remove(type);
            return false;
        }
        // Origins already address the referent/pointee storage. Read using the
        // element type, NOT the pointer/reference type: a struct read preserves
        // its field locations, while int*& must load the referenced int* binding.
        if (type is ArrayTypeSymbol array)
            return HasHiddenAccess(Read(ArrayElements(fresh), array.ElementType), array.ElementType, visited, []);
        if (ElementType(type) is { } element)
            return HasHiddenAccess(Read(ArrayCapabilityRange(fresh), element), element, visited, []);
        // Interface values erase the concrete field layout. Follow the known
        // graph conservatively rather than losing capabilities in that view.
        if (type is InterfaceTypeSymbol)
        {
            var pending = new Stack<object>(fresh);
            var seen = new HashSet<object>();
            while (pending.TryPop(out object? origin))
            {
                origin = Unwrap(origin);
                if (ReferenceEquals(origin, _hidden)) return true;
                if (!seen.Add(origin)) continue;
                if (_memory.TryGetValue(origin, out var contents))
                    foreach (object value in contents) pending.Push(value);
                if (_fields.TryGetValue(origin, out var fields))
                    foreach (object value in fields.Values) pending.Push(value);
            }
        }
        return false;
    }

    private void CheckCall(FunctionSymbol callee, BoundExpression site)
    {
        if (!callee.IsReadonly)
            Report(site, $"cannot call non-readonly function or member '{callee.Name}'",
                DiagnosticIds.NonReadonlyCallFromReadonlyFunction);
    }

    private void Report(BoundExpression site, string message, string id)
    {
        TextLocation location = locations.TryGetValue(site, out TextLocation source) ? source : _location;
        string diagnostic = $"readonly function '{function.Name}' {message}";
        if (_reported.Add((location, diagnostic))) diagnostics.Report(location, diagnostic, id);
    }

    private static bool IsMutableParameter(TypeSymbol type) =>
        type is PointerTypeSymbol { IsReadonly: false } or ReferenceTypeSymbol { IsReadonly: false };

    private static bool ContainsAccess(TypeSymbol type) => ContainsAccess(type, []);

    private static bool ContainsAccess(TypeSymbol type, HashSet<TypeSymbol> visited) => type switch
    {
        PointerTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol or OwnershipTypeSymbol or InterfaceTypeSymbol => true,
        IFieldStorageTypeSymbol structure when visited.Add(type) => structure.AllInstanceFields.Any(field => ContainsAccess(field.Type, visited)),
        _ => false,
    };

    private static bool ExposesWritableAccess(TypeSymbol type) => ExposesWritableAccess(type, []);

    private static bool ExposesWritableAccess(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        if (!visited.Add(type)) return false;
        return type switch
        {
            PointerTypeSymbol pointer => !pointer.IsReadonly || ExposesWritableAccess(pointer.ElementType, visited),
            ReferenceTypeSymbol reference => !reference.IsReadonly || ExposesWritableAccess(reference.ElementType, visited),
            ArrayTypeSymbol or InterfaceTypeSymbol => true,
            UniqueTypeSymbol or SharedTypeSymbol => true,
            IFieldStorageTypeSymbol structure => structure.AllInstanceFields.Any(field => ExposesWritableAccess(field.Type, visited)),
            _ => false,
        };
    }
}
