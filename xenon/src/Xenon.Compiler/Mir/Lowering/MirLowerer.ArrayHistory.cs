using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private void PrepareArrayHistory(TemporaryGuard guard, ArrayTypeSymbol array, MirSourceInfo source)
    {
        var node = new StructTypeSymbol(_bound.Symbol.Name + ".cleanup." + _locals.Count,
            _bound.Symbol.ContainingNamespace, false, SymbolOrigin.CompilerGenerated, accessibility: Accessibility.Private);
        PointerTypeSymbol pointer = _types.PointerTo(node);
        node.SetFields(new[] { ("previous", (TypeSymbol)pointer), ("array", array), ("count", BuiltinTypes.Int), ("order", BuiltinTypes.Long) }
            .Select((field, ordinal) => new FieldSymbol(field.Item1, node, field.Item2, ordinal, Accessibility.Private, false, false, false, false, null))
            .ToImmutableArray());
        guard.HistoryType = node;
        guard.History = Temporary(pointer, source);
        _current.Statements.Add(new MirAssign(guard.History, new MirUse(new MirConstant(null, pointer)), source));
    }

    // One source allocation can execute repeatedly before its lexical scope ends
    // (for example in a while condition). Preserve each prior backing allocation.
    private void PushArrayHistory(TemporaryGuard guard, MirSourceInfo source)
    {
        if (guard.History is null) return;
        StructTypeSymbol type = guard.HistoryType!;
        PointerTypeSymbol pointer = _types.PointerTo(type);
        Block push = NewBlock(), after = NewBlock();
        End(new MirSwitch(new MirCopy(guard.Active, BuiltinTypes.Bool), [new(new(true, BuiltinTypes.Bool), push.Id)], after.Id, source));
        _current = push;
        MirOperand node = Save(new MirStackAllocation(pointer), source);
        MirPlace fields = ((MirCopy)node).Place.Project(new MirDerefProjection());
        MirOperand[] values = [new MirCopy(guard.History, pointer), new MirCopy(guard.Place, type.Fields[1].Type),
            new MirCopy(guard.Count!, BuiltinTypes.Int), new MirCopy(guard.Order!, BuiltinTypes.Long)];
        for (int i = 0; i < values.Length; i++)
            _current.Statements.Add(new MirAssign(fields.Project(new MirFieldProjection(type.Fields[i])), new MirUse(values[i]), source));
        _current.Statements.Add(new MirAssign(guard.History, new MirUse(node), source));
        _current.Statements.Add(new MirAssign(guard.Count!, new MirUse(new MirConstant(0, BuiltinTypes.Int)), source));
        Jump(after);
        _current = after;
    }

    private Block PopArrayHistory(TemporaryGuard guard, Block next, MirSourceInfo source)
    {
        Block pop = NewBlock();
        if (guard.History is null)
        {
            pop.Statements.Add(new MirAssign(guard.Active, new MirUse(new MirConstant(false, BuiltinTypes.Bool)), source));
            pop.Terminator = new MirGoto(next.Id, source);
            return pop;
        }
        StructTypeSymbol type = guard.HistoryType!;
        PointerTypeSymbol pointer = _types.PointerTo(type);
        MirPlace empty = Temporary(BuiltinTypes.Bool, source), savedHead = Temporary(pointer, source);
        Block restore = NewBlock(), done = NewBlock();
        pop.Statements.Add(new MirAssign(savedHead, new MirUse(new MirCopy(guard.History, pointer)), source));
        pop.Statements.Add(new MirAssign(empty, new MirBinary(MirBinaryOperator.Equal, new MirCopy(savedHead, pointer),
            new MirConstant(null, pointer), BuiltinTypes.Bool), source));
        pop.Terminator = new MirSwitch(new MirCopy(empty, BuiltinTypes.Bool), [new(new(true, BuiltinTypes.Bool), done.Id)], restore.Id, source);
        done.Statements.Add(new MirAssign(guard.Active, new MirUse(new MirConstant(false, BuiltinTypes.Bool)), source));
        done.Terminator = new MirGoto(next.Id, source);
        MirPlace fields = savedHead.Project(new MirDerefProjection());
        MirPlace[] targets = [guard.History, guard.Place, guard.Count!, guard.Order!];
        for (int i = 0; i < targets.Length; i++)
            restore.Statements.Add(new MirAssign(targets[i], new MirUse(new MirCopy(
                fields.Project(new MirFieldProjection(type.Fields[i])), type.Fields[i].Type)), source));
        restore.Statements.Add(new MirAssign(guard.Active, new MirUse(new MirConstant(true, BuiltinTypes.Bool)), source));
        restore.Terminator = new MirGoto(next.Id, source);
        return pop;
    }
}
