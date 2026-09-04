using System.Collections.Concurrent;
using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

public sealed record TemplateRequirementFailure(
    TemplateMemberRequirementSymbol Requirement,
    ImmutableArray<Symbol> Candidates);

public sealed class TemplateMatchResult
{
    internal TemplateMatchResult(
        ImmutableArray<TemplateRequirementFailure> missingMembers,
        ImmutableArray<TemplateRequirementFailure> signatureMismatches,
        ImmutableArray<TemplateRequirementFailure> accessibilityFailures)
    {
        MissingMembers = missingMembers;
        SignatureMismatches = signatureMismatches;
        AccessibilityFailures = accessibilityFailures;
    }

    public bool IsValid => MissingMembers.IsEmpty && SignatureMismatches.IsEmpty && AccessibilityFailures.IsEmpty;
    public ImmutableArray<TemplateRequirementFailure> MissingMembers { get; }
    public ImmutableArray<TemplateRequirementFailure> SignatureMismatches { get; }
    public ImmutableArray<TemplateRequirementFailure> AccessibilityFailures { get; }
}

/// <summary>Compilation-time structural matching. It never inspects bodies or creates runtime metadata.</summary>
public sealed class TemplateConformanceMatcher
{
    private readonly ConcurrentDictionary<(StructTypeSymbol Concrete, TemplateSymbol Template), TemplateMatchResult> _cache = new();
    private readonly ConcurrentDictionary<(TemplateSymbol Available, TemplateSymbol Required),
        ImmutableArray<TemplateMemberRequirementSymbol>> _templateMissingRequirements = new();
    private readonly ConcurrentDictionary<(GenericParameterSymbol Parameter, TemplateSymbol Required),
        ImmutableArray<TemplateMemberRequirementSymbol>> _constraintMissingRequirements = new();

    public TemplateMatchResult Match(StructTypeSymbol concrete, TemplateSymbol template)
    {
        ArgumentNullException.ThrowIfNull(concrete);
        ArgumentNullException.ThrowIfNull(template);
        return _cache.GetOrAdd((concrete, template), static pair => MatchCore(pair.Concrete, pair.Template));
    }

    public bool Guarantees(TemplateSymbol available, TemplateSymbol required)
        => MissingRequirements(available, required).IsEmpty;

    public ImmutableArray<TemplateMemberRequirementSymbol> MissingRequirements(
        TemplateSymbol available, TemplateSymbol required)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(required);
        return _templateMissingRequirements.GetOrAdd((available, required), static pair =>
            pair.Required.Members.Where(requirement => !pair.Available.Members.Any(candidate =>
                TemplateRequirementMatches(requirement, candidate, pair.Required, pair.Available))).ToImmutableArray());
    }

    public ImmutableArray<TemplateMemberRequirementSymbol> MissingRequirements(
        GenericParameterSymbol parameter, TemplateSymbol required)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(required);
        return _constraintMissingRequirements.GetOrAdd((parameter, required), static pair =>
        {
            Symbol[] available = GenericConstraintMemberLookup.GetMembers(pair.Parameter).ToArray();
            return pair.Required.Members.Where(requirement =>
                !ConstraintSetGuarantees(requirement, pair.Required, available)).ToImmutableArray();
        });
    }

    private static bool ConstraintSetGuarantees(TemplateMemberRequirementSymbol required,
        TemplateSymbol requiredTemplate, Symbol[] available)
    {
        if (required is TemplatePropertyRequirementSymbol property)
        {
            Symbol[] matching = available.Where(candidate =>
                PropertyShapeMatches(property, candidate, requiredTemplate)).ToArray();
            return matching.Length > 0 &&
                   (!property.HasGetter || matching.Any(HasGetter)) &&
                   (!property.HasSetter || matching.Any(HasSetter));
        }
        if (required is TemplateIndexerRequirementSymbol indexer)
        {
            Symbol[] matching = available.Where(candidate =>
                IndexerShapeMatches(indexer, candidate, requiredTemplate)).ToArray();
            return matching.Length > 0 &&
                   (!indexer.HasGetter || matching.Any(HasGetter)) &&
                   (!indexer.HasSetter || matching.Any(HasSetter));
        }
        return available.Any(candidate => ConstraintMemberMatches(
            required, candidate, requiredTemplate));
    }

    private static bool ConstraintMemberMatches(TemplateMemberRequirementSymbol required,
        Symbol available, TemplateSymbol requiredTemplate)
    {
        if (available is TemplateMemberRequirementSymbol templateRequirement)
            return TemplateRequirementMatches(required, templateRequirement,
                requiredTemplate, templateRequirement.Template);
        return (required, available) switch
        {
            (TemplateMethodRequirementSymbol expected, FunctionSymbol actual) =>
                actual.FunctionKind == FunctionKind.Method &&
                expected.Name == actual.Name && expected.Accessibility == actual.Accessibility &&
                expected.IsStatic == actual.IsStatic && expected.IsReadonly == actual.IsReadonly &&
                TemplateTypeMatchesNominal(expected.ReturnType, actual.ReturnType, requiredTemplate) &&
                TemplateParametersMatchNominal(expected.Parameters, actual.Parameters, requiredTemplate),
            // A base constraint does not guarantee constructors for every derived T.
            (TemplateConstructorRequirementSymbol, FunctionSymbol) => false,
            _ => false,
        };
    }

    private static bool PropertyShapeMatches(TemplatePropertyRequirementSymbol required,
        Symbol available, TemplateSymbol requiredTemplate) => available switch
    {
        TemplatePropertyRequirementSymbol actual =>
            required.Name == actual.Name && required.Accessibility == actual.Accessibility &&
            required.IsStatic == actual.IsStatic && required.IsReadonly == actual.IsReadonly &&
            TemplateTypesMatch(required.Type, actual.Type, requiredTemplate, actual.Template),
        PropertySymbol actual => required.Name == actual.Name &&
            required.Accessibility == actual.Accessibility &&
            required.IsStatic == actual.Declaration.IsStatic &&
            required.IsReadonly == actual.Declaration.IsReadonly &&
            TemplateTypeMatchesNominal(required.Type, actual.Type, requiredTemplate),
        InterfacePropertySymbol actual => required.Name == actual.Name &&
            required.Accessibility == Accessibility.Public && !required.IsStatic &&
            required.IsReadonly == actual.Declaration.IsReadonly &&
            TemplateTypeMatchesNominal(required.Type, actual.Type, requiredTemplate),
        _ => false,
    };

    private static bool IndexerShapeMatches(TemplateIndexerRequirementSymbol required,
        Symbol available, TemplateSymbol requiredTemplate) => available switch
    {
        TemplateIndexerRequirementSymbol actual =>
            required.Accessibility == actual.Accessibility &&
            required.IsStatic == actual.IsStatic && required.IsReadonly == actual.IsReadonly &&
            TemplateTypesMatch(required.Type, actual.Type, requiredTemplate, actual.Template) &&
            TemplateParametersMatch(required.Parameters, actual.Parameters, requiredTemplate, actual.Template),
        IndexerSymbol actual => required.Accessibility == actual.Accessibility &&
            required.IsStatic == actual.Declaration.IsStatic &&
            required.IsReadonly == actual.Declaration.IsReadonly &&
            TemplateTypeMatchesNominal(required.Type, actual.Type, requiredTemplate) &&
            TemplateParametersMatchNominal(required.Parameters, actual.Parameters, requiredTemplate),
        InterfaceIndexerSymbol actual => required.Accessibility == Accessibility.Public &&
            !required.IsStatic && required.IsReadonly == actual.Declaration.IsReadonly &&
            TemplateTypeMatchesNominal(required.Type, actual.Type, requiredTemplate) &&
            TemplateParametersMatchNominal(required.Parameters, actual.Parameters, requiredTemplate),
        _ => false,
    };

    private static bool HasGetter(Symbol symbol) => symbol switch
    {
        TemplatePropertyRequirementSymbol property => property.HasGetter,
        TemplateIndexerRequirementSymbol indexer => indexer.HasGetter,
        PropertySymbol property => property.Getter is not null,
        IndexerSymbol indexer => indexer.Getter is not null,
        InterfacePropertySymbol property => property.Getter is not null,
        InterfaceIndexerSymbol indexer => indexer.Getter is not null,
        _ => false,
    };

    private static bool HasSetter(Symbol symbol) => symbol switch
    {
        TemplatePropertyRequirementSymbol property => property.HasSetter,
        TemplateIndexerRequirementSymbol indexer => indexer.HasSetter,
        PropertySymbol property => property.Setter is not null,
        IndexerSymbol indexer => indexer.Setter is not null,
        InterfacePropertySymbol property => property.Setter is not null,
        InterfaceIndexerSymbol indexer => indexer.Setter is not null,
        _ => false,
    };

    private static bool TemplateParametersMatchNominal(ImmutableArray<ParameterSymbol> required,
        ImmutableArray<ParameterSymbol> available, TemplateSymbol requiredTemplate) =>
        required.Length == available.Length && required.Zip(available).All(pair =>
            pair.First.IsReadonly == pair.Second.IsReadonly &&
            TemplateTypeMatchesNominal(pair.First.Type, pair.Second.Type, requiredTemplate));

    private static bool TemplateTypeMatchesNominal(TypeSymbol required, TypeSymbol available,
        TemplateSymbol requiredTemplate)
    {
        if (required is TemplateSelfTypeSymbol self && ReferenceEquals(self.Template, requiredTemplate))
            return false;
        return (required, available) switch
        {
            (PointerTypeSymbol left, PointerTypeSymbol right) => left.IsReadonly == right.IsReadonly &&
                TemplateTypeMatchesNominal(left.ElementType, right.ElementType, requiredTemplate),
            (ReferenceTypeSymbol left, ReferenceTypeSymbol right) => left.IsReadonly == right.IsReadonly &&
                TemplateTypeMatchesNominal(left.ElementType, right.ElementType, requiredTemplate),
            (ArrayTypeSymbol left, ArrayTypeSymbol right) => left.Rank == right.Rank &&
                TemplateTypeMatchesNominal(left.ElementType, right.ElementType, requiredTemplate),
            (StructTypeSymbol { GenericDefinition: not null } left,
                StructTypeSymbol { GenericDefinition: not null } right) =>
                ReferenceEquals(left.GenericDefinition, right.GenericDefinition) &&
                left.TypeArguments.Length == right.TypeArguments.Length &&
                left.TypeArguments.Zip(right.TypeArguments).All(pair =>
                    TemplateTypeMatchesNominal(pair.First, pair.Second, requiredTemplate)),
            _ => TypeIdentity.AreSame(required, available),
        };
    }

    private static bool TemplateRequirementMatches(TemplateMemberRequirementSymbol required,
        TemplateMemberRequirementSymbol available, TemplateSymbol requiredTemplate,
        TemplateSymbol availableTemplate)
    {
        if (required.Accessibility != available.Accessibility) return false;
        return (required, available) switch
        {
            (TemplateMethodRequirementSymbol expected, TemplateMethodRequirementSymbol actual) =>
                expected.Name == actual.Name && expected.IsStatic == actual.IsStatic &&
                expected.IsReadonly == actual.IsReadonly &&
                TemplateTypesMatch(expected.ReturnType, actual.ReturnType, requiredTemplate, availableTemplate) &&
                TemplateParametersMatch(expected.Parameters, actual.Parameters, requiredTemplate, availableTemplate),
            (TemplateConstructorRequirementSymbol expected, TemplateConstructorRequirementSymbol actual) =>
                TemplateParametersMatch(expected.Parameters, actual.Parameters, requiredTemplate, availableTemplate),
            (TemplatePropertyRequirementSymbol expected, TemplatePropertyRequirementSymbol actual) =>
                expected.Name == actual.Name && expected.IsStatic == actual.IsStatic &&
                expected.IsReadonly == actual.IsReadonly &&
                (!expected.HasGetter || actual.HasGetter) && (!expected.HasSetter || actual.HasSetter) &&
                TemplateTypesMatch(expected.Type, actual.Type, requiredTemplate, availableTemplate),
            (TemplateIndexerRequirementSymbol expected, TemplateIndexerRequirementSymbol actual) =>
                expected.IsStatic == actual.IsStatic && expected.IsReadonly == actual.IsReadonly &&
                (!expected.HasGetter || actual.HasGetter) && (!expected.HasSetter || actual.HasSetter) &&
                TemplateTypesMatch(expected.Type, actual.Type, requiredTemplate, availableTemplate) &&
                TemplateParametersMatch(expected.Parameters, actual.Parameters, requiredTemplate, availableTemplate),
            _ => false,
        };
    }

    private static bool TemplateParametersMatch(ImmutableArray<ParameterSymbol> required,
        ImmutableArray<ParameterSymbol> available, TemplateSymbol requiredTemplate,
        TemplateSymbol availableTemplate) => required.Length == available.Length &&
        required.Zip(available).All(pair => pair.First.IsReadonly == pair.Second.IsReadonly &&
            TemplateTypesMatch(pair.First.Type, pair.Second.Type, requiredTemplate, availableTemplate));

    private static bool TemplateTypesMatch(TypeSymbol required, TypeSymbol available,
        TemplateSymbol requiredTemplate, TemplateSymbol availableTemplate)
    {
        if (required is TemplateSelfTypeSymbol requiredSelf &&
            ReferenceEquals(requiredSelf.Template, requiredTemplate))
            return available is TemplateSelfTypeSymbol availableSelf &&
                   ReferenceEquals(availableSelf.Template, availableTemplate);
        return (required, available) switch
        {
            (PointerTypeSymbol left, PointerTypeSymbol right) => left.IsReadonly == right.IsReadonly &&
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (ReferenceTypeSymbol left, ReferenceTypeSymbol right) => left.IsReadonly == right.IsReadonly &&
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (ArrayTypeSymbol left, ArrayTypeSymbol right) => left.Rank == right.Rank &&
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (AtomicTypeSymbol left, AtomicTypeSymbol right) =>
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (UniqueTypeSymbol left, UniqueTypeSymbol right) =>
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (SharedTypeSymbol left, SharedTypeSymbol right) =>
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (WeakTypeSymbol left, WeakTypeSymbol right) =>
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (StorageTypeSymbol left, StorageTypeSymbol right) =>
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (PinTypeSymbol left, PinTypeSymbol right) =>
                TemplateTypesMatch(left.ElementType, right.ElementType, requiredTemplate, availableTemplate),
            (StructTypeSymbol { GenericDefinition: not null } left,
                StructTypeSymbol { GenericDefinition: not null } right) =>
                ReferenceEquals(left.GenericDefinition, right.GenericDefinition) &&
                left.TypeArguments.Length == right.TypeArguments.Length &&
                left.TypeArguments.Zip(right.TypeArguments).All(pair =>
                    TemplateTypesMatch(pair.First, pair.Second, requiredTemplate, availableTemplate)),
            _ => TypeIdentity.AreSame(required, available),
        };
    }

    private static TemplateMatchResult MatchCore(StructTypeSymbol concrete, TemplateSymbol template)
    {
        var missing = ImmutableArray.CreateBuilder<TemplateRequirementFailure>();
        var mismatches = ImmutableArray.CreateBuilder<TemplateRequirementFailure>();
        var inaccessible = ImmutableArray.CreateBuilder<TemplateRequirementFailure>();

        foreach (TemplateMemberRequirementSymbol requirement in template.Members)
        {
            // Xenon permits T() when a struct declares no constructors. Treat
            // that implicit public default construction capability consistently.
            if (requirement is TemplateConstructorRequirementSymbol { Parameters.IsEmpty: true } &&
                concrete.Constructors.IsEmpty)
                continue;

            ImmutableArray<Symbol> candidates = Candidates(concrete, requirement);
            if (candidates.IsEmpty)
            {
                missing.Add(new TemplateRequirementFailure(requirement, candidates));
                continue;
            }

            Symbol[] matchingShape = candidates.Where(candidate =>
                HasRequiredShape(requirement, candidate, template, concrete)).ToArray();
            if (matchingShape.Length == 0)
            {
                mismatches.Add(new TemplateRequirementFailure(requirement, candidates));
                continue;
            }

            if (!matchingShape.Any(candidate => HasRequiredAccessibility(requirement, candidate)))
                inaccessible.Add(new TemplateRequirementFailure(requirement, [.. matchingShape]));
        }

        return new TemplateMatchResult(missing.ToImmutable(), mismatches.ToImmutable(), inaccessible.ToImmutable());
    }

    private static ImmutableArray<Symbol> Candidates(
        StructTypeSymbol concrete, TemplateMemberRequirementSymbol requirement) => requirement switch
    {
        TemplateConstructorRequirementSymbol => concrete.Constructors.Cast<Symbol>().ToImmutableArray(),
        TemplateMethodRequirementSymbol method => concrete.LookupMethods(method.Name).Cast<Symbol>().ToImmutableArray(),
        TemplatePropertyRequirementSymbol property => concrete.LookupMembers(property.Name)
            .OfType<PropertySymbol>().Cast<Symbol>().ToImmutableArray(),
        TemplateIndexerRequirementSymbol => concrete.LookupMembers("this")
            .OfType<IndexerSymbol>().Cast<Symbol>().ToImmutableArray(),
        _ => [],
    };

    private static bool HasRequiredShape(TemplateMemberRequirementSymbol requirement, Symbol candidate,
        TemplateSymbol template, StructTypeSymbol concrete) => (requirement, candidate) switch
    {
        (TemplateMethodRequirementSymbol expected, FunctionSymbol actual) =>
            actual.FunctionKind == FunctionKind.Method &&
            expected.IsStatic == actual.IsStatic &&
            expected.IsReadonly == actual.IsReadonly &&
            TypesMatch(expected.ReturnType, actual.ReturnType, template, concrete) &&
            ParametersMatch(expected.Parameters, actual.Parameters, template, concrete),
        (TemplateConstructorRequirementSymbol expected, FunctionSymbol actual) =>
            actual.FunctionKind == FunctionKind.Constructor &&
            ParametersMatch(expected.Parameters, actual.Parameters, template, concrete),
        (TemplatePropertyRequirementSymbol expected, PropertySymbol actual) =>
            expected.IsStatic == actual.Declaration.IsStatic &&
            expected.IsReadonly == actual.Declaration.IsReadonly &&
            (!expected.HasGetter || actual.Getter is not null) &&
            (!expected.HasSetter || actual.Setter is not null) &&
            TypesMatch(expected.Type, actual.Type, template, concrete),
        (TemplateIndexerRequirementSymbol expected, IndexerSymbol actual) =>
            expected.IsStatic == actual.Declaration.IsStatic &&
            expected.IsReadonly == actual.Declaration.IsReadonly &&
            (!expected.HasGetter || actual.Getter is not null) &&
            (!expected.HasSetter || actual.Setter is not null) &&
            TypesMatch(expected.Type, actual.Type, template, concrete) &&
            ParametersMatch(expected.Parameters, actual.Parameters, template, concrete),
        _ => false,
    };

    private static bool HasRequiredAccessibility(TemplateMemberRequirementSymbol requirement, Symbol candidate)
    {
        Accessibility actual = candidate switch
        {
            FunctionSymbol function => function.Accessibility,
            PropertySymbol property => property.Accessibility,
            IndexerSymbol indexer => indexer.Accessibility,
            _ => Accessibility.Private,
        };
        return requirement.Accessibility == actual;
    }

    private static bool ParametersMatch(ImmutableArray<ParameterSymbol> expected,
        ImmutableArray<ParameterSymbol> actual, TemplateSymbol template, StructTypeSymbol concrete) =>
        expected.Length == actual.Length && expected.Zip(actual).All(pair =>
            pair.First.IsReadonly == pair.Second.IsReadonly &&
            TypesMatch(pair.First.Type, pair.Second.Type, template, concrete));

    private static bool TypesMatch(TypeSymbol expected, TypeSymbol actual,
        TemplateSymbol template, StructTypeSymbol concrete)
    {
        if (expected is TemplateSelfTypeSymbol self && ReferenceEquals(self.Template, template))
            return TypeIdentity.AreSame(actual, concrete);
        return (expected, actual) switch
        {
            (PointerTypeSymbol left, PointerTypeSymbol right) =>
                left.IsReadonly == right.IsReadonly && TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (ReferenceTypeSymbol left, ReferenceTypeSymbol right) =>
                left.IsReadonly == right.IsReadonly && TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (ArrayTypeSymbol left, ArrayTypeSymbol right) =>
                left.Rank == right.Rank && TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (AtomicTypeSymbol left, AtomicTypeSymbol right) =>
                TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (UniqueTypeSymbol left, UniqueTypeSymbol right) =>
                TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (SharedTypeSymbol left, SharedTypeSymbol right) =>
                TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (WeakTypeSymbol left, WeakTypeSymbol right) =>
                TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (StorageTypeSymbol left, StorageTypeSymbol right) =>
                TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (PinTypeSymbol left, PinTypeSymbol right) =>
                TypesMatch(left.ElementType, right.ElementType, template, concrete),
            (StructTypeSymbol { GenericDefinition: not null } left,
                StructTypeSymbol { GenericDefinition: not null } right) =>
                ReferenceEquals(left.GenericDefinition, right.GenericDefinition) &&
                left.TypeArguments.Length == right.TypeArguments.Length &&
                left.TypeArguments.Zip(right.TypeArguments).All(pair =>
                    TypesMatch(pair.First, pair.Second, template, concrete)),
            _ => TypeIdentity.AreSame(expected, actual),
        };
    }
}

public sealed record GenericConstraintFailure(
    GenericConstraintSymbol Constraint,
    TemplateMatchResult? TemplateMatch = null);

public sealed class GenericConstraintValidationResult
{
    internal GenericConstraintValidationResult(ImmutableArray<GenericConstraintFailure> failures) => Failures = failures;
    public ImmutableArray<GenericConstraintFailure> Failures { get; }
    public bool IsValid => Failures.IsEmpty;
}

internal static class GenericConstraintDiagnostics
{
    public static string Format(GenericParameterSymbol parameter, TypeSymbol concrete,
        GenericConstraintValidationResult validation)
    {
        var details = new List<string>();
        foreach (GenericConstraintFailure failure in validation.Failures)
        {
            if (failure.Constraint.Target is not TemplateSymbol template || failure.TemplateMatch is null)
            {
                details.Add($"type '{concrete.ToDisplayString()}' does not satisfy constraint '{failure.Constraint.Target.Name}' for '{parameter.Name}'");
                continue;
            }

            details.Add($"type '{concrete.ToDisplayString()}' does not satisfy template '{template.Name}' for '{parameter.Name}'");
            AddFailures(details, "missing member", failure.TemplateMatch.MissingMembers);
            AddFailures(details, "member signature mismatch", failure.TemplateMatch.SignatureMismatches);
            AddFailures(details, "member is not accessible", failure.TemplateMatch.AccessibilityFailures);
        }
        return string.Join("; ", details);
    }

    private static void AddFailures(List<string> details, string description,
        ImmutableArray<TemplateRequirementFailure> failures)
    {
        foreach (TemplateRequirementFailure failure in failures.Take(3))
        {
            string required = failure.Requirement.ToDisplayString(SymbolDisplayFormat.Signature);
            if (failure.Candidates.IsEmpty)
                details.Add($"{description}: required {required}");
            else
                details.Add($"{description}: required {required}; found {string.Join(" | ", failure.Candidates.Take(3).Select(candidate => candidate.ToDisplayString(SymbolDisplayFormat.Signature)))}");
        }
    }
}

internal static class GenericConstraintGuarantees
{
    private static readonly TemplateConformanceMatcher Templates = new();

    public static bool IsGuaranteed(GenericParameterSymbol argument, GenericConstraintSymbol required) =>
        required.Target is TemplateSymbol requiredTemplate
            ? Templates.MissingRequirements(argument, requiredTemplate).IsEmpty
            : argument.Constraints.Any(available => IsGuaranteeCompatible(available, required));

    public static string GetFailureDetail(GenericParameterSymbol argument, GenericConstraintSymbol required)
    {
        if (required.Target is not TemplateSymbol requiredTemplate) return string.Empty;
        ImmutableArray<TemplateMemberRequirementSymbol> missing =
            Templates.MissingRequirements(argument, requiredTemplate);
        if (missing.IsEmpty) return string.Empty;
        return $"; missing guarantee: {string.Join(" | ", missing.Take(3).Select(requirement =>
            requirement.ToDisplayString(SymbolDisplayFormat.Signature)))}";
    }

    private static bool IsGuaranteeCompatible(GenericConstraintSymbol available,
        GenericConstraintSymbol required)
    {
        if (ReferenceEquals(available.Target, required.Target)) return true;
        return (available.Target, required.Target) switch
        {
            (StructTypeSymbol actual, StructTypeSymbol target) => IsOrDerivesFrom(actual, target),
            (InterfaceTypeSymbol actual, InterfaceTypeSymbol target) => actual.IsOrInherits(target),
            (StructTypeSymbol actual, InterfaceTypeSymbol target) => actual.Implements(target),
            (TemplateSymbol actual, TemplateSymbol target) => Templates.Guarantees(actual, target),
            _ => false,
        };
    }

    private static bool IsOrDerivesFrom(StructTypeSymbol actual, StructTypeSymbol target)
    {
        for (StructTypeSymbol? current = actual; current is not null; current = current.BaseType)
            if (TypeIdentity.AreSame(current, target)) return true;
        return false;
    }
}

public sealed class GenericConstraintValidator
{
    private readonly TemplateConformanceMatcher _templates;

    public GenericConstraintValidator(TemplateConformanceMatcher? templates = null) =>
        _templates = templates ?? new TemplateConformanceMatcher();

    public GenericConstraintValidationResult Validate(GenericParameterSymbol parameter, TypeSymbol concrete)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        ArgumentNullException.ThrowIfNull(concrete);
        var failures = ImmutableArray.CreateBuilder<GenericConstraintFailure>();
        foreach (GenericConstraintSymbol constraint in parameter.Constraints)
        {
            switch (constraint.Kind)
            {
                case GenericConstraintKind.BaseStruct when constraint.Target is StructTypeSymbol required:
                    if (concrete is not StructTypeSymbol concreteStruct || !IsOrDerivesFrom(concreteStruct, required))
                        failures.Add(new GenericConstraintFailure(constraint));
                    break;
                case GenericConstraintKind.Interface when constraint.Target is InterfaceTypeSymbol required:
                    if (concrete is not StructTypeSymbol interfaceStruct || !interfaceStruct.Implements(required))
                        failures.Add(new GenericConstraintFailure(constraint));
                    break;
                case GenericConstraintKind.StructuralTemplate when constraint.Target is TemplateSymbol required:
                    if (concrete is not StructTypeSymbol templateStruct)
                    {
                        failures.Add(new GenericConstraintFailure(constraint));
                        break;
                    }
                    TemplateMatchResult match = _templates.Match(templateStruct, required);
                    if (!match.IsValid) failures.Add(new GenericConstraintFailure(constraint, match));
                    break;
            }
        }
        return new GenericConstraintValidationResult(failures.ToImmutable());
    }

    private static bool IsOrDerivesFrom(StructTypeSymbol concrete, StructTypeSymbol required)
    {
        for (StructTypeSymbol? current = concrete; current is not null; current = current.BaseType)
            if (TypeIdentity.AreSame(current, required)) return true;
        return false;
    }
}
