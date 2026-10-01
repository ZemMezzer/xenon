using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>The source-facing boundary of MIR ownership diagnostics.</summary>
internal static class MirSemanticDiagnostics
{
    public static void Analyze(IEnumerable<BoundFunction> functions, TypeFactory types,
        DiagnosticBag diagnostics, IReadOnlyDictionary<BoundExpression, TextLocation> locations,
        CancellationToken cancellation)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        var invalid = locations.Where(pair => errors.Any(error =>
            ReferenceEquals(pair.Value.Source, error.Location.Source) &&
            error.Location.Span.Start >= pair.Value.Span.Start && error.Location.Span.End <= pair.Value.Span.End))
            .Select(pair => pair.Key).ToHashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        var bodies = functions.DistinctBy(function => function.Symbol)
            .Where(function => !TypeIdentity.AreSame(function.Symbol.ReturnType, BuiltinTypes.Error) &&
                !function.Symbol.Parameters.Any(parameter => TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void)))
            .Select(function => MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true, invalidExpressions: invalid)).ToArray();
        // Calls conservatively accept ownership before unwinding. Cleanup edges can
        // use destructor effects so a nonthrowing replacement does not poison a catch.
        var effects = new MirExceptionEffects(bodies, cancellation).Infer()
            .Where(pair => pair.Key.FunctionKind == FunctionKind.Destructor).ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (MirFunction mir in bodies)
        {
            cancellation.ThrowIfCancellationRequested();
            var blocks = mir.Blocks.ToDictionary(block => block.Id);
            var locals = mir.Locals.ToDictionary(local => local.Id);
            var graph = MirFlowFacts.Graph(mir, cancellation, effects);
            var reported = new HashSet<(TextLocation, string, string)>();
            foreach (MirOwnershipViolation violation in MirOwnershipChecks.Check(mir, cancellation, effects))
            {
                MirBasicBlock block = blocks[violation.Location.Block];
                MirAssign? read = violation.Location.Statement < block.Statements.Length
                    ? block.Statements[violation.Location.Statement] as MirAssign : null;
                bool loop = read?.IsMoveRead == true && InCycle(violation.Location.Block, graph);
                FieldSymbol? field = violation.Place.Projections.OfType<MirFieldProjection>().LastOrDefault()?.Field;
                bool ambiguousPin = violation.Check == MirOwnershipCheck.PinnedInitialization &&
                    violation.Status == MirOwnershipStatus.MaybeInitialized && mir.Symbol.FunctionKind == FunctionKind.Constructor &&
                    locals[violation.Place.Local].Kind == MirLocalKind.Receiver;
                string id = violation.Check == MirOwnershipCheck.ArgumentReservation ? DiagnosticIds.ArgumentLifetimeConflict :
                    violation.Check == MirOwnershipCheck.SelfMove ? DiagnosticIds.SelfMove :
                    violation.Check == MirOwnershipCheck.RequiredField
                    ? field!.Type is PinTypeSymbol ? DiagnosticIds.PinnedRelocation : DiagnosticIds.ReferenceFieldNotInitialized :
                    violation.Check == MirOwnershipCheck.PinnedInitialization
                    ? ambiguousPin ? DiagnosticIds.AmbiguousConstructorFieldInitialization : DiagnosticIds.PinnedRelocation :
                    violation.Check == MirOwnershipCheck.UntrackedReplacement ? DiagnosticIds.ConditionalMoveReinitializationNotTracked :
                    loop ? DiagnosticIds.MoveAcrossLoopBackedge : violation.Status switch
                {
                    MirOwnershipStatus.PartiallyMoved => DiagnosticIds.PartiallyMovedUse,
                    MirOwnershipStatus.Moved or MirOwnershipStatus.MaybeMoved => DiagnosticIds.UseAfterMove,
                    _ => DiagnosticIds.DefiniteAssignment,
                };
                string Name(MirPlace place) => locals[place.Local].Name +
                    string.Concat(place.Projections.OfType<MirFieldProjection>().Select(field => "." + field.Field.Name));
                string name = Name(violation.Place), cause = Name(violation.Cause);
                string message = violation.Check == MirOwnershipCheck.ArgumentReservation
                    ? $"cannot reinitialize '{name}' after moving an overlapping place in an earlier argument; split the operations into separate statements"
                    : violation.Check == MirOwnershipCheck.SelfMove ? $"cannot move '{name}' into itself" :
                    violation.Check == MirOwnershipCheck.RequiredField
                    ? field!.Type is PinTypeSymbol
                        ? $"pinned field '{field.Name}' must be constructed at its final address before the object is used or its constructor exits"
                        : $"field '{field.Name}' contains a reference and must be initialized before the object is used or its constructor exits"
                    : violation.Check == MirOwnershipCheck.PinnedInitialization
                    ? ambiguousPin
                        ? $"cannot determine whether constructor assignment to field '{name}' initializes or replaces its value: the field may already be initialized on another control-flow path or loop backedge"
                        : $"cannot assign to '{((PointerTypeSymbol)((MirBorrow)read!.Value).Type).ElementType.ToDisplayString()}' after its pinned lifetime has begun"
                    : violation.Check == MirOwnershipCheck.UntrackedReplacement
                    ? $"cannot reassign possibly moved place '{name}' because this indirect or receiver field has no runtime lifetime flag; reinitialize it separately on each control-flow path"
                    : loop
                    ? $"cannot move '{name}' across a loop back-edge because it may already be moved on the next iteration"
                    : id == DiagnosticIds.DefiniteAssignment
                        ? locals[violation.Place.Local].Kind == MirLocalKind.Receiver && field is not null
                            ? $"field '{field.Name}' is used before it is initialized"
                            : $"local variable '{cause}' is used before it is initialized"
                        : id == DiagnosticIds.PartiallyMovedUse
                            ? $"cannot use '{name}' as a complete value because field '{cause}' has been moved"
                            : violation.Cause.Projections.IsEmpty
                                ? $"cannot use '{cause}' because it has been moved"
                                : $"cannot use '{name}' because '{cause}' has been moved";
                if (reported.Add((violation.Source.Location, id, message)))
                    diagnostics.Report(violation.Source.Location, message, id);
            }
        }
    }

    public static void InferReceiverMoves(IEnumerable<BoundFunction> functions, TypeFactory types,
        DiagnosticBag diagnostics, IReadOnlyDictionary<BoundExpression, TextLocation> locations,
        CancellationToken cancellation)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToArray();
        var invalid = locations.Where(pair => errors.Any(error =>
            ReferenceEquals(pair.Value.Source, error.Location.Source) &&
            error.Location.Span.Start >= pair.Value.Span.Start && error.Location.Span.End <= pair.Value.Span.End))
            .Select(pair => pair.Key).ToHashSet<BoundExpression>(ReferenceEqualityComparer.Instance);
        foreach (BoundFunction function in functions.DistinctBy(function => function.Symbol))
        {
            if (!function.Symbol.HasImplicitThis || TypeIdentity.AreSame(function.Symbol.ReturnType, BuiltinTypes.Error) ||
                function.Symbol.Parameters.Any(parameter => TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) ||
                    TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void))) continue;
            MirFunction mir = MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true, invalidExpressions: invalid);
            MirReceiverMoveSummary summary = MirReceiverMoveEffects.Analyze(mir, cancellation);
            function.Symbol.SetReceiverMoveEffects([.. summary.Moved
                .OrderBy(place => string.Join(',', place.Projections.OfType<MirFieldProjection>().Select(field => field.Field.Ordinal)), StringComparer.Ordinal)
                .Select(place => new ReceiverMoveEffect([.. place.Projections.OfType<MirFieldProjection>().Select(field => field.Field.Ordinal)]))]);
            foreach (MirPlace place in summary.Inconsistent)
            {
                string name = "this" + string.Concat(place.Projections.OfType<MirFieldProjection>().Select(field => "." + field.Field.Name));
                string message = $"method '{function.Symbol.Name}' does not leave receiver field '{name}' in a consistent move state across all reachable exits; some exits move '{name}' while others leave it live";
                TextLocation location = function.Symbol.Locations.FirstOrDefault(TextLocation.None);
                if (!diagnostics.Any(diagnostic => diagnostic.Id == DiagnosticIds.InconsistentReceiverMoveEffect &&
                    diagnostic.Location == location && diagnostic.Message == message))
                    diagnostics.Report(location, message, DiagnosticIds.InconsistentReceiverMoveEffect);
            }
        }
    }
    private static bool InCycle(MirBlockId start, MirControlFlow graph)
    {
        var seen = new HashSet<MirBlockId>();
        var pending = new Stack<MirBlockId>(graph.Successors[start].Select(edge => edge.Target));
        while (pending.TryPop(out MirBlockId block))
        {
            if (block == start) return true;
            if (!seen.Add(block)) continue;
            foreach (MirEdge edge in graph.Successors[block]) pending.Push(edge.Target);
        }
        return false;
    }
}