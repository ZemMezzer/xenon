using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.CodeGen;

public sealed class TargetSelectionTests
{
    private const string Source = """
        namespace TargetProbe;
        #if XENON_WINDOWS
        private extern int windows_only();
        public int Select() { return windows_only(); }
        #elif XENON_LINUX
        private extern int linux_only();
        public int Select() { return linux_only(); }
        #elif XENON_MACOS
        private extern int macos_only();
        public int Select() { return macos_only(); }
        #else
        public int Select() { return 0; }
        #endif
        #if XENON_X64
        internal const int TargetArchitecture = 1;
        #elif XENON_ARM64
        internal const int TargetArchitecture = 2;
        #else
        internal const int TargetArchitecture = 0;
        #endif
        public int Architecture() { return TargetArchitecture; }
        """;

    [Theory]
    [InlineData("x86_64-pc-windows-msvc", "windows_only", 1)]
    [InlineData("x86_64-unknown-linux-gnu", "linux_only", 1)]
    [InlineData("aarch64-unknown-linux-gnu", "linux_only", 2)]
    [InlineData("x86_64-apple-darwin", "macos_only", 1)]
    [InlineData("arm64-apple-darwin", "macos_only", 2)]
    public void TargetLibraryContainsSelectedPlatformAtO0(string triple, string selected, int architecture)
    {
        Compilation library = Compilation.Create(new CompilationOptions
        {
            ConditionalCompilation = new ConditionalCompilationOptions(targetTriple: triple),
        }, [], SourceText.From(Source, "platform.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("Platform"));
        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Compilation app = Compilation.Create(new CompilationOptions(ConditionalCompilation: new(targetTriple: triple)), [reference], SourceText.From("""
            using TargetProbe;
            namespace App;
            int Main() { return Select() + Architecture(); }
            """, "main.xe"));
        var target = new LlvmTargetOptions(triple, OptimizationLevel: 0);
        Compilation bound = LlvmIrGenerator.BindForTarget(app, target);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(bound, target);
        string calls = string.Join("\n", ir.Split('\n').Where(line =>
            line.Contains("call ", StringComparison.Ordinal) || line.Contains("invoke ", StringComparison.Ordinal)));
        Assert.Contains("@" + selected, calls);
        foreach (string name in new[] { "windows_only", "linux_only", "macos_only" }.Where(name => name != selected))
            Assert.DoesNotContain("@" + name, calls);
        Assert.DoesNotContain("@__xenon_target_", calls);
        Assert.Contains($"ret i32 {architecture}", ir);
    }

    [Theory]
    [InlineData("__xenon_target_os", false)]
    [InlineData("__xenon_target_arch", false)]
    [InlineData("__xenon_target_os", true)]
    [InlineData("__xenon_target_arch", true)]
    public void FormerQueryNamesAreOrdinaryExterns(string name, bool takeAddress)
    {
        string body = takeAddress
            ? $"function long(int)* query = &{name}; return cast<int>(query(7));"
            : $"return cast<int>({name}(7));";
        Compilation app = Compilation.Create(SourceText.From($$"""
            namespace App;
            private extern long {{name}}(int value);
            int Main() { {{body}} }
            """, "ordinary-extern.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        var target = new LlvmTargetOptions("x86_64-pc-windows-msvc");
        string ir = new LlvmIrGenerator().GenerateForTarget(LlvmIrGenerator.BindForTarget(app, target), target);
        Assert.Contains($"declare i64 @{name}(i32", ir);
        if (takeAddress)
            Assert.Contains($"store ptr @{name}", ir);
        else
            Assert.Contains($"@{name}(i32 7)", ir);
        Assert.DoesNotContain($"define internal i64 @{name}", ir);
    }
}
