using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

/// <summary>Interprocedural exception sets from explicit MIR unwind exits.</summary>
public sealed class MirExceptionEffects
{
    private readonly Dictionary<FunctionSymbol, MirFunction> _bodies;
    private readonly Dictionary<FunctionSymbol, HashSet<TypeSymbol>> _effects;
    private readonly CancellationToken _cancellation;
    private readonly Dictionary<MirIntrinsicCall, ImmutableHashSet<MirReferenceOrigin>> _releaseOrigins = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<MirFunction> _originBodies = new(ReferenceEqualityComparer.Instance);

    public MirExceptionEffects(IEnumerable<MirFunction> invocationBodies, CancellationToken cancellation = default)
    {
        _bodies = invocationBodies.DistinctBy(body => body.Symbol).ToDictionary(body => body.Symbol);
        _effects = _bodies.Keys.ToDictionary(symbol => symbol, _ => new HashSet<TypeSymbol>(TypeIdentity.Comparer));
        _cancellation = cancellation;
    }

    public IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>> Infer()
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var (symbol, body) in _bodies)
            {
                _cancellation.ThrowIfCancellationRequested();
                HashSet<TypeSymbol> escaped = Escaping(body);
                int before = _effects[symbol].Count;
                _effects[symbol].UnionWith(escaped);
                changed |= before != _effects[symbol].Count;
            }
        } while (changed);
        return _effects;
    }

    public HashSet<TypeSymbol> Escaping(MirFunction body)
    {
        if (_originBodies.Add(body) && body.Blocks.Any(block => block.Terminator is MirIntrinsicCall { RestoresAllocator: true, AllocationReleases.IsEmpty: false }))
        {
            var origins = new MirReferenceOrigins(body);
            var states = MirDataflow.Solve(new MirControlFlow(body), origins, _cancellation);
            foreach (MirBasicBlock block in body.Blocks.Where(block => states.Graph.Reachable.Contains(block.Id)))
                if (block.Terminator is MirIntrinsicCall { RestoresAllocator: true } release)
                    _releaseOrigins[release] = origins.Operand(release.Arguments[0], states.Before[new(block.Id, block.Statements.Length)]).Roots;
        }
        var analysis = new MirFlowFacts(_effects, CallEffects);
        var flow = MirDataflow.Solve(new MirControlFlow(body), analysis, _cancellation);
        var result = new HashSet<TypeSymbol>(TypeIdentity.Comparer);
        foreach (MirBasicBlock block in body.Blocks)
            if ((block.Terminator is MirResumeUnwind || block.Id == body.UnwindExit) && flow.Output[block.Id].Reachable)
                result.UnionWith(flow.Output[block.Id].Pending);
        return result;
    }

    private IEnumerable<TypeSymbol>? CallEffects(MirTerminator terminator)
    {
        if (terminator is MirIntrinsicCall { Function.AllocationOperation: AllocationOperation.Allocate } allocation)
            return _bodies.Keys.Where(f => f.AllocationOperation == AllocationOperation.Allocate &&
                    f.ContainingStruct is { } candidate && allocation.Arguments[^1].Type is PointerTypeSymbol { ElementType: StructTypeSymbol provider } &&
                    AllocationRules.IsAllocatorSubtype(candidate, provider)).Append(allocation.Function!)
                .SelectMany(Effects).Distinct(TypeIdentity.Comparer).ToArray();
        if (terminator is MirIntrinsicCall { RestoresAllocator: true } release)
        {
            IEnumerable<FunctionSymbol> targets = release.AllocationReleases;
            if (_releaseOrigins.TryGetValue(release, out var roots) && !roots.IsEmpty && roots.All(root => root.AllocationKind == MirAllocationKind.Typed))
            {
                StructTypeSymbol[] providers = roots.Select(root => root.AllocatorType).OfType<StructTypeSymbol>().Distinct().ToArray();
                targets = targets.Concat(_bodies.Keys.Where(f => f.AllocationOperation == AllocationOperation.Deallocate))
                    .Where(f => providers.Any(provider => ReferenceEquals(f, provider.DeallocationOperator) ||
                        f.ContainingStruct is { } candidate && AllocationRules.IsAllocatorSubtype(candidate, provider))).Distinct();
            }
            return targets.SelectMany(Effects).Distinct(TypeIdentity.Comparer).ToArray();
        }
        if (terminator is not MirCall call) return null;
        if (call.Callee is MirFunctionOperand direct && call.InterfaceType is null)
        {
            var targets = new List<FunctionSymbol> { direct.Function };
            if (direct.Function.IsVirtual || direct.Function.IsOverride)
                targets.AddRange(_bodies.Keys.Where(candidate => candidate.IsOverride && candidate.Name == direct.Function.Name));
            return targets.SelectMany(Effects).Distinct(TypeIdentity.Comparer).ToArray();
        }
        (TypeSymbol Result, ImmutableArray<TypeSymbol> Parameters)? signature = call.Callee.Type switch
        {
            FunctionPointerTypeSymbol pointer => (pointer.ReturnType, pointer.ParameterTypes),
            FunctionValueTypeSymbol callable => (callable.ReturnType, callable.ParameterTypes),
            _ => null,
        };
        if (signature is not { } type) return null;
        return _bodies.Keys.Where(candidate => TypeIdentity.AreSame(candidate.ReturnType, type.Result) &&
                candidate.Parameters.Select(parameter => parameter.Type).SequenceEqual(type.Parameters, TypeIdentity.Comparer))
            .SelectMany(Effects).Distinct(TypeIdentity.Comparer).ToArray();
    }

    private IEnumerable<TypeSymbol> Effects(FunctionSymbol function) => function.IsExtern ? [] :
        _effects.TryGetValue(function, out var known) ? known : [BuiltinTypes.Error];
}