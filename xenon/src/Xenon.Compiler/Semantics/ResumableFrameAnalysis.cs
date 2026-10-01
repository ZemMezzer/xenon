using System.Collections.Immutable;
using System.Numerics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

/// <summary>
/// Source-facing frame diagnostics backed by the common MIR CFG, liveness and
/// reaching-storage analysis. Bound identities here only map diagnostics back
/// to their origins; there is no source-level control-flow interpretation.
/// </summary>
public sealed class ResumableFrameAnalysis
{
    public PinTypeSymbol FrameType { get; }
    public MirFunction Mir { get; }
    public MirDataflowResult<ImmutableHashSet<MirLocalId>> Liveness { get; }
    public IReadOnlyDictionary<BoundAwaitExpression, ImmutableHashSet<Symbol>> Suspensions { get; }
    public IReadOnlyDictionary<BoundAwaitExpression, ImmutableHashSet<Symbol>> LiveValues { get; }
    public ImmutableHashSet<Symbol> LiveAcross { get; }
    public ImmutableHashSet<BoundArrayCreationExpression> RetainedArrays { get; }

    private ResumableFrameAnalysis(FunctionSymbol function, BoundBlockStatement body, TypeFactory types)
    {
        var expressions = BoundTree.DescendantsAndSelf(body).OfType<BoundExpression>()
            .Distinct<BoundExpression>(ReferenceEqualityComparer.Instance).ToArray();
        var origins = expressions.Select((expression, id) => (expression, id))
            .ToDictionary(pair => pair.expression, pair => pair.id, (IEqualityComparer<BoundExpression>)ReferenceEqualityComparer.Instance);
        Mir = MirLowerer.Lower(new(function, body), types, origins: origins);
        var analysis = new MirFrameAnalysis(Mir);
        Liveness = analysis.Liveness;
        var locals = Mir.Locals.ToDictionary(local => local.Id);
        var blocks = Mir.Blocks.ToDictionary(block => block.Id);
        var suspensions = new Dictionary<BoundAwaitExpression, ImmutableHashSet<Symbol>>(ReferenceEqualityComparer.Instance);
        var retained = ImmutableHashSet.CreateBuilder<BoundArrayCreationExpression>(ReferenceEqualityComparer.Instance);
        foreach (var (blockId, state) in analysis.Suspensions)
        {
            if (blocks[blockId].Terminator.Source.OriginId is not { } id) continue;
            var suspension = (BoundAwaitExpression)expressions[id];
            foreach (MirBlockId allocation in state.Allocations)
                if (blocks[allocation].Terminator.Source.OriginId is { } allocationOrigin &&
                    expressions[allocationOrigin] is BoundArrayCreationExpression array) retained.Add(array);
            ImmutableHashSet<Symbol> symbols = state.Live.Select(local =>
                locals[local].Kind == MirLocalKind.Receiver ? (Symbol)function : locals[local].Variable)
                .OfType<Symbol>().ToImmutableHashSet();
            suspensions[suspension] = suspensions.GetValueOrDefault(suspension, []).Union(symbols);
        }
        Suspensions = LiveValues = suspensions;
        LiveAcross = suspensions.Values.SelectMany(value => value).ToImmutableHashSet();
        RetainedArrays = retained.ToImmutable();
        FrameType = analysis.CreateFrameType(Mir, types);
        if (TypeFacts.CanRelocate(FrameType)) throw new InvalidOperationException("A pinned frame cannot relocate.");
    }

    public static ResumableFrameAnalysis Analyze(FunctionSymbol function, BoundBlockStatement body, TypeFactory types) =>
        new(function, body, types);

    public static bool TryGetConstantLength(BoundArrayCreationExpression array, out ulong length)
    {
        BigInteger count = 1;
        var constants = new ConstantEvaluationContext(null);
        foreach (BoundExpression dimension in array.Dimensions)
        {
            if (!constants.TryFold(dimension, out object? value)) { length = 0; return false; }
            BigInteger size = SemanticAnalyzer.ToInteger(value);
            if (size < 0) { length = 0; return false; }
            count *= size;
        }
        length = count <= int.MaxValue ? (ulong)count : 0;
        return count <= int.MaxValue;
    }

    internal static bool CarriesBorrow(TypeSymbol type) => CarriesBorrow(type, []);
    private static bool CarriesBorrow(TypeSymbol type, HashSet<TypeSymbol> visited) =>
        type is ReferenceTypeSymbol or ArrayTypeSymbol or FunctionValueTypeSymbol ||
        type is LifetimeModifierTypeSymbol modifier && CarriesBorrow(modifier.ElementType, visited) ||
        type is IFieldStorageTypeSymbol structure && visited.Add(type) &&
            structure.AllInstanceFields.Any(field => CarriesBorrow(field.Type, visited));
}
