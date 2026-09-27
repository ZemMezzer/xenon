using System.Buffers;
using System.Text;

namespace Xenon.Compiler.Syntax;

// Identifier categories are Unicode scalar categories; offsets remain UTF-16 indices.
internal static class IdentifierFacts
{
    public static bool TryGetStart(string text, int index, out int width) =>
        TryGet(text, index, continuation: false, out width);

    public static bool TryGetContinue(string text, int index, out int width) =>
        TryGet(text, index, continuation: true, out width);

    private static bool TryGet(string text, int index, bool continuation, out int width)
    {
        width = 0;
        if ((uint)index >= (uint)text.Length ||
            Rune.DecodeFromUtf16(text.AsSpan(index), out Rune rune, out int consumed) != OperationStatus.Done)
            return false;
        if (rune.Value != '_' && !(continuation ? Rune.IsLetterOrDigit(rune) : Rune.IsLetter(rune)))
            return false;
        width = consumed;
        return true;
    }
}
