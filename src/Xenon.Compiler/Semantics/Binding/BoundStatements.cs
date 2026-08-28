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

public static class BoundControlFlow
{
    public static bool AlwaysReturns(BoundStatement statement) => statement switch
    {
        BoundReturnStatement => true,
        BoundBlockStatement block => BlockReturns(block),
        BoundIfStatement { ElseStatement: not null } conditional => AlwaysReturns(conditional.ThenStatement) && AlwaysReturns(conditional.ElseStatement),
        BoundSwitchStatement selection => selection.Sections.Any(section => section.Value is null) &&
            selection.Sections.All(section => section.Body.Statements.IsEmpty || AlwaysReturns(section.Body)),
        _ => false,
    };

    public static bool TerminatesSection(BoundStatement statement) => statement switch
    {
        BoundBreakStatement or BoundReturnStatement or BoundContinueStatement => true,
        BoundBlockStatement block => block.Statements.Any(TerminatesSection),
        BoundIfStatement { ElseStatement: not null } conditional => TerminatesSection(conditional.ThenStatement) && TerminatesSection(conditional.ElseStatement),
        _ => AlwaysReturns(statement),
    };

    private static bool BlockReturns(BoundBlockStatement block)
    {
        foreach (BoundStatement statement in block.Statements)
        {
            if (AlwaysReturns(statement)) return true;
            if (TerminatesSection(statement)) return false;
        }
        return false;
    }
}

public sealed record BoundBlockStatement(
    ImmutableArray<BoundStatement> Statements) : BoundStatement
{
    // Function-level finalization, after local scope cleanup on every exit.
    public BoundExpression? ExitCleanup { get; init; }
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
