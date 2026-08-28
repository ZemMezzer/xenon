using System.Collections.Immutable;
using System.Numerics;
using LLVMSharp.Interop;
using Xenon.Compiler;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.CodeGen.LLVM;

public sealed class LlvmIrGenerator
{
    private readonly Dictionary<FunctionSymbol, LlvmFunction> _functions = [];
    private readonly Dictionary<string, LlvmFunction> _nativeFunctions = new(StringComparer.Ordinal);
    private readonly Dictionary<StructTypeSymbol, LLVMTypeRef> _structTypes = [];
    private readonly Dictionary<InterfaceTypeSymbol, LLVMTypeRef> _interfaceTypes = [];
    private readonly Dictionary<FieldSymbol, LLVMValueRef> _staticFields = [];
    private readonly Dictionary<StructTypeSymbol, LlvmVTable> _virtualTables = [];
    private readonly Dictionary<StructTypeSymbol, LlvmVTable> _interfaceMaps = [];
    private LLVMContextRef _context;
    private LLVMModuleRef _module;
    private NativeTargetMachine? _targetMachine;
    private LlvmMemoryRuntime? _memoryRuntime;

    public string Generate(Compilation compilation, string moduleName = "xenon") =>
        GenerateModule(
            compilation,
            moduleName,
            targetMachine: null,
            module => module.PrintToString(),
            generateExecutableEntryPoint: false);

    public string GenerateForTarget(
        Compilation compilation,
        LlvmTargetOptions targetOptions,
        string moduleName = "xenon",
        bool generateExecutableEntryPoint = false)
    {
        ArgumentNullException.ThrowIfNull(targetOptions);
        using NativeTargetMachine targetMachine = NativeTargetMachine.Create(targetOptions);
        return GenerateModule(
            compilation,
            moduleName,
            targetMachine,
            module => module.PrintToString(),
            generateExecutableEntryPoint);
    }

    /// <summary>Performs target-specific semantic validation and returns source-located diagnostics.</summary>
    public static Compilation BindForTarget(Compilation compilation, LlvmTargetOptions targetOptions)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(targetOptions);
        if (compilation.HasErrors) return compilation;
        using NativeTargetMachine target = NativeTargetMachine.Create(targetOptions);
        return BindForTarget(compilation, target);
    }

    private static Compilation BindForTarget(Compilation compilation, NativeTargetMachine target)
    {
        using var layout = new LlvmTypeLayout(target);
        return compilation.WithTargetLayout(layout);
    }

    internal TResult GenerateModule<TResult>(
        Compilation compilation,
        string moduleName,
        NativeTargetMachine? targetMachine,
        Func<LLVMModuleRef, TResult> resultFactory,
        bool generateExecutableEntryPoint)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentNullException.ThrowIfNull(resultFactory);

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

        _context = LLVMContextRef.Create();
        _module = _context.CreateModuleWithName(moduleName);
        _targetMachine = targetMachine;

        try
        {
            if (targetMachine is not null)
            {
                _module.Target = targetMachine.Triple;
                _module.DataLayout = targetMachine.DataLayout;
            }

            ValidateEnumStorage(compilation.SemanticModel.GlobalNamespace);
            DeclareInterfaceTypes(compilation.SemanticModel.GlobalNamespace);
            DeclareStructTypes(compilation.SemanticModel.GlobalNamespace);
            DeclareFunctions(compilation.SemanticModel.GlobalNamespace);
            DeclareInterfaceTables(compilation.SemanticModel.GlobalNamespace);
            DeclareVirtualTables(compilation.SemanticModel.GlobalNamespace);
            DeclareStaticFields(compilation.SemanticModel.GlobalNamespace);
            EmitFunctionBodies(compilation.SemanticModel.Functions);
            if (generateExecutableEntryPoint)
            {
                EmitExecutableEntryPoint(compilation.SemanticModel.Functions);
            }

            _module.Verify(LLVMVerifierFailureAction.LLVMReturnStatusAction);
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
            _context.Dispose();
            _functions.Clear();
            _nativeFunctions.Clear();
            _structTypes.Clear();
            _interfaceTypes.Clear();
            _staticFields.Clear();
            _virtualTables.Clear();
            _interfaceMaps.Clear();
            _targetMachine = null;
            _memoryRuntime = null;
        }
    }

    private void DeclareStructTypes(NamespaceSymbol globalNamespace)
    {
        var types = new List<StructTypeSymbol>();
        CollectStructTypes(globalNamespace, types);
        foreach (StructTypeSymbol type in types)
        {
            _structTypes.Add(type, _context.CreateNamedStruct(type.FullName));
        }

        foreach (StructTypeSymbol type in types)
        {
            LLVMTypeRef[] fields = LlvmStructLayout.Elements(type, MapType, LLVMTypeRef.CreatePointer(_context.Int8Type, 0));
            _structTypes[type].StructSetBody(fields, false);
        }
    }

    private void DeclareInterfaceTypes(NamespaceSymbol @namespace)
    {
        foreach (InterfaceTypeSymbol type in @namespace.Interfaces)
        {
            LLVMTypeRef llvmType = _context.CreateNamedStruct(type.FullName);
            llvmType.StructSetBody([LLVMTypeRef.CreatePointer(_context.Int8Type, 0), LLVMTypeRef.CreatePointer(_context.Int8Type, 0)], false);
            _interfaceTypes.Add(type, llvmType);
        }
        foreach (NamespaceSymbol child in @namespace.Namespaces)
            DeclareInterfaceTypes(child);
    }

    private void DeclareVirtualTables(NamespaceSymbol @namespace)
    {
        foreach (StructTypeSymbol type in @namespace.Types.Where(type => type.HasVirtualDispatch))
        {
            LLVMTypeRef elementType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef tableType = LLVMTypeRef.CreateArray(elementType, (uint)type.VirtualMethods.Length + 1);
            LLVMValueRef table = _module.AddGlobal(tableType, $"{type.FullName}.__vtable");
            table.Linkage = LLVMLinkage.LLVMInternalLinkage;
            // The runtime interface map precedes the virtual method slots.
            // Object layout still needs only one dispatch pointer.
            LLVMValueRef[] entries = [
                _interfaceMaps.TryGetValue(type, out LlvmVTable map) ? map.Value : LLVMValueRef.CreateConstPointerNull(elementType),
                .. type.VirtualMethods.Select(method => _functions[method].Value)];
            table.Initializer = LLVMValueRef.CreateConstArray(elementType, entries);
            _virtualTables.Add(type, new LlvmVTable(table, tableType));
        }
        foreach (NamespaceSymbol child in @namespace.Namespaces)
            DeclareVirtualTables(child);
    }

    private void DeclareInterfaceTables(NamespaceSymbol @namespace)
    {
        foreach (StructTypeSymbol type in @namespace.Types)
        {
            var tables = new Dictionary<InterfaceTypeSymbol, LlvmVTable>();
            foreach (InterfaceTypeSymbol @interface in type.ImplementedInterfaces)
            {
                FunctionSymbol[] implementations = @interface.AllMethods.Select(required => type.FindInterfaceImplementation(required)!).ToArray();
                LLVMTypeRef elementType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                LLVMTypeRef tableType = LLVMTypeRef.CreateArray(elementType, (uint)implementations.Length);
                LLVMValueRef table = _module.AddGlobal(tableType, $"{type.FullName}.{@interface.Name}.__itable");
                table.Linkage = LLVMLinkage.LLVMInternalLinkage;
                table.Initializer = LLVMValueRef.CreateConstArray(elementType, implementations.Select(method => _functions[method].Value).ToArray());
                tables.Add(@interface, new LlvmVTable(table, tableType));
            }

            if (tables.Count > 0)
            {
                LLVMTypeRef entryType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                LLVMTypeRef mapType = LLVMTypeRef.CreateArray(entryType, (uint)_interfaceTypes.Count);
                LLVMValueRef[] entries = Enumerable.Repeat(
                    LLVMValueRef.CreateConstPointerNull(entryType),
                    _interfaceTypes.Count).ToArray();
                foreach ((InterfaceTypeSymbol @interface, LlvmVTable table) in tables)
                    entries[@interface.DispatchId] = table.Value;

                LLVMValueRef map = _module.AddGlobal(mapType, $"{type.FullName}.__imap");
                map.Linkage = LLVMLinkage.LLVMInternalLinkage;
                map.Initializer = LLVMValueRef.CreateConstArray(entryType, entries);
                _interfaceMaps.Add(type, new LlvmVTable(map, mapType));
            }
        }
        foreach (NamespaceSymbol child in @namespace.Namespaces)
            DeclareInterfaceTables(child);
    }

    private void DeclareStaticFields(NamespaceSymbol @namespace)
    {
        foreach (StructTypeSymbol type in @namespace.Types)
        {
            foreach (FieldSymbol field in type.StaticFields)
            {
                LLVMTypeRef fieldType = MapType(field.Type);
                LLVMValueRef global = _module.AddGlobal(fieldType, $"{type.FullName}.{field.Name}");
                global.Linkage = field.IsPublic ? LLVMLinkage.LLVMExternalLinkage : LLVMLinkage.LLVMInternalLinkage;
                global.Initializer = CreateStaticInitializer(field.Type, field.ConstantValue);
                _staticFields.Add(field, global);
            }
        }
        foreach (NamespaceSymbol child in @namespace.Namespaces)
            DeclareStaticFields(child);
    }

    private LLVMValueRef CreateStaticInitializer(TypeSymbol type, object? value)
    {
        LLVMTypeRef llvmType = MapType(type);
        if (value is null)
            return DefaultValue(type, MapType, _virtualTables);
        if (ReferenceEquals(type, BuiltinTypes.Bool))
            return LLVMValueRef.CreateConstInt(llvmType, value is true ? 1UL : 0UL, false);
        if (type is PrimitiveTypeSymbol { IsInteger: true })
            return LLVMValueRef.CreateConstInt(llvmType, GetIntegerConstantBits(value), false);
        if (type is PrimitiveTypeSymbol { IsFloatingPoint: true })
            return LLVMValueRef.CreateConstReal(llvmType, Convert.ToDouble(value));
        throw new LlvmCodeGenerationException($"static field type '{type.Name}' does not support a constant initializer");
    }

    private static LLVMValueRef DefaultValue(TypeSymbol type, Func<TypeSymbol, LLVMTypeRef> mapType,
        Dictionary<StructTypeSymbol, LlvmVTable> virtualTables, StructTypeSymbol? runtimeType = null)
    {
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

    private static ulong GetIntegerConstantBits(object value) => value switch
    {
        int integer => unchecked((ulong)(long)integer),
        long integer => unchecked((ulong)integer),
        ulong integer => integer,
        _ => Convert.ToUInt64(value),
    };

    private static void CollectStructTypes(NamespaceSymbol @namespace, ICollection<StructTypeSymbol> types)
    {
        foreach (StructTypeSymbol type in @namespace.Types)
        {
            types.Add(type);
        }

        foreach (NamespaceSymbol child in @namespace.Namespaces)
        {
            CollectStructTypes(child, types);
        }
    }

    private void DeclareFunctions(NamespaceSymbol @namespace)
    {
        foreach (FunctionSymbol function in @namespace.Functions)
        {
            DeclareFunction(function);
        }

        foreach (StructTypeSymbol type in @namespace.Types)
        {
            foreach (FunctionSymbol method in type.Methods)
            {
                DeclareFunction(method);
            }

            foreach (FunctionSymbol constructor in type.Constructors)
                DeclareFunction(constructor);

            if (type.InstanceInitializer is not null)
                DeclareFunction(type.InstanceInitializer);

            if (type.Destructor is not null)
            {
                DeclareFunction(type.Destructor);
            }
        }

        foreach (NamespaceSymbol child in @namespace.Namespaces)
        {
            DeclareFunctions(child);
        }
    }

    private void DeclareFunction(FunctionSymbol function)
    {
        LLVMTypeRef returnType = MapType(function.ReturnType);
        var parameterTypes = new List<LLVMTypeRef>();
        if (function.HasImplicitThis)
        {
            parameterTypes.Add(LLVMTypeRef.CreatePointer(MapType(function.ContainingType!), 0));
        }

        parameterTypes.AddRange(function.Parameters.Select(parameter => MapType(parameter.Type)));
        LLVMTypeRef functionType = LLVMTypeRef.CreateFunction(returnType, [.. parameterTypes], false);
        string nativeName = NativeSymbolNames.Get(function);
        if (_nativeFunctions.TryGetValue(nativeName, out LlvmFunction existing))
        {
            if (!function.IsExtern || existing.Type != functionType)
                throw new LlvmCodeGenerationException($"Native symbol '{nativeName}' has incompatible declarations.");
            _functions.Add(function, existing);
            return;
        }
        LLVMValueRef value = _module.AddFunction(nativeName, functionType);
        if (function.IsAbstract)
        {
            value.Linkage = LLVMLinkage.LLVMInternalLinkage;
            using LLVMBuilderRef builder = _context.CreateBuilder();
            LLVMBasicBlockRef entry = value.AppendBasicBlock("entry");
            builder.PositionAtEnd(entry);
            builder.BuildUnreachable();
        }
        else if (!function.IsExtern && !function.IsExport && !function.IsPublic)
        {
            value.Linkage = LLVMLinkage.LLVMInternalLinkage;
        }
        else if (function.IsExport && IsWindowsTarget())
        {
            value.DLLStorageClass = LLVMDLLStorageClass.LLVMDLLExportStorageClass;
        }

        var llvmFunction = new LlvmFunction(value, functionType);
        _functions.Add(function, llvmFunction);
        _nativeFunctions.Add(nativeName, llvmFunction);
    }

    private void EmitFunctionBodies(ImmutableArray<BoundFunction> functions)
    {
        foreach (BoundFunction function in functions)
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
                _staticFields,
                _virtualTables,
                _interfaceMaps,
                _interfaceTypes.Count,
                MapType,
                GetOrDeclareMemoryRuntime,
                GetOrDeclareTrap,
                GetAbiSize,
                GetAbiAlignment,
                GetFieldOffset,
                GetIntegerBitWidth);
            emitter.Emit(function.Body);
        }
    }

    private void EmitExecutableEntryPoint(ImmutableArray<BoundFunction> functions)
    {
        BoundFunction[] candidates = functions
            .Where(function =>
                function.Symbol.FunctionKind == FunctionKind.Ordinary &&
                function.Symbol.ContainingType is null &&
                function.Symbol.Name == "Main")
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

        FunctionSymbol entryPoint = candidates[0].Symbol;
        if (!ReferenceEquals(entryPoint.ReturnType, BuiltinTypes.Int) || !entryPoint.Parameters.IsEmpty)
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
        LLVMBasicBlockRef block = nativeEntryPoint.AppendBasicBlock("entry");
        using LLVMBuilderRef builder = _context.CreateBuilder();
        builder.PositionAtEnd(block);
        LLVMValueRef result = builder.BuildCall2(
            xenonEntryPoint.Type,
            xenonEntryPoint.Value,
            Array.Empty<LLVMValueRef>(),
            "result");
        builder.BuildRet(result);
    }

    private LLVMTypeRef MapType(TypeSymbol type)
    {
        if (ReferenceEquals(type, BuiltinTypes.Void))
        {
            return _context.VoidType;
        }

        if (ReferenceEquals(type, BuiltinTypes.Bool))
        {
            return _context.Int1Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Byte) || ReferenceEquals(type, BuiltinTypes.SByte))
        {
            return _context.Int8Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Short) || ReferenceEquals(type, BuiltinTypes.UShort))
        {
            return _context.Int16Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Int) || ReferenceEquals(type, BuiltinTypes.UInt))
        {
            return _context.Int32Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.Long) || ReferenceEquals(type, BuiltinTypes.ULong))
        {
            return _context.Int64Type;
        }

        if (ReferenceEquals(type, BuiltinTypes.NInt) || ReferenceEquals(type, BuiltinTypes.NUInt))
        {
            return MapTargetInteger(type, GetPointerBitWidth());
        }

        if (ReferenceEquals(type, BuiltinTypes.CLong) || ReferenceEquals(type, BuiltinTypes.CULong))
        {
            int bitWidth = IsWindowsTarget() ? 32 : GetPointerBitWidth();
            return MapTargetInteger(type, bitWidth);
        }

        if (ReferenceEquals(type, BuiltinTypes.Float))
        {
            return _context.FloatType;
        }

        if (ReferenceEquals(type, BuiltinTypes.Double))
        {
            return _context.DoubleType;
        }

        if (type is ArrayTypeSymbol array)
        {
            return LLVMTypeRef.CreatePointer(MapType(array.ElementType), 0);
        }

        if (type is EnumTypeSymbol enumeration) return MapType(enumeration.UnderlyingType);

        if (type is PointerTypeSymbol pointer)
        {
            return LLVMTypeRef.CreatePointer(MapType(pointer.ElementType), 0);
        }

        if (type is ReferenceTypeSymbol reference)
        {
            return LLVMTypeRef.CreatePointer(MapType(reference.ElementType), 0);
        }

        if (type is StructTypeSymbol structType && _structTypes.TryGetValue(structType, out LLVMTypeRef llvmStruct))
        {
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
        if (type is PrimitiveTypeSymbol { IsInteger: true, BitWidth: int bitWidth })
        {
            return bitWidth;
        }

        if (ReferenceEquals(type, BuiltinTypes.NInt) || ReferenceEquals(type, BuiltinTypes.NUInt))
        {
            return GetPointerBitWidth();
        }

        if (ReferenceEquals(type, BuiltinTypes.CLong) || ReferenceEquals(type, BuiltinTypes.CULong))
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
            _module.GetNamedFunction("free").Handle != IntPtr.Zero)
        {
            throw new LlvmCodeGenerationException(
                "Native symbols 'malloc' and 'free' are reserved for Xenon heap operations.");
        }

        LLVMTypeRef pointerType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        LLVMTypeRef sizeType = MapTargetInteger(BuiltinTypes.NUInt, _targetMachine.PointerBitWidth);
        LLVMTypeRef mallocType = LLVMTypeRef.CreateFunction(pointerType, [sizeType], false);
        LLVMTypeRef freeType = LLVMTypeRef.CreateFunction(_context.VoidType, [pointerType], false);
        _memoryRuntime = new LlvmMemoryRuntime(
            _module.AddFunction("malloc", mallocType),
            mallocType,
            _module.AddFunction("free", freeType),
            freeType,
            sizeType,
            _module.AddFunction("llvm.stacksave.p0", LLVMTypeRef.CreateFunction(pointerType, [], false)),
            _module.AddFunction("llvm.stackrestore.p0", freeType));
        return _memoryRuntime;
    }

    private LLVMValueRef GetOrDeclareTrap()
    {
        LLVMValueRef trap = _module.GetNamedFunction("llvm.trap");
        return trap.Handle != IntPtr.Zero ? trap
            : _module.AddFunction("llvm.trap", LLVMTypeRef.CreateFunction(_context.VoidType, [], false));
    }

    private int GetPointerBitWidth() => _targetMachine?.PointerBitWidth
        ?? throw new LlvmCodeGenerationException(
            "Target-dependent integer types require a configured LLVM target machine.");

    private bool IsWindowsTarget() => _targetMachine?.Triple.Contains(
        "windows",
        StringComparison.OrdinalIgnoreCase) is true ||
        _targetMachine?.Triple.Contains("win32", StringComparison.OrdinalIgnoreCase) is true;

    private readonly record struct LlvmFunction(LLVMValueRef Value, LLVMTypeRef Type);
    private readonly record struct LlvmVTable(LLVMValueRef Value, LLVMTypeRef Type);

    private sealed record LlvmMemoryRuntime(
        LLVMValueRef Malloc,
        LLVMTypeRef MallocType,
        LLVMValueRef Free,
        LLVMTypeRef FreeType,
        LLVMTypeRef SizeType,
        LLVMValueRef StackSave,
        LLVMValueRef StackRestore);

    private sealed class FunctionEmitter
    {
        private readonly LLVMContextRef _context;
        private readonly LLVMBuilderRef _builder;
        private readonly FunctionSymbol _function;
        private readonly LLVMValueRef _llvmFunction;
        private readonly Dictionary<FunctionSymbol, LlvmFunction> _functions;
        private readonly Dictionary<FieldSymbol, LLVMValueRef> _staticFields;
        private readonly Dictionary<StructTypeSymbol, LlvmVTable> _virtualTables;
        private readonly Dictionary<StructTypeSymbol, LlvmVTable> _interfaceMaps;
        private readonly int _interfaceCount;
        private readonly Func<TypeSymbol, LLVMTypeRef> _mapType;
        private readonly Func<LlvmMemoryRuntime> _getMemoryRuntime;
        private readonly Func<LLVMValueRef> _getTrap;
        private readonly Func<TypeSymbol, ulong> _getAbiSize;
        private readonly Func<TypeSymbol, uint> _getAbiAlignment;
        private readonly Func<StructTypeSymbol, FieldSymbol, ulong> _getFieldOffset;
        private readonly Func<TypeSymbol, int> _getIntegerBitWidth;
        private readonly LLVMValueRef _thisValue;
        private readonly Dictionary<VariableSymbol, LLVMValueRef> _addresses = [];
        private readonly Stack<LoopTargets> _loopTargets = [];
        private readonly Stack<BranchTarget> _breakTargets = [];
        private readonly List<CleanupScope> _cleanupScopes = [];
        private LLVMValueRef _cleanupHead;
        private LLVMTypeRef _cleanupNodeType;
        private bool _terminated;

        public FunctionEmitter(
            LLVMContextRef context,
            LLVMBuilderRef builder,
            FunctionSymbol function,
            LLVMValueRef llvmFunction,
            Dictionary<FunctionSymbol, LlvmFunction> functions,
            Dictionary<FieldSymbol, LLVMValueRef> staticFields,
            Dictionary<StructTypeSymbol, LlvmVTable> virtualTables,
            Dictionary<StructTypeSymbol, LlvmVTable> interfaceMaps,
            int interfaceCount,
            Func<TypeSymbol, LLVMTypeRef> mapType,
            Func<LlvmMemoryRuntime> getMemoryRuntime,
            Func<LLVMValueRef> getTrap,
            Func<TypeSymbol, ulong> getAbiSize,
            Func<TypeSymbol, uint> getAbiAlignment,
            Func<StructTypeSymbol, FieldSymbol, ulong> getFieldOffset,
            Func<TypeSymbol, int> getIntegerBitWidth)
        {
            _context = context;
            _builder = builder;
            _function = function;
            _llvmFunction = llvmFunction;
            _functions = functions;
            _staticFields = staticFields;
            _virtualTables = virtualTables;
            _interfaceMaps = interfaceMaps;
            _interfaceCount = interfaceCount;
            _mapType = mapType;
            _getMemoryRuntime = getMemoryRuntime;
            _getTrap = getTrap;
            _getAbiSize = getAbiSize;
            _getAbiAlignment = getAbiAlignment;
            _getFieldOffset = getFieldOffset;
            _getIntegerBitWidth = getIntegerBitWidth;
            _thisValue = function.HasImplicitThis ? llvmFunction.GetParam(0) : default;

            uint parameterOffset = function.HasImplicitThis ? 1u : 0u;
            for (int index = 0; index < function.Parameters.Length; index++)
            {
                ParameterSymbol parameter = function.Parameters[index];
                LLVMValueRef address = _builder.BuildAlloca(_mapType(parameter.Type), parameter.Name);
                _builder.BuildStore(llvmFunction.GetParam(parameterOffset + (uint)index), address);
                _addresses.Add(parameter, address);
            }
        }

        public void Emit(BoundBlockStatement body)
        {
            AllocateLocals(body);
            if (_function.HasStackArrays)
            {
                LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                // One runtime LIFO list per call tracks allocations, not aliases. Nodes
                // retain the element stride and destructor so different types can mix.
                _cleanupNodeType = _context.GetStructType([pointer, pointer, _mapType(BuiltinTypes.NUInt), _mapType(BuiltinTypes.NUInt), pointer], false);
                _cleanupHead = _builder.BuildAlloca(pointer, "stack.cleanup.head");
                _builder.BuildStore(LLVMValueRef.CreateConstPointerNull(pointer), _cleanupHead);
            }
            EmitBlock(body);

            if (!_terminated && ReferenceEquals(_function.ReturnType, BuiltinTypes.Void))
            {
                _builder.BuildRetVoid();
                _terminated = true;
            }
        }

        private void AllocateLocals(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    foreach (BoundStatement child in block.Statements)
                    {
                        AllocateLocals(child);
                    }

                    break;
                case BoundVariableDeclarationStatement variable:
                    LLVMValueRef address = _builder.BuildAlloca(_mapType(variable.Variable.Type), variable.Variable.Name);
                    _addresses.Add(variable.Variable, address);
                    break;
                case BoundIfStatement @if:
                    AllocateLocals(@if.ThenStatement);
                    if (@if.ElseStatement is not null)
                    {
                        AllocateLocals(@if.ElseStatement);
                    }

                    break;
                case BoundWhileStatement @while:
                    AllocateLocals(@while.Body);
                    break;
                case BoundSwitchStatement @switch:
                    foreach (BoundSwitchSection section in @switch.Sections) AllocateLocals(section.Body);
                    break;
                case BoundForStatement @for:
                    if (@for.Initializer is not null)
                    {
                        AllocateLocals(@for.Initializer);
                    }

                    AllocateLocals(@for.Body);
                    break;
            }
        }

        private void EmitBlock(BoundBlockStatement block)
        {
            BeginCleanupScope();
            foreach (BoundStatement statement in block.Statements)
            {
                if (_terminated)
                {
                    break;
                }

                EmitStatement(statement);
            }
            EndCleanupScope();
        }

        private void EmitEmbeddedStatement(BoundStatement statement)
        {
            if (statement is BoundBlockStatement block)
            {
                EmitBlock(block);
                return;
            }
            BeginCleanupScope();
            EmitStatement(statement);
            EndCleanupScope();
        }

        private void BeginCleanupScope()
        {
            if (!_function.HasStackArrays) return;
            LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMValueRef stack = _builder.BuildCall2(LLVMTypeRef.CreateFunction(pointer, [], false), _getMemoryRuntime().StackSave, Array.Empty<LLVMValueRef>(), "scope.stack");
            LLVMValueRef head = _builder.BuildLoad2(pointer, _cleanupHead, "scope.cleanup.head");
            _cleanupScopes.Add(new CleanupScope(stack, head));
        }

        private void EndCleanupScope()
        {
            if (!_function.HasStackArrays) return;
            if (!_terminated) EmitCleanup(_cleanupScopes.Count - 1);
            _cleanupScopes.RemoveAt(_cleanupScopes.Count - 1);
        }

        private void EmitCleanup(int retainedDepth)
        {
            if (_cleanupScopes.Count <= retainedDepth) return;
            CleanupScope scope = _cleanupScopes[retainedDepth];
            LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMBasicBlockRef condition = _llvmFunction.AppendBasicBlock("stack.cleanup.condition");
            LLVMBasicBlockRef body = _llvmFunction.AppendBasicBlock("stack.cleanup.body");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("stack.cleanup.end");
            _builder.BuildBr(condition);
            _builder.PositionAtEnd(condition);
            LLVMValueRef node = _builder.BuildLoad2(pointer, _cleanupHead, "stack.cleanup.node");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, node, scope.Head), body, end);
            _builder.PositionAtEnd(body);
            LLVMValueRef Load(uint index, LLVMTypeRef type) => _builder.BuildLoad2(type, _builder.BuildStructGEP2(_cleanupNodeType, node, index), "stack.cleanup.field");
            LLVMValueRef next = Load(0, pointer);
            LLVMValueRef data = Load(1, pointer);
            LLVMValueRef length = Load(2, _mapType(BuiltinTypes.NUInt));
            LLVMValueRef stride = Load(3, _mapType(BuiltinTypes.NUInt));
            LLVMValueRef destructor = Load(4, pointer);
            EmitElementLoop(length, reverse: true, index =>
            {
                LLVMValueRef element = _builder.BuildGEP2(_context.Int8Type, data, new[] { _builder.BuildMul(index, stride) }, "stack.destroy.element");
                _builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [pointer], false), destructor, new[] { element }, string.Empty);
            });
            _builder.BuildStore(next, _cleanupHead);
            _builder.BuildBr(condition);
            _builder.PositionAtEnd(end);
            _builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [pointer], false), _getMemoryRuntime().StackRestore, new[] { scope.Stack }, string.Empty);
        }

        private void EmitStatement(BoundStatement statement)
        {
            switch (statement)
            {
                case BoundBlockStatement block:
                    EmitBlock(block);
                    break;
                case BoundVariableDeclarationStatement variable:
                    EmitVariableDeclaration(variable);
                    break;
                case BoundExpressionStatement expression:
                    EmitExpression(expression.Expression);
                    break;
                case BoundReturnStatement @return:
                    EmitReturn(@return);
                    break;
                case BoundIfStatement @if:
                    EmitIf(@if);
                    break;
                case BoundWhileStatement @while:
                    EmitWhile(@while);
                    break;
                case BoundForStatement @for:
                    EmitFor(@for);
                    break;
                case BoundBreakStatement:
                    EmitLoopBranch(_breakTargets.Peek());
                    break;
                case BoundSwitchStatement @switch:
                    EmitSwitch(@switch);
                    break;
                case BoundContinueStatement:
                    EmitLoopBranch(_loopTargets.Peek().ContinueTarget);
                    break;
                default:
                    throw new LlvmCodeGenerationException($"Bound statement '{statement.Kind}' is not supported by LLVM code generation.");
            }
        }

        private void EmitSwitch(BoundSwitchStatement statement)
        {
            if (statement.Expression.Type is PrimitiveTypeSymbol { IsInteger: true, BitWidth: null } integer)
                foreach (BoundSwitchSection section in statement.Sections)
                    if (section.Value is BoundLiteralExpression literal && !FitsTargetInteger(literal.Value, _getIntegerBitWidth(integer), integer.IsSigned))
                        throw new LlvmCodeGenerationException("case value is out of range for the selected target's switch operand type");
            LLVMValueRef value = EmitExpression(statement.Expression);
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("switch.end");
            var blocks = new LLVMBasicBlockRef[statement.Sections.Length];
            LLVMBasicBlockRef next = end;
            for (int i = blocks.Length - 1; i >= 0; i--)
            {
                if (!statement.Sections[i].Body.Statements.IsEmpty) next = _llvmFunction.AppendBasicBlock("switch.case");
                blocks[i] = next;
            }
            LLVMBasicBlockRef fallback = end;
            for (int i = 0; i < blocks.Length; i++)
                if (statement.Sections[i].Value is null) fallback = blocks[i];
            LLVMValueRef dispatch = _builder.BuildSwitch(value, fallback, (uint)blocks.Length);
            for (int i = 0; i < blocks.Length; i++)
                if (statement.Sections[i].Value is BoundLiteralExpression label) dispatch.AddCase(EmitLiteral(label), blocks[i]);
            _breakTargets.Push(new BranchTarget(end, _cleanupScopes.Count));
            for (int i = 0; i < blocks.Length; i++)
            {
                if (statement.Sections[i].Body.Statements.IsEmpty) continue;
                _builder.PositionAtEnd(blocks[i]);
                _terminated = false;
                EmitBlock(statement.Sections[i].Body);
                if (!_terminated) _builder.BuildBr(end);
            }
            _breakTargets.Pop();
            _builder.PositionAtEnd(end);
            _terminated = false;
            // An exhaustive returning switch has no live merge edge.
            if (BoundControlFlow.AlwaysReturns(statement))
            {
                _builder.BuildUnreachable();
                _terminated = true;
            }
        }

        private void EmitIf(BoundIfStatement statement)
        {
            LLVMValueRef condition = EmitExpression(statement.Condition);
            LLVMBasicBlockRef thenBlock = _llvmFunction.AppendBasicBlock("if.then");

            if (statement.ElseStatement is null)
            {
                LLVMBasicBlockRef endBlock = _llvmFunction.AppendBasicBlock("if.end");
                _builder.BuildCondBr(condition, thenBlock, endBlock);

                _builder.PositionAtEnd(thenBlock);
                _terminated = false;
                EmitEmbeddedStatement(statement.ThenStatement);
                if (!_terminated)
                {
                    _builder.BuildBr(endBlock);
                }

                _builder.PositionAtEnd(endBlock);
                _terminated = false;
                return;
            }

            LLVMBasicBlockRef elseBlock = _llvmFunction.AppendBasicBlock("if.else");
            _builder.BuildCondBr(condition, thenBlock, elseBlock);

            _builder.PositionAtEnd(thenBlock);
            _terminated = false;
            EmitEmbeddedStatement(statement.ThenStatement);
            bool thenFallsThrough = !_terminated;
            LLVMBasicBlockRef thenEnd = _builder.InsertBlock;

            _builder.PositionAtEnd(elseBlock);
            _terminated = false;
            EmitEmbeddedStatement(statement.ElseStatement);
            bool elseFallsThrough = !_terminated;
            LLVMBasicBlockRef elseEnd = _builder.InsertBlock;

            if (!thenFallsThrough && !elseFallsThrough)
            {
                _terminated = true;
                return;
            }

            LLVMBasicBlockRef mergeBlock = _llvmFunction.AppendBasicBlock("if.end");
            if (thenFallsThrough)
            {
                _builder.PositionAtEnd(thenEnd);
                _builder.BuildBr(mergeBlock);
            }

            if (elseFallsThrough)
            {
                _builder.PositionAtEnd(elseEnd);
                _builder.BuildBr(mergeBlock);
            }

            _builder.PositionAtEnd(mergeBlock);
            _terminated = false;
        }

        private void EmitWhile(BoundWhileStatement statement)
        {
            LLVMBasicBlockRef conditionBlock = _llvmFunction.AppendBasicBlock("while.condition");
            LLVMBasicBlockRef bodyBlock = _llvmFunction.AppendBasicBlock("while.body");
            LLVMBasicBlockRef endBlock = _llvmFunction.AppendBasicBlock("while.end");
            _builder.BuildBr(conditionBlock);

            _builder.PositionAtEnd(conditionBlock);
            LLVMValueRef condition = EmitExpression(statement.Condition);
            _builder.BuildCondBr(condition, bodyBlock, endBlock);

            _builder.PositionAtEnd(bodyBlock);
            _terminated = false;
            _loopTargets.Push(new LoopTargets(new BranchTarget(conditionBlock, _cleanupScopes.Count)));
            _breakTargets.Push(new BranchTarget(endBlock, _cleanupScopes.Count));
            EmitEmbeddedStatement(statement.Body);
            _breakTargets.Pop();
            _loopTargets.Pop();
            if (!_terminated)
            {
                _builder.BuildBr(conditionBlock);
            }

            _builder.PositionAtEnd(endBlock);
            _terminated = false;
        }

        private void EmitFor(BoundForStatement statement)
        {
            BeginCleanupScope();
            if (statement.Initializer is not null)
            {
                EmitStatement(statement.Initializer);
            }

            LLVMBasicBlockRef conditionBlock = _llvmFunction.AppendBasicBlock("for.condition");
            LLVMBasicBlockRef bodyBlock = _llvmFunction.AppendBasicBlock("for.body");
            LLVMBasicBlockRef incrementBlock = _llvmFunction.AppendBasicBlock("for.increment");
            LLVMBasicBlockRef endBlock = _llvmFunction.AppendBasicBlock("for.end");
            _builder.BuildBr(conditionBlock);

            _builder.PositionAtEnd(conditionBlock);
            LLVMValueRef condition = statement.Condition is null
                ? LLVMValueRef.CreateConstInt(_context.Int1Type, 1, false)
                : EmitExpression(statement.Condition);
            _builder.BuildCondBr(condition, bodyBlock, endBlock);

            _builder.PositionAtEnd(bodyBlock);
            _terminated = false;
            _loopTargets.Push(new LoopTargets(new BranchTarget(incrementBlock, _cleanupScopes.Count)));
            _breakTargets.Push(new BranchTarget(endBlock, _cleanupScopes.Count));
            EmitEmbeddedStatement(statement.Body);
            _breakTargets.Pop();
            _loopTargets.Pop();
            if (!_terminated)
            {
                _builder.BuildBr(incrementBlock);
            }

            _builder.PositionAtEnd(incrementBlock);
            _terminated = false;
            if (statement.Increment is not null)
            {
                EmitExpression(statement.Increment);
            }

            _builder.BuildBr(conditionBlock);

            _builder.PositionAtEnd(endBlock);
            _terminated = false;
            EndCleanupScope();
        }

        private void EmitLoopBranch(BranchTarget target)
        {
            EmitCleanup(target.RetainedDepth);
            _builder.BuildBr(target.Block);
            _terminated = true;
        }

        private void EmitVariableDeclaration(BoundVariableDeclarationStatement statement)
        {
            LLVMValueRef address = GetAddress(statement.Variable);

            if (statement.Initializer is not null)
            {
                _builder.BuildStore(EmitExpression(statement.Initializer), address);
            }
        }

        private void EmitReturn(BoundReturnStatement statement)
        {
            // Capture the result before destructors can mutate observable state.
            LLVMValueRef result = statement.Expression is null ? default : EmitExpression(statement.Expression);
            EmitCleanup(0);
            if (statement.Expression is null)
            {
                _builder.BuildRetVoid();
            }
            else
            {
                _builder.BuildRet(result);
            }

            _terminated = true;
        }

        private LLVMValueRef EmitExpression(BoundExpression expression) => expression switch
        {
            BoundLiteralExpression literal => EmitLiteral(literal),
            BoundVariableExpression variable => EmitVariable(variable),
            BoundThisExpression => _thisValue,
            BoundUnaryExpression unary => EmitUnary(unary),
            BoundBinaryExpression binary => EmitBinary(binary),
            BoundAssignmentExpression assignment => EmitAssignment(assignment),
            BoundCompoundAccessorAssignmentExpression assignment => EmitCompoundAccessorAssignment(assignment),
            BoundCallExpression call => EmitCall(call),
            BoundMethodCallExpression methodCall when methodCall.Method.VTableSlot is not null => EmitVirtualMethodCall(methodCall),
            BoundMethodCallExpression methodCall => EmitMethodCall(methodCall),
            BoundPropertySetExpression propertySet => EmitPropertySet(propertySet),
            BoundInterfacePropertySetExpression interfacePropertySet => EmitInterfacePropertySet(interfacePropertySet),
            BoundIndexerSetExpression indexerSet => EmitIndexerSet(indexerSet),
            BoundInterfaceIndexerSetExpression interfaceIndexerSet => EmitInterfaceIndexerSet(interfaceIndexerSet),
            BoundMemberAccessExpression member => EmitMemberAccess(member),
            BoundStaticFieldExpression field => _builder.BuildLoad2(_mapType(field.Type), _staticFields[field.Field], field.Field.Name),
            BoundTypeLayoutExpression layout => EmitTypeLayout(layout),
            BoundCastExpression cast => EmitCast(cast),
            BoundInterfaceConversionExpression conversion => EmitInterfaceConversion(conversion),
            BoundReferenceConversionExpression conversion => EmitReferenceConversion(conversion),
            BoundReferenceDereferenceExpression dereference => EmitReferenceDereference(dereference),
            BoundInterfaceMethodCallExpression interfaceCall => EmitInterfaceMethodCall(interfaceCall),
            BoundIndexExpression index => EmitIndex(index),
            BoundStructConstructionExpression construction => EmitStructConstruction(
                construction.StructType,
                construction.Arguments,
                construction.IsDefaultInitialization),
            BoundConstructorCallExpression constructor => EmitConstructorCall(constructor),
            BoundBaseLifecycleCallExpression lifecycle => EmitLifecycleCall(lifecycle.Function, _thisValue, lifecycle.Arguments, initializeVTable: false),
            BoundArrayCreationExpression array => EmitArrayCreation(array),
            BoundArrayMetadataExpression metadata => EmitArrayMetadata(metadata),
            BoundNewExpression @new => EmitNew(@new),
            BoundFreeExpression free => EmitFree(free),
            _ => throw new LlvmCodeGenerationException($"Bound expression '{expression.Kind}' is not supported by LLVM code generation."),
        };

        private LLVMValueRef EmitLiteral(BoundLiteralExpression expression)
        {
            if (expression.Value is null && expression.Type is not PointerTypeSymbol)
            {
                throw new LlvmCodeGenerationException(
                    "Uncontextualized null literal reached LLVM code generation; null must be bound to a concrete pointer type first.");
            }

            LLVMTypeRef type = _mapType(expression.Type);

            if (ReferenceEquals(expression.Type, BuiltinTypes.Bool))
            {
                return LLVMValueRef.CreateConstInt(type, expression.Value is true ? 1UL : 0UL, false);
            }

            if (expression.Type is PrimitiveTypeSymbol { IsInteger: true } or EnumTypeSymbol)
            {
                ulong value = expression.Value switch
                {
                    int integer => unchecked((ulong)integer),
                    long integer => unchecked((ulong)integer),
                    ulong integer => integer,
                    _ => throw new LlvmCodeGenerationException("Invalid bound integer literal."),
                };
                return LLVMValueRef.CreateConstInt(type, value, true);
            }

            if (expression.Type is PrimitiveTypeSymbol { IsFloatingPoint: true })
            {
                double value = expression.Value switch
                {
                    float single => single,
                    double @double => @double,
                    _ => throw new LlvmCodeGenerationException("Invalid bound floating-point literal."),
                };
                return LLVMValueRef.CreateConstReal(type, value);
            }

            if (expression.Value is string text)
            {
                return _builder.BuildGlobalStringPtr(text, "str");
            }

            if (expression.Type is PointerTypeSymbol && expression.Value is null)
            {
                return LLVMValueRef.CreateConstPointerNull(type);
            }

            throw new LlvmCodeGenerationException($"Literal of type '{expression.Type.Name}' is not supported.");
        }

        private LLVMValueRef EmitVariable(BoundVariableExpression expression)
        {
            LLVMValueRef address = GetAddress(expression.Variable);
            return _builder.BuildLoad2(_mapType(expression.Type), address, expression.Variable.Name);
        }

        private LLVMValueRef EmitReferenceConversion(BoundReferenceConversionExpression expression)
        {
            if (expression.Source is BoundThisExpression)
                return EmitExpression(expression.Source);
            if (IsAddressable(expression.Source))
                return EmitAddress(expression.Source);
            return StoreTemporary(expression.Source, expression.Source.Type);
        }

        private LLVMValueRef EmitReferenceDereference(BoundReferenceDereferenceExpression expression) =>
            _builder.BuildLoad2(
                _mapType(expression.ReferenceType.ElementType),
                EmitExpression(expression.Reference),
                "reference.value");

        private LLVMValueRef EmitUnary(BoundUnaryExpression expression)
        {
            if (expression.OperatorKind == SyntaxKind.AmpersandToken)
            {
                return EmitAddress(expression.Operand);
            }
            if (expression.OperatorKind is SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken)
                return EmitIncrement(expression);

            LLVMValueRef operand = EmitExpression(expression.Operand);
            return expression.OperatorKind switch
            {
                SyntaxKind.PlusToken => operand,
                SyntaxKind.MinusToken when expression.Type is PrimitiveTypeSymbol { IsFloatingPoint: true } =>
                    _builder.BuildFNeg(operand, "fneg"),
                SyntaxKind.MinusToken => _builder.BuildNeg(operand, "neg"),
                SyntaxKind.BangToken or SyntaxKind.TildeToken => _builder.BuildNot(operand, "not"),
                SyntaxKind.StarToken when expression.Operand.Type is PointerTypeSymbol pointer =>
                    _builder.BuildLoad2(_mapType(pointer.ElementType), operand, "deref"),
                _ => throw new LlvmCodeGenerationException($"Unary operator '{expression.OperatorKind}' is not supported."),
            };
        }

        private LLVMValueRef EmitIncrement(BoundUnaryExpression expression)
        {
            LLVMValueRef address = EmitAddress(expression.Operand);
            LLVMValueRef operand = _builder.BuildLoad2(_mapType(expression.Type), address, "increment.current");
            TypeSymbol stepType = expression.Type is PointerTypeSymbol ? BuiltinTypes.NInt : expression.Type;
            LLVMValueRef one = expression.Type is PrimitiveTypeSymbol { IsFloatingPoint: true }
                ? LLVMValueRef.CreateConstReal(_mapType(expression.Type), 1.0)
                : LLVMValueRef.CreateConstInt(_mapType(stepType), 1, false);
            LLVMValueRef result = expression.OperatorKind == SyntaxKind.PlusPlusToken
                ? EmitArithmetic(SyntaxKind.PlusToken, expression.Type, operand, one, stepType)
                : EmitArithmetic(SyntaxKind.MinusToken, expression.Type, operand, one, stepType);
            _builder.BuildStore(result, address);
            return expression.IsPostfix ? operand : result;
        }

        private LLVMValueRef EmitBinary(BoundBinaryExpression expression)
        {
            if (expression.OperatorKind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken)
            {
                return EmitShortCircuit(expression);
            }

            LLVMValueRef left = EmitExpression(expression.Left);
            LLVMValueRef right = EmitExpression(expression.Right);

            if (expression.OperatorKind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or
                SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or
                SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken)
            {
                return EmitComparison(expression, left, right);
            }

            return EmitArithmetic(expression.OperatorKind, expression.Left.Type, left, right, expression.Right.Type);
        }

        private LLVMValueRef EmitShortCircuit(BoundBinaryExpression expression)
        {
            LLVMValueRef left = EmitExpression(expression.Left);
            LLVMBasicBlockRef leftBlock = _builder.InsertBlock;
            LLVMBasicBlockRef rightBlock = _llvmFunction.AppendBasicBlock("logic.rhs");
            LLVMBasicBlockRef mergeBlock = _llvmFunction.AppendBasicBlock("logic.end");
            bool isAnd = expression.OperatorKind == SyntaxKind.AmpersandAmpersandToken;

            if (isAnd)
            {
                _builder.BuildCondBr(left, rightBlock, mergeBlock);
            }
            else
            {
                _builder.BuildCondBr(left, mergeBlock, rightBlock);
            }

            _builder.PositionAtEnd(rightBlock);
            LLVMValueRef right = EmitExpression(expression.Right);
            LLVMBasicBlockRef rightEnd = _builder.InsertBlock;
            _builder.BuildBr(mergeBlock);

            _builder.PositionAtEnd(mergeBlock);
            LLVMValueRef shortCircuitValue = LLVMValueRef.CreateConstInt(
                _context.Int1Type,
                isAnd ? 0UL : 1UL,
                false);
            LLVMValueRef result = _builder.BuildPhi(_context.Int1Type, "logic");
            result.AddIncoming(
                [shortCircuitValue, right],
                [leftBlock, rightEnd],
                2);
            return result;
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

        private LLVMValueRef EmitComparison(
            BoundBinaryExpression expression,
            LLVMValueRef left,
            LLVMValueRef right)
        {
            if (expression.Left.Type is PrimitiveTypeSymbol { IsFloatingPoint: true })
            {
                LLVMRealPredicate realPredicate = expression.OperatorKind switch
                {
                    SyntaxKind.EqualsEqualsToken => LLVMRealPredicate.LLVMRealOEQ,
                    SyntaxKind.BangEqualsToken => LLVMRealPredicate.LLVMRealUNE,
                    SyntaxKind.LessToken => LLVMRealPredicate.LLVMRealOLT,
                    SyntaxKind.LessOrEqualsToken => LLVMRealPredicate.LLVMRealOLE,
                    SyntaxKind.GreaterToken => LLVMRealPredicate.LLVMRealOGT,
                    SyntaxKind.GreaterOrEqualsToken => LLVMRealPredicate.LLVMRealOGE,
                    _ => throw new LlvmCodeGenerationException("Invalid floating-point comparison."),
                };
                return _builder.BuildFCmp(realPredicate, left, right, "fcmp");
            }

            bool signed = expression.Left.Type is PrimitiveTypeSymbol { IsSigned: true };
            LLVMIntPredicate intPredicate = expression.OperatorKind switch
            {
                SyntaxKind.EqualsEqualsToken => LLVMIntPredicate.LLVMIntEQ,
                SyntaxKind.BangEqualsToken => LLVMIntPredicate.LLVMIntNE,
                SyntaxKind.LessToken when signed => LLVMIntPredicate.LLVMIntSLT,
                SyntaxKind.LessToken => LLVMIntPredicate.LLVMIntULT,
                SyntaxKind.LessOrEqualsToken when signed => LLVMIntPredicate.LLVMIntSLE,
                SyntaxKind.LessOrEqualsToken => LLVMIntPredicate.LLVMIntULE,
                SyntaxKind.GreaterToken when signed => LLVMIntPredicate.LLVMIntSGT,
                SyntaxKind.GreaterToken => LLVMIntPredicate.LLVMIntUGT,
                SyntaxKind.GreaterOrEqualsToken when signed => LLVMIntPredicate.LLVMIntSGE,
                SyntaxKind.GreaterOrEqualsToken => LLVMIntPredicate.LLVMIntUGE,
                _ => throw new LlvmCodeGenerationException("Invalid integer comparison."),
            };
            return _builder.BuildICmp(intPredicate, left, right, "icmp");
        }

        private LLVMValueRef EmitAssignment(BoundAssignmentExpression expression)
        {
            LLVMValueRef value = EmitExpression(expression.Expression);
            LLVMValueRef address = EmitAddress(expression.Target);

            if (expression.OperatorKind != SyntaxKind.EqualsToken)
            {
                LLVMValueRef current = _builder.BuildLoad2(_mapType(expression.Target.Type), address, "current");
                value = EmitArithmetic(
                    GetBinaryOperatorForCompoundAssignment(expression.OperatorKind),
                    expression.Target.Type,
                    current,
                    value,
                    expression.Expression.Type);
            }

            _builder.BuildStore(value, address);
            return value;
        }

        private LLVMValueRef EmitMemberAccess(BoundMemberAccessExpression expression)
        {
            if (!expression.IsPointerAccess && !IsAddressable(expression.Receiver))
            {
                return _builder.BuildExtractValue(
                    ExtractBaseValue(EmitExpression(expression.Receiver),
                        (StructTypeSymbol)expression.Receiver.Type, expression.Field.ContainingType),
                    checked((uint)expression.Field.Ordinal),
                    expression.Field.Name);
            }

            LLVMValueRef address = EmitAddress(expression);
            return _builder.BuildLoad2(_mapType(expression.Type), address, expression.Field.Name);
        }

        private LLVMValueRef EmitStructConstruction(
            StructTypeSymbol structType,
            ImmutableArray<BoundExpression> arguments,
            bool defaultInitialize = false)
        {
            if (structType.AllInstanceFields.Any(field => field.Initializer is not null))
            {
                LLVMValueRef address = _builder.BuildAlloca(_mapType(structType), $"{structType.Name}.init.tmp");
                if (defaultInitialize)
                    _builder.BuildStore(DefaultValue(structType, _mapType, _virtualTables), address);
                if (structType.HasVirtualDispatch && _virtualTables.TryGetValue(structType, out LlvmVTable initializedVTable))
                {
                    LLVMValueRef vtableAddress = EmitDispatchAddress(structType, address);
                    _builder.BuildStore(initializedVTable.Value, vtableAddress);
                }

                EmitDefaultInstanceInitialization(structType, address);
                for (int index = 0; index < arguments.Length; index++)
                {
                    FieldSymbol field = structType.AllInstanceFields[index];
                    LLVMValueRef fieldAddress = _builder.BuildStructGEP2(
                        _mapType(field.ContainingType),
                        address,
                        checked((uint)field.Ordinal),
                        $"{field.Name}.address");
                    _builder.BuildStore(EmitExpression(arguments[index]), fieldAddress);
                }

                return _builder.BuildLoad2(_mapType(structType), address, $"{structType.Name}.value");
            }

            LLVMValueRef value = defaultInitialize
                ? DefaultValue(structType, _mapType, _virtualTables)
                : _mapType(structType).Poison;
            if (structType.HasVirtualDispatch && _virtualTables.TryGetValue(structType, out LlvmVTable vtable))
            {
                StructTypeSymbol owner = structType.DispatchStorageOwner!;
                value = InsertSubobjectElement(value, structType, owner, vtable.Value,
                    LlvmStructLayout.DispatchIndex(owner), "vtable.init");
            }
            for (int index = 0; index < arguments.Length; index++)
            {
                value = InsertSubobjectElement(
                    value, structType, structType.AllInstanceFields[index].ContainingType,
                    EmitExpression(arguments[index]),
                    checked((uint)structType.AllInstanceFields[index].Ordinal),
                    $"{structType.AllInstanceFields[index].Name}.init");
            }

            return value;
        }

        private LLVMValueRef ExtractBaseValue(LLVMValueRef value, StructTypeSymbol type, StructTypeSymbol baseType)
        {
            while (!ReferenceEquals(type, baseType))
            {
                value = _builder.BuildExtractValue(value, 0, "base.value");
                type = type.BaseType ?? throw new LlvmCodeGenerationException("Invalid base subobject.");
            }
            return value;
        }

        private LLVMValueRef InsertSubobjectElement(LLVMValueRef value, StructTypeSymbol type,
            StructTypeSymbol owner, LLVMValueRef element, uint index, string name)
        {
            if (ReferenceEquals(type, owner))
                return _builder.BuildInsertValue(value, element, index, name);
            LLVMValueRef baseValue = _builder.BuildExtractValue(value, 0, "base.value");
            baseValue = InsertSubobjectElement(baseValue, type.BaseType!, owner, element, index, name);
            return _builder.BuildInsertValue(value, baseValue, 0, "base.init");
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

        private void EmitDefaultInstanceInitialization(StructTypeSymbol type, LLVMValueRef address)
        {
            if (type.BaseType is StructTypeSymbol baseType)
                EmitDefaultInstanceInitialization(baseType, address);
            if (type.InstanceInitializer is FunctionSymbol initializer)
                EmitLifecycleCall(initializer, address, [], initializeVTable: false);
        }

        private LLVMValueRef EmitConstructorCall(BoundConstructorCallExpression expression)
        {
            LLVMValueRef address = _builder.BuildAlloca(_mapType(expression.StructType), $"{expression.StructType.Name}.ctor.tmp");
            _builder.BuildStore(DefaultValue(expression.StructType, _mapType, _virtualTables), address);
            EmitLifecycleCall(expression.Constructor, address, expression.Arguments);
            return _builder.BuildLoad2(_mapType(expression.StructType), address, $"{expression.StructType.Name}.value");
        }

        private LLVMValueRef EmitArrayCreation(BoundArrayCreationExpression expression)
        {
            LLVMValueRef[] dimensions = expression.Dimensions.Select(dimension =>
            {
                LLVMValueRef value = EmitExpression(dimension);
                LLVMTypeRef sourceType = _mapType(dimension.Type);
                int width = _getIntegerBitWidth(dimension.Type);
                ulong max = width < 32 ? (1UL << width) - 1 : int.MaxValue;
                LLVMValueRef valid = _builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, value, LLVMValueRef.CreateConstInt(sourceType, max), "array.dimension.valid");
                if (dimension.Type is PrimitiveTypeSymbol { IsSigned: true })
                    valid = _builder.BuildAnd(valid, _builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, value, LLVMValueRef.CreateConstInt(sourceType, 0)), "array.dimension.nonnegative");
                EmitRuntimeCheck(valid);
                return ConvertIntegerToSize(value, dimension.Type);
            }).ToArray();
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef hasZeroDimension = LLVMValueRef.CreateConstInt(_context.Int1Type, 0);
            foreach (LLVMValueRef dimension in dimensions)
                hasZeroDimension = _builder.BuildOr(hasZeroDimension, _builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, dimension, SizeConstant(0)));
            LLVMValueRef length = _builder.BuildSelect(hasZeroDimension, SizeConstant(0), SizeConstant(1), "array.initial.length");
            foreach (LLVMValueRef dimension in dimensions)
            {
                LLVMValueRef divisor = _builder.BuildSelect(_builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, dimension, SizeConstant(0)), SizeConstant(1), dimension, "array.product.divisor");
                EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, length, _builder.BuildUDiv(SizeConstant(int.MaxValue), divisor), "array.product.valid"));
                length = _builder.BuildMul(length, dimension, "array.length");
            }
            ulong headerSize = ArrayHeaderSize(expression.ArrayType);
            ulong elementBytes = _getAbiSize(expression.ElementType);
            ulong maxSize = _getIntegerBitWidth(BuiltinTypes.NUInt) == 32 ? uint.MaxValue : ulong.MaxValue;
            if (elementBytes > 0)
                EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, length, SizeConstant((maxSize - headerSize) / elementBytes), "array.bytes.valid"));
            LLVMValueRef elementSize = SizeConstant(elementBytes);
            LLVMValueRef byteCount = _builder.BuildMul(length, elementSize, "array.bytes");
            LLVMValueRef allocationSize = _builder.BuildAdd(byteCount, SizeConstant(headerSize), "array.allocation.bytes");
            LLVMValueRef address;
            if (expression.Storage == ArrayStorageKind.Stack)
            {
                address = _builder.BuildArrayAlloca(_context.Int8Type, allocationSize, $"{expression.ElementType.Name}.stack.array");
                address.Alignment = Math.Max(4, _getAbiAlignment(expression.ElementType));
            }
            else
            {
                address = EmitAllocation(allocationSize, $"{expression.ElementType.Name}.heap.array");
            }
            _builder.BuildStore(ToInt32(length), address);
            for (int i = 0; i < dimensions.Length; i++)
                _builder.BuildStore(ToInt32(dimensions[i]), MetadataAddress(address, IntConstant(i + 1)));
            LLVMValueRef data = ArrayData(address, expression.ArrayType);
            EmitElementLoop(length, reverse: false, index =>
            {
                LLVMValueRef element = _builder.BuildGEP2(_mapType(expression.ElementType), data, new LLVMValueRef[] { index }, "array.initialize.element");
                _builder.BuildStore(DefaultValue(expression.ElementType, _mapType, _virtualTables), element);
                if (expression.ElementType is StructTypeSymbol structure)
                {
                    if (structure.HasVirtualDispatch && _virtualTables.TryGetValue(structure, out LlvmVTable table))
                        _builder.BuildStore(table.Value, EmitDispatchAddress(structure, element));
                    EmitDefaultInstanceInitialization(structure, element);
                }
            });
            if (expression.Storage == ArrayStorageKind.Stack &&
                expression.ElementType is StructTypeSymbol elementType && elementType.FindDestructor() is FunctionSymbol destructor)
            {
                LLVMValueRef node = _builder.BuildAlloca(_cleanupNodeType, "stack.cleanup.registration");
                LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
                LLVMValueRef[] fields = [_builder.BuildLoad2(pointer, _cleanupHead), data, length, elementSize, _functions[destructor].Value];
                for (uint i = 0; i < fields.Length; i++)
                    _builder.BuildStore(fields[i], _builder.BuildStructGEP2(_cleanupNodeType, node, i));
                _builder.BuildStore(node, _cleanupHead);
            }
            return address;
        }

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
            _builder.BuildGEP2(_context.Int8Type, array, new LLVMValueRef[] { SizeConstant(ArrayHeaderSize(type)) }, "array.data");

        private LLVMValueRef MetadataAddress(LLVMValueRef array, LLVMValueRef slot) =>
            _builder.BuildGEP2(_context.Int32Type, array, new LLVMValueRef[] { slot }, "array.metadata.address");

        private LLVMValueRef ReadDimension(LLVMValueRef array, LLVMValueRef dimension) =>
            _builder.BuildLoad2(_context.Int32Type, MetadataAddress(array, _builder.BuildAdd(dimension, IntConstant(1))), "array.dimension");

        private LLVMValueRef EmitArrayMetadata(BoundArrayMetadataExpression expression)
        {
            LLVMValueRef array = EmitExpression(expression.Receiver);
            var type = (ArrayTypeSymbol)expression.Receiver.Type;
            if (expression.Member == "Rank") return IntConstant(type.Rank);
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, array, LLVMValueRef.CreateConstPointerNull(array.TypeOf), "array.valid"));
            if (expression.Member == "Length") return _builder.BuildLoad2(_context.Int32Type, array, "array.length");
            LLVMValueRef dimension = EmitExpression(expression.Dimension!);
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, dimension, IntConstant(type.Rank), "array.dimension.inrange"));
            return ReadDimension(array, dimension);
        }

        private void EmitRuntimeCheck(LLVMValueRef valid)
        {
            LLVMBasicBlockRef success = _llvmFunction.AppendBasicBlock("array.check.ok");
            LLVMBasicBlockRef failure = _llvmFunction.AppendBasicBlock("array.check.failed");
            _builder.BuildCondBr(valid, success, failure);
            _builder.PositionAtEnd(failure);
            _builder.BuildCall2(LLVMTypeRef.CreateFunction(_context.VoidType, [], false), _getTrap(), Array.Empty<LLVMValueRef>(), string.Empty);
            _builder.BuildUnreachable();
            _builder.PositionAtEnd(success);
        }

        private void EmitElementLoop(LLVMValueRef length, bool reverse, Action<LLVMValueRef> emitElement)
        {
            LLVMBasicBlockRef entry = _builder.InsertBlock;
            LLVMBasicBlockRef condition = _llvmFunction.AppendBasicBlock("array.elements.condition");
            LLVMBasicBlockRef body = _llvmFunction.AppendBasicBlock("array.elements.body");
            LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("array.elements.end");
            _builder.BuildBr(condition);
            _builder.PositionAtEnd(condition);
            LLVMValueRef index = _builder.BuildPhi(_mapType(BuiltinTypes.NUInt), "array.element.index");
            index.AddIncoming([reverse ? length : SizeConstant(0)], [entry], 1);
            LLVMValueRef test = _builder.BuildICmp(reverse ? LLVMIntPredicate.LLVMIntNE : LLVMIntPredicate.LLVMIntULT, index, reverse ? SizeConstant(0) : length);
            _builder.BuildCondBr(test, body, end);
            _builder.PositionAtEnd(body);
            LLVMValueRef current = reverse ? _builder.BuildSub(index, SizeConstant(1), "array.reverse.index") : index;
            emitElement(current);
            LLVMValueRef next = reverse ? current : _builder.BuildAdd(index, SizeConstant(1));
            LLVMBasicBlockRef backEdge = _builder.InsertBlock;
            _builder.BuildBr(condition);
            index.AddIncoming([next], [backEdge], 1);
            _builder.PositionAtEnd(end);
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

            bool signed = sourceType is PrimitiveTypeSymbol { IsSigned: true } || ReferenceEquals(sourceType, BuiltinTypes.NInt) || ReferenceEquals(sourceType, BuiltinTypes.CLong);
            return signed
                ? _builder.BuildSExt(value, sizeType, "array.length.sext")
                : _builder.BuildZExt(value, sizeType, "array.length.zext");
        }

        private LLVMValueRef EmitNew(BoundNewExpression expression)
        {
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef size = LLVMValueRef.CreateConstInt(
                runtime.SizeType,
                Math.Max(1UL, _getAbiSize(expression.StructType)),
                false);
            LLVMValueRef address = EmitAllocation(size, $"{expression.StructType.Name}.heap");

            if (expression.IsPositionalInitialization)
            {
                LLVMValueRef value = EmitStructConstruction(expression.StructType, expression.Arguments, expression.IsDefaultInitialization);
                _builder.BuildStore(value, address);
            }
            else
            {
                _builder.BuildStore(DefaultValue(expression.StructType, _mapType, _virtualTables), address);
                EmitLifecycleCall(expression.Constructor!, address, expression.Arguments);
            }

            return address;
        }

        private LLVMValueRef EmitAllocation(LLVMValueRef size, string name)
        {
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef address = _builder.BuildCall2(runtime.MallocType, runtime.Malloc, new LLVMValueRef[] { size }, name);
            EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, address,
                LLVMValueRef.CreateConstPointerNull(address.TypeOf), "allocation.valid"));
            return address;
        }

        private LLVMValueRef EmitFree(BoundFreeExpression expression)
        {
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef address = EmitExpression(expression.Pointer);
            if (expression.Pointer.Type is ArrayTypeSymbol array)
            {
                LLVMBasicBlockRef body = _llvmFunction.AppendBasicBlock("array.free");
                LLVMBasicBlockRef end = _llvmFunction.AppendBasicBlock("array.free.end");
                _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, address, LLVMValueRef.CreateConstPointerNull(address.TypeOf)), body, end);
                _builder.PositionAtEnd(body);
                if (expression.Destructor is not null)
                {
                    LLVMValueRef count = ConvertIntegerToSize(_builder.BuildLoad2(_context.Int32Type, address, "array.destroy.length"), BuiltinTypes.Int);
                    LLVMValueRef data = ArrayData(address, array);
                    EmitElementLoop(count, reverse: true, index =>
                    {
                        LLVMValueRef element = _builder.BuildGEP2(_mapType(array.ElementType), data, new LLVMValueRef[] { index }, "array.destroy.element");
                        EmitLifecycleCall(expression.Destructor, element, []);
                    });
                }
                _builder.BuildCall2(runtime.FreeType, runtime.Free, new LLVMValueRef[] { address }, string.Empty);
                _builder.BuildBr(end);
                _builder.PositionAtEnd(end);
                return default;
            }
            LLVMBasicBlockRef pointerBody = _llvmFunction.AppendBasicBlock("pointer.free");
            LLVMBasicBlockRef pointerEnd = _llvmFunction.AppendBasicBlock("pointer.free.end");
            _builder.BuildCondBr(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, address,
                LLVMValueRef.CreateConstPointerNull(address.TypeOf)), pointerBody, pointerEnd);
            _builder.PositionAtEnd(pointerBody);
            if (expression.Destructor is not null)
            {
                if (expression.Destructor.VTableSlot is int slot &&
                    expression.Pointer.Type is PointerTypeSymbol { ElementType: StructTypeSymbol staticType } &&
                    _virtualTables.TryGetValue(staticType, out LlvmVTable vtable))
                {
                    EmitVirtualDestructor(expression.Destructor, address, vtable, slot);
                }
                else
                {
                    EmitLifecycleCall(expression.Destructor, address, []);
                }
            }

            _builder.BuildCall2(
                runtime.FreeType,
                runtime.Free,
                new LLVMValueRef[] { address },
                string.Empty);
            _builder.BuildBr(pointerEnd);
            _builder.PositionAtEnd(pointerEnd);
            return default;
        }

        private void EmitVirtualDestructor(FunctionSymbol destructor, LLVMValueRef address, LlvmVTable vtable, int slot)
        {
            StructTypeSymbol staticType = destructor.ContainingType!;
            LLVMValueRef vtablePointer = EmitRuntimeDispatch(staticType, address);
            LLVMValueRef functionAddress = _builder.BuildGEP2(vtable.Type, vtablePointer,
                new LLVMValueRef[] { LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)slot + 1, false) },
                "destructor.slot");
            LLVMValueRef target = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), functionAddress, "destructor");
            LlvmFunction signature = _functions[destructor];
            _builder.BuildCall2(signature.Type, target, new LLVMValueRef[] { address }, string.Empty);
        }

        private LLVMValueRef EmitLifecycleCall(
            FunctionSymbol function,
            LLVMValueRef thisAddress,
            ImmutableArray<BoundExpression> arguments,
            bool initializeVTable = true)
        {
            if (initializeVTable && function.FunctionKind == FunctionKind.Constructor &&
                function.ContainingType is StructTypeSymbol type &&
                type.HasVirtualDispatch && _virtualTables.TryGetValue(type, out LlvmVTable vtable))
            {
                LLVMValueRef vtableAddress = EmitDispatchAddress(type, thisAddress);
                _builder.BuildStore(vtable.Value, vtableAddress);
            }

            LlvmFunction llvmFunction = _functions[function];
            var values = new LLVMValueRef[arguments.Length + 1];
            values[0] = thisAddress;
            for (int index = 0; index < arguments.Length; index++)
            {
                values[index + 1] = EmitExpression(arguments[index]);
            }

            return _builder.BuildCall2(llvmFunction.Type, llvmFunction.Value, values, string.Empty);
        }

        private LLVMValueRef EmitIndex(BoundIndexExpression expression)
        {
            LLVMValueRef address = EmitIndexAddress(expression);
            return _builder.BuildLoad2(_mapType(expression.ElementType), address, "element");
        }

        private LLVMValueRef EmitAddress(BoundExpression expression) => expression switch
        {
            BoundVariableExpression variable => GetAddress(variable.Variable),
            BoundStaticFieldExpression field => _staticFields[field.Field],
            BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } dereference =>
                EmitExpression(dereference.Operand),
            BoundReferenceDereferenceExpression dereference => EmitExpression(dereference.Reference),
            BoundMemberAccessExpression member => EmitMemberAddress(member),
            BoundIndexExpression index => EmitIndexAddress(index),
            _ => throw new LlvmCodeGenerationException(
                $"Expression '{expression.Kind}' does not have an addressable storage location."),
        };

        private LLVMValueRef EmitTypeLayout(BoundTypeLayoutExpression expression)
        {
            ulong value = expression.OperatorKind switch
            {
                SyntaxKind.SizeOfKeyword => _getAbiSize(expression.TargetType),
                SyntaxKind.AlignOfKeyword => _getAbiAlignment(expression.TargetType),
                SyntaxKind.OffsetOfKeyword => _getFieldOffset((StructTypeSymbol)expression.TargetType, expression.Field!),
                _ => throw new LlvmCodeGenerationException($"Unknown type layout intrinsic '{expression.OperatorKind}'."),
            };
            return LLVMValueRef.CreateConstInt(_mapType(BuiltinTypes.NUInt), value, false);
        }

        private LLVMValueRef EmitCast(BoundCastExpression expression)
        {
            LLVMValueRef value = EmitExpression(expression.Expression);
            if (ReferenceEquals(expression.Expression.Type, expression.TargetType))
                return value;

            LLVMTypeRef target = _mapType(expression.TargetType);
            bool sourceInteger = expression.Expression.Type is PrimitiveTypeSymbol { IsInteger: true } or EnumTypeSymbol;
            bool targetInteger = expression.TargetType is PrimitiveTypeSymbol { IsInteger: true } or EnumTypeSymbol;
            bool sourceFloat = expression.Expression.Type is PrimitiveTypeSymbol { IsFloatingPoint: true };
            bool targetFloat = expression.TargetType is PrimitiveTypeSymbol { IsFloatingPoint: true };
            if (sourceInteger && targetInteger)
            {
                int sourceWidth = _getIntegerBitWidth(expression.Expression.Type);
                int targetWidth = _getIntegerBitWidth(expression.TargetType);
                if (sourceWidth == targetWidth)
                    return value;
                if (sourceWidth > targetWidth)
                    return _builder.BuildTrunc(value, target, "cast.trunc");
                bool signed = expression.Expression.Type is PrimitiveTypeSymbol { IsSigned: true } or EnumTypeSymbol { UnderlyingType.IsSigned: true };
                return signed
                    ? _builder.BuildSExt(value, target, "cast.sext")
                    : _builder.BuildZExt(value, target, "cast.zext");
            }
            if (sourceInteger && targetFloat)
            {
                bool signed = expression.Expression.Type is PrimitiveTypeSymbol { IsSigned: true };
                return signed
                    ? _builder.BuildSIToFP(value, target, "cast.sitofp")
                    : _builder.BuildUIToFP(value, target, "cast.uitofp");
            }
            if (sourceFloat && targetInteger)
            {
                bool signed = expression.TargetType is PrimitiveTypeSymbol { IsSigned: true };
                return signed
                    ? _builder.BuildFPToSI(value, target, "cast.fptosi")
                    : _builder.BuildFPToUI(value, target, "cast.fptoui");
            }
            if (sourceFloat && targetFloat)
            {
                int sourceWidth = ((PrimitiveTypeSymbol)expression.Expression.Type).BitWidth!.Value;
                int targetWidth = ((PrimitiveTypeSymbol)expression.TargetType).BitWidth!.Value;
                return sourceWidth < targetWidth
                    ? _builder.BuildFPExt(value, target, "cast.fpext")
                    : _builder.BuildFPTrunc(value, target, "cast.fptrunc");
            }
            throw new LlvmCodeGenerationException($"cast from '{expression.Expression.Type.Name}' to '{expression.TargetType.Name}' is not supported");
        }

        private LLVMValueRef EmitInterfaceConversion(BoundInterfaceConversionExpression expression)
        {
            LLVMValueRef data = IsAddressable(expression.Source)
                ? EmitAddress(expression.Source)
                : StoreTemporary(expression.Source, expression.SourceType);
            LLVMTypeRef pointerType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMValueRef dispatch = EmitRuntimeDispatch(expression.SourceType, data);
            LLVMValueRef map = _builder.BuildLoad2(pointerType, dispatch, "interface.runtime.map");
            LLVMValueRef value = _mapType(expression.InterfaceType).Poison;
            value = _builder.BuildInsertValue(value, data, 0, "interface.data");
            return _builder.BuildInsertValue(value, map, 1, "interface.map");
        }

        private LLVMValueRef StoreTemporary(BoundExpression expression, TypeSymbol type)
        {
            LLVMValueRef temporary = _builder.BuildAlloca(_mapType(type), "interface.tmp");
            _builder.BuildStore(EmitExpression(expression), temporary);
            return temporary;
        }

        private LLVMValueRef EmitInterfaceMethodCall(BoundInterfaceMethodCallExpression expression)
        {
            LLVMValueRef interfaceValue = expression.IsPointerAccess
                ? _builder.BuildLoad2(_mapType(expression.InterfaceType), EmitExpression(expression.Receiver), "interface")
                : EmitExpression(expression.Receiver);
            LLVMValueRef data = _builder.BuildExtractValue(interfaceValue, 0, "interface.data");
            LLVMValueRef map = _builder.BuildExtractValue(interfaceValue, 1, "interface.map");
            LLVMTypeRef entryType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef mapType = LLVMTypeRef.CreateArray(entryType, (uint)_interfaceCount);
            LLVMValueRef tableAddress = _builder.BuildGEP2(mapType, map,
                new LLVMValueRef[] { LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)expression.InterfaceType.DispatchId, false) },
                "interface.table.address");
            LLVMValueRef table = _builder.BuildLoad2(entryType, tableAddress, "interface.table");
            LLVMTypeRef tableType = LLVMTypeRef.CreateArray(entryType, (uint)expression.InterfaceType.AllMethods.Length);
            LLVMValueRef address = _builder.BuildGEP2(tableType, table,
                new LLVMValueRef[] { LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)expression.InterfaceType.GetMethodSlot(expression.Method), false) },
                "interface.slot");
            LLVMValueRef function = _builder.BuildLoad2(entryType, address, "interface.method");
            var parameterTypes = new List<LLVMTypeRef> { LLVMTypeRef.CreatePointer(_context.Int8Type, 0) };
            parameterTypes.AddRange(expression.Method.Parameters.Select(parameter => _mapType(parameter.Type)));
            LLVMTypeRef signature = LLVMTypeRef.CreateFunction(_mapType(expression.Method.ReturnType), [.. parameterTypes], false);
            var arguments = new LLVMValueRef[expression.Arguments.Length + 1];
            arguments[0] = data;
            for (int index = 0; index < expression.Arguments.Length; index++) arguments[index + 1] = EmitExpression(expression.Arguments[index]);
            return _builder.BuildCall2(signature, function, arguments, ReferenceEquals(expression.Type, BuiltinTypes.Void) ? string.Empty : "interface.call");
        }

        private LLVMValueRef EmitInterfacePropertySet(BoundInterfacePropertySetExpression expression)
        {
            FunctionSymbol setter = expression.Property.Setter
                ?? throw new LlvmCodeGenerationException($"interface property '{expression.Property.Name}' has no setter");
            LLVMValueRef interfaceValue = expression.IsPointerAccess
                ? _builder.BuildLoad2(_mapType(expression.InterfaceType), EmitExpression(expression.Receiver), "interface")
                : EmitExpression(expression.Receiver);
            LLVMValueRef data = _builder.BuildExtractValue(interfaceValue, 0, "interface.data");
            LLVMValueRef map = _builder.BuildExtractValue(interfaceValue, 1, "interface.map");
            LLVMTypeRef entryType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef mapType = LLVMTypeRef.CreateArray(entryType, (uint)_interfaceCount);
            LLVMValueRef tableAddress = _builder.BuildGEP2(
                mapType,
                map,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)expression.InterfaceType.DispatchId, false),
                },
                "interface.table.address");
            LLVMValueRef table = _builder.BuildLoad2(entryType, tableAddress, "interface.table");
            LLVMTypeRef tableType = LLVMTypeRef.CreateArray(entryType, (uint)expression.InterfaceType.AllMethods.Length);
            LLVMValueRef address = _builder.BuildGEP2(
                tableType,
                table,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)expression.InterfaceType.GetMethodSlot(setter), false),
                },
                "interface.slot");
            LLVMValueRef function = _builder.BuildLoad2(entryType, address, "interface.method");
            LLVMValueRef value = EmitExpression(expression.Value);
            LLVMTypeRef signature = LLVMTypeRef.CreateFunction(
                _mapType(BuiltinTypes.Void),
                [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), _mapType(expression.Property.Type)],
                false);
            _builder.BuildCall2(signature, function, new[] { data, value }, string.Empty);
            return value;
        }

        private LLVMValueRef EmitInterfaceIndexerSet(BoundInterfaceIndexerSetExpression expression)
        {
            FunctionSymbol setter = expression.Indexer.Setter
                ?? throw new LlvmCodeGenerationException("interface indexer has no setter");
            LLVMValueRef interfaceValue = EmitExpression(expression.Receiver);
            LLVMValueRef data = _builder.BuildExtractValue(interfaceValue, 0, "interface.data");
            LLVMValueRef map = _builder.BuildExtractValue(interfaceValue, 1, "interface.map");
            LLVMTypeRef entryType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef mapType = LLVMTypeRef.CreateArray(entryType, (uint)_interfaceCount);
            LLVMValueRef tableAddress = _builder.BuildGEP2(
                mapType,
                map,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)expression.InterfaceType.DispatchId, false),
                },
                "interface.table.address");
            LLVMValueRef table = _builder.BuildLoad2(entryType, tableAddress, "interface.table");
            LLVMTypeRef tableType = LLVMTypeRef.CreateArray(entryType, (uint)expression.InterfaceType.AllMethods.Length);
            LLVMValueRef address = _builder.BuildGEP2(
                tableType,
                table,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)expression.InterfaceType.GetMethodSlot(setter), false),
                },
                "interface.slot");
            LLVMValueRef function = _builder.BuildLoad2(entryType, address, "interface.method");
            var arguments = new LLVMValueRef[expression.Arguments.Length + 2];
            arguments[0] = data;
            for (int index = 0; index < expression.Arguments.Length; index++)
                arguments[index + 1] = EmitExpression(expression.Arguments[index]);
            LLVMValueRef value = EmitExpression(expression.Value);
            arguments[^1] = value;
            var parameterTypes = new List<LLVMTypeRef> { entryType };
            parameterTypes.AddRange(setter.Parameters.Select(parameter => _mapType(parameter.Type)));
            LLVMTypeRef signature = LLVMTypeRef.CreateFunction(_mapType(BuiltinTypes.Void), [.. parameterTypes], false);
            _builder.BuildCall2(signature, function, arguments, string.Empty);
            return value;
        }

        private LLVMValueRef EmitCompoundAccessorAssignment(BoundCompoundAccessorAssignmentExpression expression)
        {
            if (expression.InterfaceType is InterfaceTypeSymbol interfaceType)
            {
                LLVMValueRef interfaceValue = expression.IsPointerAccess
                    ? _builder.BuildLoad2(_mapType(interfaceType), EmitExpression(expression.Receiver), "interface")
                    : EmitExpression(expression.Receiver);
                LLVMValueRef data = _builder.BuildExtractValue(interfaceValue, 0, "interface.data");
                LLVMValueRef map = _builder.BuildExtractValue(interfaceValue, 1, "interface.map");
                LLVMValueRef[] arguments = expression.Arguments.Select(EmitExpression).ToArray();
                LLVMValueRef current = EmitInterfaceAccessorCall(
                    interfaceType,
                    expression.Getter,
                    data,
                    map,
                    arguments,
                    "interface.get");
                LLVMValueRef value = EmitExpression(expression.Value);
                LLVMValueRef result = EmitArithmetic(expression.OperatorKind, expression.Type, current, value, expression.Value.Type);
                EmitInterfaceAccessorCall(
                    interfaceType,
                    expression.Setter,
                    data,
                    map,
                    [.. arguments, result],
                    string.Empty);
                return result;
            }

            StructTypeSymbol receiverType = expression.IsPointerAccess
                ? (StructTypeSymbol)((PointerTypeSymbol)expression.Receiver.Type).ElementType
                : (StructTypeSymbol)expression.Receiver.Type;
            LLVMValueRef receiver = EmitInstanceReceiverAddress(
                expression.Receiver,
                expression.IsPointerAccess,
                expression.Getter.ContainingType!,
                expression.Getter.Name);
            LLVMValueRef[] instanceArguments = expression.Arguments.Select(EmitExpression).ToArray();
            LLVMValueRef instanceCurrent = EmitInstanceAccessorCall(
                expression.Getter,
                receiverType,
                receiver,
                instanceArguments,
                "accessor.get");
            LLVMValueRef instanceValue = EmitExpression(expression.Value);
            LLVMValueRef instanceResult = EmitArithmetic(
                expression.OperatorKind,
                expression.Type,
                instanceCurrent,
                instanceValue,
                expression.Value.Type);
            EmitInstanceAccessorCall(
                expression.Setter,
                receiverType,
                receiver,
                [.. instanceArguments, instanceResult],
                string.Empty);
            return instanceResult;
        }

        private LLVMValueRef EmitInterfaceAccessorCall(
            InterfaceTypeSymbol interfaceType,
            FunctionSymbol accessor,
            LLVMValueRef data,
            LLVMValueRef map,
            LLVMValueRef[] arguments,
            string name)
        {
            LLVMTypeRef entryType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef mapType = LLVMTypeRef.CreateArray(entryType, (uint)_interfaceCount);
            LLVMValueRef tableAddress = _builder.BuildGEP2(
                mapType,
                map,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)interfaceType.DispatchId, false),
                },
                "interface.table.address");
            LLVMValueRef table = _builder.BuildLoad2(entryType, tableAddress, "interface.table");
            LLVMTypeRef tableType = LLVMTypeRef.CreateArray(entryType, (uint)interfaceType.AllMethods.Length);
            LLVMValueRef functionAddress = _builder.BuildGEP2(
                tableType,
                table,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)interfaceType.GetMethodSlot(accessor), false),
                },
                "interface.slot");
            LLVMValueRef function = _builder.BuildLoad2(entryType, functionAddress, "interface.method");
            var parameterTypes = new List<LLVMTypeRef> { entryType };
            parameterTypes.AddRange(accessor.Parameters.Select(parameter => _mapType(parameter.Type)));
            LLVMTypeRef signature = LLVMTypeRef.CreateFunction(_mapType(accessor.ReturnType), [.. parameterTypes], false);
            var callArguments = new LLVMValueRef[arguments.Length + 1];
            callArguments[0] = data;
            arguments.CopyTo(callArguments, 1);
            return _builder.BuildCall2(signature, function, callArguments, name);
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
                return _builder.BuildCall2(signature.Type, signature.Value, callArguments, name);

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
            return _builder.BuildCall2(signature.Type, function, callArguments, name);
        }

        private LLVMValueRef EmitIndexAddress(BoundIndexExpression expression)
        {
            LLVMValueRef pointer = EmitExpression(expression.Receiver);
            if (expression.Receiver.Type is ArrayTypeSymbol arrayType)
            {
                EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, pointer, LLVMValueRef.CreateConstPointerNull(pointer.TypeOf), "array.valid"));
                LLVMValueRef linear = SizeConstant(0);
                for (int i = 0; i < expression.Indices.Length; i++)
                {
                    BoundExpression argument = expression.Indices[i];
                    LLVMValueRef value = EmitExpression(argument);
                    LLVMValueRef index = ConvertIntegerToSize(value, argument.Type);
                    if (_getIntegerBitWidth(argument.Type) > _getIntegerBitWidth(BuiltinTypes.NUInt))
                        EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULE, value, LLVMValueRef.CreateConstInt(value.TypeOf, uint.MaxValue), "array.index.fits"));
                    LLVMValueRef dimension = ConvertIntegerToSize(ReadDimension(pointer, IntConstant(i)), BuiltinTypes.Int);
                    EmitRuntimeCheck(_builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, index, dimension, "array.index.inrange"));
                    linear = _builder.BuildAdd(_builder.BuildMul(linear, dimension), index, "array.linear.index");
                }
                return _builder.BuildGEP2(_mapType(arrayType.ElementType), ArrayData(pointer, arrayType), new LLVMValueRef[] { linear }, "element.address");
            }
            LLVMValueRef pointerIndex = EmitExpression(expression.Index);
            TypeSymbol elementType = expression.Receiver.Type switch
            {
                ArrayTypeSymbol array => array.ElementType,
                PointerTypeSymbol pointerType => pointerType.ElementType,
                _ => expression.ElementType,
            };
            return _builder.BuildGEP2(
                _mapType(elementType),
                pointer,
                new LLVMValueRef[] { pointerIndex },
                "element.address");
        }

        private LLVMValueRef EmitMemberAddress(BoundMemberAccessExpression expression)
        {
            LLVMValueRef receiverAddress;
            if (expression.IsPointerAccess)
            {
                receiverAddress = EmitExpression(expression.Receiver);
            }
            else
            {
                receiverAddress = EmitAddress(expression.Receiver);
            }

            return _builder.BuildStructGEP2(
                _mapType(expression.Field.ContainingType),
                receiverAddress,
                checked((uint)expression.Field.Ordinal),
                $"{expression.Field.Name}.address");
        }

        private static bool IsAddressable(BoundExpression expression) => expression switch
        {
            BoundVariableExpression => true,
            BoundStaticFieldExpression => true,
            BoundUnaryExpression { OperatorKind: SyntaxKind.StarToken } => true,
            BoundReferenceDereferenceExpression => true,
            BoundMemberAccessExpression { IsPointerAccess: true } => true,
            BoundMemberAccessExpression member => IsAddressable(member.Receiver),
            BoundIndexExpression => true,
            _ => false,
        };

        private LLVMValueRef EmitMethodCall(BoundMethodCallExpression expression)
        {
            LlvmFunction function = _functions[expression.Method];
            var arguments = new LLVMValueRef[expression.Arguments.Length + 1];
            arguments[0] = EmitMethodReceiverAddress(expression);

            for (int index = 0; index < expression.Arguments.Length; index++)
            {
                arguments[index + 1] = EmitExpression(expression.Arguments[index]);
            }

            string name = ReferenceEquals(expression.Type, BuiltinTypes.Void) ? string.Empty : "method.call";
            return _builder.BuildCall2(function.Type, function.Value, arguments, name);
        }

        private LLVMValueRef EmitPropertySet(BoundPropertySetExpression expression)
        {
            FunctionSymbol setter = expression.Property.Setter
                ?? throw new LlvmCodeGenerationException($"property '{expression.Property.Name}' has no setter");
            LLVMValueRef receiver = EmitInstanceReceiverAddress(
                expression.Receiver,
                expression.IsPointerAccess,
                setter.ContainingType!,
                expression.Property.Name);
            LLVMValueRef value = EmitExpression(expression.Value);

            if (setter.VTableSlot is not int slot)
            {
                LlvmFunction function = _functions[setter];
                _builder.BuildCall2(function.Type, function.Value, new[] { receiver, value }, string.Empty);
                return value;
            }

            StructTypeSymbol receiverType = expression.IsPointerAccess
                ? (StructTypeSymbol)((PointerTypeSymbol)expression.Receiver.Type).ElementType
                : (StructTypeSymbol)expression.Receiver.Type;
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
            LlvmFunction signature = _functions[setter];
            LLVMValueRef target = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), functionAddress, "virtual.method");
            _builder.BuildCall2(signature.Type, target, new[] { receiver, value }, string.Empty);
            return value;
        }

        private LLVMValueRef EmitIndexerSet(BoundIndexerSetExpression expression)
        {
            FunctionSymbol setter = expression.Indexer.Setter
                ?? throw new LlvmCodeGenerationException("indexer has no setter");
            LLVMValueRef receiver = EmitInstanceReceiverAddress(
                expression.Receiver,
                isPointerAccess: false,
                setter.ContainingType!,
                "Item");
            var arguments = new LLVMValueRef[expression.Arguments.Length + 2];
            arguments[0] = receiver;
            for (int index = 0; index < expression.Arguments.Length; index++)
                arguments[index + 1] = EmitExpression(expression.Arguments[index]);
            LLVMValueRef value = EmitExpression(expression.Value);
            arguments[^1] = value;

            if (setter.VTableSlot is not int slot)
            {
                LlvmFunction function = _functions[setter];
                _builder.BuildCall2(function.Type, function.Value, arguments, string.Empty);
                return value;
            }

            var receiverType = (StructTypeSymbol)expression.Receiver.Type;
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
            LlvmFunction signature = _functions[setter];
            LLVMValueRef target = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), functionAddress, "virtual.method");
            _builder.BuildCall2(signature.Type, target, arguments, string.Empty);
            return value;
        }

        private LLVMValueRef EmitVirtualMethodCall(BoundMethodCallExpression expression)
        {
            StructTypeSymbol receiverType = expression.IsPointerAccess
                ? (StructTypeSymbol)((PointerTypeSymbol)expression.Receiver.Type).ElementType
                : (StructTypeSymbol)expression.Receiver.Type;
            if (!_virtualTables.TryGetValue(receiverType, out LlvmVTable vtable))
                throw new LlvmCodeGenerationException($"struct '{receiverType.Name}' has no virtual method table.");

            LLVMValueRef receiver = EmitMethodReceiverAddress(expression);
            LLVMValueRef vtablePointer = EmitRuntimeDispatch(receiverType, receiver);
            LLVMValueRef functionAddress = _builder.BuildGEP2(
                vtable.Type,
                vtablePointer,
                new LLVMValueRef[] { LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)expression.Method.VTableSlot!.Value + 1, false) },
                "virtual.slot");
            LlvmFunction signature = _functions[expression.Method];
            LLVMValueRef target = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), functionAddress, "virtual.method");
            var arguments = new LLVMValueRef[expression.Arguments.Length + 1];
            arguments[0] = receiver;
            for (int index = 0; index < expression.Arguments.Length; index++)
                arguments[index + 1] = EmitExpression(expression.Arguments[index]);
            return _builder.BuildCall2(signature.Type, target, arguments, ReferenceEquals(expression.Type, BuiltinTypes.Void) ? string.Empty : "virtual.call");
        }

        private LLVMValueRef EmitMethodReceiverAddress(BoundMethodCallExpression expression)
        {
            return EmitInstanceReceiverAddress(
                expression.Receiver,
                expression.IsPointerAccess,
                expression.Method.ContainingType!,
                expression.Method.Name);
        }

        private LLVMValueRef EmitInstanceReceiverAddress(
            BoundExpression receiver,
            bool isPointerAccess,
            StructTypeSymbol containingType,
            string memberName)
        {
            if (isPointerAccess)
                return EmitExpression(receiver);

            if (IsAddressable(receiver))
                return EmitAddress(receiver);

            LLVMValueRef temporary = _builder.BuildAlloca(
                _mapType(receiver.Type),
                $"{containingType.Name}.{memberName}.tmp");
            _builder.BuildStore(EmitExpression(receiver), temporary);
            return temporary;
        }

        private LLVMValueRef EmitCall(BoundCallExpression expression)
        {
            LlvmFunction function = _functions[expression.Function];
            LLVMValueRef[] arguments = expression.Arguments.Select(EmitExpression).ToArray();
            string name = ReferenceEquals(expression.Type, BuiltinTypes.Void) ? string.Empty : "call";
            return _builder.BuildCall2(function.Type, function.Value, arguments, name);
        }

        private LLVMValueRef GetAddress(VariableSymbol variable)
        {
            if (!_addresses.TryGetValue(variable, out LLVMValueRef address))
            {
                throw new LlvmCodeGenerationException($"No storage was allocated for variable '{variable.Name}'.");
            }

            return address;
        }

        private static SyntaxKind GetBinaryOperatorForCompoundAssignment(SyntaxKind kind) => kind switch
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
            _ => throw new LlvmCodeGenerationException($"Invalid compound assignment operator '{kind}'."),
        };

        private readonly record struct CleanupScope(LLVMValueRef Stack, LLVMValueRef Head);
        private readonly record struct BranchTarget(LLVMBasicBlockRef Block, int RetainedDepth);
        private readonly record struct LoopTargets(BranchTarget ContinueTarget);
    }
}
