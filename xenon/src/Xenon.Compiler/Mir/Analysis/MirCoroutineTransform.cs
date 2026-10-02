using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed record MirCoroutineState(int State, MirBlockId Suspension, MirBlockId Resume,
    ImmutableHashSet<MirLocalId> Live, ImmutableHashSet<MirBlockId> Allocations);

public sealed record MirCoroutineLayout(MirLocalId Handle, ImmutableArray<MirCoroutineState> States,
    ImmutableHashSet<MirLocalId> FrameLocals, PinTypeSymbol FrameType);

/// <summary>Elaborates suspension and terminal frame release into ordinary MIR control flow.</summary>
public static class MirCoroutineTransform
{
    public static MirFunction Lower(MirFunction function, TypeFactory types, CancellationToken cancellation = default)
    {
        if (function.Coroutine is not null || function.Resumable is null) return function;
        MirVerifier.VerifyOrThrow(function);
        var frame = new MirFrameAnalysis(function, cancellation);
        var retainedAllocations = frame.Suspensions.Values.SelectMany(state => state.Allocations).ToHashSet();
        var locals = function.Locals.ToBuilder();
        var blocks = function.Blocks.ToBuilder();
        int nextBlock = function.Blocks.Max(block => block.Id.Value) + 1;
        var pointer = types.PointerTo(BuiltinTypes.Byte);
        var source = function.Source;
        MirLocalId AddLocal(string name, TypeSymbol type)
        {
            var id = new MirLocalId(locals.Count);
            locals.Add(new(id, name, type, MirLocalKind.Temporary, source));
            return id;
        }
        MirBlockId NewBlock() => new(nextBlock++);
        var handle = AddLocal("coroutine.handle", pointer);
        var state = AddLocal("coroutine.suspend.result", BuiltinTypes.Byte);
        var entry = NewBlock();
        var complete = NewBlock();
        var free = NewBlock();
        var end = NewBlock();
        var exit = NewBlock();
        var unreachable = NewBlock();
        MirBlockId unwind = function.UnwindExit ?? unreachable;
        var handleValue = new MirCopy(new(handle), pointer);
        var stateValue = new MirCopy(new(state), BuiltinTypes.Byte);
        var states = ImmutableArray.CreateBuilder<MirCoroutineState>();

        void Suspend(MirBlockId block, ImmutableArray<MirStatement> statements, MirBlockId resume,
            bool terminal, MirSourceInfo at, int? replace = null)
        {
            var dispatch = NewBlock();
            var lowered = new MirBasicBlock(block, statements,
                new MirIntrinsicCall(MirIntrinsicKind.CoroutineSuspend,
                    [new MirConstant(terminal, BuiltinTypes.Bool)], BuiltinTypes.Byte, new(state), dispatch, unwind, at));
            if (replace is { } index) blocks[index] = lowered;
            else blocks.Add(lowered);
            blocks.Add(new(dispatch, [], new MirSwitch(stateValue,
                [new(new((byte)0, BuiltinTypes.Byte), resume), new(new((byte)1, BuiltinTypes.Byte), free)], end, at)));
        }

        for (int index = 0; index < function.Blocks.Length; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var block = function.Blocks[index];
            if (block.Terminator is MirIntrinsicCall { Intrinsic: MirIntrinsicKind.AllocateStackArray } allocation &&
                retainedAllocations.Contains(block.Id))
            {
                if (allocation.FixedArrayLength is null)
                    throw new InvalidOperationException("A retained stack allocation requires a fixed frame size.");
                blocks[index] = block with { Terminator = allocation with { RetainedInFrame = true } };
            }
            if (block.Terminator is MirSuspend suspend)
            {
                if (suspend.Payload is not null)
                    throw new NotSupportedException("Coroutine payload delivery requires a defined suspension protocol.");
                if (frame.Suspensions.TryGetValue(block.Id, out var retained))
                    states.Add(new(states.Count + 1, block.Id, suspend.Resume, retained.Live, retained.Allocations));
                Suspend(block.Id, block.Statements, suspend.Resume, false, suspend.Source, index);
            }
            else if (block.Terminator is MirReturn)
                blocks[index] = block with { Terminator = new MirGoto(complete, block.Terminator.Source) };
        }
        Suspend(complete, [], unreachable, true, source);
        blocks.Add(new(entry, [], new MirIntrinsicCall(MirIntrinsicKind.CoroutineCreate, [], pointer,
            new(handle), function.Entry, unwind, source)));
        blocks.Add(new(free, [], new MirIntrinsicCall(MirIntrinsicKind.CoroutineFree, [handleValue],
            BuiltinTypes.Void, null, end, unwind, source)));
        blocks.Add(new(end, [], new MirIntrinsicCall(MirIntrinsicKind.CoroutineEnd, [handleValue],
            BuiltinTypes.Void, null, exit, unwind, source)));
        blocks.Add(new(exit, [], new MirReturn(handleValue, source)));
        blocks.Add(new(unreachable, [], new MirUnreachable(source)));
        var result = function with
        {
            Locals = locals.ToImmutable(), Blocks = blocks.ToImmutable(), Entry = entry, ReturnType = pointer,
            Coroutine = new(handle, states.ToImmutable(), frame.LiveAcross, frame.CreateFrameType(function, types)),
        };
        MirVerifier.VerifyOrThrow(result);
        return result;
    }
}