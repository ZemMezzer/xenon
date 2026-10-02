using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private void DestroySharedOrWeak(BoundOwnershipDestructionExpression destruction, MirSourceInfo source)
    {
        OwnershipTypeSymbol type = destruction.OwnershipType;
        MirPlace storage = ((MirCopy)ThisAddress(type)).Place.Project(new MirDerefProjection());
        MirOperand control = Save(new MirUse(new MirCopy(storage, type)), source);
        if (type is WeakTypeSymbol)
        {
            _ = Intrinsic(MirIntrinsicKind.ReleaseWeak, [control], BuiltinTypes.Void, source);
            return;
        }
        var shared = (SharedTypeSymbol)type;
        MirOperand last = Intrinsic(MirIntrinsicKind.ReleaseStrong, [control], BuiltinTypes.Bool, source)!;
        Block destroy = NewBlock(), done = NewBlock(), outer = _unwindTarget, failed = NewBlock();
        End(new MirSwitch(last, [new(new(true, BuiltinTypes.Bool), destroy.Id)], done.Id, source));
        _current = destroy;
        MirOperand payload = Intrinsic(MirIntrinsicKind.SharedPayload, [control], shared.StorageType, source)!;
        _unwindTarget = failed;
        DeleteValue(payload, destruction.ElementDestructor, source);
        _unwindTarget = outer;
        _ = Intrinsic(MirIntrinsicKind.ReleaseWeak, [control], BuiltinTypes.Void, source);
        Jump(done);
        _current = failed;
        _ = Intrinsic(MirIntrinsicKind.ReleaseWeak, [control], BuiltinTypes.Void, source);
        Jump(outer);
        _current = done;
    }

    private void DestroyCallableControl(MirOperand control, MirSourceInfo source)
    {
        MirOperand last = Intrinsic(MirIntrinsicKind.ReleaseCallableCount, [control], BuiltinTypes.Bool, source)!;
        Block destroy = NewBlock(), done = NewBlock(), outer = _unwindTarget, failed = NewBlock();
        End(new MirSwitch(last, [new(new(true, BuiltinTypes.Bool), destroy.Id)], done.Id, source));
        _current = destroy;
        PointerTypeSymbol pointer = _types.PointerTo(BuiltinTypes.Byte);
        MirOperand environment = Intrinsic(MirIntrinsicKind.CallableEnvironment, [control], pointer, source)!;
        MirOperand destructor = Intrinsic(MirIntrinsicKind.CallableDestructor, [control],
            _types.FunctionPointer(BuiltinTypes.Void, [pointer]), source)!;
        _unwindTarget = failed;
        _ = CallValues(destructor, [environment], source);
        _unwindTarget = outer;
        _ = Intrinsic(MirIntrinsicKind.Free, [control], BuiltinTypes.Void, source);
        Jump(done);
        _current = failed;
        _ = Intrinsic(MirIntrinsicKind.Free, [control], BuiltinTypes.Void, source);
        Jump(outer);
        _current = done;
    }
}
