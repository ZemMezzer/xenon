using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

public static class AsyncEntryPoint
{
    internal static ImmutableArray<Diagnostic> Validate(Compilation compilation)
    {
        if (compilation.Options.OutputKind != CompilationOutputKind.Executable) return [];
        var diagnostics = new DiagnosticBag();
        foreach (var entry in compilation.SemanticModel.Functions.Select(function => function.Symbol).Where(function =>
                     function.IsAsync && function.Name == "Main" && function.FunctionKind == FunctionKind.Ordinary && function.ContainingType is null))
            if (GetError(entry) is { } message)
                diagnostics.Report(entry.Locations.IsEmpty ? new TextLocation(compilation.SyntaxTrees[0].Source, new TextSpan(0, 0)) : entry.Locations[0],
                    message, DiagnosticIds.InvalidResumableReturn);
        return diagnostics.ToImmutableArray();
    }

    public static string? GetError(FunctionSymbol entry)
    {
        if (!entry.Parameters.IsEmpty || entry.IsGenericDefinition)
            return "async Main must be non-generic and parameterless";
        FunctionSymbol? awaiting = GetAwaitOperator(entry);
        if (awaiting is null) return "async Main return type requires an unambiguous accessible operator await";
        if (awaiting.Parameters.Length == 3 && !TypeIdentity.AreSame(
                ((StorageTypeSymbol)((ReferenceTypeSymbol)awaiting.Parameters[1].Type).ElementType).ElementType, BuiltinTypes.Int))
            return "async Main await result must be int or void";
        if (awaiting.Parameters[0].Type is not ReferenceTypeSymbol && !TypeFacts.CanCopy(entry.ReturnType))
            return "async Main requires a copyable by-value await operand or a reference operator await";
        return null;
    }

    public static FunctionSymbol? GetAwaitOperator(FunctionSymbol entry)
    {
        if (!entry.IsAsync || entry.ReturnType is not StructTypeSymbol type) return null;
        FunctionSymbol[] candidates = type.Methods.Where(method => method.OperatorKind == OperatorKind.Await &&
            method.Accessibility == Accessibility.Public && OperatorFacts.ProtocolSignatureError(method) is null &&
            TypeIdentity.AreSame(OperatorFacts.ValueType(method.Parameters[0].Type), type)).ToArray();
        // Match ordinary await conversion costs for a mutable lvalue.
        int Cost(FunctionSymbol method) => TypeFacts.GetImplicitConversionCost(method.Parameters[0].Type, type) ??
            (method.Parameters[0].Type is ReferenceTypeSymbol reference ? TypeFacts.GetReferenceBindingCost(reference, type) : null) ?? int.MaxValue;
        if (candidates.Length == 0) return null;
        int best = candidates.Min(Cost);
        candidates = candidates.Where(method => Cost(method) == best).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }
}
