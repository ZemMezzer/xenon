using System.Collections.Immutable;
using System.Numerics;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed class SemanticAnalyzer
{
    private readonly ImmutableArray<SyntaxTree> _syntaxTrees;
    private readonly TypeFactory _typeFactory;
    private readonly ConstantEvaluationContext _constants;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly NamespaceSymbol _globalNamespace = new(string.Empty, null);
    private readonly Dictionary<SyntaxTree, NamespaceSymbol> _treeNamespaces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SyntaxTree, FileSymbolScope> _treeScopes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionDeclarationSyntax, FunctionSymbol> _functionSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructDeclarationSyntax, StructTypeSymbol> _structSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<InterfaceDeclarationSyntax, InterfaceTypeSymbol> _interfaceSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TemplateDeclarationSyntax, TemplateSymbol> _templateSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructDeclarationSyntax, FileSymbolScope> _structScopes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TemplateDeclarationSyntax, FileSymbolScope> _templateScopes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ConstantSymbol, FileSymbolScope> _constantScopes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ConstantSymbol> _evaluatingConstants = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ConstantSymbol> _failedConstants = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<EnumDeclarationSyntax, (EnumTypeSymbol Type, SyntaxTree Tree)> _enums = [];
    private readonly Dictionary<ConstantSymbol, (EnumTypeSymbol Type, ConstantSymbol? Previous, bool Automatic)> _enumMembers = [];
    private readonly List<(FunctionSymbol Symbol, BlockStatementSyntax Body, FileSymbolScope Scope)> _functionBodies = [];
    private readonly List<BoundFunction> _synthesizedFunctions = [];
    private readonly HashSet<StructTypeSymbol> _boundSpecializedInstanceInitializers = [];
    private readonly HashSet<FieldSymbol> _boundSpecializedStaticInitializers = [];
    private readonly HashSet<StructTypeSymbol> _validatedGenericLayouts = [];
    private readonly Dictionary<BoundExpression, TextLocation> _expressionLocations = new(ReferenceEqualityComparer.Instance);
    private readonly SemanticInfoStore _semanticInfo = new();
    private readonly CancellationToken _cancellationToken;
    private readonly ImmutableArray<NamespaceSymbol> _referencedNamespaces;
    private GenericStructSpecializer? _genericStructSpecializer;

    private SemanticAnalyzer(ImmutableArray<SyntaxTree> syntaxTrees, TypeFactory typeFactory,
        ImmutableArray<NamespaceSymbol> referencedNamespaces, ITargetTypeLayout? targetLayout,
        CancellationToken cancellationToken)
    {
        _syntaxTrees = syntaxTrees;
        _typeFactory = typeFactory;
        _referencedNamespaces = referencedNamespaces;
        _constants = new ConstantEvaluationContext(targetLayout);
        _cancellationToken = cancellationToken;
    }

    public static SemanticModel Analyze(ImmutableArray<SyntaxTree> syntaxTrees, TypeFactory typeFactory,
        ImmutableArray<NamespaceSymbol> referencedNamespaces = default,
        ITargetTypeLayout? targetLayout = null, CancellationToken cancellationToken = default)
    {
        var analyzer = new SemanticAnalyzer(syntaxTrees, typeFactory,
            referencedNamespaces.IsDefault ? [] : referencedNamespaces, targetLayout, cancellationToken);
        return analyzer.Analyze();
    }

    private SemanticModel Analyze()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        foreach (NamespaceSymbol referencedNamespace in _referencedNamespaces)
            _globalNamespace.ImportPublicMembers(referencedNamespace);
        DeclareNamespaces();
        DeclareTemplates();
        DeclareStructs();
        DeclareInterfaces();
        DeclareEnums();
        BindUsingDirectives();
        BindStructGenericConstraints();
        InitializeGenericStructSpecializer();
        DeclareTemplateMembers();
        BindTypeInheritance();
        ValidateInheritanceCycles();
        MarkVirtualDispatchRequirements();
        DeclareInterfaceMethods();
        ValidateInheritedInterfaceMembers();
        AssignInterfaceMethodSlots();
        BindStructFields();
        _genericStructSpecializer!.CompleteFields();
        ValidateStructLayouts();
        DeclareConstants();
        DeclareEnumMembers();
        // Invalid by-value layouts must not be queried through a native ABI provider.
        if (_diagnostics.Count != 0) _constants.TargetLayout = null;
        EvaluateConstants();
        _genericStructSpecializer!.CompleteConstants();
        BindStaticFieldInitializers();
        DeclareStructProperties();
        DeclareStructIndexers();
        DeclareStructMethods();
        DeclareStructLifecycleFunctions();
        foreach (StructTypeSymbol type in _structSymbols.Values)
            if (type.Destructor is null && type.BaseType?.FindDestructor() is { IsPublic: false } inheritedDestructor)
                _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                    $"destructor '{inheritedDestructor.ContainingType!.Name}' is private",
                    DiagnosticIds.InaccessibleSymbol);
        BuildVirtualMethodTables();
        ValidateInterfaceImplementations();
        _genericStructSpecializer!.CompleteMembers();
        DeclareFunctions();
        ValidateAbstractValueStorage();
        BindInstanceFieldInitializers();
        ValidateNativeSymbols();

        var genericDefinitions = _functionBodies
            .Where(entry => entry.Symbol.FunctionKind == FunctionKind.Ordinary && !entry.Symbol.TypeParameters.IsEmpty)
            .ToDictionary(entry => entry.Symbol, entry => (entry.Body, entry.Scope));
        var genericSpecializer = new GenericFunctionSpecializer(genericDefinitions, _typeFactory,
            _diagnostics, _constants, _genericStructSpecializer!, _cancellationToken);
        StabilizeConstructorReferenceSummaries();
        var functions = ImmutableArray.CreateBuilder<BoundFunction>();
        foreach ((FunctionSymbol symbol, BlockStatementSyntax body, FileSymbolScope scope) in _functionBodies)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var binder = new FunctionBodyBinder(symbol, scope, _diagnostics, _constants, _semanticInfo,
                genericSpecializer, _cancellationToken);
            BoundBlockStatement boundBody = binder.BindBody(body);
            // Generic definitions are checked now, but only concrete specializations may
            // enter the emitted function set.
            if (!symbol.IsGenericDefinition)
                functions.Add(new BoundFunction(symbol, boundBody));
            foreach (var entry in binder.ExpressionLocations) _expressionLocations.TryAdd(entry.Key, entry.Value);
        }
        StabilizeCallableSummaries();
        BindSpecializedStructFunctions(functions, genericSpecializer);
        ValidateCallableMoveEffects();
        functions.AddRange(genericSpecializer.Functions);
        functions.AddRange(_synthesizedFunctions);
        AddGeneratedDestructorFunctions(functions);

        // Lifecycle/accessor checks need all bodies, including declarations that
        // occur after the readonly caller and synthesized field initializers.
        var bodies = functions.ToDictionary(bound => bound.Symbol, bound => bound.Body);
        ImmutableArray<StructTypeSymbol> types = [.. _structSymbols.Values];
        foreach ((FunctionSymbol symbol, BlockStatementSyntax body, _) in _functionBodies)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (symbol.IsGenericDefinition) continue;
            if (symbol.IsReadonly)
                new ReadonlyEffectAnalyzer(symbol, _diagnostics, _expressionLocations,
                    body.OpenBraceToken.Location, bodies, types, _cancellationToken).Analyze(bodies[symbol]);
        }

        RecordDeclarations(_globalNamespace);
        return new SemanticModel(_globalNamespace, _typeFactory, functions.ToImmutable(), _diagnostics.ToImmutableArray(),
            _syntaxTrees, _semanticInfo, _constants.RequiresTargetLayout);
    }

    private void StabilizeCallableSummaries()
    {
        DiagnosticBag lastDiagnostics = new();
        for (int round = 0; round <= _functionBodies.Count; round++)
        {
            PropagateGenericStructCallableSummaries();
            string before = GetCallableSummaryFingerprint();
            var diagnostics = new DiagnosticBag();
            foreach ((FunctionSymbol symbol, BlockStatementSyntax body, FileSymbolScope scope) in _functionBodies)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var binder = new FunctionBodyBinder(symbol, scope, diagnostics, _constants,
                    new SemanticInfoStore(), _cancellationToken);
                _ = binder.BindBody(body);
            }
            PropagateGenericStructCallableSummaries();
            lastDiagnostics = diagnostics;
            string after = GetCallableSummaryFingerprint();
            if (string.Equals(before, after, StringComparison.Ordinal)) break;
        }

        HashSet<string> callableSummaryDiagnosticIds =
            [DiagnosticIds.UseAfterMove, DiagnosticIds.PartiallyMovedUse,
                DiagnosticIds.InconsistentReceiverMoveEffect, DiagnosticIds.EscapingLocalReference];
        foreach (Diagnostic diagnostic in lastDiagnostics.Where(diagnostic => callableSummaryDiagnosticIds.Contains(diagnostic.Id)))
        {
            bool duplicate = _diagnostics.Any(existing =>
                existing.Id == diagnostic.Id && existing.Message == diagnostic.Message &&
                existing.Location.Span.Equals(diagnostic.Location.Span));
            if (!duplicate) _diagnostics.AddRange([diagnostic]);
        }
    }

    private void StabilizeConstructorReferenceSummaries()
    {
        var constructors = _functionBodies
            .Where(entry => entry.Symbol.FunctionKind == FunctionKind.Constructor &&
                            entry.Symbol.ContainingStruct is { } owner &&
                            TypeFacts.ContainsReferenceStorage(owner))
            .ToArray();
        for (int round = 0; round <= constructors.Length; round++)
        {
            PropagateGenericStructCallableSummaries();
            string before = string.Join('|', constructors.Select(entry => string.Join(';',
                entry.Symbol.ReferenceFieldOrigins.Select(origin =>
                    $"{string.Join(',', origin.FieldOrdinals)}:{(int)origin.Origin.Kind}:" +
                    $"{origin.Origin.ParameterOrdinal}:{string.Join(',', origin.Origin.FieldOrdinals)}:{origin.IsReadonly}"))));
            foreach ((FunctionSymbol symbol, BlockStatementSyntax body, FileSymbolScope scope) in constructors)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var binder = new FunctionBodyBinder(symbol, scope, new DiagnosticBag(), _constants,
                    new SemanticInfoStore(), _cancellationToken);
                _ = binder.BindBody(body);
            }
            PropagateGenericStructCallableSummaries();
            string after = string.Join('|', constructors.Select(entry => string.Join(';',
                entry.Symbol.ReferenceFieldOrigins.Select(origin =>
                    $"{string.Join(',', origin.FieldOrdinals)}:{(int)origin.Origin.Kind}:" +
                    $"{origin.Origin.ParameterOrdinal}:{string.Join(',', origin.Origin.FieldOrdinals)}:{origin.IsReadonly}"))));
            if (string.Equals(before, after, StringComparison.Ordinal)) break;
        }
    }

    private string GetCallableSummaryFingerprint() => string.Join('|', _functionBodies.Select(entry =>
        $"{string.Join(';', entry.Symbol.ReceiverMoveEffects.Select(effect => string.Join(',', effect.FieldOrdinals)))}" +
        $"/{string.Join(';', entry.Symbol.ReferenceReturnOrigins.Select(origin =>
            $"{(int)origin.Kind}:{origin.ParameterOrdinal}:{string.Join(',', origin.FieldOrdinals)}"))}" +
        $"/{string.Join(';', entry.Symbol.SharedReturnOrigins.Select(origin =>
            $"{(int)origin.Kind}:{origin.ParameterOrdinal}"))}" +
        $"/{string.Join(';', entry.Symbol.ReferenceFieldOrigins.Select(origin =>
            $"{string.Join(',', origin.FieldOrdinals)}:{(int)origin.Origin.Kind}:" +
            $"{origin.Origin.ParameterOrdinal}:{string.Join(',', origin.Origin.FieldOrdinals)}:{origin.IsReadonly}"))}"));

    private void PropagateGenericStructCallableSummaries()
    {
        if (_genericStructSpecializer is null) return;
        foreach (SpecializedStructFunction entry in _genericStructSpecializer.SpecializedFunctions)
        {
            entry.Specialized.SetReceiverMoveEffects(entry.Definition.ReceiverMoveEffects);
            entry.Specialized.SetReferenceReturnOrigins(entry.Definition.ReferenceReturnOrigins);
            entry.Specialized.SetSharedReturnOrigins(entry.Definition.SharedReturnOrigins);
            entry.Specialized.SetReferenceFieldOrigins(entry.Definition.ReferenceFieldOrigins);
        }
    }

    private void ValidateCallableMoveEffects()
    {
        IEnumerable<StructTypeSymbol> types = _structSymbols.Values;
        if (_genericStructSpecializer is not null)
            types = types.Concat(_genericStructSpecializer.Specializations);

        var validated = new HashSet<(FunctionSymbol Contract, FunctionSymbol Implementation)>();
        foreach (StructTypeSymbol type in types.Distinct())
        {
            foreach (InterfaceTypeSymbol @interface in type.ImplementedInterfaces)
            {
                foreach (FunctionSymbol contract in @interface.AllMethods)
                {
                    FunctionSymbol? implementation = type.FindInterfaceImplementation(contract);
                    if (implementation is null || !validated.Add((contract, implementation))) continue;
                    ValidateMoveEffectContract(contract, implementation,
                        $"interface method '{@interface.Name}.{contract.Name}'",
                        contract.ReceiverMoveEffects);
                }
            }

            foreach (FunctionSymbol implementation in type.Methods.Where(method => method.IsOverride))
            {
                FunctionSymbol? contract = type.BaseType?.VirtualMethods.FirstOrDefault(candidate =>
                    candidate.VTableSlot == implementation.VTableSlot && candidate.HasSameSignature(implementation));
                if (contract is null || !validated.Add((contract, implementation))) continue;
                ValidateMoveEffectContract(contract, implementation,
                    $"virtual method '{contract.ContainingType!.Name}.{contract.Name}'",
                    contract.ReceiverMoveEffects);
            }

            // Until source-level effect declarations exist, a virtual method's
            // dispatch contract declares no destructive receiver effect.  Keep
            // the inferred summary intact, but validate it against that empty
            // public contract in the same centralized compatibility routine.
            foreach (FunctionSymbol implementation in type.Methods.Where(method =>
                         method.IsVirtual && !method.IsOverride))
            {
                if (!validated.Add((implementation, implementation))) continue;
                ValidateMoveEffectContract(implementation, implementation,
                    $"virtual method contract '{implementation.ContainingType!.Name}.{implementation.Name}'", []);
            }
        }
    }

    private void ValidateMoveEffectContract(
        FunctionSymbol contract,
        FunctionSymbol implementation,
        string contractDisplay,
        ImmutableArray<ReceiverMoveEffect> contractEffects)
    {
        ImmutableArray<ReceiverMoveEffect> implementationEffects = implementation.ReceiverMoveEffects;
        if (AreMoveEffectsCompatible(contractEffects, implementationEffects)) return;

        ReceiverMoveEffect? incompatible = implementationEffects.FirstOrDefault(effect =>
            !contractEffects.Any(candidate => SameMoveEffect(candidate, effect)));
        string place = incompatible is { } effect
            ? FormatReceiverMoveEffect(implementation, effect)
            : "this";
        _diagnostics.Report(MemberLocation(implementation),
            $"method '{implementation.ContainingType!.Name}.{implementation.Name}' has caller-visible receiver move effects that are not compatible with {contractDisplay}; incompatible receiver place: '{place}'",
            DiagnosticIds.HiddenVirtualMoveEffect,
            contract.Locations.Select(location =>
                new RelatedDiagnosticLocation(location, "callable contract declared here")));
    }

    private static bool AreMoveEffectsCompatible(
        ImmutableArray<ReceiverMoveEffect> contract,
        ImmutableArray<ReceiverMoveEffect> implementation) =>
        implementation.All(effect => contract.Any(candidate => SameMoveEffect(candidate, effect)));

    private static bool SameMoveEffect(ReceiverMoveEffect left, ReceiverMoveEffect right) =>
        left.FieldOrdinals.SequenceEqual(right.FieldOrdinals);

    private static string FormatReceiverMoveEffect(FunctionSymbol method, ReceiverMoveEffect effect)
    {
        TypeSymbol? current = method.ContainingType;
        var names = new List<string> { "this" };
        foreach (int ordinal in effect.FieldOrdinals)
        {
            if (current is not StructTypeSymbol structure ||
                structure.Fields.FirstOrDefault(field => field.Ordinal == ordinal) is not { } field)
                return string.Join('.', names);
            names.Add(field.Name);
            current = field.Type;
        }
        return string.Join('.', names);
    }

    private void AddGeneratedDestructorFunctions(ImmutableArray<BoundFunction>.Builder functions)
    {
        var existing = functions.Select(function => function.Symbol).ToHashSet(ReferenceEqualityComparer.Instance);
        void Visit(NamespaceSymbol @namespace)
        {
            foreach (StructTypeSymbol type in @namespace.Structs.Where(type => type.IsConcreteType))
            {
                Symbol root = type;
                while (root.ContainingSymbol is not null) root = root.ContainingSymbol;
                if (!ReferenceEquals(root, _globalNamespace)) continue;
                if (type.CompleteDestructor is not { FunctionKind: FunctionKind.DestructorGlue } destructor || !existing.Add(destructor))
                    continue;
                functions.Add(new BoundFunction(destructor, new BoundBlockStatement([
                    new BoundExpressionStatement(new BoundDestroyFieldsExpression(type)),
                ])));
            }
            foreach (NamespaceSymbol child in @namespace.Namespaces) Visit(child);
        }
        Visit(_globalNamespace);

        foreach (OwnershipTypeSymbol ownership in _typeFactory.OwnershipTypes)
        {
            if (GenericTypeFacts.ContainsGenericParameter(ownership) ||
                ownership.CompleteDestructor is not { } destructor ||
                !existing.Add(destructor))
                continue;
            FunctionSymbol? elementDestructor = ownership is WeakTypeSymbol ? null : ownership.ElementType switch
            {
                ArrayTypeSymbol array => TypeFacts.GetCompleteDestructor(array.ElementType),
                _ => TypeFacts.GetCompleteDestructor(ownership.ElementType),
            };
            functions.Add(new BoundFunction(destructor, new BoundBlockStatement([
                new BoundExpressionStatement(new BoundOwnershipDestructionExpression(ownership, elementDestructor)),
            ])));
        }

        foreach (StorageTypeSymbol storage in _typeFactory.StorageTypes)
        {
            if (GenericTypeFacts.ContainsGenericParameter(storage) ||
                storage.CompleteDestructor is not { } destructor ||
                !existing.Add(destructor))
                continue;
            functions.Add(new BoundFunction(destructor, new BoundBlockStatement([
                new BoundExpressionStatement(new BoundStorageDestructionExpression(
                    storage, TypeFacts.GetCompleteDestructor(storage.ElementType))),
            ])));
        }
    }

    private void InitializeGenericStructSpecializer()
    {
        _genericStructSpecializer = new GenericStructSpecializer(
            _typeFactory, _diagnostics, EvaluateSpecializedConstants);
        foreach (FileSymbolScope scope in _treeScopes.Values.Concat(_structScopes.Values)
            .Concat(_templateScopes.Values).Distinct())
            scope.SetGenericStructSpecializer(_genericStructSpecializer);
    }

    private void BindSpecializedStructFunctions(ImmutableArray<BoundFunction>.Builder functions,
        GenericFunctionSpecializer genericFunctionSpecializer)
    {
        if (_genericStructSpecializer is null) return;
        Dictionary<FunctionSymbol, (BlockStatementSyntax Body, FileSymbolScope Scope)> sources =
            _functionBodies.ToDictionary(entry => entry.Symbol, entry => (entry.Body, entry.Scope));
        var bound = new HashSet<FunctionSymbol>(ReferenceEqualityComparer.Instance);
        while (true)
        {
            int specializationCountBefore = _genericStructSpecializer.SpecializationCount;
            bool changed = _genericStructSpecializer.CompletePendingConstraints();
            changed |= BindPendingSpecializedInitializers(functions, genericFunctionSpecializer);
            changed |= ValidatePendingGenericSpecializationLayouts();
            SpecializedStructFunction[] pending = _genericStructSpecializer.SpecializedFunctions
                .Where(entry => entry.Owner.IsConcreteType && !bound.Contains(entry.Specialized)).ToArray();
            foreach (SpecializedStructFunction entry in pending)
            {
                entry.Specialized.SetReceiverMoveEffects(entry.Definition.ReceiverMoveEffects);
                entry.Specialized.SetReferenceReturnOrigins(entry.Definition.ReferenceReturnOrigins);
                entry.Specialized.SetSharedReturnOrigins(entry.Definition.SharedReturnOrigins);
                entry.Specialized.SetReferenceFieldOrigins(entry.Definition.ReferenceFieldOrigins);
            }
            foreach (SpecializedStructFunction entry in pending)
            {
                changed = true;
                FunctionSymbol definition = entry.Definition;
                FunctionSymbol specialized = entry.Specialized;
                bound.Add(specialized);
                if (!sources.TryGetValue(definition, out var source)) continue;
                StructTypeSymbol owner = specialized.ContainingStruct!;
                var semanticInfo = new SemanticInfoStore();
                FileSymbolScope scope = source.Scope.WithTypeSubstitutions(
                    _genericStructSpecializer.GetSubstitutions(owner), semanticInfo);
                var binder = new FunctionBodyBinder(specialized, scope, _diagnostics, _constants,
                    semanticInfo, genericFunctionSpecializer, _cancellationToken);
                functions.Add(new BoundFunction(specialized, binder.BindBody(source.Body)));
            }
            if (_genericStructSpecializer.SpecializationCount != specializationCountBefore)
                changed = true;
            if (!changed) break;
        }
        _genericStructSpecializer.ReportUnresolvedConstraints();
    }

    private bool ValidatePendingGenericSpecializationLayouts()
    {
        if (_genericStructSpecializer is null) return false;
        StructTypeSymbol[] pending = _genericStructSpecializer.Specializations
            .Where(type => type.IsConcreteType && _validatedGenericLayouts.Add(type)).ToArray();
        foreach (StructTypeSymbol type in pending)
        {
            foreach (FieldSymbol field in type.StaticFields)
                if (field.Declaration.Initializer is null && TypeFacts.ContainsReferenceStorage(field.Type))
                    _diagnostics.Report(field.Declaration.Type.NameToken.Location,
                        $"static field '{field.Name}' contains a reference and requires explicit initialization",
                        DiagnosticIds.ReferenceRequiresInitializer);
            foreach (FieldSymbol field in type.Fields)
            {
                if (ContainsStructByValue(field.Type, type, []))
                    _diagnostics.Report(field.Declaration.Type.NameToken.Location,
                        $"struct '{type.Name}' has a recursive by-value field '{field.Name}'; use a pointer or array handle instead",
                        DiagnosticIds.RecursiveValueLayout);
                if (field.Type is StructTypeSymbol { IsAbstract: true } abstractType)
                    _diagnostics.Report(field.Declaration.Type.NameToken.Location,
                        $"abstract struct '{abstractType.Name}' cannot be stored in field '{field.Name}'",
                        DiagnosticIds.AbstractValueStorage);
            }
        }
        return pending.Length > 0;
    }

    private bool BindPendingSpecializedInitializers(ImmutableArray<BoundFunction>.Builder functions,
        GenericFunctionSpecializer genericFunctionSpecializer)
    {
        if (_genericStructSpecializer is null) return false;
        bool changed = false;
        foreach (StructTypeSymbol type in _genericStructSpecializer.Specializations
            .Where(type => type.IsConcreteType).ToArray())
        {
            StructTypeSymbol definition = type.GenericDefinition!;
            FileSymbolScope sourceScope = _structScopes[definition.Declaration];
            var semanticInfo = new SemanticInfoStore();
            FileSymbolScope scope = sourceScope.WithTypeSubstitutions(
                _genericStructSpecializer.GetSubstitutions(type), semanticInfo);

            if (type.Fields.Any(field => field.Declaration.Initializer is not null) &&
                _boundSpecializedInstanceInitializers.Add(type))
            {
                changed = true;
                var initializer = new FunctionSymbol(FunctionKind.InstanceInitializer, type, [],
                    type.Declaration, Accessibility.Private);
                type.SetInstanceInitializer(initializer);
                var binder = new FunctionBodyBinder(initializer, scope, _diagnostics, _constants,
                    semanticInfo, genericFunctionSpecializer, _cancellationToken);
                foreach (FieldSymbol field in type.Fields)
                    if (binder.BindFieldInitializer(field) is BoundExpression boundInitializer)
                        field.SetInitializer(boundInitializer);
                functions.Add(new BoundFunction(initializer,
                    new BoundBlockStatement(binder.CreateInstanceFieldInitializerStatements(type))));
            }

            foreach (FieldSymbol field in type.StaticFields.Where(field =>
                field.Declaration.Initializer is not null && _boundSpecializedStaticInitializers.Add(field)))
            {
                changed = true;
                BindStaticFieldInitializer(field, type, scope);
            }
        }
        return changed;
    }

    private void BindInstanceFieldInitializers()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            if (type.IsGenericDefinition) continue;
            if (!type.Fields.Any(field => field.Declaration.Initializer is not null))
                continue;

            var initializer = new FunctionSymbol(
                FunctionKind.InstanceInitializer,
                type,
                [],
                declaration,
                Accessibility.Private);
            type.SetInstanceInitializer(initializer);
            var binder = new FunctionBodyBinder(
                initializer,
                _structScopes[declaration],
                _diagnostics,
                _constants,
                _semanticInfo,
                _cancellationToken);

            foreach (FieldSymbol field in type.Fields)
            {
                if (binder.BindFieldInitializer(field) is BoundExpression boundInitializer)
                    field.SetInitializer(boundInitializer);
            }
            foreach (var entry in binder.ExpressionLocations) _expressionLocations.TryAdd(entry.Key, entry.Value);

            _synthesizedFunctions.Add(new BoundFunction(
                initializer,
                new BoundBlockStatement(binder.CreateInstanceFieldInitializerStatements(type))));
        }
    }

    private void DeclareNamespaces()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            NamespaceSymbol current = _globalNamespace;
            NamespaceDeclarationSyntax declaration = tree.Root.Namespace;
            for (int index = 0; index < declaration.NameParts.Length; index++)
            {
                current = current.GetOrAddNamespace(declaration.NameParts[index].Text);
                current.AddDeclaration(declaration, index);
            }

            _treeNamespaces.Add(tree, current);
            _semanticInfo.Declarations[tree.Root.Namespace] = current;
        }
    }

    private void DeclareStructs()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            foreach (StructDeclarationSyntax declaration in tree.Root.Members.OfType<StructDeclarationSyntax>())
            {
                var type = new StructTypeSymbol(declaration.IdentifierToken.Text, @namespace, declaration);
                type.SetTypeParameters(CreateGenericParameters(declaration.TypeParameters, type));
                if (!@namespace.TryDeclareType(type))
                {
                    DeclaredTypeSymbol? previous = @namespace.FindAnyType(type.Name);
                    _diagnostics.Report(
                        declaration.IdentifierToken.Location,
                        $"type '{@namespace.FullName}.{type.Name}' is already declared",
                        DiagnosticIds.DuplicateDeclaration,
                        previous?.Locations.Select(location => new RelatedDiagnosticLocation(location, "previous declaration")));
                    continue;
                }

                _structSymbols.Add(declaration, type);
                _semanticInfo.TypeRegions.Add(new TypeRegion(
                    declaration.OpenBraceToken.Location.Source,
                    TextSpan.FromBounds(declaration.OpenBraceToken.Location.Span.Start,
                        Math.Max(declaration.OpenBraceToken.Location.Span.Start, declaration.CloseBraceToken.Location.Span.End)),
                    type,
                    declaration.CloseBraceToken.IsMissing));
            }
        }
    }

    private void DeclareTemplates()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            foreach (TemplateDeclarationSyntax declaration in tree.Root.Members.OfType<TemplateDeclarationSyntax>())
            {
                var template = new TemplateSymbol(declaration.IdentifierToken.Text, @namespace, declaration);
                if (!@namespace.TryDeclareTemplate(template))
                {
                    _diagnostics.Report(declaration.IdentifierToken.Location,
                        $"type or template '{@namespace.FullName}.{template.Name}' is already declared",
                        DiagnosticIds.DuplicateDeclaration);
                    continue;
                }
                _templateSymbols.Add(declaration, template);
            }
        }
    }

    private void DeclareEnums()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        foreach (EnumDeclarationSyntax declaration in tree.Root.Members.OfType<EnumDeclarationSyntax>())
        {
            var type = new EnumTypeSymbol(declaration.IdentifierToken.Text, _treeNamespaces[tree], declaration);
            if (!type.ContainingNamespace.TryDeclareType(type))
                _diagnostics.Report(declaration.IdentifierToken.Location, $"type '{type.FullName}' is already declared",
                    DiagnosticIds.DuplicateDeclaration);
            else
                _enums.Add(declaration, (type, tree));
        }
    }

    private void DeclareEnumMembers()
    {
        foreach ((EnumDeclarationSyntax syntax, (EnumTypeSymbol type, SyntaxTree tree)) in _enums)
        {
            TypeSymbol underlying = syntax.UnderlyingType is null ? BuiltinTypes.Int : TypeResolver.Resolve(syntax.UnderlyingType, _treeScopes[tree], _diagnostics);
            if (underlying is PrimitiveTypeSymbol { IsInteger: true } integer)
                type.UnderlyingType = integer;
            else
                _diagnostics.Report(syntax.IdentifierToken.Location, "enum underlying type must be an integer type",
                    DiagnosticIds.InvalidEnumUnderlyingType);
            var members = ImmutableArray.CreateBuilder<ConstantSymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            ConstantSymbol? previous = null;
            foreach (EnumMemberDeclarationSyntax member in syntax.Members)
            {
                if (!names.Add(member.IdentifierToken.Text))
                {
                    _diagnostics.Report(member.IdentifierToken.Location, $"duplicate enum member '{member.IdentifierToken.Text}'",
                        DiagnosticIds.DuplicateEnumMember);
                    continue;
                }
                ExpressionSyntax initializer = member.Value ?? new LiteralExpressionSyntax(
                    new SyntaxToken(SyntaxKind.IntegerLiteralToken, member.IdentifierToken.Location, "0", 0UL));
                var constant = new ConstantSymbol(member.IdentifierToken.Text, type, type, initializer, member);
                _constantScopes.Add(constant, _treeScopes[tree]);
                _enumMembers.Add(constant, (type, previous, member.Value is null));
                members.Add(constant);
                previous = constant;
            }
            type.Members = members.ToImmutable();
        }
    }

    private bool EvaluateEnumMember(ConstantSymbol member, EnumTypeSymbol type, ConstantSymbol? previous, bool automatic)
    {
        object? value;
        if (automatic)
        {
            if (previous is not null && !EvaluateConstant(previous)) return false;
            if (previous?.BoundValue is BoundDeferredConstantExpression)
            {
                member.SetBoundValue(new BoundDeferredConstantExpression(type));
                return true;
            }
            BigInteger number = previous is null ? BigInteger.Zero : ToInteger(previous.Value) + 1;
            if (!FitsInteger(number, type.UnderlyingType, _constants.TargetLayout))
            {
                _diagnostics.Report(member.IdentifierToken.Location, $"enum value is out of range for '{type.UnderlyingType.Name}'",
                    DiagnosticIds.EnumValueOutOfRange);
                return false;
            }
            value = IntegerValue(number, type.UnderlyingType, _constants.TargetLayout);
        }
        else
        {
            BoundExpression? expression = BindConstantExpression(member.Initializer, member);
            if (expression is null || !(TypeFacts.IsInteger(expression.Type) || TypeIdentity.AreSame(expression.Type, type)))
            {
                _diagnostics.Report(member.IdentifierToken.Location, "enum value must be an integer compile-time constant",
                    DiagnosticIds.EnumConstantRequired);
                return false;
            }
            ConstantFoldStatus status = _constants.Fold(expression, out value);
            if (status == ConstantFoldStatus.TargetDependent)
            {
                member.SetBoundValue(new BoundDeferredConstantExpression(type));
                return true;
            }
            if (status == ConstantFoldStatus.Invalid)
            {
                _diagnostics.Report(member.IdentifierToken.Location, "enum value must be an integer compile-time constant with valid operations",
                    DiagnosticIds.InvalidConstantOperation);
                return false;
            }
            BigInteger number = ToInteger(value);
            if (!FitsInteger(number, type.UnderlyingType, _constants.TargetLayout))
            {
                _diagnostics.Report(member.IdentifierToken.Location, $"enum value is out of range for '{type.UnderlyingType.Name}'",
                    DiagnosticIds.EnumValueOutOfRange);
                return false;
            }
            value = IntegerValue(number, type.UnderlyingType, _constants.TargetLayout);
        }
        member.SetValue(value);
        member.SetBoundValue(new BoundLiteralExpression(value, type));
        return true;
    }

    internal static BigInteger ToInteger(object? value) => value switch
    {
        int number => number,
        long number => number,
        ulong number => number,
        _ => throw new InvalidOperationException("Expected an integer constant."),
    };

    internal static bool FitsInteger(BigInteger value, PrimitiveTypeSymbol type, ITargetTypeLayout? targetLayout = null)
    {
        int bits = type.BitWidth ?? targetLayout?.GetIntegerBitWidth(type) ?? 64;
        return type.IsSigned
            ? value >= -(BigInteger.One << (bits - 1)) && value < (BigInteger.One << (bits - 1))
            : value >= 0 && value < (BigInteger.One << bits);
    }

    internal static object IntegerValue(BigInteger value, PrimitiveTypeSymbol type, ITargetTypeLayout? targetLayout = null)
    {
        int bits = type.BitWidth ?? targetLayout?.GetIntegerBitWidth(type) ?? 64;
        if (!type.IsSigned && bits >= 32) return (ulong)value;
        if (bits > 32) return (long)value;
        return (int)value;
    }

    private void DeclareInterfaces()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            foreach (InterfaceDeclarationSyntax declaration in tree.Root.Members.OfType<InterfaceDeclarationSyntax>())
            {
                var type = new InterfaceTypeSymbol(declaration.IdentifierToken.Text, @namespace, declaration);
                if (!@namespace.TryDeclareType(type))
                {
                    _diagnostics.Report(declaration.IdentifierToken.Location, $"type '{@namespace.FullName}.{type.Name}' is already declared",
                        DiagnosticIds.DuplicateDeclaration);
                    continue;
                }
                _interfaceSymbols.Add(declaration, type);
                _semanticInfo.TypeRegions.Add(new TypeRegion(
                    declaration.OpenBraceToken.Location.Source,
                    TextSpan.FromBounds(declaration.OpenBraceToken.Location.Span.Start,
                        Math.Max(declaration.OpenBraceToken.Location.Span.Start, declaration.CloseBraceToken.Location.Span.End)),
                    type,
                    declaration.CloseBraceToken.IsMissing));
            }
        }
    }

    private void BindUsingDirectives()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            var scope = new FileSymbolScope(_globalNamespace, _treeNamespaces[tree], _typeFactory, _semanticInfo);
            scope.BindUsings(tree.Root.Usings, _diagnostics);
            _treeScopes.Add(tree, scope);
            _semanticInfo.FileScopes[tree.Source] = scope;

            foreach (StructDeclarationSyntax declaration in tree.Root.Members.OfType<StructDeclarationSyntax>())
            {
                if (_structSymbols.ContainsKey(declaration))
                {
                    _structScopes.Add(declaration, scope.WithTypeParameters(_structSymbols[declaration].TypeParameters));
                }
            }

            foreach (TemplateDeclarationSyntax declaration in tree.Root.Members.OfType<TemplateDeclarationSyntax>())
                if (_templateSymbols.TryGetValue(declaration, out TemplateSymbol? template))
                    _templateScopes.Add(declaration, scope.WithTemplateSelf(template));
        }
    }

    private void BindStructGenericConstraints()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
            BindGenericConstraints(declaration.WhereClauses, type.TypeParameters, _structScopes[declaration]);
    }

    private void DeclareTemplateMembers()
    {
        foreach ((TemplateDeclarationSyntax declaration, TemplateSymbol template) in _templateSymbols)
        {
            FileSymbolScope scope = _templateScopes[declaration];
            var members = ImmutableArray.CreateBuilder<TemplateMemberRequirementSymbol>();
            foreach (TypeMemberDeclarationSyntax member in declaration.Members)
            {
                switch (member)
                {
                    case MethodDeclarationSyntax method:
                    {
                        if (method.IsStatic)
                        {
                            _diagnostics.Report(method.StaticKeyword!.Location,
                                "static structural template requirements are not supported yet",
                                DiagnosticIds.InvalidTemplateMember);
                            break;
                        }
                        TypeSymbol returnType = ResolveTemplateType(method.ReturnType, scope);
                        ImmutableArray<ParameterSymbol> parameters = BindTemplateParameters(method.Parameters, scope);
                        members.Add(new TemplateMethodRequirementSymbol(template, returnType, parameters,
                            TemplateAccessibility(method.AccessModifierToken), method));
                        break;
                    }
                    case PropertyDeclarationSyntax property:
                        if (property.IsStatic)
                        {
                            _diagnostics.Report(property.StaticKeyword!.Location,
                                "static structural template requirements are not supported yet",
                                DiagnosticIds.InvalidTemplateMember);
                            break;
                        }
                        members.Add(new TemplatePropertyRequirementSymbol(template,
                            ResolveTemplateType(property.Type, scope),
                            TemplateAccessibility(property.AccessModifierToken), property));
                        break;
                    case IndexerDeclarationSyntax indexer:
                        if (indexer.IsStatic)
                        {
                            _diagnostics.Report(indexer.StaticKeyword!.Location,
                                "static structural template requirements are not supported yet",
                                DiagnosticIds.InvalidTemplateMember);
                            break;
                        }
                        members.Add(new TemplateIndexerRequirementSymbol(template,
                            ResolveTemplateType(indexer.Type, scope),
                            BindTemplateParameters(indexer.Parameters, scope),
                            TemplateAccessibility(indexer.AccessModifierToken), indexer));
                        break;
                    case TemplateConstructorDeclarationSyntax constructor:
                        members.Add(new TemplateConstructorRequirementSymbol(template,
                            BindTemplateParameters(constructor.Parameters, scope),
                            TemplateAccessibility(constructor.AccessModifierToken), constructor));
                        break;
                }
            }
            template.SetMembers(members.ToImmutable());
        }
    }

    private TypeSymbol ResolveTemplateType(TypeSyntax syntax, FileSymbolScope scope)
        => TypeResolver.Resolve(syntax, scope, _diagnostics);

    private ImmutableArray<ParameterSymbol> BindTemplateParameters(
        ImmutableArray<ParameterSyntax> parameterSyntax, FileSymbolScope scope)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < parameterSyntax.Length; index++)
        {
            ParameterSyntax syntax = parameterSyntax[index];
            TypeSymbol type = ResolveTemplateType(syntax.Type, scope);
            if (!names.Add(syntax.IdentifierToken.Text))
                _diagnostics.Report(syntax.IdentifierToken.Location,
                    $"parameter '{syntax.IdentifierToken.Text}' is already declared", DiagnosticIds.DuplicateDeclaration);
            parameters.Add(new ParameterSymbol(syntax.IdentifierToken.Text, type, index,
                syntax.Type.IsBindingReadonly(), declaration: syntax));
        }
        return parameters.ToImmutable();
    }

    private static Accessibility TemplateAccessibility(SyntaxToken? modifier) =>
        modifier?.Kind == SyntaxKind.PrivateKeyword ? Accessibility.Private : Accessibility.Public;

    private void BindStructFields()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            var staticFields = ImmutableArray.CreateBuilder<FieldSymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (FieldDeclarationSyntax fieldSyntax in declaration.Fields)
            {
                TypeSymbol fieldType = TypeResolver.Resolve(
                    fieldSyntax.Type,
                    scope,
                    _diagnostics);
                if (TypeIdentity.AreSame(fieldType, BuiltinTypes.Void))
                {
                    _diagnostics.Report(fieldSyntax.Type.NameToken.Location, "field type cannot be 'void'",
                        DiagnosticIds.VoidFieldType);
                }

                if (!names.Add(fieldSyntax.IdentifierToken.Text))
                {
                    _diagnostics.Report(
                        fieldSyntax.IdentifierToken.Location,
                        $"field '{fieldSyntax.IdentifierToken.Text}' is already declared in struct '{type.Name}'",
                        DiagnosticIds.DuplicateDeclaration);
                }

                var field = new FieldSymbol(
                    fieldSyntax.IdentifierToken.Text,
                    type,
                    fieldType,
                    fieldSyntax.IsStatic ? staticFields.Count : type.DeclaredFieldStart + fields.Count,
                    fieldSyntax.IsPublic ? Accessibility.Public : Accessibility.Private,
                    fieldSyntax.IsStatic,
                    fieldSyntax.IsReadonly,
                    null,
                    fieldSyntax);
                if (fieldSyntax.IsStatic)
                    staticFields.Add(field);
                else
                    fields.Add(field);
            }

            type.SetFields(fields.ToImmutable());
            type.SetStaticFields(staticFields.ToImmutable());
        }
    }

    private void BindStaticFieldInitializers()
    {
        // Layout queries are safe only after every struct's fields and layout are known.
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in
            _structSymbols.Where(entry => !entry.Value.IsGenericDefinition))
        foreach (FieldSymbol field in type.StaticFields.Where(field => field.Declaration.Initializer is not null))
            BindStaticFieldInitializer(field, type, _structScopes[declaration]);
    }

    private void BindStaticFieldInitializer(FieldSymbol field, StructTypeSymbol type, FileSymbolScope scope)
    {
        FieldDeclarationSyntax syntax = field.Declaration;
        var context = new ConstantSymbol(field.Name, field.Type, type, syntax.Initializer!, syntax);
        _constantScopes.Add(context, scope);
        BoundExpression? initializer = BindConstantExpression(syntax.Initializer!, context);
        _constantScopes.Remove(context);
        object? value = null;
        ConstantFoldStatus status = initializer is null ? ConstantFoldStatus.Invalid : _constants.Fold(initializer, out value);
        TypeSymbol constantType = initializer?.Type ?? BuiltinTypes.Error;
        if (status == ConstantFoldStatus.Invalid)
            _diagnostics.Report(syntax.IdentifierToken.Location, "static field initializers must be compile-time constants",
                DiagnosticIds.ConstantValueRequired);
        else if (TypeIdentity.AreSame(constantType, BuiltinTypes.Error) || !TypeFacts.CanAssign(field.Type, constantType))
            _diagnostics.Report(syntax.IdentifierToken.Location, $"cannot implicitly convert '{constantType.Name}' to '{field.Type.ToDisplayString()}'",
                DiagnosticIds.TypeMismatch);
        else if (!IsSupportedStaticInitializer(field.Type, value))
            _diagnostics.Report(syntax.IdentifierToken.Location, $"static field type '{field.Type.ToDisplayString()}' does not support this constant initializer",
                DiagnosticIds.StaticInitializerTypeUnsupported);
        else
        {
            SetConvertedType(syntax.Initializer!, field.Type);
            field.SetConstantValue(value);
        }
    }

    private void DeclareConstants()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            FileSymbolScope scope = _treeScopes[tree];
            foreach (ModuleConstantDeclarationSyntax syntax in tree.Root.Members.OfType<ModuleConstantDeclarationSyntax>())
            {
                TypeSymbol type = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var constant = new ConstantSymbol(syntax.IdentifierToken.Text, type, @namespace, syntax.Initializer, syntax);
                if (!@namespace.TryDeclareConstant(constant))
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"constant '{@namespace.FullName}.{constant.Name}' is already declared",
                        DiagnosticIds.DuplicateDeclaration);
                else
                    _constantScopes.Add(constant, scope);
            }
        }

        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var constants = ImmutableArray.CreateBuilder<ConstantSymbol>();
            foreach (TypeConstantDeclarationSyntax syntax in declaration.Constants)
            {
                TypeSymbol constantType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var constant = new ConstantSymbol(syntax.IdentifierToken.Text, constantType, type, syntax.Initializer, syntax);
                if (constants.Any(existing => existing.Name == constant.Name))
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"constant '{constant.Name}' is already declared in struct '{type.Name}'",
                        DiagnosticIds.DuplicateDeclaration);
                else
                {
                    constants.Add(constant);
                    _constantScopes.Add(constant, scope);
                }
            }
            type.SetConstants(constants.ToImmutable());
        }
    }

    private void EvaluateConstants()
    {
        ConstantSymbol[] constants = _constantScopes.Keys.ToArray();
        foreach (ConstantSymbol constant in constants)
            if (constant.ContainingType is not StructTypeSymbol { GenericDefinition: not null })
                EvaluateConstant(constant);
    }

    private void EvaluateSpecializedConstants(StructTypeSymbol specialization)
    {
        if (!specialization.IsConcreteType || specialization.Constants.IsEmpty ||
            _genericStructSpecializer is null)
            return;

        StructTypeSymbol definition = specialization.GenericDefinition!;
        if (definition.Constants.Any(_failedConstants.Contains))
        {
            foreach (ConstantSymbol constant in specialization.Constants)
                _failedConstants.Add(constant);
            return;
        }
        FileSymbolScope scope = _structScopes[definition.Declaration].WithTypeSubstitutions(
            _genericStructSpecializer.GetSubstitutions(specialization), _semanticInfo);
        foreach (ConstantSymbol constant in specialization.Constants)
            _constantScopes.TryAdd(constant, scope);

        foreach (ConstantSymbol constant in specialization.Constants)
        {
            EvaluateConstant(constant);
        }
    }

    private bool EvaluateConstant(ConstantSymbol constant)
    {
        if (constant.HasValue)
            return true;
        if (_failedConstants.Contains(constant))
            return false;
        if (!_evaluatingConstants.Add(constant))
        {
            if (_failedConstants.Add(constant))
                _diagnostics.Report(constant.IdentifierToken.Location,
                    $"circular constant dependency involving '{constant.Name}'", DiagnosticIds.ConstantCycle);
            return false;
        }
        try
        {
            if (_enumMembers.TryGetValue(constant, out var enumMember))
                return EvaluateEnumMember(constant, enumMember.Type, enumMember.Previous, enumMember.Automatic);
            BoundExpression? value = BindConstantExpression(constant.Initializer, constant);
            if (value is null)
            {
                if (_failedConstants.Add(constant))
                    _diagnostics.Report(constant.IdentifierToken.Location,
                        $"initializer of constant '{constant.Name}' is not a compile-time constant",
                        DiagnosticIds.ConstantValueRequired);
                return false;
            }
            if (!TypeFacts.CanAssign(constant.Type, value.Type))
            {
                if (TypeFacts.IsNumeric(constant.Type) && TypeFacts.IsNumeric(value.Type))
                    value = new BoundCastExpression(value, constant.Type);
                else
                {
                    if (_failedConstants.Add(constant))
                        _diagnostics.Report(constant.IdentifierToken.Location,
                            $"cannot implicitly convert '{value.Type.ToDisplayString()}' to '{constant.Type.ToDisplayString()}'",
                            DiagnosticIds.TypeMismatch);
                    return false;
                }
            }

            SetConvertedType(constant.Initializer, value.Type);

            object? foldedValue;
            ConstantFoldStatus foldStatus = constant.ContainingType is StructTypeSymbol { IsGenericDefinition: true }
                ? FoldConstantExpression(value, out foldedValue, _constants.TargetLayout)
                : _constants.Fold(value, out foldedValue);
            if (foldStatus == ConstantFoldStatus.Invalid)
            {
                if (_failedConstants.Add(constant))
                    _diagnostics.Report(
                        constant.IdentifierToken.Location,
                        $"initializer of constant '{constant.Name}' contains an invalid compile-time operation",
                        DiagnosticIds.InvalidConstantOperation);
                return false;
            }

            if (foldStatus == ConstantFoldStatus.Folded)
            {
                constant.SetValue(foldedValue);
                constant.SetBoundValue(new BoundLiteralExpression(foldedValue, value.Type));
            }
            else
            {
                constant.SetBoundValue(value);
            }
            return true;
        }
        finally
        {
            _evaluatingConstants.Remove(constant);
        }
    }

    private BoundExpression? BindConstantExpression(ExpressionSyntax syntax, ConstantSymbol context)
    {
        BoundExpression? expression = BindConstantExpressionCore(syntax, context);
        TypeSymbol type = expression?.Type ?? BuiltinTypes.Error;
        _semanticInfo.Types[syntax] = new TypeInfo(type, type);
        return expression;
    }

    private BoundExpression? BindConstantExpressionCore(ExpressionSyntax syntax, ConstantSymbol context)
    {
        switch (syntax)
        {
            case LiteralExpressionSyntax literal:
                return new BoundLiteralExpression(GetConstantLiteralValue(literal), GetConstantExpressionType(literal));
            case ParenthesizedExpressionSyntax parenthesized:
                return BindConstantExpression(parenthesized.Expression, context);
            case NameExpressionSyntax name:
            {
                ConstantSymbol? referenced = (_enumMembers.TryGetValue(context, out var enumContext) ? enumContext.Type.FindMember(name.IdentifierToken.Text) : null) ??
                    context.ContainingType?.FindMember<ConstantSymbol>(name.IdentifierToken.Text) ??
                    _constantScopes[context].ResolveConstant(name.IdentifierToken.Text, name.IdentifierToken.Location, _diagnostics);
                if (referenced is null) return null;
                _semanticInfo.Symbols[name] = SymbolInfo.FromSymbol(referenced);
                return EvaluateConstant(referenced) ? referenced.BoundValue : null;
            }
            case MemberAccessExpressionSyntax member when member.Receiver is NameExpressionSyntax typeName &&
                _constantScopes[context].ResolveType(typeName.IdentifierToken.Text, typeName.IdentifierToken.Location, _diagnostics) is DeclaredTypeSymbol structType &&
                structType.FindMember<ConstantSymbol>(member.MemberToken.Text) is ConstantSymbol associated:
                RecordStaticConstantReference(member, typeName, structType, associated);
                return EvaluateConstant(associated) ? associated.BoundValue : null;
            case MemberAccessExpressionSyntax member:
            {
                var parts = new List<SyntaxToken>();
                ExpressionSyntax receiver = member;
                while (receiver is MemberAccessExpressionSyntax access && access.OperatorToken.Kind == SyntaxKind.DotToken)
                {
                    parts.Insert(0, access.MemberToken);
                    receiver = access.Receiver;
                }
                if (receiver is not NameExpressionSyntax name) return null;
                parts.Insert(0, name.IdentifierToken);
                TypeSymbol? resolved = parts.Count == 2
                    ? _constantScopes[context].ResolveType(parts[0].Text, parts[0].Location, _diagnostics)
                    : _constantScopes[context].ResolveQualifiedType(parts.Take(parts.Count - 1).Select(part => part.Text).ToArray());
                ConstantSymbol? referenced = resolved switch
                {
                    EnumTypeSymbol enumeration => enumeration.FindMember(parts[^1].Text),
                    StructTypeSymbol structure => structure.FindConstant(parts[^1].Text),
                    _ => null,
                };
                if (referenced is null) return null;
                _semanticInfo.Symbols[member] = SymbolInfo.FromSymbol(referenced);
                _semanticInfo.Types[member] = new TypeInfo(referenced.Type, referenced.Type);
                if (resolved is TypeSymbol receiverType)
                {
                    _semanticInfo.Types[member.Receiver] = new TypeInfo(receiverType, receiverType);
                    _semanticInfo.Receivers[member.Receiver] = new ReceiverInfo(receiverType, true, true, false);
                    _semanticInfo.Symbols[member.Receiver] = SymbolInfo.FromSymbol(receiverType);
                }
                return EvaluateConstant(referenced) ? referenced.BoundValue : null;
            }
            case UnaryExpressionSyntax unary:
            {
                BoundExpression? operand = BindConstantExpression(unary.Operand, context);
                if (operand is null)
                    return null;
                TypeSymbol? result = unary.OperatorToken.Kind switch
                {
                    SyntaxKind.PlusToken or SyntaxKind.MinusToken when TypeFacts.IsNumeric(operand.Type) => operand.Type,
                    SyntaxKind.BangToken when TypeIdentity.AreSame(operand.Type, BuiltinTypes.Bool) => BuiltinTypes.Bool,
                    SyntaxKind.TildeToken when TypeFacts.IsInteger(operand.Type) => operand.Type,
                    _ => null,
                };
                return result is null ? null : new BoundUnaryExpression(unary.OperatorToken.Kind, operand, result);
            }
            case BinaryExpressionSyntax binary:
            {
                BoundExpression? left = BindConstantExpression(binary.Left, context);
                BoundExpression? right = BindConstantExpression(binary.Right, context);
                if (left is null || right is null)
                    return null;
                bool shift = binary.OperatorToken.Kind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken;
                if (!TypeIdentity.AreSame(left.Type, right.Type) && !(shift && TypeFacts.IsInteger(left.Type) && TypeFacts.IsInteger(right.Type)))
                    return null;
                bool comparison = binary.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or
                    SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken;
                if (binary.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken &&
                    !TypeFacts.CanCompareEquality(left.Type, right.Type))
                    return null;
                bool logical = binary.OperatorToken.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken;
                bool arithmetic = binary.OperatorToken.Kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken;
                bool bitwise = binary.OperatorToken.Kind is SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.CaretToken or SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken;
                if ((logical && !TypeIdentity.AreSame(left.Type, BuiltinTypes.Bool)) ||
                    (arithmetic && !TypeFacts.IsNumeric(left.Type)) ||
                    (bitwise && !TypeFacts.IsInteger(left.Type)) ||
                    (!comparison && !logical && !arithmetic && !bitwise))
                    return null;
                return new BoundBinaryExpression(left, binary.OperatorToken.Kind, right, comparison || logical ? BuiltinTypes.Bool : left.Type);
            }
            case TypeLayoutExpressionSyntax layout:
            {
                TypeSymbol target = TypeResolver.Resolve(layout.Type, _constantScopes[context], _diagnostics);
                if (TypeIdentity.AreSame(target, BuiltinTypes.Void) || TypeIdentity.AreSame(target, BuiltinTypes.Error))
                    return null;
                FieldSymbol? field = null;
                if (layout.Keyword.Kind == SyntaxKind.OffsetOfKeyword)
                {
                    if (target is not StructTypeSymbol targetStruct ||
                        (field = targetStruct.FindField(layout.FieldToken!.Text)) is null)
                        return null;
                }
                return new BoundTypeLayoutExpression(layout.Keyword.Kind, target, field);
            }
            case CastExpressionSyntax cast:
            {
                BoundExpression? expression = BindConstantExpression(cast.Expression, context);
                TypeSymbol target = TypeResolver.Resolve(cast.Type, _constantScopes[context], _diagnostics);
                if (expression is null || !TypeFacts.CanExplicitlyCast(target, expression.Type))
                    return null;
                return new BoundCastExpression(expression, target);
            }
            default:
                return null;
        }
    }

    private void RecordStaticConstantReference(MemberAccessExpressionSyntax member, ExpressionSyntax receiver,
        TypeSymbol receiverType, ConstantSymbol constant)
    {
        _semanticInfo.Symbols[member] = SymbolInfo.FromSymbol(constant);
        _semanticInfo.Types[member] = new TypeInfo(constant.Type, constant.Type);
        _semanticInfo.Symbols[receiver] = SymbolInfo.FromSymbol(receiverType);
        _semanticInfo.Types[receiver] = new TypeInfo(receiverType, receiverType);
        _semanticInfo.Receivers[receiver] = new ReceiverInfo(receiverType, true, true, false);
    }

    private void SetConvertedType(ExpressionSyntax syntax, TypeSymbol convertedType)
    {
        TypeInfo current = _semanticInfo.Types.GetValueOrDefault(syntax, new TypeInfo(convertedType, convertedType));
        _semanticInfo.Types[syntax] = current with { ConvertedType = convertedType };
    }

    internal static ConstantFoldStatus FoldConstantExpression(BoundExpression expression, out object? value, ITargetTypeLayout? targetLayout)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal:
                if (targetLayout is null && (literal.Type is PrimitiveTypeSymbol { IsInteger: true, BitWidth: null } or EnumTypeSymbol { UnderlyingType.BitWidth: null }))
                {
                    value = null;
                    return ConstantFoldStatus.TargetDependent;
                }
                value = literal.Value;
                return ConstantFoldStatus.Folded;
            case BoundDeferredConstantExpression:
                value = null;
                return ConstantFoldStatus.TargetDependent;
            case BoundTypeLayoutExpression layout:
                if (targetLayout is null || GenericTypeFacts.ContainsGenericParameter(layout.TargetType))
                {
                    value = null;
                    return ConstantFoldStatus.TargetDependent;
                }
                value = layout.OperatorKind switch
                {
                    SyntaxKind.SizeOfKeyword => targetLayout.GetSize(layout.TargetType),
                    SyntaxKind.AlignOfKeyword => (ulong)targetLayout.GetAlignment(layout.TargetType),
                    SyntaxKind.OffsetOfKeyword => targetLayout.GetFieldOffset((StructTypeSymbol)layout.TargetType, layout.Field!),
                    _ => throw new InvalidOperationException("Unknown layout intrinsic."),
                };
                return ConstantFoldStatus.Folded;
            case BoundUnaryExpression unary:
            {
                ConstantFoldStatus operandStatus = FoldConstantExpression(unary.Operand, out object? operand, targetLayout);
                if (operandStatus != ConstantFoldStatus.Folded)
                {
                    value = null;
                    return operandStatus;
                }
                if (TryEvaluateUnaryConstant(unary.OperatorKind, operand, out object? unaryValue) &&
                    TryNormalizeFoldedValue(unaryValue, unary.Type, out value, targetLayout))
                {
                    return ConstantFoldStatus.Folded;
                }
                value = null;
                return ConstantFoldStatus.Invalid;
            }
            case BoundBinaryExpression binary:
            {
                ConstantFoldStatus leftStatus = FoldConstantExpression(binary.Left, out object? left, targetLayout);
                if (leftStatus == ConstantFoldStatus.Invalid)
                {
                    value = null;
                    return ConstantFoldStatus.Invalid;
                }
                if (leftStatus == ConstantFoldStatus.Folded && left is bool leftBoolean)
                {
                    if (binary.OperatorKind == SyntaxKind.AmpersandAmpersandToken && !leftBoolean)
                    {
                        value = false;
                        return ConstantFoldStatus.Folded;
                    }
                    if (binary.OperatorKind == SyntaxKind.PipePipeToken && leftBoolean)
                    {
                        value = true;
                        return ConstantFoldStatus.Folded;
                    }
                }

                ConstantFoldStatus rightStatus = FoldConstantExpression(binary.Right, out object? right, targetLayout);
                if (rightStatus == ConstantFoldStatus.Invalid ||
                    (binary.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken && IsIntegerZero(right)))
                {
                    value = null;
                    return ConstantFoldStatus.Invalid;
                }
                if (leftStatus == ConstantFoldStatus.TargetDependent || rightStatus == ConstantFoldStatus.TargetDependent)
                {
                    value = null;
                    return ConstantFoldStatus.TargetDependent;
                }

                try
                {
                    if (binary.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken &&
                        binary.Left.Type is PrimitiveTypeSymbol { IsInteger: true, IsSigned: true } signedType)
                    {
                        int? signedWidth = signedType.BitWidth ?? targetLayout?.GetIntegerBitWidth(signedType);
                        if (signedWidth is null)
                        {
                            value = null;
                            return ConstantFoldStatus.TargetDependent;
                        }
                        if (ToInteger(left) == -(BigInteger.One << (signedWidth.Value - 1)) && ToInteger(right) == -1)
                        {
                            value = null;
                            return ConstantFoldStatus.Invalid;
                        }
                    }
                    if (binary.OperatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken)
                    {
                        var operandType = (PrimitiveTypeSymbol)binary.Left.Type;
                        int? width = operandType.BitWidth ?? targetLayout?.GetIntegerBitWidth(operandType);
                        BigInteger count = ToInteger(right);
                        if (width is null)
                        {
                            value = null;
                            return ConstantFoldStatus.TargetDependent;
                        }
                        if (count < 0 || count >= width)
                        {
                            value = null;
                            return ConstantFoldStatus.Invalid;
                        }
                        object shifted = (left, binary.OperatorKind) switch
                        {
                            (int integer, SyntaxKind.LessLessToken) => (object)(integer << (int)count),
                            (int integer, _) => integer >> (int)count,
                            (long integer, SyntaxKind.LessLessToken) => integer << (int)count,
                            (long integer, _) => integer >> (int)count,
                            (ulong integer, SyntaxKind.LessLessToken) => integer << (int)count,
                            (ulong integer, _) => integer >> (int)count,
                            _ => throw new InvalidOperationException("Invalid shift constant."),
                        };
                        return TryNormalizeFoldedValue(shifted, binary.Type, out value, targetLayout)
                            ? ConstantFoldStatus.Folded : ConstantFoldStatus.Invalid;
                    }
                    if (TryEvaluateBinaryConstant(left, binary.OperatorKind, right, out object? binaryValue) &&
                        TryNormalizeFoldedValue(binaryValue, binary.Type, out value, targetLayout))
                    {
                        return ConstantFoldStatus.Folded;
                    }
                }
                catch (Exception exception) when (exception is OverflowException or DivideByZeroException)
                {
                }

                value = null;
                return ConstantFoldStatus.Invalid;
            }
            case BoundCastExpression cast:
            {
                ConstantFoldStatus operandStatus = FoldConstantExpression(cast.Expression, out object? operand, targetLayout);
                if (operandStatus != ConstantFoldStatus.Folded)
                {
                    value = null;
                    return operandStatus;
                }
                if (targetLayout is null && (cast.TargetType is PrimitiveTypeSymbol { IsInteger: true, BitWidth: null } or EnumTypeSymbol { UnderlyingType.BitWidth: null }))
                {
                    value = null;
                    return ConstantFoldStatus.TargetDependent;
                }
                return TryFoldPrimitiveCast(operand, cast.TargetType, out value, targetLayout)
                    ? ConstantFoldStatus.Folded
                    : ConstantFoldStatus.Invalid;
            }
            default:
                value = null;
                return ConstantFoldStatus.Invalid;
        }
    }

    private static bool IsIntegerZero(object? value) => value switch
    {
        int integer => integer == 0,
        long integer => integer == 0,
        ulong integer => integer == 0,
        _ => false,
    };

    private static bool TryFoldPrimitiveCast(object? value, TypeSymbol targetType, out object? converted, ITargetTypeLayout? targetLayout)
    {
        if (targetType is EnumTypeSymbol enumeration) targetType = enumeration.UnderlyingType;
        if (targetType is PrimitiveTypeSymbol { IsInteger: true, BitWidth: null } native && targetLayout is not null)
        {
            int width = targetLayout.GetIntegerBitWidth(native);
            targetType = (width, native.IsSigned) switch
            {
                (32, true) => BuiltinTypes.Int,
                (32, false) => BuiltinTypes.UInt,
                (64, true) => BuiltinTypes.Long,
                (64, false) => BuiltinTypes.ULong,
                _ => throw new InvalidOperationException($"Unsupported native integer width {width}."),
            };
        }
        try
        {
            if (TypeIdentity.AreSame(targetType, BuiltinTypes.Float))
            {
                converted = Convert.ToSingle(value);
                return true;
            }
            if (TypeIdentity.AreSame(targetType, BuiltinTypes.Double))
            {
                converted = Convert.ToDouble(value);
                return true;
            }
            if (targetType is not PrimitiveTypeSymbol { IsInteger: true } integerType)
            {
                converted = null;
                return false;
            }

            if (value is float or double)
            {
                double number = Convert.ToDouble(value);
                if (!double.IsFinite(number)) { converted = null; return false; }
                BigInteger truncated = new(Math.Truncate(number));
                int width = integerType.BitWidth!.Value;
                BigInteger minimum = integerType.IsSigned ? -(BigInteger.One << (width - 1)) : BigInteger.Zero;
                BigInteger maximum = (BigInteger.One << (integerType.IsSigned ? width - 1 : width)) - 1;
                if (truncated < minimum || truncated > maximum) { converted = null; return false; }
                value = integerType.IsSigned ? (object)(long)truncated : (ulong)truncated;
            }
            ulong bits = value switch
            {
                int integer => unchecked((ulong)(long)integer),
                long integer => unchecked((ulong)integer),
                ulong integer => integer,
                _ => throw new InvalidCastException(),
            };
            converted = targetType.Name switch
            {
                "byte" => (int)unchecked((byte)bits),
                "sbyte" => (int)unchecked((sbyte)bits),
                "short" => (int)unchecked((short)bits),
                "ushort" => (int)unchecked((ushort)bits),
                "int" => unchecked((int)bits),
                "uint" => (ulong)unchecked((uint)bits),
                "long" => unchecked((long)bits),
                "ulong" => bits,
                _ => null,
            };
            return converted is not null;
        }
        catch (Exception exception) when (exception is OverflowException or InvalidCastException or FormatException)
        {
            converted = null;
            return false;
        }
    }

    private static bool TryNormalizeFoldedValue(object? value, TypeSymbol type, out object? normalized, ITargetTypeLayout? targetLayout)
    {
        if (TypeIdentity.AreSame(type, BuiltinTypes.Bool) && value is bool)
        {
            normalized = value;
            return true;
        }
        return TryFoldPrimitiveCast(value, type, out normalized, targetLayout);
    }

    private static bool IsSupportedStaticInitializer(TypeSymbol type, object? value) =>
        value is null ||
        (TypeIdentity.AreSame(type, BuiltinTypes.Bool) && value is bool) ||
        (type is PrimitiveTypeSymbol { IsInteger: true } && value is not bool) ||
        type is PrimitiveTypeSymbol { IsFloatingPoint: true };

    private static object? GetConstantLiteralValue(LiteralExpressionSyntax literal) => literal.LiteralToken switch
    {
        { Kind: SyntaxKind.TrueKeyword } => true,
        { Kind: SyntaxKind.FalseKeyword } => false,
        { Kind: SyntaxKind.IntegerLiteralToken, Value: ulong integer } when integer <= int.MaxValue => (int)integer,
        { Kind: SyntaxKind.IntegerLiteralToken, Value: ulong integer } when integer <= long.MaxValue => (long)integer,
        { Kind: SyntaxKind.IntegerLiteralToken, Value: ulong integer } => integer,
        _ => literal.LiteralToken.Value,
    };

    private static bool TryEvaluateUnaryConstant(SyntaxKind operation, object? operand, out object? value)
    {
        value = (operation, operand) switch
        {
            (SyntaxKind.PlusToken, int or long or ulong or float or double) => operand,
            (SyntaxKind.MinusToken, int integer) => unchecked(-integer),
            (SyntaxKind.MinusToken, long integer) => unchecked(-integer),
            (SyntaxKind.MinusToken, ulong integer) => unchecked(0UL - integer),
            (SyntaxKind.MinusToken, float number) => -number,
            (SyntaxKind.MinusToken, double number) => -number,
            (SyntaxKind.BangToken, bool boolean) => !boolean,
            (SyntaxKind.TildeToken, int integer) => ~integer,
            (SyntaxKind.TildeToken, long integer) => ~integer,
            (SyntaxKind.TildeToken, ulong integer) => ~integer,
            _ => null,
        };
        return value is not null;
    }

    private TypeSymbol GetConstantExpressionType(ExpressionSyntax syntax) => syntax switch
    {
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken, LiteralToken.Value: ulong value } when value <= int.MaxValue => BuiltinTypes.Int,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken, LiteralToken.Value: ulong value } when value <= long.MaxValue => BuiltinTypes.Long,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken } => BuiltinTypes.ULong,
        LiteralExpressionSyntax { LiteralToken.Value: float } => BuiltinTypes.Float,
        LiteralExpressionSyntax { LiteralToken.Value: double } => BuiltinTypes.Double,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword } => BuiltinTypes.Bool,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.StringLiteralToken } => _typeFactory.PointerTo(BuiltinTypes.Byte, isReadonly: true),
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.NullKeyword } => BuiltinTypes.Null,
        ParenthesizedExpressionSyntax parenthesized => GetConstantExpressionType(parenthesized.Expression),
        UnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.BangToken } => BuiltinTypes.Bool,
        UnaryExpressionSyntax unary => GetConstantExpressionType(unary.Operand),
        BinaryExpressionSyntax binary when binary.OperatorToken.Kind is
            SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or
            SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or
            SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken or
            SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken => BuiltinTypes.Bool,
        BinaryExpressionSyntax binary when TypeIdentity.AreSame(GetConstantExpressionType(binary.Left), GetConstantExpressionType(binary.Right)) => GetConstantExpressionType(binary.Left),
        _ => BuiltinTypes.Error,
    };

    private static bool TryEvaluateBinaryConstant(object? left, SyntaxKind operation, object? right, out object? value)
    {
        if (left is bool leftBool && right is bool rightBool)
        {
            value = operation switch
            {
                SyntaxKind.AmpersandAmpersandToken => leftBool && rightBool,
                SyntaxKind.PipePipeToken => leftBool || rightBool,
                SyntaxKind.EqualsEqualsToken => leftBool == rightBool,
                SyntaxKind.BangEqualsToken => leftBool != rightBool,
                _ => null,
            };
            return value is not null;
        }

        if (left is float leftFloat && right is float rightFloat)
        {
            value = operation switch
            {
                SyntaxKind.PlusToken => leftFloat + rightFloat,
                SyntaxKind.MinusToken => leftFloat - rightFloat,
                SyntaxKind.StarToken => leftFloat * rightFloat,
                SyntaxKind.SlashToken => leftFloat / rightFloat,
                SyntaxKind.PercentToken => leftFloat % rightFloat,
                SyntaxKind.EqualsEqualsToken => leftFloat == rightFloat,
                SyntaxKind.BangEqualsToken => leftFloat != rightFloat,
                SyntaxKind.LessToken => leftFloat < rightFloat,
                SyntaxKind.LessOrEqualsToken => leftFloat <= rightFloat,
                SyntaxKind.GreaterToken => leftFloat > rightFloat,
                SyntaxKind.GreaterOrEqualsToken => leftFloat >= rightFloat,
                _ => null,
            };
            return value is not null;
        }

        if (left is double leftDouble && right is double rightDouble)
        {
            value = operation switch
            {
                SyntaxKind.PlusToken => leftDouble + rightDouble,
                SyntaxKind.MinusToken => leftDouble - rightDouble,
                SyntaxKind.StarToken => leftDouble * rightDouble,
                SyntaxKind.SlashToken => leftDouble / rightDouble,
                SyntaxKind.PercentToken => leftDouble % rightDouble,
                SyntaxKind.EqualsEqualsToken => leftDouble == rightDouble,
                SyntaxKind.BangEqualsToken => leftDouble != rightDouble,
                SyntaxKind.LessToken => leftDouble < rightDouble,
                SyntaxKind.LessOrEqualsToken => leftDouble <= rightDouble,
                SyntaxKind.GreaterToken => leftDouble > rightDouble,
                SyntaxKind.GreaterOrEqualsToken => leftDouble >= rightDouble,
                _ => null,
            };
            return value is not null;
        }

        if (left is int leftInt && right is int rightInt)
            return TryEvaluateInt32Constant(leftInt, operation, rightInt, out value);
        if (left is long leftLong && right is long rightLong)
            return TryEvaluateInt64Constant(leftLong, operation, rightLong, out value);
        if (left is ulong leftULong && right is ulong rightULong)
            return TryEvaluateUInt64Constant(leftULong, operation, rightULong, out value);

        value = null;
        return false;
    }

    private static bool TryEvaluateInt32Constant(int left, SyntaxKind operation, int right, out object? value)
    {
        value = operation switch
        {
            SyntaxKind.PlusToken => unchecked(left + right),
            SyntaxKind.MinusToken => unchecked(left - right),
            SyntaxKind.StarToken => unchecked(left * right),
            SyntaxKind.SlashToken when right != 0 => left / right,
            SyntaxKind.PercentToken when right != 0 => left % right,
            SyntaxKind.AmpersandToken => left & right,
            SyntaxKind.PipeToken => left | right,
            SyntaxKind.CaretToken => left ^ right,
            SyntaxKind.LessLessToken => left << right,
            SyntaxKind.GreaterGreaterToken => left >> right,
            SyntaxKind.EqualsEqualsToken => left == right,
            SyntaxKind.BangEqualsToken => left != right,
            SyntaxKind.LessToken => left < right,
            SyntaxKind.LessOrEqualsToken => left <= right,
            SyntaxKind.GreaterToken => left > right,
            SyntaxKind.GreaterOrEqualsToken => left >= right,
            _ => null,
        };
        return value is not null;
    }

    private static bool TryEvaluateInt64Constant(long left, SyntaxKind operation, long right, out object? value)
    {
        value = operation switch
        {
            SyntaxKind.PlusToken => unchecked(left + right),
            SyntaxKind.MinusToken => unchecked(left - right),
            SyntaxKind.StarToken => unchecked(left * right),
            SyntaxKind.SlashToken when right != 0 => left / right,
            SyntaxKind.PercentToken when right != 0 => left % right,
            SyntaxKind.AmpersandToken => left & right,
            SyntaxKind.PipeToken => left | right,
            SyntaxKind.CaretToken => left ^ right,
            SyntaxKind.LessLessToken => left << (int)right,
            SyntaxKind.GreaterGreaterToken => left >> (int)right,
            SyntaxKind.EqualsEqualsToken => left == right,
            SyntaxKind.BangEqualsToken => left != right,
            SyntaxKind.LessToken => left < right,
            SyntaxKind.LessOrEqualsToken => left <= right,
            SyntaxKind.GreaterToken => left > right,
            SyntaxKind.GreaterOrEqualsToken => left >= right,
            _ => null,
        };
        return value is not null;
    }

    private static bool TryEvaluateUInt64Constant(ulong left, SyntaxKind operation, ulong right, out object? value)
    {
        value = operation switch
        {
            SyntaxKind.PlusToken => unchecked(left + right),
            SyntaxKind.MinusToken => unchecked(left - right),
            SyntaxKind.StarToken => unchecked(left * right),
            SyntaxKind.SlashToken when right != 0 => left / right,
            SyntaxKind.PercentToken when right != 0 => left % right,
            SyntaxKind.AmpersandToken => left & right,
            SyntaxKind.PipeToken => left | right,
            SyntaxKind.CaretToken => left ^ right,
            SyntaxKind.LessLessToken => left << (int)right,
            SyntaxKind.GreaterGreaterToken => left >> (int)right,
            SyntaxKind.EqualsEqualsToken => left == right,
            SyntaxKind.BangEqualsToken => left != right,
            SyntaxKind.LessToken => left < right,
            SyntaxKind.LessOrEqualsToken => left <= right,
            SyntaxKind.GreaterToken => left > right,
            SyntaxKind.GreaterOrEqualsToken => left >= right,
            _ => null,
        };
        return value is not null;
    }

    private void BindTypeInheritance()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            var interfaces = ImmutableArray.CreateBuilder<InterfaceTypeSymbol>();
            foreach (TypeSyntax baseSyntax in declaration.BaseTypes)
            {
                TypeSymbol resolved = TypeResolver.Resolve(baseSyntax, _structScopes[declaration], _diagnostics);
                if (resolved is StructTypeSymbol baseStruct)
                {
                    if (type.BaseType is not null)
                        _diagnostics.Report(baseSyntax.NameToken.Location, $"struct '{type.Name}' may inherit from at most one struct",
                            DiagnosticIds.MultipleStructBaseTypes);
                    else if (TypeIdentity.AreSame(baseStruct, type))
                        _diagnostics.Report(baseSyntax.NameToken.Location, $"struct '{type.Name}' cannot inherit from itself",
                            DiagnosticIds.SelfInheritance);
                    else
                        type.SetBaseType(baseStruct);
                }
                else if (resolved is InterfaceTypeSymbol @interface)
                    interfaces.Add(@interface);
                else if (!TypeIdentity.AreSame(resolved, BuiltinTypes.Error))
                    _diagnostics.Report(baseSyntax.NameToken.Location, $"'{baseSyntax.Name}' is not a struct or interface type",
                        DiagnosticIds.InvalidBaseType);
            }
            type.SetInterfaces(interfaces.ToImmutable());
        }

        foreach ((InterfaceDeclarationSyntax declaration, InterfaceTypeSymbol type) in _interfaceSymbols)
        {
            var bases = ImmutableArray.CreateBuilder<InterfaceTypeSymbol>();
            foreach (TypeSyntax baseSyntax in declaration.BaseInterfaces)
            {
                TypeSymbol resolved = TypeResolver.Resolve(baseSyntax, _treeScopes.First(pair => ReferenceEquals(pair.Key.Root.Members.OfType<InterfaceDeclarationSyntax>().FirstOrDefault(d => ReferenceEquals(d, declaration)), declaration)).Value, _diagnostics);
                if (resolved is InterfaceTypeSymbol @interface && !TypeIdentity.AreSame(@interface, type))
                    bases.Add(@interface);
                else if (!TypeIdentity.AreSame(resolved, BuiltinTypes.Error))
                    _diagnostics.Report(baseSyntax.NameToken.Location, $"interface '{type.Name}' may inherit only from interfaces",
                        DiagnosticIds.InterfaceBaseMustBeInterface);
            }
            type.SetBaseInterfaces(bases.ToImmutable());
        }
    }

    private void ValidateInheritedInterfaceMembers()
    {
        foreach (InterfaceTypeSymbol type in _interfaceSymbols.Values)
        {
            foreach (var group in type.AllMethods.GroupBy(TypeSignature.Method))
            {
                FunctionSymbol first = group.First();
                if (group.Any(method => !TypeIdentity.AreSame(method.ReturnType, first.ReturnType) || method.IsReadonly != first.IsReadonly))
                    _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                        $"interface '{type.Name}' inherits incompatible member '{first.Name}'",
                        DiagnosticIds.InheritedInterfaceMemberConflict);
            }
            foreach (var group in type.AllProperties.GroupBy(property => property.Name))
            {
                InterfacePropertySymbol first = group.First();
                if (group.Any(property => !TypeIdentity.AreSame(property.Type, first.Type) ||
                    (property.Getter is null) != (first.Getter is null) || (property.Setter is null) != (first.Setter is null)) ||
                    type.SelfAndBaseInterfaces.SelectMany(parent => parent.Methods).Any(method => method.Name == first.Name))
                    _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                        $"interface '{type.Name}' inherits incompatible member '{first.Name}'",
                        DiagnosticIds.InheritedInterfaceMemberConflict);
            }
            foreach (var group in type.SelfAndBaseInterfaces.SelectMany(parent => parent.Indexers)
                .GroupBy(indexer => TypeSignature.Parameters(indexer.Parameters)))
            {
                InterfaceIndexerSymbol first = group.First();
                if (group.Any(indexer => !TypeIdentity.AreSame(indexer.Type, first.Type) ||
                    (indexer.Getter is null) != (first.Getter is null) || (indexer.Setter is null) != (first.Setter is null)))
                    _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                        $"interface '{type.Name}' inherits incompatible indexers",
                        DiagnosticIds.InheritedInterfaceMemberConflict);
            }
        }
    }

    private void DeclareInterfaceMethods()
    {
        foreach ((InterfaceDeclarationSyntax declaration, InterfaceTypeSymbol type) in _interfaceSymbols)
        {
            FileSymbolScope scope = _treeScopes.First(pair => pair.Key.Root.Members.Contains(declaration)).Value;
            var methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
            foreach (InterfaceMethodDeclarationSyntax syntax in declaration.Methods)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(syntax.Parameters, scope);
                if (methods.Any(m => m.Name == syntax.IdentifierToken.Text && HaveSameParameterTypes(m.Parameters, parameters)))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface '{type.Name}' already declares method '{syntax.IdentifierToken.Text}'",
                        DiagnosticIds.DuplicateDeclaration);
                    continue;
                }
                methods.Add(new FunctionSymbol(syntax.IdentifierToken.Text, type, TypeResolver.ResolveReturnType(syntax.ReturnType, scope, _diagnostics), parameters, syntax));
            }
            type.SetMethods(methods.ToImmutable());

            var properties = ImmutableArray.CreateBuilder<InterfacePropertySymbol>();
            foreach (InterfacePropertyDeclarationSyntax syntax in declaration.Properties)
            {
                if (properties.Any(property => string.Equals(property.Name, syntax.IdentifierToken.Text, StringComparison.Ordinal)) ||
                    declaration.Methods.Any(method => string.Equals(method.IdentifierToken.Text, syntax.IdentifierToken.Text, StringComparison.Ordinal)))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface '{type.Name}' already declares member '{syntax.IdentifierToken.Text}'",
                        DiagnosticIds.DuplicateDeclaration);
                    continue;
                }

                TypeSymbol propertyType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var property = new InterfacePropertySymbol(syntax.IdentifierToken.Text, type, propertyType, syntax);
                if (syntax.Accessors.Count(accessor => accessor.IsGetter) > 1)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface property '{property.Name}' declares more than one getter",
                        DiagnosticIds.DuplicateGetter);
                if (syntax.Accessors.Count(accessor => accessor.IsSetter) > 1)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface property '{property.Name}' declares more than one setter",
                        DiagnosticIds.DuplicateSetter);
                if (syntax.Getter is null && syntax.Setter is null)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface property '{property.Name}' must declare a getter or setter",
                        DiagnosticIds.AccessorRequired);

                FunctionSymbol? getter = syntax.Getter is null
                    ? null
                    : new FunctionSymbol($"get_{property.Name}", property, propertyType, [], syntax.Getter);
                FunctionSymbol? setter = syntax.Setter is null
                    ? null
                    : new FunctionSymbol(
                        $"set_{property.Name}",
                        property,
                        BuiltinTypes.Void,
                        [new ParameterSymbol("value", propertyType, 0)],
                        syntax.Setter);
                property.SetAccessors(getter, setter);
                properties.Add(property);
            }
            type.SetProperties(properties.ToImmutable());

            var indexers = ImmutableArray.CreateBuilder<InterfaceIndexerSymbol>();
            foreach (InterfaceIndexerDeclarationSyntax syntax in declaration.Indexers)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(syntax.Parameters, scope);
                if (parameters.IsEmpty)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "an indexer must declare at least one parameter",
                        DiagnosticIds.IndexerRequiresParameter);
                if (indexers.Any(candidate => HaveSameParameterTypes(candidate.Parameters, parameters)))
                {
                    _diagnostics.Report(syntax.ThisKeyword.Location, $"interface '{type.Name}' already declares an indexer with the same parameter types",
                        DiagnosticIds.DuplicateDeclaration);
                    continue;
                }
                TypeSymbol indexerType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var indexer = new InterfaceIndexerSymbol(type, indexerType, parameters, syntax);
                FunctionSymbol? getter = syntax.Getter is null
                    ? null
                    : new FunctionSymbol(indexer.GetAccessorName(getter: true), indexer, indexerType, parameters, syntax.Getter);
                FunctionSymbol? setter = syntax.Setter is null
                    ? null
                    : new FunctionSymbol(
                        indexer.GetAccessorName(getter: false),
                        indexer,
                        BuiltinTypes.Void,
                        [.. parameters, new ParameterSymbol("value", indexerType, parameters.Length)],
                        syntax.Setter);
                if (getter is null && setter is null)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "an interface indexer must declare a getter or setter",
                        DiagnosticIds.AccessorRequired);
                indexer.SetAccessors(getter, setter);
                indexers.Add(indexer);
            }
            type.SetIndexers(indexers.ToImmutable());
        }
    }

    private void ValidateInheritanceCycles()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        {
            if (type.BaseType is not null && ReachesStruct(type.BaseType, type, []))
            {
                _diagnostics.Report(type.Declaration.IdentifierToken.Location, $"struct inheritance cycle involving '{type.Name}'",
                    DiagnosticIds.InheritanceCycle);
                type.ClearBaseType();
            }
        }

        foreach (InterfaceTypeSymbol type in _interfaceSymbols.Values)
        {
            ImmutableArray<InterfaceTypeSymbol> validBases = type.BaseInterfaces
                .Where(baseType => !ReachesInterface(baseType, type, []))
                .ToImmutableArray();
            if (validBases.Length != type.BaseInterfaces.Length)
            {
                _diagnostics.Report(type.Declaration.IdentifierToken.Location, $"interface inheritance cycle involving '{type.Name}'",
                    DiagnosticIds.InheritanceCycle);
                type.SetBaseInterfaces(validBases);
            }
        }
    }

    private static bool ReachesStruct(StructTypeSymbol current, StructTypeSymbol target, HashSet<StructTypeSymbol> visited)
    {
        if (TypeIdentity.AreSame(current, target))
            return true;
        return visited.Add(current) && current.BaseType is not null && ReachesStruct(current.BaseType, target, visited);
    }

    private static bool ReachesInterface(InterfaceTypeSymbol current, InterfaceTypeSymbol target, HashSet<InterfaceTypeSymbol> visited)
    {
        if (TypeIdentity.AreSame(current, target))
            return true;
        return visited.Add(current) && current.BaseInterfaces.Any(baseType => ReachesInterface(baseType, target, visited));
    }

    private void AssignInterfaceMethodSlots()
    {
        foreach (InterfaceTypeSymbol @interface in _interfaceSymbols.Values)
        {
            @interface.SetMethodSlots(@interface.AllMethods);
        }
    }

    private void MarkVirtualDispatchRequirements()
    {
        // Dispatch is a declaration/inherited contract, not a property of the set
        // of known descendants. Propagate only downwards before assigning fields.
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            if (!type.Interfaces.IsEmpty || declaration.Methods.Any(method => method.IsVirtual || method.IsOverride || method.IsAbstract) ||
                declaration.Properties.Any(property => property.IsVirtual || property.IsOverride || property.IsAbstract) ||
                declaration.Indexers.Any(indexer => indexer.IsVirtual || indexer.IsOverride || indexer.IsAbstract) ||
                declaration.Destructor?.IsVirtual == true)
            {
                type.SetHasVirtualDispatch();
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (StructTypeSymbol type in _structSymbols.Values.Where(type => type.BaseType?.HasVirtualDispatch == true && !type.HasVirtualDispatch))
            {
                type.SetHasVirtualDispatch();
                changed = true;
            }
        } while (changed);
    }

    private void BuildVirtualMethodTables()
    {
        var built = new HashSet<StructTypeSymbol>();
        foreach (StructTypeSymbol type in _structSymbols.Values)
            BuildVirtualMethodTable(type, built);
    }

    private void BuildVirtualMethodTable(StructTypeSymbol type, HashSet<StructTypeSymbol> built)
    {
        if (!built.Add(type))
            return;
        if (type.BaseType is not null)
            BuildVirtualMethodTable(type.BaseType, built);

        var slots = type.BaseType?.VirtualMethods.ToBuilder() ?? ImmutableArray.CreateBuilder<FunctionSymbol>();
        var invalidAccessors = new HashSet<FunctionSymbol>();
        foreach (PropertySymbol property in type.Properties)
        {
            PropertySymbol? inherited = type.BaseType?.FindProperty(property.Name);
            ValidateAccessorOverride($"property '{property.ToDisplayString(SymbolDisplayFormat.Diagnostic)}'", property.Locations[0],
                property.Declaration.IsOverride, property.Getter, property.Setter, inherited?.Getter, inherited?.Setter, invalidAccessors);
        }
        foreach (IndexerSymbol indexer in type.Indexers)
        {
            IndexerSymbol? inherited = type.BaseType?.AllIndexers.FirstOrDefault(candidate => HaveSameParameterTypes(indexer.Parameters, candidate.Parameters));
            ValidateAccessorOverride($"indexer '{indexer.ToDisplayString(SymbolDisplayFormat.Diagnostic)}'",
                indexer.Locations[0], indexer.Declaration.IsOverride,
                indexer.Getter, indexer.Setter, inherited?.Getter, inherited?.Setter, invalidAccessors);
        }
        foreach (FunctionSymbol method in type.Methods)
        {
            // Search inherited slots by member kind and the complete signature,
            // not the first declaration with this name in an intermediate type.
            FunctionSymbol? inherited = type.BaseType?.VirtualMethods.FirstOrDefault(method.HasSameSignature);
            if (invalidAccessors.Contains(method)) continue;
            if (method.ContainingProperty is null && method.ContainingIndexer is null && !ValidateMethodOverride(method, inherited)) continue;
            if (method.IsStatic) continue;
            if (method.IsOverride && inherited?.VTableSlot is int slot && method.Overrides(inherited))
            {
                method.SetVTableSlot(slot);
                slots[slot] = method;
            }
            else if (method.IsVirtual || method.IsAbstract)
            {
                method.SetVTableSlot(slots.Count);
                slots.Add(method);
            }
        }

        if (type.Destructor is FunctionSymbol destructor)
        {
            FunctionSymbol? inheritedDestructor = type.BaseType?.FindDestructor();
            int? inheritedSlot = inheritedDestructor?.VTableSlot;
            if (destructor.IsOverride && inheritedSlot is null)
                _diagnostics.Report(MemberLocation(destructor), $"destructor '{type.Name}' does not override a virtual base destructor",
                    DiagnosticIds.NoCompatibleOverrideTarget);
            else if (!destructor.IsOverride && inheritedSlot is not null)
                _diagnostics.Report(MemberLocation(destructor), $"destructor '{type.FullName}' overrides an inherited virtual destructor and must be declared 'override'",
                    DiagnosticIds.MissingOverrideModifier);
            else if (destructor.IsOverride && inheritedDestructor is not null && !HasCompatibleOverrideAccessibility(destructor, inheritedDestructor))
                _diagnostics.Report(MemberLocation(destructor), "an override cannot reduce the accessibility of its inherited member",
                    DiagnosticIds.OverrideAccessibilityReduction);
            else if (destructor.IsOverride && inheritedSlot is int slot)
            {
                destructor.SetVTableSlot(slot);
                slots[slot] = destructor;
            }
            else if (destructor.IsVirtual)
            {
                destructor.SetVTableSlot(slots.Count);
                slots.Add(destructor);
            }
        }
        type.SetVirtualMethods(slots.ToImmutable());
        if (!type.IsAbstract)
        {
            foreach (FunctionSymbol member in type.VirtualMethods.Where(method => method.IsAbstract)
                .DistinctBy(method => (object?)method.ContainingProperty ?? method.ContainingIndexer ?? (object)method))
                _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                    $"concrete struct '{type.FullName}' does not implement abstract member '{MemberName(member)}'; implement it or declare the struct 'abstract'",
                    DiagnosticIds.UnimplementedInterfaceMember);
        }
    }

    private bool ValidateMethodOverride(FunctionSymbol method, FunctionSymbol? inherited)
    {
        if (method.IsStatic && (method.IsVirtual || method.IsOverride || method.IsAbstract))
        {
            // The parser owns XE1007 for this invalid modifier combination. Keep
            // rejecting the method here without duplicating the syntax diagnostic.
            return false;
        }
        if (method.IsOverride && (inherited is null || !method.Overrides(inherited)))
        {
            _diagnostics.Report(MemberLocation(method), $"method '{MemberName(method)}' does not override a compatible virtual or abstract base method",
                DiagnosticIds.NoCompatibleOverrideTarget);
            return false;
        }
        if (!method.IsOverride && inherited is not null)
        {
            _diagnostics.Report(MemberLocation(method), $"method '{MemberName(method)}' overrides inherited member '{MemberName(inherited)}' and must be declared 'override'",
                DiagnosticIds.MissingOverrideModifier);
            return false;
        }
        if (method.IsOverride && inherited is not null && !HasCompatibleOverrideAccessibility(method, inherited))
        {
            _diagnostics.Report(MemberLocation(method), "an override cannot reduce the accessibility of its inherited member",
                DiagnosticIds.OverrideAccessibilityReduction);
            return false;
        }
        return true;
    }

    private void ValidateAccessorOverride(string name, TextLocation location, bool isOverride,
        FunctionSymbol? getter, FunctionSymbol? setter, FunctionSymbol? baseGetter, FunctionSymbol? baseSetter,
        HashSet<FunctionSymbol> invalid)
    {
        bool inheritedVirtual = baseGetter?.VTableSlot is not null || baseSetter?.VTableSlot is not null;
        string? diagnostic = !isOverride && inheritedVirtual
            ? $"{name} overrides an inherited virtual or abstract member and must be declared 'override'"
            : isOverride && (!inheritedVirtual || !Compatible(getter, baseGetter) || !Compatible(setter, baseSetter))
                ? $"{name} does not override a compatible virtual or abstract base member; type, readonly qualifier and getter/setter contract must match"
                : null;
        if (diagnostic is null) return;
        _diagnostics.Report(location, diagnostic,
            isOverride ? DiagnosticIds.NoCompatibleOverrideTarget : DiagnosticIds.MissingOverrideModifier);
        if (getter is not null) invalid.Add(getter);
        if (setter is not null) invalid.Add(setter);

        static bool Compatible(FunctionSymbol? accessor, FunctionSymbol? inherited) => accessor is null
            ? inherited is null
            : inherited?.VTableSlot is not null && accessor.Overrides(inherited) && HasCompatibleOverrideAccessibility(accessor, inherited);
    }

    private static bool HasCompatibleOverrideAccessibility(FunctionSymbol member, FunctionSymbol inherited) =>
        !inherited.IsPublic || member.IsPublic;

    private static TextLocation MemberLocation(FunctionSymbol method) => method.Declaration switch
    {
        MethodDeclarationSyntax => method.Locations[0],
        DestructorDeclarationSyntax syntax => (syntax.OverrideKeyword ?? syntax.IdentifierToken).Location,
        _ => method.ContainingType!.Declaration.IdentifierToken.Location,
    };

    private static string MemberName(FunctionSymbol method) =>
        (method.ContainingProperty as Symbol ?? method.ContainingIndexer as Symbol ?? method)
            .ToDisplayString(SymbolDisplayFormat.Diagnostic);

    private void ValidateStructLayouts()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        {
            foreach (FieldSymbol field in type.StaticFields)
                if (field.Declaration.Initializer is null && TypeFacts.ContainsReferenceStorage(field.Type))
                    _diagnostics.Report(field.Declaration.Type.NameToken.Location,
                        $"static field '{field.Name}' contains a reference and requires explicit initialization",
                        DiagnosticIds.ReferenceRequiresInitializer);
            foreach (FieldSymbol field in type.Fields)
            {
                if (ContainsStructByValue(field.Type, type, []))
                {
                    _diagnostics.Report(
                        field.Declaration.Type.NameToken.Location,
                        $"struct '{type.Name}' has a recursive by-value field '{field.Name}'; use a pointer or array handle instead",
                        DiagnosticIds.RecursiveValueLayout);
                }
            }
        }
    }

    private void ValidateAbstractValueStorage()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        foreach (FieldSymbol field in type.Fields.Concat(type.StaticFields))
            if (field.Type is StructTypeSymbol { IsAbstract: true } abstractType)
                _diagnostics.Report(field.Declaration.Type.NameToken.Location,
                    $"abstract struct '{abstractType.Name}' cannot be stored in field '{field.Name}'",
                    DiagnosticIds.AbstractValueStorage);
        var signatures = _functionBodies.Select(entry => (entry.Symbol, Location: entry.Body.OpenBraceToken.Location))
            .Concat(_functionSymbols.Select(entry => (entry.Value, entry.Key.IdentifierToken.Location)))
            .Concat(_structSymbols.Values.SelectMany(type => type.Methods.Select(method => (method, type.Declaration.IdentifierToken.Location))))
            .Concat(_interfaceSymbols.SelectMany(entry => entry.Value.AllMethods.Select(method => (method, entry.Key.IdentifierToken.Location))));
        foreach (var (symbol, location) in signatures.DistinctBy(entry => entry.Item1))
        {
            if (symbol.ReturnType is StructTypeSymbol { IsAbstract: true } ||
                symbol.Parameters.Any(parameter => parameter.Type is StructTypeSymbol { IsAbstract: true }))
                _diagnostics.Report(location, "abstract structs cannot be passed or returned by value",
                    DiagnosticIds.AbstractValueInSignature);
            if (TypeFacts.IsPinned(symbol.ReturnType) ||
                symbol.Parameters.Any(parameter => TypeFacts.IsPinned(parameter.Type)))
                _diagnostics.Report(location,
                    "pinned values and aggregates containing pinned fields cannot be passed or returned by value; use a pointer or reference",
                    DiagnosticIds.PinnedRelocation);
        }
    }

    private static bool ContainsStructByValue(
        TypeSymbol candidate,
        StructTypeSymbol target,
        HashSet<StructTypeSymbol> visited)
    {
        if (candidate is not StructTypeSymbol structType)
        {
            return false;
        }

        if (TypeIdentity.AreSame(structType, target))
        {
            return true;
        }

        if (!visited.Add(structType))
        {
            return false;
        }

        return structType.Fields.Any(field => ContainsStructByValue(field.Type, target, visited));
    }

    private void DeclareStructMethods()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
            foreach (PropertySymbol property in type.Properties)
            {
                if (property.Getter is not null)
                    methods.Add(property.Getter);
                if (property.Setter is not null)
                    methods.Add(property.Setter);
            }
            foreach (IndexerSymbol indexer in type.Indexers)
            {
                if (indexer.Getter is not null)
                    methods.Add(indexer.Getter);
                if (indexer.Setter is not null)
                    methods.Add(indexer.Setter);
            }

            foreach (MethodDeclarationSyntax methodSyntax in declaration.Methods)
            {
                if (type.FindField(methodSyntax.IdentifierToken.Text) is not null)
                {
                    _diagnostics.Report(
                        methodSyntax.IdentifierToken.Location,
                        $"struct '{type.Name}' already contains field '{methodSyntax.IdentifierToken.Text}'",
                        DiagnosticIds.DuplicateDeclaration);
                }

                if (type.FindProperty(methodSyntax.IdentifierToken.Text) is not null)
                {
                    _diagnostics.Report(
                        methodSyntax.IdentifierToken.Location,
                        $"struct '{type.Name}' already contains property '{methodSyntax.IdentifierToken.Text}'",
                        DiagnosticIds.DuplicateDeclaration);
                }

                TypeSymbol returnType = TypeResolver.ResolveReturnType(
                    methodSyntax.ReturnType,
                    scope,
                    _diagnostics);
                ImmutableArray<ParameterSymbol> parameters = BindParameters(
                    methodSyntax.Parameters,
                    scope);

                var method = new FunctionSymbol(
                    methodSyntax.IdentifierToken.Text,
                    type,
                    returnType,
                    parameters,
                    methodSyntax);

                FunctionSymbol? sameName = methods.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, method.Name, StringComparison.Ordinal));
                if (sameName is not null && !CanFormReadonlyOverloadPair(sameName, method))
                {
                    _diagnostics.Report(
                        methodSyntax.IdentifierToken.Location,
                        $"method overloading is not supported yet; struct '{type.Name}' may declare only one method named '{method.Name}'",
                        DiagnosticIds.MethodOverloadingNotSupported);
                    continue;
                }

                methods.Add(method);
                if (methodSyntax.Body is not null)
                {
                    _functionBodies.Add((method, methodSyntax.Body, scope));
                }
            }

            type.SetMethods(methods.ToImmutable());
        }
    }

    private void DeclareStructProperties()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var properties = ImmutableArray.CreateBuilder<PropertySymbol>();
            foreach (PropertyDeclarationSyntax syntax in declaration.Properties)
            {
                if (properties.Any(property => string.Equals(property.Name, syntax.IdentifierToken.Text, StringComparison.Ordinal)))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"property '{syntax.IdentifierToken.Text}' is already declared in struct '{type.Name}'",
                        DiagnosticIds.DuplicateDeclaration);
                    continue;
                }
                if (type.FindField(syntax.IdentifierToken.Text) is not null)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"struct '{type.Name}' already contains field '{syntax.IdentifierToken.Text}'",
                        DiagnosticIds.DuplicateDeclaration);
                }
                if (syntax.IsStatic)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, "static properties are not supported in this iteration",
                        DiagnosticIds.StaticPropertyNotSupported);
                }

                TypeSymbol propertyType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var property = new PropertySymbol(
                    syntax.IdentifierToken.Text,
                    type,
                    propertyType,
                    syntax.IsPublic ? Accessibility.Public : Accessibility.Private,
                    syntax);

                PropertyAccessorDeclarationSyntax? getterSyntax = syntax.Getter;
                PropertyAccessorDeclarationSyntax? setterSyntax = syntax.Setter;
                if (syntax.Accessors.Count(accessor => accessor.IsGetter) > 1)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"property '{property.Name}' declares more than one getter",
                        DiagnosticIds.DuplicateGetter);
                if (syntax.Accessors.Count(accessor => accessor.IsSetter) > 1)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"property '{property.Name}' declares more than one setter",
                        DiagnosticIds.DuplicateSetter);
                if (getterSyntax is null && setterSyntax is null)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"property '{property.Name}' must declare a getter or setter",
                        DiagnosticIds.AccessorRequired);

                FunctionSymbol? getter = getterSyntax is null
                    ? null
                    : new FunctionSymbol($"get_{property.Name}", property, propertyType, [], getterSyntax);
                FunctionSymbol? setter = setterSyntax is null
                    ? null
                    : new FunctionSymbol(
                        $"set_{property.Name}",
                        property,
                        BuiltinTypes.Void,
                        [new ParameterSymbol("value", propertyType, 0)],
                        setterSyntax);
                property.SetAccessors(getter, setter);
                properties.Add(property);

                AddPropertyAccessorBody(getter, getterSyntax, syntax, scope);
                AddPropertyAccessorBody(setter, setterSyntax, syntax, scope);
            }

            type.SetProperties(properties.ToImmutable());
        }
    }

    private void AddPropertyAccessorBody(
        FunctionSymbol? accessor,
        PropertyAccessorDeclarationSyntax? accessorSyntax,
        PropertyDeclarationSyntax propertySyntax,
        FileSymbolScope scope)
    {
        if (accessor is null || accessorSyntax is null)
            return;

        if (accessorSyntax.Body is not null)
        {
            if (propertySyntax.IsAbstract)
                _diagnostics.Report(accessorSyntax.KeywordToken.Location, "abstract property accessors cannot have a body",
                    DiagnosticIds.AbstractAccessorHasBody);
            _functionBodies.Add((accessor, accessorSyntax.Body, scope));
        }
        else if (!propertySyntax.IsAbstract)
        {
            _diagnostics.Report(accessorSyntax.KeywordToken.Location, "property accessor without a body must be abstract",
                DiagnosticIds.NonAbstractAccessorWithoutBody);
        }
    }

    private void DeclareStructIndexers()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var indexers = ImmutableArray.CreateBuilder<IndexerSymbol>();
            foreach (IndexerDeclarationSyntax syntax in declaration.Indexers)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(syntax.Parameters, scope);
                if (parameters.IsEmpty)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "an indexer must declare at least one parameter",
                        DiagnosticIds.IndexerRequiresParameter);
                if (indexers.Any(candidate => HaveSameParameterTypes(candidate.Parameters, parameters)))
                {
                    _diagnostics.Report(syntax.ThisKeyword.Location, $"struct '{type.Name}' already declares an indexer with the same parameter types",
                        DiagnosticIds.DuplicateDeclaration);
                    continue;
                }
                if (syntax.IsStatic)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "static indexers are not supported",
                        DiagnosticIds.StaticIndexerNotSupported);

                TypeSymbol indexerType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var indexer = new IndexerSymbol(
                    type,
                    indexerType,
                    parameters,
                    syntax.IsPublic ? Accessibility.Public : Accessibility.Private,
                    syntax);
                FunctionSymbol? getter = syntax.Getter is null
                    ? null
                    : new FunctionSymbol(indexer.GetAccessorName(getter: true), indexer, indexerType, parameters, syntax.Getter);
                FunctionSymbol? setter = syntax.Setter is null
                    ? null
                    : new FunctionSymbol(
                        indexer.GetAccessorName(getter: false),
                        indexer,
                        BuiltinTypes.Void,
                        [.. parameters, new ParameterSymbol("value", indexerType, parameters.Length)],
                        syntax.Setter);
                if (getter is null && setter is null)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "an indexer must declare a getter or setter",
                        DiagnosticIds.AccessorRequired);
                indexer.SetAccessors(getter, setter);
                indexers.Add(indexer);
                AddIndexerAccessorBody(getter, syntax.Getter, syntax, scope);
                AddIndexerAccessorBody(setter, syntax.Setter, syntax, scope);
            }
            type.SetIndexers(indexers.ToImmutable());
        }
    }

    private void AddIndexerAccessorBody(
        FunctionSymbol? accessor,
        PropertyAccessorDeclarationSyntax? accessorSyntax,
        IndexerDeclarationSyntax indexerSyntax,
        FileSymbolScope scope)
    {
        if (accessor is null || accessorSyntax is null)
            return;
        if (accessorSyntax.Body is not null)
        {
            if (indexerSyntax.IsAbstract)
                _diagnostics.Report(accessorSyntax.KeywordToken.Location, "abstract indexer accessors cannot have a body",
                    DiagnosticIds.AbstractAccessorHasBody);
            _functionBodies.Add((accessor, accessorSyntax.Body, scope));
        }
        else if (!indexerSyntax.IsAbstract)
        {
            _diagnostics.Report(accessorSyntax.KeywordToken.Location, "indexer accessor without a body must be abstract",
                DiagnosticIds.NonAbstractAccessorWithoutBody);
        }
    }

    private static bool HaveSameParameterTypes(
        ImmutableArray<ParameterSymbol> first,
        ImmutableArray<ParameterSymbol> second) =>
        first.Length == second.Length &&
        first.Zip(second).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type));

    private static bool CanFormReadonlyOverloadPair(FunctionSymbol first, FunctionSymbol second) =>
        !first.IsStatic &&
        !second.IsStatic &&
        first.IsReadonly != second.IsReadonly &&
        first.Parameters.Length == second.Parameters.Length &&
        first.Parameters.Zip(second.Parameters).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type));

    private void ValidateInterfaceImplementations()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        {
            foreach (InterfaceTypeSymbol @interface in type.ImplementedInterfaces)
            {
                foreach (FunctionSymbol required in @interface.AllMethods)
                {
                    FunctionSymbol? implementation = type.FindInterfaceImplementation(required);
                    if (implementation is null || implementation.IsStatic || !implementation.IsPublic)
                        _diagnostics.Report(type.Declaration.IdentifierToken.Location, $"struct '{type.Name}' does not implement interface method '{@interface.Name}.{required.Name}'",
                            DiagnosticIds.UnimplementedInterfaceMember);
                }
            }
        }
    }

    private void DeclareStructLifecycleFunctions()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var constructors = ImmutableArray.CreateBuilder<FunctionSymbol>();
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConstructorDeclarationSyntax constructorSyntax in declaration.Constructors)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(constructorSyntax.Parameters, scope);
                string signature = TypeSignature.Parameters(parameters);
                var constructor = new FunctionSymbol(FunctionKind.Constructor, type, parameters, constructorSyntax,
                    constructorSyntax.IsPublic ? Accessibility.Public : Accessibility.Private);
                if (!signatures.Add(signature))
                {
                    _diagnostics.Report(constructor.Locations[0], $"constructor '{constructor.ToDisplayString(SymbolDisplayFormat.Diagnostic)}' is already declared",
                        DiagnosticIds.DuplicateDeclaration);
                    continue;
                }
                constructors.Add(constructor);
                _functionBodies.Add((constructor, constructorSyntax.Body, scope));
            }
            if (constructors.Count == 0 && type.BaseType is not null)
            {
                // Use the same body binder as an explicit empty constructor, including
                // overload/access checks and the base-before-fields initialization order.
                var constructor = new FunctionSymbol(FunctionKind.Constructor, type, [], declaration, Accessibility.Public);
                constructors.Add(constructor);
                _functionBodies.Add((constructor,
                    new BlockStatementSyntax(declaration.OpenBraceToken, [], declaration.CloseBraceToken), scope));
            }
            type.SetConstructors(constructors.ToImmutable());

            DestructorDeclarationSyntax[] destructors = declaration.Members
                .OfType<DestructorDeclarationSyntax>()
                .ToArray();
            if (destructors.Length > 1)
            {
                foreach (DestructorDeclarationSyntax duplicate in destructors.Skip(1))
                {
                    _diagnostics.Report(
                        duplicate.TildeToken.Location,
                        $"struct '{type.Name}' may declare only one destructor",
                        DiagnosticIds.DuplicateDestructor);
                }
            }

            DestructorDeclarationSyntax? destructorSyntax = destructors.FirstOrDefault();
            if (destructorSyntax is not null)
            {
                if (!string.Equals(destructorSyntax.IdentifierToken.Text, type.Name, StringComparison.Ordinal))
                {
                    _diagnostics.Report(
                        destructorSyntax.IdentifierToken.Location,
                        $"destructor name must match containing struct '{type.Name}'",
                        DiagnosticIds.DestructorNameMismatch);
                }

                var destructor = new FunctionSymbol(
                    FunctionKind.Destructor,
                    type,
                    [],
                    destructorSyntax,
                    destructorSyntax.IsPublic ? Accessibility.Public : Accessibility.Private);
                type.SetDestructor(destructor);
                _functionBodies.Add((destructor, destructorSyntax.Body, scope));
            }
        }
    }

    private void DeclareFunctions()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            FileSymbolScope scope = _treeScopes[tree];
            foreach (FunctionDeclarationSyntax declaration in tree.Root.Members.OfType<FunctionDeclarationSyntax>())
            {
                ImmutableArray<GenericParameterSymbol> typeParameters =
                    CreateGenericParameters(declaration.TypeParameters, @namespace);
                FileSymbolScope declarationScope = scope.WithTypeParameters(typeParameters);
                BindGenericConstraints(declaration.WhereClauses, typeParameters, declarationScope);
                if (declaration.IsExtern && declaration.IdentifierToken.Text is "malloc" or "calloc" or "free")
                {
                    _diagnostics.Report(
                        declaration.IdentifierToken.Location,
                        $"native symbol '{declaration.IdentifierToken.Text}' is reserved for Xenon memory operations",
                        DiagnosticIds.ReservedNativeSymbol);
                }

                TypeSymbol returnType = TypeResolver.ResolveReturnType(declaration.ReturnType, declarationScope, _diagnostics);
                ImmutableArray<ParameterSymbol> parameters = BindParameters(declaration.Parameters, declarationScope);
                var function = new FunctionSymbol(
                    declaration.IdentifierToken.Text,
                    @namespace,
                    returnType,
                    parameters,
                    declaration,
                    typeParameters);
                foreach (GenericParameterSymbol parameter in typeParameters)
                    parameter.SetDeclaringSymbol(function);

                if (!typeParameters.IsEmpty && (declaration.IsExtern || declaration.IsExport))
                    _diagnostics.Report(declaration.IdentifierToken.Location,
                        "generic declarations cannot be exposed through the C ABI before a concrete specialization exists",
                        DiagnosticIds.GenericSpecializationNotImplemented);

                ValidateExternalStructAbi(declaration, function);

                if (!@namespace.TryDeclareFunction(function))
                {
                    FunctionSymbol? previous = @namespace.FindFunction(function.Name);
                    _diagnostics.Report(
                        declaration.IdentifierToken.Location,
                        $"function '{@namespace.FullName}.{function.Name}' is already declared",
                        DiagnosticIds.DuplicateDeclaration,
                        previous?.Locations.Select(location => new RelatedDiagnosticLocation(location, "previous declaration")));
                    continue;
                }

                _functionSymbols.Add(declaration, function);
                if (declaration.Body is not null)
                {
                    _functionBodies.Add((function, declaration.Body, declarationScope));
                }
            }
        }
    }

    private void ValidateNativeSymbols()
    {
        var symbols = new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal);
        IEnumerable<FunctionSymbol> functions = _functionSymbols.Values.Concat(_structSymbols.Values.SelectMany(type =>
            type.Methods.Concat(type.Constructors).Concat(new[] { type.Destructor, type.InstanceInitializer }.OfType<FunctionSymbol>())))
            .Where(function => !function.IsGenericDefinition);
        foreach (FunctionSymbol function in functions)
        {
            string name = NativeSymbolNames.Get(function);
            if (symbols.TryAdd(name, function)) continue;
            FunctionSymbol previous = symbols[name];
            string? signature = NativeSymbolNames.GetAbiSignature(function, _constants.TargetLayout);
            string? previousSignature = NativeSymbolNames.GetAbiSignature(previous, _constants.TargetLayout);
            if (function.IsExtern && previous.IsExtern)
            {
                if (signature is null || previousSignature is null)
                {
                    _constants.RequireTargetLayout();
                    continue;
                }
                if (signature == previousSignature) continue;
            }
            TextLocation location = function.Declaration is FunctionDeclarationSyntax declaration
                ? declaration.IdentifierToken.Location : function.ContainingType!.Declaration.IdentifierToken.Location;
            _diagnostics.Report(location,
                $"native symbol '{name}' collides between '{previous.FullName}' and '{function.FullName}' with incompatible ABI or multiple definitions",
                DiagnosticIds.NativeSymbolCollision);
        }
    }

    private void ValidateExternalStructAbi(
        FunctionDeclarationSyntax declaration,
        FunctionSymbol function)
    {
        if (!declaration.IsExtern && !declaration.IsExport)
        {
            return;
        }

        if (function.ReturnType is OwnershipTypeSymbol returnOwnership)
        {
            _diagnostics.Report(
                declaration.ReturnType.NameToken.Location,
                $"external ABI does not support ownership type '{returnOwnership.OwnershipKind}'; use a raw pointer instead",
                DiagnosticIds.UnsupportedNativeOwnershipType);
        }

        if (function.ReturnType is StructTypeSymbol returnStruct)
        {
            _diagnostics.Report(
                declaration.ReturnType.NameToken.Location,
                $"external ABI does not yet support struct '{returnStruct.Name}' by value; use a pointer instead",
                DiagnosticIds.UnsupportedNativeStructByValue);
        }

        if (function.ReturnType is ArrayTypeSymbol)
        {
            _diagnostics.Report(
                declaration.ReturnType.NameToken.Location,
                "external ABI does not yet support Xenon array types directly; use a pointer and explicit length",
                DiagnosticIds.UnsupportedNativeArrayType);
        }

        for (int index = 0; index < function.Parameters.Length; index++)
        {
            TypeSymbol parameterType = function.Parameters[index].Type;
            if (parameterType is OwnershipTypeSymbol parameterOwnership)
            {
                _diagnostics.Report(
                    declaration.Parameters[index].Type.NameToken.Location,
                    $"external ABI does not support ownership type '{parameterOwnership.OwnershipKind}'; use a raw pointer instead",
                    DiagnosticIds.UnsupportedNativeOwnershipType);
            }
            else if (parameterType is StructTypeSymbol parameterStruct)
            {
                _diagnostics.Report(
                    declaration.Parameters[index].Type.NameToken.Location,
                    $"external ABI does not yet support struct '{parameterStruct.Name}' by value; use a pointer instead",
                    DiagnosticIds.UnsupportedNativeStructByValue);
            }
            else if (parameterType is ArrayTypeSymbol)
            {
                _diagnostics.Report(
                    declaration.Parameters[index].Type.NameToken.Location,
                    "external ABI does not yet support Xenon array types directly; use a pointer and explicit length",
                    DiagnosticIds.UnsupportedNativeArrayType);
            }
        }
    }

    private ImmutableArray<ParameterSymbol> BindParameters(
        ImmutableArray<ParameterSyntax> parameterSyntax,
        FileSymbolScope scope)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < parameterSyntax.Length; index++)
        {
            ParameterSyntax syntax = parameterSyntax[index];
            TypeSymbol type = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);

            if (TypeIdentity.AreSame(type, BuiltinTypes.Void))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, "parameter type cannot be 'void'",
                    DiagnosticIds.VoidParameterType);
            }

            if (!names.Add(syntax.IdentifierToken.Text))
            {
                _diagnostics.Report(
                    syntax.IdentifierToken.Location,
                    $"parameter '{syntax.IdentifierToken.Text}' is already declared",
                    DiagnosticIds.DuplicateDeclaration);
            }

            parameters.Add(new ParameterSymbol(syntax.IdentifierToken.Text, type, index, syntax.Type.IsBindingReadonly(), declaration: syntax));
        }

        return parameters.ToImmutable();
    }

    private static ImmutableArray<GenericParameterSymbol> CreateGenericParameters(
        GenericParameterListSyntax? syntax, Symbol containingSymbol)
    {
        if (syntax is null) return [];
        return syntax.Parameters.Select((parameter, ordinal) =>
            new GenericParameterSymbol(parameter.IdentifierToken.Text, ordinal, containingSymbol, parameter)).ToImmutableArray();
    }

    private void BindGenericConstraints(ImmutableArray<WhereClauseSyntax> clauses,
        ImmutableArray<GenericParameterSymbol> parameters, FileSymbolScope scope)
    {
        var constraints = parameters.ToDictionary(
            parameter => parameter,
            _ => ImmutableArray.CreateBuilder<GenericConstraintSymbol>());
        foreach (WhereClauseSyntax clause in clauses)
        {
            GenericParameterSymbol? parameter = parameters.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, clause.TypeParameterToken.Text, StringComparison.Ordinal));
            if (parameter is null)
            {
                _diagnostics.Report(clause.TypeParameterToken.Location,
                    $"where clause references unknown generic parameter '{clause.TypeParameterToken.Text}'",
                    DiagnosticIds.UnknownConstraintTypeParameter);
                continue;
            }

            foreach (GenericConstraintSyntax constraintSyntax in clause.Constraints)
            {
                TypeSyntax syntax = constraintSyntax.Type;
                if (syntax is not NamedTypeSyntax named || named.TypeArguments is not null)
                {
                    _diagnostics.Report(syntax.NameToken.Location,
                        "a generic constraint must name a struct, interface, or structural template",
                        DiagnosticIds.InvalidGenericConstraint);
                    continue;
                }

                string[] parts = named.NameParts.Select(part => part.Text).ToArray();
                Symbol? target = scope.ResolveConstraintTarget(parts, named.NameToken.Location, _diagnostics);
                GenericConstraintKind kind = target switch
                {
                    StructTypeSymbol => GenericConstraintKind.BaseStruct,
                    InterfaceTypeSymbol => GenericConstraintKind.Interface,
                    TemplateSymbol => GenericConstraintKind.StructuralTemplate,
                    _ => GenericConstraintKind.StructuralTemplate,
                };

                if (target is not (StructTypeSymbol or InterfaceTypeSymbol or TemplateSymbol))
                {
                    _diagnostics.Report(named.NameToken.Location,
                        $"unknown or invalid generic constraint '{named.Name}'",
                        DiagnosticIds.InvalidGenericConstraint);
                    continue;
                }
                if (constraints[parameter].Any(existing => ReferenceEquals(existing.Target, target)))
                {
                    _diagnostics.Report(named.NameToken.Location,
                        $"constraint '{named.Name}' is already specified for '{parameter.Name}'",
                        DiagnosticIds.DuplicateGenericConstraint);
                    continue;
                }

                constraints[parameter].Add(new GenericConstraintSymbol(kind, target, constraintSyntax));
                _semanticInfo.Symbols[constraintSyntax] = SymbolInfo.FromSymbol(target);
                _semanticInfo.Symbols[syntax] = SymbolInfo.FromSymbol(target);
            }
        }

        foreach (GenericParameterSymbol parameter in parameters)
            parameter.SetConstraints(constraints[parameter].ToImmutable());
    }

    private void RecordDeclarations(Symbol symbol)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
            _semanticInfo.Declarations.TryAdd(reference.Declaration, symbol);

        IEnumerable<Symbol> children = symbol switch
        {
            NamespaceSymbol ns => ns.Namespaces.Cast<Symbol>().Concat(ns.Types).Concat(ns.Templates).Concat(ns.Functions).Concat(ns.Constants),
            StructTypeSymbol type => type.TypeParameters.Cast<Symbol>().Concat(type.GetMembers()),
            DeclaredTypeSymbol type => type.GetMembers(),
            FunctionSymbol function => function.TypeParameters.Cast<Symbol>().Concat(function.Parameters),
            TemplateSymbol template => template.Members,
            TemplateMethodRequirementSymbol method => method.Parameters,
            TemplateConstructorRequirementSymbol constructor => constructor.Parameters,
            TemplateIndexerRequirementSymbol indexer => indexer.Parameters,
            PropertySymbol property => new[] { property.Getter, property.Setter }.OfType<Symbol>(),
            IndexerSymbol indexer => indexer.Parameters.Cast<Symbol>()
                .Concat(new[] { indexer.Getter, indexer.Setter }.OfType<Symbol>()),
            InterfacePropertySymbol property => new[] { property.Getter, property.Setter }.OfType<Symbol>(),
            InterfaceIndexerSymbol indexer => indexer.Parameters.Cast<Symbol>()
                .Concat(new[] { indexer.Getter, indexer.Setter }.OfType<Symbol>()),
            _ => [],
        };
        foreach (Symbol child in children)
            RecordDeclarations(child);
    }
}
