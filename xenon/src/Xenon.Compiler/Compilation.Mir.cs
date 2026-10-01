using System.Collections.Immutable;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler;

public sealed partial class Compilation
{
    private readonly object _mirLock = new();
    private ImmutableArray<MirFunction> _initialMir;
    private ImmutableArray<MirFunction> _loweredMir;

    public ImmutableArray<FunctionSymbol> GetDeclaredFunctionSymbols() =>
        [.. SemanticModel.Functions.Select(function => function.Symbol)];

    public ImmutableArray<FunctionSymbol> GetStaticImplementationSymbols() =>
        [.. GetStaticImplementationFunctions().Select(function => function.Symbol)];

    /// <summary>Verified executable bodies; LLVM and dump tools consume this compiler-owned pipeline.</summary>
    public ImmutableArray<MirFunction> GetMirFunctions(bool lowered = true, CancellationToken cancellation = default)
    {
        if (HasErrors) throw new InvalidOperationException("Executable MIR requires a compilation without errors.");
        lock (_mirLock)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_initialMir.IsDefault)
                _initialMir = GetNativeReachability(cancellation).InitialMir;
            if (!lowered) return _initialMir;
            if (_loweredMir.IsDefault)
                _loweredMir = [.. _initialMir.Select(function => MirCoroutineTransform.Lower(
                    MirReachability.Prune(function, cancellation), TypeFactory, cancellation))];
            return _loweredMir;
        }
    }
    /// <summary>Stable MIR text, including the separate initializer of resumable functions.</summary>
    public string DumpMir(bool lowered = true, bool includeSource = false, CancellationToken cancellation = default, bool includeProvenance = false)
    {
        var text = new System.Text.StringBuilder();
        foreach (var function in GetMirFunctions(lowered, cancellation)) Append(function);
        return text.ToString();

        void Append(MirFunction function)
        {
            cancellation.ThrowIfCancellationRequested();
            if (function.Resumable is { } resumable)
            {
                text.Append("// resumable initialization\n");
                Append(resumable.Initialization);
                text.Append("// resumable body\n");
            }
            text.Append(MirPrinter.Dump(function, includeSource));
            if (includeProvenance) text.Append(MirProvenancePrinter.Dump(function, cancellation));
        }
    }
}