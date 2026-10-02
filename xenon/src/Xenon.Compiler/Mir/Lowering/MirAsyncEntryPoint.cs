using System.Collections.Immutable;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

/// <summary>The host pump protocol, result checks and operation cleanup precede LLVM emission.</summary>
internal static class MirAsyncEntryPoint
{
    public static MirFunction Create(MirFunction entry, TypeFactory types)
    {
        var symbol = new FunctionSymbol(entry.Symbol);
        var awaiting = AsyncEntryPoint.GetAwaitOperator(entry.Symbol)!;
        var source = entry.Source;
        var locals = ImmutableArray.CreateBuilder<MirLocal>();
        var blocks = new List<MirBasicBlock>();
        var statements = new List<MirStatement>();
        var abort = new MirBlockId(0);
        blocks.Add(new(abort, [], new MirAbort(source)));
        MirPlace Local(string name, TypeSymbol type)
        {
            var place = new MirPlace(new(locals.Count));
            locals.Add(new(place.Local, name, type, MirLocalKind.Temporary, source));
            return place;
        }
        TypeSymbol Type(MirPlace place) => locals[place.Local.Value].Type;
        MirCopy Value(MirPlace place) => new(place, Type(place));
        void End(MirTerminator terminator)
        {
            blocks.Add(new(new(blocks.Count), [.. statements], terminator));
            statements.Clear();
        }
        void Primitive(MirIntrinsicKind kind, ImmutableArray<MirOperand> arguments, MirPlace? target = null) =>
            End(new MirIntrinsicCall(kind, arguments, target is null ? BuiltinTypes.Void : Type(target),
                target, new(blocks.Count + 1), abort, source));
        void Call(FunctionSymbol function, ImmutableArray<MirOperand> arguments, MirPlace destination) =>
            End(new MirCall(new MirFunctionOperand(function,
                types.FunctionPointer(function.ReturnType, function.Parameters.Select(parameter => parameter.Type))),
                arguments, destination, new(blocks.Count + 1), abort, source));
        MirPlace Borrow(MirPlace place, TypeSymbol type)
        {
            var address = Local(locals[place.Local.Value].Name + ".address", type);
            statements.Add(new MirAssign(address, new MirBorrow(place, type is ReferenceTypeSymbol reference
                ? reference.IsReadonly ? MirBorrowKind.Shared : MirBorrowKind.Exclusive : MirBorrowKind.Raw, type), source));
            return address;
        }
        var root = Local("async.root", types.PointerTo(BuiltinTypes.Byte));
        var operation = Local("async.operation", entry.Symbol.ReturnType);
        var result = Local("async.exit.code", BuiltinTypes.Int);
        var complete = Local("async.complete", BuiltinTypes.Bool);
        var continuation = Local("async.continuation", awaiting.Parameters[^1].Type);
        Primitive(MirIntrinsicKind.AsyncRootCreate, [], root);
        Call(entry.Symbol, [], operation);
        MirPlace? slot = null, slotAddress = null, slotReference = null;
        if (awaiting.Parameters.Length == 3)
        {
            var reference = (ReferenceTypeSymbol)awaiting.Parameters[1].Type;
            slot = Local("async.result.storage", reference.ElementType);
            statements.Add(new MirAssign(slot, new MirDefault(reference.ElementType), source));
            slotAddress = Borrow(slot, types.PointerTo(reference.ElementType));
            slotReference = Borrow(slot, reference);
        }
        var retry = new MirBlockId(blocks.Count + 1);
        End(new MirGoto(retry, source));
        Primitive(MirIntrinsicKind.AsyncRootContinuation, [Value(root)], continuation);
        MirPlace operand;
        if (awaiting.Parameters[0].Type is ReferenceTypeSymbol referenceOperand)
            operand = Borrow(operation, referenceOperand);
        else
        {
            operand = Local("async.operand", entry.Symbol.ReturnType);
            Primitive(MirIntrinsicKind.CloneValue, [Value(operation)], operand);
        }
        Call(awaiting, slotReference is null ? [Value(operand), Value(continuation)] :
            [Value(operand), Value(slotReference), Value(continuation)], complete);
        int branchIndex = blocks.Count;
        End(new MirUnreachable(source));
        var pending = new MirBlockId(blocks.Count);
        if (slotAddress is not null) Primitive(MirIntrinsicKind.CheckStorageEmpty, [Value(slotAddress)]);
        Primitive(MirIntrinsicKind.AsyncRootPump, [Value(root)]);
        End(new MirGoto(retry, source));
        var ready = new MirBlockId(blocks.Count);
        if (slotAddress is not null) Primitive(MirIntrinsicKind.CheckStorageInitialized, [Value(slotAddress)]);
        statements.Add(new MirAssign(result, new MirUse(slot is null ? new MirConstant(0, BuiltinTypes.Int) :
            new MirCopy(slot.Project(new MirLifetimeProjection()), BuiltinTypes.Int)), source));
        if (TypeFacts.GetCompleteDestructor(entry.Symbol.ReturnType) is { } destructor)
            End(new MirDrop(operation, destructor, new(blocks.Count + 1), abort, source));
        Primitive(MirIntrinsicKind.AsyncRootClose, [Value(root)]);
        End(new MirReturn(Value(result), source));
        blocks[branchIndex] = blocks[branchIndex] with
        { Terminator = new MirSwitch(Value(complete), [new(new(true, BuiltinTypes.Bool), ready)], pending, source) };
        var function = new MirFunction(symbol, locals.ToImmutable(), [.. blocks], new(1), source);
        MirVerifier.VerifyOrThrow(function);
        return function;
    }
}