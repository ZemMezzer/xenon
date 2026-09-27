using Xenon.Compiler;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class UnsupportedWindowsX86TargetTests
{
    [Theory]
    [InlineData("i686-pc-windows-msvc")]
    [InlineData("i386-pc-win32-msvc")]
    [InlineData("x86-pc-windows-msvc")]
    [InlineData("windows-x86")]
    public void WindowsX86TargetIsRejectedBeforeCompilation(string triple)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => CompilationTarget.Normalize(triple));
        Assert.Contains("Windows x86 (32-bit) target", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not supported", exception.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new ConditionalCompilationOptions(targetTriple: triple));
    }

    [Theory]
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("aarch64-pc-windows-msvc")]
    [InlineData("i686-unknown-linux-gnu")]
    public void OtherTargetsRemainAccepted(string triple) =>
        Assert.Equal(triple, CompilationTarget.Normalize(triple));
}
