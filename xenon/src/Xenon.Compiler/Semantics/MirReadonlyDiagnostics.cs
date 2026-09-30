using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>Source diagnostic boundary for readonly effects inferred from MIR.</summary>
internal static class MirReadonlyDiagnostics
{
    public static void Analyze(IEnumerable<BoundFunction> functions,
        IEnumerable<(FunctionSymbol Function, TextLocation Location)> targets,
        ImmutableArray<StructTypeSymbol> structures, TypeFactory types,
        DiagnosticBag diagnostics, IReadOnlyDictionary<BoundExpression, TextLocation> locations,
        CancellationToken cancellation)
    {
        var checkedFunctions = targets.ToArray();
        if (checkedFunctions.Length == 0) return;
        var bodies = functions.Where(function => !TypeIdentity.AreSame(function.Symbol.ReturnType, BuiltinTypes.Error) &&
                !function.Symbol.Parameters.Any(parameter => TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void)))
            .ToDictionary(function => function.Symbol,
                function => MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true));
        foreach (var (function, location) in checkedFunctions)
        {
            cancellation.ThrowIfCancellationRequested();
            if (bodies.TryGetValue(function, out var body))
                new MirReadonlyAnalysis(function, diagnostics, location, bodies, structures, cancellation).Analyze(body);
        }
    }
}