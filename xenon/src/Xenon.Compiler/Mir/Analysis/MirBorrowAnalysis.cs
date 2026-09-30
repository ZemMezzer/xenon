using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public enum MirBorrowCheck { Read, Mutation, Borrow, Move }
public sealed record MirBorrowViolation(MirLocation Location, MirSourceInfo Source, MirBorrowCheck Check,
    MirReferenceOrigin Place, MirLocalId Alias);

/// <summary>Loan conflicts at MIR accesses, using reaching origins and backward storage liveness.</summary>
public sealed class MirBorrowAnalysis(MirFunction function, CancellationToken cancellation = default)
{
    private sealed record Loan(MirLocalId Alias, MirReferenceOrigin Place, bool IsReadonly);
    private readonly Dictionary<MirLocalId, MirLocal> _locals = function.Locals.ToDictionary(local => local.Id);
    public ImmutableArray<MirBorrowViolation> Check()
    {
        var graph = MirFlowFacts.Graph(function, cancellation);
        var origins = new MirReferenceOrigins(function);
        var flow = MirDataflow.Solve(graph, origins, cancellation);
        var live = MirDataflow.Solve(graph, new MirLiveness(), cancellation);
        var violations = new HashSet<MirBorrowViolation>();
        foreach (MirBasicBlock block in function.Blocks.Where(block => graph.Reachable.Contains(block.Id)))
        {
            cancellation.ThrowIfCancellationRequested();
            for (int index = 0; index < block.Statements.Length; index++)
            {
                var location = new MirLocation(block.Id, index);
                if (block.Statements[index] is not MirAssign assign) continue;
                var state = flow.Before[location];
                var loans = Active(state, live.Before[location]).ToArray();
                if (assign.Value is MirBorrow { Kind: not MirBorrowKind.Raw } borrow)
                    Access(origins.Address(borrow.Place, state), origins.ThroughAliases(borrow.Place, state),
                        borrow.Kind == MirBorrowKind.Shared, MirBorrowCheck.Borrow, assign.Source, location, loans);
                else if (assign.IsSemanticRead)
                {
                    MirPlace? read = assign.Value is MirBorrow address ? address.Place :
                        MirOperands.Of(assign.Value).SelectMany(MirOperands.Places).FirstOrDefault();
                    if (read is not null)
                    {
                        var places = origins.Address(read, state);
                        if (assign.IsMoveRead && assign.Value.Type is OwnershipTypeSymbol)
                            places = places.Union(origins.Read(read, state).Roots);
                        Access(places, origins.ThroughAliases(read, state), !assign.IsMoveRead,
                            assign.IsMoveRead ? MirBorrowCheck.Move : MirBorrowCheck.Read, assign.Source, location, loans);
                    }
                }
                if (assign.IsSemanticWrite)
                    Access(origins.Address(assign.Destination, state), origins.ThroughAliases(assign.Destination, state),
                        false, MirBorrowCheck.Mutation, assign.Source, location, loans);
                if (assign.IsDeclaration && _locals[assign.Destination.Local].Type is ReferenceTypeSymbol reference)
                {
                    var value = origins.Value(assign.Value, state);
                    Access(value.Roots, value.Aliases.Union(value.Lineage).Union(MirOperands.Of(assign.Value).SelectMany(MirOperands.Places).Select(place => place.Local)), reference.IsReadonly,
                        MirBorrowCheck.Borrow, assign.Source, location, loans);
                }
            }
            if (block.Terminator is MirCall call)
            {
                var location = new MirLocation(block.Id, block.Statements.Length);
                var state = flow.Output[block.Id];
                var loans = Active(state, live.Before[location]).ToArray();
                var arguments = call.Arguments.Where(argument => argument.Type is ReferenceTypeSymbol)
                    .Select(argument => (Value: origins.Operand(argument, state), Readonly: ((ReferenceTypeSymbol)argument.Type).IsReadonly)).ToArray();
                foreach (var argument in arguments)
                    Access(argument.Value.Roots, argument.Value.Aliases.Union(argument.Value.Lineage), argument.Readonly,
                        MirBorrowCheck.Borrow, call.Source, location, loans, arguments.SelectMany(argument => argument.Value.Roots).ToHashSet());
            }
        }
        return [.. violations];

        IEnumerable<Loan> Active(ImmutableDictionary<MirLocalId, MirReferenceValue> state, ImmutableHashSet<MirLocalId> alive)
        {
            foreach (MirLocalId local in alive)
            {
                if (_locals[local].Kind is MirLocalKind.Parameter or MirLocalKind.Receiver or MirLocalKind.Capture || !state.TryGetValue(local, out var value)) continue;
                var leaves = MirReferenceOrigins.ReferenceLeaves(_locals[local].Type).ToArray();
                if (leaves.Length == 0) continue;
                var aliases = _locals[local].Variable is LocalVariableSymbol ? ImmutableHashSet.Create(local) : value.Aliases;
                if (aliases.IsEmpty) aliases = [local];
                foreach (var leaf in leaves)
                    foreach (var origin in value.Project(string.Join('/', leaf.Path)).Roots.Where(Tracked))
                        foreach (var alias in aliases)
                            yield return new(alias, origin, leaf.IsReadonly || origin.IsReadonly);
            }
        }
        void Access(IEnumerable<MirReferenceOrigin> places, ImmutableHashSet<MirLocalId> through, bool shared,
            MirBorrowCheck check, MirSourceInfo source, MirLocation location, Loan[] loans, HashSet<MirReferenceOrigin>? arguments = null)
        {
            foreach (var place in places.Where(Tracked))
            {
                var conflict = loans.FirstOrDefault(loan => !through.Contains(loan.Alias) &&
                    !(shared && loan.IsReadonly) && Overlaps(place, loan.Place) &&
                    // The argument's own ephemeral borrow is consumed by this call.
                    !(arguments is not null && _locals[loan.Alias].Kind == MirLocalKind.Temporary && arguments.Contains(loan.Place)));
                if (conflict is not null) violations.Add(new(location, source, check, place, conflict.Alias));
            }
        }
    }
    private static bool Tracked(MirReferenceOrigin origin) => origin.Kind is not MirReferenceOriginKind.Static and not MirReferenceOriginKind.Unknown;
    public static bool Overlaps(MirReferenceOrigin left, MirReferenceOrigin right)
    {
        if (left.Kind != right.Kind) return false;
        if (left.Kind is MirReferenceOriginKind.RawPointee or MirReferenceOriginKind.UniquePointee or MirReferenceOriginKind.SharedPointee)
        {
            if (left.PointeeType is not null && right.PointeeType is not null && !TypeIdentity.AreSame(left.PointeeType, right.PointeeType)) return false;
            if (left.HandleIdentity != right.HandleIdentity &&
                !(left.Kind == MirReferenceOriginKind.SharedPointee && (!left.IsFresh || !right.IsFresh))) return false;
        }
        else if (left.Ordinal != right.Ordinal) return false;
        string[] a = left.Path.Split('/', StringSplitOptions.RemoveEmptyEntries), b = right.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return a.Zip(b).All(pair => pair.First == "*" || pair.Second == "*" || pair.First == pair.Second);
    }
}