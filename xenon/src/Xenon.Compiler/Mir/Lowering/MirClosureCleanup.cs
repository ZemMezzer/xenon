using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

/// <summary>Closure environment destruction is ordinary, explicit MIR control flow.</summary>
internal static class MirClosureCleanup
{
    public static MirFunction Create(MirFunction closure, TypeFactory types)
    {
        var pointer = types.PointerTo(BuiltinTypes.Byte);
        var symbol = new FunctionSymbol(closure.Symbol, pointer);
        var source = closure.Source;
        var locals = ImmutableArray.CreateBuilder<MirLocal>();
        locals.Add(new(new(0), "environment", pointer, MirLocalKind.Parameter, source)
        { Variable = symbol.Parameters[0] });
        var environment = new MirCopy(new(new(0)), pointer);
        // Borrowed captures carry no destruction responsibility.
        var captures = closure.Symbol.LambdaCaptures
            .Where(capture => !capture.IsBorrow && TypeFacts.GetCompleteDestructor(capture.Type) is not null)
            .ToArray();
        foreach (var capture in captures)
            locals.Add(new(new(locals.Count), capture.Name, capture.Type, MirLocalKind.Capture, source)
            { Variable = capture });
        var blocks = ImmutableArray.CreateBuilder<MirBasicBlock>();
        MirBlockId Add(MirTerminator terminator)
        {
            var id = new MirBlockId(blocks.Count);
            blocks.Add(new(id, [], terminator));
            return id;
        }
        var returned = Add(new MirReturn(null, source));
        var rethrow = Add(new MirResumeUnwind(source));
        var abort = Add(new MirAbort(source));
        MirBlockId Free(MirBlockId next) => Add(new MirIntrinsicCall(MirIntrinsicKind.Free,
            [environment], BuiltinTypes.Void, null, next, abort, source));
        var normal = Free(returned);
        var exceptional = Free(rethrow);
        // Build prefixes in construction order; entry executes the last capture first.
        // Once unwinding, a second throwing destructor terminates the process.
        for (int index = 0; index < captures.Length; index++)
        {
            var place = new MirPlace(new(index + 1));
            var destructor = TypeFacts.GetCompleteDestructor(captures[index].Type)!;
            normal = Add(new MirDrop(place, destructor, normal, exceptional, source));
            exceptional = Add(new MirDrop(place, destructor, exceptional, abort, source));
        }
        var result = new MirFunction(symbol, locals.ToImmutable(), blocks.ToImmutable(), normal, source)
        { UnwindExit = rethrow };
        MirVerifier.VerifyOrThrow(result);
        return result;
    }
}