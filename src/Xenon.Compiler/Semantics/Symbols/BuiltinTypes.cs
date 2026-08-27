using System.Collections.Concurrent;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public static class BuiltinTypes
{
    private static readonly ConcurrentDictionary<(TypeSymbol Element, bool IsReadonly), PointerTypeSymbol> PointerTypes = new();
    private static readonly ConcurrentDictionary<(TypeSymbol Element, bool IsReadonly), ReferenceTypeSymbol> ReferenceTypes = new();
    private static readonly ConcurrentDictionary<(TypeSymbol Element, int Rank), ArrayTypeSymbol> ArrayTypes = new();

    public static PrimitiveTypeSymbol Void { get; } = new("void");
    public static PrimitiveTypeSymbol Bool { get; } = new("bool");
    public static PrimitiveTypeSymbol Byte { get; } = new("byte", true, false, 8);
    public static PrimitiveTypeSymbol SByte { get; } = new("sbyte", true, true, 8);
    public static PrimitiveTypeSymbol Short { get; } = new("short", true, true, 16);
    public static PrimitiveTypeSymbol UShort { get; } = new("ushort", true, false, 16);
    public static PrimitiveTypeSymbol Int { get; } = new("int", true, true, 32);
    public static PrimitiveTypeSymbol UInt { get; } = new("uint", true, false, 32);
    public static PrimitiveTypeSymbol Long { get; } = new("long", true, true, 64);
    public static PrimitiveTypeSymbol ULong { get; } = new("ulong", true, false, 64);
    public static PrimitiveTypeSymbol Float { get; } = new("float", isFloatingPoint: true, bitWidth: 32);
    public static PrimitiveTypeSymbol Double { get; } = new("double", isFloatingPoint: true, bitWidth: 64);
    public static PrimitiveTypeSymbol NInt { get; } = new("nint", true, true);
    public static PrimitiveTypeSymbol NUInt { get; } = new("nuint", true, false);
    public static PrimitiveTypeSymbol CLong { get; } = new("clong", true, true);
    public static PrimitiveTypeSymbol CULong { get; } = new("culong", true, false);

    internal static TypeSymbol Error { get; } = new SpecialTypeSymbol("<error>");

    internal static TypeSymbol Null { get; } = new SpecialTypeSymbol("<null>");

    public static PointerTypeSymbol PointerTo(TypeSymbol elementType, bool isReadonly = false) =>
        PointerTypes.GetOrAdd((elementType, isReadonly), key => new PointerTypeSymbol(key.Element, key.IsReadonly));

    public static ReferenceTypeSymbol ReferenceTo(TypeSymbol elementType, bool isReadonly = false) =>
        ReferenceTypes.GetOrAdd((elementType, isReadonly), key => new ReferenceTypeSymbol(key.Element, key.IsReadonly));

    public static ArrayTypeSymbol ArrayOf(TypeSymbol elementType, int rank = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rank, 1);
        return ArrayTypes.GetOrAdd((elementType, rank), static key => new ArrayTypeSymbol(key.Element, key.Rank));
    }

    internal static TypeSymbol? FromSyntaxKind(SyntaxKind kind) => kind switch
    {
        SyntaxKind.VoidKeyword => Void,
        SyntaxKind.BoolKeyword => Bool,
        SyntaxKind.ByteKeyword => Byte,
        SyntaxKind.SByteKeyword => SByte,
        SyntaxKind.ShortKeyword => Short,
        SyntaxKind.UShortKeyword => UShort,
        SyntaxKind.IntKeyword => Int,
        SyntaxKind.UIntKeyword => UInt,
        SyntaxKind.LongKeyword => Long,
        SyntaxKind.ULongKeyword => ULong,
        SyntaxKind.FloatKeyword => Float,
        SyntaxKind.DoubleKeyword => Double,
        SyntaxKind.NIntKeyword => NInt,
        SyntaxKind.NUIntKeyword => NUInt,
        SyntaxKind.CLongKeyword => CLong,
        SyntaxKind.CULongKeyword => CULong,
        _ => null,
    };
}
