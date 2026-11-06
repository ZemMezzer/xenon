using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics.Symbols;

public static class AllocationRules
{
    public static FunctionSymbol? SelectConstructor(StructTypeSymbol type, TypeSymbol? allocator)
        => SelectConstructor(type, allocator, out _);

    private static FunctionSymbol? SelectConstructor(StructTypeSymbol type, TypeSymbol? allocator, out bool ambiguous)
    {
        ambiguous = false;
        if (allocator is not null && allocator is not PointerTypeSymbol { IsReadonly: false, ElementType: StructTypeSymbol { IsAllocator: true } }) return null;
        FunctionSymbol? best = null;
        int bestCost = int.MaxValue;
        // Use the same standard conversion ranking as ordinary overloads. The single
        // allocator-pointer parameter permits identity and ancestor conversions only.
        foreach (FunctionSymbol candidate in type.AllocationConstructors)
        {
            int cost;
            if (allocator is null)
            {
                if (!candidate.Parameters.IsEmpty) continue;
                cost = 0;
            }
            else
            {
                if (candidate.Parameters.Length != 1 ||
                    candidate.Parameters[0].Type is not PointerTypeSymbol { IsReadonly: false, ElementType: StructTypeSymbol { IsAllocator: true } } parameter ||
                    TypeFacts.GetImplicitConversionCost(parameter, allocator) is not int conversionCost) continue;
                cost = conversionCost;
            }
            if (cost < bestCost)
            {
                best = candidate;
                bestCost = cost;
                ambiguous = false;
            }
            else if (cost == bestCost) ambiguous = true;
        }
        return ambiguous ? null : best;
    }

    public static void ValidateConstruction(TypeSymbol? type, TypeSymbol? allocator, Symbol context, DiagnosticBag diagnostics, TextLocation location)
    {
        if (allocator is not null)
        {
            if (allocator is not PointerTypeSymbol { IsReadonly: false, ElementType: StructTypeSymbol { IsAllocator: true } provider })
            {
                diagnostics.Report(location, "new(allocator) requires a mutable pointer to an allocator type", DiagnosticIds.InvalidAllocatorOperand);
                return;
            }
            if (provider.AllocationOperator is null || provider.DeallocationOperator is null)
            {
                diagnostics.Report(location, "new(allocator) requires a declared or inherited allocation/deallocation pair, including through abstract allocator pointers", DiagnosticIds.InvalidAllocatorOperand);
                return;
            }
            foreach (FunctionSymbol operation in new[] { provider.AllocationOperator!, provider.DeallocationOperator! })
                if (!AccessibilityRules.IsAccessible(operation, context))
                    diagnostics.Report(location, "allocation requires access to both allocator operations", DiagnosticIds.InaccessibleSymbol);
        }
        while (type is AtomicTypeSymbol atomic) type = atomic.ElementType;
        if (type is not StructTypeSymbol structure) return;
        for (StructTypeSymbol? current = structure; current is not null; current = current.BaseType)
        {
            if (current.AllocationConstructors.IsEmpty) continue;
            FunctionSymbol? selected = SelectConstructor(current, allocator, out bool ambiguous);
            if (ambiguous)
                diagnostics.Report(location, $"allocation context constructor of '{current.Name}' is ambiguous for the selected allocator", DiagnosticIds.AmbiguousCall);
            else if (selected is null)
                diagnostics.Report(location, allocator is null
                    ? $"'{current.Name}' requires a parameterless allocation context constructor for construction without an explicit allocator"
                    : $"'{current.Name}' requires a compatible allocator for its allocation constructor", DiagnosticIds.AllocationConstructorMismatch);
            else if (!AccessibilityRules.IsAccessible(selected, context))
                diagnostics.Report(location,
                    $"allocation context constructor '{SymbolDisplay.ToDisplayString(selected, SymbolDisplayFormat.Diagnostic)}' is inaccessible due to its protection level",
                    DiagnosticIds.InaccessibleSymbol);
        }
    }

    public static bool IsAllocatorValue(TypeSymbol type) => type switch
    {
        StructTypeSymbol { IsAllocator: true } => true,
        AtomicTypeSymbol atomic => IsAllocatorValue(atomic.ElementType),
        LifetimeModifierTypeSymbol lifetime => IsAllocatorValue(lifetime.ElementType),
        _ => false,
    };

    public static bool RequiresExplicitInitialization(TypeSymbol type) => RequiresExplicitInitialization(type, []);
    private static bool RequiresExplicitInitialization(TypeSymbol type, HashSet<TypeSymbol> visited)
    {
        if (!visited.Add(type)) return false;
        return type switch
        {
            AtomicTypeSymbol atomic => RequiresExplicitInitialization(atomic.ElementType, visited),
            StructTypeSymbol structure => structure.RequiresAllocator || structure.AllInstanceFields.Any(f => RequiresExplicitInitialization(f.Type, visited)),
            _ => false,
        };
    }

    public static bool IsAllocatorSubtype(StructTypeSymbol type, StructTypeSymbol ancestor)
    {
        for (StructTypeSymbol? current = type; current is not null; current = current.BaseType)
            if (TypeIdentity.AreSame(current, ancestor)) return true;
        return false;
    }

    public static bool IsValidAllocatorFieldType(TypeSymbol type) =>
        type is PointerTypeSymbol { IsReadonly: false, ElementType: StructTypeSymbol { IsAllocator: true } };

    // An outer context does not implicitly initialize ordinary embedded values.
    public static IEnumerable<StructTypeSymbol> InvalidDefaultFields(TypeSymbol type)
        => InvalidDefaultFields(type, new HashSet<TypeSymbol>(TypeIdentity.Comparer));

    private static IEnumerable<StructTypeSymbol> InvalidDefaultFields(TypeSymbol type, HashSet<TypeSymbol> path)
    {
        while (type is AtomicTypeSymbol atomic) type = atomic.ElementType;
        if (type is not StructTypeSymbol structure || !path.Add(type)) yield break;
        if (structure.BaseType is { } parent)
            foreach (var invalid in InvalidDefaultFields(parent, path)) yield return invalid;
        foreach (FieldSymbol field in structure.Fields.Where(f => !f.HasInitializer &&
                     !(f.IsAllocatorField && !structure.AllocationConstructors.IsEmpty)))
        {
            TypeSymbol fieldType = field.Type;
            while (fieldType is AtomicTypeSymbol atomic) fieldType = atomic.ElementType;
            if (fieldType is StructTypeSymbol { RequiresAllocator: true } required) yield return required;
            else foreach (var invalid in InvalidDefaultFields(fieldType, path)) yield return invalid;
        }
        path.Remove(type);
    }
}
