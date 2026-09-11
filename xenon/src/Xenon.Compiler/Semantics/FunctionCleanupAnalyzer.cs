using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

internal readonly record struct FunctionCleanupRequirements(
    bool HasScalarCleanup,
    bool HasStackArrays)
{
    public bool HasScopeCleanup => HasScalarCleanup || HasStackArrays;
}

/// <summary>
/// Derives function-level cleanup metadata from the final bound body and concrete
/// symbol types. This pass is intentionally independent of source syntax so it
/// also applies to bodies materialized from Library IR.
/// </summary>
internal static class FunctionCleanupAnalyzer
{
    public static FunctionCleanupRequirements Analyze(FunctionSymbol function, BoundBlockStatement body)
    {
        bool hasScalarCleanup = function.Parameters.Any(parameter =>
            TypeFacts.GetCompleteDestructor(parameter.Type) is not null);
        bool hasStackArrays = false;

        foreach (BoundNode node in BoundTree.DescendantsAndSelf(body))
        {
            switch (node)
            {
                case BoundVariableDeclarationStatement declaration:
                    // Library IR can carry preliminary metadata from an open generic
                    // definition. The concrete type is the authoritative source.
                    declaration.Variable.Destructor = TypeFacts.GetCompleteDestructor(declaration.Variable.Type);
                    hasScalarCleanup |= declaration.Variable.Destructor is not null;
                    break;
                case BoundArrayCreationExpression { Storage: ArrayStorageKind.Stack }:
                    // Stack allocation is structural rather than element-type-dependent,
                    // but is still derived from the final body instead of copied.
                    hasStackArrays = true;
                    break;
            }
        }

        return new FunctionCleanupRequirements(hasScalarCleanup, hasStackArrays);
    }

    public static void Recompute(FunctionSymbol function, BoundBlockStatement body)
    {
        FunctionCleanupRequirements requirements = Analyze(function, body);
        function.HasScalarCleanup = requirements.HasScalarCleanup;
        function.HasStackArrays = requirements.HasStackArrays;
    }
}
