using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

/// <summary>An implementation provider for an open generic function, independent of its storage format.</summary>
internal interface IGenericFunctionImplementation
{
    BoundBlockStatement Bind(
        FunctionSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics,
        ConstantEvaluationContext constants,
        GenericFunctionSpecializer specializer,
        CancellationToken cancellationToken);
}

/// <summary>The current source-backed implementation. A future Library IR provider implements the same contract.</summary>
internal sealed class SourceGenericFunctionImplementation(
    BlockStatementSyntax body,
    FileSymbolScope scope) : IGenericFunctionImplementation
{
    public BoundBlockStatement Bind(FunctionSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics, ConstantEvaluationContext constants,
        GenericFunctionSpecializer specializer, CancellationToken cancellationToken)
    {
        var semanticInfo = new SemanticInfoStore();
        FileSymbolScope specializedScope = scope.WithTypeSubstitutions(substitutions, semanticInfo);
        var binder = new FunctionBodyBinder(specialization, specializedScope, diagnostics, constants,
            semanticInfo, specializer, cancellationToken);
        return binder.BindBody(body);
    }
}
