using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public enum MirOwnershipCheck { Read, UntrackedReplacement, PinnedInitialization, RequiredField }
public sealed record MirOwnershipViolation(MirLocation Location, MirPlace Place,
    MirOwnershipStatus Status, MirSourceInfo Source, MirPlace Cause, MirOwnershipCheck Check = MirOwnershipCheck.Read);

/// <summary>Checks source reads against MIR ownership states without diagnosing cleanup bookkeeping.</summary>
public static class MirOwnershipChecks
{
    public static ImmutableArray<MirOwnershipViolation> Check(MirFunction function, CancellationToken cancellation = default)
    {
        var analysis = new MirMoveAnalysis(function, cancellation);
        var graph = MirFlowFacts.Graph(function, cancellation);
        var states = MirDataflow.Solve(graph, analysis, cancellation);
        var locals = function.Locals.ToDictionary(local => local.Id);
        var violations = ImmutableArray.CreateBuilder<MirOwnershipViolation>();
        foreach (MirBasicBlock block in function.Blocks.Where(block => states.Graph.Reachable.Contains(block.Id)))
        {
            for (int index = 0; index < block.Statements.Length; index++)
            {
                if (block.Statements[index] is not MirAssign read || !(read.IsSemanticRead || read.IsUntrackedReplacementCheck || read.IsPinnedInitializationCheck)) continue;
                var location = new MirLocation(block.Id, index);
                if (read.IsCompleteReceiverRead)
                    RequiredFields(location, read.Source, states.Before[location]);
                IEnumerable<MirOperand> operands = read.Value is MirBorrow borrow
                    ? [new MirCopy(borrow.Place, borrow.Type)] : MirOperands.Of(read.Value);
                foreach (MirOperand operand in operands)
                {
                    MirPlace? place = operand switch
                    {
                        MirCopy copy => copy.Place,
                        MirMove move => move.OwnershipPlace ?? move.Place,
                        _ => null,
                    };
                    if (place is null) continue;
                    foreach (MirPlace resolved in analysis.Resolve(place, read))
                    {
                        // Compiler temporaries have their own structural verifier.
                        // Unknown external pointees are governed by borrow contracts.
                        if (locals[resolved.Local].Kind == MirLocalKind.Temporary) continue;
                        MirOwnershipStatus status = analysis.Status(resolved, states.Before[location]);
                        if (read.IsPinnedInitializationCheck)
                        {
                            if (status != MirOwnershipStatus.Uninitialized)
                                violations.Add(new(location, resolved, status, read.Source, resolved, MirOwnershipCheck.PinnedInitialization));
                            continue;
                        }
                        // Default-zeroed ordinary fields may be read before their
                        // first explicit constructor assignment. References and
                        // pinned storage still require final-address initialization.
                        if (status is MirOwnershipStatus.Uninitialized or MirOwnershipStatus.MaybeInitialized &&
                            function.Symbol.FunctionKind is FunctionKind.Constructor or FunctionKind.InstanceInitializer &&
                            locals[resolved.Local].Kind == MirLocalKind.Receiver &&
                            resolved.Projections.OfType<MirFieldProjection>().FirstOrDefault() is { } initialField &&
                            !TypeFacts.ContainsReferenceStorage(initialField.Field.Type) &&
                            initialField.Field.Type is not PinTypeSymbol) continue;
                        if (read.IsUntrackedReplacementCheck)
                        {
                            if (status == MirOwnershipStatus.MaybeMoved)
                                violations.Add(new(location, resolved, status, read.Source, resolved, MirOwnershipCheck.UntrackedReplacement));
                            continue;
                        }
                        if (status == MirOwnershipStatus.Initialized ||
                            read.IsProjectionBaseRead && status == MirOwnershipStatus.PartiallyMoved) continue;
                        var state = states.Before[location];
                        MirPlace cause = status == MirOwnershipStatus.PartiallyMoved
                            ? state.Keys.Where(place => resolved.IsPrefixOf(place) && !place.Equals(resolved) &&
                                analysis.Status(place, state) is MirOwnershipStatus.Moved or MirOwnershipStatus.MaybeMoved)
                                .OrderBy(place => place.Projections.Length).FirstOrDefault() ?? resolved
                            : state.Keys.Where(place => place.IsPrefixOf(resolved) &&
                                analysis.Status(place, state) == status)
                                .OrderBy(place => place.Projections.Length).FirstOrDefault() ?? resolved;
                        violations.Add(new(location, resolved, status, read.Source, cause));
                    }
                }
            }
            if (block.Terminator is MirReturn && function.Symbol.FunctionKind == FunctionKind.Constructor)
            {
                var location = new MirLocation(block.Id, block.Statements.Length);
                RequiredFields(location, block.Terminator.Source, states.Before[location]);
            }
        }
        return violations.ToImmutable();

        void RequiredFields(MirLocation location, MirSourceInfo source, ImmutableDictionary<MirPlace, MirInitialization> state)
        {
            if (function.Symbol.FunctionKind is not (FunctionKind.Constructor or FunctionKind.InstanceInitializer) ||
                function.Symbol.ContainingStruct is not { } owner) return;
            foreach (MirLocal receiver in function.Locals.Where(local => local.Kind == MirLocalKind.Receiver))
                foreach (FieldSymbol field in owner.Fields.Where(field =>
                    TypeFacts.ContainsReferenceStorage(field.Type) || field.Type is PinTypeSymbol))
                {
                    MirPlace place = new MirPlace(receiver.Id).Project(new MirDerefProjection()).Project(new MirFieldProjection(field));
                    MirOwnershipStatus status = analysis.Status(place, state);
                    if (status is MirOwnershipStatus.Uninitialized or MirOwnershipStatus.MaybeInitialized)
                        violations.Add(new(location, place, status, source, place, MirOwnershipCheck.RequiredField));
                }
        }
    }
}