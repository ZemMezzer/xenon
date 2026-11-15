using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirPointerLifetimeAnalysis
{
    private static MirFunction[] WithCaptureViews(MirFunction[] bodies)
    {
        var views = new List<MirFunction>();
        foreach (var body in bodies.Where(body => !body.Symbol.LambdaCaptures.IsEmpty && !body.Symbol.IsAsync))
        {
            var original = body.Symbol;
            var parameters = original.Parameters.Concat(original.LambdaCaptures.Select(capture =>
                new ParameterSymbol(capture.Name, capture.StorageType, original.Parameters.Length + capture.Ordinal))).ToImmutableArray();
            var symbol = new FunctionSymbol(original.Name + "$capture_effects", original.ContainingSymbol!, FunctionKind.Ordinary,
                original.ReturnType, parameters, Accessibility.Private, isDefinition: true);
            original.MirCaptureFunction = symbol;
            var locals = body.Locals.Select(local => local.Variable switch
            {
                CaptureVariableSymbol capture => local with { Kind = MirLocalKind.Parameter,
                    Variable = symbol.Parameters[original.Parameters.Length + capture.Ordinal] },
                ParameterSymbol parameter => local with { Variable = symbol.Parameters[parameter.Ordinal] },
                _ => local,
            }).ToImmutableArray();
            views.Add(body with { Symbol = symbol, Locals = locals });
        }
        return [.. bodies, .. views];
    }
}
