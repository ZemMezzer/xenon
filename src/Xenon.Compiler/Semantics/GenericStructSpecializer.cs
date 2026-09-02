using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal enum GenericStructSpecializationState
{
    Created,
    ResolvingFields,
    FieldsResolved,
    ResolvingMembers,
    MembersResolved,
    ValidatingConstraints,
    ConstraintsValidated,
}

internal readonly record struct SpecializedStructFunction(
    FunctionSymbol Definition,
    StructTypeSymbol Owner,
    FunctionSymbol Specialized);

internal sealed class GenericStructSpecializer
{
    private readonly TypeFactory _types;
    private readonly DiagnosticBag _diagnostics;
    private readonly Action<StructTypeSymbol>? _constantsCompletedCallback;
    private readonly GenericConstraintValidator _constraintValidator = new();
    private readonly Dictionary<GenericInstantiationKey, StructTypeSymbol> _cache = [];
    private readonly Dictionary<StructTypeSymbol, ImmutableDictionary<GenericParameterSymbol, TypeSymbol>> _substitutions = [];
    private readonly Dictionary<(FunctionSymbol Definition, StructTypeSymbol Owner), FunctionSymbol> _specializedFunctions = [];
    private readonly Dictionary<StructTypeSymbol, GenericStructSpecializationState> _states = [];
    private readonly Dictionary<StructTypeSymbol, TextLocation> _originLocations = [];
    private readonly HashSet<StructTypeSymbol> _constantsCompleted = [];
    private bool _fieldsReady;
    private bool _membersReady;
    private bool _constantsReady;

    public GenericStructSpecializer(TypeFactory types, DiagnosticBag diagnostics,
        Action<StructTypeSymbol>? constantsCompletedCallback = null)
    {
        _types = types;
        _diagnostics = diagnostics;
        _constantsCompletedCallback = constantsCompletedCallback;
    }

    public IEnumerable<StructTypeSymbol> Specializations => _cache.Values;
    public int SpecializationCount => _cache.Count;
    public IEnumerable<SpecializedStructFunction> SpecializedFunctions => _specializedFunctions.Select(entry =>
        new SpecializedStructFunction(entry.Key.Definition, entry.Key.Owner, entry.Value));

    public void CompleteFields()
    {
        _fieldsReady = true;
        CompleteAll(EnsureFields);
    }

    public void CompleteMembers()
    {
        CompleteFields();
        _membersReady = true;
        CompleteAll(EnsureMembers);
        foreach (StructTypeSymbol specialization in _cache.Values.ToArray())
            EnsureConstraintsValidated(specialization);
    }

    public void CompleteConstants()
    {
        _constantsReady = true;
        CompleteAll(EnsureConstants);
    }

    public StructTypeSymbol? GetOrCreate(StructTypeSymbol definition,
        ImmutableArray<TypeSymbol> typeArguments, TextLocation location)
    {
        if (!definition.IsGenericDefinition)
        {
            _diagnostics.Report(location,
                $"generic type arguments cannot be applied because struct '{definition.Name}' is not generic",
                DiagnosticIds.GenericTypeArgumentsNotSupported);
            return null;
        }
        if (typeArguments.Length != definition.TypeParameters.Length)
        {
            _diagnostics.Report(location,
                $"generic struct '{definition.Name}' expects {definition.TypeParameters.Length} type argument(s), but {typeArguments.Length} were provided",
                DiagnosticIds.GenericArityMismatch);
            return null;
        }
        typeArguments = typeArguments.Select(_types.Intern).ToImmutableArray();
        var key = new GenericInstantiationKey(definition, typeArguments);
        if (_cache.TryGetValue(key, out StructTypeSymbol? existing))
        {
            EnsureAvailable(existing);
            return existing;
        }

        string name = $"{definition.Name}<{string.Join(",", typeArguments.Select(type => type.ToDisplayString(TypeDisplayFormat.FullyQualified)))}>";
        var specialized = new StructTypeSymbol(name, definition.ContainingNamespace, definition.Declaration);
        specialized.SetGenericSpecialization(definition, typeArguments);
        // Publish the skeleton before resolving fields so recursive constructed
        // types can find this in-progress specialization.
        _cache.Add(key, specialized);
        _states.Add(specialized, GenericStructSpecializationState.Created);
        _originLocations.Add(specialized, location);
        ImmutableDictionary<GenericParameterSymbol, TypeSymbol> substitutions = definition.TypeParameters
            .Zip(typeArguments).ToImmutableDictionary(pair => pair.First, pair => pair.Second);
        _substitutions.Add(specialized, substitutions);
        EnsureAvailable(specialized);
        return specialized;
    }

    public IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> GetSubstitutions(StructTypeSymbol specialization) =>
        _substitutions[specialization];

    public GenericStructSpecializationState GetState(StructTypeSymbol specialization) => _states[specialization];

    public bool CompletePendingConstraints()
    {
        bool changed = false;
        foreach (StructTypeSymbol specialization in _cache.Values.ToArray())
        {
            GenericStructSpecializationState before = _states[specialization];
            EnsureConstraintsValidated(specialization);
            changed |= _states[specialization] != before;
        }
        return changed;
    }

    public void ReportUnresolvedConstraints()
    {
        foreach (StructTypeSymbol specialization in _cache.Values.Where(type =>
            _states[type] == GenericStructSpecializationState.MembersResolved).ToArray())
        {
            _diagnostics.Report(_originLocations[specialization],
                $"constraint validation for generic struct '{specialization.Name}' could not complete because a dependent specialization remained incomplete",
                DiagnosticIds.GenericSpecializationNotImplemented);
            _states[specialization] = GenericStructSpecializationState.ConstraintsValidated;
        }
    }

    private void CompleteAll(Action<StructTypeSymbol> complete)
    {
        while (true)
        {
            StructTypeSymbol[] snapshot = _cache.Values.ToArray();
            foreach (StructTypeSymbol specialization in snapshot)
                complete(specialization);
            if (snapshot.Length == _cache.Count) return;
        }
    }

    private void EnsureAvailable(StructTypeSymbol specialization)
    {
        if (_fieldsReady)
            EnsureFields(specialization);
        if (_constantsReady && _states[specialization] >= GenericStructSpecializationState.FieldsResolved)
            EnsureConstants(specialization);
        if (!_membersReady || _states[specialization] < GenericStructSpecializationState.FieldsResolved)
            return;
        EnsureMembers(specialization);
        if (_states[specialization] < GenericStructSpecializationState.MembersResolved)
            return;
        EnsureConstraintsValidated(specialization);
    }

    private void EnsureFields(StructTypeSymbol specialized)
    {
        GenericStructSpecializationState state = _states[specialized];
        if (state >= GenericStructSpecializationState.FieldsResolved ||
            state == GenericStructSpecializationState.ResolvingFields)
            return;

        _states[specialized] = GenericStructSpecializationState.ResolvingFields;
        StructTypeSymbol definition = specialized.GenericDefinition!;
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions = _substitutions[specialized];
        if (definition.BaseType is not null &&
            Substitute(definition.BaseType, substitutions, _originLocations[specialized]) is StructTypeSymbol baseType)
            specialized.SetBaseType(baseType);
        specialized.SetInterfaces(definition.Interfaces);
        if (definition.HasVirtualDispatch) specialized.SetHasVirtualDispatch();

        specialized.SetFields(definition.Fields.Select(field => new FieldSymbol(field.Name, specialized,
            Substitute(field.Type, substitutions, _originLocations[specialized]), field.Ordinal, field.Accessibility, field.IsStatic,
            field.IsReadonly, field.ConstantValue, field.Declaration)).ToImmutableArray());
        specialized.SetStaticFields(definition.StaticFields.Select(field => new FieldSymbol(field.Name, specialized,
            Substitute(field.Type, substitutions, _originLocations[specialized]), field.Ordinal, field.Accessibility, field.IsStatic,
            field.IsReadonly, field.ConstantValue, field.Declaration)).ToImmutableArray());
        specialized.SetConstants([]);
        _states[specialized] = GenericStructSpecializationState.FieldsResolved;
    }

    private void EnsureConstants(StructTypeSymbol specialized)
    {
        EnsureFields(specialized);
        if (_states[specialized] < GenericStructSpecializationState.FieldsResolved ||
            !_constantsCompleted.Add(specialized))
            return;

        StructTypeSymbol definition = specialized.GenericDefinition!;
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions = _substitutions[specialized];
        ImmutableArray<ConstantSymbol> constants = definition.Constants.Select(source =>
            new ConstantSymbol(source.Name, Substitute(source.Type, substitutions, _originLocations[specialized]), specialized,
                source.Initializer, source.Declaration)).ToImmutableArray();
        specialized.SetConstants(constants);
        _constantsCompletedCallback?.Invoke(specialized);
    }

    private void EnsureMembers(StructTypeSymbol specialized)
    {
        EnsureFields(specialized);
        GenericStructSpecializationState state = _states[specialized];
        if (state < GenericStructSpecializationState.FieldsResolved)
            return;
        if (state >= GenericStructSpecializationState.MembersResolved ||
            state == GenericStructSpecializationState.ResolvingMembers)
            return;

        _states[specialized] = GenericStructSpecializationState.ResolvingMembers;
        StructTypeSymbol definition = specialized.GenericDefinition!;
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions = _substitutions[specialized];

        var methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
        var properties = ImmutableArray.CreateBuilder<PropertySymbol>();
        foreach (PropertySymbol source in definition.Properties)
        {
            TypeSymbol propertyType = Substitute(source.Type, substitutions, _originLocations[specialized]);
            var property = new PropertySymbol(source.Name, specialized, propertyType,
                source.Accessibility, source.Declaration);
            FunctionSymbol? getter = source.Getter is null ? null : new FunctionSymbol(source.Getter.Name,
                property, propertyType, [], (PropertyAccessorDeclarationSyntax)source.Getter.Declaration);
            FunctionSymbol? setter = source.Setter is null ? null : new FunctionSymbol(source.Setter.Name,
                property, BuiltinTypes.Void, [new ParameterSymbol("value", propertyType, 0)],
                (PropertyAccessorDeclarationSyntax)source.Setter.Declaration);
            property.SetAccessors(getter, setter);
            properties.Add(property);
            AddFunction(source.Getter, getter, specialized, methods);
            AddFunction(source.Setter, setter, specialized, methods);
        }
        specialized.SetProperties(properties.ToImmutable());

        var indexers = ImmutableArray.CreateBuilder<IndexerSymbol>();
        foreach (IndexerSymbol source in definition.Indexers)
        {
            TypeSymbol indexerType = Substitute(source.Type, substitutions, _originLocations[specialized]);
            ImmutableArray<ParameterSymbol> parameters = SubstituteParameters(source.Parameters, substitutions,
                _originLocations[specialized]);
            var indexer = new IndexerSymbol(specialized, indexerType, parameters,
                source.Accessibility, source.Declaration);
            FunctionSymbol? getter = source.Getter is null ? null : new FunctionSymbol(source.Getter.Name,
                indexer, indexerType, parameters, (PropertyAccessorDeclarationSyntax)source.Getter.Declaration);
            FunctionSymbol? setter = source.Setter is null ? null : new FunctionSymbol(source.Setter.Name,
                indexer, BuiltinTypes.Void,
                [.. parameters, new ParameterSymbol("value", indexerType, parameters.Length)],
                (PropertyAccessorDeclarationSyntax)source.Setter.Declaration);
            indexer.SetAccessors(getter, setter);
            indexers.Add(indexer);
            AddFunction(source.Getter, getter, specialized, methods);
            AddFunction(source.Setter, setter, specialized, methods);
        }
        specialized.SetIndexers(indexers.ToImmutable());

        foreach (FunctionSymbol source in definition.Methods.Where(method => !method.IsAccessor))
        {
            var method = new FunctionSymbol(source.Name, specialized,
                Substitute(source.ReturnType, substitutions, _originLocations[specialized]),
                SubstituteParameters(source.Parameters, substitutions, _originLocations[specialized]),
                (MethodDeclarationSyntax)source.Declaration);
            AddFunction(source, method, specialized, methods);
        }
        specialized.SetMethods(methods.ToImmutable());
        ImmutableArray<FunctionSymbol> virtualMethods = definition.VirtualMethods
            .Select(source => _specializedFunctions.GetValueOrDefault((source, specialized)))
            .OfType<FunctionSymbol>().ToImmutableArray();
        foreach (FunctionSymbol source in definition.VirtualMethods)
            if (source.VTableSlot is int slot &&
                _specializedFunctions.TryGetValue((source, specialized), out FunctionSymbol? target))
                target.SetVTableSlot(slot);
        specialized.SetVirtualMethods(virtualMethods);

        ImmutableArray<FunctionSymbol> constructors = definition.Constructors.Select(source =>
        {
            var constructor = new FunctionSymbol(FunctionKind.Constructor, specialized,
                SubstituteParameters(source.Parameters, substitutions, _originLocations[specialized]),
                source.Declaration, source.Accessibility);
            _specializedFunctions.Add((source, specialized), constructor);
            return constructor;
        }).ToImmutableArray();
        specialized.SetConstructors(constructors);

        if (definition.Destructor is { } sourceDestructor)
        {
            var destructor = new FunctionSymbol(FunctionKind.Destructor, specialized, [],
                sourceDestructor.Declaration, sourceDestructor.Accessibility);
            specialized.SetDestructor(destructor);
            _specializedFunctions.Add((sourceDestructor, specialized), destructor);
        }
        _states[specialized] = GenericStructSpecializationState.MembersResolved;
    }

    private void AddFunction(FunctionSymbol? source, FunctionSymbol? specialized, StructTypeSymbol owner,
        ImmutableArray<FunctionSymbol>.Builder methods)
    {
        if (source is null || specialized is null) return;
        methods.Add(specialized);
        _specializedFunctions.Add((source, owner), specialized);
    }

    private void EnsureConstraintsValidated(StructTypeSymbol specialized)
    {
        if (_states[specialized] >= GenericStructSpecializationState.ConstraintsValidated)
            return;
        EnsureMembers(specialized);
        if (_states[specialized] < GenericStructSpecializationState.MembersResolved ||
            _states[specialized] == GenericStructSpecializationState.ValidatingConstraints)
            return;

        StructTypeSymbol definition = specialized.GenericDefinition!;
        if (MustDeferStructuralConstraintValidation(definition, specialized))
            return;
        _states[specialized] = GenericStructSpecializationState.ValidatingConstraints;
        bool isValid = true;
        for (int index = 0; index < specialized.TypeArguments.Length; index++)
        {
            if (specialized.TypeArguments[index] is GenericParameterSymbol argumentParameter)
            {
                foreach (GenericConstraintSymbol required in definition.TypeParameters[index].Constraints)
                {
                    if (GenericConstraintGuarantees.IsGuaranteed(argumentParameter, required)) continue;
                    isValid = false;
                    _diagnostics.Report(_originLocations[specialized],
                        $"constraints for '{argumentParameter.Name}' do not guarantee '{required.Target.Name}' required by '{definition.Name}'" +
                        GenericConstraintGuarantees.GetFailureDetail(argumentParameter, required),
                        DiagnosticIds.GenericConstraintNotSatisfied);
                }
                continue;
            }
            GenericConstraintValidationResult validation =
                _constraintValidator.Validate(definition.TypeParameters[index], specialized.TypeArguments[index]);
            if (validation.IsValid) continue;
            isValid = false;
            _diagnostics.Report(_originLocations[specialized],
                GenericConstraintDiagnostics.Format(definition.TypeParameters[index],
                    specialized.TypeArguments[index], validation),
                DiagnosticIds.GenericConstraintNotSatisfied);
        }
        if (isValid && specialized.IsConcreteType)
            definition.ContainingNamespace.TryDeclareType(specialized);
        _states[specialized] = GenericStructSpecializationState.ConstraintsValidated;
    }

    private bool MustDeferStructuralConstraintValidation(StructTypeSymbol definition,
        StructTypeSymbol specialized)
    {
        for (int index = 0; index < specialized.TypeArguments.Length; index++)
        {
            if (!definition.TypeParameters[index].Constraints.Any(constraint =>
                    constraint.Kind == GenericConstraintKind.StructuralTemplate))
                continue;
            if (specialized.TypeArguments[index] is StructTypeSymbol { GenericDefinition: not null } candidate &&
                _states.TryGetValue(candidate, out GenericStructSpecializationState candidateState) &&
                candidateState < GenericStructSpecializationState.MembersResolved)
                return true;
        }
        return false;
    }

    private ImmutableArray<ParameterSymbol> SubstituteParameters(ImmutableArray<ParameterSymbol> parameters,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions, TextLocation origin) => parameters.Select(parameter =>
        new ParameterSymbol(parameter.Name, Substitute(parameter.Type, substitutions, origin), parameter.Ordinal,
            parameter.IsReadonly, declaration: parameter.Declaration)).ToImmutableArray();

    internal TypeSymbol Substitute(TypeSymbol type,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions, TextLocation? origin = null) => type switch
    {
        GenericParameterSymbol parameter when substitutions.TryGetValue(parameter, out TypeSymbol? replacement) => replacement,
        StructTypeSymbol { GenericDefinition: not null } constructed => SubstituteConstructed(constructed, substitutions, origin),
        PointerTypeSymbol pointer => _types.PointerTo(Substitute(pointer.ElementType, substitutions, origin), pointer.IsReadonly),
        ReferenceTypeSymbol reference => _types.ReferenceTo(Substitute(reference.ElementType, substitutions, origin), reference.IsReadonly),
        ArrayTypeSymbol array => _types.ArrayOf(Substitute(array.ElementType, substitutions, origin), array.Rank),
        UniqueTypeSymbol unique => SubstituteUnique(unique, substitutions, origin),
        SharedTypeSymbol shared => SubstituteOwnership(shared, substitutions, origin),
        WeakTypeSymbol weak => SubstituteOwnership(weak, substitutions, origin),
        _ => type,
    };

    private TypeSymbol SubstituteUnique(UniqueTypeSymbol unique,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions, TextLocation? origin)
    {
        UniqueTypeSymbol result = _types.UniqueOf(Substitute(unique.ElementType, substitutions, origin));
        if (unique.CompleteDestructor is { } sourceDestructor)
            _types.EnsureUniqueDestructor(result, sourceDestructor.ContainingNamespace, sourceDestructor.Declaration);
        return result;
    }

    private TypeSymbol SubstituteOwnership(OwnershipTypeSymbol ownership,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions, TextLocation? origin)
    {
        TypeSymbol element = Substitute(ownership.ElementType, substitutions, origin);
        OwnershipTypeSymbol result = ownership switch
        {
            SharedTypeSymbol => _types.SharedOf(element),
            WeakTypeSymbol => _types.WeakOf(element),
            _ => throw new InvalidOperationException(),
        };
        if (ownership.CompleteDestructor is { } sourceDestructor)
            _types.EnsureOwnershipDestructor(result, sourceDestructor.ContainingNamespace, sourceDestructor.Declaration);
        return result;
    }

    private TypeSymbol SubstituteConstructed(StructTypeSymbol constructed,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions, TextLocation? origin)
    {
        ImmutableArray<TypeSymbol> arguments = constructed.TypeArguments
            .Select(argument => Substitute(argument, substitutions, origin)).ToImmutableArray();
        if (arguments.Zip(constructed.TypeArguments).All(pair => TypeIdentity.AreSame(pair.First, pair.Second)))
            return constructed;
        return (TypeSymbol?)GetOrCreate(constructed.GenericDefinition!, arguments,
            origin ?? constructed.Declaration.IdentifierToken.Location) ?? BuiltinTypes.Error;
    }
}
