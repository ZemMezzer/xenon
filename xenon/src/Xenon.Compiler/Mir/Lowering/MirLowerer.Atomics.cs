using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private MirOperand AtomicAssignment(BoundAssignmentExpression expression, MirPlace target, AtomicTypeSymbol atomic, MirSourceInfo source)
    {
        MirOperand address = Address(target, atomic, source), value = Value(expression.Expression);
        MirPlace? state = expression.Target is BoundMemberAccessExpression member && IsThisRooted(member)
            ? _constructionStates.GetValueOrDefault(member.Field) : null;
        RecordStore(expression, target);
        AssignmentState ownership = Ownership(expression);
        MirOperand result;
        if (ownership.RuntimeCheck && state is not null)
        {
            MirPlace merged = Temporary(atomic.ElementType, source);
            Block initialized = NewBlock(), empty = NewBlock(), after = NewBlock();
            End(new MirSwitch(new MirCopy(state, BuiltinTypes.Bool), [new(new(true, BuiltinTypes.Bool), initialized.Id)], empty.Id, source));
            _current = initialized;
            MirOperand replacement = Write(MirIntrinsicKind.AtomicStore);
            _current.Statements.Add(new MirAssign(merged, new MirUse(replacement), source));
            Jump(after);
            _current = empty;
            MirOperand initial = Write(MirIntrinsicKind.AtomicInitialize);
            _current.Statements.Add(new MirAssign(merged, new MirUse(initial), source));
            Jump(after);
            _current = after;
            result = new MirCopy(merged, atomic.ElementType);
        }
        else result = Write(ownership.Initialize ? MirIntrinsicKind.AtomicInitialize :
            expression.OperatorKind == SyntaxKind.EqualsToken ? MirIntrinsicKind.AtomicStore : MirIntrinsicKind.AtomicUpdate);
        if (state is not null) SetFlag(state, true, source);
        Activate(target, true, source);
        return result;

        MirOperand Write(MirIntrinsicKind kind)
        {
            if (kind == MirIntrinsicKind.AtomicStore && TypeFacts.GetCompleteDestructor(atomic.ElementType) is { } destructor)
            {
                MirOperand previous = Intrinsic(MirIntrinsicKind.AtomicExchange, [address, value], atomic.ElementType, source)!;
                Block afterDrop = NewBlock();
                End(new MirDrop(((MirCopy)previous).Place, destructor, afterDrop.Id,
                    GuardedUnwind(_unwindTarget, source).Id, source));
                _current = afterDrop;
                return value;
            }
            return Intrinsic(kind, [address, value], atomic.ElementType, source,
                op: kind == MirIntrinsicKind.AtomicUpdate ? BinaryOperator(expression.OperatorKind) : null)!;
        }
    }
    private MirOperand CompareExchange(BoundCompareExchangeExpression expression, MirSourceInfo source)
    {
        MirOperand address = Address(Place(expression.Target), expression.Target.Type, source);
        TypeSymbol element = expression.Expected.Type;
        MirOperand expectedValue = Arguments([expression.Expected])[0];
        TemporaryGuard? expected = GuardValue(expectedValue, source);
        MirOperand desiredValue = Arguments([expression.Desired])[0];
        if (expected is null)
            return Intrinsic(MirIntrinsicKind.CompareExchange, [address, expectedValue, desiredValue], BuiltinTypes.Bool, source)!;
        TemporaryGuard desired = GuardValue(desiredValue, source)!;
        MirOperand[] values = [expectedValue, desiredValue];
        var resultType = new StructTypeSymbol(_bound.Symbol.Name + ".exchange." + _locals.Count,
            _bound.Symbol.ContainingNamespace, false, SymbolOrigin.CompilerGenerated, accessibility: Accessibility.Private);
        resultType.SetFields([
            new FieldSymbol("succeeded", resultType, BuiltinTypes.Bool, 0, Accessibility.Private, false, false, false, false, null),
            new FieldSymbol("discarded", resultType, element, 1, Accessibility.Private, false, false, false, false, null)]);
        MirOperand exchanged = Intrinsic(MirIntrinsicKind.CompareExchangeOwned, [address, .. values], resultType, source)!;
        EndValueGuard(desired, source);
        MirPlace result = ((MirCopy)exchanged).Place;
        TemporaryGuard discarded = GuardValue(new MirCopy(result.Project(new MirFieldProjection(resultType.Fields[1])), element), source)!;
        Block after = NewBlock();
        Jump(CleanupChain([expected, discarded], after, GuardedUnwind(_unwindTarget, source), false, source));
        _current = after;
        EndValueGuard(discarded, source);
        EndValueGuard(expected, source);
        return new MirCopy(result.Project(new MirFieldProjection(resultType.Fields[0])), BuiltinTypes.Bool);
    }
}
