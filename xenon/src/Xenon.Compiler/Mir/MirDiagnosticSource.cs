using System.Runtime.CompilerServices;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Mir;

/// <summary>One source-location policy for all MIR diagnostics, including source-less specializations.</summary>
public static class MirDiagnosticSource
{
    private static readonly ConditionalWeakTable<FunctionSymbol, SourceText> LibrarySources = new();
    internal static bool IsSource(TextLocation location) =>
        location.Source is not null && location.Path != "<metadata>" && !location.Path.StartsWith("<specialization ", StringComparison.Ordinal);

    internal static FunctionSymbol Definition(FunctionSymbol function)
    {
        while ((function.GenericDefinition ?? function.OriginalDefinition) is { } definition && !ReferenceEquals(definition, function))
            function = definition;
        return function;
    }

    public static TextLocation Resolve(FunctionSymbol function, TextLocation original, TextLocation callSite = default)
    {
        if (IsSource(original)) return original;
        var definition = Definition(function);
        var source = definition.Locations.FirstOrDefault(IsSource);
        if (IsSource(source)) return source;
        if (IsSource(callSite)) return callSite;
        source = function.SpecializationLocations.FirstOrDefault(IsSource);
        if (IsSource(source)) return source;
        if (IsSource(function.SpecializationOrigin)) return function.SpecializationOrigin;
        source = function.Locations.FirstOrDefault(IsSource);
        if (IsSource(source)) return source;
        for (Symbol? owner = function.ContainingSymbol; owner is not null; owner = owner.ContainingSymbol)
        {
            source = owner.Locations.FirstOrDefault(IsSource);
            if (IsSource(source)) return source;
        }
        if (definition.Origin.Kind == SymbolOriginKind.Library)
            return new(LibrarySources.GetValue(function, symbol => SourceText.From("", $"<specialization {Context(symbol)}>")), new(0, 0));
        return TextLocation.None;
    }

    internal static string Context(FunctionSymbol function)
    {
        var definition = Definition(function);
        return definition.Origin.Kind == SymbolOriginKind.Library
            ? $"{function.FullName} imported from {definition.Origin.LibraryDisplayName ?? definition.Origin.LibrarySymbolKey ?? "library"}"
            : function.FullName;
    }
}