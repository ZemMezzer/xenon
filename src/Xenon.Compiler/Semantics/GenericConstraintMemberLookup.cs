using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

internal sealed record GenericMethodMember(
    Symbol Symbol,
    TypeSymbol ReturnType,
    ImmutableArray<TypeSymbol> ParameterTypes,
    bool IsStatic,
    bool IsReadonly);

internal sealed record GenericFieldMember(
    FieldSymbol Symbol,
    TypeSymbol Type,
    bool IsStatic,
    bool IsReadonly);

internal sealed record GenericPropertyMember(
    Symbol Symbol,
    TypeSymbol Type,
    bool HasGetter,
    bool HasSetter,
    bool IsStatic,
    bool IsReadonly);

internal sealed record GenericIndexerMember(
    Symbol Symbol,
    TypeSymbol Type,
    ImmutableArray<TypeSymbol> ParameterTypes,
    bool HasGetter,
    bool HasSetter,
    bool IsReadonly);

internal sealed record GenericConstructorMember(
    Symbol Symbol,
    ImmutableArray<TypeSymbol> ParameterTypes);

internal static class GenericConstraintMemberLookup
{
    public static IEnumerable<Symbol> GetMembers(GenericParameterSymbol parameter)
    {
        foreach (GenericConstraintSymbol constraint in parameter.Constraints)
        {
            switch (constraint.Target)
            {
                case StructTypeSymbol structure:
                    for (StructTypeSymbol? current = structure; current is not null; current = current.BaseType)
                        foreach (Symbol member in current.GetMembers())
                            if (IsConstraintAccessible(member)) yield return member;
                    break;
                case InterfaceTypeSymbol @interface:
                    foreach (InterfaceTypeSymbol current in @interface.SelfAndBaseInterfaces)
                        foreach (Symbol member in current.GetMembers())
                            yield return member;
                    break;
                case TemplateSymbol template:
                    foreach (TemplateMemberRequirementSymbol member in template.Members)
                        if (member.IsPublic) yield return member;
                    break;
            }
        }
    }

    public static IEnumerable<GenericFieldMember> GetFields(GenericParameterSymbol parameter, string name) =>
        GetMembers(parameter).OfType<FieldSymbol>()
            .Where(field => field.Name == name)
            .Select(field => new GenericFieldMember(field, field.Type, field.IsStatic, field.IsReadonly));

    public static IEnumerable<GenericMethodMember> GetMethods(GenericParameterSymbol parameter, string name,
        TypeFactory types, GenericStructSpecializer? specializer) => GetMembers(parameter).Where(member => member.Name == name).SelectMany<Symbol, GenericMethodMember>(member => member switch
    {
        FunctionSymbol method when method.FunctionKind == FunctionKind.Method =>
            [new GenericMethodMember(method, method.ReturnType,
                method.Parameters.Select(value => value.Type).ToImmutableArray(), method.IsStatic, method.IsReadonly)],
        TemplateMethodRequirementSymbol method =>
            [new GenericMethodMember(method,
                SubstituteTemplateSelf(method.ReturnType, method.Template, parameter, types, specializer),
                method.Parameters.Select(value => SubstituteTemplateSelf(value.Type, method.Template, parameter, types, specializer)).ToImmutableArray(),
                method.IsStatic, method.IsReadonly)],
        _ => [],
    });

    public static IEnumerable<GenericPropertyMember> GetProperties(GenericParameterSymbol parameter, string name,
        TypeFactory types, GenericStructSpecializer? specializer) => GetMembers(parameter).Where(member => member.Name == name).SelectMany<Symbol, GenericPropertyMember>(member => member switch
    {
        PropertySymbol property => [new GenericPropertyMember(property, property.Type,
            property.Getter is not null, property.Setter is not null, false, property.Getter?.IsReadonly == true)],
        InterfacePropertySymbol property => [new GenericPropertyMember(property, property.Type,
            property.Getter is not null, property.Setter is not null, false, property.Getter?.IsReadonly == true)],
        TemplatePropertyRequirementSymbol property => [new GenericPropertyMember(property,
            SubstituteTemplateSelf(property.Type, property.Template, parameter, types, specializer), property.HasGetter,
            property.HasSetter, property.IsStatic, property.IsReadonly)],
        _ => [],
    });

    public static IEnumerable<GenericIndexerMember> GetIndexers(GenericParameterSymbol parameter,
        TypeFactory types, GenericStructSpecializer? specializer) => GetMembers(parameter).SelectMany<Symbol, GenericIndexerMember>(member => member switch
    {
        IndexerSymbol indexer => [new GenericIndexerMember(indexer, indexer.Type,
            indexer.Parameters.Select(value => value.Type).ToImmutableArray(), indexer.Getter is not null,
            indexer.Setter is not null, indexer.Getter?.IsReadonly == true)],
        InterfaceIndexerSymbol indexer => [new GenericIndexerMember(indexer, indexer.Type,
            indexer.Parameters.Select(value => value.Type).ToImmutableArray(), indexer.Getter is not null,
            indexer.Setter is not null, indexer.Getter?.IsReadonly == true)],
        TemplateIndexerRequirementSymbol indexer => [new GenericIndexerMember(indexer,
            SubstituteTemplateSelf(indexer.Type, indexer.Template, parameter, types, specializer),
            indexer.Parameters.Select(value => SubstituteTemplateSelf(value.Type, indexer.Template, parameter, types, specializer)).ToImmutableArray(),
            indexer.HasGetter, indexer.HasSetter, indexer.IsReadonly)],
        _ => [],
    });

    public static IEnumerable<GenericConstructorMember> GetConstructors(GenericParameterSymbol parameter,
        TypeFactory types, GenericStructSpecializer? specializer) => parameter.Constraints
        .Where(constraint => constraint.Target is TemplateSymbol)
        .SelectMany(constraint => ((TemplateSymbol)constraint.Target).Members.OfType<TemplateConstructorRequirementSymbol>())
        .Where(constructor => constructor.IsPublic)
        .Select(constructor => new GenericConstructorMember(constructor,
            constructor.Parameters.Select(value => SubstituteTemplateSelf(value.Type, constructor.Template, parameter, types, specializer)).ToImmutableArray()));

    private static TypeSymbol SubstituteTemplateSelf(TypeSymbol type, TemplateSymbol template,
        GenericParameterSymbol parameter, TypeFactory types, GenericStructSpecializer? specializer) => type switch
    {
        TemplateSelfTypeSymbol self when ReferenceEquals(self.Template, template) => parameter,
        StructTypeSymbol { GenericDefinition: not null } constructed when specializer is not null =>
            SubstituteConstructedTemplateSelf(constructed, template, parameter, types, specializer),
        PointerTypeSymbol pointer => types.PointerTo(
            SubstituteTemplateSelf(pointer.ElementType, template, parameter, types, specializer), pointer.IsReadonly),
        ReferenceTypeSymbol reference => types.ReferenceTo(
            SubstituteTemplateSelf(reference.ElementType, template, parameter, types, specializer), reference.IsReadonly),
        ArrayTypeSymbol array => types.ArrayOf(
            SubstituteTemplateSelf(array.ElementType, template, parameter, types, specializer), array.Rank),
        UniqueTypeSymbol unique => types.UniqueOf(
            SubstituteTemplateSelf(unique.ElementType, template, parameter, types, specializer)),
        SharedTypeSymbol shared => types.SharedOf(
            SubstituteTemplateSelf(shared.ElementType, template, parameter, types, specializer)),
        WeakTypeSymbol weak => types.WeakOf(
            SubstituteTemplateSelf(weak.ElementType, template, parameter, types, specializer)),
        _ => type,
    };

    private static TypeSymbol SubstituteConstructedTemplateSelf(StructTypeSymbol constructed,
        TemplateSymbol template, GenericParameterSymbol parameter, TypeFactory types,
        GenericStructSpecializer specializer)
    {
        ImmutableArray<TypeSymbol> arguments = constructed.TypeArguments.Select(argument =>
            SubstituteTemplateSelf(argument, template, parameter, types, specializer)).ToImmutableArray();
        return (TypeSymbol?)specializer.GetOrCreate(constructed.GenericDefinition!, arguments,
            constructed.Declaration.IdentifierToken.Location) ?? BuiltinTypes.Error;
    }

    private static bool IsConstraintAccessible(Symbol member) => member switch
    {
        FieldSymbol field => field.IsPublic,
        FunctionSymbol function => function.IsPublic,
        PropertySymbol property => property.IsPublic,
        IndexerSymbol indexer => indexer.IsPublic,
        _ => true,
    };
}
