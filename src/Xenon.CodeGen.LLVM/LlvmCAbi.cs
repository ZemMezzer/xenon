using System.Collections.Immutable;
using LLVMSharp.Interop;
using LLVMApi = LLVMSharp.Interop.LLVM;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

/// <summary>
/// Target-specific C aggregate classification. Xenon keeps its ordinary aggregate
/// representation internally; these plans describe only the native call boundary.
/// </summary>
internal sealed class LlvmCAbi
{
    private readonly LLVMContextRef _context;
    private readonly NativeTargetMachine _target;
    private readonly LlvmTypeLayout _layout;
    private readonly Func<TypeSymbol, LLVMTypeRef> _mapType;
    private readonly LlvmCAbiKind _kind;

    public LlvmCAbi(
        LLVMContextRef context,
        NativeTargetMachine target,
        LlvmTypeLayout layout,
        Func<TypeSymbol, LLVMTypeRef> mapType)
    {
        _context = context;
        _target = target;
        _layout = layout;
        _mapType = mapType;
        _kind = ClassifyTarget(target.Triple);
    }

    public static bool RequiresLowering(TypeSymbol returnType, IEnumerable<TypeSymbol> parameterTypes) =>
        returnType is StructTypeSymbol || parameterTypes.Any(type => type is StructTypeSymbol);

    public bool SupportsStructValues => _kind != LlvmCAbiKind.Unsupported;

    public LlvmCAbiFunctionPlan Classify(TypeSymbol returnType, IEnumerable<TypeSymbol> parameterTypes)
    {
        LlvmCAbiReturnPlan result = ClassifyReturn(returnType);
        ImmutableArray<TypeSymbol> logicalParameters = [.. parameterTypes];
        ImmutableArray<LlvmCAbiParameterPlan> parameters = _kind == LlvmCAbiKind.SysVAmd64
            ? ClassifySysVParameters(result, logicalParameters)
            : [.. logicalParameters.Select(ClassifyParameter)];
        var physical = ImmutableArray.CreateBuilder<LLVMTypeRef>();
        if (result.IsIndirect)
            physical.Add(LLVMTypeRef.CreatePointer(result.LogicalLlvmType, 0));
        foreach (LlvmCAbiParameterPlan parameter in parameters)
            physical.AddRange(parameter.PhysicalTypes);
        LLVMTypeRef functionType = LLVMTypeRef.CreateFunction(
            result.IsIndirect ? _context.VoidType : result.PhysicalType,
            physical.ToArray(),
            false);
        return new LlvmCAbiFunctionPlan(result, parameters, physical.ToImmutable(), functionType);
    }

    private ImmutableArray<LlvmCAbiParameterPlan> ClassifySysVParameters(
        LlvmCAbiReturnPlan result,
        ImmutableArray<TypeSymbol> parameterTypes)
    {
        const int IntegerRegisterCount = 6;
        const int SseRegisterCount = 8;

        // An indirect result is passed in RDI and therefore participates in the
        // integer argument-register budget.
        int integerRegisters = result.IsIndirect ? 1 : 0;
        int sseRegisters = 0;
        var parameters = ImmutableArray.CreateBuilder<LlvmCAbiParameterPlan>(parameterTypes.Length);

        foreach (TypeSymbol type in parameterTypes)
        {
            LlvmCAbiParameterPlan parameter = ClassifyParameter(type);
            if (type is StructTypeSymbol && !parameter.IsIndirect)
            {
                int requiredInteger = parameter.Components.Count(
                    component => component.Class == LlvmCAbiLeafClass.Integer);
                int requiredSse = parameter.Components.Length - requiredInteger;

                // SysV requires an aggregate to be assigned atomically. If even
                // one of its eightbytes cannot get the required register, undo the
                // tentative assignment and pass the complete value in memory.
                if (integerRegisters + requiredInteger > IntegerRegisterCount ||
                    sseRegisters + requiredSse > SseRegisterCount)
                {
                    parameter = LlvmCAbiParameterPlan.Indirect(
                        parameter.LogicalType,
                        parameter.LogicalLlvmType,
                        parameter.Size,
                        parameter.Alignment,
                        byValue: true);
                }
                else
                {
                    integerRegisters += requiredInteger;
                    sseRegisters += requiredSse;
                }
            }
            else if (!parameter.IsIndirect)
            {
                if (IsSysVSseScalar(type))
                    sseRegisters = Math.Min(SseRegisterCount, sseRegisters + 1);
                else
                    integerRegisters = Math.Min(IntegerRegisterCount, integerRegisters + 1);
            }

            parameters.Add(parameter);
        }

        return parameters.ToImmutable();
    }

    private static bool IsSysVSseScalar(TypeSymbol type) =>
        TypeIdentity.AreSame(type, BuiltinTypes.Float) ||
        TypeIdentity.AreSame(type, BuiltinTypes.Double);

    private LlvmCAbiReturnPlan ClassifyReturn(TypeSymbol type)
    {
        LLVMTypeRef logical = _mapType(type);
        if (type is not StructTypeSymbol structure)
            return LlvmCAbiReturnPlan.Identity(type, logical);

        ulong size = RequireNonEmpty(structure);
        uint alignment = _layout.GetAlignment(structure);
        return _kind switch
        {
            LlvmCAbiKind.WindowsX64 when size is 1 or 2 or 4 or 8 =>
                LlvmCAbiReturnPlan.Coerced(type, logical, IntegerForBytes(size), size, alignment),
            LlvmCAbiKind.WindowsX64 =>
                LlvmCAbiReturnPlan.Indirect(type, logical, size, alignment),
            LlvmCAbiKind.SysVAmd64 => ClassifySysVReturn(structure, logical, size, alignment),
            LlvmCAbiKind.AArch64Linux or
            LlvmCAbiKind.AArch64Apple or
            LlvmCAbiKind.AArch64Windows =>
                ClassifyAArch64Return(structure, logical, size, alignment),
            _ => throw new LlvmCodeGenerationException($"Unsupported C ABI target '{_target.Triple}'."),
        };
    }

    private LlvmCAbiParameterPlan ClassifyParameter(TypeSymbol type)
    {
        LLVMTypeRef logical = _mapType(type);
        if (type is not StructTypeSymbol structure)
            return LlvmCAbiParameterPlan.Identity(type, logical);

        ulong size = RequireNonEmpty(structure);
        uint alignment = _layout.GetAlignment(structure);
        return _kind switch
        {
            LlvmCAbiKind.WindowsX64 when size is 1 or 2 or 4 or 8 =>
                LlvmCAbiParameterPlan.Coerced(type, logical, IntegerForBytes(size), size, alignment),
            LlvmCAbiKind.WindowsX64 =>
                LlvmCAbiParameterPlan.Indirect(type, logical, size, alignment, byValue: false),
            LlvmCAbiKind.SysVAmd64 => ClassifySysVParameter(structure, logical, size, alignment),
            LlvmCAbiKind.AArch64Linux or
            LlvmCAbiKind.AArch64Apple or
            LlvmCAbiKind.AArch64Windows =>
                ClassifyAArch64Parameter(structure, logical, size, alignment),
            _ => throw new LlvmCodeGenerationException($"Unsupported C ABI target '{_target.Triple}'."),
        };
    }

    private LlvmCAbiReturnPlan ClassifySysVReturn(
        StructTypeSymbol type, LLVMTypeRef logical, ulong size, uint alignment)
    {
        ImmutableArray<LlvmCAbiComponent> components = ClassifySysVComponents(type, size);
        if (components.IsDefaultOrEmpty)
            return LlvmCAbiReturnPlan.Indirect(type, logical, size, alignment);
        LLVMTypeRef physical = components.Length == 1
            ? components[0].Type
            : _context.GetStructType([.. components.Select(component => component.Type)], false);
        return LlvmCAbiReturnPlan.Coerced(type, logical, physical, size, alignment, components);
    }

    private LlvmCAbiParameterPlan ClassifySysVParameter(
        StructTypeSymbol type, LLVMTypeRef logical, ulong size, uint alignment)
    {
        ImmutableArray<LlvmCAbiComponent> components = ClassifySysVComponents(type, size);
        return components.IsDefaultOrEmpty
            ? LlvmCAbiParameterPlan.Indirect(type, logical, size, alignment, byValue: true)
            : LlvmCAbiParameterPlan.Flattened(type, logical, size, alignment, components);
    }

    private ImmutableArray<LlvmCAbiComponent> ClassifySysVComponents(StructTypeSymbol type, ulong size)
    {
        if (size > 16) return [];
        ImmutableArray<LlvmCAbiLeaf> leaves = GetLeaves(type);
        int count = checked((int)((size + 7) / 8));
        var result = ImmutableArray.CreateBuilder<LlvmCAbiComponent>(count);
        for (int chunk = 0; chunk < count; chunk++)
        {
            ulong offset = checked((ulong)chunk * 8);
            ulong chunkSize = Math.Min(8, size - offset);
            LlvmCAbiLeaf[] present = leaves
                .Where(leaf => leaf.Offset < offset + chunkSize && leaf.Offset + leaf.Size > offset)
                .ToArray();
            bool integer = present.Any(leaf => leaf.Class == LlvmCAbiLeafClass.Integer);
            LLVMTypeRef physical = integer
                ? SysVIntegerComponent(present, offset, chunkSize)
                : SysVSseComponent(present, offset, chunkSize);
            result.Add(new LlvmCAbiComponent(
                physical,
                offset,
                integer ? LlvmCAbiLeafClass.Integer : LlvmCAbiLeafClass.Sse));
        }
        return result.ToImmutable();
    }

    private LLVMTypeRef SysVIntegerComponent(LlvmCAbiLeaf[] leaves, ulong offset, ulong size)
    {
        if (leaves.Length == 1 && leaves[0] is { Class: LlvmCAbiLeafClass.Integer } leaf &&
            leaf.Offset == offset)
            return leaf.IsPointer ? _mapType(leaf.Type) : ExactIntegerForBytes(leaf.Size);
        return ExactIntegerForBytes(size);
    }

    private LLVMTypeRef SysVSseComponent(LlvmCAbiLeaf[] leaves, ulong offset, ulong size)
    {
        LlvmCAbiLeaf[] floating = leaves
            .Where(leaf => leaf.Class == LlvmCAbiLeafClass.Sse)
            .OrderBy(leaf => leaf.Offset)
            .ToArray();
        if (floating.Length == 1 && TypeIdentity.AreSame(floating[0].Type, BuiltinTypes.Double))
            return _context.DoubleType;
        if (floating.Length == 1 && TypeIdentity.AreSame(floating[0].Type, BuiltinTypes.Float) &&
            floating[0].Offset == offset)
            return _context.FloatType;
        if (floating.Length == 2 && floating.All(leaf => TypeIdentity.AreSame(leaf.Type, BuiltinTypes.Float)) &&
            floating[0].Offset == offset && floating[1].Offset == offset + 4)
            return LLVMTypeRef.CreateVector(_context.FloatType, 2);
        // Natural Xenon layouts only reach this fallback through padding-heavy nested
        // aggregates. A 64-bit vector preserves the required SSE register class.
        return LLVMTypeRef.CreateVector(_context.Int8Type, checked((uint)size));
    }

    private LlvmCAbiReturnPlan ClassifyAArch64Return(
        StructTypeSymbol type, LLVMTypeRef logical, ulong size, uint alignment)
    {
        if (TryGetHomogeneousFloatAggregate(type, out PrimitiveTypeSymbol? element, out int count))
            return LlvmCAbiReturnPlan.Coerced(type, logical, logical, size, alignment,
                [new LlvmCAbiComponent(logical, 0)]);
        if (size > 16)
            return LlvmCAbiReturnPlan.Indirect(type, logical, size, alignment);
        LLVMTypeRef physical = size <= 8
            ? ExactIntegerForBytes(size)
            : LLVMTypeRef.CreateArray(_context.Int64Type, 2);
        return LlvmCAbiReturnPlan.Coerced(type, logical, physical, size, alignment);
    }

    private LlvmCAbiParameterPlan ClassifyAArch64Parameter(
        StructTypeSymbol type, LLVMTypeRef logical, ulong size, uint alignment)
    {
        if (TryGetHomogeneousFloatAggregate(type, out PrimitiveTypeSymbol? element, out int count))
        {
            LLVMTypeRef physical = LLVMTypeRef.CreateArray(_mapType(element), checked((uint)count));
            return LlvmCAbiParameterPlan.Coerced(
                type,
                logical,
                physical,
                size,
                alignment,
                stackAlignment: _kind == LlvmCAbiKind.AArch64Linux ? 8u : 0u);
        }
        if (size > 16)
            return LlvmCAbiParameterPlan.Indirect(type, logical, size, alignment, byValue: false);
        LLVMTypeRef integer = size <= 8
            ? _context.Int64Type
            : LLVMTypeRef.CreateArray(_context.Int64Type, 2);
        return LlvmCAbiParameterPlan.Coerced(type, logical, integer, size, alignment);
    }

    private bool TryGetHomogeneousFloatAggregate(
        StructTypeSymbol type, out PrimitiveTypeSymbol element, out int count)
    {
        ImmutableArray<LlvmCAbiLeaf> leaves = GetLeaves(type);
        LlvmCAbiLeaf[] floating = leaves
            .Where(leaf => leaf.Class == LlvmCAbiLeafClass.Sse)
            .ToArray();
        if (floating.Length is >= 1 and <= 4 && floating.Length == leaves.Length &&
            floating[0].Type is PrimitiveTypeSymbol first &&
            (TypeIdentity.AreSame(first, BuiltinTypes.Float) || TypeIdentity.AreSame(first, BuiltinTypes.Double)) &&
            floating.All(leaf => TypeIdentity.AreSame(leaf.Type, first)))
        {
            element = first;
            count = floating.Length;
            return true;
        }
        element = BuiltinTypes.Float;
        count = 0;
        return false;
    }

    private ImmutableArray<LlvmCAbiLeaf> GetLeaves(StructTypeSymbol type)
    {
        var leaves = ImmutableArray.CreateBuilder<LlvmCAbiLeaf>();
        AddLeaves(type, 0, leaves);
        return leaves.ToImmutable();
    }

    private void AddLeaves(TypeSymbol type, ulong offset, ImmutableArray<LlvmCAbiLeaf>.Builder leaves)
    {
        if (type is EnumTypeSymbol enumeration)
        {
            AddLeaves(enumeration.UnderlyingType, offset, leaves);
            return;
        }
        if (type is StructTypeSymbol structure)
        {
            if (structure.BaseType is not null) AddLeaves(structure.BaseType, offset, leaves);
            foreach (FieldSymbol field in structure.Fields)
                AddLeaves(field.Type, checked(offset + _layout.GetFieldOffset(structure, field)), leaves);
            return;
        }
        ulong size = _layout.GetSize(type);
        bool floating = TypeIdentity.AreSame(type, BuiltinTypes.Float) ||
            TypeIdentity.AreSame(type, BuiltinTypes.Double);
        bool pointer = type is PointerTypeSymbol or FunctionPointerTypeSymbol;
        leaves.Add(new LlvmCAbiLeaf(
            type,
            offset,
            size,
            floating ? LlvmCAbiLeafClass.Sse : LlvmCAbiLeafClass.Integer,
            pointer));
    }

    private ulong RequireNonEmpty(StructTypeSymbol type)
    {
        if (TypeFacts.GetCAbiStructIncompatibility(type) is { } failure)
            throw new LlvmCodeGenerationException(
                $"C ABI cannot pass struct '{type.FullName}' by value because {failure}.");
        ulong size = _layout.GetSize(type);
        if (size == 0)
            throw new LlvmCodeGenerationException(
                $"C ABI does not support empty Xenon struct '{type.FullName}' by value.");
        return size;
    }

    private LLVMTypeRef IntegerForBytes(ulong size) => size switch
    {
        <= 1 => _context.Int8Type,
        <= 2 => _context.Int16Type,
        <= 4 => _context.Int32Type,
        <= 8 => _context.Int64Type,
        _ => throw new LlvmCodeGenerationException($"Cannot coerce {size} bytes to one integer register."),
    };

    private LLVMTypeRef ExactIntegerForBytes(ulong size)
    {
        if (size is 0 or > 8)
            throw new LlvmCodeGenerationException($"Cannot coerce {size} bytes to one integer register.");
        return _context.GetIntType(checked((uint)(size * 8)));
    }

    private static LlvmCAbiKind ClassifyTarget(string triple)
    {
        bool windows = triple.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
            triple.Contains("win32", StringComparison.OrdinalIgnoreCase) ||
            triple.Contains("msvc", StringComparison.OrdinalIgnoreCase);
        if ((triple.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
             triple.Contains("amd64", StringComparison.OrdinalIgnoreCase)) && windows)
            return LlvmCAbiKind.WindowsX64;
        if (triple.Contains("x86_64", StringComparison.OrdinalIgnoreCase) ||
            triple.Contains("amd64", StringComparison.OrdinalIgnoreCase))
            return LlvmCAbiKind.SysVAmd64;
        bool aarch64 = triple.Contains("aarch64", StringComparison.OrdinalIgnoreCase) ||
            triple.Contains("arm64", StringComparison.OrdinalIgnoreCase);
        if (triple.Contains("arm64ec", StringComparison.OrdinalIgnoreCase))
            return LlvmCAbiKind.Unsupported;
        if (aarch64 && windows)
            return LlvmCAbiKind.AArch64Windows;
        if (aarch64 && (triple.Contains("apple", StringComparison.OrdinalIgnoreCase) ||
                       triple.Contains("darwin", StringComparison.OrdinalIgnoreCase) ||
                       triple.Contains("macos", StringComparison.OrdinalIgnoreCase) ||
                       triple.Contains("ios", StringComparison.OrdinalIgnoreCase) ||
                       triple.Contains("tvos", StringComparison.OrdinalIgnoreCase) ||
                       triple.Contains("watchos", StringComparison.OrdinalIgnoreCase)))
            return LlvmCAbiKind.AArch64Apple;
        if (aarch64 && (triple.Contains("linux", StringComparison.OrdinalIgnoreCase) ||
                       triple.Contains("android", StringComparison.OrdinalIgnoreCase)))
            return LlvmCAbiKind.AArch64Linux;
        return LlvmCAbiKind.Unsupported;
    }
}

internal enum LlvmCAbiKind
{
    WindowsX64,
    SysVAmd64,
    AArch64Linux,
    AArch64Apple,
    AArch64Windows,
    Unsupported,
}

internal enum LlvmCAbiLeafClass
{
    Integer,
    Sse,
}

internal readonly record struct LlvmCAbiLeaf(
    TypeSymbol Type,
    ulong Offset,
    ulong Size,
    LlvmCAbiLeafClass Class,
    bool IsPointer);

internal readonly record struct LlvmCAbiComponent(
    LLVMTypeRef Type,
    ulong Offset,
    LlvmCAbiLeafClass Class = LlvmCAbiLeafClass.Integer);

internal sealed record LlvmCAbiReturnPlan(
    TypeSymbol LogicalType,
    LLVMTypeRef LogicalLlvmType,
    LLVMTypeRef PhysicalType,
    ulong Size,
    uint Alignment,
    bool IsIndirect,
    ImmutableArray<LlvmCAbiComponent> Components)
{
    public bool IsIdentity => !IsIndirect && LogicalLlvmType == PhysicalType;

    public static LlvmCAbiReturnPlan Identity(TypeSymbol type, LLVMTypeRef llvmType) =>
        new(type, llvmType, llvmType, 0, 0, false, []);

    public static LlvmCAbiReturnPlan Coerced(
        TypeSymbol type, LLVMTypeRef logical, LLVMTypeRef physical, ulong size, uint alignment,
        ImmutableArray<LlvmCAbiComponent> components = default) =>
        new(type, logical, physical, size, alignment, false,
            components.IsDefault ? [new LlvmCAbiComponent(physical, 0)] : components);

    public static LlvmCAbiReturnPlan Indirect(
        TypeSymbol type, LLVMTypeRef logical, ulong size, uint alignment) =>
        new(type, logical, default, size, alignment, true, []);
}

internal sealed record LlvmCAbiParameterPlan(
    TypeSymbol LogicalType,
    LLVMTypeRef LogicalLlvmType,
    ImmutableArray<LLVMTypeRef> PhysicalTypes,
    ulong Size,
    uint Alignment,
    bool IsIndirect,
    bool IsByValue,
    uint StackAlignment,
    ImmutableArray<LlvmCAbiComponent> Components)
{
    public bool IsIdentity => !IsIndirect && PhysicalTypes.Length == 1 &&
        PhysicalTypes[0] == LogicalLlvmType;

    public static LlvmCAbiParameterPlan Identity(TypeSymbol type, LLVMTypeRef llvmType) =>
        new(type, llvmType, [llvmType], 0, 0, false, false, 0, []);

    public static LlvmCAbiParameterPlan Coerced(
        TypeSymbol type, LLVMTypeRef logical, LLVMTypeRef physical, ulong size, uint alignment,
        uint stackAlignment = 0) =>
        new(type, logical, [physical], size, alignment, false, false, stackAlignment,
            [new LlvmCAbiComponent(physical, 0)]);

    public static LlvmCAbiParameterPlan Flattened(
        TypeSymbol type, LLVMTypeRef logical, ulong size, uint alignment,
        ImmutableArray<LlvmCAbiComponent> components) =>
        new(type, logical, [.. components.Select(component => component.Type)], size, alignment,
            false, false, 0, components);

    public static LlvmCAbiParameterPlan Indirect(
        TypeSymbol type, LLVMTypeRef logical, ulong size, uint alignment, bool byValue) =>
        new(type, logical, [LLVMTypeRef.CreatePointer(logical, 0)], size, alignment,
            true, byValue, 0, []);
}

internal sealed record LlvmCAbiFunctionPlan(
    LlvmCAbiReturnPlan Return,
    ImmutableArray<LlvmCAbiParameterPlan> Parameters,
    ImmutableArray<LLVMTypeRef> PhysicalParameterTypes,
    LLVMTypeRef FunctionType);

/// <summary>Builds the value shuffles described by an <see cref="LlvmCAbiFunctionPlan"/>.</summary>
internal sealed unsafe class LlvmCAbiMarshaller
{
    private readonly LLVMContextRef _context;
    private readonly LLVMBuilderRef _builder;

    public LlvmCAbiMarshaller(LLVMContextRef context, LLVMBuilderRef builder)
    {
        _context = context;
        _builder = builder;
    }

    public ImmutableArray<LLVMValueRef> UnpackParameters(
        LLVMValueRef function,
        LlvmCAbiFunctionPlan plan)
    {
        var values = ImmutableArray.CreateBuilder<LLVMValueRef>(plan.Parameters.Length);
        uint physicalIndex = plan.Return.IsIndirect ? 1u : 0u;
        foreach (LlvmCAbiParameterPlan parameter in plan.Parameters)
        {
            if (parameter.IsIdentity)
            {
                values.Add(function.GetParam(physicalIndex++));
                continue;
            }
            if (parameter.IsIndirect)
            {
                values.Add(_builder.BuildLoad2(
                    parameter.LogicalLlvmType,
                    function.GetParam(physicalIndex++),
                    "abi.argument"));
                continue;
            }

            LLVMTypeRef storageType = GetParameterStorageType(parameter);
            LLVMValueRef storage = _builder.BuildAlloca(storageType, "abi.argument.storage");
            if (parameter.PhysicalTypes.Length == 1)
            {
                _builder.BuildStore(function.GetParam(physicalIndex++), storage);
            }
            else
            {
                for (uint component = 0; component < parameter.PhysicalTypes.Length; component++)
                    _builder.BuildStore(
                        function.GetParam(physicalIndex++),
                        _builder.BuildStructGEP2(storageType, storage, component));
            }
            values.Add(_builder.BuildLoad2(parameter.LogicalLlvmType, storage, "abi.argument"));
        }
        return values.ToImmutable();
    }

    public LLVMValueRef EmitCall(
        LLVMValueRef target,
        LlvmCAbiFunctionPlan plan,
        IReadOnlyList<LLVMValueRef> logicalArguments,
        string name)
    {
        var physical = new List<LLVMValueRef>();
        LLVMValueRef resultStorage = default;
        if (plan.Return.IsIndirect)
        {
            resultStorage = _builder.BuildAlloca(plan.Return.LogicalLlvmType, "abi.result.storage");
            physical.Add(resultStorage);
        }

        for (int index = 0; index < plan.Parameters.Length; index++)
            PackParameter(plan.Parameters[index], logicalArguments[index], physical);

        LLVMValueRef call = _builder.BuildCall2(
            plan.FunctionType,
            target,
            physical.ToArray(),
            plan.Return.IsIndirect || TypeIdentity.AreSame(plan.Return.LogicalType, BuiltinTypes.Void)
                ? string.Empty
                : name);
        ApplyCallSiteAttributes(call, plan);

        if (plan.Return.IsIndirect)
            return _builder.BuildLoad2(plan.Return.LogicalLlvmType, resultStorage, name);
        if (plan.Return.IsIdentity)
            return call;
        LLVMValueRef storage = _builder.BuildAlloca(plan.Return.PhysicalType, "abi.result.coerce");
        _builder.BuildStore(call, storage);
        return _builder.BuildLoad2(plan.Return.LogicalLlvmType, storage, name);
    }

    public void EmitReturn(
        LLVMValueRef physicalFunction,
        LlvmCAbiFunctionPlan plan,
        LLVMValueRef logicalResult)
    {
        if (plan.Return.IsIndirect)
        {
            _builder.BuildStore(logicalResult, physicalFunction.GetParam(0));
            _builder.BuildRetVoid();
            return;
        }
        if (TypeIdentity.AreSame(plan.Return.LogicalType, BuiltinTypes.Void))
        {
            _builder.BuildRetVoid();
            return;
        }
        if (plan.Return.IsIdentity)
        {
            _builder.BuildRet(logicalResult);
            return;
        }
        LLVMValueRef storage = _builder.BuildAlloca(plan.Return.PhysicalType, "abi.return.coerce");
        _builder.BuildStore(logicalResult, storage);
        _builder.BuildRet(_builder.BuildLoad2(plan.Return.PhysicalType, storage, "abi.return"));
    }

    public void ApplyFunctionAttributes(LLVMValueRef function, LlvmCAbiFunctionPlan plan) =>
        ApplyAttributes(plan, (index, attribute) => function.AddAttributeAtIndex(index, attribute));

    private void ApplyCallSiteAttributes(LLVMValueRef call, LlvmCAbiFunctionPlan plan) =>
        ApplyAttributes(plan, (index, attribute) => LLVMApi.AddCallSiteAttribute(call, index, attribute));

    private void PackParameter(
        LlvmCAbiParameterPlan parameter,
        LLVMValueRef logical,
        ICollection<LLVMValueRef> physical)
    {
        if (parameter.IsIdentity)
        {
            physical.Add(logical);
            return;
        }
        if (parameter.IsIndirect)
        {
            LLVMValueRef storage = _builder.BuildAlloca(parameter.LogicalLlvmType, "abi.argument.copy");
            _builder.BuildStore(logical, storage);
            physical.Add(storage);
            return;
        }

        LLVMTypeRef storageType = GetParameterStorageType(parameter);
        LLVMValueRef storageAddress = _builder.BuildAlloca(storageType, "abi.argument.coerce");
        _builder.BuildStore(logical, storageAddress);
        if (parameter.PhysicalTypes.Length == 1)
        {
            physical.Add(_builder.BuildLoad2(parameter.PhysicalTypes[0], storageAddress, "abi.argument"));
            return;
        }
        for (uint component = 0; component < parameter.PhysicalTypes.Length; component++)
            physical.Add(_builder.BuildLoad2(
                parameter.PhysicalTypes[checked((int)component)],
                _builder.BuildStructGEP2(storageType, storageAddress, component),
                "abi.argument.part"));
    }

    private LLVMTypeRef GetParameterStorageType(LlvmCAbiParameterPlan parameter) =>
        parameter.PhysicalTypes.Length == 1
            ? parameter.PhysicalTypes[0]
            : _context.GetStructType(parameter.PhysicalTypes.ToArray(), false);

    private void ApplyAttributes(
        LlvmCAbiFunctionPlan plan,
        Action<LLVMAttributeIndex, LLVMAttributeRef> apply)
    {
        uint physicalIndex = 1;
        if (plan.Return.IsIndirect)
        {
            apply((LLVMAttributeIndex)physicalIndex, CreateTypeAttribute("sret"u8, plan.Return.LogicalLlvmType));
            apply((LLVMAttributeIndex)physicalIndex, CreateEnumAttribute("align"u8, plan.Return.Alignment));
            physicalIndex++;
        }
        foreach (LlvmCAbiParameterPlan parameter in plan.Parameters)
        {
            if (parameter.IsByValue)
            {
                apply((LLVMAttributeIndex)physicalIndex,
                    CreateTypeAttribute("byval"u8, parameter.LogicalLlvmType));
                apply((LLVMAttributeIndex)physicalIndex,
                    CreateEnumAttribute("align"u8, parameter.Alignment));
            }
            if (parameter.StackAlignment != 0)
                apply((LLVMAttributeIndex)physicalIndex,
                    CreateEnumAttribute("alignstack"u8, parameter.StackAlignment));
            physicalIndex += checked((uint)parameter.PhysicalTypes.Length);
        }
    }

    private LLVMAttributeRef CreateTypeAttribute(ReadOnlySpan<byte> name, LLVMTypeRef type)
    {
        uint kind = GetAttributeKind(name);
        return new LLVMAttributeRef((IntPtr)LLVMApi.CreateTypeAttribute(_context, kind, type));
    }

    private LLVMAttributeRef CreateEnumAttribute(ReadOnlySpan<byte> name, ulong value)
    {
        uint kind = GetAttributeKind(name);
        return _context.CreateEnumAttribute(kind, value);
    }

    private static uint GetAttributeKind(ReadOnlySpan<byte> name)
    {
        fixed (byte* pointer = name)
            return LLVMApi.GetEnumAttributeKindForName((sbyte*)pointer, (UIntPtr)name.Length);
    }
}
