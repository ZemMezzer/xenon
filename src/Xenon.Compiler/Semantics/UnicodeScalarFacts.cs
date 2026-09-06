using System.Numerics;

namespace Xenon.Compiler.Semantics;

internal static class UnicodeScalarFacts
{
    public static bool IsValid(uint value) =>
        value <= 0x10FFFF && value is not (>= 0xD800 and <= 0xDFFF);

    public static bool IsValid(BigInteger value) =>
        value >= BigInteger.Zero && value <= 0x10FFFF &&
        (value < 0xD800 || value > 0xDFFF);
}
