using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirFlowFactsTests
{
    private static readonly MirSourceInfo Source = MirSourceInfo.Generated;
    private static readonly TypeFactory Types = new();
    private static readonly PointerTypeSymbol RecordType = Types.PointerTo(BuiltinTypes.Byte);
    private static readonly Lazy<FunctionSymbol> Symbol = new(() =>
        Compilation.Create(SourceText.From("namespace Facts; void F() {}"))
            .SemanticModel.Functions.Single(function => function.Symbol.Name == "F").Symbol);
    private static MirPlace Place(int id) => new(new(id));
    private static MirFunction CatchBody(bool unknown, TypeSymbol caught)
    {
        MirOperand callee = new MirRequirementOperand(Symbol.Value, MirGenericOperation.FunctionCall,
            Types.FunctionPointer(BuiltinTypes.Void, []), []);
        MirTerminator incoming = unknown
            ? new MirCall(callee, [], null, new(4), new(1), Source)
            : new MirThrow(new MirConstant(1, BuiltinTypes.Int), new(1), Source);
        return new(Symbol.Value,
            [new(new(0), "record", RecordType, MirLocalKind.Temporary, Source),
             new(new(1), "matches", BuiltinTypes.Bool, MirLocalKind.Temporary, Source)],
            [new(new(0), [], incoming),
             new(new(1), [
                 new MirAssign(Place(0), new MirCurrentException(RecordType), Source),
                 new MirAssign(Place(1), new MirExceptionMatches(new MirCopy(Place(0), RecordType), caught), Source)],
                 new MirSwitch(new MirCopy(Place(1), BuiltinTypes.Bool),
                     [new(new(true, BuiltinTypes.Bool), new(2))], new(3), Source)),
             new(new(2), [], new MirReturn(null, Source)),
             new(new(3), [], new MirResumeUnwind(Source)),
             new(new(4), [], new MirReturn(null, Source))], new(0), Source);
    }

    [Fact]
    public void ExactTypedThrowCannotMissItsMatchingCatch()
    {
        MirControlFlow graph = MirFlowFacts.Graph(CatchBody(false, BuiltinTypes.Int));
        Assert.Contains(new MirBlockId(2), graph.Reachable);
        Assert.DoesNotContain(new MirBlockId(3), graph.Reachable);
    }

    [Fact]
    public void IncompatibleCatchCannotConsumeKnownException()
    {
        MirControlFlow graph = MirFlowFacts.Graph(CatchBody(false, BuiltinTypes.Bool));
        Assert.DoesNotContain(new MirBlockId(2), graph.Reachable);
        Assert.Contains(new MirBlockId(3), graph.Reachable);
    }

    [Fact]
    public void UnknownCallExceptionPreservesBothCatchEdges()
    {
        MirControlFlow graph = MirFlowFacts.Graph(CatchBody(true, BuiltinTypes.Int));
        Assert.Contains(new MirBlockId(2), graph.Reachable);
        Assert.Contains(new MirBlockId(3), graph.Reachable);
        Assert.Contains(new MirBlockId(4), graph.Reachable);
    }
}