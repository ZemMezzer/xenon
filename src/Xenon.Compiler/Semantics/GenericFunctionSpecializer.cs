using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed class GenericFunctionSpecializer
{
    private readonly IGenericImplementationProvider _implementations;
    private readonly TypeFactory _types;
    private readonly DiagnosticBag _diagnostics;
    private readonly ConstantEvaluationContext _constants;
    private readonly CancellationToken _cancellationToken;
    private readonly GenericStructSpecializer _structSpecializer;
    private readonly Func<NamespaceSymbol, NamespaceSymbol> _resolveNamespace;
    private readonly Dictionary<GenericInstantiationKey, FunctionSymbol> _symbols = [];
    private readonly List<BoundFunction> _functions = [];
    private readonly GenericConstraintValidator _constraintValidator = new();

    public GenericFunctionSpecializer(
        IGenericImplementationProvider implementations,
        TypeFactory types,
        DiagnosticBag diagnostics,
        ConstantEvaluationContext constants,
        GenericStructSpecializer structSpecializer,
        Func<NamespaceSymbol, NamespaceSymbol> resolveNamespace,
        CancellationToken cancellationToken)
    {
        _implementations = implementations;
        _types = types;
        _diagnostics = diagnostics;
        _constants = constants;
        _structSpecializer = structSpecializer;
        _resolveNamespace = resolveNamespace;
        _cancellationToken = cancellationToken;
    }

    public ImmutableArray<BoundFunction> Functions => [.. _functions];
    internal GenericStructSpecializer StructSpecializer => _structSpecializer;
    internal TypeFactory Types => _types;

    public FunctionSymbol? GetOrCreate(FunctionSymbol definition, ImmutableArray<TypeSymbol> typeArguments,
        TextLocation location)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!definition.IsGenericDefinition || definition.ContainingType is not null)
            return null;
        if (!_implementations.TryGetFunction(definition, out IGenericFunctionImplementation? implementation))
        {
            _diagnostics.Report(location,
                $"generic function '{definition.Name}' cannot be specialized because its implementation is unavailable",
                DiagnosticIds.GenericSpecializationNotImplemented);
            return null;
        }
        if (typeArguments.Length != definition.TypeParameters.Length)
        {
            _diagnostics.Report(location,
                $"generic function '{definition.Name}' expects {definition.TypeParameters.Length} type argument(s), but {typeArguments.Length} were provided",
                DiagnosticIds.GenericArityMismatch);
            return null;
        }
        if (typeArguments.Any(GenericTypeFacts.ContainsGenericParameter))
        {
            _diagnostics.Report(location,
                $"specialization of '{definition.Name}' requires concrete type arguments",
                DiagnosticIds.GenericSpecializationNotImplemented);
            return null;
        }

        for (int index = 0; index < typeArguments.Length; index++)
        {
            GenericConstraintValidationResult validation =
                _constraintValidator.Validate(definition.TypeParameters[index], typeArguments[index]);
            if (validation.IsValid) continue;
            _diagnostics.Report(location,
                GenericConstraintDiagnostics.Format(definition.TypeParameters[index], typeArguments[index], validation),
                DiagnosticIds.GenericConstraintNotSatisfied);
            return null;
        }

        typeArguments = typeArguments.Select(_types.Intern).ToImmutableArray();
        var key = new GenericInstantiationKey(definition, typeArguments);
        if (_symbols.TryGetValue(key, out FunctionSymbol? existing)) return existing;

        var substitutions = definition.TypeParameters.Zip(typeArguments)
            .ToDictionary(pair => pair.First, pair => pair.Second);
        TypeSymbol returnType = Substitute(definition.ReturnType, substitutions, location);
        ImmutableArray<ParameterSymbol> parameters = definition.Parameters.Select(parameter =>
            new ParameterSymbol(parameter.Name, Substitute(parameter.Type, substitutions, location), parameter.Ordinal,
                parameter.IsReadonly)).ToImmutableArray();
        string name = $"{definition.Name}<{string.Join(",", typeArguments.Select(type => type.ToDisplayString(TypeDisplayFormat.FullyQualified)))}>";
        var specialized = new FunctionSymbol(name, _resolveNamespace(definition.ContainingNamespace), FunctionKind.Ordinary,
            returnType, parameters, definition.Accessibility, isStatic: definition.IsStatic,
            isReadonly: definition.IsReadonly, isVirtual: definition.IsVirtual,
            isOverride: definition.IsOverride, isAbstract: definition.IsAbstract,
            isExtern: definition.IsExtern, isExport: definition.IsExport, isDefinition: true,
            origin: SymbolOrigin.CompilerGenerated, documentation: definition.Documentation);
        specialized.HasStackArrays = definition.HasStackArrays;
        specialized.HasScalarCleanup = definition.HasScalarCleanup;
        specialized.SetGenericSpecialization(definition, typeArguments);
        specialized.SetReceiverMoveEffects(definition.ReceiverMoveEffects);
        specialized.SetReferenceReturnOrigins(definition.ReferenceReturnOrigins);
        specialized.SetSharedReturnOrigins(definition.SharedReturnOrigins);
        specialized.SetReferenceFieldOrigins(definition.ReferenceFieldOrigins);
        _symbols.Add(key, specialized);

        BoundBlockStatement body = implementation.Bind(specialized, substitutions,
            _diagnostics, _constants, this, _cancellationToken);
        _functions.Add(new BoundFunction(specialized, body));
        return specialized;
    }

    public FunctionSymbol? InferAndCreate(FunctionSymbol definition,
        ImmutableArray<BoundExpression> arguments, TextLocation location, out bool inferenceSucceeded)
    {
        inferenceSucceeded = false;
        var inferred = new Dictionary<GenericParameterSymbol, TypeSymbol>(ReferenceEqualityComparer.Instance);
        if (arguments.Length != definition.Parameters.Length) return null;
        for (int index = 0; index < arguments.Length; index++)
            if (!TryInfer(definition.Parameters[index].Type, arguments[index].Type, inferred))
                return null;
        if (definition.TypeParameters.Any(parameter => !inferred.ContainsKey(parameter))) return null;
        inferenceSucceeded = true;
        return GetOrCreate(definition,
            definition.TypeParameters.Select(parameter => inferred[parameter]).ToImmutableArray(), location);
    }

    private TypeSymbol Substitute(TypeSymbol type,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        TextLocation location) =>
        _structSpecializer.Substitute(type, substitutions, location);

    private bool TryInfer(TypeSymbol pattern, TypeSymbol actual,
        IDictionary<GenericParameterSymbol, TypeSymbol> inferred)
    {
        if (pattern is GenericParameterSymbol parameter)
        {
            actual = _types.Intern(actual);
            if (!inferred.TryGetValue(parameter, out TypeSymbol? previous))
            {
                inferred.Add(parameter, actual);
                return true;
            }
            return TypeIdentity.AreSame(previous, actual);
        }
        return (pattern, actual) switch
        {
            (PointerTypeSymbol left, PointerTypeSymbol right) when left.IsReadonly == right.IsReadonly =>
                TryInfer(left.ElementType, right.ElementType, inferred),
            (ReferenceTypeSymbol left, ReferenceTypeSymbol right) when left.IsReadonly == right.IsReadonly =>
                TryInfer(left.ElementType, right.ElementType, inferred),
            (ReferenceTypeSymbol left, _) =>
                TryInfer(left.ElementType, actual, inferred),
            (ArrayTypeSymbol left, ArrayTypeSymbol right) when left.Rank == right.Rank =>
                TryInfer(left.ElementType, right.ElementType, inferred),
            (AtomicTypeSymbol left, AtomicTypeSymbol right) =>
                TryInfer(left.ElementType, right.ElementType, inferred),
            (UniqueTypeSymbol left, UniqueTypeSymbol right) =>
                TryInfer(left.ElementType, right.ElementType, inferred),
            (SharedTypeSymbol left, SharedTypeSymbol right) =>
                TryInfer(left.ElementType, right.ElementType, inferred),
            (WeakTypeSymbol left, WeakTypeSymbol right) =>
                TryInfer(left.ElementType, right.ElementType, inferred),
            (StructTypeSymbol { GenericDefinition: not null } left,
                StructTypeSymbol { GenericDefinition: not null } right)
                when ReferenceEquals(left.GenericDefinition, right.GenericDefinition) &&
                     left.TypeArguments.Length == right.TypeArguments.Length =>
                left.TypeArguments.Zip(right.TypeArguments).All(pair => TryInfer(pair.First, pair.Second, inferred)),
            _ => TypeIdentity.AreSame(pattern, actual),
        };
    }
}
