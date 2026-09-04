using LLVMSharp.Interop;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

/// <summary>
/// Durable compiler-side copy of the target ABI facts used by semantic analysis.
/// It owns no LLVM context, target data, machine, or other disposable native handle.
/// </summary>
internal sealed class LlvmTypeLayout : ITargetTypeLayout
{
    private readonly int _pointerBitWidth;
    private readonly AbiValueLayout _pointer;
    private readonly AbiValueLayout _bool;
    private readonly AbiValueLayout _int8;
    private readonly AbiValueLayout _int16;
    private readonly AbiValueLayout _int32;
    private readonly AbiValueLayout _int64;
    private readonly AbiValueLayout _float;
    private readonly AbiValueLayout _double;
    private readonly bool _windowsCLong;

    private LlvmTypeLayout(int pointerBitWidth, AbiValueLayout pointer, AbiValueLayout @bool,
        AbiValueLayout int8, AbiValueLayout int16, AbiValueLayout int32, AbiValueLayout int64,
        AbiValueLayout @float, AbiValueLayout @double, bool windowsCLong)
    {
        _pointerBitWidth = pointerBitWidth;
        _pointer = pointer;
        _bool = @bool;
        _int8 = int8;
        _int16 = int16;
        _int32 = int32;
        _int64 = int64;
        _float = @float;
        _double = @double;
        _windowsCLong = windowsCLong;
    }

    public static LlvmTypeLayout Create(NativeTargetMachine target)
    {
        ArgumentNullException.ThrowIfNull(target);
        LLVMTargetDataRef data = target.TargetData;
        using LLVMContextRef context = LLVMContextRef.Create();
        static AbiValueLayout Query(LLVMTargetDataRef data, LLVMTypeRef type) =>
            new(data.ABISizeOfType(type), data.ABIAlignmentOfType(type));
        LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(context.Int8Type, 0);
        bool windows = target.Triple.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
            target.Triple.Contains("win32", StringComparison.OrdinalIgnoreCase);
        return new LlvmTypeLayout(target.PointerBitWidth,
            Query(data, pointer), Query(data, context.Int1Type), Query(data, context.Int8Type),
            Query(data, context.Int16Type), Query(data, context.Int32Type), Query(data, context.Int64Type),
            Query(data, context.FloatType), Query(data, context.DoubleType), windows);
    }

    public int GetIntegerBitWidth(PrimitiveTypeSymbol type)
    {
        if (type.BitWidth is int width) return width;
        if (TypeIdentity.AreSame(type, BuiltinTypes.CLong) || TypeIdentity.AreSame(type, BuiltinTypes.CULong))
            return _windowsCLong ? 32 : _pointerBitWidth;
        if (TypeIdentity.AreSame(type, BuiltinTypes.NInt) || TypeIdentity.AreSame(type, BuiltinTypes.NUInt))
            return _pointerBitWidth;
        throw new LlvmCodeGenerationException($"'{type.Name}' is not an integer type.");
    }

    public ulong GetSize(TypeSymbol type) => GetLayout(type, []).Size;

    public uint GetAlignment(TypeSymbol type) => GetLayout(type, []).Alignment;

    public ulong GetFieldOffset(StructTypeSymbol type, FieldSymbol field)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(field);
        StructTypeSymbol owner = field.ContainingType as StructTypeSymbol
            ?? throw new LlvmCodeGenerationException($"Field '{field.Name}' is not owned by a struct.");
        IReadOnlyList<AbiValueLayout> elements = GetStructElementLayouts(owner, []);
        if ((uint)field.Ordinal >= (uint)elements.Count)
            throw new LlvmCodeGenerationException($"Field '{field.Name}' has an invalid layout ordinal.");
        ulong offset = 0;
        for (int index = 0; index <= field.Ordinal; index++)
        {
            AbiValueLayout element = elements[index];
            offset = AlignTo(offset, element.Alignment);
            if (index == field.Ordinal) return offset;
            offset = checked(offset + element.Size);
        }
        throw new InvalidOperationException("Field offset calculation did not produce a result.");
    }

    private AbiValueLayout GetLayout(TypeSymbol type, HashSet<StructTypeSymbol> building)
    {
        if (type is StorageTypeSymbol storage)
            return AggregateLayout([GetLayout(storage.ElementType, building), _bool]);
        if (type is AtomicTypeSymbol atomic)
            return LlvmAtomicStorage.RequiresLock(atomic.ElementType)
                ? AggregateLayout([_int8, GetLayout(atomic.ElementType, building)])
                : GetLayout(atomic.ElementType, building);
        if (type is LifetimeModifierTypeSymbol modifier) return GetLayout(modifier.ElementType, building);
        if (type is EnumTypeSymbol enumeration) return GetLayout(enumeration.UnderlyingType, building);
        if (type is PointerTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol or OwnershipTypeSymbol) return _pointer;
        if (TypeIdentity.AreSame(type, BuiltinTypes.Bool)) return _bool;
        if (TypeIdentity.AreSame(type, BuiltinTypes.Float)) return _float;
        if (TypeIdentity.AreSame(type, BuiltinTypes.Double)) return _double;
        if (type is PrimitiveTypeSymbol { IsInteger: true } integer)
            return GetIntegerBitWidth(integer) switch
            {
                8 => _int8,
                16 => _int16,
                32 => _int32,
                64 => _int64,
                int width => throw new LlvmCodeGenerationException($"Unsupported integer width {width}."),
            };
        if (type is InterfaceTypeSymbol) return AggregateLayout([_pointer, _pointer]);
        if (type is StructTypeSymbol structure)
        {
            if (!building.Add(structure))
                throw new LlvmCodeGenerationException($"Recursive layout for '{structure.FullName}'.");
            AbiValueLayout result = AggregateLayout(GetStructElementLayouts(structure, building));
            building.Remove(structure);
            return result;
        }
        throw new LlvmCodeGenerationException($"Cannot determine layout of '{type.Name}'.");
    }

    private IReadOnlyList<AbiValueLayout> GetStructElementLayouts(
        StructTypeSymbol type, HashSet<StructTypeSymbol> building)
    {
        var elements = new List<AbiValueLayout>();
        if (type.BaseType is not null) elements.Add(GetLayout(type.BaseType, building));
        if (type.IntroducesVirtualDispatch) elements.Add(_pointer);
        elements.AddRange(type.Fields.Select(field => GetLayout(field.Type, building)));
        return elements;
    }

    private AbiValueLayout AggregateLayout(IEnumerable<AbiValueLayout> elements)
    {
        ulong size = 0;
        uint alignment = 1;
        foreach (AbiValueLayout element in elements)
        {
            size = AlignTo(size, element.Alignment);
            size = checked(size + element.Size);
            alignment = Math.Max(alignment, element.Alignment);
        }
        return new AbiValueLayout(AlignTo(size, alignment), alignment);
    }

    private static ulong AlignTo(ulong value, uint alignment)
    {
        ulong mask = alignment - 1UL;
        return checked((value + mask) & ~mask);
    }

    private readonly record struct AbiValueLayout(ulong Size, uint Alignment);

}
