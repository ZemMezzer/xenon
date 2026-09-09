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

    ImmutableArray<BoundFunction> PortableStaticFieldInitializers { get; }

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
        ConstantEvaluationContext constants,
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
        BoundBlockStatement result = binder.BindBody(body);
        specializer.AddGeneratedFunctions(semanticInfo.LambdaFunctions);
        return result;
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
    public ImmutableArray<BoundFunction> PortableStaticFieldInitializers { get; private set; } = [];

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
        functionSpecializer.AddGeneratedFunctions(semanticInfo.LambdaFunctions);
    }

    internal void CapturePortableStaticInitializers(StructTypeSymbol definition,
        GenericStructImplementationServices services, DiagnosticBag diagnostics,
        ConstantEvaluationContext constants, GenericFunctionSpecializer functionSpecializer,
        CancellationToken cancellationToken)
    {
        var functions = ImmutableArray.CreateBuilder<BoundFunction>();
        var semanticInfo = new SemanticInfoStore();
        var substitutions = new Dictionary<GenericParameterSymbol, TypeSymbol>(
            ReferenceEqualityComparer.Instance);
        foreach (GenericParameterSymbol parameter in definition.TypeParameters)
            substitutions.Add(parameter, parameter);
        FileSymbolScope scope = CreateScope(substitutions, semanticInfo, functionSpecializer);
        foreach (FieldSymbol field in definition.StaticFields.Where(field => field.HasInitializer))
            if (field.Implementation is SourceSymbolImplementation
                {
                    Declaration: FieldDeclarationSyntax declaration,
                })
            {
                if (!field.IsThreadLocal)
                {
                    services.BindSourceStaticFieldInitializer(field, definition, sourceScope, declaration);
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
        PortableStaticFieldInitializers = functions.ToImmutable();
        functionSpecializer.AddGeneratedFunctions(semanticInfo.LambdaFunctions);
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
        ConstantEvaluationContext constants,
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
    Func<XelibSymbolReference, Symbol> resolveSymbol,
    IReadOnlyDictionary<FunctionSymbol, XelibBodyNode> lambdaBodies) : IGenericFunctionImplementation
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
        var specializedLambdas = new Dictionary<FunctionSymbol, FunctionSymbol>(ReferenceEqualityComparer.Instance);
        Symbol Symbol(XelibSymbolReference reference) => MapSymbol(resolveSymbol(reference), specialization,
            specializer.StructSpecializer, substitutions, SpecializeLambda);
        return XelibBodyCodec.Decode(body, specialization, Type, Symbol,
            (receiver, requirement, arguments, isPointerAccess) =>
                ResolveGenericMethod(receiver, requirement, arguments, isPointerAccess, specializer),
            (operation, receiver, requirement, arguments, value, operatorKind, isPointerAccess,
                resultType, typeArguments) => ResolveGenericOperation(operation, receiver,
                requirement, arguments, value, operatorKind, isPointerAccess, resultType,
                typeArguments, specializer));

        FunctionSymbol SpecializeLambda(FunctionSymbol source)
        {
            if (specializedLambdas.TryGetValue(source, out FunctionSymbol? existing)) return existing;
            ImmutableArray<ParameterSymbol> parameters = source.Parameters.Select(parameter =>
                new ParameterSymbol(parameter.Name,
                    specializer.StructSpecializer.Substitute(parameter.Type, substitutions),
                    parameter.Ordinal, parameter.IsReadonly)).ToImmutableArray();
            var created = new FunctionSymbol(
                $"{source.Name}.{specialization.Name}", source.ContainingNamespace,
                FunctionKind.Ordinary,
                specializer.StructSpecializer.Substitute(source.ReturnType, substitutions),
                parameters, Accessibility.Private, isDefinition: true,
                origin: SymbolOrigin.CompilerGenerated)
            {
                IsLambda = true,
                IsCapturingLambda = source.IsCapturingLambda,
                HasStackArrays = source.HasStackArrays,
                HasScalarCleanup = source.HasScalarCleanup,
            };
            specializedLambdas.Add(source, created);
            created.LambdaCaptures = source.LambdaCaptures.Select(capture =>
                new CaptureVariableSymbol(capture.Name,
                    specializer.StructSpecializer.Substitute(capture.Type, substitutions),
                    specializer.StructSpecializer.Substitute(capture.StorageType, substitutions),
                    capture.CaptureKind, capture.Ordinal, created,
                    SymbolOrigin.CompilerGenerated)).ToImmutableArray();
            if (!lambdaBodies.TryGetValue(source, out XelibBodyNode? lambdaBody))
                throw new XelibFormatException(XelibErrorCode.InvalidReference,
                    $"generic closure invoke body '{source.Name}' is unavailable");
            BoundBlockStatement boundBody = XelibBodyCodec.Decode(lambdaBody, created, Type, Symbol,
                (receiver, requirement, arguments, isPointerAccess) =>
                    ResolveGenericMethod(receiver, requirement, arguments, isPointerAccess, specializer),
                (operation, receiver, requirement, arguments, value, operatorKind, isPointerAccess,
                    resultType, typeArguments) => ResolveGenericOperation(operation, receiver,
                    requirement, arguments, value, operatorKind, isPointerAccess, resultType,
                    typeArguments, specializer));
            specializer.AddGeneratedFunctions([new BoundFunction(created, boundBody)]);
            return created;
        }
    }

    private BoundExpression ResolveGenericOperation(BoundDeferredGenericOperationKind operation,
        BoundExpression? receiver, Symbol requirement, ImmutableArray<BoundExpression> arguments,
        BoundExpression? value, SyntaxKind operatorKind, bool isPointerAccess, TypeSymbol resultType,
        ImmutableArray<TypeSymbol> typeArguments, GenericFunctionSpecializer specializer)
    {
        if (operation == BoundDeferredGenericOperationKind.OperatorCall)
        {
            if (requirement is not FunctionSymbol operatorDefinition ||
                typeArguments is not [StructTypeSymbol owner] || owner.GenericDefinition is null)
                throw InvalidGenericOperation(operation, requirement, resultType);
            operatorDefinition = specializer.StructSpecializer.GetFunctionDefinition(operatorDefinition);
            if (specializer.StructSpecializer.FindSpecializedFunction(operatorDefinition, owner) is not { } function)
                throw InvalidGenericOperation(operation, requirement, owner);
            // For this opcode the context flag denotes an explicit conversion, not receiver access.
            return new BoundCallExpression(function, arguments) { IsExplicitConversion = isPointerAccess };
        }
        if (operation == BoundDeferredGenericOperationKind.FunctionCall)
        {
            if (requirement is not FunctionSymbol definition)
                throw InvalidGenericOperation(operation, requirement, resultType);
            FunctionSymbol function = specializer.GetOrCreate(definition, typeArguments, TextLocation.None) ??
                throw InvalidGenericOperation(operation, requirement, resultType);
            return new BoundCallExpression(function, arguments);
        }

        if (operation is BoundDeferredGenericOperationKind.Construction or
            BoundDeferredGenericOperationKind.Allocation)
        {
            StructTypeSymbol? structure = resultType switch
            {
                StructTypeSymbol item => item,
                PointerTypeSymbol { ElementType: StructTypeSymbol item } => item,
                _ => null,
            };
            if (structure is null) throw InvalidGenericOperation(operation, requirement, resultType);
            FunctionSymbol? constructor = FindConstructor(structure, arguments);
            if (constructor is null && !arguments.IsEmpty)
                throw InvalidGenericOperation(operation, requirement, resultType);
            if (operation == BoundDeferredGenericOperationKind.Construction)
                return constructor is null
                    ? new BoundStructConstructionExpression(structure, []) { IsDefaultInitialization = true }
                    : new BoundConstructorCallExpression(structure, constructor, arguments);
            var pointer = resultType as PointerTypeSymbol ??
                throw InvalidGenericOperation(operation, requirement, resultType);
            return new BoundNewExpression(structure, constructor, arguments,
                IsPositionalInitialization: false, pointer)
            {
                IsDefaultInitialization = constructor is null,
            };
        }

        if (receiver is null) throw InvalidGenericOperation(operation, requirement, resultType);
        TypeSymbol receiverType = receiver.Type switch
        {
            PointerTypeSymbol pointer => pointer.ElementType,
            ReferenceTypeSymbol reference => reference.ElementType,
            UniqueTypeSymbol unique when isPointerAccess => unique.ElementType,
            SharedTypeSymbol shared when isPointerAccess => shared.ElementType,
            _ => receiver.Type,
        };
        if (operation is BoundDeferredGenericOperationKind.FieldGet or
            BoundDeferredGenericOperationKind.FieldSet)
        {
            if (receiverType is not StructTypeSymbol structure ||
                structure.FindField(requirement.Name) is not { } field)
                throw InvalidGenericOperation(operation, requirement, receiverType);
            var target = new BoundMemberAccessExpression(receiver, field, isPointerAccess);
            return operation == BoundDeferredGenericOperationKind.FieldGet
                ? target
                : new BoundAssignmentExpression(target, operatorKind,
                    value ?? throw InvalidGenericOperation(operation, requirement, receiverType));
        }

        if (operation is BoundDeferredGenericOperationKind.PropertyGet or
            BoundDeferredGenericOperationKind.PropertySet)
        {
            if (receiverType is StructTypeSymbol structure &&
                structure.FindProperty(requirement.Name) is { } property)
            {
                if (operation == BoundDeferredGenericOperationKind.PropertyGet)
                    return new BoundMethodCallExpression(receiver,
                        property.Getter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                        [], isPointerAccess);
                BoundExpression assigned = value ??
                    throw InvalidGenericOperation(operation, requirement, receiverType);
                if (operatorKind == SyntaxKind.EqualsToken)
                    return new BoundPropertySetExpression(receiver, property, assigned, isPointerAccess);
                return new BoundCompoundAccessorAssignmentExpression(receiver,
                    property.Getter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                    property.Setter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                    [], CompoundOperator(operatorKind), assigned, isPointerAccess, null);
            }
            if (receiverType is InterfaceTypeSymbol @interface &&
                @interface.FindProperty(requirement.Name) is { } interfaceProperty)
            {
                if (operation == BoundDeferredGenericOperationKind.PropertyGet)
                    return new BoundInterfaceMethodCallExpression(receiver, @interface,
                        interfaceProperty.Getter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                        [], isPointerAccess);
                BoundExpression assigned = value ??
                    throw InvalidGenericOperation(operation, requirement, receiverType);
                if (operatorKind == SyntaxKind.EqualsToken)
                    return new BoundInterfacePropertySetExpression(receiver, @interface,
                        interfaceProperty, assigned, isPointerAccess);
                return new BoundCompoundAccessorAssignmentExpression(receiver,
                    interfaceProperty.Getter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                    interfaceProperty.Setter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                    [], CompoundOperator(operatorKind), assigned, isPointerAccess, @interface);
            }
            throw InvalidGenericOperation(operation, requirement, receiverType);
        }

        if (operation is BoundDeferredGenericOperationKind.IndexerGet or
            BoundDeferredGenericOperationKind.IndexerSet)
        {
            if (receiverType is StructTypeSymbol structure &&
                FindIndexer(structure.AllIndexers, arguments) is { } indexer)
            {
                if (operation == BoundDeferredGenericOperationKind.IndexerGet)
                    return new BoundMethodCallExpression(receiver,
                        indexer.Getter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                        arguments, isPointerAccess);
                BoundExpression assigned = value ??
                    throw InvalidGenericOperation(operation, requirement, receiverType);
                if (operatorKind == SyntaxKind.EqualsToken)
                    return new BoundIndexerSetExpression(receiver, indexer, arguments, assigned);
                return new BoundCompoundAccessorAssignmentExpression(receiver,
                    indexer.Getter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                    indexer.Setter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                    arguments, CompoundOperator(operatorKind), assigned, isPointerAccess, null);
            }
            if (receiverType is InterfaceTypeSymbol @interface &&
                FindInterfaceIndexer(@interface.AllIndexers, arguments) is { } interfaceIndexer)
            {
                if (operation == BoundDeferredGenericOperationKind.IndexerGet)
                    return new BoundInterfaceMethodCallExpression(receiver, @interface,
                        interfaceIndexer.Getter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                        arguments, isPointerAccess);
                BoundExpression assigned = value ??
                    throw InvalidGenericOperation(operation, requirement, receiverType);
                if (operatorKind == SyntaxKind.EqualsToken)
                    return new BoundInterfaceIndexerSetExpression(receiver, @interface,
                        interfaceIndexer, arguments, assigned);
                return new BoundCompoundAccessorAssignmentExpression(receiver,
                    interfaceIndexer.Getter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                    interfaceIndexer.Setter ?? throw InvalidGenericOperation(operation, requirement, receiverType),
                    arguments, CompoundOperator(operatorKind), assigned, isPointerAccess, @interface);
            }
        }
        throw InvalidGenericOperation(operation, requirement, receiverType);
    }

    private static SyntaxKind CompoundOperator(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusEqualsToken => SyntaxKind.PlusToken,
        SyntaxKind.MinusEqualsToken => SyntaxKind.MinusToken,
        SyntaxKind.StarEqualsToken => SyntaxKind.StarToken,
        SyntaxKind.SlashEqualsToken => SyntaxKind.SlashToken,
        SyntaxKind.PercentEqualsToken => SyntaxKind.PercentToken,
        SyntaxKind.AmpersandEqualsToken => SyntaxKind.AmpersandToken,
        SyntaxKind.PipeEqualsToken => SyntaxKind.PipeToken,
        SyntaxKind.CaretEqualsToken => SyntaxKind.CaretToken,
        SyntaxKind.LessLessEqualsToken => SyntaxKind.LessLessToken,
        SyntaxKind.GreaterGreaterEqualsToken => SyntaxKind.GreaterGreaterToken,
        _ => kind,
    };

    private static FunctionSymbol? FindConstructor(StructTypeSymbol structure,
        ImmutableArray<BoundExpression> arguments) => structure.Constructors.FirstOrDefault(candidate =>
        ParametersMatch(candidate.Parameters, arguments));

    private static IndexerSymbol? FindIndexer(IEnumerable<IndexerSymbol> candidates,
        ImmutableArray<BoundExpression> arguments) => candidates.FirstOrDefault(candidate =>
        ParametersMatch(candidate.Parameters, arguments));

    private static InterfaceIndexerSymbol? FindInterfaceIndexer(
        IEnumerable<InterfaceIndexerSymbol> candidates, ImmutableArray<BoundExpression> arguments) =>
        candidates.FirstOrDefault(candidate => ParametersMatch(candidate.Parameters, arguments));

    private static bool ParametersMatch(ImmutableArray<ParameterSymbol> parameters,
        ImmutableArray<BoundExpression> arguments) => parameters.Length == arguments.Length &&
        parameters.Zip(arguments).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type));

    private static XelibFormatException InvalidGenericOperation(
        BoundDeferredGenericOperationKind operation, Symbol requirement, TypeSymbol type) =>
        new(XelibErrorCode.InvalidReference,
            $"generic operation '{operation}' for requirement '{requirement.Name}' cannot be resolved on '{type}'");

    private static BoundExpression ResolveGenericMethod(BoundExpression receiver, Symbol requirement,
        ImmutableArray<BoundExpression> arguments, bool isPointerAccess,
        GenericFunctionSpecializer specializer)
    {
        TypeSymbol receiverType = receiver.Type switch
        {
            PointerTypeSymbol pointer => pointer.ElementType,
            ReferenceTypeSymbol reference => reference.ElementType,
            UniqueTypeSymbol unique when isPointerAccess => unique.ElementType,
            SharedTypeSymbol shared when isPointerAccess => shared.ElementType,
            _ => receiver.Type,
        };
        ImmutableArray<TypeSymbol> requiredParameters = requirement switch
        {
            TemplateMethodRequirementSymbol template => template.Parameters.Select(parameter =>
                SubstituteTemplateSelf(parameter.Type, template.Template, receiverType, specializer)).ToImmutableArray(),
            FunctionSymbol function => function.Parameters.Select(parameter => parameter.Type).ToImmutableArray(),
            _ => arguments.Select(argument => argument.Type).ToImmutableArray(),
        };
        TypeSymbol? requiredReturn = requirement switch
        {
            TemplateMethodRequirementSymbol template => SubstituteTemplateSelf(
                template.ReturnType, template.Template, receiverType, specializer),
            FunctionSymbol function => function.ReturnType,
            _ => null,
        };
        bool? requiredStatic = requirement switch
        {
            TemplateMethodRequirementSymbol template => template.IsStatic,
            FunctionSymbol function => function.IsStatic,
            _ => null,
        };
        if (receiverType is StructTypeSymbol structure)
        {
            FunctionSymbol[] matches = structure.FindMethods(requirement.Name).Where(candidate =>
                candidate.Name == requirement.Name && candidate.Parameters.Length == requiredParameters.Length &&
                candidate.Parameters.Zip(requiredParameters).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second)) &&
                (requiredReturn is null || TypeIdentity.AreSame(candidate.ReturnType, requiredReturn)) &&
                (requiredStatic is null || candidate.IsStatic == requiredStatic) &&
                RequirementReadonlyMatches(requirement, candidate)).ToArray();
            if (matches.Length == 1)
                return new BoundMethodCallExpression(receiver, matches[0], arguments, isPointerAccess);
        }
        if (receiverType is InterfaceTypeSymbol @interface)
        {
            FunctionSymbol[] matches = @interface.FindMethods(requirement.Name).Where(candidate =>
                candidate.Name == requirement.Name && candidate.Parameters.Length == requiredParameters.Length &&
                candidate.Parameters.Zip(requiredParameters).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second)) &&
                (requiredReturn is null || TypeIdentity.AreSame(candidate.ReturnType, requiredReturn)) &&
                (requiredStatic is null || candidate.IsStatic == requiredStatic) &&
                RequirementReadonlyMatches(requirement, candidate)).ToArray();
            if (matches.Length == 1)
                return new BoundInterfaceMethodCallExpression(receiver, @interface, matches[0], arguments,
                    isPointerAccess);
        }
        throw new XelibFormatException(XelibErrorCode.InvalidReference,
            $"generic method requirement '{requirement.Name}' cannot be resolved on '{receiverType}'");
    }

    private static bool RequirementReadonlyMatches(Symbol requirement, FunctionSymbol candidate) =>
        requirement switch
        {
            TemplateMethodRequirementSymbol template => template.IsReadonly == candidate.IsReadonly,
            FunctionSymbol function => function.IsReadonly == candidate.IsReadonly,
            _ => true,
        };

    private static TypeSymbol SubstituteTemplateSelf(TypeSymbol type, TemplateSymbol template,
        TypeSymbol receiverType, GenericFunctionSpecializer specializer) => type switch
    {
        TemplateSelfTypeSymbol self when ReferenceEquals(self.Template, template) => receiverType,
        PointerTypeSymbol pointer => specializer.Types.PointerTo(
            SubstituteTemplateSelf(pointer.ElementType, template, receiverType, specializer), pointer.IsReadonly),
        ReferenceTypeSymbol reference => specializer.Types.ReferenceTo(
            SubstituteTemplateSelf(reference.ElementType, template, receiverType, specializer), reference.IsReadonly),
        FunctionPointerTypeSymbol function => specializer.Types.FunctionPointer(
            SubstituteTemplateSelf(function.ReturnType, template, receiverType, specializer),
            function.ParameterTypes.Select(parameter =>
                SubstituteTemplateSelf(parameter, template, receiverType, specializer))),
        FunctionValueTypeSymbol function => specializer.Types.FunctionValue(
            SubstituteTemplateSelf(function.ReturnType, template, receiverType, specializer),
            function.ParameterTypes.Select(parameter =>
                SubstituteTemplateSelf(parameter, template, receiverType, specializer))),
        ArrayTypeSymbol array => specializer.Types.ArrayOf(
            SubstituteTemplateSelf(array.ElementType, template, receiverType, specializer), array.Rank),
        AtomicTypeSymbol atomic => specializer.Types.AtomicOf(
            SubstituteTemplateSelf(atomic.ElementType, template, receiverType, specializer)),
        UniqueTypeSymbol unique => specializer.Types.UniqueOf(
            SubstituteTemplateSelf(unique.ElementType, template, receiverType, specializer)),
        SharedTypeSymbol shared => specializer.Types.SharedOf(
            SubstituteTemplateSelf(shared.ElementType, template, receiverType, specializer)),
        WeakTypeSymbol weak => specializer.Types.WeakOf(
            SubstituteTemplateSelf(weak.ElementType, template, receiverType, specializer)),
        StorageTypeSymbol storage => specializer.Types.StorageOf(
            SubstituteTemplateSelf(storage.ElementType, template, receiverType, specializer)),
        PinTypeSymbol pin => specializer.Types.PinOf(
            SubstituteTemplateSelf(pin.ElementType, template, receiverType, specializer)),
        StructTypeSymbol { GenericDefinition: { } definition } constructed =>
            specializer.StructSpecializer.GetOrCreate(definition,
                constructed.TypeArguments.Select(argument =>
                    SubstituteTemplateSelf(argument, template, receiverType, specializer)).ToImmutableArray(),
                TextLocation.None) ?? type,
        _ => type,
    };

    private Symbol MapSymbol(Symbol source, FunctionSymbol specialization, GenericStructSpecializer structSpecializer,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        Func<FunctionSymbol, FunctionSymbol> specializeLambda)
    {
        if (ReferenceEquals(source, definition)) return specialization;
        if (source is ParameterSymbol parameter && parameter.ContainingSymbol is FunctionSymbol owner)
        {
            if (MapFunction(owner, specialization, structSpecializer, substitutions,
                    specializeLambda) is { } mapped &&
                parameter.Ordinal >= 0 && parameter.Ordinal < mapped.Parameters.Length)
                return mapped.Parameters[parameter.Ordinal];
        }

        if (source is FunctionSymbol callable)
            return MapFunction(callable, specialization, structSpecializer, substitutions,
                specializeLambda) ?? source;
        StructTypeSymbol? definitionOwner = definition.ContainingStruct;
        StructTypeSymbol? specializedOwner = specialization.ContainingStruct;
        if (ContainingStruct(source) is not { } sourceOwner) return source;
        if (!ReferenceEquals(sourceOwner, definitionOwner))
        {
            if (sourceOwner.GenericDefinition is null) return source;
            specializedOwner = (StructTypeSymbol)structSpecializer.Substitute(sourceOwner, substitutions);
            if (ReferenceEquals(specializedOwner, sourceOwner)) return source;
            definitionOwner = sourceOwner;
        }
        if (definitionOwner is null || specializedOwner is null) return source;

        return source switch
        {
            FieldSymbol field => MapByIndex(definitionOwner.Fields, specializedOwner.Fields, field) ??
                MapByIndex(definitionOwner.StaticFields, specializedOwner.StaticFields, field) ?? source,
            ConstantSymbol constant => MapByIndex(definitionOwner.Constants, specializedOwner.Constants, constant) ?? source,
            PropertySymbol property => MapByIndex(definitionOwner.Properties, specializedOwner.Properties, property) ?? source,
            IndexerSymbol indexer => MapByIndex(definitionOwner.Indexers, specializedOwner.Indexers, indexer) ?? source,
            _ => source,
        };
    }

    private FunctionSymbol? MapFunction(FunctionSymbol source, FunctionSymbol specialization, GenericStructSpecializer structSpecializer,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        Func<FunctionSymbol, FunctionSymbol> specializeLambda)
    {
        if (ReferenceEquals(source, definition)) return specialization;
        if (source.IsLambda) return specializeLambda(source);
        StructTypeSymbol? definitionOwner = definition.ContainingStruct;
        StructTypeSymbol? specializedOwner = specialization.ContainingStruct;
        if (source.ContainingStruct is not { } sourceOwner) return null;
        if (!ReferenceEquals(sourceOwner, definitionOwner))
        {
            if (sourceOwner.GenericDefinition is null) return null;
            specializedOwner = (StructTypeSymbol)structSpecializer.Substitute(sourceOwner, substitutions);
            if (ReferenceEquals(specializedOwner, sourceOwner)) return source;
        }
        if (specializedOwner is null) return null;
        source = structSpecializer.GetFunctionDefinition(source);
        return structSpecializer.FindSpecializedFunction(source, specializedOwner) ??
            (ReferenceEquals(source.ContainingStruct?.Destructor, source) ? specializedOwner.Destructor : null) ??
            (ReferenceEquals(source.ContainingStruct?.InstanceInitializer, source) ? specializedOwner.InstanceInitializer : null);
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
    ImmutableArray<(FieldSymbol Field, FunctionSymbol Definition, XelibBodyNode Body)> staticInitializers,
    ImmutableArray<(ConstantSymbol Constant, XelibBodyNode Expression)> constantExpressions,
    Func<int, TypeSymbol> resolveType,
    Func<XelibSymbolReference, Symbol> resolveSymbol,
    IReadOnlyDictionary<FunctionSymbol, XelibBodyNode> lambdaBodies) : IGenericStructImplementation
{
    public BoundFunction? PortableInstanceInitializer => null;
    public ImmutableArray<BoundFunction> PortableStaticFieldInitializers => [];

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
            initializerBody, resolveType, resolveSymbol, lambdaBodies);
        return new BoundFunction(initializer, implementation.Bind(initializer, substitutions,
            diagnostics, constants, functionSpecializer, cancellationToken));
    }

    public ImmutableArray<BoundFunction> BindStaticFieldInitializers(StructTypeSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        DiagnosticBag diagnostics, ConstantEvaluationContext constants,
        SemanticInfoStore semanticInfo, GenericFunctionSpecializer functionSpecializer,
        GenericStructImplementationServices services, CancellationToken cancellationToken)
    {
        var functions = ImmutableArray.CreateBuilder<BoundFunction>();
        foreach (var entry in staticInitializers)
        {
            int ordinal = entry.Field.Ordinal;
            FieldSymbol? field = specialization.StaticFields.FirstOrDefault(candidate =>
                candidate.Ordinal == ordinal && candidate.Name == entry.Field.Name);
            if (field is null) continue;
            var initializer = new FunctionSymbol(field);
            var implementation = new LibraryGenericFunctionImplementation(entry.Definition,
                entry.Body, resolveType, resolveSymbol, lambdaBodies);
            functions.Add(new BoundFunction(initializer, implementation.Bind(initializer,
                substitutions, diagnostics, constants, functionSpecializer, cancellationToken)));
        }
        return functions.ToImmutable();
    }

    public bool EvaluateConstant(ConstantSymbol specialization,
        IReadOnlyDictionary<GenericParameterSymbol, TypeSymbol> substitutions,
        GenericStructSpecializer structSpecializer, ConstantEvaluationContext constants,
        SemanticInfoStore semanticInfo,
        GenericStructImplementationServices services)
    {
        if (specialization.GenericDefinition is not { } definition)
            return false;
        if (definition.HasValue)
        {
            specialization.SetValue(definition.Value);
            return true;
        }
        XelibBodyNode? payload = constantExpressions.FirstOrDefault(entry =>
            ReferenceEquals(entry.Constant, definition)).Expression;
        if (payload is null) return false;
        TypeSymbol Type(int id) => structSpecializer.Substitute(resolveType(id), substitutions);
        Symbol Symbol(XelibSymbolReference reference)
        {
            Symbol resolved = resolveSymbol(reference);
            if (resolved is FieldSymbol field && ReferenceEquals(field.ContainingType, definition.ContainingType) &&
                specialization.ContainingType is StructTypeSymbol specializedType)
                return specializedType.Fields.Concat(specializedType.StaticFields)
                    .First(candidate => candidate.Ordinal == field.Ordinal && candidate.Name == field.Name);
            return resolved;
        }
        BoundExpression expression = XelibBodyCodec.DecodeExpression(payload, Type, Symbol);
        ConstantFoldStatus status = SemanticAnalyzer.FoldConstantExpression(expression,
            out object? value, constants.TargetLayout);
        if (status == ConstantFoldStatus.Invalid) return false;
        specialization.SetBoundValue(expression);
        if (status == ConstantFoldStatus.Folded) specialization.SetValue(value);
        return true;
    }
}
