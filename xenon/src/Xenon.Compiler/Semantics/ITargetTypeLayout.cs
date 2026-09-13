using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

/// <summary>Target ABI facts used during semantic constant evaluation. No backend handles escape this interface.</summary>
public interface ITargetTypeLayout
{
    /// <summary>Canonical ABI target, when supplied by a production backend.</summary>
    string? TargetTriple => null;
    int GetIntegerBitWidth(PrimitiveTypeSymbol type);
    ulong GetSize(TypeSymbol type);
    uint GetAlignment(TypeSymbol type);
    ulong GetFieldOffset(StructTypeSymbol type, FieldSymbol field);
}
