namespace Xenon.Compiler.Semantics.Symbols;

internal static class TypeFacts
{
    // Raw aggregate storage does not invoke nested constructors/initializers.
    public static bool ContainsReferenceStorage(TypeSymbol type) => ContainsReferenceStorage(type, []);

    private static bool ContainsReferenceStorage(TypeSymbol type, HashSet<TypeSymbol> visited) =>
        type is ReferenceTypeSymbol || type is IFieldStorageTypeSymbol structure && visited.Add(type) &&
        structure.AllInstanceFields.Any(field => ContainsReferenceStorage(field.Type, visited));

    public static bool IsNumeric(TypeSymbol type) =>
        type is PrimitiveTypeSymbol { IsInteger: true } or PrimitiveTypeSymbol { IsFloatingPoint: true };

    public static bool IsInteger(TypeSymbol type) => type is PrimitiveTypeSymbol { IsInteger: true };

    public static bool CanCompareEquality(TypeSymbol left, TypeSymbol right)
    {
        if (TypeIdentity.AreSame(left, right) && (IsNumeric(left) || left is EnumTypeSymbol || TypeIdentity.AreSame(left, BuiltinTypes.Bool)))
            return true;
        if (left is PointerTypeSymbol && TypeIdentity.AreSame(right, BuiltinTypes.Null) ||
            right is PointerTypeSymbol && TypeIdentity.AreSame(left, BuiltinTypes.Null))
            return true;
        if (left is not PointerTypeSymbol a || right is not PointerTypeSymbol b) return false;
        return TypeIdentity.AreSame(a.ElementType, b.ElementType) ||
            a.ElementType is StructTypeSymbol aStruct && b.ElementType is StructTypeSymbol bStruct &&
            (GetInheritanceDistance(aStruct, bStruct) is not null || GetInheritanceDistance(bStruct, aStruct) is not null);
    }

    public static bool CanExplicitlyCast(TypeSymbol target, TypeSymbol source) =>
        (IsNumeric(target) && IsNumeric(source)) ||
        (target is EnumTypeSymbol && IsInteger(source)) ||
        (source is EnumTypeSymbol && IsInteger(target)) ||
        (target is EnumTypeSymbol && TypeIdentity.AreSame(target, source));

    public static bool CanAssign(TypeSymbol destination, TypeSymbol source) =>
        GetImplicitConversionCost(destination, source) is not null;

    public static int? GetImplicitConversionCost(TypeSymbol destination, TypeSymbol source)
    {
        if (TypeIdentity.AreSame(destination, BuiltinTypes.Error) || TypeIdentity.AreSame(source, BuiltinTypes.Error))
        {
            return 0;
        }

        if (TypeIdentity.AreSame(destination, source))
        {
            return 0;
        }

        if (destination is PointerTypeSymbol && TypeIdentity.AreSame(source, BuiltinTypes.Null))
            return 1000;

        if (destination is PointerTypeSymbol { IsReadonly: var destinationReadonly } destinationPointer &&
            source is PointerTypeSymbol { IsReadonly: var sourceReadonly } sourcePointer &&
            (!sourceReadonly || destinationReadonly))
        {
            int readonlyQualificationCost = sourceReadonly == destinationReadonly ? 0 : 1;
            if (TypeIdentity.AreSame(destinationPointer.ElementType, sourcePointer.ElementType))
                return readonlyQualificationCost;
            if (destinationPointer.ElementType is StructTypeSymbol destinationStruct &&
                sourcePointer.ElementType is StructTypeSymbol sourceStruct &&
                GetInheritanceDistance(sourceStruct, destinationStruct) is int inheritanceDistance)
            {
                return inheritanceDistance + readonlyQualificationCost;
            }
        }

        if (destination is InterfaceTypeSymbol destinationInterface && source is StructTypeSymbol interfaceSourceStruct && interfaceSourceStruct.Implements(destinationInterface))
            return 100;

        return null;
    }

    public static int? GetReferenceBindingCost(ReferenceTypeSymbol destination, TypeSymbol source)
    {
        int readonlyQualificationCost = 0;
        if (source is ReferenceTypeSymbol sourceReference)
        {
            if (sourceReference.IsReadonly && !destination.IsReadonly)
                return null;
            readonlyQualificationCost = sourceReference.IsReadonly == destination.IsReadonly ? 0 : 1;
            source = sourceReference.ElementType;
        }

        if (TypeIdentity.AreSame(destination.ElementType, source))
            return readonlyQualificationCost;

        if (destination.ElementType is StructTypeSymbol destinationStruct &&
            source is StructTypeSymbol sourceStruct &&
            GetInheritanceDistance(sourceStruct, destinationStruct) is int inheritanceDistance)
        {
            return readonlyQualificationCost + inheritanceDistance;
        }

        if (destination.ElementType is InterfaceTypeSymbol destinationInterface)
        {
            if (source is StructTypeSymbol sourceStructType && sourceStructType.Implements(destinationInterface))
                return readonlyQualificationCost + 100;
            if (source is InterfaceTypeSymbol sourceInterface && sourceInterface.IsOrInherits(destinationInterface))
                return readonlyQualificationCost + 1;
        }

        return null;
    }

    private static int? GetInheritanceDistance(StructTypeSymbol candidate, StructTypeSymbol expected)
    {
        int distance = 0;
        for (StructTypeSymbol? current = candidate; current is not null; current = current.BaseType, distance++)
            if (TypeIdentity.AreSame(current, expected)) return distance;
        return null;
    }
}
