using System.Collections.Immutable;
using LLVMSharp.Interop;
using Xenon.Compiler;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.CodeGen.LLVM;

public sealed class LlvmIrGenerator
{
    private readonly Dictionary<FunctionSymbol, LlvmFunction> _functions = [];
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

            DeclareInterfaceTypes(compilation.SemanticModel.GlobalNamespace);
            DeclareStructTypes(compilation.SemanticModel.GlobalNamespace);
            DeclareStaticFields(compilation.SemanticModel.GlobalNamespace);
            DeclareFunctions(compilation.SemanticModel.GlobalNamespace);
            DeclareVirtualTables(compilation.SemanticModel.GlobalNamespace);
            DeclareInterfaceTables(compilation.SemanticModel.GlobalNamespace);
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
            LLVMTypeRef[] fields = type.HasVirtualDispatch
                ? [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), .. type.AllInstanceFields.Select(field => MapType(field.Type))]
                : type.AllInstanceFields.Select(field => MapType(field.Type)).ToArray();
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
        foreach (StructTypeSymbol type in @namespace.Types.Where(type => !type.VirtualMethods.IsEmpty))
        {
            LLVMTypeRef elementType = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            LLVMTypeRef tableType = LLVMTypeRef.CreateArray(elementType, (uint)type.VirtualMethods.Length);
            LLVMValueRef table = _module.AddGlobal(tableType, $"{type.FullName}.__vtable");
            table.Linkage = LLVMLinkage.LLVMInternalLinkage;
            LLVMValueRef[] entries = type.VirtualMethods
                .Select(method => _functions[method].Value)
                .ToArray();
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
            return type is PointerTypeSymbol ? LLVMValueRef.CreateConstPointerNull(llvmType) : LLVMValueRef.CreateConstNull(llvmType);
        if (ReferenceEquals(type, BuiltinTypes.Bool))
            return LLVMValueRef.CreateConstInt(llvmType, value is true ? 1UL : 0UL, false);
        if (type is PrimitiveTypeSymbol { IsInteger: true })
            return LLVMValueRef.CreateConstInt(llvmType, GetIntegerConstantBits(value), false);
        if (type is PrimitiveTypeSymbol { IsFloatingPoint: true })
            return LLVMValueRef.CreateConstReal(llvmType, Convert.ToDouble(value));
        throw new LlvmCodeGenerationException($"static field type '{type.Name}' does not support a constant initializer");
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
        LLVMValueRef value = _module.AddFunction(NativeSymbolNames.Get(function), functionType);
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

        _functions.Add(function, new LlvmFunction(value, functionType));
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

    private int GetIntegerBitWidth(TypeSymbol type)
    {
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
        return _targetMachine.TargetData.OffsetOfElement(MapType(type), (uint)field.Ordinal);
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
            sizeType);
        return _memoryRuntime;
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
        LLVMTypeRef SizeType);

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
        private readonly Func<TypeSymbol, ulong> _getAbiSize;
        private readonly Func<TypeSymbol, uint> _getAbiAlignment;
        private readonly Func<StructTypeSymbol, FieldSymbol, ulong> _getFieldOffset;
        private readonly Func<TypeSymbol, int> _getIntegerBitWidth;
        private readonly LLVMValueRef _thisValue;
        private readonly Dictionary<VariableSymbol, LLVMValueRef> _addresses = [];
        private readonly Stack<LoopTargets> _loopTargets = [];
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
            foreach (BoundStatement statement in block.Statements)
            {
                if (_terminated)
                {
                    break;
                }

                EmitStatement(statement);
            }
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
                    EmitLoopBranch(_loopTargets.Peek().BreakTarget);
                    break;
                case BoundContinueStatement:
                    EmitLoopBranch(_loopTargets.Peek().ContinueTarget);
                    break;
                default:
                    throw new LlvmCodeGenerationException($"Bound statement '{statement.Kind}' is not supported by LLVM code generation.");
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
                EmitStatement(statement.ThenStatement);
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
            EmitStatement(statement.ThenStatement);
            bool thenFallsThrough = !_terminated;
            LLVMBasicBlockRef thenEnd = _builder.InsertBlock;

            _builder.PositionAtEnd(elseBlock);
            _terminated = false;
            EmitStatement(statement.ElseStatement);
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
            _loopTargets.Push(new LoopTargets(endBlock, conditionBlock));
            EmitStatement(statement.Body);
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
            _loopTargets.Push(new LoopTargets(endBlock, incrementBlock));
            EmitStatement(statement.Body);
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
        }

        private void EmitLoopBranch(LLVMBasicBlockRef target)
        {
            _builder.BuildBr(target);
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
            if (statement.Expression is null)
            {
                _builder.BuildRetVoid();
            }
            else
            {
                _builder.BuildRet(EmitExpression(statement.Expression));
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
                construction.Arguments),
            BoundConstructorCallExpression constructor => EmitConstructorCall(constructor),
            BoundBaseLifecycleCallExpression lifecycle => EmitLifecycleCall(lifecycle.Function, _thisValue, lifecycle.Arguments, initializeVTable: false),
            BoundArrayCreationExpression array => EmitArrayCreation(array),
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

            if (expression.Type is PrimitiveTypeSymbol { IsInteger: true })
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
                SyntaxKind.PlusPlusToken or SyntaxKind.MinusMinusToken => EmitIncrement(expression, operand),
                _ => throw new LlvmCodeGenerationException($"Unary operator '{expression.OperatorKind}' is not supported."),
            };
        }

        private LLVMValueRef EmitIncrement(BoundUnaryExpression expression, LLVMValueRef operand)
        {
            LLVMValueRef one = expression.Type is PrimitiveTypeSymbol { IsFloatingPoint: true }
                ? LLVMValueRef.CreateConstReal(_mapType(expression.Type), 1.0)
                : LLVMValueRef.CreateConstInt(_mapType(expression.Type), 1, false);
            LLVMValueRef result = expression.OperatorKind == SyntaxKind.PlusPlusToken
                ? EmitArithmetic(SyntaxKind.PlusToken, expression.Type, operand, one)
                : EmitArithmetic(SyntaxKind.MinusToken, expression.Type, operand, one);
            _builder.BuildStore(result, EmitAddress(expression.Operand));
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

            return EmitArithmetic(expression.OperatorKind, expression.Left.Type, left, right);
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
            LLVMValueRef right)
        {
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
                    SyntaxKind.BangEqualsToken => LLVMRealPredicate.LLVMRealONE,
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
                    value);
            }

            _builder.BuildStore(value, address);
            return value;
        }

        private LLVMValueRef EmitMemberAccess(BoundMemberAccessExpression expression)
        {
            if (!expression.IsPointerAccess && !IsAddressable(expression.Receiver))
            {
                return _builder.BuildExtractValue(
                    EmitExpression(expression.Receiver),
                    checked((uint)expression.Field.Ordinal),
                    expression.Field.Name);
            }

            LLVMValueRef address = EmitAddress(expression);
            return _builder.BuildLoad2(_mapType(expression.Type), address, expression.Field.Name);
        }

        private LLVMValueRef EmitStructConstruction(
            StructTypeSymbol structType,
            ImmutableArray<BoundExpression> arguments)
        {
            if (structType.AllInstanceFields.Any(field => field.Initializer is not null))
            {
                LLVMValueRef address = _builder.BuildAlloca(_mapType(structType), $"{structType.Name}.init.tmp");
                if (structType.HasVirtualDispatch && _virtualTables.TryGetValue(structType, out LlvmVTable initializedVTable))
                {
                    LLVMValueRef vtableAddress = _builder.BuildStructGEP2(_mapType(structType), address, 0, "vtable.address");
                    _builder.BuildStore(initializedVTable.Value, vtableAddress);
                }

                EmitDefaultInstanceInitialization(structType, address);
                for (int index = 0; index < arguments.Length; index++)
                {
                    FieldSymbol field = structType.AllInstanceFields[index];
                    LLVMValueRef fieldAddress = _builder.BuildStructGEP2(
                        _mapType(structType),
                        address,
                        checked((uint)field.Ordinal),
                        $"{field.Name}.address");
                    _builder.BuildStore(EmitExpression(arguments[index]), fieldAddress);
                }

                return _builder.BuildLoad2(_mapType(structType), address, $"{structType.Name}.value");
            }

            LLVMValueRef value = _mapType(structType).Poison;
            if (structType.HasVirtualDispatch && _virtualTables.TryGetValue(structType, out LlvmVTable vtable))
            {
                value = _builder.BuildInsertValue(value, vtable.Value, 0, "vtable.init");
            }
            for (int index = 0; index < arguments.Length; index++)
            {
                value = _builder.BuildInsertValue(
                    value,
                    EmitExpression(arguments[index]),
                    checked((uint)structType.AllInstanceFields[index].Ordinal),
                    $"{structType.AllInstanceFields[index].Name}.init");
            }

            return value;
        }

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
            EmitLifecycleCall(expression.Constructor, address, expression.Arguments);
            return _builder.BuildLoad2(_mapType(expression.StructType), address, $"{expression.StructType.Name}.value");
        }

        private LLVMValueRef EmitArrayCreation(BoundArrayCreationExpression expression)
        {
            LLVMValueRef length = ConvertIntegerToSize(EmitExpression(expression.Length), expression.Length.Type);
            if (expression.Storage == ArrayStorageKind.Stack)
            {
                return _builder.BuildArrayAlloca(_mapType(expression.ElementType), length, $"{expression.ElementType.Name}.stack.array");
            }

            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef elementSize = LLVMValueRef.CreateConstInt(
                runtime.SizeType,
                _getAbiSize(expression.ElementType),
                false);
            LLVMValueRef byteCount = _builder.BuildMul(length, elementSize, "array.bytes");
            return _builder.BuildCall2(
                runtime.MallocType,
                runtime.Malloc,
                new LLVMValueRef[] { byteCount },
                $"{expression.ElementType.Name}.heap.array");
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
                _getAbiSize(expression.StructType),
                false);
            LLVMValueRef address = _builder.BuildCall2(
                runtime.MallocType,
                runtime.Malloc,
                new LLVMValueRef[] { size },
                $"{expression.StructType.Name}.heap");

            if (expression.IsPositionalInitialization)
            {
                LLVMValueRef value = EmitStructConstruction(expression.StructType, expression.Arguments);
                _builder.BuildStore(value, address);
            }
            else
            {
                EmitLifecycleCall(expression.Constructor!, address, expression.Arguments);
            }

            return address;
        }

        private LLVMValueRef EmitFree(BoundFreeExpression expression)
        {
            LlvmMemoryRuntime runtime = _getMemoryRuntime();
            LLVMValueRef address = EmitExpression(expression.Pointer);
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

            return _builder.BuildCall2(
                runtime.FreeType,
                runtime.Free,
                new LLVMValueRef[] { address },
                string.Empty);
        }

        private void EmitVirtualDestructor(FunctionSymbol destructor, LLVMValueRef address, LlvmVTable vtable, int slot)
        {
            StructTypeSymbol staticType = destructor.ContainingType!;
            LLVMValueRef vtableAddress = _builder.BuildStructGEP2(_mapType(staticType), address, 0, "vtable.address");
            LLVMValueRef vtablePointer = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), vtableAddress, "vtable");
            LLVMValueRef functionAddress = _builder.BuildGEP2(vtable.Type, vtablePointer,
                new LLVMValueRef[] { LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)slot, false) },
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
                LLVMValueRef vtableAddress = _builder.BuildStructGEP2(_mapType(type), thisAddress, 0, "vtable.address");
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
            bool sourceInteger = expression.Expression.Type is PrimitiveTypeSymbol { IsInteger: true };
            bool targetInteger = expression.TargetType is PrimitiveTypeSymbol { IsInteger: true };
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
                bool signed = expression.Expression.Type is PrimitiveTypeSymbol { IsSigned: true };
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
            if (!_interfaceMaps.TryGetValue(expression.SourceType, out LlvmVTable map))
                throw new LlvmCodeGenerationException($"struct '{expression.SourceType.Name}' has no table for interface '{expression.InterfaceType.Name}'.");

            LLVMValueRef data = IsAddressable(expression.Source)
                ? EmitAddress(expression.Source)
                : StoreTemporary(expression.Source, expression.SourceType);
            LLVMValueRef value = _mapType(expression.InterfaceType).Poison;
            value = _builder.BuildInsertValue(value, data, 0, "interface.data");
            return _builder.BuildInsertValue(value, map.Value, 1, "interface.map");
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
                LLVMValueRef result = EmitArithmetic(expression.OperatorKind, expression.Type, current, value);
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
                instanceValue);
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
            LLVMValueRef vtableAddress = _builder.BuildStructGEP2(
                _mapType(receiverType),
                receiver,
                0,
                "vtable.address");
            LLVMValueRef vtablePointer = _builder.BuildLoad2(
                LLVMTypeRef.CreatePointer(_context.Int8Type, 0),
                vtableAddress,
                "vtable");
            LLVMValueRef functionAddress = _builder.BuildGEP2(
                vtable.Type,
                vtablePointer,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)slot, false),
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
            LLVMValueRef index = EmitExpression(expression.Index);
            TypeSymbol elementType = expression.Receiver.Type switch
            {
                ArrayTypeSymbol array => array.ElementType,
                PointerTypeSymbol pointerType => pointerType.ElementType,
                _ => expression.ElementType,
            };
            LLVMValueRef pointer = EmitExpression(expression.Receiver);
            return _builder.BuildGEP2(
                _mapType(elementType),
                pointer,
                new LLVMValueRef[] { index },
                "element.address");
        }

        private LLVMValueRef EmitMemberAddress(BoundMemberAccessExpression expression)
        {
            StructTypeSymbol structType;
            LLVMValueRef receiverAddress;
            if (expression.IsPointerAccess)
            {
                var pointer = (PointerTypeSymbol)expression.Receiver.Type;
                structType = (StructTypeSymbol)pointer.ElementType;
                receiverAddress = EmitExpression(expression.Receiver);
            }
            else
            {
                structType = (StructTypeSymbol)expression.Receiver.Type;
                receiverAddress = EmitAddress(expression.Receiver);
            }

            return _builder.BuildStructGEP2(
                _mapType(structType),
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

            LLVMValueRef vtableAddress = _builder.BuildStructGEP2(_mapType(receiverType), receiver, 0, "vtable.address");
            LLVMValueRef vtablePointer = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), vtableAddress, "vtable");
            LLVMValueRef functionAddress = _builder.BuildGEP2(
                vtable.Type,
                vtablePointer,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)slot, false),
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
            LLVMValueRef vtableAddress = _builder.BuildStructGEP2(_mapType(receiverType), receiver, 0, "vtable.address");
            LLVMValueRef vtablePointer = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), vtableAddress, "vtable");
            LLVMValueRef functionAddress = _builder.BuildGEP2(
                vtable.Type,
                vtablePointer,
                new LLVMValueRef[]
                {
                    LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false),
                    LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)slot, false),
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
            LLVMValueRef vtableAddress = _builder.BuildStructGEP2(_mapType(receiverType), receiver, 0, "vtable.address");
            LLVMValueRef vtablePointer = _builder.BuildLoad2(LLVMTypeRef.CreatePointer(_context.Int8Type, 0), vtableAddress, "vtable");
            LLVMValueRef functionAddress = _builder.BuildGEP2(
                vtable.Type,
                vtablePointer,
                new LLVMValueRef[] { LLVMValueRef.CreateConstInt(_context.Int32Type, 0, false), LLVMValueRef.CreateConstInt(_context.Int32Type, (ulong)expression.Method.VTableSlot!.Value, false) },
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

        private readonly record struct LoopTargets(
            LLVMBasicBlockRef BreakTarget,
            LLVMBasicBlockRef ContinueTarget);
    }
}
