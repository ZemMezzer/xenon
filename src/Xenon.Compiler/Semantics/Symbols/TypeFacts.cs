using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

public enum Copyability
{
    Copyable,
    NotGuaranteed,
    NonCopyable,
}

internal sealed record CopyabilityFailure(
    TypeSymbol Type,
    ImmutableArray<FieldSymbol> FieldPath,
    Copyability Kind);

internal sealed record ValueEqualityFailure(
    TypeSymbol Type,
    ImmutableArray<FieldSymbol> FieldPath,
    bool ContainsAtomicStorage);

public static class TypeFacts
{
    public static bool CanCopy(TypeSymbol type) => GetCopyability(type) == Copyability.Copyable;

    public static Copyability GetCopyability(TypeSymbol type) =>
        GetCopyability(type, [], out _);

    internal static CopyabilityFailure? GetCopyabilityFailure(TypeSymbol type)
    {
        GetCopyability(type, [], out CopyabilityFailure? failure);
        return failure;
    }

    private static Copyability GetCopyability(
        TypeSymbol type,
        HashSet<TypeSymbol> active,
        out CopyabilityFailure? failure)
    {
        failure = null;
        if (TypeIdentity.AreSame(type, BuiltinTypes.Error)) return Copyability.Copyable;
        if (type is UniqueTypeSymbol)
        {
            failure = new CopyabilityFailure(type, [], Copyability.NonCopyable);
            return Copyability.NonCopyable;
        }
        if (type is ReferenceTypeSymbol { IsReadonly: false })
        {
            failure = new CopyabilityFailure(type, [], Copyability.NonCopyable);
            return Copyability.NonCopyable;
        }
        if (type is ReferenceTypeSymbol)
            return Copyability.Copyable;
        if (type is AtomicTypeSymbol)
        {
            failure = new CopyabilityFailure(type, [], Copyability.NonCopyable);
            return Copyability.NonCopyable;
        }
        if (type is SharedTypeSymbol or WeakTypeSymbol)
            return Copyability.Copyable;
        if (type is StorageTypeSymbol or PinTypeSymbol)
        {
            failure = new CopyabilityFailure(type, [], Copyability.NonCopyable);
            return Copyability.NonCopyable;
        }
        if (type is GenericParameterSymbol)
        {
            failure = new CopyabilityFailure(type, [], Copyability.NotGuaranteed);
            return Copyability.NotGuaranteed;
        }
        if (type is not StructTypeSymbol structure || !active.Add(type))
            return Copyability.Copyable;

        try
        {
            if (structure.BaseType is { } baseType)
            {
                Copyability baseResult = GetCopyability(baseType, active, out failure);
                if (baseResult != Copyability.Copyable) return baseResult;
            }
            foreach (FieldSymbol field in structure.Fields)
            {
                Copyability fieldResult = GetCopyability(field.Type, active, out failure);
                if (fieldResult == Copyability.Copyable) continue;
                failure = failure! with { FieldPath = failure.FieldPath.Insert(0, field) };
                return fieldResult;
            }
            return Copyability.Copyable;
        }
        finally
        {
            active.Remove(type);
        }
    }

    public static bool RequiresDestruction(TypeSymbol type) => RequiresDestruction(type, []);

    public static bool CanMove(TypeSymbol type) => !IsPinned(type);

    public static bool CanRelocate(TypeSymbol type) => CanMove(type) && !ContainsAtomicStorage(type) &&
        (type is not StructTypeSymbol structure || !structure.AllInstanceFields.Any(field => IsPinned(field.Type)));

    /// <summary>
    /// True when the value physically contains atomic wrapper storage. Pointer,
    /// array and ownership handles do not inline the storage they refer to.
    /// </summary>
    public static bool ContainsAtomicStorage(TypeSymbol type) => ContainsAtomicStorage(type, []);

    private static bool ContainsAtomicStorage(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        if (type is AtomicTypeSymbol) return true;
        if (type is StorageTypeSymbol storage) return ContainsAtomicStorage(storage.ElementType, visited);
        if (type is PinTypeSymbol pin) return ContainsAtomicStorage(pin.ElementType, visited);
        if (type is not StructTypeSymbol structure || !visited.Add(structure)) return false;
        try
        {
            return structure.BaseType is not null && ContainsAtomicStorage(structure.BaseType, visited) ||
                structure.Fields.Any(field => ContainsAtomicStorage(field.Type, visited));
        }
        finally
        {
            visited.Remove(structure);
        }
    }

    /// <summary>Follows native pointer/reference boundaries to prevent exposing atomic layout.</summary>
    public static bool ExposesAtomicStorageToNativeAbi(TypeSymbol type) =>
        ExposesAtomicStorageToNativeAbi(type, []);

    private static bool ExposesAtomicStorageToNativeAbi(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        if (type is AtomicTypeSymbol) return true;
        if (type is PointerTypeSymbol pointer)
            return ExposesAtomicStorageToNativeAbi(pointer.ElementType, visited);
        if (type is ReferenceTypeSymbol reference)
            return ExposesAtomicStorageToNativeAbi(reference.ElementType, visited);
        if (type is StorageTypeSymbol storage)
            return ExposesAtomicStorageToNativeAbi(storage.ElementType, visited);
        if (type is PinTypeSymbol pin)
            return ExposesAtomicStorageToNativeAbi(pin.ElementType, visited);
        if (type is not StructTypeSymbol structure || !visited.Add(structure)) return false;
        try
        {
            return structure.BaseType is not null &&
                    ExposesAtomicStorageToNativeAbi(structure.BaseType, visited) ||
                structure.Fields.Any(field => ExposesAtomicStorageToNativeAbi(field.Type, visited));
        }
        finally
        {
            visited.Remove(structure);
        }
    }

    public static bool HasAutomaticDestructor(TypeSymbol type) => GetCompleteDestructor(type) is not null;

    public static bool IsPinned(TypeSymbol type) => IsPinned(type, []);

    private static bool IsPinned(TypeSymbol type, HashSet<TypeSymbol> visited) => type switch
    {
        PinTypeSymbol => true,
        LifetimeModifierTypeSymbol modifier => IsPinned(modifier.ElementType, visited),
        StructTypeSymbol structure when visited.Add(structure) =>
            structure.AllInstanceFields.Any(field => IsPinned(field.Type, visited)),
        _ => false,
    };

    public static bool IsStorageType(TypeSymbol type)
    {
        while (type is PinTypeSymbol pin) type = pin.ElementType;
        return type is StorageTypeSymbol;
    }

    private static bool RequiresDestruction(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        if (type is AtomicTypeSymbol atomic) return RequiresDestruction(atomic.ElementType, visited);
        if (type is StorageTypeSymbol storage) return RequiresDestruction(storage.ElementType, visited);
        if (type is PinTypeSymbol pin) return RequiresDestruction(pin.ElementType, visited);
        if (type is OwnershipTypeSymbol) return true;
        if (type is not StructTypeSymbol structure || !visited.Add(type)) return false;
        return structure.Destructor is not null ||
            structure.BaseType is not null && RequiresDestruction(structure.BaseType, visited) ||
            structure.Fields.Any(field => RequiresDestruction(field.Type, visited));
    }

    public static FunctionSymbol? GetCompleteDestructor(TypeSymbol type) => type switch
    {
        AtomicTypeSymbol atomic => GetCompleteDestructor(atomic.ElementType),
        UniqueTypeSymbol unique => unique.CompleteDestructor,
        SharedTypeSymbol shared => shared.CompleteDestructor,
        WeakTypeSymbol weak => weak.CompleteDestructor,
        StorageTypeSymbol storage => storage.CompleteDestructor,
        PinTypeSymbol pin => GetCompleteDestructor(pin.ElementType),
        StructTypeSymbol structure => structure.CompleteDestructor,
        _ => null,
    };

    // Raw aggregate storage does not invoke nested constructors/initializers.
    public static bool ContainsReferenceStorage(TypeSymbol type) => ContainsReferenceStorage(type, []);

    private static bool ContainsReferenceStorage(TypeSymbol type, HashSet<TypeSymbol> visited) =>
        type is ReferenceTypeSymbol ||
        type is StorageTypeSymbol storage && ContainsReferenceStorage(storage.ElementType, visited) ||
        type is PinTypeSymbol pin && ContainsReferenceStorage(pin.ElementType, visited) ||
        type is IFieldStorageTypeSymbol structure && visited.Add(type) &&
        structure.AllInstanceFields.Any(field => ContainsReferenceStorage(field.Type, visited));

    public static bool IsNumeric(TypeSymbol type) =>
        type is PrimitiveTypeSymbol { IsInteger: true } or PrimitiveTypeSymbol { IsFloatingPoint: true };

    public static bool IsInteger(TypeSymbol type) => type is PrimitiveTypeSymbol { IsInteger: true };

    internal static ValueEqualityFailure? GetValueEqualityFailure(TypeSymbol type) =>
        GetValueEqualityFailure(type, []);

    private static ValueEqualityFailure? GetValueEqualityFailure(
        TypeSymbol type,
        HashSet<TypeSymbol> visited)
    {
        if (type is AtomicTypeSymbol)
            return new ValueEqualityFailure(type, [], ContainsAtomicStorage: true);
        if ((type is PrimitiveTypeSymbol primitive && !TypeIdentity.AreSame(primitive, BuiltinTypes.Void)) ||
            type is EnumTypeSymbol or PointerTypeSymbol or ArrayTypeSymbol or SharedTypeSymbol or WeakTypeSymbol)
            return null;
        if (type is not StructTypeSymbol structure || !visited.Add(structure))
            return new ValueEqualityFailure(type, [], ContainsAtomicStorage: false);
        try
        {
            if (structure.BaseType is { } baseType &&
                GetValueEqualityFailure(baseType, visited) is { } baseFailure)
                return baseFailure;
            foreach (FieldSymbol field in structure.Fields)
            {
                if (GetValueEqualityFailure(field.Type, visited) is not { } failure) continue;
                return failure with { FieldPath = [field, .. failure.FieldPath] };
            }
            return null;
        }
        finally
        {
            visited.Remove(structure);
        }
    }

    public static bool CanCompareEquality(TypeSymbol left, TypeSymbol right)
    {
        if (TypeIdentity.AreSame(left, right) && GetValueEqualityFailure(left) is null)
            return true;
        if (left is PointerTypeSymbol && TypeIdentity.AreSame(right, BuiltinTypes.Null) ||
            right is PointerTypeSymbol && TypeIdentity.AreSame(left, BuiltinTypes.Null))
            return true;
        if (left is SharedTypeSymbol && TypeIdentity.AreSame(right, BuiltinTypes.Null) ||
            right is SharedTypeSymbol && TypeIdentity.AreSame(left, BuiltinTypes.Null))
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

        if (source is AtomicTypeSymbol atomic && AtomicTypeRules.SupportsOperations(atomic.ElementType) &&
            GetImplicitConversionCost(destination, atomic.ElementType) is int atomicReadCost)
            return atomicReadCost + 1;

        if (destination is PinTypeSymbol pin && TypeIdentity.AreSame(pin.ElementType, source))
            return 1;

        if (destination is PointerTypeSymbol && TypeIdentity.AreSame(source, BuiltinTypes.Null))
            return 1000;

        if (destination is SharedTypeSymbol && TypeIdentity.AreSame(source, BuiltinTypes.Null))
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
