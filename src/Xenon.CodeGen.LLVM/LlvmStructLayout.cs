using LLVMSharp.Interop;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.CodeGen.LLVM;

/// <summary>
/// Declaration-local layout shared by constant evaluation and code generation.
/// A complete base is always the first element, preserving its size, padding and
/// address. Only the first polymorphic type adds dispatch storage, after its base.
/// No layout query depends on descendants or compilation-unit discovery order.
/// </summary>
internal static class LlvmStructLayout
{
    public static LLVMTypeRef[] Elements(StructTypeSymbol type, Func<TypeSymbol, LLVMTypeRef> mapType,
        LLVMTypeRef pointerType)
    {
        var elements = new List<LLVMTypeRef>();
        if (type.BaseType is not null) elements.Add(mapType(type.BaseType));
        if (type.IntroducesVirtualDispatch) elements.Add(pointerType);
        elements.AddRange(type.Fields.Select(field => mapType(field.Type)));
        return elements.ToArray();
    }

    public static uint DispatchIndex(StructTypeSymbol owner) => owner.BaseType is null ? 0u : 1u;
}
