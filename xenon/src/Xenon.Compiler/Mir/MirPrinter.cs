using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Xenon.Compiler.Mir;

/// <summary>Deterministic, culture-independent text for debugging and snapshots.</summary>
public static class MirPrinter
{
    public static string Dump(MirFunction function, bool includeSource = false)
    {
        var output = new StringBuilder();
        output.Append("fn ").Append(function.Symbol.FullName).Append(" -> ").Append(function.ReturnType).Append(" {\n");
        output.Append("  entry ").Append(function.Entry).Append('\n');
        if (function.Coroutine is { } coroutine)
        {
            output.Append("  coroutine handle ").Append(coroutine.Handle).Append(" frame [")
                .AppendJoin(", ", coroutine.FrameLocals.OrderBy(local => local.Value)).Append("]\n");
            foreach (var state in coroutine.States.OrderBy(state => state.State))
                output.Append("  state ").Append(state.State.ToString(CultureInfo.InvariantCulture))
                    .Append(" suspend ").Append(state.Suspension).Append(" resume ").Append(state.Resume)
                    .Append(" live [").AppendJoin(", ", state.Live.OrderBy(local => local.Value)).Append("]\n");
        }
        if (includeSource)
            foreach (MirScope scope in function.Scopes.OrderBy(scope => scope.Id))
                output.Append("  scope ").Append(scope.Id.ToString(CultureInfo.InvariantCulture)).Append(" parent ")
                    .Append(scope.Parent?.ToString(CultureInfo.InvariantCulture) ?? "root").Append('\n');
        foreach (MirLocal local in function.Locals.OrderBy(local => local.Id.Value))
            output.Append("  local ").Append(local.Id).Append(": ").Append(local.Type).Append(" [")
                .Append(local.Kind.ToString().ToLowerInvariant()).Append("] ").Append(Quote(local.Name)).Append('\n');
        foreach (MirBasicBlock block in function.Blocks.OrderBy(block => block.Id.Value))
        {
            output.Append('\n').Append(block.Id).Append(":\n");
            foreach (MirStatement statement in block.Statements)
                Line(Statement(statement), statement.Source);
            Line(Terminator(block.Terminator), block.Terminator.Source);
        }
        return output.Append("}\n").ToString();

        void Line(string text, MirSourceInfo source)
        {
            output.Append("  ").Append(text);
            if (includeSource)
                output.Append(" // ").Append(Quote(source.Location.Path)).Append('@')
                    .Append(source.Location.Span.Start.ToString(CultureInfo.InvariantCulture)).Append('+')
                    .Append(source.Location.Span.Length.ToString(CultureInfo.InvariantCulture))
                    .Append(" scope ").Append(source.Scope.ToString(CultureInfo.InvariantCulture));
            output.Append('\n');
        }
    }

    public static string Place(MirPlace place)
    {
        string result = place.Local.ToString();
        foreach (MirProjection projection in place.Projections)
            result = projection switch
            {
                MirFieldProjection field => $"{result}.{field.Field.Name}",
                MirAtomicStorageProjection => $"{result}.atomic_value",
                MirOwnerStorageProjection => $"{result}.owner_storage",
                MirLifetimeProjection => $"{result}.value",
                MirBaseProjection parent => $"{result}.base<{parent.BaseType}>",
                MirLinearIndexProjection linear => $"{result}.element[{Operand(linear.Index)}]",
                MirDerefProjection => $"(*{result})",
                MirIndexProjection index => $"{result}[{string.Join(", ", index.Indices.Select(Operand))}]",
                _ => throw new NotSupportedException($"Unknown MIR projection {projection.GetType().Name}."),
            };
        return result;
    }

    public static string Operand(MirOperand operand) => operand switch
    {
        MirConstant constant => $"const {Constant(constant.Value)}: {constant.Type}",
        MirCopy copy => $"copy {Place(copy.Place)}",
        MirMove move => $"move {Place(move.Place)}{(move.OwnershipPlace is null ? "" : " owner " + Place(move.OwnershipPlace))}",
        MirDeferredConstant deferred => $"const deferred<{deferred.Type}>",
        MirFunctionOperand function => $"fn {function.Function.FullName}",
        MirRequirementOperand requirement => $"requirement {requirement.Operation} {requirement.Requirement.Name}<{string.Join(", ", requirement.TypeArguments)}>",
        _ => throw new NotSupportedException($"Unknown MIR operand {operand.GetType().Name}."),
    };

    // Encode the primitive directly: reflection-based serialization is unavailable in NativeAOT.
    private static string Quote(string? value) =>
        value is null ? "null" : $"\"{JsonEncodedText.Encode(value)}\"";

    private static string Constant(object? value) => value switch
    {
        null => "null",
        string text => Quote(text),
        char character => Quote(character.ToString()),
        bool boolean => boolean ? "true" : "false",
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException($"Unknown MIR constant {value.GetType().Name}."),
    };

    private static string RValue(MirRValue value) => value switch
    {
        MirAtomicValue atomic => $"atomic<{atomic.AtomicType}>({Operand(atomic.Value)})",
        MirStackAllocation allocation => $"stack.alloc<{allocation.PointerType.ElementType}>",
        MirStackSave => "stack.save",
        MirStorageState state => $"storage.initialized {Place(state.Place)}",
        MirUse use => Operand(use.Operand),
        MirUnary unary => $"{unary.Operator.ToString().ToLowerInvariant()}({Operand(unary.Operand)})",
        MirBinary binary => $"{binary.Operator.ToString().ToLowerInvariant()}({Operand(binary.Left)}, {Operand(binary.Right)})",
        MirCast cast => $"cast<{cast.Type}>({Operand(cast.Operand)})",
        MirBorrow borrow => $"borrow.{borrow.Kind.ToString().ToLowerInvariant()} {Place(borrow.Place)}",
        MirAggregate aggregate => $"aggregate<{aggregate.Type}>({string.Join(", ", aggregate.Fields.Select(Operand))})",
        MirDefault zero => $"default<{zero.Type}>",
        MirTypeLayout layout => $"layout.{layout.Query.ToString().ToLowerInvariant()}<{layout.SubjectType}>{(layout.Field is null ? "" : "." + layout.Field.Name)}",
        MirInterfaceView view => $"interface<{view.InterfaceType}>({Operand(view.Address)})",
        MirStaticFieldAddress address => $"address {address.Field.ContainingType.FullName}.{address.Field.Name}",
        MirCurrentException => "exception.current",
        MirExceptionMatches match => $"exception.matches<{match.ExceptionType}>({Operand(match.Record)})",
        MirExceptionReference reference => $"exception.reference<{reference.Type}>({Operand(reference.Record)})",
        _ => throw new NotSupportedException($"Unknown MIR rvalue {value.GetType().Name}."),
    };

    public static string Statement(MirStatement statement) => statement switch
    {
        MirAssign assign => $"{Place(assign.Destination)} = {RValue(assign.Value)}" +
            (assign.TransferDestination is { } transfer ? $" [transfer to {Place(transfer)}]" : "") +
            (assign.IsAggregateInitialization ? " [aggregate.init]" : "") +
            (assign.IsArgumentReservationCheck ? " [argument.reservation.check]" : "") +
            (assign.ReservedMove is { } reserved ? $" [reserve {Place(reserved)}]" : "") +
            (assign.WriteKind == MirWriteKind.Initialize && assign.PreviousValueState == MirPreviousValueState.Live &&
                assign.ConstructorField is null && !assign.RequiresRuntimeInitializationCheck ? "" :
                $" [write {assign.WriteKind.ToString().ToLowerInvariant()}, previous {assign.PreviousValueState.ToString().ToLowerInvariant()}" +
                $"{(assign.ConstructorField is null ? "" : ", field " + assign.ConstructorField.Name)}" +
                $"{(assign.RequiresRuntimeInitializationCheck ? ", checked" : "")}]"),
        MirStackRestore restore => $"stack.restore {Operand(restore.Token)}",
        MirSetStorageState state => $"storage.state {Place(state.Place)} = {state.Initialized.ToString().ToLowerInvariant()}",
        MirCompleteOperation complete => $"operation.complete {Operand(complete.Operation)}" +
            (complete.Result is { } result ? $" -> {Place(result)}" : ""),
        MirForget forget => $"forget {Place(forget.Place)}",
        MirStorageLive live => $"storage.live {live.Local}",
        MirStorageDead dead => $"storage.dead {dead.Local}",
        MirInitializeDispatch dispatch => $"dispatch.init<{dispatch.Type}> {Place(dispatch.Place)}",
        MirReleaseException release => $"exception.{(release.Abandon ? "abandon" : "handle")} {Operand(release.Record)}" +
            (release.RestoredRecord is { } restored ? $" restore {Operand(restored)}" : ""),
        _ => throw new NotSupportedException($"Unknown MIR statement {statement.GetType().Name}."),
    };

    public static string Terminator(MirTerminator terminator) => terminator switch
    {
        MirGoto go => $"goto {go.Target}",
        MirSwitch selection => $"switch {Operand(selection.Value)} [" +
            string.Join(", ", selection.Cases.Select(item => $"{Operand(item.Value)} -> {item.Target}").Append($"otherwise -> {selection.Otherwise}")) + "]",
        MirIntrinsicCall call => $"{(call.Destination is null ? "" : Place(call.Destination) + " = ")}intrinsic {call.Intrinsic}" +
                        $"({string.Join(", ", call.Arguments.Select(Operand))})" +
            $"{(call.StorageCheck == MirStorageCheckPurpose.None ? "" : " check " + call.StorageCheck)}" +
            $"{(call.Function is null ? "" : " fn " + call.Function.FullName)}{(call.SubjectType is null ? "" : " type " + call.SubjectType)}" +
            $"{(call.Field is null ? "" : " field " + call.Field.Name)}{(call.FixedArrayLength is null ? "" : " length " + call.FixedArrayLength.Value.ToString(CultureInfo.InvariantCulture))}{(call.Operator is null ? "" : " op " + call.Operator)}{(call.ReturnsOldValue ? " old" : "")}{(call.RetainedInFrame ? " frame" : "")} -> {call.Normal} unwind {call.Unwind}",
        MirCall call => $"{(call.Destination is null ? "" : Place(call.Destination) + " = ")}call {Operand(call.Callee)}" +
            $"({string.Join(", ", call.Arguments.Select(Operand))}){(call.Receiver is null ? "" : " receiver " + Operand(call.Receiver))}{(call.IsIndirectReceiver ? " indirect-receiver" : "")}{(call.IsVirtual ? " virtual" : "")}{(call.InterfaceType is null ? "" : " interface " + call.InterfaceType)} -> {call.Normal} unwind {call.Unwind}",
        MirReturn ret => ret.Value is null ? "return" : $"return {Operand(ret.Value)}",
        MirThrow throwing => $"throw {(throwing.Exception is null ? "current" : Operand(throwing.Exception))} unwind {throwing.Unwind}",
        MirResumeUnwind => "resume.unwind",
        MirSuspend suspend => $"suspend{(suspend.Payload is null ? "" : " " + Operand(suspend.Payload))} resume {suspend.Resume}",
        MirAbort => "abort",
        MirUnreachable => "unreachable",
        MirDrop drop => $"drop{(drop.IsExplicit ? " explicit" : "")}{(drop.IsVirtual ? " virtual" : "")} {Place(drop.Place)}{(drop.Destructor is null ? "" : " via " + drop.Destructor.FullName)} -> {drop.Normal} unwind {drop.Unwind}",
        _ => throw new NotSupportedException($"Unknown MIR terminator {terminator.GetType().Name}."),
    };
}
