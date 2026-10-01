using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir;

/// <summary>Native symbol references in executable MIR, including initialization bodies.</summary>
public static class MirSymbols
{
    public static IEnumerable<Symbol> Referenced(MirFunction function)
    {
        if (function.Resumable is { } resumable)
            foreach (var symbol in Referenced(resumable.Initialization)) yield return symbol;
        foreach (var block in function.Blocks)
        {
            foreach (var assign in block.Statements.OfType<MirAssign>())
            {
                if (assign.Value is MirStaticFieldAddress field) yield return field.Field;
                foreach (var operand in MirOperands.Of(assign.Value))
                    if (operand is MirFunctionOperand target) yield return target.Function;
            }
            foreach (var operand in MirOperands.Of(block.Terminator))
                if (operand is MirFunctionOperand target) yield return target.Function;
            if (block.Terminator is MirDrop { Destructor: { } destructor }) yield return destructor;
            if (block.Terminator is MirIntrinsicCall intrinsic)
            {
                if (intrinsic.Function is { } target) yield return target;
                if (intrinsic.Field is { } field) yield return field;
            }
            if (block.Terminator is MirThrow { Exception: { } exception } &&
                TypeFacts.GetCompleteDestructor(exception.Type) is { } exceptionDestructor) yield return exceptionDestructor;
        }
    }
}