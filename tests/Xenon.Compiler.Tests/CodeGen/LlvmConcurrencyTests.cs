using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.CodeGen;

public sealed class LlvmConcurrencyTests
{
    [Fact]
    public async Task ParallelTargetLayouts_DoNotUseGlobalContext()
    {
        (LlvmTargetOptions Options, int PointerBytes, int CLongBytes)[] targets =
        [
            (LlvmTargetOptions.CreateHost(), IntPtr.Size, OperatingSystem.IsWindows() ? 4 : IntPtr.Size),
            (new LlvmTargetOptions("i686-pc-windows-msvc"), 4, 4),
            (new LlvmTargetOptions("x86_64-pc-windows-msvc"), 8, 4),
            (new LlvmTargetOptions("x86_64-unknown-linux-gnu"), 8, 8),
            (new LlvmTargetOptions("aarch64-unknown-linux-gnu"), 8, 8),
        ];

        Task[] workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            Compilation compilation = CreateLayoutCompilation();
            for (int iteration = 0; iteration < 32; iteration++)
            {
                var target = targets[(worker + iteration) % targets.Length];
                Compilation bound = LlvmIrGenerator.BindForTarget(
                    compilation,
                    target.Options);

                Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
                var layout = Assert.Single(
                    Assert.Single(bound.SemanticModel.GlobalNamespace.Namespaces).Enums);
                Assert.Equal(
                    [target.PointerBytes, target.PointerBytes, target.CLongBytes,
                        1, 1, 2, 2, 4, 4, 8, 8, 4, 4, 8, 8],
                    layout.Members.Select(member => (int)member.Value!).ToArray());
            }
        })).ToArray();

        await Task.WhenAll(workers);
    }

    [Fact]
    public void GeneratorReuse_SequentialSecondInvocationIsRejected()
    {
        Compilation compilation = CreateGenerationCompilation(0);
        var generator = new LlvmIrGenerator();

        generator.Generate(compilation, "first");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => generator.Generate(compilation, "second"));
        Assert.Throws<InvalidOperationException>(() => generator.GenerateForTarget(
            compilation,
            new LlvmTargetOptions(string.Empty),
            "third"));

        Assert.Contains("single-use", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratorReuse_ConcurrentInvocationHasOneWinner()
    {
        Compilation compilation = CreateGenerationCompilation(0);
        var generator = new LlvmIrGenerator();
        using var gate = new ManualResetEventSlim();
        Task<Exception?>[] attempts = Enumerable.Range(0, 2).Select(index => Task.Run<Exception?>(() =>
        {
            gate.Wait();
            Exception? error = Record.Exception(
                () => generator.Generate(compilation, $"attempt_{index}"));
            return error;
        })).ToArray();

        gate.Set();
        Exception?[] errors = await Task.WhenAll(attempts);

        Assert.Equal(1, errors.Count(error => error is null));
        InvalidOperationException rejection = Assert.IsType<InvalidOperationException>(
            Assert.Single(errors.Where(error => error is not null)));
        Assert.Contains("single-use", rejection.Message, StringComparison.Ordinal);
    }

    private static Compilation CreateLayoutCompilation() => Compilation.Create(SourceText.From("""
        namespace Example;
        enum Layout
        {
            Pointer = cast<int>(sizeof(nint)),
            PointerAlignment = cast<int>(alignof(nint)),
            CLong = cast<int>(sizeof(clong)),
            BoolSize = cast<int>(sizeof(bool)),
            BoolAlignment = cast<int>(alignof(bool)),
            Int16Size = cast<int>(sizeof(short)),
            Int16Alignment = cast<int>(alignof(short)),
            Int32Size = cast<int>(sizeof(int)),
            Int32Alignment = cast<int>(alignof(int)),
            Int64Size = cast<int>(sizeof(long)),
            Int64Alignment = cast<int>(alignof(long)),
            FloatSize = cast<int>(sizeof(float)),
            FloatAlignment = cast<int>(alignof(float)),
            DoubleSize = cast<int>(sizeof(double)),
            DoubleAlignment = cast<int>(alignof(double))
        }
        """, "layout-concurrency.xe"));

    private static Compilation CreateGenerationCompilation(int worker) => Compilation.Create(SourceText.From($$"""
        namespace Parallel{{worker}};
        int Value() { return {{worker}} + 42; }
        """, $"parallel-{worker}.xe"));
}
