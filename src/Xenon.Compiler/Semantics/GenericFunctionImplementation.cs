using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xenon.Compiler.Libraries;

namespace Xenon.Compiler.Semantics;

/// <summary>An implementation provider for an open generic function, independent of its storage format.</summary>
internal interface IGenericFunctionImplementation
{
    BoundBlockStatement? PortableBody { get; }

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
    BoundFunction? PortableInstanceInitializer { get; }

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
    public BoundBlockStatement? PortableBody { get; private set; }

    internal void SetPortableBody(BoundBlockStatement body) => PortableBody = body;

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
    public BoundFunction? PortableInstanceInitializer { get; private set; }

    internal void CapturePortableInstanceInitializer(StructTypeSymbol definition,
        DiagnosticBag diagnostics, ConstantEvaluationContext constants,
        GenericFunctionSpecializer functionSpecializer, CancellationToken cancellationToken)
    {
        if (!definition.Fields.Any(field => field.HasInitializer)) return;
        var initializer = new FunctionSymbol(FunctionKind.InstanceInitializer, definition, [],
            SymbolOrigin.CompilerGenerated, Accessibility.Private);
        definition.SetInstanceInitializer(initializer);
        var semanticInfo = new SemanticInfoStore();
        var substitutions = new Dictionary<GenericParameterSymbol, TypeSymbol>(
            ReferenceEqualityComparer.Instance);
        foreach (GenericParameterSymbol parameter in definition.TypeParameters)
            substitutions.Add(parameter, parameter);
        FileSymbolScope scope = CreateScope(substitutions, semanticInfo, functionSpecializer);
        var binder = new FunctionBodyBinder(initializer, scope, diagnostics, constants,
            semanticInfo, functionSpecializer, cancellationToken);
        foreach (FieldSymbol field in definition.Fields)
        {
            if (!field.HasInitializer ||
                field.Implementation is not SourceSymbolImplementation
                {
                    Declaration: FieldDeclarationSyntax declaration,
                })
                continue;
            if (binder.BindFieldInitializer(field, declaration.Initializer) is BoundExpression bound)
                field.SetInitializer(bound);
        }
        PortableInstanceInitializer = new BoundFunction(initializer,
            new BoundBlockStatement(binder.CreateInstanceFieldInitializerStatements(definition)));
    }

    internal void CapturePortableStaticInitializers(StructTypeSymbol definition,
        GenericStructImplementationServices services)
    {
        foreach (FieldSymbol field in definition.StaticFields.Where(field =>
                     field.HasInitializer && !field.IsThreadLocal))
            if (field.Implementation is SourceSymbolImplementation
                {
                    Declaration: FieldDeclarationSyntax declaration,
                })
                services.BindSourceStaticFieldInitializer(field, definition, sourceScope, declaration);
    }

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

/// <summary>Source-free implementation of a generic function stored as stable Library IR.</summary>
internal sealed class LibraryGenericFunctionImplementation(
    FunctionSymbol definition,
    XelibBodyNode body,
    Func<int, TypeSymbol> resolveType,
    Func<XelibSymbolReference, Symbol> resolveSymbol) : IGenericFunctionImplementation
{
    public BoundBlockStatement? PortableBody => null;

    public BoundBlockStatement Bind(FunctionSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics, ConstantEvaluationContext constants,
        GenericFunctionSpecializer specializer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TypeSymbol Type(int id) => specializer.StructSpecializer.Substitute(
            resolveType(id), substitutions);
        Symbol Symbol(XelibSymbolReference reference) => MapSymbol(resolveSymbol(reference), specialization);
        return XelibBodyCodec.Decode(body, specialization, Type, Symbol, ResolveGenericMethod);
    }

    private static BoundExpression ResolveGenericMethod(BoundExpression receiver, Symbol requirement,
        ImmutableArray<BoundExpression> arguments, bool isPointerAccess)
    {
        TypeSymbol receiverType = receiver.Type switch
        {
            PointerTypeSymbol pointer => pointer.ElementType,
            ReferenceTypeSymbol reference => reference.ElementType,
            _ => receiver.Type,
        };
        if (receiverType is StructTypeSymbol structure)
        {
            FunctionSymbol? method = structure.Methods.FirstOrDefault(candidate =>
                candidate.Name == requirement.Name && candidate.Parameters.Length == arguments.Length &&
                candidate.Parameters.Zip(arguments).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type)));
            if (method is not null)
                return new BoundMethodCallExpression(receiver, method, arguments, isPointerAccess);
        }
        if (receiverType is InterfaceTypeSymbol @interface)
        {
            FunctionSymbol? method = @interface.AllMethods.FirstOrDefault(candidate =>
                candidate.Name == requirement.Name && candidate.Parameters.Length == arguments.Length &&
                candidate.Parameters.Zip(arguments).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type)));
            if (method is not null)
                return new BoundInterfaceMethodCallExpression(receiver, @interface, method, arguments,
                    isPointerAccess);
        }
        throw new XelibFormatException(XelibErrorCode.InvalidReference,
            $"generic method requirement '{requirement.Name}' cannot be resolved on '{receiverType}'");
    }

    private Symbol MapSymbol(Symbol source, FunctionSymbol specialization)
    {
        if (ReferenceEquals(source, definition)) return specialization;
        if (source is ParameterSymbol parameter && parameter.ContainingSymbol is FunctionSymbol owner)
        {
            if (MapFunction(owner, specialization) is { } mapped &&
                parameter.Ordinal >= 0 && parameter.Ordinal < mapped.Parameters.Length)
                return mapped.Parameters[parameter.Ordinal];
        }

        StructTypeSymbol? definitionOwner = definition.ContainingStruct;
        StructTypeSymbol? specializedOwner = specialization.ContainingStruct;
        if (definitionOwner is null || specializedOwner is null ||
            ContainingStruct(source) is not { } sourceOwner ||
            !ReferenceEquals(sourceOwner, definitionOwner))
            return source;

        return source switch
        {
            FieldSymbol field => MapByIndex(definitionOwner.Fields, specializedOwner.Fields, field) ??
                MapByIndex(definitionOwner.StaticFields, specializedOwner.StaticFields, field) ?? source,
            ConstantSymbol constant => MapByIndex(definitionOwner.Constants, specializedOwner.Constants, constant) ?? source,
            PropertySymbol property => MapByIndex(definitionOwner.Properties, specializedOwner.Properties, property) ?? source,
            IndexerSymbol indexer => MapByIndex(definitionOwner.Indexers, specializedOwner.Indexers, indexer) ?? source,
            FunctionSymbol function => MapFunction(function, specialization) ?? source,
            _ => source,
        };
    }

    private FunctionSymbol? MapFunction(FunctionSymbol source, FunctionSymbol specialization)
    {
        if (ReferenceEquals(source, definition)) return specialization;
        StructTypeSymbol? definitionOwner = definition.ContainingStruct;
        StructTypeSymbol? specializedOwner = specialization.ContainingStruct;
        if (definitionOwner is null || specializedOwner is null ||
            !ReferenceEquals(source.ContainingStruct, definitionOwner))
            return null;
        return MapByIndex(definitionOwner.Methods, specializedOwner.Methods, source) ??
            MapByIndex(definitionOwner.Constructors, specializedOwner.Constructors, source) ??
            (ReferenceEquals(definitionOwner.Destructor, source) ? specializedOwner.Destructor : null) ??
            (ReferenceEquals(definitionOwner.InstanceInitializer, source) ? specializedOwner.InstanceInitializer : null);
    }

    private static T? MapByIndex<T>(ImmutableArray<T> definitions,
        ImmutableArray<T> specializations, T source) where T : class
    {
        int index = definitions.IndexOf(source);
        return index >= 0 && index < specializations.Length ? specializations[index] : null;
    }

    private static StructTypeSymbol? ContainingStruct(Symbol symbol)
    {
        for (Symbol? current = symbol; current is not null; current = current.ContainingSymbol)
            if (current is StructTypeSymbol structure) return structure;
        return null;
    }
}

internal sealed class LibraryGenericStructImplementation(
    FunctionSymbol? initializerDefinition,
    XelibBodyNode? initializerBody,
    Func<int, TypeSymbol> resolveType,
    Func<XelibSymbolReference, Symbol> resolveSymbol) : IGenericStructImplementation
{
    public BoundFunction? PortableInstanceInitializer => null;

    public BoundFunction? BindInstanceFieldInitializers(StructTypeSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics, ConstantEvaluationContext constants,
        SemanticInfoStore semanticInfo, GenericFunctionSpecializer functionSpecializer,
        CancellationToken cancellationToken)
    {
        if (initializerDefinition is null || initializerBody is null) return null;
        var initializer = new FunctionSymbol(FunctionKind.InstanceInitializer, specialization, [],
            SymbolOrigin.CompilerGenerated, Accessibility.Private);
        specialization.SetInstanceInitializer(initializer);
        var implementation = new LibraryGenericFunctionImplementation(initializerDefinition,
            initializerBody, resolveType, resolveSymbol);
        return new BoundFunction(initializer, implementation.Bind(initializer, substitutions,
            diagnostics, constants, functionSpecializer, cancellationToken));
    }

    public ImmutableArray<BoundFunction> BindStaticFieldInitializers(StructTypeSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics, ConstantEvaluationContext constants,
        SemanticInfoStore semanticInfo, GenericFunctionSpecializer functionSpecializer,
        GenericStructImplementationServices services, CancellationToken cancellationToken) => [];

    public bool EvaluateConstant(ConstantSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        GenericStructSpecializer structSpecializer, SemanticInfoStore semanticInfo,
        GenericStructImplementationServices services)
    {
        if (specialization.GenericDefinition is not { HasValue: true } definition ||
            definition.BoundValue is not null)
            return false;
        specialization.SetValue(definition.Value);
        return true;
    }
}
