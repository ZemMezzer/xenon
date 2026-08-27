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
// The points-to graph is monotone and flow-insensitive: joins include every
// possible origin, including loop back-edges and writes through local aliases.
internal sealed class ReadonlyEffectAnalyzer(
    FunctionSymbol function,
    DiagnosticBag diagnostics,
    IReadOnlyDictionary<BoundExpression, TextLocation> locations,
    TextLocation fallbackLocation)
{
    private readonly object _hidden = new();
    private readonly object _external = new();
    private readonly Dictionary<object, HashSet<object>> _memory = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _roots = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(TextLocation Location, string Message)> _reported = [];
    private readonly HashSet<StructTypeSymbol> _initializing = [];
    private object? _initializerReceiver;
    private bool _changed;
    private bool _reportErrors;
    private TextLocation _location = fallbackLocation;

    public void Analyze(BoundBlockStatement body)
    {
        foreach (ParameterSymbol parameter in function.Parameters)
        {
            if (ContainsAccess(parameter.Type))
                Store([Root(parameter)], [IsMutableParameter(parameter.Type) ? _external : _hidden]);
        }

        do
        {
            _changed = false;
            Visit(body);
        } while (_changed);

        // Report only after alias information has reached a fixed point.
        _reportErrors = true;
        Visit(body);
    }

    private void Visit(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundBlockStatement block:
                foreach (BoundStatement child in block.Statements) Visit(child);
                break;
            case BoundVariableDeclarationStatement declaration:
                if (declaration.Initializer is { } initializer)
                    Store([Root(declaration.Variable)], Evaluate(initializer));
                break;
            case BoundExpressionStatement expression:
                Evaluate(expression.Expression);
                break;
            case BoundReturnStatement { Expression: { } expression }:
                HashSet<object> returned = Evaluate(expression);
                if (ExposesWritableAccess(function.ReturnType) && HasHiddenAccess(returned, function.ReturnType))
                    Report(expression, "cannot return a mutable capability obtained from hidden state");
                break;
            case BoundIfStatement conditional:
                Evaluate(conditional.Condition);
                Visit(conditional.ThenStatement);
                if (conditional.ElseStatement is { } alternative) Visit(alternative);
                break;
            case BoundWhileStatement loop:
                Evaluate(loop.Condition);
                Visit(loop.Body);
                break;
            case BoundForStatement loop:
                if (loop.Initializer is { } init) Visit(init);
                if (loop.Condition is { } condition) Evaluate(condition);
                Visit(loop.Body);
                if (loop.Increment is { } increment) Evaluate(increment);
                break;
            case BoundSwitchStatement selection:
                Evaluate(selection.Expression);
                foreach (BoundSwitchSection section in selection.Sections) Visit(section.Body);
                break;
            case BoundReturnStatement or BoundBreakStatement or BoundContinueStatement:
                break;
            default:
                throw new InvalidOperationException($"Missing readonly effect analysis for '{statement.Kind}'.");
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
            case BoundThisExpression:
                return [_initializerReceiver ?? _hidden];
            case BoundStaticFieldExpression field:
                return ContainsAccess(field.Type) ? [_hidden] : [];
            case BoundMemberAccessExpression member:
                Evaluate(member.Receiver);
                return Read(Address(member), member.Type);
            case BoundReferenceConversionExpression conversion:
                return conversion.Source is BoundThisExpression
                    ? Evaluate(conversion.Source) : Address(conversion.Source);
            case BoundReferenceDereferenceExpression dereference:
                return Read(Evaluate(dereference.Reference), dereference.Type);
            case BoundInterfaceConversionExpression conversion:
                return Address(conversion.Source);
            case BoundCastExpression cast:
                return Evaluate(cast.Expression);
            case BoundUnaryExpression unary:
            {
                HashSet<object> operand = Evaluate(unary.Operand);
                if (unary.OperatorKind == SyntaxKind.AmpersandToken) return Address(unary.Operand);
                if (unary.OperatorKind == SyntaxKind.StarToken) return Read(operand, unary.Type);
                if (unary.OperatorKind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
                    CheckWrite(Address(unary.Operand), unary);
                return ContainsAccess(unary.Type) ? operand : [];
            }
            case BoundBinaryExpression binary:
            {
                HashSet<object> left = Evaluate(binary.Left);
                left.UnionWith(Evaluate(binary.Right));
                return ContainsAccess(binary.Type) ? left : [];
            }
            case BoundAssignmentExpression assignment:
            {
                HashSet<object> target = Address(assignment.Target);
                HashSet<object> value = Evaluate(assignment.Expression);
                if (assignment.OperatorKind != SyntaxKind.EqualsToken)
                    value.UnionWith(Evaluate(assignment.Target));
                CheckWrite(target, assignment);
                if (target.Contains(_external) && ExposesWritableAccess(assignment.Target.Type) && HasHiddenAccess(value, assignment.Target.Type))
                    Report(assignment, "cannot store a mutable capability obtained from hidden state through an output parameter");
                if (ContainsAccess(assignment.Target.Type)) Store(target, value);
                return value;
            }
            case BoundIndexExpression index:
                foreach (BoundExpression argument in index.Indices) Evaluate(argument);
                return Read(Evaluate(index.Receiver), index.Type);
            case BoundArrayMetadataExpression metadata:
                Evaluate(metadata.Receiver);
                if (metadata.Dimension is { } dimension) Evaluate(dimension);
                return [];
            case BoundCallExpression call:
                return Call(call.Function, call.Arguments, call);
            case BoundMethodCallExpression call:
                Evaluate(call.Receiver);
                return Call(call.Method, call.Arguments, call);
            case BoundInterfaceMethodCallExpression call:
                Evaluate(call.Receiver);
                return Call(call.Method, call.Arguments, call);
            case BoundPropertySetExpression set:
                Evaluate(set.Receiver);
                return Call(set.Property.Setter!, [set.Value], set);
            case BoundInterfacePropertySetExpression set:
                Evaluate(set.Receiver);
                return Call(set.Property.Setter!, [set.Value], set);
            case BoundIndexerSetExpression set:
                Evaluate(set.Receiver);
                return Call(set.Indexer.Setter!, set.Arguments.Add(set.Value), set);
            case BoundInterfaceIndexerSetExpression set:
                Evaluate(set.Receiver);
                return Call(set.Indexer.Setter!, set.Arguments.Add(set.Value), set);
            case BoundCompoundAccessorAssignmentExpression set:
                Evaluate(set.Receiver);
                Call(set.Getter, set.Arguments, set);
                return Call(set.Setter, set.Arguments.Add(set.Value), set);
            case BoundConstructorCallExpression construction:
                return Call(construction.Constructor, construction.Arguments, construction);
            case BoundBaseLifecycleCallExpression call:
                return Call(call.Function, call.Arguments, call);
            case BoundStructConstructionExpression construction:
                Initialize(construction.StructType, construction.Arguments, construction);
                return Read([Root(construction)], construction.Type);
            case BoundNewExpression allocation:
                if (allocation.Constructor is { } constructor)
                    Call(constructor, allocation.Arguments, allocation);
                else
                    Initialize(allocation.StructType, allocation.Arguments, allocation);
                return [Root(allocation)];
            case BoundArrayCreationExpression allocation:
                foreach (BoundExpression length in allocation.Dimensions) Evaluate(length);
                if (allocation.ElementType is StructTypeSymbol element)
                {
                    Initialize(element, [], allocation);
                    if (allocation.Storage == ArrayStorageKind.Stack && element.FindDestructor() is { } destructor)
                        CheckCall(destructor, allocation);
                }
                return [Root(allocation)];
            case BoundFreeExpression free:
            {
                HashSet<object> pointer = Evaluate(free.Pointer);
                CheckWrite(pointer, free);
                if (free.Pointer.Type is PointerTypeSymbol { IsReadonly: true })
                    Report(free, "cannot free memory through a readonly pointer");
                if (free.Destructor is { } destructor) CheckCall(destructor, free);
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
            case BoundVariableExpression variable:
                return [Root(variable.Variable)];
            case BoundStaticFieldExpression:
                return [_hidden];
            case BoundReferenceDereferenceExpression reference:
                return Evaluate(reference.Reference);
            case BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } pointer:
                return Evaluate(pointer.Operand);
            case BoundMemberAccessExpression member:
                return member.IsPointerAccess ? Evaluate(member.Receiver) : Address(member.Receiver);
            case BoundIndexExpression index:
                foreach (BoundExpression argument in index.Indices) Evaluate(argument);
                return Evaluate(index.Receiver);
            default:
                // A reference may bind to a materialized struct/interface value.
                Store([Root(expression)], Evaluate(expression));
                return [Root(expression)];
        }
    }

    private HashSet<object> Call(FunctionSymbol callee, ImmutableArray<BoundExpression> arguments, BoundExpression site)
    {
        CheckCall(callee, site);
        var mutableArguments = new List<HashSet<object>>();
        HashSet<object> available = [Root(site)]; // A readonly callee may allocate fresh storage.
        for (int index = 0; index < arguments.Length; index++)
        {
            HashSet<object> argument = Evaluate(arguments[index]);
            if (index >= callee.Parameters.Length || !IsMutableParameter(callee.Parameters[index].Type)) continue;
            if (HasHiddenAccess(argument, callee.Parameters[index].Type))
                Report(site, $"cannot pass a mutable capability obtained from hidden state to parameter '{callee.Parameters[index].Name}' of '{callee.Name}'");
            available.UnionWith(argument);
            mutableArguments.Add(argument);
        }

        // Calls can store explicit input capabilities or freshly allocated values
        // through mutable outputs. Preserve those aliases for subsequent loads.
        foreach (HashSet<object> argument in mutableArguments) Store(argument, available);
        Store([Root(site)], available);
        if (!ContainsAccess(callee.ReturnType)) return [];
        // A returned pointer/reference may alias any explicit input, not just
        // fresh storage. Writes through that result must reach the original roots.
        return available;
    }

    private void Initialize(StructTypeSymbol type, ImmutableArray<BoundExpression> arguments, BoundExpression site)
    {
        if (!_initializing.Add(type))
        {
            Store([Root(site)], [_hidden]);
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
                    Store([Root(site)], Evaluate(initializer));
            }
            foreach (BoundExpression argument in arguments) Store([Root(site)], Evaluate(argument));
        }
        finally
        {
            _initializerReceiver = previous;
            _initializing.Remove(type);
        }
    }

    private HashSet<object> Read(IEnumerable<object> storage, TypeSymbol type)
    {
        HashSet<object> result = new(ReferenceEqualityComparer.Instance);
        if (!ContainsAccess(type)) return result;
        foreach (object origin in storage)
        {
            if (ReferenceEquals(origin, _hidden) || ReferenceEquals(origin, _external)) result.Add(origin);
            else if (_memory.TryGetValue(origin, out HashSet<object>? contents)) result.UnionWith(contents);
        }
        return result;
    }

    private object Root(object identity)
    {
        if (!_roots.TryGetValue(identity, out object? root))
            _roots.Add(identity, root = new object());
        return root;
    }

    private void Store(IEnumerable<object> storage, HashSet<object> values)
    {
        if (values.Count == 0) return;
        foreach (object origin in storage)
        {
            if (ReferenceEquals(origin, _hidden) || ReferenceEquals(origin, _external)) continue;
            if (!_memory.TryGetValue(origin, out HashSet<object>? contents))
                _memory.Add(origin, contents = new(ReferenceEqualityComparer.Instance));
            foreach (object value in values) _changed |= contents.Add(value);
        }
    }

    private void CheckWrite(HashSet<object> storage, BoundExpression site)
    {
        if (storage.Contains(_hidden))
            Report(site, "cannot mutate hidden state; use an explicitly mutable pointer/reference parameter");
    }

    private bool HasHiddenAccess(HashSet<object> origins, TypeSymbol type)
    {
        if (origins.Contains(_hidden)) return true;
        // Passing &localPointer must not launder the hidden pointer stored in it.
        // Scalar pointees cannot carry another capability, even when their
        // containing aggregate shares an abstract storage root with other fields.
        bool followContents = type switch
        {
            PointerTypeSymbol pointer => ExposesWritableAccess(pointer.ElementType),
            ReferenceTypeSymbol reference => ExposesWritableAccess(reference.ElementType),
            ArrayTypeSymbol array => ExposesWritableAccess(array.ElementType),
            StructTypeSymbol or InterfaceTypeSymbol => true,
            _ => false,
        };
        if (!followContents) return false;
        var pending = new Stack<object>(origins);
        var visited = new HashSet<object>();
        while (pending.TryPop(out object? origin))
        {
            if (ReferenceEquals(origin, _hidden)) return true;
            if (!visited.Add(origin) || !_memory.TryGetValue(origin, out HashSet<object>? contents)) continue;
            foreach (object value in contents) pending.Push(value);
        }
        return false;
    }

    private void CheckCall(FunctionSymbol callee, BoundExpression site)
    {
        if (!callee.IsReadonly)
            Report(site, $"cannot call non-readonly function or member '{callee.Name}'");
    }

    private void Report(BoundExpression site, string message)
    {
        if (!_reportErrors) return;
        TextLocation location = locations.TryGetValue(site, out TextLocation source) ? source : _location;
        string diagnostic = $"readonly function '{function.Name}' {message}";
        if (_reported.Add((location, diagnostic))) diagnostics.Report(location, diagnostic);
    }

    private static bool IsMutableParameter(TypeSymbol type) =>
        type is PointerTypeSymbol { IsReadonly: false } or ReferenceTypeSymbol { IsReadonly: false };

    private static bool ContainsAccess(TypeSymbol type) => ContainsAccess(type, []);

    private static bool ContainsAccess(TypeSymbol type, HashSet<TypeSymbol> visited) => type switch
    {
        PointerTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol or InterfaceTypeSymbol => true,
        StructTypeSymbol structure when visited.Add(type) => structure.AllInstanceFields.Any(field => ContainsAccess(field.Type, visited)),
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
            StructTypeSymbol structure => structure.AllInstanceFields.Any(field => ExposesWritableAccess(field.Type, visited)),
            _ => false,
        };
    }
}
