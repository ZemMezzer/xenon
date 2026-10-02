using System.Runtime.CompilerServices;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>Deduplicates semantic MIR violations, never diagnostic message text.</summary>
internal static class MirDiagnosticReporter
{
    private sealed record Key(FunctionSymbol Definition, TextLocation Source, string Id, string Violation);
    private sealed record Entry(Key Key, FunctionSymbol Function, TextLocation CallSite, Diagnostic Diagnostic);
    private static readonly ConditionalWeakTable<DiagnosticBag, List<Entry>> Reports = new();

    internal static void RegisterCallSites(IEnumerable<BoundFunction> functions,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations)
    {
        foreach (var body in functions)
            foreach (var expression in BoundTree.DescendantsAndSelf(body.Body).OfType<BoundExpression>())
            {
                FunctionSymbol? target = expression switch
                {
                    BoundCallExpression call => call.Function,
                    BoundMethodCallExpression call => call.Method,
                    BoundConstructorCallExpression call => call.Constructor,
                    _ => null,
                };
                if (target?.ContainingType is not null && !ReferenceEquals(MirDiagnosticSource.Definition(target), target) &&
                    locations.TryGetValue(expression, out var location))
                    target.AddSpecializationLocation(location);
            }
    }

    internal static void Report(DiagnosticBag diagnostics, MirFunction function, MirSourceInfo source,
        string message, string id, string violation = "") =>
        Report(diagnostics, function.Symbol, source.IsFallback ? TextLocation.None : source.Location,
            message, id, source.IsFallback ? $"{violation}:origin={source.DiagnosticOriginId ?? source.OriginId}" : violation);

    internal static void Report(DiagnosticBag diagnostics, FunctionSymbol function, TextLocation source,
        string message, string id, string violation = "")
    {
        var definition = MirDiagnosticSource.Definition(function);
        var location = MirDiagnosticSource.Resolve(function, source);
        var key = new Key(definition, location, id, violation);
        var entries = Reports.GetOrCreateValue(diagnostics);
        bool portable = ReferenceEquals(function, definition);
        if (!portable && entries.Any(entry => entry.Key == key && ReferenceEquals(entry.Function, definition))) return;
        if (portable)
            foreach (var entry in entries.Where(entry => entry.Key == key && !ReferenceEquals(entry.Function, definition)).ToArray())
            {
                diagnostics.Remove(entry.Diagnostic);
                entries.Remove(entry);
            }
        TextLocation[] sites = !portable && !function.SpecializationLocations.IsEmpty
            ? [.. function.SpecializationLocations] : [default];
        foreach (var site in sites)
        {
            if (entries.Any(entry => entry.Key == key && ReferenceEquals(entry.Function, function) && entry.CallSite == site)) continue;
            var primary = MirDiagnosticSource.IsSource(source) || definition.Locations.Any(MirDiagnosticSource.IsSource)
                ? location : MirDiagnosticSource.Resolve(function, source, site);
            var related = MirDiagnosticSource.IsSource(site)
                ? new[] { new RelatedDiagnosticLocation(site, $"while specializing {MirDiagnosticSource.Context(function)}") } : [];
            var diagnostic = diagnostics.ReportDiagnostic(primary, message, id, related);
            entries.Add(new(key, function, site, diagnostic));
        }
    }
}