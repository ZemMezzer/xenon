using System.Globalization;
using System.Text;
using Xenon.Compiler.Mir.Analysis;

namespace Xenon.Compiler.Mir;

/// <summary>Optional deterministic provenance trace for semantic reads and borrows.</summary>
public static class MirProvenancePrinter
{
    public static string Dump(MirFunction function, CancellationToken cancellation = default)
    {
        var origins = new MirReferenceOrigins(function);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(function, cancellation), origins, cancellation);
        var text = new StringBuilder("// provenance ").Append(function.Symbol.FullName).Append('\n');
        foreach (var block in function.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)).OrderBy(block => block.Id.Value))
            for (int index = 0; index < block.Statements.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (block.Statements[index] is not MirAssign assign) continue;
                IEnumerable<MirPlace> places = assign.Value is MirBorrow borrow ? [borrow.Place] :
                    assign.IsSemanticRead ? MirOperands.Of(assign.Value).SelectMany(MirOperands.Places) : [];
                foreach (var place in places.Distinct())
                {
                    var roots = origins.Address(place, flow.Before[new(block.Id, index)]);
                    text.Append("// ").Append(block.Id).Append(':').Append(index.ToString(CultureInfo.InvariantCulture))
                        .Append(' ').Append(MirPrinter.Place(place)).Append(" => ");
                    text.AppendJoin(" | ", roots.Select(root =>
                        $"{root.Kind}(root={root.Ordinal.ToString(CultureInfo.InvariantCulture)}, handle={root.HandleIdentity ?? "-"}, authority={root.Authority}, fresh={root.IsFresh.ToString().ToLowerInvariant()})/{root.Path}" + (root.SharedOwner is { } owner ? $" shared-owner={owner.Identity}/{owner.Path}, fresh={owner.IsFresh}" : ""))
                        .Order(StringComparer.Ordinal));
                    text.Append('\n');
                }
            }
        return text.ToString();
    }
}