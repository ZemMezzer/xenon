using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirCoroutineTests
{
    private static (Compilation Compilation, MirFunction Mir) Body(string body)
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Coroutine;
            struct Result { public static void operator resolve(Result& target, int value) {} }
            struct Operation
            {
                public static bool operator await(readonly Operation& value, storage<int>& result, function void() continuation)
                { result = 42; return true; }
            }
            """ + " async Result Use() { " + body + " }"));
        Assert.Empty(compilation.Diagnostics);
        var function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        return (compilation, MirLowerer.Lower(function, compilation.TypeFactory));
    }

    [Fact]
    public void FrameBackingSelectionDoesNotRequireSourceOriginIds()
    {
        var (compilation, mir) = Body("int[] values = int[3]; int result = await Operation(); return values.Length + result;");
        Assert.All(mir.Blocks, block => Assert.Null(block.Terminator.Source.OriginId));
        var lowered = MirCoroutineTransform.Lower(mir, compilation.TypeFactory);
        Assert.Contains(lowered.Blocks, block => block.Terminator is MirIntrinsicCall
            { Intrinsic: MirIntrinsicKind.AllocateStackArray, RetainedInFrame: true, FixedArrayLength: 3 });
        Assert.False(TypeFacts.CanRelocate(lowered.Coroutine!.FrameType));
        Assert.Equal(lowered.Coroutine.FrameLocals.Count,
            ((StructTypeSymbol)lowered.Coroutine.FrameType.ElementType).Fields.Length);
        Assert.Empty(MirVerifier.Verify(lowered));
    }
    [Theory]
    [InlineData("return 1;")]
    [InlineData("return 1; await Operation();")]
    [InlineData("if (false) await Operation(); return 1;")]
    public void NoSuspensionNeedsNoCoroutineTransformation(string body)
    {
        var (compilation, mir) = Body(body);
        Assert.Null(mir.Resumable);
        Assert.Same(mir, MirCoroutineTransform.Lower(mir, compilation.TypeFactory));
        Assert.Null(mir.Coroutine);
    }

    [Fact]
    public void SuspensionAndCompletionHaveExplicitDispatchAndFrameRelease()
    {
        var (compilation, mir) = Body("int live = 7; int first = await Operation(); int second = await Operation(); return live + first + second;");
        var analysis = new MirFrameAnalysis(mir);
        var lowered = MirCoroutineTransform.Lower(mir, compilation.TypeFactory);
        Assert.Empty(MirVerifier.Verify(lowered));
        Assert.DoesNotContain(lowered.Blocks, block => block.Terminator is MirSuspend);
        Assert.Equal(2, lowered.Coroutine!.States.Length);
        Assert.True(analysis.LiveAcross.SetEquals(lowered.Coroutine.FrameLocals));
        Assert.Contains(lowered.Locals.Single(local => local.Name == "live").Id, lowered.Coroutine.FrameLocals);
        var operations = lowered.Blocks.Select(block => block.Terminator).OfType<MirIntrinsicCall>().ToArray();
        Assert.Single(operations, operation => operation.Intrinsic == MirIntrinsicKind.CoroutineCreate);
        Assert.Single(operations, operation => operation.Intrinsic == MirIntrinsicKind.CoroutineFree);
        Assert.Single(operations, operation => operation.Intrinsic == MirIntrinsicKind.CoroutineEnd);
        Assert.Equal(3, operations.Count(operation => operation.Intrinsic == MirIntrinsicKind.CoroutineSuspend));
        foreach (var suspend in operations.Where(operation => operation.Intrinsic == MirIntrinsicKind.CoroutineSuspend))
        {
            var dispatch = Assert.IsType<MirSwitch>(lowered.Blocks.Single(block => block.Id == suspend.Normal).Terminator);
            Assert.Equal(2, dispatch.Cases.Length);
        }
        Assert.Contains("coroutine handle", MirPrinter.Dump(lowered));
        Assert.Same(lowered, MirCoroutineTransform.Lower(lowered, compilation.TypeFactory));
    }

    [Fact]
    public void UndefinedPayloadProtocolCannotBeSilentlyDiscarded()
    {
        var (compilation, mir) = Body("return await Operation();");
        mir = mir with { Blocks = [.. mir.Blocks.Select(block => block.Terminator is MirSuspend suspend
            ? block with { Terminator = suspend with { Payload = new MirConstant(1, BuiltinTypes.Int) } } : block)] };
        Assert.Throws<NotSupportedException>(() => MirCoroutineTransform.Lower(mir, compilation.TypeFactory));
    }

    [Fact]
    public void VerifierRejectsFrameThatOmitsLiveStorage()
    {
        var (compilation, mir) = Body("int live = 7; return live + await Operation();");
        var lowered = MirCoroutineTransform.Lower(mir, compilation.TypeFactory);
        lowered = lowered with { Coroutine = lowered.Coroutine! with { FrameLocals = [] } };
        Assert.Contains(MirVerifier.Verify(lowered), error => error.Message.Contains("exactly the suspension live set"));
    }
}