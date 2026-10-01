using System.Collections.Immutable;
using System.Numerics;
using System.Text;
using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;
using Xenon.Compiler;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.CodeGen.LLVM;

public sealed record LlvmNativeExport(string Name, bool IsData = false);

/// <summary>
/// Generates one LLVM module. Instances are single-use; create a new generator for every operation.
/// </summary>
public sealed partial class LlvmIrGenerator
{
    private static readonly object OptimizedContextLock = new();
    private int _invocationStarted;
    private readonly Dictionary<FunctionSymbol, LlvmFunction> _functions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, LlvmFunction> _nativeFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<FunctionSymbol, LlvmCAbiFunction> _cAbiFunctions = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionSymbol, LlvmFunction> _functionValueAdapters = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructTypeSymbol, LLVMTypeRef> _structTypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StorageTypeSymbol, LLVMTypeRef> _storageTypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<AtomicTypeSymbol, LLVMTypeRef> _atomicTypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<InterfaceTypeSymbol, LLVMTypeRef> _interfaceTypes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<InterfaceTypeSymbol, LLVMValueRef> _interfaceKeys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, LLVMValueRef> _exceptionTypeKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<FieldSymbol, LLVMValueRef> _staticFields = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<FieldSymbol> _referencedStaticFields = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Symbol> _implementationAbiRoots = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<FunctionSymbol> _wholeProgramFunctions = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DeclaredTypeSymbol> _wholeProgramTypes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<FieldSymbol> _wholeProgramStaticFields = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FieldSymbol, LlvmFunction> _threadLocalEnsures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructTypeSymbol, LlvmVTable> _virtualTables = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructTypeSymbol, LlvmVTable> _interfaceMaps = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionSymbol, LlvmFunction> _closureEnvironmentDestructors = new(ReferenceEqualityComparer.Instance);
    private IReadOnlyDictionary<FunctionSymbol, LlvmFunctionEffects> _functionEffects =
        new Dictionary<FunctionSymbol, LlvmFunctionEffects>(ReferenceEqualityComparer.Instance);
    private LLVMContextRef _context;
    private LLVMModuleRef _module;
    private NativeTargetMachine? _targetMachine;
    private LlvmCAbi? _cAbi;
    private LlvmMemoryRuntime? _memoryRuntime;
    private LlvmExceptionRuntime? _exceptionRuntime;
    private bool _exceptionsEnabled;
    private Compilation _compilation = null!;
    private LLVMTypeRef _interfaceMapEntryType;
    private LLVMTypeRef _threadLocalCleanupNodeType;
    private LLVMValueRef _windowsThreadLocalCleanupIndex;
    private LLVMValueRef _windowsThreadLocalCleanupCallback;
    private LLVMValueRef _unixThreadLocalCleanupKey;
    private LLVMValueRef _unixThreadLocalCleanupCallback;
    private IReadOnlyDictionary<NamespaceSymbol, LlvmNativeReference> _nativeReferences = null!;
    private string _moduleIdentity = null!;

    public string Generate(
        Compilation compilation,
        string moduleName = "xenon",
        LlvmCodeGenerationOptions? codeGenerationOptions = null) =>
        GenerateModule(
            compilation,
            moduleName,
            targetMachine: null,
            codeGenerationOptions,
            module => module.PrintToString());

    public string GenerateForTarget(
        Compilation compilation,
        LlvmTargetOptions targetOptions,
        string moduleName = "xenon",
        LlvmCodeGenerationOptions? codeGenerationOptions = null)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(targetOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        BeginInvocation();
        compilation = SelectConditionalTarget(compilation, targetOptions.Triple);
        ThrowIfCompilationHasErrors(compilation);

        using NativeTargetMachine targetMachine = NativeTargetMachine.Create(targetOptions);
        return GenerateModuleCore(
            compilation,
            moduleName,
            targetMachine,
            codeGenerationOptions,
            module => module.PrintToString());
    }

    /// <summary>Performs target-specific semantic validation and returns source-located diagnostics.</summary>
    public static Compilation BindForTarget(Compilation compilation, LlvmTargetOptions targetOptions)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(targetOptions);
        compilation = SelectConditionalTarget(compilation, targetOptions.Triple);
        if (compilation.HasErrors) return compilation;
        using NativeTargetMachine target = NativeTargetMachine.Create(targetOptions);
        return BindForTarget(compilation, target);
    }

    /// <summary>Native implementation ABI required by source project references.</summary>
    public static ImmutableArray<LlvmNativeExport> GetProjectNativeExports(
        Compilation compilation,
        string abiIdentity)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(abiIdentity);
        var exports = ImmutableArray.CreateBuilder<LlvmNativeExport>();
        var implementationAbiRoots = new HashSet<Symbol>(
            compilation.GetImplementationNativeAbiRoots(), ReferenceEqualityComparer.Instance);
        foreach (FunctionSymbol function in compilation.GetStaticImplementationSymbols())
        {
            bool sourceExport = compilation.IsSymbolDefinedHere(function) &&
                (IsExternallyVisible(function) || function.IsExport ||
                 implementationAbiRoots.Contains(function) ||
                 function.FunctionKind is FunctionKind.InstanceInitializer or FunctionKind.DestructorGlue);
            bool xelibExport = compilation.GetOwningLibrary(function) is not null && function.IsExport;
            bool implementationExport = implementationAbiRoots.Contains(function) &&
                compilation.IsSymbolDefinedInCurrentArtifact(function);
            if (sourceExport || xelibExport || implementationExport)
                exports.Add(new LlvmNativeExport(GetFunctionNativeName(
                    function, GetArtifactAbiIdentity(compilation, function, abiIdentity))));
        }
        // Abstract virtual declarations have no executable body, but externally
        // derived vtables can still reference their unreachable ABI stubs.
        foreach (FunctionSymbol function in implementationAbiRoots.OfType<FunctionSymbol>().Where(function =>
                     function.IsAbstract && !function.IsGenericDefinition && function.VTableSlot is not null &&
                     compilation.IsSymbolDefinedInCurrentArtifact(function)))
            exports.Add(new LlvmNativeExport(GetFunctionNativeName(
                function, GetArtifactAbiIdentity(compilation, function, abiIdentity))));
        void AddTypeExports(NamespaceSymbol @namespace)
        {
            foreach (DeclaredTypeSymbol type in StaticFieldOwners(@namespace)
                .Where(compilation.IsSymbolDefinedHere))
            {
                foreach (FieldSymbol field in GetStaticFields(type)
                    .Where(field => IsExternallyVisible(field) || implementationAbiRoots.Contains(field)))
                {
                    exports.Add(new LlvmNativeExport(MangleManagedName(
                        abiIdentity, "static_field", GetStaticFieldSourceName(field)), IsData: true));
                    if (RequiresThreadLocalEnsure(field))
                        exports.Add(new LlvmNativeExport(MangleManagedName(
                            abiIdentity, "threadlocal_ensure", GetStaticFieldSourceName(field))));
                }
                if (type is not StructTypeSymbol structure) continue;
                if (structure.HasVirtualDispatch)
                    exports.Add(new LlvmNativeExport(MangleManagedName(
                        abiIdentity, "vtable", GetVirtualTableSourceName(structure)), IsData: true));
                if (structure.ImplementedInterfaces.Any())
                {
                    foreach (InterfaceTypeSymbol @interface in structure.ImplementedInterfaces)
                        exports.Add(new LlvmNativeExport(MangleManagedName(
                            abiIdentity, "interface_table", GetInterfaceTableSourceName(structure, @interface)), IsData: true));
                    exports.Add(new LlvmNativeExport(MangleManagedName(
                        abiIdentity, "interface_map", GetInterfaceMapSourceName(structure)), IsData: true));
                }
            }
            foreach (NamespaceSymbol child in @namespace.Namespaces) AddTypeExports(child);
        }
        AddTypeExports(compilation.SemanticModel.GlobalNamespace);
        return exports.Distinct().OrderBy(item => item.Name, StringComparer.Ordinal).ToImmutableArray();
    }

    private static string GetArtifactAbiIdentity(
        Compilation compilation,
        FunctionSymbol function,
        string sourceAbiIdentity)
    {
        if (compilation.IsSymbolDefinedHere(function)) return sourceAbiIdentity;
        if (compilation.GetOwningLibrary(function) is { } library)
            return $"xelib:{library.LibraryIdentity.ContentIdentity}";
        throw new LlvmCodeGenerationException(
            $"Missing native ABI metadata for implementation root '{function.QualifiedName}'.");
    }

    /// <summary>Whether this compilation emits pthread TLS cleanup calls on Unix targets.</summary>
    public static bool RequiresNativeThreadingRuntime(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        bool Visit(NamespaceSymbol @namespace) =>
            StaticFieldOwners(@namespace)
                .Where(compilation.IsSymbolDefinedInCurrentArtifact)
                .Where(compilation.IsImportedSymbolNativeReachable)
                .SelectMany(type => GetStaticFields(type).Where(compilation.IsImportedSymbolNativeReachable))
                .Any(field => field.IsThreadLocal &&
                    TypeFacts.GetCompleteDestructor(field.Type) is not null) ||
            @namespace.Namespaces.Any(Visit);
        return Visit(compilation.SemanticModel.GlobalNamespace) ||
            compilation.References.Any(reference => Visit(reference.GlobalNamespace));
    }

    public static bool RequiresNativeExceptionRuntime(Compilation compilation)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        foreach (MirFunction function in compilation.GetMirFunctions(lowered: false))
            if (function.HasExceptionRegions || function.Blocks.Any(block => block.Terminator is MirThrow)) return true;
        return false;
    }

    private static Compilation SelectConditionalTarget(Compilation compilation, string triple) =>
        compilation.WithOptions(compilation.Options with
        { ConditionalCompilation = compilation.Options.ConditionalOptions.WithTarget(triple) });

    private static Compilation BindForTarget(Compilation compilation, NativeTargetMachine target)
    {
        LlvmTypeLayout layout = LlvmTypeLayout.Create(target);
        return SelectConditionalTarget(compilation, target.Triple).WithTargetLayout(layout);
    }

    internal TResult GenerateModule<TResult>(
        Compilation compilation,
        string moduleName,
        NativeTargetMachine? targetMachine,
        LlvmCodeGenerationOptions? codeGenerationOptions,
        Func<LLVMModuleRef, TResult> resultFactory)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(resultFactory);
        BeginInvocation();
        if (targetMachine is not null) compilation = SelectConditionalTarget(compilation, targetMachine.Triple);
        ThrowIfCompilationHasErrors(compilation);

        return GenerateModuleCore(
            compilation,
            moduleName,
            targetMachine,
            codeGenerationOptions,
            resultFactory);
    }

    private TResult GenerateModuleCore<TResult>(
        Compilation compilation,
        string moduleName,
        NativeTargetMachine? targetMachine,
        LlvmCodeGenerationOptions? codeGenerationOptions,
        Func<LLVMModuleRef, TResult> resultFactory)
    {
        bool useGlobalContext = targetMachine is { OptimizationLevel: > 0 };
        if (useGlobalContext && !Monitor.IsEntered(OptimizedContextLock))
        {
            lock (OptimizedContextLock)
                return GenerateModuleCore(compilation, moduleName, targetMachine, codeGenerationOptions, resultFactory);
        }

        if (compilation.HasErrors)
        {
            throw new LlvmCodeGenerationException("LLVM IR cannot be generated while the compilation contains errors.");
        }

        if (targetMachine is not null)
        {
            compilation = BindForTarget(compilation, targetMachine);
            if (compilation.HasErrors)
                throw new LlvmCodeGenerationException("Target-specific semantic validation failed:" + Environment.NewLine +
                    string.Join(Environment.NewLine, compilation.Diagnostics.Select(diagnostic =>
                        $"{diagnostic.Location.Source.Path}({diagnostic.Location.Start.Line + 1},{diagnostic.Location.Start.Character + 1}): {diagnostic.Message}")));
        }
        if (compilation.RequiresTargetLayout)
            throw new LlvmCodeGenerationException("Constant evaluation requires a target layout; use GenerateForTarget or select a CLI target.");

        IReadOnlyDictionary<NamespaceSymbol, LlvmNativeReference> nativeReferences =
            ValidateNativeReferences(compilation, codeGenerationOptions);
        ImmutableArray<LlvmNativeReference> wholeProgramReferences = targetMachine is { OptimizationLevel: > 0 }
            ? GetWholeProgramStaticReferences(compilation, nativeReferences)
            : [];

        // LLVM 20.1.2's optimization passes create some loop metadata and
        // opaque pointer types in the global context. Optimized modules must
        // therefore share that context until the upstream package is updated.
        // The enclosing lock keeps LLVM's global context access serialized.
        _context = useGlobalContext ? LLVMContextRef.Global : LLVMContextRef.Create();
        _module = _context.CreateModuleWithName(moduleName);
        _targetMachine = targetMachine;
        _compilation = compilation;
        _moduleIdentity = codeGenerationOptions?.AbiIdentity ?? moduleName;
        _nativeReferences = nativeReferences;
        _exceptionsEnabled = RequiresNativeExceptionRuntime(compilation) ||
            nativeReferences.Values.Any(reference =>
                RequiresNativeExceptionRuntime(reference.Compilation));
        _implementationAbiRoots.UnionWith(compilation.GetImplementationNativeAbiRoots());

        try
        {
            if (targetMachine is not null)
            {
                _module.Target = targetMachine.Triple;
                _module.DataLayout = targetMachine.DataLayout;
            }

            ValidateEnumStorage(compilation.SemanticModel.GlobalNamespace);
            _interfaceMapEntryType = _context.CreateNamedStruct("__xenon.interface_map_entry");
            LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            _interfaceMapEntryType.StructSetBody([pointer, pointer], false);
            DeclareInterfaceTypes(compilation.SemanticModel.GlobalNamespace);
            foreach (CompilationReference reference in compilation.References)
                DeclareInterfaceTypes(reference.GlobalNamespace);
            foreach (LlvmNativeReference reference in wholeProgramReferences)
                DeclareInterfaceTypes(reference.Compilation.SemanticModel.GlobalNamespace);
            DeclareStructTypes(compilation.SemanticModel.GlobalNamespace);
            foreach (LlvmNativeReference reference in wholeProgramReferences)
                DeclareStructTypes(reference.Compilation.SemanticModel.GlobalNamespace);
            if (targetMachine is not null)
                _cAbi = new LlvmCAbi(
                    _context,
                    targetMachine,
                    LlvmTypeLayout.Create(targetMachine),
                    MapType);
            var implementationFunctionBuilder = ImmutableArray.CreateBuilder<MirFunction>();
            implementationFunctionBuilder.AddRange(compilation.GetMirFunctions());
            foreach (LlvmNativeReference reference in wholeProgramReferences)
            {
                MarkWholeProgramSymbols(reference.Compilation);
                foreach (MirFunction function in reference.Compilation.GetMirFunctions()
                             .Where(function => reference.Compilation.IsSymbolDefinedInCurrentArtifact(function.Symbol)))
                {
                    if (_wholeProgramFunctions.Add(function.Symbol))
                        implementationFunctionBuilder.Add(function);
                }
            }
            ImmutableArray<MirFunction> implementationFunctions = implementationFunctionBuilder
                .DistinctBy(function => function.Symbol, ReferenceEqualityComparer.Instance)
                .ToImmutableArray();
            _functionEffects = LlvmFunctionEffectAnalysis.Analyze(implementationFunctions);
            DeclareFunctions(compilation.SemanticModel.GlobalNamespace);
            foreach (MirFunction function in implementationFunctions)
                if (!_functions.ContainsKey(function.Symbol))
                    DeclareFunction(function.Symbol);
            foreach (FunctionSymbol function in _implementationAbiRoots.OfType<FunctionSymbol>().Where(function =>
                         function.IsAbstract && !function.IsGenericDefinition &&
                         compilation.GetOwningLibrary(function) is not null &&
                         compilation.IsSymbolDefinedInCurrentArtifact(function)))
                if (!_functions.ContainsKey(function))
                    DeclareFunction(function);
            foreach (MirFunction implementation in implementationFunctions)
                foreach (Symbol symbol in MirSymbols.Referenced(implementation)) DeclareReferencedSymbol(symbol);
            DeclareClosureEnvironmentDestructors(implementationFunctions);
            DeclareInterfaceTables(compilation.SemanticModel.GlobalNamespace);
            foreach (CompilationReference reference in compilation.References)
                DeclareInterfaceTables(reference.GlobalNamespace);
            foreach (LlvmNativeReference reference in wholeProgramReferences)
                DeclareInterfaceTables(reference.Compilation.SemanticModel.GlobalNamespace);
            DeclareVirtualTables(compilation.SemanticModel.GlobalNamespace);
            foreach (CompilationReference reference in compilation.References)
                DeclareVirtualTables(reference.GlobalNamespace);
            foreach (LlvmNativeReference reference in wholeProgramReferences)
                DeclareVirtualTables(reference.Compilation.SemanticModel.GlobalNamespace);
            DeclareStaticFields(compilation.SemanticModel.GlobalNamespace);
            foreach (CompilationReference reference in compilation.References)
                DeclareStaticFields(reference.GlobalNamespace);
            foreach (LlvmNativeReference reference in wholeProgramReferences)
                DeclareStaticFields(reference.Compilation.SemanticModel.GlobalNamespace);
            foreach (FieldSymbol field in _referencedStaticFields) DeclareStaticField(field);
            DeclareThreadLocalHelpers();

            EmitFunctionBodies(implementationFunctions);
            EmitCAbiThunks();
            if (compilation.Options.OutputKind == CompilationOutputKind.Executable)
            {
                EmitExecutableEntryPoint(compilation.GetDeclaredFunctionSymbols());
            }

            if (implementationFunctions.Any(function => function.Coroutine is not null))
                LlvmResumableRuntime.LinkAndLower(_module, GetIntegerBitWidth(BuiltinTypes.NUInt));
            _module.Verify(LLVMVerifierFailureAction.LLVMReturnStatusAction);
            if (targetMachine is not null)
            {
                LlvmOptimizationDiagnostics? diagnostics = codeGenerationOptions?.OptimizationDiagnostics;
                diagnostics?.PreOptimizationIr?.Invoke(_module.PrintToString());
                LlvmOptimizer.Run(_module, targetMachine, diagnostics?.OptimizationRemark);
                _module.Verify(LLVMVerifierFailureAction.LLVMReturnStatusAction);
                diagnostics?.PostOptimizationIr?.Invoke(_module.PrintToString());
                if (diagnostics?.Assembly is { } assemblySink)
                    assemblySink(EmitAssembly(_module, targetMachine));
            }
            return resultFactory(_module);
        }
        catch (LlvmCodeGenerationException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new LlvmCodeGenerationException("LLVM failed to generate or verify the module.", exception);
        }
        finally
        {
            _module.Dispose();
            if (!useGlobalContext)
                _context.Dispose();
            _functions.Clear();
            _nativeFunctions.Clear();
            _cAbiFunctions.Clear();
            _functionValueAdapters.Clear();
            _structTypes.Clear();
            _storageTypes.Clear();
            _interfaceTypes.Clear();
            _interfaceKeys.Clear();
            _exceptionTypeKeys.Clear();
            _staticFields.Clear();
            _referencedStaticFields.Clear();
            _implementationAbiRoots.Clear();
            _threadLocalEnsures.Clear();
            _virtualTables.Clear();
            _interfaceMaps.Clear();
            _targetMachine = null;
            _cAbi = null;
            _memoryRuntime = null;
            _exceptionRuntime = null;
            _nativeReferences = null!;
            _moduleIdentity = null!;
        }
    }

    private void DeclareReferencedSymbol(Symbol symbol)
    {
        switch (symbol)
        {
            case FunctionSymbol function when !_functions.ContainsKey(function):
                DeclareFunction(function);
                break;
            case FieldSymbol { IsStatic: true } field:
                _referencedStaticFields.Add(field);
                break;
            case PropertySymbol property:
                if (property.Getter is { } getter && !_functions.ContainsKey(getter)) DeclareFunction(getter);
                if (property.Setter is { } setter && !_functions.ContainsKey(setter)) DeclareFunction(setter);
                break;
            case IndexerSymbol indexer:
                if (indexer.Getter is { } indexerGetter && !_functions.ContainsKey(indexerGetter))
                    DeclareFunction(indexerGetter);
                if (indexer.Setter is { } indexerSetter && !_functions.ContainsKey(indexerSetter))
                    DeclareFunction(indexerSetter);
                break;
        }
    }

    private void BeginInvocation()
    {
        if (Interlocked.CompareExchange(ref _invocationStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException(
                "LlvmIrGenerator instances are single-use; create a new instance for each generation operation.");
        }
    }

    private static void ThrowIfCompilationHasErrors(Compilation compilation)
    {
        if (compilation.HasErrors)
        {
            throw new LlvmCodeGenerationException(
                "LLVM IR cannot be generated while the compilation contains errors.");
        }
    }

    private static ImmutableArray<LlvmNativeReference> GetWholeProgramStaticReferences(
        Compilation compilation,
        IReadOnlyDictionary<NamespaceSymbol, LlvmNativeReference> nativeReferences)
    {
        var result = ImmutableArray.CreateBuilder<LlvmNativeReference>();
        var visited = new HashSet<Compilation>(ReferenceEqualityComparer.Instance);
        void Visit(Compilation current)
        {
            foreach (SourceCompilationReference sourceReference in current.References
                         .OfType<SourceCompilationReference>())
            {
                Compilation dependency = sourceReference.Compilation;
                if (!nativeReferences.TryGetValue(dependency.SemanticModel.GlobalNamespace,
                        out LlvmNativeReference? nativeReference) ||
                    nativeReference.Kind != LlvmNativeReferenceKind.Static ||
                    !visited.Add(dependency))
                    continue;
                result.Add(nativeReference);
                Visit(dependency);
            }
        }
        Visit(compilation);
        return result.ToImmutable();
    }

    private void MarkWholeProgramSymbols(Compilation compilation)
    {
        void Visit(NamespaceSymbol @namespace)
        {
            foreach (DeclaredTypeSymbol type in @namespace.Structs.Cast<DeclaredTypeSymbol>()
                         .Concat(@namespace.Enums))
            {
                if (!compilation.IsSymbolDefinedInCurrentArtifact(type)) continue;
                _wholeProgramTypes.Add(type);
                foreach (FieldSymbol field in GetStaticFields(type))
                    if (compilation.IsSymbolDefinedInCurrentArtifact(field))
                        _wholeProgramStaticFields.Add(field);
            }
            foreach (NamespaceSymbol child in @namespace.Namespaces) Visit(child);
        }
        Visit(compilation.SemanticModel.GlobalNamespace);
    }

    private static IReadOnlyDictionary<NamespaceSymbol, LlvmNativeReference> ValidateNativeReferences(
        Compilation compilation,
        LlvmCodeGenerationOptions? options)
    {
        var required = new HashSet<Compilation>(ReferenceEqualityComparer.Instance);
        void Collect(Compilation current)
        {
            foreach (SourceCompilationReference reference in current.References.OfType<SourceCompilationReference>())
                if (required.Add(reference.Compilation)) Collect(reference.Compilation);
        }
        Collect(compilation);

        if (required.Count != 0 && options is null)
            throw new LlvmCodeGenerationException(
                "Missing native ABI metadata for referenced compilation snapshot(s).");

        LlvmNativeReference[] supplied = options?.NativeReferences.ToArray() ?? [];
        foreach (LlvmNativeReference reference in supplied)
            if (!required.Contains(reference.Compilation))
                throw new LlvmCodeGenerationException(
                    "Native reference metadata does not match a semantic compilation reference snapshot.");
        foreach (Compilation reference in required)
            if (!supplied.Any(item => ReferenceEquals(item.Compilation, reference)))
                throw new LlvmCodeGenerationException(
                    "Missing native ABI metadata for an exact referenced compilation snapshot.");

        var result = new Dictionary<NamespaceSymbol, LlvmNativeReference>(ReferenceEqualityComparer.Instance);
        foreach (LlvmNativeReference reference in supplied)
            result.Add(reference.Compilation.SemanticModel.GlobalNamespace, reference);
        return result;
    }

    private bool IsSharedReference(Symbol symbol)
    {
        Symbol root = symbol;
        while (root.ContainingSymbol is not null) root = root.ContainingSymbol;
        return root is NamespaceSymbol @namespace &&
            _nativeReferences.TryGetValue(@namespace, out LlvmNativeReference? reference) &&
            reference.Kind == LlvmNativeReferenceKind.Shared;
    }

    private static string GetInterfaceKeySourceName(InterfaceTypeSymbol type) =>
        $"{type.FullName}.__interface_key";

    private static string GetInterfaceTableSourceName(
        StructTypeSymbol type,
        InterfaceTypeSymbol @interface) =>
        $"{type.FullName}.{@interface.FullName}.__itable";

    private static string GetStaticFieldSourceName(FieldSymbol field) =>
        $"{field.ContainingType.FullName}.{field.Name}";

    private static bool RequiresThreadLocalEnsure(FieldSymbol field) =>
        field.IsThreadLocal &&
        (field.HasInitializer || TypeFacts.GetCompleteDestructor(field.Type) is not null);

    private static string GetVirtualTableSourceName(StructTypeSymbol type) =>
        $"{type.FullName}.__vtable";

    private static string GetInterfaceMapSourceName(StructTypeSymbol type) =>
        $"{type.FullName}.__imap";

    private static string MangleManagedName(
        string abiIdentity,
        string category,
        string sourceIdentity) =>
        $"{RuntimeAbiNames.Prefix}{category}_{Convert.ToHexString(Encoding.UTF8.GetBytes(abiIdentity))}_" +
        Convert.ToHexString(Encoding.UTF8.GetBytes(sourceIdentity));

    private string GetManagedName(Symbol owner, string category, string sourceIdentity) =>
        MangleManagedName(GetAbiIdentity(owner), category, sourceIdentity);

    private static string GetFunctionNativeName(FunctionSymbol function, string abiIdentity) =>
        function.IsExtern || function.IsExport
            ? NativeSymbolNames.Get(function)
            : MangleManagedName(abiIdentity, "function", NativeSymbolNames.Get(function));

    private string GetFunctionNativeName(FunctionSymbol function) =>
        GetFunctionNativeName(function, GetAbiIdentity(function));

    private string GetAbiIdentity(Symbol symbol)
    {
        Symbol root = symbol;
        while (root.ContainingSymbol is not null) root = root.ContainingSymbol;
        if (ReferenceEquals(root, _compilation.SemanticModel.GlobalNamespace)) return _moduleIdentity;
        if (_compilation.GetOwningLibrary(symbol) is { } library)
            return $"xelib:{library.LibraryIdentity.ContentIdentity}";
        if (root is NamespaceSymbol @namespace &&
            _nativeReferences.TryGetValue(@namespace, out LlvmNativeReference? reference))
            return reference.AbiIdentity;
        foreach (LlvmNativeReference transitiveReference in _nativeReferences.Values)
            if (transitiveReference.Compilation.GetOwningLibrary(symbol) is { } transitiveLibrary)
                return $"xelib:{transitiveLibrary.LibraryIdentity.ContentIdentity}";
        throw new LlvmCodeGenerationException(
            $"Missing native ABI metadata for symbol '{symbol.QualifiedName}'.");
    }

    private string GetInterfaceRuntimeIdentity(InterfaceTypeSymbol type)
    {
        string moduleIdentity = GetAbiIdentity(type);
        int byteLength = Encoding.UTF8.GetByteCount(moduleIdentity);
        return $"{byteLength}:{moduleIdentity}:{type.FullName}";
    }

    private void DeclareStructTypes(NamespaceSymbol globalNamespace)
    {
        var types = new HashSet<StructTypeSymbol>(ReferenceEqualityComparer.Instance);
        CollectStructTypes(globalNamespace, types);
        foreach (CompilationReference reference in _compilation.References)
            CollectStructTypes(reference.GlobalNamespace, types);
        StructTypeSymbol[] newTypes = types.Where(type => !_structTypes.ContainsKey(type)).ToArray();
        foreach (StructTypeSymbol type in newTypes)
        {
            string typeName = _compilation.IsSymbolDefinedInCurrentArtifact(type)
                ? type.FullName
                : GetManagedName(type, "ir_type", type.FullName);
            _structTypes.Add(type, _context.CreateNamedStruct(typeName));
        }

        foreach (StructTypeSymbol type in newTypes)
        {
            LLVMTypeRef[] fields = LlvmStructLayout.Elements(type, MapType, LLVMTypeRef.CreatePointer(_context.Int8Type, 0));
            _structTypes[type].StructSetBody(fields, false);
        }
    }

    private void DeclareInterfaceTypes(NamespaceSymbol @namespace)
    {
        foreach (InterfaceTypeSymbol type in @namespace.Interfaces
                     .Where(_compilation.IsImportedSymbolNativeReachable))
        {
            if (_interfaceTypes.ContainsKey(type)) continue;
            string typeName = _compilation.IsSymbolDefinedInCurrentArtifact(type)
                ? type.FullName
                : GetManagedName(type, "ir_type", type.FullName);
            LLVMTypeRef llvmType = _context.CreateNamedStruct(typeName);
            llvmType.StructSetBody([LLVMTypeRef.CreatePointer(_context.Int8Type, 0), LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false);
            _interfaceTypes.Add(type, llvmType);

            byte[] identity = Encoding.UTF8.GetBytes(GetInterfaceRuntimeIdentity(type) + '\0');
            LLVMTypeRef keyType = LLVMTypeRef.CreateArray(_context.Int8Type, (uint)identity.Length);
            LLVMValueRef key = _module.AddGlobal(keyType, GetManagedName(
                type, "interface_key", GetInterfaceKeySourceName(type)));
            key.Linkage = LLVMLinkage.LLVMInternalLinkage;
            key.IsGlobalConstant = true;
            key.Initializer = LLVMValueRef.CreateConstArray(
                _context.Int8Type,
                identity.Select(value => LLVMValueRef.CreateConstInt(_context.Int8Type, value, false)).ToArray());
            _interfaceKeys.Add(type, key);
        }
        foreach (NamespaceSymbol child in @namespace.Namespaces)
            DeclareInterfaceTypes(child);
    }

    private void DeclareVirtualTables(NamespaceSymbol @namespace)
    {
        foreach (StructTypeSymbol type in @namespace.Structs.Where(type => type.IsConcreteType && type.HasVirtualDispatch)
                     .Where(_compilation.IsImportedDispatchTypeNativeReachable))
        {
            if (_virtualTables.ContainsKey(type)) continue;
            LLVMTypeRef elementType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef tableType = LLVMTypeRef.CreateArray(elementType, (uint)type.VirtualMethods.Length + 1);
            LLVMValueRef table = _module.AddGlobal(tableType, GetManagedName(
                type, "vtable", GetVirtualTableSourceName(type)));
            bool owned = _compilation.IsSymbolDefinedInCurrentArtifact(type) ||
                _wholeProgramTypes.Contains(type);
            table.Linkage = LLVMLinkage.LLVMExternalLinkage;
            if (owned)
            {
                // The runtime interface map precedes the virtual method slots.
                // Object layout still needs only one dispatch pointer.
                LLVMValueRef[] entries = [
                    _interfaceMaps.TryGetValue(type, out LlvmVTable map) ? map.Value : LLVMValueRef.CreateConstPointerNull(elementType),
                    .. type.VirtualMethods.Select(method => GetVirtualTableFunction(type, method).Value)];
                table.Initializer = LLVMValueRef.CreateConstArray(elementType, entries);
            }
            else if (IsWindowsTarget() && IsSharedReference(type))
                table.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
            _virtualTables.Add(type, new LlvmVTable(table, tableType));
        }
        foreach (NamespaceSymbol child in @namespace.Namespaces)
            DeclareVirtualTables(child);
    }

    private LlvmFunction GetVirtualTableFunction(StructTypeSymbol type, FunctionSymbol slot)
    {
        FunctionSymbol target = slot.FunctionKind == FunctionKind.Destructor
            ? type.CompleteDestructor ?? slot
            : slot;
        if (!_functions.TryGetValue(target, out LlvmFunction function))
        {
            DeclareFunction(target);
            function = _functions[target];
        }
        return function;
    }

    private void DeclareInterfaceTables(NamespaceSymbol @namespace)
    {
        foreach (StructTypeSymbol type in @namespace.Structs.Where(type => type.IsConcreteType)
                     .Where(_compilation.IsImportedInterfaceDispatchTypeNativeReachable))
        {
            if (_interfaceMaps.ContainsKey(type)) continue;
            var tables = new Dictionary<InterfaceTypeSymbol, (LlvmVTable Table, FunctionSymbol[] Implementations)>();
            bool owned = _compilation.IsSymbolDefinedInCurrentArtifact(type) ||
                _wholeProgramTypes.Contains(type);
            foreach (InterfaceTypeSymbol @interface in type.ImplementedInterfaces
                .OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                LLVMTypeRef elementType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                FunctionSymbol[] implementations = @interface.AllMethods
                    .Select(required => type.FindInterfaceImplementation(required)!).ToArray();
                // Slot zero retains the runtime interface map for interface upcasts.
                // Method slots start at one, allowing ordinary calls to dispatch directly
                // through the table stored in the two-word interface value.
                LLVMTypeRef tableType = LLVMTypeRef.CreateArray(elementType, (uint)implementations.Length + 1);
                LLVMValueRef table = _module.AddGlobal(
                    tableType, GetManagedName(
                        type, "interface_table", GetInterfaceTableSourceName(type, @interface)));
                table.Linkage = LLVMLinkage.LLVMExternalLinkage;
                if (!owned && IsWindowsTarget() && IsSharedReference(type))
                    table.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
                tables.Add(@interface, (new LlvmVTable(table, tableType), implementations));
            }

            if (tables.Count > 0)
            {
                LLVMTypeRef pointerType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                LLVMTypeRef mapType = LLVMTypeRef.CreateArray(_interfaceMapEntryType, (uint)tables.Count + 1);
                LLVMValueRef[] entries = tables
                    .OrderBy(pair => pair.Key.FullName, StringComparer.Ordinal)
                    .Select(pair => LLVMValueRef.CreateConstNamedStruct(
                        _interfaceMapEntryType, [_interfaceKeys[pair.Key], pair.Value.Table.Value]))
                    .Append(LLVMValueRef.CreateConstNamedStruct(_interfaceMapEntryType,
                        [LLVMValueRef.CreateConstPointerNull(pointerType), LLVMValueRef.CreateConstPointerNull(pointerType)]))
                    .ToArray();

                LLVMValueRef map = _module.AddGlobal(mapType, GetManagedName(
                    type, "interface_map", GetInterfaceMapSourceName(type)));
                map.Linkage = LLVMLinkage.LLVMExternalLinkage;
                if (owned) map.Initializer = LLVMValueRef.CreateConstArray(_interfaceMapEntryType, entries);
                else if (IsWindowsTarget() && IsSharedReference(type))
                    map.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
                _interfaceMaps.Add(type, new LlvmVTable(map, mapType));

                if (owned)
                {
                    foreach ((LlvmVTable table, FunctionSymbol[] implementations) in tables.Values)
                    {
                        LLVMValueRef[] tableEntries =
                        [
                            map,
                            .. implementations.Select(method => _functions[method].Value),
                        ];
                        LLVMValueRef tableValue = table.Value;
                        tableValue.Initializer = LLVMValueRef.CreateConstArray(pointerType, tableEntries);
                    }
                }
            }
        }
        foreach (NamespaceSymbol child in @namespace.Namespaces)
            DeclareInterfaceTables(child);
    }

    private void DeclareStaticFields(NamespaceSymbol @namespace)
    {
        foreach (DeclaredTypeSymbol type in StaticFieldOwners(@namespace)
                     .Where(_compilation.IsImportedSymbolNativeReachable))
        {
            foreach (FieldSymbol field in GetStaticFields(type).Where(_compilation.IsImportedSymbolNativeReachable))
                DeclareStaticField(field);
        }
        foreach (NamespaceSymbol child in @namespace.Namespaces)
            DeclareStaticFields(child);
    }

    private void DeclareStaticField(FieldSymbol field)
    {
        if (_staticFields.ContainsKey(field)) return;
        LLVMTypeRef fieldType = MapType(field.Type);
        LLVMValueRef global = _module.AddGlobal(fieldType, GetManagedName(
            field, "static_field", GetStaticFieldSourceName(field)));
        bool owned = _compilation.IsSymbolDefinedInCurrentArtifact(field) ||
            _wholeProgramStaticFields.Contains(field);
        global.Linkage = !owned || IsExternallyVisible(field)
            || _implementationAbiRoots.Contains(field)
            ? LLVMLinkage.LLVMExternalLinkage : LLVMLinkage.LLVMInternalLinkage;
        if (owned) global.Initializer = CreateStaticInitializer(field.Type, field.ConstantValue);
        else if (IsWindowsTarget() && IsSharedReference(field))
            global.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
        if (field.IsThreadLocal)
        {
            global.IsThreadLocal = true;
            global.ThreadLocalMode = LLVMThreadLocalMode.LLVMGeneralDynamicTLSModel;
        }
        _staticFields.Add(field, global);
    }

    private void DeclareThreadLocalHelpers()
    {
        LLVMTypeRef helperType = LLVMTypeRef.CreateFunction(_context.VoidType, [], false);
        foreach (FieldSymbol field in _staticFields.Keys.Where(RequiresThreadLocalEnsure).ToArray())
        {
            string helperName = GetManagedName(field, "threadlocal_ensure", GetStaticFieldSourceName(field));
            LLVMValueRef helper = _module.AddFunction(helperName, helperType);
            bool owned = _compilation.IsSymbolDefinedInCurrentArtifact(field) ||
                _wholeProgramStaticFields.Contains(field);
            helper.Linkage = !owned || IsExternallyVisible(field)
                || _implementationAbiRoots.Contains(field)
                ? LLVMLinkage.LLVMExternalLinkage
                : LLVMLinkage.LLVMInternalLinkage;
            if (!owned && IsWindowsTarget() && IsSharedReference(field))
                helper.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLImportStorageClass;
            var declaration = new LlvmFunction(helper, helperType);
            _threadLocalEnsures.Add(field, declaration);
            if (owned) EmitThreadLocalEnsure(field, declaration);
        }
    }

    private void EmitThreadLocalEnsure(FieldSymbol field, LlvmFunction ensure)
    {
        LLVMValueRef guard = _module.AddGlobal(_context.Int8Type,
            GetManagedName(field, "threadlocal_guard", GetStaticFieldSourceName(field)));
        guard.Linkage = LLVMLinkage.LLVMInternalLinkage;
        guard.Initializer = LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false);
        guard.IsThreadLocal = true;
        guard.ThreadLocalMode = LLVMThreadLocalMode.LLVMGeneralDynamicTLSModel;

        using LLVMBuilderRef builder = _context.CreateBuilder();
        LLVMBasicBlockRef entry = ensure.Value.AppendBasicBlock("entry");
        LLVMBasicBlockRef check = ensure.Value.AppendBasicBlock("threadlocal.check");
        LLVMBasicBlockRef initialize = ensure.Value.AppendBasicBlock("threadlocal.initialize");
        LLVMBasicBlockRef recursive = ensure.Value.AppendBasicBlock("threadlocal.recursive");
        LLVMBasicBlockRef ready = ensure.Value.AppendBasicBlock("threadlocal.ready");
        builder.PositionAtEnd(entry);
        LLVMValueRef state = builder.BuildLoad2(_context.Int8Type, guard, "threadlocal.state");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, state,
            LLVMValueRef.CreateConstInt(_context.Int8Type, 2, false), "threadlocal.initialized"), ready, check);

        builder.PositionAtEnd(check);
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, state,
            LLVMValueRef.CreateConstInt(_context.Int8Type, 1, false), "threadlocal.initializing"), recursive, initialize);

        builder.PositionAtEnd(recursive);
        LLVMValueRef trap = GetOrDeclareTrap();
        builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [], false), trap,
            Array.Empty<LLVMValueRef>(), string.Empty);
        builder.BuildUnreachable();

        builder.PositionAtEnd(initialize);
        builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int8Type, 1, false), guard);
        FunctionSymbol? initializer = _functions.Keys.FirstOrDefault(function =>
            function.FunctionKind == FunctionKind.ThreadLocalInitializer &&
            ReferenceEquals(function.ThreadLocalField, field));
        if (initializer is not null)
        {
            LlvmFunction initializerFunction = _functions[initializer];
            if (_exceptionsEnabled)
            {
                LlvmFunction guardedInitialize = GetOrDeclareExceptionRuntime().Initialize;
                builder.BuildCall2(guardedInitialize.Type, guardedInitialize.Value,
                    new[] { initializerFunction.Value, guard }, string.Empty);
            }
            else
            {
                builder.BuildCall2(initializerFunction.Type, initializerFunction.Value,
                    Array.Empty<LLVMValueRef>(), string.Empty);
            }
        }
        builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int8Type, 2, false), guard);
        if (TypeFacts.GetCompleteDestructor(field.Type) is { } destructor)
            EmitThreadLocalDestructorRegistration(builder, field, destructor);
        builder.BuildBr(ready);

        builder.PositionAtEnd(ready);
        builder.BuildRetVoid();
    }

    private void EmitThreadLocalDestructorRegistration(
        LLVMBuilderRef builder,
        FieldSymbol field,
        FunctionSymbol destructor)
    {
        // Thread-exit callbacks execute code emitted into this Xenon module. The native host must
        // keep the module loaded until every thread that initialized its non-trivial TLS has exited.
        if (IsWindowsTarget())
        {
            EmitWindowsThreadLocalDestructorRegistration(builder, field, destructor);
            return;
        }

        if (IsMacOsTarget())
        {
            EmitMacOsThreadLocalDestructorRegistration(builder, field, destructor);
            return;
        }

        EmitUnixThreadLocalDestructorRegistration(builder, field, destructor);
    }

    private void EmitMacOsThreadLocalDestructorRegistration(
        LLVMBuilderRef builder,
        FieldSymbol field,
        FunctionSymbol destructor)
    {
        // Darwin tears down Mach-O TLV storage before running pthread TSD destructors. Registering
        // the address of a native TLS slot with pthread_setspecific therefore leaves the callback
        // looking at cleared storage. _tlv_atexit is libSystem's public TLV terminator API and runs
        // callbacks while the current thread's TLV storage is still alive.
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        LLVMTypeRef registerType = LLVMTypeRef.CreateFunction(
            _context.VoidType, [pointer, pointer], false);
        LLVMValueRef register = GetOrAddNativeFunction("_tlv_atexit", registerType);
        LLVMTypeRef callbackType = LLVMTypeRef.CreateFunction(_context.VoidType, [pointer], false);
        LLVMValueRef callback = _module.AddFunction(
            GetManagedName(field, "threadlocal_tlv_cleanup", GetStaticFieldSourceName(field)),
            callbackType);
        callback.Linkage = LLVMLinkage.LLVMInternalLinkage;
        using (LLVMBuilderRef callbackBuilder = _context.CreateBuilder())
        {
            callbackBuilder.PositionAtEnd(callback.AppendBasicBlock("entry"));
            LLVMValueRef addressParameter = callback.GetParam(0);
            if (_exceptionsEnabled)
            {
                LlvmFunction cleanup = GetOrDeclareExceptionRuntime().Cleanup;
                callbackBuilder.BuildCall2(cleanup.Type, cleanup.Value,
                    new[] { _functions[destructor].Value, addressParameter }, string.Empty);
            }
            else
            {
                callbackBuilder.BuildCall2(callbackType, _functions[destructor].Value,
                    new[] { addressParameter }, string.Empty);
            }
            callbackBuilder.BuildRetVoid();
        }
        LLVMValueRef address = _staticFields[field];
        if (field.Type is AtomicTypeSymbol atomic)
            address = LlvmAtomicStorage.GetValueAddress(
                builder, MapType(atomic), address, atomic, "threadlocal.atomic.value");
        builder.BuildCall2(registerType, register,
            new LLVMValueRef[] { callback, address }, string.Empty);
    }

    private void EmitUnixThreadLocalDestructorRegistration(
        LLVMBuilderRef builder,
        FieldSymbol field,
        FunctionSymbol destructor)
    {
        EnsureUnixThreadLocalCleanupRuntime();
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        LLVMTypeRef keyType = GetPthreadKeyType();
        LLVMTypeRef keyCreateType = LLVMTypeRef.CreateFunction(
            _context.Int32Type, [LLVMTypeRef.CreatePointer(keyType, 0), pointer], false);
        LLVMTypeRef keyDeleteType = LLVMTypeRef.CreateFunction(_context.Int32Type, [keyType], false);
        LLVMTypeRef getSpecificType = LLVMTypeRef.CreateFunction(pointer, [keyType], false);
        LLVMTypeRef setSpecificType = LLVMTypeRef.CreateFunction(
            _context.Int32Type, [keyType, pointer], false);
        LLVMValueRef keyCreate = GetOrAddNativeFunction("pthread_key_create", keyCreateType);
        LLVMValueRef keyDelete = GetOrAddNativeFunction("pthread_key_delete", keyDeleteType);
        LLVMValueRef getSpecific = GetOrAddNativeFunction("pthread_getspecific", getSpecificType);
        LLVMValueRef setSpecific = GetOrAddNativeFunction("pthread_setspecific", setSpecificType);
        LLVMValueRef sentinel = LLVMValueRef.CreateConstInt(keyType, ulong.MaxValue, false);
        var atomics = new LlvmAtomicOperations(builder);
        LLVMBasicBlockRef current = builder.InsertBlock;
        LLVMValueRef loaded = atomics.Load(keyType, _unixThreadLocalCleanupKey,
            LLVMAtomicOrdering.LLVMAtomicOrderingAcquire, "threadlocal.pthread.key");
        LLVMValueRef owner = current.Parent;
        LLVMBasicBlockRef allocate = owner.AppendBasicBlock("threadlocal.pthread.allocate");
        LLVMBasicBlockRef allocationFailed = owner.AppendBasicBlock("threadlocal.pthread.allocation.failed");
        LLVMBasicBlockRef install = owner.AppendBasicBlock("threadlocal.pthread.install");
        LLVMBasicBlockRef won = owner.AppendBasicBlock("threadlocal.pthread.won");
        LLVMBasicBlockRef lost = owner.AppendBasicBlock("threadlocal.pthread.lost");
        LLVMBasicBlockRef use = owner.AppendBasicBlock("threadlocal.pthread.use");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, loaded, sentinel,
            "threadlocal.pthread.unallocated"), allocate, use);

        builder.PositionAtEnd(allocate);
        LLVMValueRef candidateAddress = builder.BuildAlloca(keyType, "threadlocal.pthread.candidate.address");
        LLVMValueRef created = builder.BuildCall2(keyCreateType, keyCreate,
            new LLVMValueRef[] { candidateAddress, _unixThreadLocalCleanupCallback },
            "threadlocal.pthread.created");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, created,
            LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
            "threadlocal.pthread.create.failed"), allocationFailed, install);

        LLVMValueRef trap = GetOrDeclareTrap();
        builder.PositionAtEnd(allocationFailed);
        builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [], false), trap,
            Array.Empty<LLVMValueRef>(), string.Empty);
        builder.BuildUnreachable();

        builder.PositionAtEnd(install);
        LLVMValueRef candidate = builder.BuildLoad2(keyType, candidateAddress,
            "threadlocal.pthread.candidate");
        LlvmCompareExchangeResult installed = atomics.CompareExchange(
            _unixThreadLocalCleanupKey, sentinel, candidate,
            LLVMAtomicOrdering.LLVMAtomicOrderingAcquireRelease,
            LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
            "threadlocal.pthread.install");
        builder.BuildCondBr(installed.Succeeded, won, lost);

        builder.PositionAtEnd(won);
        builder.BuildBr(use);
        builder.PositionAtEnd(lost);
        builder.BuildCall2(keyDeleteType, keyDelete, new LLVMValueRef[] { candidate }, string.Empty);
        builder.BuildBr(use);

        builder.PositionAtEnd(use);
        LLVMValueRef key = builder.BuildPhi(keyType, "threadlocal.pthread.selected");
        key.AddIncoming([loaded, candidate, installed.Observed], [current, won, lost], 3);
        LLVMValueRef head = builder.BuildCall2(getSpecificType, getSpecific,
            new LLVMValueRef[] { key }, "threadlocal.cleanup.head");
        LlvmMemoryRuntime memory = GetOrDeclareMemoryRuntime();
        ulong pointerBytes = (ulong)(_targetMachine?.PointerBitWidth ?? IntPtr.Size * 8) / 8;
        LLVMValueRef node = builder.BuildCall2(memory.MallocType, memory.Malloc,
            new LLVMValueRef[] { LLVMValueRef.CreateConstInt(memory.SizeType, pointerBytes * 3, false) },
            "threadlocal.cleanup.node");
        builder.BuildStore(head, builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 0));
        LLVMValueRef address = _staticFields[field];
        if (field.Type is AtomicTypeSymbol atomic)
            address = LlvmAtomicStorage.GetValueAddress(
                builder, MapType(atomic), address, atomic, "threadlocal.atomic.value");
        builder.BuildStore(address, builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 1));
        builder.BuildStore(_functions[destructor].Value,
            builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 2));
        LLVMValueRef registered = builder.BuildCall2(setSpecificType, setSpecific,
            new LLVMValueRef[] { key, node }, "threadlocal.cleanup.registered");
        LLVMBasicBlockRef registrationFailed = owner.AppendBasicBlock(
            "threadlocal.cleanup.registration.failed");
        LLVMBasicBlockRef registrationReady = owner.AppendBasicBlock(
            "threadlocal.cleanup.registration.ready");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, registered,
            LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
            "threadlocal.cleanup.failed"), registrationFailed, registrationReady);
        builder.PositionAtEnd(registrationFailed);
        builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [], false), trap,
            Array.Empty<LLVMValueRef>(), string.Empty);
        builder.BuildUnreachable();
        builder.PositionAtEnd(registrationReady);
    }

    private void EnsureUnixThreadLocalCleanupRuntime()
    {
        if (_unixThreadLocalCleanupCallback.Handle != IntPtr.Zero) return;
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        LLVMTypeRef keyType = GetPthreadKeyType();
        _threadLocalCleanupNodeType = _context.GetStructType([pointer, pointer, pointer], false);
        _unixThreadLocalCleanupKey = _module.AddGlobal(keyType,
            MangleManagedName(_moduleIdentity, "threadlocal_pthread_key", _moduleIdentity));
        _unixThreadLocalCleanupKey.Linkage = LLVMLinkage.LLVMInternalLinkage;
        _unixThreadLocalCleanupKey.Initializer =
            LLVMValueRef.CreateConstInt(keyType, ulong.MaxValue, false);
        LLVMTypeRef callbackType = LLVMTypeRef.CreateFunction(_context.VoidType, [pointer], false);
        _unixThreadLocalCleanupCallback = _module.AddFunction(
            MangleManagedName(_moduleIdentity, "threadlocal_pthread_cleanup", _moduleIdentity), callbackType);
        _unixThreadLocalCleanupCallback.Linkage = LLVMLinkage.LLVMInternalLinkage;
        EmitThreadLocalCleanupCallback(_unixThreadLocalCleanupCallback);
    }

    private void EmitThreadLocalCleanupCallback(LLVMValueRef callback)
    {
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        using LLVMBuilderRef builder = _context.CreateBuilder();
        LLVMBasicBlockRef entry = callback.AppendBasicBlock("entry");
        LLVMBasicBlockRef test = callback.AppendBasicBlock("cleanup.test");
        LLVMBasicBlockRef body = callback.AppendBasicBlock("cleanup.body");
        LLVMBasicBlockRef end = callback.AppendBasicBlock("cleanup.end");
        builder.PositionAtEnd(entry);
        LLVMValueRef current = builder.BuildAlloca(pointer, "threadlocal.cleanup.current");
        builder.BuildStore(callback.GetParam(0), current);
        builder.BuildBr(test);
        builder.PositionAtEnd(test);
        LLVMValueRef node = builder.BuildLoad2(pointer, current, "threadlocal.cleanup.node");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, node,
            LLVMValueRef.CreateConstPointerNull(pointer), "threadlocal.cleanup.present"), body, end);
        builder.PositionAtEnd(body);
        LLVMValueRef next = builder.BuildLoad2(pointer,
            builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 0), "threadlocal.cleanup.next");
        LLVMValueRef data = builder.BuildLoad2(pointer,
            builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 1), "threadlocal.cleanup.data");
        LLVMValueRef destructor = builder.BuildLoad2(pointer,
            builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 2),
            "threadlocal.cleanup.destructor");
        if (_exceptionsEnabled)
        {
            LlvmFunction cleanup = GetOrDeclareExceptionRuntime().Cleanup;
            builder.BuildCall2(cleanup.Type, cleanup.Value, new[] { destructor, data }, string.Empty);
        }
        else
        {
            builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [pointer], false), destructor,
                new LLVMValueRef[] { data }, string.Empty);
        }
        LlvmMemoryRuntime memory = GetOrDeclareMemoryRuntime();
        builder.BuildCall2(memory.FreeType, memory.Free, new LLVMValueRef[] { node }, string.Empty);
        builder.BuildStore(next, current);
        builder.BuildBr(test);
        builder.PositionAtEnd(end);
        builder.BuildRetVoid();
    }

    private void EmitWindowsThreadLocalDestructorRegistration(
        LLVMBuilderRef builder,
        FieldSymbol field,
        FunctionSymbol destructor)
    {
        EnsureWindowsThreadLocalCleanupRuntime();
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        LLVMTypeRef flsAllocType = LLVMTypeRef.CreateFunction(_context.Int32Type, [pointer], false);
        LLVMTypeRef flsFreeType = LLVMTypeRef.CreateFunction(_context.Int32Type, [_context.Int32Type], false);
        LLVMTypeRef flsGetType = LLVMTypeRef.CreateFunction(pointer, [_context.Int32Type], false);
        LLVMTypeRef flsSetType = LLVMTypeRef.CreateFunction(
            _context.Int32Type, [_context.Int32Type, pointer], false);
        LLVMValueRef flsAlloc = GetOrAddNativeFunction("FlsAlloc", flsAllocType);
        LLVMValueRef flsFree = GetOrAddNativeFunction("FlsFree", flsFreeType);
        LLVMValueRef flsGet = GetOrAddNativeFunction("FlsGetValue", flsGetType);
        LLVMValueRef flsSet = GetOrAddNativeFunction("FlsSetValue", flsSetType);
        LLVMValueRef sentinel = LLVMValueRef.CreateConstInt(_context.Int32Type, uint.MaxValue, false);
        var atomics = new LlvmAtomicOperations(builder);
        LLVMBasicBlockRef current = builder.InsertBlock;
        LLVMValueRef loaded = atomics.Load(_context.Int32Type, _windowsThreadLocalCleanupIndex,
            LLVMAtomicOrdering.LLVMAtomicOrderingAcquire, "threadlocal.fls.index");
        LLVMValueRef owner = current.Parent;
        LLVMBasicBlockRef allocate = owner.AppendBasicBlock("threadlocal.fls.allocate");
        LLVMBasicBlockRef candidateReady = owner.AppendBasicBlock("threadlocal.fls.candidate");
        LLVMBasicBlockRef allocationFailed = owner.AppendBasicBlock("threadlocal.fls.allocation.failed");
        LLVMBasicBlockRef won = owner.AppendBasicBlock("threadlocal.fls.won");
        LLVMBasicBlockRef lost = owner.AppendBasicBlock("threadlocal.fls.lost");
        LLVMBasicBlockRef use = owner.AppendBasicBlock("threadlocal.fls.use");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, loaded, sentinel,
            "threadlocal.fls.unallocated"), allocate, use);

        builder.PositionAtEnd(allocate);
        LLVMValueRef candidate = builder.BuildCall2(flsAllocType, flsAlloc,
            new LLVMValueRef[] { _windowsThreadLocalCleanupCallback }, "threadlocal.fls.candidate");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, candidate, sentinel,
            "threadlocal.fls.failed"), allocationFailed, candidateReady);

        builder.PositionAtEnd(allocationFailed);
        LLVMValueRef trap = GetOrDeclareTrap();
        builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [], false), trap,
            Array.Empty<LLVMValueRef>(), string.Empty);
        builder.BuildUnreachable();

        builder.PositionAtEnd(candidateReady);
        LlvmCompareExchangeResult installed = atomics.CompareExchange(
            _windowsThreadLocalCleanupIndex, sentinel, candidate,
            LLVMAtomicOrdering.LLVMAtomicOrderingAcquireRelease,
            LLVMAtomicOrdering.LLVMAtomicOrderingAcquire,
            "threadlocal.fls.install");
        builder.BuildCondBr(installed.Succeeded, won, lost);

        builder.PositionAtEnd(won);
        builder.BuildBr(use);
        builder.PositionAtEnd(lost);
        builder.BuildCall2(flsFreeType, flsFree, new LLVMValueRef[] { candidate }, string.Empty);
        builder.BuildBr(use);

        builder.PositionAtEnd(use);
        LLVMValueRef index = builder.BuildPhi(_context.Int32Type, "threadlocal.fls.selected");
        index.AddIncoming(
            [loaded, candidate, installed.Observed],
            [current, won, lost],
            3);
        LLVMValueRef head = builder.BuildCall2(flsGetType, flsGet,
            new LLVMValueRef[] { index }, "threadlocal.cleanup.head");
        LlvmMemoryRuntime memory = GetOrDeclareMemoryRuntime();
        ulong pointerBytes = (ulong)(_targetMachine?.PointerBitWidth ?? IntPtr.Size * 8) / 8;
        LLVMValueRef node = builder.BuildCall2(memory.MallocType, memory.Malloc,
            new LLVMValueRef[] { LLVMValueRef.CreateConstInt(memory.SizeType, pointerBytes * 3, false) },
            "threadlocal.cleanup.node");
        builder.BuildStore(head, builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 0));
        LLVMValueRef address = _staticFields[field];
        if (field.Type is AtomicTypeSymbol atomic)
            address = LlvmAtomicStorage.GetValueAddress(
                builder, MapType(atomic), address, atomic, "threadlocal.atomic.value");
        builder.BuildStore(address, builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 1));
        builder.BuildStore(_functions[destructor].Value,
            builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 2));
        LLVMValueRef registered = builder.BuildCall2(flsSetType, flsSet,
            new LLVMValueRef[] { index, node }, "threadlocal.cleanup.registered");
        LLVMBasicBlockRef registrationFailed = owner.AppendBasicBlock("threadlocal.cleanup.registration.failed");
        LLVMBasicBlockRef registrationReady = owner.AppendBasicBlock("threadlocal.cleanup.registration.ready");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, registered,
            LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), "threadlocal.cleanup.failed"),
            registrationFailed, registrationReady);
        builder.PositionAtEnd(registrationFailed);
        builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [], false), trap,
            Array.Empty<LLVMValueRef>(), string.Empty);
        builder.BuildUnreachable();
        builder.PositionAtEnd(registrationReady);
    }

    private void EnsureWindowsThreadLocalCleanupRuntime()
    {
        if (_windowsThreadLocalCleanupCallback.Handle != IntPtr.Zero) return;
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        _threadLocalCleanupNodeType = _context.GetStructType([pointer, pointer, pointer], false);
        _windowsThreadLocalCleanupIndex = _module.AddGlobal(_context.Int32Type,
            MangleManagedName(_moduleIdentity, "threadlocal_fls_index", _moduleIdentity));
        _windowsThreadLocalCleanupIndex.Linkage = LLVMLinkage.LLVMInternalLinkage;
        _windowsThreadLocalCleanupIndex.Initializer =
            LLVMValueRef.CreateConstInt(_context.Int32Type, uint.MaxValue, false);
        LLVMTypeRef callbackType = LLVMTypeRef.CreateFunction(_context.VoidType, [pointer], false);
        _windowsThreadLocalCleanupCallback = _module.AddFunction(
            MangleManagedName(_moduleIdentity, "threadlocal_fls_cleanup", _moduleIdentity), callbackType);
        _windowsThreadLocalCleanupCallback.Linkage = LLVMLinkage.LLVMInternalLinkage;
        using LLVMBuilderRef builder = _context.CreateBuilder();
        LLVMBasicBlockRef entry = _windowsThreadLocalCleanupCallback.AppendBasicBlock("entry");
        LLVMBasicBlockRef test = _windowsThreadLocalCleanupCallback.AppendBasicBlock("cleanup.test");
        LLVMBasicBlockRef body = _windowsThreadLocalCleanupCallback.AppendBasicBlock("cleanup.body");
        LLVMBasicBlockRef end = _windowsThreadLocalCleanupCallback.AppendBasicBlock("cleanup.end");
        builder.PositionAtEnd(entry);
        LLVMValueRef current = builder.BuildAlloca(pointer, "threadlocal.cleanup.current");
        builder.BuildStore(_windowsThreadLocalCleanupCallback.GetParam(0), current);
        builder.BuildBr(test);
        builder.PositionAtEnd(test);
        LLVMValueRef node = builder.BuildLoad2(pointer, current, "threadlocal.cleanup.node");
        builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, node,
            LLVMValueRef.CreateConstPointerNull(pointer), "threadlocal.cleanup.present"), body, end);
        builder.PositionAtEnd(body);
        LLVMValueRef next = builder.BuildLoad2(pointer,
            builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 0), "threadlocal.cleanup.next");
        LLVMValueRef data = builder.BuildLoad2(pointer,
            builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 1), "threadlocal.cleanup.data");
        LLVMValueRef destructor = builder.BuildLoad2(pointer,
            builder.BuildStructGEP2(_threadLocalCleanupNodeType, node, 2), "threadlocal.cleanup.destructor");
        if (_exceptionsEnabled)
        {
            LlvmFunction cleanup = GetOrDeclareExceptionRuntime().Cleanup;
            builder.BuildCall2(cleanup.Type, cleanup.Value, new[] { destructor, data }, string.Empty);
        }
        else
        {
            builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [pointer], false), destructor,
                new LLVMValueRef[] { data }, string.Empty);
        }
        LlvmMemoryRuntime memory = GetOrDeclareMemoryRuntime();
        builder.BuildCall2(memory.FreeType, memory.Free, new LLVMValueRef[] { node }, string.Empty);
        builder.BuildStore(next, current);
        builder.BuildBr(test);
        builder.PositionAtEnd(end);
        builder.BuildRetVoid();
    }

    private LLVMValueRef GetOrAddNativeFunction(string name, LLVMTypeRef type)
    {
        LLVMValueRef function = _module.GetNamedFunction(name);
        return function.Handle == IntPtr.Zero ? _module.AddFunction(name, type) : function;
    }

    private LLVMValueRef CreateStaticInitializer(TypeSymbol type, object? value)
    {
        if (type is AtomicTypeSymbol atomic)
        {
            if (LlvmAtomicStorage.RequiresLock(atomic.ElementType))
                return LLVMValueRef.CreateConstNamedStruct(MapType(atomic),
                [
                    LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false),
                    value is null
                        ? DefaultValue(atomic.ElementType, MapType, _virtualTables)
                        : CreateStaticInitializer(atomic.ElementType, value),
                ]);
            if (TypeIdentity.AreSame(atomic.ElementType, BuiltinTypes.Bool))
                return LLVMValueRef.CreateConstInt(MapType(type), value is true ? 1UL : 0UL, false);
            return CreateStaticInitializer(atomic.ElementType, value);
        }
        LLVMTypeRef llvmType = MapType(type);
        if (value is null)
            return DefaultValue(type, MapType, _virtualTables);
        if (TypeIdentity.AreSame(type, BuiltinTypes.Bool))
            return LLVMValueRef.CreateConstInt(llvmType, value is true ? 1UL : 0UL, false);
        if (type is PrimitiveTypeSymbol { IsInteger: true })
            return LLVMValueRef.CreateConstInt(llvmType, GetIntegerConstantBits(value), false);
        if (type is PrimitiveTypeSymbol { IsCharacter: true })
            return LLVMValueRef.CreateConstInt(llvmType, GetIntegerConstantBits(value), false);
        if (type is PrimitiveTypeSymbol { IsFloatingPoint: true })
            return LLVMValueRef.CreateConstReal(llvmType, Convert.ToDouble(value));
        throw new LlvmCodeGenerationException($"static field type '{type.Name}' does not support a constant initializer");
    }

    private static LLVMValueRef DefaultValue(TypeSymbol type, Func<TypeSymbol, LLVMTypeRef> mapType,
        Dictionary<StructTypeSymbol, LlvmVTable> virtualTables, StructTypeSymbol? runtimeType = null)
    {
        if (type is AtomicTypeSymbol atomic && LlvmAtomicStorage.RequiresLock(atomic.ElementType))
            return LLVMValueRef.CreateConstNamedStruct(mapType(atomic),
            [
                LLVMValueRef.CreateConstInt(mapType(BuiltinTypes.Byte), 0, false),
                DefaultValue(atomic.ElementType, mapType, virtualTables, runtimeType),
            ]);
        if (type is StorageTypeSymbol storage)
            return LLVMValueRef.CreateConstNamedStruct(mapType(storage),
                [DefaultValue(storage.ElementType, mapType, virtualTables),
                    LLVMValueRef.CreateConstInt(mapType(BuiltinTypes.Bool), 0)]);
        if (type is LifetimeModifierTypeSymbol modifier)
            return DefaultValue(modifier.ElementType, mapType, virtualTables, runtimeType);
        if (type is not StructTypeSymbol structure) return LLVMValueRef.CreateConstNull(mapType(type));
        var fields = new List<LLVMValueRef>();
        runtimeType ??= structure;
        if (structure.BaseType is not null)
            fields.Add(DefaultValue(structure.BaseType, mapType, virtualTables, runtimeType));
        if (structure.IntroducesVirtualDispatch) fields.Add(virtualTables[runtimeType].Value);
        foreach (FieldSymbol field in structure.Fields)
            fields.Add(DefaultValue(field.Type, mapType, virtualTables));
        return LLVMValueRef.CreateConstNamedStruct(mapType(type), fields.ToArray());
    }

    private static bool HasAllBitsZeroDefault(TypeSymbol type)
    {
        if (type is AtomicTypeSymbol atomic) return HasAllBitsZeroDefault(atomic.ElementType);
        if (type is not StructTypeSymbol structure)
            return true;

        return !structure.IntroducesVirtualDispatch &&
               (structure.BaseType is null || HasAllBitsZeroDefault(structure.BaseType)) &&
               structure.Fields.All(field => HasAllBitsZeroDefault(field.Type));
    }

    private static bool HasDefaultInstanceInitializer(StructTypeSymbol structure) =>
        structure.InstanceInitializer is not null ||
        structure.BaseType is not null && HasDefaultInstanceInitializer(structure.BaseType);

    private static ulong GetIntegerConstantBits(object value) => value switch
    {
        int integer => unchecked((ulong)(long)integer),
        long integer => unchecked((ulong)integer),
        ulong integer => integer,
        _ => Convert.ToUInt64(value),
    };

    private static void CollectStructTypes(NamespaceSymbol @namespace, ICollection<StructTypeSymbol> types)
    {
        foreach (StructTypeSymbol type in @namespace.Structs.Where(type => type.IsConcreteType && !type.IsStatic))
        {
            types.Add(type);
        }

        foreach (NamespaceSymbol child in @namespace.Namespaces)
        {
            CollectStructTypes(child, types);
        }
    }

    private static IEnumerable<DeclaredTypeSymbol> StaticFieldOwners(NamespaceSymbol @namespace) =>
        @namespace.Structs.Where(type => type.IsConcreteType).Cast<DeclaredTypeSymbol>().Concat(@namespace.Enums);

    private static ImmutableArray<FieldSymbol> GetStaticFields(DeclaredTypeSymbol type) => type switch
    {
        StructTypeSymbol structure => structure.StaticFields,
        EnumTypeSymbol enumeration => enumeration.StaticFields,
        _ => [],
    };

    private static bool IsExternallyVisible(Symbol symbol) =>
        AccessibilityFacts.IsExternallyInheritable(AccessibilityRules.GetAccessibility(symbol)) &&
        (symbol.ContainingSymbol switch
        {
            NamespaceSymbol => true,
            DeclaredTypeSymbol type => type.IsPublic,
            PropertySymbol property => property.ContainingType.IsPublic,
            IndexerSymbol indexer => indexer.ContainingType.IsPublic,
            _ => true,
        });

    private void DeclareFunctions(NamespaceSymbol @namespace)
    {
        foreach (FunctionSymbol function in @namespace.Functions)
        {
            if (!function.IsGenericDefinition && _compilation.IsImportedSymbolNativeReachable(function))
                DeclareFunction(function);
        }

        foreach (StructTypeSymbol type in @namespace.Structs.Where(type => type.IsConcreteType)
                     .Where(_compilation.IsImportedSymbolNativeReachable))
        {
            foreach (FunctionSymbol method in type.Methods)
            {
                if (!method.IsGenericDefinition && _compilation.IsImportedSymbolNativeReachable(method))
                    DeclareFunction(method);
            }

            foreach (FunctionSymbol constructor in type.Constructors
                         .Where(_compilation.IsImportedSymbolNativeReachable))
                DeclareFunction(constructor);

            if (type.InstanceInitializer is not null &&
                _compilation.IsImportedSymbolNativeReachable(type.InstanceInitializer))
                DeclareFunction(type.InstanceInitializer);

            if (type.Destructor is not null && _compilation.IsImportedSymbolNativeReachable(type.Destructor))
            {
                DeclareFunction(type.Destructor);
            }

            if (type.CompleteDestructor is { FunctionKind: FunctionKind.DestructorGlue } destructor &&
                _compilation.IsImportedSymbolNativeReachable(destructor))
                DeclareFunction(destructor);
        }

        foreach (NamespaceSymbol child in @namespace.Namespaces)
        {
            DeclareFunctions(child);
        }
    }

    private void DeclareFunction(FunctionSymbol function)
    {
        LLVMTypeRef functionType = CreateDirectFunctionType(function);
        string nativeName = GetFunctionNativeName(function);
        bool requiresCAbi = LlvmCAbi.RequiresLowering(
            function.ReturnType,
            function.Parameters.Select(parameter => parameter.Type));
        bool requiresExceptionBoundary = function.IsExport && _exceptionsEnabled;
        if (function.IsExtern && requiresCAbi)
        {
            LlvmCAbiFunctionPlan plan = RequireCAbi().Classify(
                function.ReturnType,
                GetCAbiParameterTypes(function));
            if (_nativeFunctions.TryGetValue(nativeName, out LlvmFunction existingExternal))
            {
                if (existingExternal.Type != plan.FunctionType)
                    throw new LlvmCodeGenerationException($"Native symbol '{nativeName}' has incompatible declarations.");
                _functions.Add(function, existingExternal);
                _cAbiFunctions.Add(function, new LlvmCAbiFunction(existingExternal.Value, plan));
                return;
            }
            LLVMValueRef external = _module.AddFunction(nativeName, plan.FunctionType);
            external.Linkage = LLVMLinkage.LLVMExternalLinkage;
            ApplyCAbiFunctionAttributes(external, plan);
            var declaration = new LlvmFunction(external, plan.FunctionType);
            _functions.Add(function, declaration);
            _nativeFunctions.Add(nativeName, declaration);
            _cAbiFunctions.Add(function, new LlvmCAbiFunction(external, plan));
            return;
        }

        string implementationName = function.IsExport && (requiresCAbi || requiresExceptionBoundary)
            ? GetManagedName(function, "export_implementation", function.FullName)
            : nativeName;
        if (_nativeFunctions.TryGetValue(nativeName, out LlvmFunction existing))
        {
            if (!function.IsExtern || existing.Type != functionType)
                throw new LlvmCodeGenerationException($"Native symbol '{nativeName}' has incompatible declarations.");
            _functions.Add(function, existing);
            return;
        }
        LLVMValueRef value = _module.AddFunction(implementationName, functionType);
        ApplyProvenFunctionAttributes(function, value);
        bool wholeProgramDefinition = _wholeProgramFunctions.Contains(function);
        bool owned = _compilation.IsSymbolDefinedInCurrentArtifact(function) || wholeProgramDefinition;
        if (!owned)
        {
            value.Linkage = LLVMLinkage.LLVMExternalLinkage;
        }
        else if (function.IsExport && implementationName != nativeName)
        {
            // Cross-artifact callers use the native export thunk, never its implementation.
            value.Linkage = LLVMLinkage.LLVMInternalLinkage;
        }
        else if (wholeProgramDefinition && !function.IsExport)
        {
            value.Linkage = LLVMLinkage.LLVMInternalLinkage;
        }
        else if (function.IsAbstract)
        {
            value.Linkage = _implementationAbiRoots.Contains(function)
                ? LLVMLinkage.LLVMExternalLinkage
                : LLVMLinkage.LLVMInternalLinkage;
            using LLVMBuilderRef builder = _context.CreateBuilder();
            LLVMBasicBlockRef entry = value.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            builder.BuildUnreachable();
        }
        else if (!function.IsExtern && !function.IsExport &&
                 !IsExternallyVisible(function) &&
                 !_implementationAbiRoots.Contains(function) &&
                 function.FunctionKind != FunctionKind.InstanceInitializer)
        {
            value.Linkage = LLVMLinkage.LLVMInternalLinkage;
        }
        else if (function.IsExport && !requiresCAbi && !requiresExceptionBoundary && IsWindowsTarget())
        {
            value.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
        }

        var llvmFunction = new LlvmFunction(value, functionType);
        _functions.Add(function, llvmFunction);
        if ((!requiresCAbi && !requiresExceptionBoundary) || !function.IsExport)
            _nativeFunctions.Add(nativeName, llvmFunction);

        if ((!requiresCAbi && !requiresExceptionBoundary) || function.IsAbstract ||
            !function.IsExport &&
            (_cAbi is null || !_cAbi.SupportsStructValues || !HasCAbiCompatibleSignature(function)))
            return;

        LlvmCAbiFunctionPlan abiPlan = RequireCAbi().Classify(
            function.ReturnType,
            GetCAbiParameterTypes(function));
        string thunkName = function.IsExport
            ? nativeName
            : GetManagedName(function, "cabi_thunk", function.FullName);
        LLVMValueRef thunk = _module.AddFunction(thunkName, abiPlan.FunctionType);
        thunk.Linkage = function.IsExport
            ? LLVMLinkage.LLVMExternalLinkage
            : LLVMLinkage.LLVMInternalLinkage;
        if (function.IsExport && IsWindowsTarget())
            thunk.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
        ApplyCAbiFunctionAttributes(thunk, abiPlan);
        // ABI marshalling can read/write indirect argument and return storage even
        // when the Xenon implementation itself is memory(none).
        ApplyProvenFunctionAttributes(function, thunk, includeMemoryEffects: false);
        _cAbiFunctions.Add(function, new LlvmCAbiFunction(thunk, abiPlan));
        if (function.IsExport)
            _nativeFunctions.Add(nativeName, new LlvmFunction(thunk, abiPlan.FunctionType));
    }

    private LLVMTypeRef CreateDirectFunctionType(FunctionSymbol function)
    {
        var parameterTypes = new List<LLVMTypeRef>();
        if (function.HasImplicitThis)
            parameterTypes.Add(LLVMTypeRef.CreatePointer(MapType(function.ContainingType!), 0));
        if (function.IsCapturingLambda)
            parameterTypes.Add(LLVMTypeRef.CreatePointer(_context.Int8Type, 0));
        parameterTypes.AddRange(function.Parameters.Select(parameter => MapType(parameter.Type)));
        return LLVMTypeRef.CreateFunction(MapType(function.ReturnType), [.. parameterTypes], false);
    }

    private void DeclareClosureEnvironmentDestructors(ImmutableArray<MirFunction> functions)
    {
        foreach (MirFunction function in functions)
            if (function.Symbol.ClosureEnvironmentOwner is { } owner)
                _closureEnvironmentDestructors.Add(owner, _functions[function.Symbol]);
    }
    private IEnumerable<TypeSymbol> GetCAbiParameterTypes(FunctionSymbol function)
    {
        if (function.HasImplicitThis)
            yield return _compilation.SemanticModel.TypeFactory.PointerTo(function.ContainingType!);
        foreach (ParameterSymbol parameter in function.Parameters)
            yield return parameter.Type;
    }

    private static bool HasCAbiCompatibleSignature(FunctionSymbol function) =>
        IsCAbiCompatibleType(function.ReturnType) &&
        function.Parameters.All(parameter => IsCAbiCompatibleType(parameter.Type));

    private static bool IsCAbiCompatibleType(TypeSymbol type) =>
        type is not StructTypeSymbol structure ||
        TypeFacts.GetCAbiStructIncompatibility(structure) is null;

    private LlvmCAbi RequireCAbi() => _cAbi ?? throw new LlvmCodeGenerationException(
        "C ABI struct lowering requires a configured LLVM target; use GenerateForTarget or object emission.");

    private void ApplyCAbiFunctionAttributes(LLVMValueRef function, LlvmCAbiFunctionPlan plan)
    {
        using LLVMBuilderRef builder = _context.CreateBuilder();
        new LlvmCAbiMarshaller(_context, builder).ApplyFunctionAttributes(function, plan);
    }

    private void EmitCAbiThunks()
    {
        bool exceptionsEnabled = _exceptionsEnabled;
        foreach ((FunctionSymbol symbol, LlvmCAbiFunction abi) in _cAbiFunctions)
        {
            if (symbol.IsExtern) continue;
            LlvmFunction implementation = _functions[symbol];
            using LLVMBuilderRef builder = _context.CreateBuilder();
            LLVMBasicBlockRef entry = abi.Value.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            var marshaller = new LlvmCAbiMarshaller(_context, builder);
            ImmutableArray<LLVMValueRef> arguments = marshaller.UnpackParameters(abi.Value, abi.Plan);
            string resultName = TypeIdentity.AreSame(symbol.ReturnType, BuiltinTypes.Void)
                ? string.Empty : "result";
            LLVMValueRef result;
            bool implementationMayThrow = !_functionEffects.TryGetValue(symbol, out LlvmFunctionEffects effects) ||
                effects.MayThrow;
            if (exceptionsEnabled && implementationMayThrow)
            {
                LLVMBasicBlockRef normal = abi.Value.AppendBasicBlock("invoke.continue");
                LLVMBasicBlockRef unwind = abi.Value.AppendBasicBlock("invoke.unwind");
                result = builder.BuildInvoke2(implementation.Type, implementation.Value,
                    arguments.ToArray(), normal, unwind, resultName);
                EmitCAbiBoundaryTermination(builder, abi.Value, unwind);
                builder.PositionAtEnd(normal);
            }
            else
            {
                result = builder.BuildCall2(implementation.Type, implementation.Value,
                    arguments.ToArray(), resultName);
            }
            marshaller.EmitReturn(abi.Value, abi.Plan, result);
        }
    }

    private unsafe void EmitCAbiBoundaryTermination(
        LLVMBuilderRef builder,
        LLVMValueRef function,
        LLVMBasicBlockRef unwind)
    {
        LLVMBasicBlockRef terminate = function.AppendBasicBlock("exception.terminate");
        builder.PositionAtEnd(unwind);
        if (IsWindowsTarget())
        {
            LLVMValueRef personality = GetOrDeclarePersonality(function.GlobalParent, "__CxxFrameHandler3");
            function.PersonalityFn = personality;
            LLVMTypeRef tokenType = new((nint)LLVMApi.TokenTypeInContext(_context));
            LLVMValueRef parentPad = LLVMValueRef.CreateConstNull(tokenType);
            sbyte* emptyName = stackalloc sbyte[1];
            emptyName[0] = 0;
            LLVMValueRef catchSwitch = new((nint)LLVMApi.BuildCatchSwitch(
                builder, parentPad, default, 1, emptyName));
            LLVMBasicBlockRef handler = function.AppendBasicBlock("exception.catchpad");
            LLVMApi.AddHandler(catchSwitch, handler);
            builder.PositionAtEnd(handler);
            LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMValueRef[] catchArguments =
            {
                LLVMValueRef.CreateConstPointerNull(pointer),
                LLVMValueRef.CreateConstInt(_context.Int32Type, 64),
                LLVMValueRef.CreateConstPointerNull(pointer),
            };
            fixed (LLVMValueRef* argumentPointer = catchArguments)
            {
                LLVMValueRef catchPad = new((nint)LLVMApi.BuildCatchPad(
                    builder, catchSwitch, (LLVMOpaqueValue**)argumentPointer,
                    (uint)catchArguments.Length, emptyName));
                LLVMApi.BuildCatchRet(builder, catchPad, terminate);
            }
        }
        else
        {
            LLVMValueRef personality = GetOrDeclarePersonality(function.GlobalParent, "__gxx_personality_v0");
            function.PersonalityFn = personality;
            LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef landingType = _context.GetStructType([pointer, _context.Int32Type], false);
            LLVMValueRef landing = builder.BuildLandingPad(landingType, personality, 1,
                "exception.landingpad");
            landing.AddClause(LLVMValueRef.CreateConstPointerNull(pointer));
            LLVMValueRef nativeException = builder.BuildExtractValue(landing, 0, "native.exception");
            LLVMTypeRef beginType = LLVMTypeRef.CreateFunction(pointer, [pointer], false);
            LLVMValueRef begin = GetOrAddFunction(function.GlobalParent, "__cxa_begin_catch", beginType);
            LLVMTypeRef endType = LLVMTypeRef.CreateFunction(_context.VoidType, [], false);
            LLVMValueRef end = GetOrAddFunction(function.GlobalParent, "__cxa_end_catch", endType);
            builder.BuildCall2(beginType, begin, new[] { nativeException }, string.Empty);
            builder.BuildCall2(endType, end, Array.Empty<LLVMValueRef>(), string.Empty);
            builder.BuildBr(terminate);
        }

        builder.PositionAtEnd(terminate);
        LlvmFunction runtimeTerminate = GetOrDeclareExceptionRuntime().Terminate;
        builder.BuildCall2(runtimeTerminate.Type, runtimeTerminate.Value,
            Array.Empty<LLVMValueRef>(), string.Empty);
        builder.BuildUnreachable();
    }

    private LLVMValueRef GetOrDeclarePersonality(LLVMModuleRef module, string name)
    {
        LLVMValueRef existing = module.GetNamedFunction(name);
        return existing.Handle != IntPtr.Zero
            ? existing
            : module.AddFunction(name, LLVMTypeRef.CreateFunction(_context.Int32Type, [], true));
    }

    private static LLVMValueRef GetOrAddFunction(LLVMModuleRef module, string name, LLVMTypeRef type)
    {
        LLVMValueRef existing = module.GetNamedFunction(name);
        return existing.Handle != IntPtr.Zero ? existing : module.AddFunction(name, type);
    }

    private LLVMValueRef GetFunctionValueInvokeAddress(FunctionSymbol function)
    {
        if (!function.IsExtern) return _functions[function].Value;
        if (!LlvmCAbi.RequiresLowering(
                function.ReturnType, function.Parameters.Select(parameter => parameter.Type)))
            return _cAbiFunctions.TryGetValue(function, out LlvmCAbiFunction directAbi)
                ? directAbi.Value
                : _functions[function].Value;
        if (_functionValueAdapters.TryGetValue(function, out LlvmFunction existing))
            return existing.Value;
        if (!_cAbiFunctions.TryGetValue(function, out LlvmCAbiFunction abi))
            throw new LlvmCodeGenerationException(
                $"Missing C ABI declaration for extern function '{function.FullName}'.");

        LLVMTypeRef signature = CreateDirectFunctionType(function);
        LLVMValueRef adapterValue = _module.AddFunction(
            MangleManagedName(_moduleIdentity, "function_value_adapter", function.FullName), signature);
        adapterValue.Linkage = LLVMLinkage.LLVMInternalLinkage;
        var adapter = new LlvmFunction(adapterValue, signature);
        _functionValueAdapters.Add(function, adapter);

        using LLVMBuilderRef builder = _context.CreateBuilder();
        builder.PositionAtEnd(adapterValue.AppendBasicBlock("entry"));
        var marshaller = new LlvmCAbiMarshaller(_context, builder);
        LLVMValueRef result = marshaller.EmitCall(abi.Value, abi.Plan,
            Enumerable.Range(0, function.Parameters.Length)
                .Select(index => adapterValue.GetParam(checked((uint)index))).ToArray(),
            TypeIdentity.AreSame(function.ReturnType, BuiltinTypes.Void)
                ? string.Empty
                : "function.value.extern.result");
        if (TypeIdentity.AreSame(function.ReturnType, BuiltinTypes.Void))
            builder.BuildRetVoid();
        else
            builder.BuildRet(result);
        return adapterValue;
    }

    private void EmitFunctionBodies(ImmutableArray<MirFunction> functions)
    {
        foreach (MirFunction function in functions)
        {
            LlvmFunction declaration = _functions[function.Symbol];
            using LLVMBuilderRef builder = _context.CreateBuilder();
            LLVMBasicBlockRef entry = declaration.Value.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);

            var emitter = new FunctionEmitter(
                _context,
                builder,
                function.Symbol,
                declaration.Value,
                _functions,
                _functionEffects,
                _cAbiFunctions,
                _cAbi,
                _staticFields,
                _threadLocalEnsures,
                _virtualTables,
                _interfaceMaps,
                _interfaceKeys,
                _closureEnvironmentDestructors,
                GetFunctionValueInvokeAddress,
                _interfaceMapEntryType,
                MapType,
                GetOrDeclareMemoryRuntime,
                GetOrDeclareExceptionRuntime,
                GetOrDeclareExceptionTypeKey,
                GetExceptionTypeDisplayName,
                GetOrDeclareTrap,
                GetOrDeclareStringCompare,
                GetAbiSize,
                GetAbiAlignment,
                GetFieldOffset,
                GetIntegerBitWidth,
                _compilation.Options.EnableRuntimeChecks,
                IsWindowsTarget(),
                _exceptionsEnabled);
            if (function.Resumable is not null) emitter.EmitMirResumable(function);
            else emitter.EmitMir(function);
        }
    }

    private void EmitExecutableEntryPoint(ImmutableArray<FunctionSymbol> functions)
    {
        FunctionSymbol[] candidates = functions
            .Where(function =>
                function.FunctionKind == FunctionKind.Ordinary &&
                function.ContainingType is null &&
                function.Name == "Main")
            .ToArray();
        if (candidates.Length == 0)
        {
            throw new LlvmCodeGenerationException(
                "Executable project must declare exactly one entry point 'int Main()'.");
        }

        if (candidates.Length > 1)
        {
            throw new LlvmCodeGenerationException(
                "Executable project declares multiple functions named 'Main'.");
        }

        FunctionSymbol entryPoint = candidates[0];
        if (entryPoint.IsAsync)
        {
            entryPoint = _compilation.GetMirFunctions().Single(function =>
                ReferenceEquals(function.Symbol.AsyncEntryPointOwner, entryPoint)).Symbol;
        }
        if (!TypeIdentity.AreSame(entryPoint.ReturnType, BuiltinTypes.Int) || !entryPoint.Parameters.IsEmpty)
        {
            throw new LlvmCodeGenerationException(
                $"Entry point '{entryPoint.FullName}' must have signature 'int Main()'.");
        }

        LlvmFunction xenonEntryPoint = _functions[entryPoint];
        LLVMTypeRef nativeEntryPointType = LLVMTypeRef.CreateFunction(_context.Int32Type, [], false);
        if (_module.GetNamedFunction("main").Handle != IntPtr.Zero)
        {
            throw new LlvmCodeGenerationException(
                "Native symbol 'main' conflicts with the generated executable entry point.");
        }

        LLVMValueRef nativeEntryPoint = _module.AddFunction("main", nativeEntryPointType);
        ApplyProvenFunctionAttributes(entryPoint, nativeEntryPoint);
        LLVMBasicBlockRef block = nativeEntryPoint.AppendBasicBlock("entry");
        using LLVMBuilderRef builder = _context.CreateBuilder();
        builder.PositionAtEnd(block);
        LLVMValueRef result;
        bool entryPointMayThrow = !_functionEffects.TryGetValue(entryPoint, out LlvmFunctionEffects effects) ||
            effects.MayThrow;
        if (_exceptionsEnabled && entryPointMayThrow)
        {
            LLVMBasicBlockRef normal = nativeEntryPoint.AppendBasicBlock("invoke.continue");
            LLVMBasicBlockRef unwind = nativeEntryPoint.AppendBasicBlock("invoke.unwind");
            result = builder.BuildInvoke2(xenonEntryPoint.Type, xenonEntryPoint.Value,
                Array.Empty<LLVMValueRef>(), normal, unwind, "result");
            EmitCAbiBoundaryTermination(builder, nativeEntryPoint, unwind);
            builder.PositionAtEnd(normal);
        }
        else
        {
            result = builder.BuildCall2(xenonEntryPoint.Type, xenonEntryPoint.Value,
                Array.Empty<LLVMValueRef>(), "result");
        }
        builder.BuildRet(result);
    }

    private LLVMTypeRef MapType(TypeSymbol type)
    {
        if (type is AtomicTypeSymbol atomic)
        {
            if (LlvmAtomicStorage.RequiresLock(atomic.ElementType))
            {
                if (_atomicTypes.TryGetValue(atomic, out LLVMTypeRef existing)) return existing;
                LLVMTypeRef result = _context.CreateNamedStruct($"__xenon.atomic.{_atomicTypes.Count}");
                _atomicTypes.Add(atomic, result);
                result.StructSetBody([_context.Int8Type, MapType(atomic.ElementType)], false);
                return result;
            }
            return TypeIdentity.AreSame(atomic.ElementType, BuiltinTypes.Bool)
                ? _context.Int8Type
                : MapType(atomic.ElementType);
        }
        if (type is StorageTypeSymbol storage)
        {
            if (_storageTypes.TryGetValue(storage, out LLVMTypeRef existing)) return existing;
            LLVMTypeRef result = _context.CreateNamedStruct($"__xenon.storage.{_storageTypes.Count}");
            _storageTypes.Add(storage, result);
            result.StructSetBody([MapType(storage.ElementType), _context.Int1Type], false);
            return result;
        }
        if (type is LifetimeModifierTypeSymbol modifier)
            return MapType(modifier.ElementType);
        if (TypeIdentity.AreSame(type, BuiltinTypes.Void))
        {
            return _context.VoidType;
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.Bool))
        {
            return _context.Int1Type;
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.Byte) || TypeIdentity.AreSame(type, BuiltinTypes.SByte))
        {
            return _context.Int8Type;
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.Short) || TypeIdentity.AreSame(type, BuiltinTypes.UShort))
        {
            return _context.Int16Type;
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.Int) || TypeIdentity.AreSame(type, BuiltinTypes.UInt) ||
            TypeIdentity.AreSame(type, BuiltinTypes.Char))
        {
            return _context.Int32Type;
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.Long) || TypeIdentity.AreSame(type, BuiltinTypes.ULong))
        {
            return _context.Int64Type;
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.NInt) || TypeIdentity.AreSame(type, BuiltinTypes.NUInt))
        {
            return MapTargetInteger(type, GetPointerBitWidth());
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.CLong) || TypeIdentity.AreSame(type, BuiltinTypes.CULong))
        {
            int bitWidth = IsWindowsTarget() ? 32 : GetPointerBitWidth();
            return MapTargetInteger(type, bitWidth);
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.Float))
        {
            return _context.FloatType;
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.Double))
        {
            return _context.DoubleType;
        }

        if (type is ArrayTypeSymbol array)
        {
            return LLVMTypeRef.CreatePointer(MapType(array.ElementType), 0);
        }

        if (type is UniqueTypeSymbol unique)
        {
            return MapType(unique.StorageType);
        }

        if (type is SharedTypeSymbol or WeakTypeSymbol)
            return LLVMTypeRef.CreatePointer(_context.Int8Type, 0);

        if (type is EnumTypeSymbol enumeration) return MapType(enumeration.UnderlyingType);

        if (type is PointerTypeSymbol pointer)
        {
            return LLVMTypeRef.CreatePointer(MapType(pointer.ElementType), 0);
        }

        if (type is FunctionPointerTypeSymbol functionPointer)
        {
            LLVMTypeRef signature = LLVMTypeRef.CreateFunction(
                MapType(functionPointer.ReturnType),
                [.. functionPointer.ParameterTypes.Select(MapType)],
                false);
            return LLVMTypeRef.CreatePointer(signature, 0);
        }

        if (type is FunctionValueTypeSymbol)
        {
            LLVMTypeRef functionValuePointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            return _context.GetStructType([functionValuePointer, functionValuePointer], false);
        }

        if (type is ReferenceTypeSymbol reference)
        {
            return LLVMTypeRef.CreatePointer(MapType(reference.ElementType), 0);
        }

        if (type is StructTypeSymbol structType)
        {
            if (_structTypes.TryGetValue(structType, out LLVMTypeRef llvmStruct)) return llvmStruct;
            llvmStruct = _context.CreateNamedStruct(structType.FullName);
            _structTypes.Add(structType, llvmStruct);
            llvmStruct.StructSetBody(LlvmStructLayout.Elements(structType, MapType, LLVMTypeRef.CreatePointer(_context.Int8Type, 0)), false);
            return llvmStruct;
        }

        if (type is InterfaceTypeSymbol interfaceType && _interfaceTypes.TryGetValue(interfaceType, out LLVMTypeRef llvmInterface))
        {
            return llvmInterface;
        }

        throw new LlvmCodeGenerationException(
            $"Type '{type.Name}' requires target information and cannot be lowered by the target-independent LLVM milestone.");
    }

    private LLVMTypeRef MapTargetInteger(TypeSymbol type, int bitWidth) => bitWidth switch
    {
        32 => _context.Int32Type,
        64 => _context.Int64Type,
        _ => throw new LlvmCodeGenerationException(
            $"Target integer type '{type.Name}' has unsupported width {bitWidth}."),
    };

    private void ValidateEnumStorage(NamespaceSymbol scope)
    {
        foreach (EnumTypeSymbol enumeration in scope.Enums)
        {
            if (enumeration.UnderlyingType.BitWidth is null && _targetMachine is null) continue;
            int bits = GetIntegerBitWidth(enumeration.UnderlyingType);
            foreach (ConstantSymbol member in enumeration.Members)
                if (!FitsTargetInteger(member.Value, bits, enumeration.UnderlyingType.IsSigned))
                    throw new LlvmCodeGenerationException($"enum member '{enumeration.FullName}.{member.Name}' is out of range for the selected target's '{enumeration.UnderlyingType.Name}'");
        }
        foreach (NamespaceSymbol child in scope.Namespaces) ValidateEnumStorage(child);
    }

    private static bool FitsTargetInteger(object? value, int bits, bool signed)
    {
        BigInteger number = value switch
        {
            int integer => integer,
            long integer => integer,
            ulong integer => integer,
            _ => throw new LlvmCodeGenerationException("Invalid integer constant."),
        };
        return signed
            ? number >= -(BigInteger.One << (bits - 1)) && number < (BigInteger.One << (bits - 1))
            : number >= 0 && number < (BigInteger.One << bits);
    }

    private int GetIntegerBitWidth(TypeSymbol type)
    {
        if (type is EnumTypeSymbol enumeration) return GetIntegerBitWidth(enumeration.UnderlyingType);
        if (TypeIdentity.AreSame(type, BuiltinTypes.Char)) return 32;
        if (type is PrimitiveTypeSymbol { IsInteger: true, BitWidth: int bitWidth })
        {
            return bitWidth;
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.NInt) || TypeIdentity.AreSame(type, BuiltinTypes.NUInt))
        {
            return GetPointerBitWidth();
        }

        if (TypeIdentity.AreSame(type, BuiltinTypes.CLong) || TypeIdentity.AreSame(type, BuiltinTypes.CULong))
        {
            return IsWindowsTarget() ? 32 : GetPointerBitWidth();
        }

        throw new LlvmCodeGenerationException($"Type '{type.Name}' is not an integer type.");
    }

    private ulong GetAbiSize(TypeSymbol type)
    {
        if (_targetMachine is null)
        {
            throw new LlvmCodeGenerationException(
                "Heap allocation requires a configured LLVM target and data layout.");
        }

        return _targetMachine.TargetData.ABISizeOfType(MapType(type));
    }

    private uint GetAbiAlignment(TypeSymbol type)
    {
        if (_targetMachine is null)
            throw new LlvmCodeGenerationException("alignof requires a configured LLVM target and data layout.");
        return _targetMachine.TargetData.ABIAlignmentOfType(MapType(type));
    }

    private ulong GetFieldOffset(StructTypeSymbol type, FieldSymbol field)
    {
        if (_targetMachine is null)
            throw new LlvmCodeGenerationException("offsetof requires a configured LLVM target and data layout.");
        return _targetMachine.TargetData.OffsetOfElement(MapType(field.ContainingType), (uint)field.Ordinal);
    }

    private LlvmMemoryRuntime GetOrDeclareMemoryRuntime()
    {
        if (_memoryRuntime is not null)
        {
            return _memoryRuntime;
        }

        if (_targetMachine is null)
        {
            throw new LlvmCodeGenerationException(
                "Heap allocation requires a configured LLVM target and data layout.");
        }

        if (_module.GetNamedFunction("malloc").Handle != IntPtr.Zero ||
            _module.GetNamedFunction("calloc").Handle != IntPtr.Zero ||
            _module.GetNamedFunction("free").Handle != IntPtr.Zero)
        {
            throw new LlvmCodeGenerationException(
                "Native symbols 'malloc', 'calloc', and 'free' are reserved for Xenon heap operations.");
        }

        // Every Xenon heap allocation carries one hidden native-pointer word immediately
        // before the aligned address. This is the only allocator metadata: it makes one
        // deallocator work for ordinary and over-aligned blocks on every platform without
        // a process-global allocation registry.
        LLVMTypeRef pointerType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        LLVMTypeRef sizeType = MapTargetInteger(BuiltinTypes.NUInt, _targetMachine.PointerBitWidth);
        LLVMTypeRef nativeMallocType = LLVMTypeRef.CreateFunction(pointerType, [sizeType], false);
        LLVMTypeRef nativeCallocType = LLVMTypeRef.CreateFunction(pointerType, [sizeType, sizeType], false);
        LLVMTypeRef nativeFreeType = LLVMTypeRef.CreateFunction(_context.VoidType, [pointerType], false);
        LLVMValueRef nativeMalloc = _module.AddFunction("malloc", nativeMallocType);
        LLVMValueRef nativeCalloc = _module.AddFunction("calloc", nativeCallocType);
        LLVMValueRef nativeFree = _module.AddFunction("free", nativeFreeType);

        LLVMTypeRef mallocType = LLVMTypeRef.CreateFunction(pointerType, [sizeType], false);
        LLVMTypeRef alignedMallocType = LLVMTypeRef.CreateFunction(pointerType, [sizeType, sizeType], false);
        LLVMTypeRef callocType = LLVMTypeRef.CreateFunction(pointerType, [sizeType, sizeType], false);
        LLVMTypeRef freeType = LLVMTypeRef.CreateFunction(_context.VoidType, [pointerType], false);
        LLVMValueRef malloc = _module.AddFunction(RuntimeAbiNames.Malloc, mallocType);
        LLVMValueRef alignedMalloc = _module.AddFunction(RuntimeAbiNames.AlignedMalloc, alignedMallocType);
        LLVMValueRef calloc = _module.AddFunction(RuntimeAbiNames.Calloc, callocType);
        LLVMValueRef free = _module.AddFunction(RuntimeAbiNames.Free, freeType);
        malloc.Linkage = LLVMLinkage.LLVMInternalLinkage;
        alignedMalloc.Linkage = LLVMLinkage.LLVMInternalLinkage;
        calloc.Linkage = LLVMLinkage.LLVMInternalLinkage;
        free.Linkage = LLVMLinkage.LLVMInternalLinkage;

        ApplyAllocatorAttributes(nativeMalloc, "malloc"u8, allockind: 1 | 8, allocSize: uint.MaxValue);
        ApplyAllocatorAttributes(nativeCalloc, "malloc"u8, allockind: 1 | 16, allocSize: 1);
        ApplyDeallocatorAttributes(nativeFree, "malloc"u8);
        ApplyAllocatorAttributes(malloc, "xenon"u8, allockind: 1 | 8, allocSize: uint.MaxValue);
        ApplyAllocatorAttributes(alignedMalloc, allockind: 1 | 8 | 32,
            allocSize: uint.MaxValue, alignmentParameterIndex: 2, family: "xenon"u8);
        ApplyAllocatorAttributes(calloc, "xenon"u8, allockind: 1 | 16, allocSize: 1);
        ApplyDeallocatorAttributes(free, "xenon"u8);

        EmitMemoryRuntimeBodies(pointerType, sizeType,
            nativeMalloc, nativeMallocType, nativeFree, nativeFreeType,
            malloc, mallocType, alignedMalloc, alignedMallocType,
            calloc, callocType, free);
        _memoryRuntime = new LlvmMemoryRuntime(
            malloc,
            mallocType,
            alignedMalloc,
            alignedMallocType,
            calloc,
            callocType,
            free,
            freeType,
            sizeType,
            _module.AddFunction("llvm.stacksave.p0", LLVMTypeRef.CreateFunction(pointerType, [], false)),
            _module.AddFunction("llvm.stackrestore.p0", freeType));
        return _memoryRuntime;
    }

    private static string EmitAssembly(LLVMModuleRef module, NativeTargetMachine targetMachine)
    {
        string path = Path.Combine(Path.GetTempPath(), $"xenon-{Guid.NewGuid():N}.s");
        try
        {
            targetMachine.Handle.EmitToFile(module, path, LLVMCodeGenFileType.LLVMAssemblyFile);
            return File.ReadAllText(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private unsafe void ApplyProvenFunctionAttributes(
        FunctionSymbol symbol,
        LLVMValueRef function,
        bool includeMemoryEffects = true)
    {
        if (!_functionEffects.TryGetValue(symbol, out LlvmFunctionEffects effects)) return;
        if (!effects.MayThrow)
            AddFunctionEnumAttribute(function, "nounwind"u8);
        if (includeMemoryEffects && !effects.MayAccessMemory)
            AddFunctionEnumAttribute(function, "memory"u8, 0);
        if (effects.InlineCandidate)
            AddFunctionEnumAttribute(function, "inlinehint"u8);
    }

    private unsafe void AddFunctionEnumAttribute(
        LLVMValueRef function,
        ReadOnlySpan<byte> name,
        ulong value = 0)
        => AddEnumAttribute(function, LLVMAttributeIndex.LLVMAttributeFunctionIndex, name, value);

    private unsafe void AddEnumAttribute(
        LLVMValueRef function,
        LLVMAttributeIndex index,
        ReadOnlySpan<byte> name,
        ulong value = 0)
    {
        fixed (byte* pointer = name)
        {
            uint kind = LLVMApi.GetEnumAttributeKindForName((sbyte*)pointer, (UIntPtr)name.Length);
            if (kind == 0)
                throw new LlvmCodeGenerationException($"LLVM does not recognize function attribute '{System.Text.Encoding.UTF8.GetString(name)}'.");
            function.AddAttributeAtIndex(index, _context.CreateEnumAttribute(kind, value));
        }
    }

    private void ApplyAllocatorAttributes(
        LLVMValueRef function,
        ReadOnlySpan<byte> family,
        ulong allockind,
        ulong allocSize,
        uint? alignmentParameterIndex = null)
    {
        AddFunctionEnumAttribute(function, "nounwind"u8);
        AddFunctionEnumAttribute(function, "allockind"u8, allockind);
        AddFunctionEnumAttribute(function, "allocsize"u8, allocSize);
        AddFunctionStringAttribute(function, "alloc-family"u8, family);
        AddEnumAttribute(function, LLVMAttributeIndex.LLVMAttributeReturnIndex, "noalias"u8);
        if (alignmentParameterIndex is uint alignmentIndex)
            AddEnumAttribute(function, (LLVMAttributeIndex)alignmentIndex, "allocalign"u8);
    }

    private void ApplyDeallocatorAttributes(LLVMValueRef function, ReadOnlySpan<byte> family)
    {
        AddFunctionEnumAttribute(function, "nounwind"u8);
        AddFunctionEnumAttribute(function, "allockind"u8, 4);
        AddFunctionStringAttribute(function, "alloc-family"u8, family);
        AddEnumAttribute(function, (LLVMAttributeIndex)1, "allocptr"u8);
    }

    private unsafe void AddFunctionStringAttribute(
        LLVMValueRef function,
        ReadOnlySpan<byte> name,
        ReadOnlySpan<byte> value)
    {
        fixed (byte* namePointer = name)
        fixed (byte* valuePointer = value)
        {
            var attribute = new LLVMAttributeRef((nint)LLVMApi.CreateStringAttribute(
                _context,
                (sbyte*)namePointer,
                checked((uint)name.Length),
                (sbyte*)valuePointer,
                checked((uint)value.Length)));
            function.AddAttributeAtIndex(LLVMAttributeIndex.LLVMAttributeFunctionIndex, attribute);
        }
    }

    private unsafe void EmitMemoryRuntimeBodies(
        LLVMTypeRef pointerType,
        LLVMTypeRef sizeType,
        LLVMValueRef nativeMalloc,
        LLVMTypeRef nativeMallocType,
        LLVMValueRef nativeFree,
        LLVMTypeRef nativeFreeType,
        LLVMValueRef malloc,
        LLVMTypeRef mallocType,
        LLVMValueRef alignedMalloc,
        LLVMTypeRef alignedMallocType,
        LLVMValueRef calloc,
        LLVMTypeRef callocType,
        LLVMValueRef free)
    {
        ulong pointerBytes = checked((ulong)GetPointerBitWidth() / 8);
        ulong defaultAlignment = Math.Max(pointerBytes, pointerBytes * 2);
        ulong maximum = GetPointerBitWidth() == 32 ? uint.MaxValue : ulong.MaxValue;
        LLVMValueRef Size(ulong value) => LLVMValueRef.CreateConstInt(sizeType, value, false);
        LLVMValueRef Null() => LLVMValueRef.CreateConstPointerNull(pointerType);

        using (LLVMBuilderRef builder = _context.CreateBuilder())
        {
            LLVMBasicBlockRef entry = alignedMalloc.AppendBasicBlock("entry");
            LLVMBasicBlockRef allocate = alignedMalloc.AppendBasicBlock("allocate");
            LLVMBasicBlockRef success = alignedMalloc.AppendBasicBlock("success");
            LLVMBasicBlockRef failure = alignedMalloc.AppendBasicBlock("failure");
            builder.PositionAtEnd(entry);
            LLVMValueRef size = alignedMalloc.GetParam(0);
            LLVMValueRef alignment = alignedMalloc.GetParam(1);
            LLVMValueRef oneLess = builder.BuildSub(alignment, Size(1), "alignment.mask");
            LLVMValueRef nonzero = builder.BuildICmp(
                LLVMIntPredicate.LLVMIntNE, alignment, Size(0), "alignment.nonzero");
            LLVMValueRef powerOfTwo = builder.BuildICmp(
                LLVMIntPredicate.LLVMIntEQ,
                builder.BuildAnd(alignment, oneLess, "alignment.power.bits"),
                Size(0), "alignment.power");
            LLVMValueRef overheadFits = builder.BuildICmp(
                LLVMIntPredicate.LLVMIntULE, alignment, Size(maximum - pointerBytes + 1),
                "alignment.overhead.fits");
            LLVMValueRef overhead = builder.BuildAdd(oneLess, Size(pointerBytes), "allocation.overhead");
            LLVMValueRef sizeFits = builder.BuildICmp(
                LLVMIntPredicate.LLVMIntULE, size,
                builder.BuildSub(Size(maximum), overhead, "allocation.maximum"),
                "allocation.size.fits");
            LLVMValueRef valid = builder.BuildAnd(nonzero, powerOfTwo, "alignment.valid");
            valid = builder.BuildAnd(valid, overheadFits, "alignment.range.valid");
            valid = builder.BuildAnd(valid, sizeFits, "allocation.valid");
            builder.BuildCondBr(valid, allocate, failure);

            builder.PositionAtEnd(allocate);
            LLVMValueRef allocation = builder.BuildCall2(nativeMallocType, nativeMalloc,
                new[] { builder.BuildAdd(size, overhead, "allocation.bytes") }, "allocation.base");
            builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE,
                allocation, Null(), "allocation.succeeded"), success, failure);

            builder.PositionAtEnd(success);
            LLVMValueRef candidate = builder.BuildAdd(
                builder.BuildPtrToInt(allocation, sizeType, "allocation.base.integer"),
                Size(pointerBytes), "allocation.candidate");
            LLVMValueRef alignedInteger = builder.BuildAnd(
                builder.BuildAdd(candidate, oneLess, "allocation.rounded"),
                builder.BuildNot(oneLess, "alignment.inverse.mask"), "allocation.aligned.integer");
            LLVMValueRef aligned = builder.BuildIntToPtr(alignedInteger, pointerType, "allocation.aligned");
            LLVMValueRef header = builder.BuildIntToPtr(
                builder.BuildSub(alignedInteger, Size(pointerBytes), "allocation.header.integer"),
                pointerType, "allocation.header");
            builder.BuildStore(allocation, header);
            builder.BuildRet(aligned);

            builder.PositionAtEnd(failure);
            builder.BuildRet(Null());
        }

        using (LLVMBuilderRef builder = _context.CreateBuilder())
        {
            builder.PositionAtEnd(malloc.AppendBasicBlock("entry"));
            builder.BuildRet(builder.BuildCall2(alignedMallocType, alignedMalloc,
                new[] { malloc.GetParam(0), Size(defaultAlignment) }, "allocation"));
        }

        using (LLVMBuilderRef builder = _context.CreateBuilder())
        {
            LLVMBasicBlockRef entry = calloc.AppendBasicBlock("entry");
            LLVMBasicBlockRef allocate = calloc.AppendBasicBlock("allocate");
            LLVMBasicBlockRef initialize = calloc.AppendBasicBlock("initialize");
            LLVMBasicBlockRef failure = calloc.AppendBasicBlock("failure");
            builder.PositionAtEnd(entry);
            LLVMValueRef count = calloc.GetParam(0);
            LLVMValueRef elementSize = calloc.GetParam(1);
            LLVMValueRef divisor = builder.BuildSelect(
                builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, elementSize, Size(0), "calloc.size.zero"),
                Size(1), elementSize, "calloc.divisor");
            LLVMValueRef valid = builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, count,
                builder.BuildUDiv(Size(maximum), divisor, "calloc.maximum.count"), "calloc.product.valid");
            builder.BuildCondBr(valid, allocate, failure);

            builder.PositionAtEnd(allocate);
            LLVMValueRef bytes = builder.BuildMul(count, elementSize, "calloc.bytes");
            LLVMValueRef allocation = builder.BuildCall2(alignedMallocType, alignedMalloc,
                new[] { bytes, Size(defaultAlignment) }, "calloc.allocation");
            builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE,
                allocation, Null(), "calloc.succeeded"), initialize, failure);

            builder.PositionAtEnd(initialize);
            LLVMApi.BuildMemSet(builder, allocation,
                LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false), bytes, 1);
            builder.BuildRet(allocation);

            builder.PositionAtEnd(failure);
            builder.BuildRet(Null());
        }

        using (LLVMBuilderRef builder = _context.CreateBuilder())
        {
            LLVMBasicBlockRef entry = free.AppendBasicBlock("entry");
            LLVMBasicBlockRef release = free.AppendBasicBlock("release");
            LLVMBasicBlockRef done = free.AppendBasicBlock("done");
            builder.PositionAtEnd(entry);
            LLVMValueRef address = free.GetParam(0);
            builder.BuildCondBr(builder.BuildICmp(LLVMIntPredicate.LLVMIntNE,
                address, Null(), "free.nonnull"), release, done);
            builder.PositionAtEnd(release);
            LLVMValueRef addressInteger = builder.BuildPtrToInt(address, sizeType, "free.address.integer");
            LLVMValueRef header = builder.BuildIntToPtr(
                builder.BuildSub(addressInteger, Size(pointerBytes), "free.header.integer"),
                pointerType, "free.header");
            LLVMValueRef allocation = builder.BuildLoad2(pointerType, header, "free.allocation");
            builder.BuildCall2(nativeFreeType, nativeFree, new[] { allocation }, string.Empty);
            builder.BuildBr(done);
            builder.PositionAtEnd(done);
            builder.BuildRetVoid();
        }
    }

    private LLVMValueRef GetOrDeclareTrap()
    {
        LLVMValueRef trap = _module.GetNamedFunction("llvm.trap");
        return trap.Handle != IntPtr.Zero ? trap
            : _module.AddFunction("llvm.trap", LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
    }

    private LlvmFunction GetOrDeclareStringCompare()
    {
        const string name = "strcmp";
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        LLVMTypeRef type = LLVMTypeRef.CreateFunction(_context.Int32Type, [pointer, pointer], false);
        if (_nativeFunctions.TryGetValue(name, out LlvmFunction existing))
        {
            if (existing.Type != type)
                throw new LlvmCodeGenerationException("Native symbol 'strcmp' has an incompatible declaration.");
            return existing;
        }
        var function = new LlvmFunction(_module.AddFunction(name, type), type);
        _nativeFunctions.Add(name, function);
        return function;
    }

    private int GetPointerBitWidth() => _targetMachine?.PointerBitWidth
        ?? throw new LlvmCodeGenerationException(
            "Target-dependent integer types require a configured LLVM target machine.");

    private bool IsWindowsTarget() => _targetMachine?.Triple.Contains(
        "windows",
        StringComparison.OrdinalIgnoreCase) is true ||
        _targetMachine?.Triple.Contains("win32", StringComparison.OrdinalIgnoreCase) is true;

    private bool IsMacOsTarget() => _targetMachine is null
        ? OperatingSystem.IsMacOS()
        : _targetMachine.Triple.Contains("darwin", StringComparison.OrdinalIgnoreCase) ||
          _targetMachine.Triple.Contains("macos", StringComparison.OrdinalIgnoreCase);

    private LLVMTypeRef GetPthreadKeyType() =>
        IsMacOsTarget() ? _context.Int64Type : _context.Int32Type;

    private readonly record struct LlvmFunction(LLVMValueRef Value, LLVMTypeRef Type);
    private readonly record struct LlvmCAbiFunction(LLVMValueRef Value, LlvmCAbiFunctionPlan Plan);
    private readonly record struct LlvmVTable(LLVMValueRef Value, LLVMTypeRef Type);

    private sealed record LlvmMemoryRuntime(
        LLVMValueRef Malloc,
        LLVMTypeRef MallocType,
        LLVMValueRef AlignedMalloc,
        LLVMTypeRef AlignedMallocType,
        LLVMValueRef Calloc,
        LLVMTypeRef CallocType,
        LLVMValueRef Free,
        LLVMTypeRef FreeType,
        LLVMTypeRef SizeType,
        LLVMValueRef StackSave,
        LLVMValueRef StackRestore);

    private LlvmExceptionRuntime GetOrDeclareExceptionRuntime()
    {
        if (_exceptionRuntime is not null) return _exceptionRuntime;
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        LLVMTypeRef size = MapType(BuiltinTypes.NUInt);
        LlvmFunction Native(string name, LLVMTypeRef result, params LLVMTypeRef[] parameters)
        {
            LLVMTypeRef type = LLVMTypeRef.CreateFunction(result, parameters, false);
            return new LlvmFunction(_module.AddFunction(name, type), type);
        }
        _exceptionRuntime = new LlvmExceptionRuntime(
            Native(RuntimeAbiNames.EhAllocate, pointer, size, size, pointer, pointer, pointer),
            Native(RuntimeAbiNames.EhObject, pointer, pointer),
            Native(RuntimeAbiNames.EhActivate, _context.VoidType, pointer),
            Native(RuntimeAbiNames.EhThrow, _context.VoidType, pointer),
            Native(RuntimeAbiNames.EhCurrent, pointer),
            Native(RuntimeAbiNames.EhMatches, _context.Int1Type, pointer, pointer),
            Native(RuntimeAbiNames.EhHandle, _context.VoidType, pointer),
            Native(RuntimeAbiNames.EhAbandon, _context.VoidType, pointer),
            Native(RuntimeAbiNames.EhReplacePrevious, _context.VoidType),
            Native(RuntimeAbiNames.EhCleanup, _context.VoidType, pointer, pointer),
            Native(RuntimeAbiNames.EhInitialize, _context.VoidType, pointer, pointer),
            Native(RuntimeAbiNames.EhRethrow, _context.VoidType),
            Native(RuntimeAbiNames.EhTerminate, _context.VoidType));
        return _exceptionRuntime;
    }

    private LLVMValueRef GetOrDeclareExceptionTypeKey(TypeSymbol type)
    {
        string identity = GetExceptionTypeRuntimeIdentity(type);
        if (_exceptionTypeKeys.TryGetValue(identity, out LLVMValueRef existing)) return existing;
        var identities = new List<string> { identity };
        if (type is StructTypeSymbol structure)
            for (StructTypeSymbol? baseType = structure.BaseType; baseType is not null; baseType = baseType.BaseType)
                identities.Add(GetExceptionTypeRuntimeIdentity(baseType));
        string chain = string.Join('\n', identities) + "\n";
        byte[] bytes = Encoding.UTF8.GetBytes(chain + '\0');
        LLVMTypeRef dataType = LLVMTypeRef.CreateArray(_context.Int8Type, (uint)bytes.Length);
        LLVMValueRef data = _module.AddGlobal(dataType,
            MangleManagedName(_moduleIdentity, "exception_type", identity));
        data.Linkage = LLVMLinkage.LLVMInternalLinkage;
        data.IsGlobalConstant = true;
        data.Initializer = LLVMValueRef.CreateConstArray(_context.Int8Type,
            bytes.Select(value => LLVMValueRef.CreateConstInt(_context.Int8Type, value, false)).ToArray());
        _exceptionTypeKeys.Add(identity, data);
        return data;
    }

    private string GetExceptionTypeRuntimeIdentity(TypeSymbol type) => type switch
    {
        PrimitiveTypeSymbol => $"builtin:{type.Name}",
        EnumTypeSymbol enumeration => $"{GetAbiIdentity(enumeration)}:{enumeration.FullName}",
        StructTypeSymbol structure => StructIdentity(structure),
        UniqueTypeSymbol unique => $"unique<{GetExceptionTypeRuntimeIdentity(unique.ElementType)}>",
        SharedTypeSymbol shared => $"shared<{GetExceptionTypeRuntimeIdentity(shared.ElementType)}>",
        WeakTypeSymbol weak => $"weak<{GetExceptionTypeRuntimeIdentity(weak.ElementType)}>",
        _ => type.ToDisplayString(TypeDisplayFormat.FullyQualified),
    };

    private string StructIdentity(StructTypeSymbol type)
    {
        StructTypeSymbol origin = type.GenericDefinition ?? type;
        string nominal = $"{GetAbiIdentity(origin)}:{origin.FullName}";
        return type.IsGenericSpecialization
            ? $"{nominal}<{string.Join(",", type.TypeArguments.Select(GetExceptionTypeRuntimeIdentity))}>"
            : nominal;
    }

    private static string GetExceptionTypeDisplayName(TypeSymbol type) =>
        type.ToDisplayString(TypeDisplayFormat.FullyQualified);

    private sealed record LlvmExceptionRuntime(
        LlvmFunction Allocate,
        LlvmFunction Object,
        LlvmFunction Activate,
        LlvmFunction Throw,
        LlvmFunction Current,
        LlvmFunction Matches,
        LlvmFunction Handle,
        LlvmFunction Abandon,
        LlvmFunction ReplacePrevious,
        LlvmFunction Cleanup,
        LlvmFunction Initialize,
        LlvmFunction Rethrow,
        LlvmFunction Terminate);

    private sealed unsafe partial class FunctionEmitter
    {
        private readonly LLVMContextRef _context;
        private readonly LLVMBuilderRef _builder;
        private readonly LlvmAtomicOperations _atomics;
        private readonly FunctionSymbol _function;
        private readonly LLVMValueRef _llvmFunction;
        private readonly Dictionary<FunctionSymbol, LlvmFunction> _functions;
        private readonly IReadOnlyDictionary<FunctionSymbol, LlvmFunctionEffects> _functionEffects;
        private readonly Dictionary<FunctionSymbol, LlvmCAbiFunction> _cAbiFunctions;
        private readonly LlvmCAbi? _cAbi;
        private readonly Dictionary<FieldSymbol, LLVMValueRef> _staticFields;
        private readonly Dictionary<FieldSymbol, LlvmFunction> _threadLocalEnsures;
        private readonly Dictionary<StructTypeSymbol, LlvmVTable> _virtualTables;
        private readonly Dictionary<StructTypeSymbol, LlvmVTable> _interfaceMaps;
        private readonly Dictionary<InterfaceTypeSymbol, LLVMValueRef> _interfaceKeys;
        private readonly Dictionary<FunctionSymbol, LlvmFunction> _closureEnvironmentDestructors;
        private readonly Func<FunctionSymbol, LLVMValueRef> _getFunctionValueInvokeAddress;
        private readonly LLVMTypeRef _interfaceMapEntryType;
        private readonly Func<TypeSymbol, LLVMTypeRef> _mapType;
        private readonly Func<LlvmMemoryRuntime> _getMemoryRuntime;
        private readonly Func<LlvmExceptionRuntime> _getExceptionRuntime;
        private readonly Func<TypeSymbol, LLVMValueRef> _getExceptionTypeKey;
        private readonly Func<TypeSymbol, string> _getExceptionTypeDisplayName;
        private readonly Func<LLVMValueRef> _getTrap;
        private readonly Func<LlvmFunction> _getStringCompare;
        private readonly Func<TypeSymbol, ulong> _getAbiSize;
        private readonly Func<TypeSymbol, uint> _getAbiAlignment;
        private readonly Func<StructTypeSymbol, FieldSymbol, ulong> _getFieldOffset;
        private readonly Func<TypeSymbol, int> _getIntegerBitWidth;
        private readonly bool _enableRuntimeChecks;
        private readonly bool _isWindowsTarget;
        private readonly bool _exceptionsEnabled;
        private readonly LLVMValueRef _thisValue;
        private readonly LLVMValueRef _closureEnvironment;
        private readonly Dictionary<VariableSymbol, LLVMValueRef> _addresses = [];
        public FunctionEmitter(
            LLVMContextRef context,
            LLVMBuilderRef builder,
            FunctionSymbol function,
            LLVMValueRef llvmFunction,
            Dictionary<FunctionSymbol, LlvmFunction> functions,
            IReadOnlyDictionary<FunctionSymbol, LlvmFunctionEffects> functionEffects,
            Dictionary<FunctionSymbol, LlvmCAbiFunction> cAbiFunctions,
            LlvmCAbi? cAbi,
            Dictionary<FieldSymbol, LLVMValueRef> staticFields,
            Dictionary<FieldSymbol, LlvmFunction> threadLocalEnsures,
            Dictionary<StructTypeSymbol, LlvmVTable> virtualTables,
            Dictionary<StructTypeSymbol, LlvmVTable> interfaceMaps,
            Dictionary<InterfaceTypeSymbol, LLVMValueRef> interfaceKeys,
            Dictionary<FunctionSymbol, LlvmFunction> closureEnvironmentDestructors,
            Func<FunctionSymbol, LLVMValueRef> getFunctionValueInvokeAddress,
            LLVMTypeRef interfaceMapEntryType,
            Func<TypeSymbol, LLVMTypeRef> mapType,
            Func<LlvmMemoryRuntime> getMemoryRuntime,
            Func<LlvmExceptionRuntime> getExceptionRuntime,
            Func<TypeSymbol, LLVMValueRef> getExceptionTypeKey,
            Func<TypeSymbol, string> getExceptionTypeDisplayName,
            Func<LLVMValueRef> getTrap,
            Func<LlvmFunction> getStringCompare,
            Func<TypeSymbol, ulong> getAbiSize,
            Func<TypeSymbol, uint> getAbiAlignment,
            Func<StructTypeSymbol, FieldSymbol, ulong> getFieldOffset,
            Func<TypeSymbol, int> getIntegerBitWidth,
            bool enableRuntimeChecks,
            bool isWindowsTarget,
            bool exceptionsEnabled,
            bool resumable = false)
        {
            _context = context;
            _builder = builder;
            _atomics = new LlvmAtomicOperations(builder);
            _function = function;
            _llvmFunction = llvmFunction;
            _functions = functions;
            _functionEffects = functionEffects;
            _cAbiFunctions = cAbiFunctions;
            _cAbi = cAbi;
            _staticFields = staticFields;
            _threadLocalEnsures = threadLocalEnsures;
            _virtualTables = virtualTables;
            _interfaceMaps = interfaceMaps;
            _interfaceKeys = interfaceKeys;
            _closureEnvironmentDestructors = closureEnvironmentDestructors;
            _getFunctionValueInvokeAddress = getFunctionValueInvokeAddress;
            _interfaceMapEntryType = interfaceMapEntryType;
            _mapType = mapType;
            _getMemoryRuntime = getMemoryRuntime;
            _getExceptionRuntime = getExceptionRuntime;
            _getExceptionTypeKey = getExceptionTypeKey;
            _getExceptionTypeDisplayName = getExceptionTypeDisplayName;
            _getTrap = getTrap;
            _getStringCompare = getStringCompare;
            _getAbiSize = getAbiSize;
            _getAbiAlignment = getAbiAlignment;
            _getFieldOffset = getFieldOffset;
            _getIntegerBitWidth = getIntegerBitWidth;
            _enableRuntimeChecks = enableRuntimeChecks;
            _isWindowsTarget = isWindowsTarget;
            _exceptionsEnabled = exceptionsEnabled;
            _thisValue = function.HasImplicitThis ? llvmFunction.GetParam(0) : default;
            _closureEnvironment = function.ClosureEnvironmentOwner is not null ? llvmFunction.GetParam(0) : function.IsCapturingLambda
                ? llvmFunction.GetParam(function.HasImplicitThis ? 1u : 0u)
                : default;

            _isResumable = resumable;

            uint parameterOffset = (function.HasImplicitThis ? 1u : 0u) +
                (function.IsCapturingLambda ? 1u : 0u);
            for (int index = 0; index < function.Parameters.Length; index++)
            {
                ParameterSymbol parameter = function.Parameters[index];
                LLVMValueRef address = _builder.BuildAlloca(_mapType(parameter.Type), parameter.Name);
                _builder.BuildStore(llvmFunction.GetParam(parameterOffset + (uint)index), address);
                _addresses.Add(parameter, address);
            }
        }

        private LLVMValueRef EmitPotentiallyThrowingCall(
            FunctionSymbol symbol,
            LlvmFunction function,
            LLVMValueRef[] arguments,
            string name)
        {
            if (_functionEffects.TryGetValue(symbol, out LlvmFunctionEffects effects) && !effects.MayThrow)
                return _builder.BuildCall2(function.Type, function.Value, arguments, name);
            return EmitPotentiallyThrowingCall(function.Type, function.Value, arguments, name);
        }

        private LLVMValueRef EmitPotentiallyThrowingCall(
            LLVMTypeRef functionType,
            LLVMValueRef function,
            LLVMValueRef[] arguments,
            string name) => MirInvoke(functionType, function, arguments, name);

        private void EmitNativeCatch(LLVMBasicBlockRef unwind, Action continuationBody)
        {
            LLVMBasicBlockRef continuation = _llvmFunction.AppendBasicBlock("exception.continue");
            _builder.PositionAtEnd(unwind);
            if (_isWindowsTarget)
            {
                LLVMValueRef personality = GetOrDeclarePersonality("__CxxFrameHandler3");
                _llvmFunction.PersonalityFn = personality;
                LLVMTypeRef tokenType = new((nint)LLVMApi.TokenTypeInContext(_context));
                LLVMValueRef parentPad = LLVMValueRef.CreateConstNull(tokenType);
                sbyte* emptyName = stackalloc sbyte[1];
                emptyName[0] = 0;
                LLVMValueRef catchSwitch = new((nint)LLVMApi.BuildCatchSwitch(
                    _builder, parentPad, default, 1, emptyName));
                LLVMBasicBlockRef handler = _llvmFunction.AppendBasicBlock("exception.catchpad");
                LLVMApi.AddHandler(catchSwitch, handler);
                _builder.PositionAtEnd(handler);
                LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                LLVMValueRef[] arguments =
                {
                    LLVMValueRef.CreateConstPointerNull(pointer),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 64),
                    LLVMValueRef.CreateConstPointerNull(pointer),
                };
                fixed (LLVMValueRef* argumentPointer = arguments)
                {
                    LLVMValueRef catchPad = new((nint)LLVMApi.BuildCatchPad(
                        _builder, catchSwitch, (LLVMOpaqueValue**)argumentPointer,
                        (uint)arguments.Length, emptyName));
                    LLVMApi.BuildCatchRet(_builder, catchPad, continuation);
                }
            }
            else
            {
                LLVMValueRef personality = GetOrDeclarePersonality("__gxx_personality_v0");
                _llvmFunction.PersonalityFn = personality;
                LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                LLVMTypeRef landingType = _context.GetStructType([pointer, _context.Int32Type], false);
                LLVMValueRef landing = _builder.BuildLandingPad(landingType, personality, 1,
                    "exception.landingpad");
                landing.AddClause(LLVMValueRef.CreateConstPointerNull(pointer));
                LLVMValueRef nativeException = _builder.BuildExtractValue(landing, 0, "native.exception");
                LLVMTypeRef beginType = LLVMTypeRef.CreateFunction(pointer, [pointer], false);
                LLVMValueRef begin = GetOrAddFunction("__cxa_begin_catch", beginType);
                LLVMTypeRef endType = LLVMTypeRef.CreateFunction(_context.VoidType, [], false);
                LLVMValueRef end = GetOrAddFunction("__cxa_end_catch", endType);
                _builder.BuildCall2(beginType, begin, new[] { nativeException }, string.Empty);
                _builder.BuildCall2(endType, end, Array.Empty<LLVMValueRef>(), string.Empty);
                _builder.BuildBr(continuation);
            }
            _builder.PositionAtEnd(continuation);
            continuationBody();
        }

        private LLVMValueRef GetOrDeclarePersonality(string name)
        {
            LLVMModuleRef module = _llvmFunction.GlobalParent;
            LLVMValueRef existing = module.GetNamedFunction(name);
            return existing.Handle != IntPtr.Zero
                ? existing
                : module.AddFunction(name, LLVMTypeRef.CreateFunction(_context.Int32Type, [], true));
        }

        private LLVMValueRef GetOrAddFunction(string name, LLVMTypeRef type)
        {
            LLVMModuleRef module = _llvmFunction.GlobalParent;
            LLVMValueRef existing = module.GetNamedFunction(name);
            return existing.Handle != IntPtr.Zero ? existing : module.AddFunction(name, type);
        }

        private LLVMValueRef EmitLoad(TypeSymbol type, LLVMValueRef address, string name)
        {
            if (type is not AtomicTypeSymbol atomic)
                return _builder.BuildLoad2(_mapType(type), address, name);
            if (IsLockBackedAtomic(atomic))
            {
                AcquireAtomicLock(address, atomic, name);
                LLVMValueRef composite = _builder.BuildLoad2(
                    _mapType(atomic.ElementType), AtomicValueAddress(address, atomic), $"{name}.value");
                composite = EmitCopyValue(composite, atomic.ElementType);
                ReleaseAtomicLock(address, atomic);
                return composite;
            }
            LLVMValueRef value = _atomics.Load(
                _mapType(atomic),
                address,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                name);
            return TypeIdentity.AreSame(atomic.ElementType, BuiltinTypes.Bool)
                ? _builder.BuildTrunc(value, _mapType(BuiltinTypes.Bool), $"{name}.bool")
                : value;
        }

        private void EmitAtomicStore(
            LLVMValueRef value,
            LLVMValueRef address,
            AtomicTypeSymbol atomic,
            bool initialize = false)
        {
            if (IsLockBackedAtomic(atomic))
            {
                if (initialize)
                    _builder.BuildStore(
                        LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false),
                        AtomicLockAddress(address, atomic));
                else
                    AcquireAtomicLock(address, atomic, "atomic.store");
                _builder.BuildStore(value, AtomicValueAddress(address, atomic));
                if (!initialize) ReleaseAtomicLock(address, atomic);
                return;
            }
            if (TypeIdentity.AreSame(atomic.ElementType, BuiltinTypes.Bool))
                value = _builder.BuildZExt(value, _mapType(atomic), "atomic.bool.storage");
            _atomics.Store(
                value,
                address,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent);
        }

        private LLVMValueRef EmitAtomicExchange(
            LLVMValueRef value,
            LLVMValueRef address,
            AtomicTypeSymbol atomic)
        {
            if (IsLockBackedAtomic(atomic))
            {
                AcquireAtomicLock(address, atomic, "atomic.exchange");
                LLVMValueRef valueAddress = AtomicValueAddress(address, atomic);
                LLVMValueRef compositePrevious = _builder.BuildLoad2(
                    _mapType(atomic.ElementType), valueAddress, "atomic.exchange.previous");
                _builder.BuildStore(value, valueAddress);
                ReleaseAtomicLock(address, atomic);
                return compositePrevious;
            }
            bool isBool = TypeIdentity.AreSame(atomic.ElementType, BuiltinTypes.Bool);
            if (isBool)
                value = _builder.BuildZExt(value, _mapType(atomic), "atomic.exchange.bool.storage");
            LLVMValueRef previous = _atomics.Exchange(
                address,
                value,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                "atomic.exchange.previous");
            return isBool
                ? _builder.BuildTrunc(previous, _mapType(BuiltinTypes.Bool), "atomic.exchange.bool")
                : previous;
        }

        private LLVMValueRef EmitFloatingCompareExchange(
            LLVMValueRef address,
            TypeSymbol floatingType,
            LLVMValueRef expected,
            LLVMValueRef desired)
        {
            LLVMTypeRef storageType = TypeIdentity.AreSame(floatingType, BuiltinTypes.Float)
                ? _context.Int32Type
                : _context.Int64Type;
            LLVMValueRef desiredBits = _builder.BuildBitCast(
                desired, storageType, "cmpxchg.desired.bits");
            LLVMBasicBlockRef entry = _builder.InsertBlock;
            LLVMValueRef initial = _atomics.Load(
                storageType,
                address,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                "cmpxchg.float.initial");
            LLVMBasicBlockRef inspect = _llvmFunction.AppendBasicBlock("cmpxchg.float.inspect");
            LLVMBasicBlockRef attempt = _llvmFunction.AppendBasicBlock("cmpxchg.float.attempt");
            LLVMBasicBlockRef mismatch = _llvmFunction.AppendBasicBlock("cmpxchg.float.mismatch");
            LLVMBasicBlockRef success = _llvmFunction.AppendBasicBlock("cmpxchg.float.success");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("cmpxchg.float.end");
            _builder.BuildBr(inspect);

            _builder.PositionAtEnd(inspect);
            LLVMValueRef currentBits = _builder.BuildPhi(storageType, "cmpxchg.float.current.bits");
            LLVMValueRef current = _builder.BuildBitCast(
                currentBits, _mapType(floatingType), "cmpxchg.float.current");
            LLVMValueRef equal = EmitValueEquality(current, expected, floatingType);
            _builder.BuildCondBr(equal, attempt, mismatch);

            _builder.PositionAtEnd(attempt);
            LlvmCompareExchangeResult exchange = _atomics.CompareExchange(
                address,
                currentBits,
                desiredBits,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                LLVMAtomicOrdering.LLVMAtomicOrderingSequentiallyConsistent,
                "cmpxchg.float.exchange");
            _builder.BuildCondBr(exchange.Succeeded, success, inspect);
            currentBits.AddIncoming([initial, exchange.Observed], [entry, attempt], 2);

            _builder.PositionAtEnd(mismatch);
            _builder.BuildBr(end);
            _builder.PositionAtEnd(success);
            _builder.BuildBr(end);
            _builder.PositionAtEnd(end);
            LLVMValueRef result = _builder.BuildPhi(_context.Int1Type, "cmpxchg.float.succeeded");
            result.AddIncoming(
                [LLVMValueRef.CreateConstInt(_context.Int1Type, 0),
                    LLVMValueRef.CreateConstInt(_context.Int1Type, 1)],
                [mismatch, success],
                2);
            return result;
        }

        private static bool IsLockBackedAtomic(AtomicTypeSymbol atomic) =>
            LlvmAtomicStorage.RequiresLock(atomic.ElementType);

        private LLVMValueRef AtomicLockAddress(LLVMValueRef address, AtomicTypeSymbol atomic) =>
            LlvmAtomicStorage.GetLockAddress(
                _builder, _mapType(atomic), address, "atomic.lock.address");

        private LLVMValueRef AtomicValueAddress(LLVMValueRef address, AtomicTypeSymbol atomic) =>
            LlvmAtomicStorage.GetValueAddress(
                _builder, _mapType(atomic), address, atomic, "atomic.value.address");

        private LLVMValueRef GetValueStorageAddress(LLVMValueRef address, TypeSymbol type) =>
            type is AtomicTypeSymbol atomic ? AtomicValueAddress(address, atomic) : address;

        private void AcquireAtomicLock(LLVMValueRef address, AtomicTypeSymbol atomic, string name)
        {
            LLVMValueRef lockAddress = AtomicLockAddress(address, atomic);
            LLVMBasicBlockRef attempt = _llvmFunction.AppendBasicBlock($"{name}.lock.attempt");
            LLVMBasicBlockRef acquired = _llvmFunction.AppendBasicBlock($"{name}.lock.acquired");
            _builder.BuildBr(attempt);
            _builder.PositionAtEnd(attempt);
            _builder.BuildCondBr(
                _atomics.TryAcquireLock(lockAddress, _context.Int8Type, $"{name}.lock"),
                acquired,
                attempt);
            _builder.PositionAtEnd(acquired);
        }

        private void ReleaseAtomicLock(LLVMValueRef address, AtomicTypeSymbol atomic) =>
            _atomics.ReleaseLock(AtomicLockAddress(address, atomic), _context.Int8Type);

        private LLVMValueRef EmitValueEquality(
            LLVMValueRef left,
            LLVMValueRef right,
            TypeSymbol type)
        {
            if (type is PrimitiveTypeSymbol { IsFloatingPoint: true })
                return _builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right, "value.equal.float");
            if (type is PrimitiveTypeSymbol or EnumTypeSymbol or PointerTypeSymbol or FunctionPointerTypeSymbol or ArrayTypeSymbol or
                SharedTypeSymbol or WeakTypeSymbol)
                return _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right, "value.equal.scalar");
            if (type is not StructTypeSymbol structure)
                throw new LlvmCodeGenerationException(
                    $"value equality is not available for '{type.ToDisplayString()}'");

            LLVMValueRef equal = LLVMValueRef.CreateConstInt(_context.Int1Type, 1, false);
            if (structure.BaseType is { } baseType)
                equal = _builder.BuildAnd(equal, EmitValueEquality(
                    _builder.BuildExtractValue(left, 0, "value.equal.left.base"),
                    _builder.BuildExtractValue(right, 0, "value.equal.right.base"),
                    baseType), "value.equal.base");
            foreach (FieldSymbol field in structure.Fields)
            {
                uint index = checked((uint)field.Ordinal);
                equal = _builder.BuildAnd(equal, EmitValueEquality(
                    _builder.BuildExtractValue(left, index, $"value.equal.left.{field.Name}"),
                    _builder.BuildExtractValue(right, index, $"value.equal.right.{field.Name}"),
                    field.Type), $"value.equal.{field.Name}");
            }
            return equal;
        }

        private LLVMValueRef GetStorageValueAddress(LLVMValueRef storageAddress, StorageTypeSymbol storage) =>
            _builder.BuildStructGEP2(_mapType(storage), storageAddress, 0, "storage.value.address");

        private LLVMValueRef GetStorageStateAddress(LLVMValueRef storageAddress, StorageTypeSymbol storage) =>
            _builder.BuildStructGEP2(_mapType(storage), storageAddress, 1, "storage.state.address");

        private void EmitStorageStateCheck(
            LLVMValueRef storageAddress,
            StorageTypeSymbol storage,
            bool expectedInitialized)
        {
            LLVMValueRef state = _builder.BuildLoad2(_context.Int1Type,
                GetStorageStateAddress(storageAddress, storage), "storage.state");
            LLVMValueRef expected = LLVMValueRef.CreateConstInt(_context.Int1Type,
                expectedInitialized ? 1UL : 0UL);
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, state, expected,
                "storage.state.valid"));
        }

        private LLVMValueRef EmitCopyValue(LLVMValueRef source, TypeSymbol type)
        {
            if (type is SharedTypeSymbol)
                return EmitOwnershipRetain(source, counterIndex: 0, "shared.retain");
            if (type is WeakTypeSymbol)
                return EmitOwnershipRetain(source, counterIndex: 1, "weak.retain");
            if (type is FunctionValueTypeSymbol)
            {
                LLVMValueRef control = _builder.BuildExtractValue(source, 1, "function.copy.control");
                EmitFunctionControlRetain(control);
                return source;
            }
            if (type is not StructTypeSymbol structure) return source;

            LLVMValueRef result = _mapType(structure).Poison;
            if (structure.BaseType is { } baseType)
            {
                LLVMValueRef baseValue = _builder.BuildExtractValue(source, 0, "copy.base");
                result = _builder.BuildInsertValue(result, EmitCopyValue(baseValue, baseType), 0, "copy.base.init");
            }
            if (structure.IntroducesVirtualDispatch)
            {
                uint dispatchIndex = LlvmStructLayout.DispatchIndex(structure);
                result = _builder.BuildInsertValue(result,
                    _builder.BuildExtractValue(source, dispatchIndex, "copy.dispatch"),
                    dispatchIndex,
                    "copy.dispatch.init");
            }
            foreach (FieldSymbol field in structure.Fields)
            {
                uint index = checked((uint)field.Ordinal);
                LLVMValueRef fieldValue = _builder.BuildExtractValue(source, index, $"copy.{field.Name}");
                result = _builder.BuildInsertValue(result, EmitCopyValue(fieldValue, field.Type), index,
                    $"copy.{field.Name}.init");
            }
            return result;
        }

        private LLVMTypeRef OwnershipControlBlockType => _context.GetStructType([
            _mapType(BuiltinTypes.NUInt),
            _mapType(BuiltinTypes.NUInt),
            LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
        ], false);

        private LLVMValueRef OwnershipControlField(LLVMValueRef control, uint index, string name) =>
            _builder.BuildStructGEP2(OwnershipControlBlockType, control, index, name);

        private LLVMTypeRef FunctionControlBlockType
        {
            get
            {
                LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                return _context.GetStructType([_mapType(BuiltinTypes.NUInt), pointer, pointer], false);
            }
        }

        private LLVMValueRef FunctionControlField(LLVMValueRef control, uint index, string name) =>
            _builder.BuildStructGEP2(FunctionControlBlockType, control, index, name);

        private void EmitFunctionControlRetain(LLVMValueRef control)
        {
            LLVMBasicBlockRef retain = _llvmFunction.AppendBasicBlock("function.retain.body");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("function.retain.end");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, control,
                LLVMValueRef.CreateConstPointerNull(control.TypeOf), "function.retain.valid"), retain, end);
            _builder.PositionAtEnd(retain);
            _atomics.FetchAdd(FunctionControlField(control, 0, "function.retain.count.address"),
                SizeConstant(1), LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic, "function.retain");
            _builder.BuildBr(end);
            _builder.PositionAtEnd(end);
        }

        private LLVMTypeRef ClosureEnvironmentType(FunctionSymbol function) =>
            _context.GetStructType([LLVMTypeRef.CreatePointer(_context.Int8Type, 0), .. function.LambdaCaptures.Select(capture => _mapType(capture.StorageType))], false);

        private LLVMValueRef EmitCaptureAddress(CaptureVariableSymbol capture)
        {
            LLVMTypeRef environmentType = ClosureEnvironmentType(_function.ClosureEnvironmentOwner ?? _function);
            LLVMValueRef field = _builder.BuildStructGEP2(environmentType, _closureEnvironment,
                checked((uint)capture.Ordinal + 1), $"{capture.Name}.capture.address");
            return capture.IsBorrow
                ? _builder.BuildLoad2(_mapType(capture.StorageType), field, $"{capture.Name}.borrow")
                : field;
        }

        private LLVMValueRef EmitOwnershipRetain(LLVMValueRef control, uint counterIndex, string name)
        {
            LLVMBasicBlockRef entry = _builder.InsertBlock;
            LLVMBasicBlockRef retain = _llvmFunction.AppendBasicBlock($"{name}.body");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock($"{name}.end");
            LLVMValueRef notNull = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, control,
                LLVMValueRef.CreateConstPointerNull(control.TypeOf), $"{name}.valid");
            _builder.BuildCondBr(notNull, retain, end);
            _builder.PositionAtEnd(retain);
            LLVMValueRef address = OwnershipControlField(control, counterIndex, $"{name}.count.address");
            _atomics.FetchAdd(
                address,
                SizeConstant(1),
                LLVMAtomicOrdering.LLVMAtomicOrderingMonotonic,
                $"{name}.count");
            _builder.BuildBr(end);
            _builder.PositionAtEnd(end);
            return control;
        }

        private LLVMValueRef EmitArithmetic(
            SyntaxKind operatorKind,
            TypeSymbol operandType,
            LLVMValueRef left,
            LLVMValueRef right,
            TypeSymbol? rightType = null)
        {
            rightType ??= operandType;
            if (operandType is PointerTypeSymbol pointer)
            {
                if (rightType is PointerTypeSymbol)
                {
                    LLVMTypeRef nativeInt = _mapType(BuiltinTypes.NInt);
                    LLVMValueRef bytes = _builder.BuildSub(
                        _builder.BuildPtrToInt(left, nativeInt, "pointer.left"),
                        _builder.BuildPtrToInt(right, nativeInt, "pointer.right"), "pointer.bytes");
                    ulong size = _getAbiSize(pointer.ElementType);
                    // Empty objects have no distinguishable element addresses.
                    EmitRuntimeCheck(LLVMValueRef.CreateConstInt(_context.Int1Type, size == 0 ? 0UL : 1UL));
                    return _builder.BuildSDiv(bytes, LLVMValueRef.CreateConstInt(nativeInt, Math.Max(1UL, size)), "pointer.distance");
                }
                LLVMValueRef offset = ConvertIntegerToSize(right, rightType);
                if (operatorKind == SyntaxKind.MinusToken)
                    offset = _builder.BuildNeg(offset, "pointer.offset.neg");
                // Deliberately not inbounds: merely computing an address must not create poison.
                return _builder.BuildGEP2(_mapType(pointer.ElementType), left, new LLVMValueRef[] { offset }, "pointer.offset");
            }
            if (rightType is PointerTypeSymbol)
                return EmitArithmetic(operatorKind, rightType, right, left, operandType);

            if (operandType is PrimitiveTypeSymbol { IsFloatingPoint: true })
            {
                return operatorKind switch
                {
                    SyntaxKind.PlusToken => _builder.BuildFAdd(left, right, "fadd"),
                    SyntaxKind.MinusToken => _builder.BuildFSub(left, right, "fsub"),
                    SyntaxKind.StarToken => _builder.BuildFMul(left, right, "fmul"),
                    SyntaxKind.SlashToken => _builder.BuildFDiv(left, right, "fdiv"),
                    SyntaxKind.PercentToken => _builder.BuildFRem(left, right, "frem"),
                    _ => throw new LlvmCodeGenerationException($"Floating-point operator '{operatorKind}' is not supported."),
                };
            }

            bool signed = operandType is PrimitiveTypeSymbol { IsSigned: true };
            if (operatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken)
            {
                // Validate before truncating; otherwise a large count could become a valid one.
                uint width = left.TypeOf.IntWidth;
                LLVMValueRef valid = _builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, right,
                    LLVMValueRef.CreateConstInt(right.TypeOf, width), "shift.count.valid");
                EmitRuntimeCheck(valid); // Unsigned comparison rejects negative counts as well.
                if (right.TypeOf.IntWidth < width)
                    right = _builder.BuildZExt(right, left.TypeOf, "shift.count");
                else if (right.TypeOf.IntWidth > width)
                    right = _builder.BuildTrunc(right, left.TypeOf, "shift.count");
            }
            if (operatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken)
            {
                LLVMValueRef valid = _builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, right,
                    LLVMValueRef.CreateConstInt(right.TypeOf, 0), "division.nonzero");
                if (signed)
                {
                    LLVMValueRef minimum = LLVMValueRef.CreateConstInt(left.TypeOf, 1UL << ((int)left.TypeOf.IntWidth - 1));
                    LLVMValueRef overflow = _builder.BuildAnd(
                        _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, minimum),
                        _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right, LLVMValueRef.CreateConstInt(right.TypeOf, ulong.MaxValue)),
                        "division.overflow");
                    valid = _builder.BuildAnd(valid, _builder.BuildNot(overflow), "division.valid");
                }
                EmitRuntimeCheck(valid);
            }
            return operatorKind switch
            {
                SyntaxKind.PlusToken => _builder.BuildAdd(left, right, "add"),
                SyntaxKind.MinusToken => _builder.BuildSub(left, right, "sub"),
                SyntaxKind.StarToken => _builder.BuildMul(left, right, "mul"),
                SyntaxKind.SlashToken when signed => _builder.BuildSDiv(left, right, "sdiv"),
                SyntaxKind.SlashToken => _builder.BuildUDiv(left, right, "udiv"),
                SyntaxKind.PercentToken when signed => _builder.BuildSRem(left, right, "srem"),
                SyntaxKind.PercentToken => _builder.BuildURem(left, right, "urem"),
                SyntaxKind.AmpersandToken => _builder.BuildAnd(left, right, "and"),
                SyntaxKind.PipeToken => _builder.BuildOr(left, right, "or"),
                SyntaxKind.CaretToken => _builder.BuildXor(left, right, "xor"),
                SyntaxKind.LessLessToken => _builder.BuildShl(left, right, "shl"),
                SyntaxKind.GreaterGreaterToken when signed => _builder.BuildAShr(left, right, "ashr"),
                SyntaxKind.GreaterGreaterToken => _builder.BuildLShr(left, right, "lshr"),
                _ => throw new LlvmCodeGenerationException($"Integer operator '{operatorKind}' is not supported."),
            };
        }

        private LLVMValueRef ExtractBaseValue(LLVMValueRef value, StructTypeSymbol type, DeclaredTypeSymbol baseType)
        {
            while (!TypeIdentity.AreSame(type, baseType))
            {
                value = _builder.BuildExtractValue(value, 0, "base.value");
                type = type.BaseType ?? throw new LlvmCodeGenerationException("Invalid base subobject.");
            }
            return value;
        }

        // Every base shares the object's address. The storage owner's declaration,
        // not the receiver's descendants, determines the dispatch pointer offset.
        private LLVMValueRef EmitDispatchAddress(StructTypeSymbol type, LLVMValueRef address)
        {
            StructTypeSymbol owner = type.DispatchStorageOwner
                ?? throw new LlvmCodeGenerationException($"struct '{type.Name}' has no runtime dispatch storage.");
            return _builder.BuildStructGEP2(_mapType(owner), address,
                LlvmStructLayout.DispatchIndex(owner), "dispatch.address");
        }

        private LLVMValueRef EmitRuntimeDispatch(StructTypeSymbol type, LLVMValueRef address) =>
            _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                EmitDispatchAddress(type, address), "dispatch.table");

        private LLVMValueRef SizeConstant(ulong value) => LLVMValueRef.CreateConstInt(_mapType(BuiltinTypes.NUInt), value);
        private LLVMValueRef IntConstant(int value) => LLVMValueRef.CreateConstInt(_context.Int32Type, unchecked((ulong)value));
        private LLVMValueRef ToInt32(LLVMValueRef value) => value.TypeOf.IntWidth == 32 ? value : _builder.BuildTrunc(value, _context.Int32Type, "array.int.length");

        // Array values point at a header of int Length followed by Rank int dimensions.
        // Padding keeps contiguous element storage aligned for the selected target ABI.
        private ulong ArrayHeaderSize(ArrayTypeSymbol array)
        {
            ulong bytes = ((ulong)array.Rank + 1) * 4;
            ulong alignment = Math.Max(4, _getAbiAlignment(array.ElementType));
            return (bytes + alignment - 1) / alignment * alignment;
        }

        private LLVMValueRef ArrayData(LLVMValueRef array, ArrayTypeSymbol type) =>
            _builder.BuildInBoundsGEP2(_context.Int8Type, array,
                new LLVMValueRef[] { SizeConstant(ArrayHeaderSize(type)) }, "array.data");

        private LLVMValueRef MetadataAddress(LLVMValueRef array, LLVMValueRef slot) =>
            _builder.BuildInBoundsGEP2(_context.Int32Type, array,
                new LLVMValueRef[] { slot }, "array.metadata.address");

        private LLVMValueRef ReadDimension(LLVMValueRef array, LLVMValueRef dimension) =>
            _builder.BuildLoad2(_context.Int32Type,
                MetadataAddress(array, _builder.BuildAdd(dimension, IntConstant(1))), "array.dimension");

        private void EmitRuntimeCheck(LLVMValueRef valid)
        {
            if (!_enableRuntimeChecks)
                return;

            LLVMBasicBlockRef success = _llvmFunction.AppendBasicBlock("array.check.ok");
            LLVMBasicBlockRef failure = _llvmFunction.AppendBasicBlock("array.check.failed");
            _builder.BuildCondBr(valid, success, failure);
            _builder.PositionAtEnd(failure);
            _builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [], false), _getTrap(), Array.Empty<LLVMValueRef>(), string.Empty);
            _builder.BuildUnreachable();
            _builder.PositionAtEnd(success);
        }

        private LLVMValueRef ConvertIntegerToSize(LLVMValueRef value, TypeSymbol sourceType)
        {
            LLVMTypeRef sizeType = _mapType(BuiltinTypes.NUInt);
            int sourceWidth = _getIntegerBitWidth(sourceType);
            int targetWidth = _getIntegerBitWidth(BuiltinTypes.NUInt);
            if (sourceWidth == targetWidth)
            {
                return value;
            }

            if (sourceWidth > targetWidth)
            {
                return _builder.BuildTrunc(value, sizeType, "array.length.trunc");
            }

            bool signed = sourceType is PrimitiveTypeSymbol { IsSigned: true } || TypeIdentity.AreSame(sourceType, BuiltinTypes.NInt) || TypeIdentity.AreSame(sourceType, BuiltinTypes.CLong);
            return signed
                ? _builder.BuildSExt(value, sizeType, "array.length.sext")
                : _builder.BuildZExt(value, sizeType, "array.length.zext");
        }

        private LLVMValueRef EmitAllocation(
            LLVMValueRef size,
            string name,
            LLVMValueRef alignment = default)
        {
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef address = alignment.Handle == IntPtr.Zero
                ? _builder.BuildCall2(runtime.MallocType, runtime.Malloc, new[] { size }, name)
                : _builder.BuildCall2(runtime.AlignedMallocType, runtime.AlignedMalloc,
                    new[] { size, alignment }, name);
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, address,
                LLVMValueRef.CreateConstPointerNull(address.TypeOf), "allocation.valid"));
            return address;
        }

        private LLVMValueRef EmitZeroedAllocation(
            LLVMValueRef size,
            string name,
            LLVMValueRef alignment = default)
        {
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef address;
            if (alignment.Handle == IntPtr.Zero)
            {
                address = _builder.BuildCall2(runtime.CallocType, runtime.Calloc,
                    new[] { SizeConstant(1), size }, name);
            }
            else
            {
                address = _builder.BuildCall2(runtime.AlignedMallocType, runtime.AlignedMalloc,
                    new[] { size, alignment }, name);
                EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, address,
                    LLVMValueRef.CreateConstPointerNull(address.TypeOf), "allocation.valid"));
                LLVMApi.BuildMemSet(_builder, address,
                    LLVMValueRef.CreateConstInt(_context.Int8Type, 0, false), size, 1);
                return address;
            }
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, address,
                LLVMValueRef.CreateConstPointerNull(address.TypeOf), "allocation.valid"));
            return address;
        }

        private LLVMValueRef EmitWeakRelease(LLVMValueRef control)
        {
            LLVMBasicBlockRef body = _llvmFunction.AppendBasicBlock("weak.release.body");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("weak.release.end");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, control,
                LLVMValueRef.CreateConstPointerNull(control.TypeOf), "weak.release.valid"), body, end);
            _builder.PositionAtEnd(body);
            LLVMValueRef weakAddress = OwnershipControlField(control, 1, "weak.release.count.address");
            EmitWeakCounterRelease(control, weakAddress, "weak.release.control");
            _builder.BuildBr(end);
            _builder.PositionAtEnd(end);
            return default;
        }

        private void EmitWeakCounterRelease(LLVMValueRef control, LLVMValueRef weakAddress, string name)
        {
            LLVMBasicBlockRef free = _llvmFunction.AppendBasicBlock($"{name}.free");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock($"{name}.end");
            LLVMValueRef previous = _atomics.FetchSub(
                weakAddress,
                SizeConstant(1),
                LLVMAtomicOrdering.LLVMAtomicOrderingRelease,
                $"{name}.weak");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, previous, SizeConstant(1),
                $"{name}.unused"), free, end);
            _builder.PositionAtEnd(free);
            _atomics.AcquireFence();
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            _builder.BuildCall2(runtime.FreeType, runtime.Free, new LLVMValueRef[] { control }, string.Empty);
            _builder.BuildBr(end);
            _builder.PositionAtEnd(end);
        }

        private LLVMValueRef EmitDeallocation(LLVMValueRef address)
        {
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            _builder.BuildCall2(runtime.FreeType, runtime.Free, new[] { address }, string.Empty);
            return default;
        }

        private LLVMValueRef EmitInterfaceAccessorCall(
            InterfaceTypeSymbol interfaceType,
            FunctionSymbol accessor,
            LLVMValueRef data,
            LLVMValueRef table,
            LLVMValueRef[] arguments,
            string name)
        {
            LLVMTypeRef entryType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef tableType = LLVMTypeRef.CreateArray(entryType, (uint)interfaceType.AllMethods.Length + 1);
            LLVMValueRef functionAddress = _builder.BuildGEP2(
                tableType,
                table,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)interfaceType.GetMethodSlot(accessor) + 1, false),
                },
                "interface.slot");
            LLVMValueRef function = _builder.BuildLoad2(entryType, functionAddress, "interface.method");
            var parameterTypes = new List<LLVMTypeRef> { entryType };
            parameterTypes.AddRange(accessor.Parameters.Select(parameter => _mapType(parameter.Type)));
            LLVMTypeRef signature = LLVMTypeRef.CreateFunction(_mapType(accessor.ReturnType), [.. parameterTypes], false);
            var callArguments = new LLVMValueRef[arguments.Length + 1];
            callArguments[0] = data;
            arguments.CopyTo(callArguments, 1);
            return EmitPotentiallyThrowingCall(signature, function, callArguments, name);
        }

        private LLVMValueRef EmitInterfaceTableLookup(
            LLVMValueRef map,
            InterfaceTypeSymbol interfaceType)
        {
            LLVMTypeRef pointerType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMValueRef indexAddress = _builder.BuildAlloca(_context.Int32Type, "interface.lookup.index");
            LLVMValueRef resultAddress = _builder.BuildAlloca(pointerType, "interface.lookup.result");
            _builder.BuildStore(LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), indexAddress);

            LLVMBasicBlockRef condition = _llvmFunction.AppendBasicBlock("interface.lookup.condition");
            LLVMBasicBlockRef compare = _llvmFunction.AppendBasicBlock("interface.lookup.compare");
            LLVMBasicBlockRef found = _llvmFunction.AppendBasicBlock("interface.lookup.found");
            LLVMBasicBlockRef next = _llvmFunction.AppendBasicBlock("interface.lookup.next");
            LLVMBasicBlockRef missing = _llvmFunction.AppendBasicBlock("interface.lookup.missing");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("interface.lookup.end");
            _builder.BuildBr(condition);

            _builder.PositionAtEnd(condition);
            LLVMValueRef index = _builder.BuildLoad2(_context.Int32Type, indexAddress, "interface.lookup.current");
            LLVMValueRef entry = _builder.BuildGEP2(
                _interfaceMapEntryType,
                map,
                new LLVMValueRef[] { index },
                "interface.map.entry");
            LLVMValueRef key = _builder.BuildLoad2(
                pointerType,
                _builder.BuildStructGEP2(_interfaceMapEntryType, entry, 0, "interface.map.key.address"),
                "interface.map.key");
            _builder.BuildCondBr(
                _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, key,
                    LLVMValueRef.CreateConstPointerNull(pointerType), "interface.map.end"),
                missing,
                compare);

            _builder.PositionAtEnd(compare);
            LlvmFunction stringCompare = _getStringCompare();
            LLVMValueRef identityComparison = _builder.BuildCall2(
                stringCompare.Type,
                stringCompare.Value,
                new LLVMValueRef[] { key, _interfaceKeys[interfaceType] },
                "interface.key.compare");
            _builder.BuildCondBr(
                _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, identityComparison,
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    "interface.map.match"),
                found,
                next);

            _builder.PositionAtEnd(found);
            LLVMValueRef table = _builder.BuildLoad2(
                pointerType,
                _builder.BuildStructGEP2(_interfaceMapEntryType, entry, 1, "interface.map.table.address"),
                "interface.map.table");
            _builder.BuildStore(table, resultAddress);
            _builder.BuildBr(end);

            _builder.PositionAtEnd(next);
            _builder.BuildStore(
                _builder.BuildAdd(index, LLVMValueRef.CreateConstInt(_context.Int32Type, 1, false),
                    "interface.lookup.increment"),
                indexAddress);
            _builder.BuildBr(condition);

            _builder.PositionAtEnd(missing);
            _builder.BuildCall2(
                LLVMTypeRef.CreateFunction(_context.VoidType, [], false),
                _getTrap(),
                Array.Empty<LLVMValueRef>(),
                string.Empty);
            _builder.BuildUnreachable();

            _builder.PositionAtEnd(end);
            return _builder.BuildLoad2(pointerType, resultAddress, "interface.table");
        }

        private LLVMValueRef EmitInstanceAccessorCall(
            FunctionSymbol accessor,
            StructTypeSymbol receiverType,
            LLVMValueRef receiver,
            LLVMValueRef[] arguments,
            string name)
        {
            var callArguments = new LLVMValueRef[arguments.Length + 1];
            callArguments[0] = receiver;
            arguments.CopyTo(callArguments, 1);
            LlvmFunction signature = _functions[accessor];
            if (accessor.VTableSlot is not int slot)
                return EmitPotentiallyThrowingCall(accessor, signature, callArguments, name);

            if (!_virtualTables.TryGetValue(receiverType, out LlvmVTable vtable))
                throw new LlvmCodeGenerationException($"struct '{receiverType.Name}' has no virtual method table.");
            LLVMValueRef vtablePointer = EmitRuntimeDispatch(receiverType, receiver);
            LLVMValueRef functionAddress = _builder.BuildGEP2(
                vtable.Type,
                vtablePointer,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)slot + 1, false),
                },
                "virtual.slot");
            LLVMValueRef function = _builder.BuildLoad2(
                LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                functionAddress,
                "virtual.method");
            return EmitPotentiallyThrowingCall(signature.Type, function, callArguments, name);
        }

        private LLVMValueRef GetFunctionAddress(FunctionSymbol function) =>
            _cAbiFunctions.TryGetValue(function, out LlvmCAbiFunction abi)
                ? abi.Value
                : LlvmCAbi.RequiresLowering(
                    function.ReturnType,
                    function.Parameters.Select(parameter => parameter.Type))
                    ? throw new LlvmCodeGenerationException(
                        $"function '{function.FullName}' cannot be used as a C ABI function pointer because its struct signature is not C-compatible")
                    : _functions[function].Value;

    }
}
