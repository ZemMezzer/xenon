using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal static class MirPointerLifetimeDiagnostics
{
    public static void Analyze(IEnumerable<BoundFunction> functions, TypeFactory types, DiagnosticBag diagnostics,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation)
    {
        var typeErrors = diagnostics.Where(diagnostic => diagnostic.Id is DiagnosticIds.TypeMismatch or
            DiagnosticIds.InvalidOperatorOperands or DiagnosticIds.CompareExchangeOperandTypeMismatch).ToArray();
        var invalid = locations.Where(pair => typeErrors.Any(error => ReferenceEquals(pair.Value.Source, error.Location.Source) &&
            error.Location.Span.Start >= pair.Value.Span.Start && error.Location.Span.End <= pair.Value.Span.End))
            .Select(pair => pair.Key).ToHashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        var bodies = functions.DistinctBy(function => function.Symbol)
            .Where(function => BoundTree.DescendantsAndSelf(function.Body).OfType<BoundExpression>().Any(expression =>
                MirPointerLifetimeAnalysis.CarriesPointer(expression.Type)))
            .Select(function => MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true, invalidExpressions: invalid)).ToArray();

        // Compilation-local summaries use available MIR bodies only. No external
        // freshness or allocator contract is invented, and no whole-program input
        // is required. The finite provenance paths bound recursive summaries.
        var summaries = MirPointerLifetimeAnalysis.BuildReturnSummaries(bodies, cancellation);

        var lifetimeSummaries = MirPointerLifetimeAnalysis.BuildSummaries(bodies, summaries, cancellation);
        var destruction = lifetimeSummaries.ToDictionary(pair => pair.Key, pair => pair.Value.Effects);
        var exits = lifetimeSummaries.Where(pair => pair.Value.Complete).ToDictionary(pair => pair.Key, pair => pair.Value.Exits);
        foreach (var (symbol, summary) in lifetimeSummaries) symbol.MirLifetimeSummary = summary;
        foreach (var body in bodies) MirBorrowDiagnostics.AnalyzeLifetimeEffects(body, lifetimeSummaries, summaries, diagnostics, cancellation);
        if (diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)) return;
        foreach (var body in bodies)
            foreach (var violation in new MirPointerLifetimeAnalysis(body, summaries, cancellation, destruction, lifetimeExits: exits).Check(cancellation))
            {
                string name = violation.Name.StartsWith('$') || violation.Name.StartsWith("tmp", StringComparison.Ordinal) ? "pointer" : violation.Name;
                string ended = violation.Origin.StorageFreed ? "storage has been freed" : "object has been destroyed";
                string operation = violation.Check switch
                {
                    MirPointerLifetimeCheck.Escape when name == "externally stored pointer" => "cannot retain a pointer in external storage after the referenced lifetime ends",
                    MirPointerLifetimeCheck.Escape => $"cannot return '{name}': pointer refers to storage destroyed before the function returns",
                    MirPointerLifetimeCheck.DoubleFree => $"cannot destroy '{name}': the referenced {ended}",
                    _ => $"cannot use '{name}': the operation requires a live object, but the referenced {ended}",
                };
                string origin = violation.Origin.Kind == MirReferenceOriginKind.Local ? "local storage" : "allocation";
                var creation = body.Locals.FirstOrDefault(local => local.Id.Value == violation.Origin.Ordinal)?.Source;
                var related = new List<RelatedDiagnosticLocation>();
                if (creation is { IsFallback: false }) related.Add(new(creation.Location, $"{origin} originates here"));
                if (violation.Origin.DestroyedAt is { IsFallback: false } death) related.Add(new(death.Location, "referenced lifetime ends here (explicit destruction or scope cleanup)"));
                if (violation.Origin.CallDeath is { Source.IsFallback: false } effect)
                    related.Add(new(effect.Source.Location, "called function destroys the referenced lifetime here"));
                if (violation.Conditional) operation += " on a control-flow path";
                string id = violation.Check switch
                {
                    MirPointerLifetimeCheck.Escape => DiagnosticIds.DanglingPointerEscape,
                    MirPointerLifetimeCheck.DoubleFree => DiagnosticIds.PointerDoubleFree,
                    _ => DiagnosticIds.PointerUseAfterFree,
                };
                MirDiagnosticReporter.Report(diagnostics, body, violation.Source, operation, id, related: related);
            }
    }
}

