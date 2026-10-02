namespace Xenon.Compiler.Mir.Analysis;

/// <summary>Shared shallow operand traversal; no executable source tree is involved.</summary>
public static class MirOperands
{
    public static IEnumerable<MirOperand> Of(MirRValue value) => value switch
    {
        MirUse use => [use.Operand],
        MirUnary unary => [unary.Operand],
        MirBinary binary => [binary.Left, binary.Right],
        MirCast cast => [cast.Operand],
        MirAggregate aggregate => aggregate.Fields,
        MirAtomicValue atomic => [atomic.Value],
        MirInterfaceView view => [view.Address],
        MirExceptionMatches matches => [matches.Record],
        MirExceptionReference reference => [reference.Record],
        MirBorrow borrow => Indices(borrow.Place),
        MirStorageState storage => Indices(storage.Place),
        MirDefault or MirTypeLayout or MirStaticFieldAddress or MirCurrentException or
            MirStackSave or MirStackAllocation => [],
        _ => throw new InvalidOperationException($"Unknown MIR rvalue {value.GetType().Name}."),
    };

    public static IEnumerable<MirOperand> Of(MirTerminator terminator) => terminator switch
    {
        MirCall call => new[] { call.Callee }.Concat(call.Arguments)
            .Concat(call.Receiver is { } receiver ? [receiver] : []),
        MirIntrinsicCall intrinsic => intrinsic.Arguments,
        MirSwitch branch => [branch.Value],
        MirReturn { Value: { } value } => [value],
        MirThrow { Exception: { } exception } => [exception],
        MirSuspend { Payload: { } payload } => [payload],
        MirGoto or MirDrop or MirReturn or MirThrow or MirSuspend or MirUnreachable or
            MirResumeUnwind or MirAbort => [],
        _ => throw new InvalidOperationException($"Unknown MIR terminator {terminator.GetType().Name}."),
    };

    public static IEnumerable<MirOperand> Indices(MirPlace place) =>
        place.Projections.SelectMany(projection => projection switch
        {
            MirIndexProjection index => index.Indices,
            MirLinearIndexProjection index => [index.Index],
            _ => Enumerable.Empty<MirOperand>(),
        });

    public static IEnumerable<MirPlace> Places(MirOperand operand)
    {
        MirPlace? place = operand switch { MirCopy copy => copy.Place, MirMove move => move.Place, _ => null };
        if (place is null) yield break;
        yield return place;
        foreach (MirPlace index in Indices(place).SelectMany(Places)) yield return index;
    }
}
