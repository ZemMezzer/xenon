using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

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

/// <summary>Provider-owned implementation data for an open generic struct.</summary>
internal interface IGenericStructImplementation
{
    BoundFunction? BindInstanceFieldInitializers(
        StructTypeSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics,
        ConstantEvaluationContext constants,
        SemanticInfoStore semanticInfo,
        GenericFunctionSpecializer functionSpecializer,
        CancellationToken cancellationToken);

    ImmutableArray<BoundFunction> BindStaticFieldInitializers(
        StructTypeSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics,
        ConstantEvaluationContext constants,
        SemanticInfoStore semanticInfo,
        GenericFunctionSpecializer functionSpecializer,
        GenericStructImplementationServices services,
        CancellationToken cancellationToken);

    bool EvaluateConstant(
        ConstantSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        GenericStructSpecializer structSpecializer,
        SemanticInfoStore semanticInfo,
        GenericStructImplementationServices services);
}

internal sealed class GenericStructImplementationServices(SemanticAnalyzer analyzer)
{
    public void BindSourceStaticFieldInitializer(FieldSymbol field, StructTypeSymbol type,
        FileSymbolScope scope, FieldDeclarationSyntax declaration) =>
        analyzer.BindSourceGenericStaticFieldInitializer(field, type, scope, declaration);

    public bool EvaluateSourceConstant(ConstantSymbol constant, FileSymbolScope scope,
        ExpressionSyntax initializer, TextLocation location) =>
        analyzer.EvaluateSourceGenericConstant(constant, scope, initializer, location);
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
        FileSymbolScope specializedScope = scope.WithTypeSubstitutions(substitutions, semanticInfo,
            specializer.Types);
        specializedScope.SetGenericStructSpecializer(specializer.StructSpecializer);
        var binder = new FunctionBodyBinder(specialization, specializedScope, diagnostics, constants,
            semanticInfo, specializer, cancellationToken);
        return binder.BindBody(body);
    }
}

/// <summary>
/// Source-backed generic struct payload. All syntax and source-scope access is
/// deliberately contained here rather than in the specialization pipeline.
/// </summary>
internal sealed class SourceGenericStructImplementation(FileSymbolScope sourceScope)
    : IGenericStructImplementation
{
    public BoundFunction? BindInstanceFieldInitializers(
        StructTypeSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics,
        ConstantEvaluationContext constants,
        SemanticInfoStore semanticInfo,
        GenericFunctionSpecializer functionSpecializer,
        CancellationToken cancellationToken)
    {
        if (!specialization.Fields.Any(field => field.HasInitializer)) return null;
        FileSymbolScope scope = CreateScope(substitutions, semanticInfo, functionSpecializer);
        var initializer = new FunctionSymbol(FunctionKind.InstanceInitializer, specialization, [],
            SymbolOrigin.CompilerGenerated, Accessibility.Private);
        specialization.SetInstanceInitializer(initializer);
        var binder = new FunctionBodyBinder(initializer, scope, diagnostics, constants,
            semanticInfo, functionSpecializer, cancellationToken);
        foreach (FieldSymbol field in specialization.Fields)
        {
            if (!field.HasInitializer ||
                field.GenericDefinition?.Implementation is not SourceSymbolImplementation
                {
                    Declaration: FieldDeclarationSyntax declaration,
                })
                continue;
            if (binder.BindFieldInitializer(field, declaration.Initializer) is BoundExpression bound)
                field.SetInitializer(bound);
        }
        return new BoundFunction(initializer,
            new BoundBlockStatement(binder.CreateInstanceFieldInitializerStatements(specialization)));
    }

    public ImmutableArray<BoundFunction> BindStaticFieldInitializers(
        StructTypeSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics,
        ConstantEvaluationContext constants,
        SemanticInfoStore semanticInfo,
        GenericFunctionSpecializer functionSpecializer,
        GenericStructImplementationServices services,
        CancellationToken cancellationToken)
    {
        FileSymbolScope scope = CreateScope(substitutions, semanticInfo, functionSpecializer);
        var functions = ImmutableArray.CreateBuilder<BoundFunction>();
        foreach (FieldSymbol field in specialization.StaticFields.Where(field => field.HasInitializer))
        {
            if (field.GenericDefinition?.Implementation is not SourceSymbolImplementation
                {
                    Declaration: FieldDeclarationSyntax declaration,
                })
                continue;
            if (!field.IsThreadLocal)
            {
                services.BindSourceStaticFieldInitializer(field, specialization, scope, declaration);
                continue;
            }

            var initializer = new FunctionSymbol(field);
            var binder = new FunctionBodyBinder(initializer, scope, diagnostics, constants,
                semanticInfo, functionSpecializer, cancellationToken);
            if (binder.BindFieldInitializer(field, declaration.Initializer) is not BoundExpression bound)
                continue;
            field.SetInitializer(bound);
            functions.Add(new BoundFunction(initializer,
                new BoundBlockStatement([binder.CreateThreadLocalFieldInitializerStatement(field)])));
        }
        return functions.ToImmutable();
    }

    public bool EvaluateConstant(ConstantSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        GenericStructSpecializer structSpecializer,
        SemanticInfoStore semanticInfo,
        GenericStructImplementationServices services)
    {
        if (specialization.GenericDefinition is not { } definition ||
            definition.Implementation is not SourceSymbolImplementation)
            return false;
        FileSymbolScope scope = sourceScope.WithTypeSubstitutions(substitutions, semanticInfo,
            structSpecializer.Types);
        scope.SetGenericStructSpecializer(structSpecializer);
        return services.EvaluateSourceConstant(specialization, scope,
            definition.Initializer, definition.IdentifierToken.Location);
    }

    private FileSymbolScope CreateScope(
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        SemanticInfoStore semanticInfo,
        GenericFunctionSpecializer functionSpecializer)
    {
        FileSymbolScope scope = sourceScope.WithTypeSubstitutions(substitutions, semanticInfo,
            functionSpecializer.Types);
        scope.SetGenericStructSpecializer(functionSpecializer.StructSpecializer);
        return scope;
    }
}
