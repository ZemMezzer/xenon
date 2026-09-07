using System.Collections.Concurrent;
using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler;

public sealed class Compilation
{
    private readonly ConcurrentDictionary<SyntaxTree, SemanticModel> _semanticModels =
        new(ReferenceEqualityComparer.Instance);
    private readonly object _nativeReachabilityLock = new();
    private NativeReachability? _nativeReachability;

    private Compilation(ImmutableArray<SyntaxTree> syntaxTrees, CompilationOptions options,
        ImmutableArray<CompilationReference> references, ITargetTypeLayout? targetLayout,
        CancellationToken cancellationToken)
    {
        ValidateTrees(syntaxTrees);
        ValidateReferences(references);
        SyntaxTrees = syntaxTrees;
        Options = options;
        References = references;
        TargetLayout = targetLayout;
        cancellationToken.ThrowIfCancellationRequested();
        TypeFactory = new TypeFactory();
        SemanticModel = SemanticAnalyzer.Analyze(syntaxTrees, TypeFactory,
            references.Select(reference => reference.GlobalNamespace).ToImmutableArray(),
            references.Select(reference => reference.GenericImplementations).ToImmutableArray(),
            targetLayout, cancellationToken);
        Diagnostics = SemanticModel.Diagnostics;
    }

    public ImmutableArray<SyntaxTree> SyntaxTrees { get; }
    public CompilationOptions Options { get; }
    public ImmutableArray<CompilationReference> References { get; }
    public ITargetTypeLayout? TargetLayout { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public SemanticModel SemanticModel { get; }
    public TypeFactory TypeFactory { get; }
    public GenericImplementationStore GenericImplementations => SemanticModel.GenericImplementations;
    public bool HasErrors => Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
    public bool RequiresTargetLayout => SemanticModel.RequiresTargetLayout;

    public static Compilation Create(params SourceText[] sources) =>
        Create(new CompilationOptions(), [], CancellationToken.None, sources);

    public static Compilation Create(CancellationToken cancellationToken, params SourceText[] sources) =>
        Create(new CompilationOptions(), [], cancellationToken, sources);

    public static Compilation Create(CompilationOptions options,
        IEnumerable<CompilationReference>? references, params SourceText[] sources) =>
        Create(options, references, CancellationToken.None, sources);

    public static Compilation Create(CompilationOptions options,
        IEnumerable<CompilationReference>? references, CancellationToken cancellationToken,
        params SourceText[] sources)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        return new Compilation(
            sources.Select(source => SyntaxTree.Parse(source, cancellationToken)).ToImmutableArray(),
            options, references?.ToImmutableArray() ?? [], null, cancellationToken);
    }

    public static Compilation Create(IEnumerable<SyntaxTree> syntaxTrees,
        CompilationOptions? options = null, IEnumerable<CompilationReference>? references = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        return new Compilation(syntaxTrees.ToImmutableArray(), options ?? new CompilationOptions(),
            references?.ToImmutableArray() ?? [], null, cancellationToken);
    }

    public Compilation WithOptions(CompilationOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options == Options ? this : Derive(SyntaxTrees, options, References, TargetLayout, cancellationToken);
    }

    public Compilation WithReferences(IEnumerable<CompilationReference> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);
        ImmutableArray<CompilationReference> value = references.ToImmutableArray();
        return References.SequenceEqual(value) ? this : Derive(SyntaxTrees, Options, value, TargetLayout, cancellationToken);
    }

    public Compilation AddReferences(params CompilationReference[] references)
    {
        ArgumentNullException.ThrowIfNull(references);
        return references.Length == 0 ? this : WithReferences(References.AddRange(references));
    }

    public Compilation RemoveReferences(params CompilationReference[] references)
    {
        ArgumentNullException.ThrowIfNull(references);
        var remove = references.ToHashSet();
        if (remove.Any(reference => !References.Contains(reference)))
            throw new ArgumentException("Every reference to remove must belong to this compilation.", nameof(references));
        return remove.Count == 0 ? this : WithReferences(References.Where(reference => !remove.Contains(reference)));
    }

    public Compilation ReplaceSyntaxTree(SyntaxTree oldTree, SyntaxTree newTree,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(oldTree);
        ArgumentNullException.ThrowIfNull(newTree);
        int index = IndexOfTree(oldTree);
        if (index < 0)
            throw new ArgumentException("The syntax tree does not belong to this compilation.", nameof(oldTree));
        if (ReferenceEquals(oldTree, newTree)) return this;
        SyntaxTree replacement = newTree.SourceFileId == oldTree.SourceFileId ? newTree :
            SyntaxTree.Parse(SourceText.From(newTree.Source.Text, newTree.Source.Path, oldTree.SourceFileId), cancellationToken);
        if (SyntaxTrees.Where((_, candidateIndex) => candidateIndex != index)
            .Any(tree => tree.SourceFileId == replacement.SourceFileId))
            throw new ArgumentException("A syntax tree with the replacement source identity already belongs to this compilation.", nameof(newTree));
        return Derive(SyntaxTrees.SetItem(index, replacement), Options, References, TargetLayout, cancellationToken);
    }

    public Compilation AddSyntaxTrees(params SyntaxTree[] syntaxTrees) => AddSyntaxTrees((IEnumerable<SyntaxTree>)syntaxTrees);

    public Compilation AddSyntaxTrees(IEnumerable<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        ImmutableArray<SyntaxTree> added = syntaxTrees.ToImmutableArray();
        return added.IsEmpty ? this : Derive(SyntaxTrees.AddRange(added), Options, References, TargetLayout, cancellationToken);
    }

    public Compilation RemoveSyntaxTrees(params SyntaxTree[] syntaxTrees) => RemoveSyntaxTrees((IEnumerable<SyntaxTree>)syntaxTrees);

    public Compilation RemoveSyntaxTrees(IEnumerable<SyntaxTree> syntaxTrees,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntaxTrees);
        var remove = new HashSet<SyntaxTree>(syntaxTrees, ReferenceEqualityComparer.Instance);
        if (remove.Any(tree => IndexOfTree(tree) < 0))
            throw new ArgumentException("Every syntax tree to remove must belong to this compilation.", nameof(syntaxTrees));
        return remove.Count == 0 ? this : Derive(SyntaxTrees.Where(tree => !remove.Contains(tree)).ToImmutableArray(),
            Options, References, TargetLayout, cancellationToken);
    }

    /// <summary>Specializes this snapshot for an ABI without mutating it.</summary>
    public Compilation WithTargetLayout(ITargetTypeLayout targetLayout) => WithTargetLayout(targetLayout, CancellationToken.None);

    public Compilation WithTargetLayout(ITargetTypeLayout targetLayout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetLayout);
        return ReferenceEquals(TargetLayout, targetLayout) ? this :
            Derive(SyntaxTrees, Options, References, targetLayout, cancellationToken);
    }

    public SemanticModel GetSemanticModel(SyntaxTree tree, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        cancellationToken.ThrowIfCancellationRequested();
        if (IndexOfTree(tree) < 0)
            throw new ArgumentException("The syntax tree does not belong to this compilation.", nameof(tree));
        SemanticModel model = _semanticModels.GetOrAdd(tree, static (syntaxTree, compilation) =>
            compilation.SemanticModel.ForTree(syntaxTree), this);
        cancellationToken.ThrowIfCancellationRequested();
        return model;
    }

    /// <summary>True when this snapshot, rather than one of its references, defines the symbol.</summary>
    public bool IsSymbolDefinedHere(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        Symbol root = symbol;
        while (root.ContainingSymbol is Symbol containing) root = containing;
        return ReferenceEquals(root, SemanticModel.GlobalNamespace);
    }

    /// <summary>True when this build emits the symbol either from source or a statically consumed XELIB.</summary>
    public bool IsSymbolDefinedInCurrentArtifact(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        Symbol root = symbol;
        while (root.ContainingSymbol is Symbol containing) root = containing;
        return ReferenceEquals(root, SemanticModel.GlobalNamespace) || References
            .OfType<LibraryCompilationReference>()
            .Any(reference => ReferenceEquals(root, reference.GlobalNamespace));
    }

    public ImmutableArray<BoundFunction> GetStaticImplementationFunctions()
        => GetNativeReachability().Functions;

    /// <summary>
    /// Gets implementation-only native ABI roots referenced by portable generic bodies or by
    /// externally inheritable virtual layouts owned by this compilation. These symbols are not
    /// part of the language-level export surface, but a separately linked consumer may reference
    /// them while instantiating a generic body or constructing a derived virtual table.
    /// </summary>
    public ImmutableArray<Symbol> GetImplementationNativeAbiRoots()
    {
        var result = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);

        void AddSymbol(Symbol symbol)
        {
            switch (symbol)
            {
                case FunctionSymbol function when IsSymbolDefinedHere(function):
                    result.Add(function);
                    break;
                case FieldSymbol { IsStatic: true } field when IsSymbolDefinedHere(field):
                    result.Add(field);
                    break;
                case PropertySymbol property:
                    if (property.Getter is { } getter) AddSymbol(getter);
                    if (property.Setter is { } setter) AddSymbol(setter);
                    break;
                case IndexerSymbol indexer:
                    if (indexer.Getter is { } indexerGetter) AddSymbol(indexerGetter);
                    if (indexer.Setter is { } indexerSetter) AddSymbol(indexerSetter);
                    break;
            }
        }

        void Collect(BoundNode body) => XelibBodyCodec.Collect(body, _ => { }, AddSymbol);

        foreach (var (definition, implementation) in GenericImplementations.Functions)
            if (IsSymbolDefinedHere(definition) && implementation.PortableBody is { } body)
                Collect(body);

        foreach (var (definition, implementation) in GenericImplementations.Structs)
        {
            if (!IsSymbolDefinedHere(definition)) continue;
            if (implementation.PortableInstanceInitializer is { } instanceInitializer)
                Collect(instanceInitializer.Body);
            foreach (BoundFunction staticInitializer in implementation.PortableStaticFieldInitializers)
                Collect(staticInitializer.Body);
            foreach (ConstantSymbol constant in definition.Constants)
                if (constant.BoundValue is { } value)
                    Collect(value);
        }

        void CollectVirtualLayoutRoots(NamespaceSymbol @namespace)
        {
            foreach (StructTypeSymbol type in @namespace.Structs.Where(type =>
                         IsSymbolDefinedHere(type) && type.IsPublic && !type.IsSealed))
                foreach (FunctionSymbol function in type.VirtualMethods.Where(IsSymbolDefinedInCurrentArtifact))
                    result.Add(function);
            foreach (NamespaceSymbol child in @namespace.Namespaces)
                CollectVirtualLayoutRoots(child);
        }
        CollectVirtualLayoutRoots(SemanticModel.GlobalNamespace);

        return result.OrderBy(symbol => symbol.QualifiedName, StringComparer.Ordinal).ToImmutableArray();
    }

    /// <summary>Whether an imported XELIB symbol is required by this native link unit.</summary>
    public bool IsImportedSymbolNativeReachable(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (GetOwningLibrary(symbol) is null) return true;
        NativeReachability reachability = GetNativeReachability();
        return symbol switch
        {
            FunctionSymbol function => reachability.FunctionSymbols.Contains(function),
            FieldSymbol field => reachability.StaticFields.Contains(field),
            DeclaredTypeSymbol type => reachability.Types.Contains(type),
            _ => true,
        };
    }

    public bool IsImportedDispatchTypeNativeReachable(StructTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (GetOwningLibrary(type) is null) return true;
        NativeReachability reachability = GetNativeReachability();
        return reachability.VirtualDispatchTypes.Contains(type) ||
            reachability.InterfaceDispatchTypes.Contains(type);
    }

    public bool IsImportedInterfaceDispatchTypeNativeReachable(StructTypeSymbol type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return GetOwningLibrary(type) is null ||
            GetNativeReachability().InterfaceDispatchTypes.Contains(type);
    }

    private NativeReachability GetNativeReachability()
    {
        if (_nativeReachability is { } cached) return cached;
        lock (_nativeReachabilityLock)
        {
            if (_nativeReachability is { } winner) return winner;
            return _nativeReachability = ComputeNativeReachability();
        }
    }

    private NativeReachability ComputeNativeReachability()
    {
        ImmutableArray<BoundFunction> source = SemanticModel.Functions;
        var available = new Dictionary<FunctionSymbol, BoundFunction>(ReferenceEqualityComparer.Instance);
        foreach (BoundFunction function in References.OfType<LibraryCompilationReference>()
                     .SelectMany(reference => reference.ImplementationFunctions))
            available.TryAdd(function.Symbol, function);
        var selected = new HashSet<FunctionSymbol>(ReferenceEqualityComparer.Instance);
        var pending = new Queue<FunctionSymbol>();
        var reachableTypes = new HashSet<DeclaredTypeSymbol>(ReferenceEqualityComparer.Instance);
        var reachableVirtualDispatchTypes = new HashSet<StructTypeSymbol>(ReferenceEqualityComparer.Instance);
        var reachableInterfaceDispatchTypes = new HashSet<StructTypeSymbol>(ReferenceEqualityComparer.Instance);
        var reachableStaticFields = new HashSet<FieldSymbol>(ReferenceEqualityComparer.Instance);
        var typeReasons = new Dictionary<TypeSymbol, TypeReachabilityReason>(ReferenceEqualityComparer.Instance);
        void Select(FunctionSymbol function)
        {
            if (available.ContainsKey(function) && selected.Add(function)) pending.Enqueue(function);
        }

        void MarkType(TypeSymbol type, TypeReachabilityReason reason = TypeReachabilityReason.Reference)
        {
            reason |= TypeReachabilityReason.Reference;
            TypeReachabilityReason previous = typeReasons.GetValueOrDefault(type);
            TypeReachabilityReason added = reason & ~previous;
            if (added == TypeReachabilityReason.None) return;
            typeReasons[type] = previous | reason;
            switch (type)
            {
                case StructTypeSymbol structure:
                    reachableTypes.Add(structure);
                    if ((added & TypeReachabilityReason.Reference) != 0)
                    {
                        if (structure.BaseType is { } baseType) MarkType(baseType);
                        foreach (InterfaceTypeSymbol @interface in structure.ImplementedInterfaces)
                            MarkType(@interface);
                        foreach (FieldSymbol field in structure.AllInstanceFields) MarkType(field.Type);
                    }
                    if ((added & TypeReachabilityReason.Construct) != 0)
                    {
                        SelectInstanceInitializers(structure);
                        if (structure.HasVirtualDispatch)
                            MarkType(structure, TypeReachabilityReason.VirtualDispatch);
                    }
                    if ((added & TypeReachabilityReason.Destruct) != 0 &&
                        TypeFacts.GetCompleteDestructor(structure) is { } destructor)
                        Select(destructor);
                    bool needsVirtualDispatch = (added & (TypeReachabilityReason.VirtualDispatch |
                        TypeReachabilityReason.InterfaceDispatch)) != 0;
                    if (needsVirtualDispatch)
                        reachableVirtualDispatchTypes.Add(structure);
                    if (needsVirtualDispatch && structure.IsConcreteType && structure.HasVirtualDispatch)
                        foreach (FunctionSymbol target in structure.VirtualMethods) Select(target);
                    if ((added & TypeReachabilityReason.InterfaceDispatch) != 0)
                    {
                        reachableInterfaceDispatchTypes.Add(structure);
                        if (!structure.IsConcreteType) break;
                        foreach (InterfaceTypeSymbol @interface in structure.ImplementedInterfaces)
                        foreach (FunctionSymbol required in @interface.AllMethods)
                            if (structure.FindInterfaceImplementation(required) is { } implementation)
                                Select(implementation);
                    }
                    break;
                case InterfaceTypeSymbol @interface:
                    reachableTypes.Add(@interface);
                    if ((added & TypeReachabilityReason.Reference) != 0)
                        foreach (InterfaceTypeSymbol baseInterface in @interface.BaseInterfaces) MarkType(baseInterface);
                    break;
                case PointerTypeSymbol pointer: MarkType(pointer.ElementType); break;
                case ReferenceTypeSymbol reference: MarkType(reference.ElementType); break;
                case ArrayTypeSymbol array:
                    MarkType(array.ElementType, (added & TypeReachabilityReason.Destruct) != 0
                        ? TypeReachabilityReason.Destruct
                        : TypeReachabilityReason.Reference);
                    break;
                case AtomicTypeSymbol atomic:
                    MarkType(atomic.ElementType, (added & TypeReachabilityReason.Destruct) != 0
                        ? TypeReachabilityReason.Destruct
                        : TypeReachabilityReason.Reference);
                    break;
                case OwnershipTypeSymbol ownership:
                    MarkType(ownership.ElementType);
                    if ((added & TypeReachabilityReason.Destruct) != 0 &&
                        ownership.CompleteDestructor is { } ownershipDestructor)
                        Select(ownershipDestructor);
                    break;
                case LifetimeModifierTypeSymbol modifier:
                    if ((added & TypeReachabilityReason.Destruct) != 0)
                    {
                        if (modifier is StorageTypeSymbol { CompleteDestructor: { } storageDestructor })
                            Select(storageDestructor);
                        else
                            MarkType(modifier.ElementType, TypeReachabilityReason.Destruct);
                    }
                    else
                        MarkType(modifier.ElementType);
                    break;
                case FunctionPointerTypeSymbol functionPointer:
                    MarkType(functionPointer.ReturnType);
                    foreach (TypeSymbol parameter in functionPointer.ParameterTypes) MarkType(parameter);
                    break;
            }
        }

        void SelectInstanceInitializers(StructTypeSymbol structure)
        {
            if (structure.BaseType is { } baseType) SelectInstanceInitializers(baseType);
            if (structure.InstanceInitializer is { } initializer) Select(initializer);
        }

        void MarkStaticField(FieldSymbol field)
        {
            if (!field.IsStatic || !reachableStaticFields.Add(field)) return;
            MarkType(field.ContainingType);
            MarkType(field.Type);
            if (field.IsThreadLocal)
                MarkDestructionRequirement(field.Type);
            foreach (FunctionSymbol initializer in available.Keys.Where(function =>
                         function.FunctionKind == FunctionKind.ThreadLocalInitializer &&
                         ReferenceEquals(function.ThreadLocalField, field)))
                Select(initializer);
        }

        void MarkDestructionRequirement(TypeSymbol type)
        {
            bool requiresDestruction = TypeFacts.GetCompleteDestructor(type) is not null ||
                type is ArrayTypeSymbol array &&
                TypeFacts.GetCompleteDestructor(array.ElementType) is not null;
            if (requiresDestruction)
                MarkType(type, TypeReachabilityReason.Destruct);
        }

        void MarkSymbol(Symbol symbol)
        {
            switch (symbol)
            {
                case FunctionSymbol function: Select(function); break;
                case FieldSymbol field: MarkStaticField(field); break;
                case PropertySymbol property:
                    if (property.Getter is { } getter) Select(getter);
                    if (property.Setter is { } setter) Select(setter);
                    break;
                case IndexerSymbol indexer:
                    if (indexer.Getter is { } indexerGetter) Select(indexerGetter);
                    if (indexer.Setter is { } indexerSetter) Select(indexerSetter);
                    break;
            }
        }

        void MarkOperation(BoundNode node)
        {
            switch (node)
            {
                case BoundAssignmentExpression assignment
                    when assignment.OperatorKind == SyntaxKind.EqualsToken &&
                         !IsSameScalarStorage(assignment):
                    bool mayReplaceLiveValue =
                        assignment.MovedPlaceReinitialization != MovedPlaceReinitializationState.DefinitelyMoved &&
                        (assignment.RequiresRuntimeInitializationCheck || !assignment.IsInitialization);
                    if (mayReplaceLiveValue || IsCleanupTrackedProjection(assignment.Target))
                        MarkDestructionRequirement(assignment.Target.Type);
                    break;
                case BoundCompareExchangeExpression
                {
                    Target.Type: AtomicTypeSymbol comparedAtomic,
                }:
                    MarkDestructionRequirement(comparedAtomic);
                    break;
                case BoundStructConstructionExpression construction:
                    MarkType(construction.StructType, TypeReachabilityReason.Construct);
                    break;
                case BoundConstructorCallExpression constructor:
                    MarkType(constructor.StructType, TypeReachabilityReason.VirtualDispatch);
                    break;
                case BoundNewExpression { StructType: { } allocated } allocation:
                    MarkType(allocated, allocation.Constructor is null
                        ? TypeReachabilityReason.Construct
                        : TypeReachabilityReason.VirtualDispatch);
                    break;
                case BoundStorageConstructExpression
                {
                    ValueType: StructTypeSymbol stored,
                    Value: null,
                } storage:
                    MarkType(stored, storage.Constructor is null
                        ? TypeReachabilityReason.Construct
                        : TypeReachabilityReason.VirtualDispatch);
                    break;
                case BoundArrayCreationExpression array:
                    TypeSymbol element = array.ElementType is AtomicTypeSymbol atomic
                        ? atomic.ElementType : array.ElementType;
                    if (element is StructTypeSymbol arrayElement)
                        MarkType(arrayElement, TypeReachabilityReason.Construct);
                    if (array.Storage == ArrayStorageKind.Stack)
                        MarkDestructionRequirement(array.ElementType);
                    break;
                case BoundDefaultValueExpression { ValueType: StructTypeSymbol defaulted }
                    when defaulted.HasVirtualDispatch:
                    MarkType(defaulted, TypeReachabilityReason.VirtualDispatch);
                    break;
                case BoundInterfaceConversionExpression conversion:
                    MarkType(conversion.SourceType, TypeReachabilityReason.InterfaceDispatch);
                    break;
                case BoundMethodCallExpression call when call.Method.VTableSlot is not null:
                    MarkVirtualReceiver(call.Receiver.Type, call.IsPointerAccess);
                    break;
                case BoundCompoundAccessorAssignmentExpression assignment
                    when assignment.Getter.VTableSlot is not null || assignment.Setter.VTableSlot is not null:
                    MarkVirtualReceiver(assignment.Receiver.Type, assignment.IsPointerAccess);
                    break;
                case BoundPropertySetExpression property when property.Property.Setter?.VTableSlot is not null:
                    MarkVirtualReceiver(property.Receiver.Type, property.IsPointerAccess);
                    break;
                case BoundIndexerSetExpression indexer when indexer.Indexer.Setter?.VTableSlot is not null:
                    MarkVirtualReceiver(indexer.Receiver.Type, pointerAccess: false);
                    break;
                case BoundFreeExpression
                {
                    Destructor.VTableSlot: not null,
                    Pointer.Type: PointerTypeSymbol { ElementType: StructTypeSymbol freed },
                }:
                    MarkType(freed, TypeReachabilityReason.VirtualDispatch);
                    break;
                case BoundDestroyFieldsExpression destruction:
                    foreach (FieldSymbol field in destruction.StructType.Fields)
                        MarkType(field.Type, TypeReachabilityReason.Destruct);
                    if (destruction.StructType.BaseType is { } destroyedBase)
                        MarkType(destroyedBase, TypeReachabilityReason.Destruct);
                    break;
            }
        }

        static bool IsSameScalarStorage(BoundAssignmentExpression assignment) =>
            assignment.Expression is BoundCopyExpression copy &&
            TryGetScalarProjection(copy.Source, out VariableSymbol? source, out ImmutableArray<FieldSymbol> sourcePath) &&
            TryGetScalarProjection(assignment.Target, out VariableSymbol? destination,
                out ImmutableArray<FieldSymbol> destinationPath) &&
            ReferenceEquals(source, destination) && sourcePath.SequenceEqual(destinationPath);

        static bool IsCleanupTrackedProjection(BoundExpression expression) =>
            TryGetScalarProjection(expression, out _, out _);

        static bool TryGetScalarProjection(
            BoundExpression expression,
            out VariableSymbol? variable,
            out ImmutableArray<FieldSymbol> path)
        {
            if (expression is BoundVariableExpression { Variable: LocalVariableSymbol or ParameterSymbol } root)
            {
                variable = root.Variable;
                path = [];
                return true;
            }
            if (expression is BoundMemberAccessExpression { IsPointerAccess: false } member &&
                TryGetScalarProjection(member.Receiver, out variable, out path))
            {
                path = path.Add(member.Field);
                return true;
            }
            if (expression is BoundLifetimeValueExpression value &&
                TryGetScalarProjection(value.Source, out variable, out path))
                return true;
            variable = null;
            path = [];
            return false;
        }

        void MarkVirtualReceiver(TypeSymbol type, bool pointerAccess)
        {
            StructTypeSymbol? structure = pointerAccess ? type switch
            {
                PointerTypeSymbol { ElementType: StructTypeSymbol pointer } => pointer,
                OwnershipTypeSymbol { ElementType: StructTypeSymbol owned } => owned,
                _ => null,
            } : type as StructTypeSymbol;
            if (structure is not null)
                MarkType(structure, TypeReachabilityReason.VirtualDispatch);
        }

        void Inspect(BoundFunction function)
        {
            MarkType(function.Symbol.ReturnType);
            foreach (ParameterSymbol parameter in function.Symbol.Parameters)
                MarkType(parameter.Type, TypeFacts.GetCompleteDestructor(parameter.Type) is null
                    ? TypeReachabilityReason.Reference
                    : TypeReachabilityReason.Destruct);
            XelibBodyCodec.Collect(function.Body, type => MarkType(type), MarkSymbol, MarkOperation);
        }
        void MarkSourceTypes(NamespaceSymbol @namespace)
        {
            foreach (StructTypeSymbol type in @namespace.Structs.Where(IsSymbolDefinedHere)) MarkType(type);
            foreach (InterfaceTypeSymbol type in @namespace.Interfaces.Where(IsSymbolDefinedHere)) MarkType(type);
            foreach (NamespaceSymbol child in @namespace.Namespaces) MarkSourceTypes(child);
        }
        void SelectSourceVirtualLayoutImplementations(NamespaceSymbol @namespace)
        {
            foreach (StructTypeSymbol type in @namespace.Structs.Where(type =>
                         IsSymbolDefinedHere(type) && type.IsConcreteType && type.HasVirtualDispatch))
                foreach (FunctionSymbol function in type.VirtualMethods)
                    Select(function);
            foreach (NamespaceSymbol child in @namespace.Namespaces)
                SelectSourceVirtualLayoutImplementations(child);
        }
        MarkSourceTypes(SemanticModel.GlobalNamespace);
        // Codegen emits every closed source vtable, including sealed/internal
        // layouts that are not downstream ABI surfaces. Their imported XELIB
        // targets still need bodies, but retain ordinary internal linkage.
        SelectSourceVirtualLayoutImplementations(SemanticModel.GlobalNamespace);
        foreach (BoundFunction function in source) Inspect(function);
        // A public source type can carry an inherited slot whose implementation
        // was statically consumed from XELIB. Keep that implementation in this
        // native artifact even when no source call site reaches it directly.
        foreach (FunctionSymbol root in GetImplementationNativeAbiRoots().OfType<FunctionSymbol>())
            Select(root);
        // Native exports are ABI roots even when no Xenon call site references them.
        foreach (FunctionSymbol export in available.Keys.Where(function => function.IsExport)) Select(export);
        while (pending.TryDequeue(out FunctionSymbol? symbol)) Inspect(available[symbol]);

        ImmutableArray<BoundFunction> functions = source.Concat(selected.Select(symbol => available[symbol])
                .OrderBy(function => function.Symbol.QualifiedName, StringComparer.Ordinal))
            .DistinctBy(function => function.Symbol, ReferenceEqualityComparer.Instance)
            .ToImmutableArray();
        return new NativeReachability(functions, selected, reachableTypes, reachableVirtualDispatchTypes,
            reachableInterfaceDispatchTypes, reachableStaticFields);
    }

    private sealed record NativeReachability(
        ImmutableArray<BoundFunction> Functions,
        HashSet<FunctionSymbol> FunctionSymbols,
        HashSet<DeclaredTypeSymbol> Types,
        HashSet<StructTypeSymbol> VirtualDispatchTypes,
        HashSet<StructTypeSymbol> InterfaceDispatchTypes,
        HashSet<FieldSymbol> StaticFields);

    [Flags]
    private enum TypeReachabilityReason : byte
    {
        None = 0,
        Reference = 1 << 0,
        Construct = 1 << 1,
        Destruct = 1 << 2,
        VirtualDispatch = 1 << 3,
        InterfaceDispatch = 1 << 4,
    }

    public LibraryCompilationReference? GetOwningLibrary(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        Symbol root = symbol;
        while (root.ContainingSymbol is Symbol containing) root = containing;
        return References.OfType<LibraryCompilationReference>()
            .FirstOrDefault(reference => ReferenceEquals(root, reference.GlobalNamespace));
    }

    private Compilation Derive(ImmutableArray<SyntaxTree> syntaxTrees, CompilationOptions options,
        ImmutableArray<CompilationReference> references, ITargetTypeLayout? targetLayout,
        CancellationToken cancellationToken) => new(syntaxTrees, options, references, targetLayout, cancellationToken);

    private int IndexOfTree(SyntaxTree tree)
    {
        for (int index = 0; index < SyntaxTrees.Length; index++)
            if (ReferenceEquals(SyntaxTrees[index], tree)) return index;
        return -1;
    }

    private static void ValidateTrees(ImmutableArray<SyntaxTree> syntaxTrees)
    {
        if (syntaxTrees.Any(tree => tree is null))
            throw new ArgumentException("Syntax tree collections cannot contain null values.", nameof(syntaxTrees));
        var identities = new HashSet<SourceFileId>();
        foreach (SyntaxTree tree in syntaxTrees)
            if (!identities.Add(tree.SourceFileId))
                throw new ArgumentException($"Duplicate source identity '{tree.SourceFileId}'.", nameof(syntaxTrees));
    }

    private static void ValidateReferences(ImmutableArray<CompilationReference> references)
    {
        if (references.Any(reference => reference is null))
            throw new ArgumentException("Reference collections cannot contain null values.", nameof(references));
        if (references.Distinct().Count() != references.Length)
            throw new ArgumentException("Duplicate compilation references are not allowed.", nameof(references));
    }
}
