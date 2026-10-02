using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal static class MirBorrowDiagnostics
{
    public static void Analyze(IEnumerable<BoundFunction> functions, TypeFactory types, DiagnosticBag diagnostics,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation)
    {
        var typeErrors = diagnostics.Where(diagnostic => diagnostic.Id is DiagnosticIds.TypeMismatch or
            DiagnosticIds.InvalidOperatorOperands or DiagnosticIds.CompareExchangeOperandTypeMismatch).ToArray();
        var invalid = locations.Where(pair => typeErrors.Any(error =>
            ReferenceEquals(pair.Value.Source, error.Location.Source) &&
            error.Location.Span.Start >= pair.Value.Span.Start && error.Location.Span.End <= pair.Value.Span.End))
            .Select(pair => pair.Key).ToHashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        foreach (var function in functions.DistinctBy(function => function.Symbol))
        {
            cancellation.ThrowIfCancellationRequested();
            if (!BoundTree.DescendantsAndSelf(function.Body).OfType<BoundExpression>()
                .Any(expression => MirReferenceOrigins.ReferenceLeaves(expression.Type).Any())) continue;
            var mir = MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true, invalidExpressions: invalid);
            var locals = mir.Locals.ToDictionary(local => local.Id);
            foreach (var violation in new MirBorrowAnalysis(mir, cancellation).Check())
            {
                string alias = locals[violation.Alias].Variable?.Name ?? "temporary argument";
                string name = MirDiagnosticNames.Name(mir, violation.Place);
                string id = violation.Check switch
                {
                    MirBorrowCheck.Read => DiagnosticIds.BorrowedPlaceAccess,
                    MirBorrowCheck.Mutation => DiagnosticIds.BorrowedPlaceMutation,
                    MirBorrowCheck.Move => DiagnosticIds.MoveWhileBorrowed,
                    MirBorrowCheck.Destruct => DiagnosticIds.DestructWhileBorrowed,
                    MirBorrowCheck.Free => DiagnosticIds.FreeWhileBorrowed,
                    MirBorrowCheck.DestructionOrder => DiagnosticIds.ReferenceDestructionOrder,
                    MirBorrowCheck.AggregateEscape => DiagnosticIds.AggregateReferenceEscape,
                    _ => DiagnosticIds.BorrowConflict,
                };
                string message = violation.Check switch
                {
                    MirBorrowCheck.Read => $"cannot access '{name}' while it is exclusively borrowed through '{alias}'",
                    MirBorrowCheck.Mutation => $"cannot mutate '{name}' while it is borrowed through '{alias}'",
                    MirBorrowCheck.Move => $"cannot move '{name}' while it is borrowed through '{alias}'",
                    MirBorrowCheck.Destruct => $"cannot destruct '{name}' while it is borrowed through '{alias}'",
                    MirBorrowCheck.Free => $"cannot free '{name}' while its pointee is borrowed through '{alias}'",
                    MirBorrowCheck.DestructionOrder => $"value '{alias}' may be destroyed after referenced local '{name}'",
                    MirBorrowCheck.AggregateEscape => "cannot store a value containing a borrowed reference in storage whose lifetime is not bounded by the referenced value",
                    _ => $"cannot create a reference to '{name}' while an overlapping borrow through '{alias}' is active",
                };
                MirDiagnosticReporter.Report(diagnostics, mir, violation.Source, message, id,
                    $"{violation.Check}:{violation.Place.Kind}:{violation.Place.Ordinal}:{violation.Place.Path}:{alias}");
            }

        }
    }
}