namespace Xenon.Compiler.Semantics.Symbols;

internal static class TypeFacts
{
    public static bool IsNumeric(TypeSymbol type) =>
        type is PrimitiveTypeSymbol { IsInteger: true } or PrimitiveTypeSymbol { IsFloatingPoint: true };

    public static bool IsInteger(TypeSymbol type) => type is PrimitiveTypeSymbol { IsInteger: true };

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

        if (destination is PointerTypeSymbol { IsConst: var destinationConst, ElementType: StructTypeSymbol destinationStruct } &&
            source is PointerTypeSymbol { IsConst: var sourceConst, ElementType: StructTypeSymbol sourceStruct } &&
            (!sourceConst || destinationConst) &&
            GetInheritanceDistance(sourceStruct, destinationStruct) is int inheritanceDistance)
        {
            int constQualificationCost = sourceConst == destinationConst ? 0 : 1;
            return inheritanceDistance + constQualificationCost;
        }

        if (destination is InterfaceTypeSymbol destinationInterface && source is StructTypeSymbol interfaceSourceStruct && interfaceSourceStruct.Implements(destinationInterface))
            return 100;

        return null;
    }

    public static int? GetReferenceBindingCost(ReferenceTypeSymbol destination, TypeSymbol source)
    {
        int constQualificationCost = 0;
        if (source is ReferenceTypeSymbol sourceReference)
        {
            if (sourceReference.IsConst && !destination.IsConst)
                return null;
            constQualificationCost = sourceReference.IsConst == destination.IsConst ? 0 : 1;
            source = sourceReference.ElementType;
        }

        if (ReferenceEquals(destination.ElementType, source))
            return constQualificationCost;

        if (destination.ElementType is StructTypeSymbol destinationStruct &&
            source is StructTypeSymbol sourceStruct &&
            GetInheritanceDistance(sourceStruct, destinationStruct) is int inheritanceDistance)
        {
            return constQualificationCost + inheritanceDistance;
        }

        if (destination.ElementType is InterfaceTypeSymbol destinationInterface)
        {
            if (source is StructTypeSymbol sourceStructType && sourceStructType.Implements(destinationInterface))
                return constQualificationCost + 100;
            if (source is InterfaceTypeSymbol sourceInterface && sourceInterface.IsOrInherits(destinationInterface))
                return constQualificationCost + 1;
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
