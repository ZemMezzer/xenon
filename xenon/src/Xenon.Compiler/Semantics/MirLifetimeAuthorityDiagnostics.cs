using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal static class MirLifetimeAuthorityDiagnostics
{
    public static void Analyze(IEnumerable<BoundFunction> functions, TypeFactory types, DiagnosticBag diagnostics,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Id is DiagnosticIds.TypeMismatch or
            DiagnosticIds.InvalidOperatorOperands or DiagnosticIds.CompareExchangeOperandTypeMismatch).ToArray();
        var invalid = locations.Where(pair => errors.Any(error =>
            ReferenceEquals(pair.Value.Source, error.Location.Source) &&
            error.Location.Span.Start >= pair.Value.Span.Start && error.Location.Span.End <= pair.Value.Span.End))
            .Select(pair => pair.Key).ToHashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        foreach (var function in functions.DistinctBy(function => function.Symbol))
        {
            cancellation.ThrowIfCancellationRequested();
            if (function.Symbol.Parameters.Any(parameter => TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) ||
                TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void))) continue;
            if (!BoundTree.DescendantsAndSelf(function.Body).Any(node => node is BoundMoveExpression or BoundStorageMoveExpression or
                BoundExplicitDestructExpression || node is BoundMethodCallExpression call && !call.Method.ReceiverMoveEffects.IsEmpty)) continue;
            var mir = MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true, invalidExpressions: invalid);
            var reported = new HashSet<(TextLocation, string)>();
            foreach (var violation in MirLifetimeAuthorityAnalysis.Check(mir, cancellation))
            {
                string id = violation.IsIndirectReceiver ? DiagnosticIds.HiddenVirtualMoveEffect : violation.IsPartialStorage ? DiagnosticIds.PartialStorageLifetimeOperation : violation.PartialDestructorOwner is not null
                    ? violation.IsDestruction ? DiagnosticIds.PartialDestructWithDestructor : DiagnosticIds.PartialMoveWithDestructor
                    : violation.Authority switch
                {
                    MirLifetimeAuthority.ReferenceParameter => DiagnosticIds.ReferenceParameterLifetimeMutation,
                    MirLifetimeAuthority.StorageValue => violation.IsReceiverEffect ? DiagnosticIds.PartialStorageLifetimeOperation : DiagnosticIds.StorageValueLifetimeMutation,
                    _ => DiagnosticIds.UnresolvedLifetimeOwner,
                };
                if (!reported.Add((violation.Source.Location, id))) continue;
                string message = violation.IsIndirectReceiver
                    ? "receiver move effect cannot be represented through this indirect receiver"
                    : violation.IsPartialStorage
                    ? "cannot manage a field lifetime of a value owned by 'storage<T>'; target the complete storage value instead"
                    : violation.PartialDestructorOwner is { } owner
                    ? $"cannot partially {(violation.IsDestruction ? "end the lifetime of" : "move")} '{MirDiagnosticNames.Name(mir, violation.Origins.First())}' because '{owner.Name}' has a user-defined destructor; {(violation.IsDestruction ? "manage the complete" : "move the entire")} '{owner.Name}' value instead"
                    : violation.Authority switch
                {
                    MirLifetimeAuthority.ReferenceParameter => "cannot manage a lifetime through an ordinary reference parameter; use 'storage<T>&' when the callable must manage storage lifetime",
                    MirLifetimeAuthority.StorageValue => violation.IsReceiverEffect
                        ? "method cannot leave a value owned by 'storage<T>' partially moved or destructed"
                        : "cannot manage the lifetime of a value through a reference borrowed from 'storage<T>'; use 'storage<T>&' to manage the storage lifetime",
                    MirLifetimeAuthority.ReferenceField => "cannot manage a lifetime through a reference field; reference fields borrow external values and do not own their lifetimes",
                    _ => "cannot manage this lifetime because its authoritative owner cannot be resolved to one semantic place",
                };
                diagnostics.Report(violation.Source.Location, message, id);
            }
        }
    }
}