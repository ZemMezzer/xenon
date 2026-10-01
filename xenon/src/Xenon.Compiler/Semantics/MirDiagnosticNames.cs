using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

internal static class MirDiagnosticNames
{
    public static string Name(MirFunction mir, MirReferenceOrigin origin)
    {
        MirLocal? local = origin.Kind == MirReferenceOriginKind.Parameter
            ? mir.Locals.FirstOrDefault(local => local.Variable is ParameterSymbol parameter && parameter.Ordinal == origin.Ordinal)
            : mir.Locals.FirstOrDefault(local => local.Id.Value == origin.Ordinal);
        string name = origin.Kind == MirReferenceOriginKind.Receiver ? "this" : local?.Variable?.Name ?? local?.Name ?? "referenced storage";
        TypeSymbol? type = origin.Kind == MirReferenceOriginKind.Receiver ? mir.Symbol.ContainingType : local?.Type;
        if (origin.Kind is MirReferenceOriginKind.RawPointee or MirReferenceOriginKind.UniquePointee or MirReferenceOriginKind.SharedPointee)
        { name = "*" + name; type = origin.PointeeType; }
        foreach (string part in origin.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            while (type is LifetimeModifierTypeSymbol modifier) type = modifier.ElementType;
            if (type is ReferenceTypeSymbol reference) type = reference.ElementType;
            if (type is not IFieldStorageTypeSymbol structure || !int.TryParse(part, out int ordinal) ||
                structure.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal) is not { } field) break;
            name += "." + field.Name; type = field.Type;
        }
        return name;
    }
}
