using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal static class MirArrayDiagnostics
{
    public static void Analyze(IEnumerable<BoundFunction> functions, TypeFactory types, DiagnosticBag diagnostics,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Id is DiagnosticIds.TypeMismatch or DiagnosticIds.InvalidOperatorOperands or DiagnosticIds.CompareExchangeOperandTypeMismatch or DiagnosticIds.ArrayLengthMustBeInteger or DiagnosticIds.ArrayLengthOutOfRange).ToArray();
        var invalid = locations.Where(pair => errors.Any(error =>
            ReferenceEquals(pair.Value.Source, error.Location.Source) &&
            error.Location.Span.Start >= pair.Value.Span.Start && error.Location.Span.End <= pair.Value.Span.End))
            .Select(pair => pair.Key).ToHashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        foreach (var function in functions.DistinctBy(function => function.Symbol))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!BoundTree.DescendantsAndSelf(function.Body).Any(node => node is BoundArrayCreationExpression { Storage: ArrayStorageKind.Stack })) continue;
            var mir = MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true, invalidExpressions: invalid);
            var analysis = new MirLifetimeAnalysis(mir, cancellation: cancellation);
            _ = analysis.Analyze();
            foreach (var diagnostic in analysis.Diagnostics.Where(diagnostic => diagnostic.Id is
                DiagnosticIds.StackArrayEscape or DiagnosticIds.StackArrayReturn or DiagnosticIds.StackArrayFree or
                DiagnosticIds.StackArrayStoredInAggregate or DiagnosticIds.StackArrayPassedAsArgument))
                    MirDiagnosticReporter.Report(diagnostics, mir, diagnostic.Source, diagnostic.Message, diagnostic.Id);
        }
    }
}