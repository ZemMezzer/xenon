using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.CodeGen;

public sealed class LlvmIrGeneratorTests
{
    [Fact]
    public void Generator_EmitsAndVerifiesMinimalMain()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return 42;
            }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "minimal");

        Assert.Contains("define internal i32 @Example.Main()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("ret i32 42", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsFunctionsArithmeticCallsAndExports()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            extern int puts(const byte* text);

            int Add(int a, int b)
            {
                return a + b;
            }

            export int Multiply(int a, int b)
            {
                return a * b;
            }

            int Main()
            {
                int result = Add(20, 22);
                puts("Hello from Xenon");
                return result;
            }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "core");

        Assert.Contains("declare i32 @puts(ptr)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define internal i32 @Example.Add(i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @Example_Multiply(i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("add i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("mul i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @Example.Add", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @puts", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RejectsCompilationWithSemanticErrors()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return missing;
            }
            """);

        var exception = Assert.Throws<LlvmCodeGenerationException>(
            () => new LlvmIrGenerator().Generate(compilation));
        Assert.Contains("contains errors", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsAndVerifiesControlFlow()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Choose(bool condition)
            {
                if (condition)
                    return 1;
                else
                    return 2;
            }

            int Sum(int count)
            {
                int total = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i == 2)
                        continue;

                    total += i;
                }

                while (total > 100)
                {
                    total--;
                    if (total == 110)
                        break;
                }

                return total;
            }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "control-flow");

        Assert.Contains("for.condition:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("while.condition:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("if.then:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("if.else:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("br i1", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UsesPhiNodesForShortCircuitBooleanOperators()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            bool Both(bool left, bool right)
            {
                return left && right;
            }

            bool Either(bool left, bool right)
            {
                return left || right;
            }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "short-circuit");

        Assert.Contains("logic.rhs:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("phi i1", llvmIr, StringComparison.Ordinal);
        Assert.Equal(2, llvmIr.Split("phi i1", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Generator_EmitsTargetedIrWithNativeEntryPoint()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return 0;
            }
            """);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            target,
            "targeted",
            generateExecutableEntryPoint: true);

        Assert.Contains($"target triple = \"{target.Triple}\"", llvmIr, StringComparison.Ordinal);
        Assert.Contains("target datalayout =", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @main()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @Example.Main()", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectEmitter_EmitsNonEmptyObjectForHostTarget()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return 42;
            }
            """);
        LlvmTargetOptions options = LlvmTargetOptions.CreateHost();
        string directory = CreateTemporaryDirectory();
        string outputPath = Path.Combine(
            directory,
            $"main{LlvmTargetPlatform.GetObjectFileExtension(options.Triple)}");

        try
        {
            LlvmObjectFile result = new LlvmObjectEmitter().Emit(
                compilation,
                outputPath,
                options,
                "object-test",
                generateExecutableEntryPoint: true);

            Assert.Equal(Path.GetFullPath(outputPath), result.Path);
            Assert.Equal(options.Triple, result.TargetTriple);
            Assert.False(string.IsNullOrWhiteSpace(result.DataLayout));
            Assert.True(new FileInfo(result.Path).Length > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObjectEmitter_LowersTargetSizedIntegerTypes()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            nint NativeIdentity(nint value)
            {
                return value;
            }

            clong CIdentity(clong value)
            {
                return value;
            }
            """);
        LlvmTargetOptions options = LlvmTargetOptions.CreateHost(optimizationLevel: 2);
        string directory = CreateTemporaryDirectory();
        string outputPath = Path.Combine(
            directory,
            $"native-types{LlvmTargetPlatform.GetObjectFileExtension(options.Triple)}");

        try
        {
            LlvmObjectFile result = new LlvmObjectEmitter().Emit(
                compilation,
                outputPath,
                options,
                "native-types");

            Assert.True(File.Exists(result.Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObjectEmitter_EmitsObjectForExplicitCrossTarget()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            export int Add(int left, int right)
            {
                return left + right;
            }
            """);
        var options = new LlvmTargetOptions("aarch64-unknown-linux-gnu", OptimizationLevel: 2);
        string directory = CreateTemporaryDirectory();
        string outputPath = Path.Combine(directory, "library.o");

        try
        {
            LlvmObjectFile result = new LlvmObjectEmitter().Emit(
                compilation,
                outputPath,
                options,
                "cross-target");
            byte[] header = File.ReadAllBytes(result.Path)[..4];

            Assert.Equal([0x7f, (byte)'E', (byte)'L', (byte)'F'], header);
            Assert.Equal("aarch64-unknown-linux-gnu", result.TargetTriple);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObjectEmitter_RejectsExecutableWithoutValidMain()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int NotMain()
            {
                return 0;
            }
            """);
        LlvmTargetOptions options = LlvmTargetOptions.CreateHost();
        string directory = CreateTemporaryDirectory();
        string outputPath = Path.Combine(
            directory,
            $"missing-main{LlvmTargetPlatform.GetObjectFileExtension(options.Triple)}");

        try
        {
            LlvmCodeGenerationException exception = Assert.Throws<LlvmCodeGenerationException>(
                () => new LlvmObjectEmitter().Emit(
                    compilation,
                    outputPath,
                    options,
                    "missing-main",
                    generateExecutableEntryPoint: true));

            Assert.Contains("int Main()", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Compilation CreateCompilation(string source) =>
        Compilation.Create(SourceText.From(source, "test.xe"));

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "xenon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
