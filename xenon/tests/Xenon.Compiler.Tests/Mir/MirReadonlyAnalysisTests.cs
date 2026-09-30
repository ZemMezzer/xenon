using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirReadonlyAnalysisTests
{
    private static ImmutableArray<Diagnostic> Analyze(string source)
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace Example; " + source));
        var bodies = compilation.SemanticModel.Functions.ToDictionary(function => function.Symbol,
            function => MirLowerer.Lower(function, compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true));
        var target = bodies.Single(pair => pair.Key.Name == "Use");
        var diagnostics = new DiagnosticBag();
        ImmutableArray<StructTypeSymbol> types = [.. bodies.Keys.Select(function => function.ContainingType).OfType<StructTypeSymbol>().Distinct()];
        new MirReadonlyAnalysis(target.Key, diagnostics, target.Value.Source.Location, bodies, types, default).Analyze(target.Value);
        return diagnostics.ToImmutableArray();
    }

    [Theory]
    [InlineData("static struct State { public static int Value; } void readonly Use() { State.Value = 1; }", DiagnosticIds.HiddenStateMutation)]
    [InlineData("static struct State { public static int Value; } void readonly Use() { int* alias = &State.Value; *alias = 1; }", DiagnosticIds.HiddenStateMutation)]
    [InlineData("static struct State { public static int Value; } int* readonly Use() { return &State.Value; }", DiagnosticIds.MutableCapabilityReturn)]
    [InlineData("void Mutate() {} void readonly Use() { Mutate(); }", DiagnosticIds.NonReadonlyCallFromReadonlyFunction)]
    [InlineData("struct Holder { public int* Pointer; } static struct State { public static int Value; } Holder readonly Use() { Holder value = Holder(); value.Pointer = &State.Value; return value; }", DiagnosticIds.MutableCapabilityReturn)]
    [InlineData("struct Holder { public int Value; public void Set() { Value = 1; } } static struct State { public static Holder Value; } void readonly Use() { State.Value.Set(); }", DiagnosticIds.MutableMethodOnHiddenState)]
    public void HiddenCapabilitiesCannotGainMutationRights(string source, string expected) =>
        Assert.Contains(Analyze(source), diagnostic => diagnostic.Id == expected);

    [Theory]
    [InlineData("void readonly Use(int* output) { *output = 1; }")]
    [InlineData("void readonly Use(int& output) { int* alias = &output; *alias = 1; }")]
    [InlineData("struct Holder { public int Value; public void Set() { Value = 1; } } void readonly Use() { Holder local = Holder(); local.Set(); }")]
    [InlineData("void readonly Use() { int[] values = int[2]; values[0] = 1; }")]
    [InlineData("struct Holder { public int* Pointer; } void readonly Use(int* output) { Holder local = Holder(); local.Pointer = output; *local.Pointer = 1; }")]
    public void ExplicitAndLocalStorageRemainWritable(string source) => Assert.Empty(Analyze(source));
}