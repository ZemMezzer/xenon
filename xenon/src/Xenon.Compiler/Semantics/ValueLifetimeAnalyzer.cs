using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>Source/XELIB contract adapter for the common MIR lifetime dataflow.</summary>
internal static class ValueLifetimeAnalyzer
{
    private sealed class Annotation { public ValueLifetimeDependencies Value = new([]); }
    private static readonly ConditionalWeakTable<BoundExpression, Annotation> Annotations = new();
    internal static ValueLifetimeDependencies GetDependencies(BoundExpression expression) =>
        Annotations.TryGetValue(expression, out var dependencies) ? dependencies.Value : new([]);

    private sealed record Input(BoundFunction Bound, MirFunction Mir, BoundExpression[] Expressions);

    public static void Analyze(ImmutableArray<BoundFunction> functions, GenericImplementationStore generics,
        TypeFactory types, DiagnosticBag diagnostics, IReadOnlyDictionary<BoundExpression, TextLocation> locations,
        CancellationToken cancellation)
    {
        BoundFunction[] bodies = functions.Concat(generics.Functions
            .Where(pair => pair.Key.IsSourceDefined && pair.Value.PortableBody is not null)
            .Select(pair => new BoundFunction(pair.Key, pair.Value.PortableBody!)))
            .DistinctBy(function => function.Symbol)
            .Where(function => !TypeIdentity.AreSame(function.Symbol.ReturnType, BuiltinTypes.Error) &&
                !function.Symbol.Parameters.Any(parameter => TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void))).ToArray();
        var inputs = bodies.Select(body =>
        {
            BoundExpression[] expressions = BoundTree.DescendantsAndSelf(body.Body).OfType<BoundExpression>()
                .Distinct<BoundExpression>(ReferenceEqualityComparer.Instance).ToArray();
            var origins = expressions.Select((expression, id) => (expression, id)).ToDictionary(
                pair => pair.expression, pair => pair.id, (IEqualityComparer<BoundExpression>)ReferenceEqualityComparer.Instance);
            return new Input(body, MirLowerer.Lower(body, types, locations, cancellation, origins, diagnosticRecovery: true), expressions);
        }).ToArray();
        foreach (Input input in inputs.Where(input => input.Bound.Body.IsResumable))
        {
            var frame = new MirFrameAnalysis(input.Mir, cancellation);
            var blocks = input.Mir.Blocks.ToDictionary(block => block.Id);
            foreach (MirBlockId allocation in frame.Suspensions.Values.SelectMany(state => state.Allocations).Distinct())
                if (blocks[allocation].Terminator is MirIntrinsicCall
                    { Intrinsic: MirIntrinsicKind.AllocateStackArray, FixedArrayLength: null } array)
                    diagnostics.Report(array.Source.Location,
                        "runtime-sized array backing storage is live across await and has no fixed inline frame layout; use owning heap storage or finish its lifetime before suspension",
                        DiagnosticIds.BorrowAcrossAwait);
        }
        var localFunctions = bodies.Select(body => body.Symbol).ToHashSet();
        IEnumerable<FunctionSymbol> BaseMethods(FunctionSymbol method) =>
            method.IsOverride && method.ContainingSymbol is StructTypeSymbol { BaseType: { } parent }
                ? parent.FindMethods(method.Name).Where(method.Overrides) : [];
        var effects = ResumableExceptionAnalyzer.InferEffects(bodies, types, cancellation);
        bool changed;
        do
        {
            changed = false;
            foreach (Input input in inputs)
            {
                cancellation.ThrowIfCancellationRequested();
                var analysis = new MirLifetimeAnalysis(input.Mir, effects, cancellation);
                _ = analysis.Analyze();
                FunctionSymbol symbol = input.Bound.Symbol;
                var returns = Order(symbol.ResultLifetimeDependencies.Union(analysis.Returns));
                var stores = Order(symbol.LifetimeStores.Union(analysis.Stores));
                bool operation = input.Bound.Body.IsResumable || analysis.ReturnsOperation || symbol.ReturnsResumableOperation;
                changed |= !returns.SequenceEqual(symbol.ResultLifetimeDependencies) ||
                    !stores.SequenceEqual(symbol.LifetimeStores) || operation != symbol.ReturnsResumableOperation;
                symbol.ResultLifetimeDependencies = returns;
                symbol.LifetimeStores = stores;
                symbol.ReturnsResumableOperation = operation;
                symbol.CreatesResumableOperation = input.Bound.Body.IsResumable;
            }
            foreach (Input input in inputs)
                foreach (FunctionSymbol inherited in BaseMethods(input.Bound.Symbol).Where(localFunctions.Contains))
                {
                    FunctionSymbol symbol = input.Bound.Symbol;
                    var returns = Order(inherited.ResultLifetimeDependencies.Union(symbol.ResultLifetimeDependencies));
                    var stores = Order(inherited.LifetimeStores.Union(symbol.LifetimeStores));
                    bool operation = inherited.ReturnsResumableOperation || symbol.ReturnsResumableOperation;
                    changed |= !returns.SequenceEqual(inherited.ResultLifetimeDependencies) ||
                        !stores.SequenceEqual(inherited.LifetimeStores) || operation != inherited.ReturnsResumableOperation;
                    inherited.ResultLifetimeDependencies = returns;
                    inherited.LifetimeStores = stores;
                    inherited.ReturnsResumableOperation = operation;
                    inherited.CreatesResumableOperation |= symbol.CreatesResumableOperation;
                }
        } while (changed);
        foreach (Input input in inputs)
        {
            cancellation.ThrowIfCancellationRequested();
            var analysis = new MirLifetimeAnalysis(input.Mir, effects, cancellation);
            var flow = analysis.Analyze();
            foreach (MirLifetimeDiagnostic diagnostic in analysis.Diagnostics)
                diagnostics.Report(Location(diagnostic.Source, input.Bound.Symbol), diagnostic.Message, diagnostic.Id);
            foreach (FunctionSymbol inherited in BaseMethods(input.Bound.Symbol).Where(method => !localFunctions.Contains(method)))
                if (input.Bound.Symbol.ResultLifetimeDependencies.Except(inherited.ResultLifetimeDependencies).Any() ||
                    input.Bound.Symbol.LifetimeStores.Except(inherited.LifetimeStores).Any())
                    diagnostics.Report(Location(input.Mir.Source, input.Bound.Symbol),
                        "override introduces a lifetime dependency absent from the imported base method contract", DiagnosticIds.ValueLifetimeEscape);
            Annotate(input, flow);
        }

        TextLocation Location(MirSourceInfo source, FunctionSymbol function)
        {
            if (source.Location.Source is not null) return source.Location;
            TextLocation location = function.Locations.FirstOrDefault();
            if (location.Source is null) location = locations.Values.FirstOrDefault();
            return location.Source is null ? new(SourceText.From("", "<library specialization>"), new(0, 0)) : location;
        }
    }

    private static ImmutableArray<LifetimeDependency> Order(IEnumerable<LifetimeDependency> values) =>
        [.. values.OrderBy(value => value.Kind).ThenBy(value => value.Ordinal).ThenBy(value => value.FieldPath)];
    private static ImmutableArray<LifetimeStore> Order(IEnumerable<LifetimeStore> values) =>
        [.. values.OrderBy(value => value.Destination).ThenBy(value => value.Source.Kind)
            .ThenBy(value => value.Source.Ordinal).ThenBy(value => value.Source.FieldPath).ThenBy(value => value.FieldPath)];

    private static void Annotate(Input input, MirDataflowResult<MirLifetimeState> flow)
    {
        var values = new Dictionary<int, ImmutableHashSet<ValueLifetimeDependency>>();
        foreach (MirBasicBlock block in input.Mir.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
        {
            for (int index = 0; index < block.Statements.Length; index++)
                if (block.Statements[index] is MirAssign assign && assign.Source.OriginId is { } origin &&
                    TypeIdentity.AreSame(assign.Value.Type, input.Expressions[origin].Type))
                    Add(origin, flow.After[new(block.Id, index)].Values.GetValueOrDefault(assign.Destination.Local, MirLifetimeValue.Empty));
            (MirPlace? Destination, MirBlockId Normal) call = block.Terminator switch
            {
                MirCall invoked => (invoked.Destination, invoked.Normal),
                MirIntrinsicCall invoked => (invoked.Destination, invoked.Normal),
                _ => (null, default),
            };
            if (call.Destination is { } destination && block.Terminator.Source.OriginId is { } callOrigin)
                Add(callOrigin, flow.Input[call.Normal].Values.GetValueOrDefault(destination.Local, MirLifetimeValue.Empty));
        }
        foreach (var (origin, dependencies) in values)
            Annotations.GetValue(input.Expressions[origin], _ => new()).Value = new([.. dependencies]);

        void Add(int origin, MirLifetimeValue value) =>
            values[origin] = values.GetValueOrDefault(origin, []).Union(value.Dependencies);
    }
}