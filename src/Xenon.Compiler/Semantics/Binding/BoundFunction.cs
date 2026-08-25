using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics.Binding;

public sealed record BoundFunction(
    FunctionSymbol Symbol,
    BoundBlockStatement Body);
