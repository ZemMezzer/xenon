using LLVMSharp.Interop;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

/// <summary>Queries LLVM DataLayout without creating an executable module or lowering any expressions.</summary>
internal sealed class LlvmTypeLayout(NativeTargetMachine target) : ITargetTypeLayout, IDisposable
{
    private readonly LLVMContextRef _context = LLVMContextRef.Create();
    private readonly Dictionary<StructTypeSymbol, LLVMTypeRef> _structures = [];
    private readonly HashSet<StructTypeSymbol> _building = [];

    public int GetIntegerBitWidth(PrimitiveTypeSymbol type)
    {
        if (type.BitWidth is int width) return width;
        if (ReferenceEquals(type, BuiltinTypes.CLong) || ReferenceEquals(type, BuiltinTypes.CULong))
            return target.Triple.Contains("windows", StringComparison.OrdinalIgnoreCase) || target.Triple.Contains("win32", StringComparison.OrdinalIgnoreCase)
                ? 32 : target.PointerBitWidth;
        if (ReferenceEquals(type, BuiltinTypes.NInt) || ReferenceEquals(type, BuiltinTypes.NUInt)) return target.PointerBitWidth;
        throw new LlvmCodeGenerationException($"'{type.Name}' is not an integer type.");
    }

    public ulong GetSize(TypeSymbol type) => target.TargetData.ABISizeOfType(MapType(type));
    public uint GetAlignment(TypeSymbol type) => target.TargetData.ABIAlignmentOfType(MapType(type));
    public ulong GetFieldOffset(StructTypeSymbol type, FieldSymbol field) => target.TargetData.OffsetOfElement(MapType(type), (uint)field.Ordinal);

    private LLVMTypeRef MapType(TypeSymbol type)
    {
        if (type is EnumTypeSymbol enumeration) return MapType(enumeration.UnderlyingType);
        if (type is PointerTypeSymbol or ReferenceTypeSymbol or ArrayTypeSymbol)
            return LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
        if (ReferenceEquals(type, BuiltinTypes.Bool)) return _context.Int1Type;
        if (ReferenceEquals(type, BuiltinTypes.Float)) return _context.FloatType;
        if (ReferenceEquals(type, BuiltinTypes.Double)) return _context.DoubleType;
        if (type is PrimitiveTypeSymbol { IsInteger: true } integer)
            return GetIntegerBitWidth(integer) switch
            {
                8 => _context.Int8Type,
                16 => _context.Int16Type,
                32 => _context.Int32Type,
                64 => _context.Int64Type,
                int width => throw new LlvmCodeGenerationException($"Unsupported integer width {width}."),
            };
        if (type is InterfaceTypeSymbol)
        {
            LLVMTypeRef pointer = LLVMTypeRef.CreatePointer(_context.Int8Type, 0);
            return _context.GetStructType([pointer, pointer], false);
        }
        if (type is StructTypeSymbol structure)
        {
            if (_structures.TryGetValue(structure, out LLVMTypeRef existing)) return existing;
            if (!_building.Add(structure)) throw new LlvmCodeGenerationException($"Recursive layout for '{structure.FullName}'.");
            LLVMTypeRef result = _context.CreateNamedStruct(structure.FullName);
            LLVMTypeRef[] fields = structure.HasVirtualDispatch
                ? [LLVMTypeRef.CreatePointer(_context.Int8Type, 0), .. structure.AllInstanceFields.Select(field => MapType(field.Type))]
                : structure.AllInstanceFields.Select(field => MapType(field.Type)).ToArray();
            result.StructSetBody(fields, false);
            _building.Remove(structure);
            _structures.Add(structure, result);
            return result;
        }
        throw new LlvmCodeGenerationException($"Cannot determine layout of '{type.Name}'.");
    }

    public void Dispose() => _context.Dispose();
}
