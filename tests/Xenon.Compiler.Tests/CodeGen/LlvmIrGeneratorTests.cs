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

        Assert.Contains("define i32 @Example.Main()", llvmIr, StringComparison.Ordinal);
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
        Assert.Contains("define i32 @Example.Add(i32", llvmIr, StringComparison.Ordinal);
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

    private static Compilation CreateCompilation(string source) =>
        Compilation.Create(SourceText.From(source, "test.xe"));
}
