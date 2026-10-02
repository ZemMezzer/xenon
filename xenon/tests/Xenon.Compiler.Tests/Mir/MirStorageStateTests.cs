using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirStorageStateTests
{
    [Theory]
    [InlineData("storage<int> slot; slot = 1;", MirStorageContent.Empty)]
    [InlineData("storage<int> slot; slot = 1; slot = 2;", MirStorageContent.Live)]
    [InlineData("storage<int> slot; if (flag) slot = 1; slot = 2;", MirStorageContent.Unknown)]
    [InlineData("storage<int> slot; slot = 1; destruct(slot); slot = 2;", MirStorageContent.Empty)]
    [InlineData("storage<int> slot; storage<int>& alias = slot; alias = 1; slot = 2;", MirStorageContent.Live)]
    public void InitializationGuardUsesReachingStorageState(string body, MirStorageContent expected)
    {
        var compilation = Compilation.Create(SourceText.From("namespace Test; void Use(bool flag) { " + body + " }"));
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var mir = MirLowerer.Lower(function, compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations, diagnosticRecovery: true);
        var analysis = new MirStorageStateAnalysis(mir);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(mir), analysis);
        var check = mir.Blocks.Last(block => block.Terminator is MirIntrinsicCall { StorageCheck: MirStorageCheckPurpose.Initialize });
        Assert.Equal(expected, analysis.State((MirIntrinsicCall)check.Terminator, flow.Output[check.Id]));
    }
}