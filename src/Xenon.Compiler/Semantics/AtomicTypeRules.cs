using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

internal readonly record struct AtomicTypeValidationFailure(string Id, string Message);

internal static class AtomicTypeRules
{
    public static bool SupportsNativeOperations(TypeSymbol element) =>
        element is PrimitiveTypeSymbol or EnumTypeSymbol or PointerTypeSymbol or ArrayTypeSymbol;

    public static bool SupportsOperations(TypeSymbol element) =>
        SupportsNativeOperations(element) || SupportsLockBackedComposite(element, []);

    private static bool SupportsLockBackedComposite(TypeSymbol element, HashSet<TypeSymbol> visited)
    {
        if (SupportsNativeOperations(element)) return true;
        if (element is SharedTypeSymbol or WeakTypeSymbol) return true;
        if (element is not StructTypeSymbol structure || !visited.Add(element)) return false;
        try
        {
            if (structure.BaseType is { } baseType && !SupportsLockBackedComposite(baseType, visited))
                return false;
            return structure.Fields.All(field => SupportsLockBackedComposite(field.Type, visited));
        }
        finally
        {
            visited.Remove(element);
        }
    }

    public static AtomicTypeValidationFailure? ValidateElement(TypeSymbol element)
    {
        if (element is UniqueTypeSymbol)
        {
            return new AtomicTypeValidationFailure(
                DiagnosticIds.AtomicUniqueTypeNotSupported,
                $"atomic storage cannot contain unique ownership type '{element.ToDisplayString()}'; atomic loads cannot duplicate exclusive ownership");
        }
        if (element is ReferenceTypeSymbol)
        {
            return new AtomicTypeValidationFailure(
                DiagnosticIds.AtomicReferenceTypeNotSupported,
                $"atomic storage cannot contain reference type '{element.ToDisplayString()}'; use a reference to atomic<T> instead");
        }
        if (TypeIdentity.AreSame(element, BuiltinTypes.Void) || element is AtomicTypeSymbol ||
            element is not GenericParameterSymbol && element.Copyability == Copyability.NonCopyable)
        {
            return new AtomicTypeValidationFailure(
                DiagnosticIds.InvalidAtomicTypeArgument,
                $"type '{element.ToDisplayString()}' is not a valid atomic value type");
        }
        return null;
    }
}
