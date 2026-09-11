using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics.Binding;

public abstract record BoundNode
{
    public abstract BoundKind Kind { get; }
}

public abstract record BoundStatement : BoundNode;

public abstract record BoundExpression(TypeSymbol Type) : BoundNode;
