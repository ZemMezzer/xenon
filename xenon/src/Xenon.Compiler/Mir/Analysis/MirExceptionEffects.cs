using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

/// <summary>Interprocedural exception sets from explicit MIR unwind exits.</summary>
public sealed class MirExceptionEffects
{
    private readonly Dictionary<FunctionSymbol, MirFunction> _bodies;
    private readonly Dictionary<FunctionSymbol, HashSet<TypeSymbol>> _effects;
    private readonly CancellationToken _cancellation;

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