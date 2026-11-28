using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics.Binding;

public sealed record BoundSwitchStatement(
    BoundExpression Expression,
    ImmutableArray<BoundSwitchSection> Sections) : BoundStatement
{
    public override BoundKind Kind => BoundKind.SwitchStatement;
}

public sealed record BoundSwitchSection(BoundExpression? Value, BoundBlockStatement Body);

public sealed record BoundTryStatement(
    BoundBlockStatement Body,
    ImmutableArray<BoundCatchClause> Catches,
    BoundBlockStatement? FinallyBody) : BoundStatement
{
    public override BoundKind Kind => BoundKind.TryStatement;
}

public sealed record BoundCatchClause(
    TypeSymbol? Type,
    LocalVariableSymbol? Variable,
    BoundBlockStatement Body)
{
    public bool IsCatchAll => Type is null;
}

public sealed record BoundThrowStatement(BoundExpression? Expression) : BoundStatement
{
    public bool IsRethrow => Expression is null;
    public override BoundKind Kind => BoundKind.ThrowStatement;
}

public static class BoundControlFlow
{
    [Flags]
    private enum Exit { None = 0, Fallthrough = 1, Break = 2, Continue = 4 }

    public static bool AlwaysReturns(BoundStatement statement) => PossibleExits(statement) == Exit.None;

    public static bool TerminatesSection(BoundStatement statement) =>
        (PossibleExits(statement) & Exit.Fallthrough) == 0;

    // Track exits that can reach another statement. Return and throw leave the function
    // or protected region, so neither contributes a fallthrough, break or continue.
    private static Exit PossibleExits(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundReturnStatement or BoundThrowStatement: return Exit.None;
            case BoundBreakStatement: return Exit.Break;
            case BoundContinueStatement: return Exit.Continue;
            case BoundBlockStatement block:
                Exit exits = Exit.Fallthrough;
                foreach (BoundStatement child in block.Statements)
                {
                    if ((exits & Exit.Fallthrough) == 0) break;
                    exits = (exits & ~Exit.Fallthrough) | PossibleExits(child);
                }
                return exits;
            case BoundIfStatement conditional:
                return PossibleExits(conditional.ThenStatement) |
                    (conditional.ElseStatement is { } other ? PossibleExits(other) : Exit.Fallthrough);
            case BoundDoWhileStatement loop:
                return PossibleExits(loop.Body) == Exit.None ? Exit.None : Exit.Fallthrough;
            case BoundSwitchStatement selection:
                Exit sections = selection.Sections.Any(section => section.Value is null) ? Exit.None : Exit.Fallthrough;
                Exit next = Exit.Fallthrough;
                for (int i = selection.Sections.Length - 1; i >= 0; i--)
                {
                    BoundBlockStatement body = selection.Sections[i].Body;
                    if (!body.Statements.IsEmpty) next = PossibleExits(body);
                    sections |= next;
                }
                if ((sections & Exit.Break) != 0) sections = (sections & ~Exit.Break) | Exit.Fallthrough;
                return sections;
            case BoundTryStatement region:
                Exit finalizer = region.FinallyBody is { } cleanup ? PossibleExits(cleanup) : Exit.Fallthrough;
                if ((finalizer & Exit.Fallthrough) == 0) return finalizer;
                Exit handlers = PossibleExits(region.Body);
                foreach (var handler in region.Catches) handlers |= PossibleExits(handler.Body);
                return handlers | (finalizer & ~Exit.Fallthrough);
            default:
                return Exit.Fallthrough;
        }
    }
}

public sealed record BoundBlockStatement(
    ImmutableArray<BoundStatement> Statements) : BoundStatement
{
    public bool IsResumable { get; init; }
    public bool RequiresSuspensionStateMachine { get; init; }
    // Finalization after this block's local cleanup on every exit. Also used for
    // function-level lifecycle cleanup and using declarations' lexical tails.
    public BoundExpression? ExitCleanup { get; init; }
    // A stack value allocated in this lexical scope was relocated to an
    // enclosing local.  Cleanup still runs here, but stackrestore is deferred
    // to the enclosing destination lifetime.
    public bool RetainsStackStorage { get; init; }
    public override BoundKind Kind => BoundKind.BlockStatement;
}

public sealed record BoundVariableDeclarationStatement(
    LocalVariableSymbol Variable,
    BoundExpression? Initializer) : BoundStatement
{
    public override BoundKind Kind => BoundKind.VariableDeclarationStatement;
}

public sealed record BoundReturnStatement(
    BoundExpression? Expression) : BoundStatement
{
    public override BoundKind Kind => BoundKind.ReturnStatement;
}

public sealed record BoundExpressionStatement(
    BoundExpression Expression) : BoundStatement
{
    public override BoundKind Kind => BoundKind.ExpressionStatement;
}

public sealed record BoundIfStatement(
    BoundExpression Condition,
    BoundStatement ThenStatement,
    BoundStatement? ElseStatement) : BoundStatement
{
    public override BoundKind Kind => BoundKind.IfStatement;
}

public sealed record BoundWhileStatement(
    BoundExpression Condition,
    BoundStatement Body) : BoundStatement
{
    public override BoundKind Kind => BoundKind.WhileStatement;
}

public sealed record BoundDoWhileStatement(
    BoundStatement Body,
    BoundExpression Condition) : BoundStatement
{
    public override BoundKind Kind => BoundKind.DoWhileStatement;
}

public sealed record BoundForStatement(
    BoundStatement? Initializer,
    BoundExpression? Condition,
    BoundExpression? Increment,
    BoundStatement Body) : BoundStatement
{
    public override BoundKind Kind => BoundKind.ForStatement;
}

public sealed record BoundBreakStatement() : BoundStatement
{
    public override BoundKind Kind => BoundKind.BreakStatement;
}

public sealed record BoundContinueStatement() : BoundStatement
{
    public override BoundKind Kind => BoundKind.ContinueStatement;
}
