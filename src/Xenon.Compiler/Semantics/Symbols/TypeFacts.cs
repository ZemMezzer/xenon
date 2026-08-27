namespace Xenon.Compiler.Semantics.Symbols;

internal static class TypeFacts
{
    public static bool IsNumeric(TypeSymbol type) =>
        type is PrimitiveTypeSymbol { IsInteger: true } or PrimitiveTypeSymbol { IsFloatingPoint: true };

    public static bool IsInteger(TypeSymbol type) => type is PrimitiveTypeSymbol { IsInteger: true };

    public static bool CanExplicitlyCast(TypeSymbol target, TypeSymbol source) =>
        (IsNumeric(target) && IsNumeric(source)) ||
        (target is EnumTypeSymbol && IsInteger(source)) ||
        (source is EnumTypeSymbol && IsInteger(target)) ||
        (target is EnumTypeSymbol && ReferenceEquals(target, source));

    public static bool CanAssign(TypeSymbol destination, TypeSymbol source) =>
        GetImplicitConversionCost(destination, source) is not null;

    public static int? GetImplicitConversionCost(TypeSymbol destination, TypeSymbol source)
    {
        if (ReferenceEquals(destination, BuiltinTypes.Error) || ReferenceEquals(source, BuiltinTypes.Error))
        {
            return 0;
        }

        if (ReferenceEquals(destination, source))
        {
            return 0;
        }

        if (destination is PointerTypeSymbol && ReferenceEquals(source, BuiltinTypes.Null))
            return 1000;

        if (destination is PointerTypeSymbol { IsReadonly: var destinationReadonly } destinationPointer &&
            source is PointerTypeSymbol { IsReadonly: var sourceReadonly } sourcePointer &&
            (!sourceReadonly || destinationReadonly))
        {
            int readonlyQualificationCost = sourceReadonly == destinationReadonly ? 0 : 1;
            if (ReferenceEquals(destinationPointer.ElementType, sourcePointer.ElementType))
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

        if (ReferenceEquals(destination.ElementType, source))
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
            if (ReferenceEquals(current, expected)) return distance;
        return null;
    }
}
