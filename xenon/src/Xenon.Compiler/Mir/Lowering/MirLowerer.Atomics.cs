using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private MirOperand AtomicAssignment(BoundAssignmentExpression expression, MirPlace target, AtomicTypeSymbol atomic, MirSourceInfo source)
    {
        MirOperand address = Address(target, atomic, source), value = Value(expression.Expression);
        MirPlace? state = expression.ConstructorField is { } field ? _constructionStates.GetValueOrDefault(field) : null;
        MirOperand result;
        if (expression.RequiresRuntimeInitializationCheck && state is not null)
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
        else result = Write(expression.IsInitialization ? MirIntrinsicKind.AtomicInitialize :
            expression.OperatorKind == SyntaxKind.EqualsToken ? MirIntrinsicKind.AtomicStore : MirIntrinsicKind.AtomicUpdate);
        if (state is not null) SetFlag(state, true, source);
        Activate(target, true, source);
        return result;

        MirOperand Write(MirIntrinsicKind kind) => Intrinsic(kind, [address, value], atomic.ElementType, source,
            op: kind == MirIntrinsicKind.AtomicUpdate ? BinaryOperator(expression.OperatorKind) : null)!;
    }
}
