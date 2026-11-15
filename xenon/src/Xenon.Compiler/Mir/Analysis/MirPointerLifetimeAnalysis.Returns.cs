using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirPointerLifetimeAnalysis
{
    internal static Dictionary<FunctionSymbol, MirReferenceValue> BuildReturnSummaries(MirFunction[] bodies, CancellationToken cancellation)
    {
        var summaries = new Dictionary<FunctionSymbol, MirReferenceValue>();
        foreach (var callee in bodies.SelectMany(body => body.Blocks).Select(block => block.Terminator)
            .OfType<MirCall>().Select(call => call.Callee).OfType<MirFunctionOperand>().Select(operand => operand.Function).Distinct())
            if (callee.MirPointerReturnSummary is { } imported) summaries[callee] = imported;
        var returning = bodies.Where(body => body.ReturnType is PointerTypeSymbol or ArrayTypeSymbol or OwnershipTypeSymbol ||
            body.ReturnType is IFieldStorageTypeSymbol || body.Symbol.FunctionKind is FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer).ToArray();
        foreach (var body in returning) summaries[body.Symbol] = MirReferenceValue.Empty;
        var graphs = returning.ToDictionary(body => body.Symbol, body => MirFlowFacts.Graph(body, cancellation, precisePointerFacts: true));
        bool changed;
        do
        {
            changed = false;
            foreach (var body in returning)
            {
                cancellation.ThrowIfCancellationRequested();
                var origins = new MirReferenceOrigins(body, pointerLifetimes: true, pointerReturns: summaries, symbolicPointerParameters: true);
                var flow = MirDataflow.Solve(graphs[body.Symbol], origins, cancellation);
                var summary = MirReferenceValue.Empty;
                foreach (var block in body.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
                    if (block.Terminator is MirReturn { Value: { } value }) summary = summary.Union(origins.Operand(value, flow.Output[block.Id]));
                    else if (block.Terminator is MirReturn && body.Symbol.FunctionKind is FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer &&
                        body.Locals.FirstOrDefault(local => local.Kind == MirLocalKind.Receiver) is { } receiver)
                        summary = summary.Union(origins.Read(new MirPlace(receiver.Id).Project(new MirDerefProjection()), flow.Output[block.Id]));
                var previous = summaries.GetValueOrDefault(body.Symbol, MirReferenceValue.Empty);
                summary = previous.Union(summary);
                if (previous.Same(summary)) continue;
                summaries[body.Symbol] = summary;
                changed = true;
            }
        } while (changed);

        foreach (var body in returning) body.Symbol.MirPointerReturnSummary = summaries[body.Symbol];

        return summaries;
    }
}

