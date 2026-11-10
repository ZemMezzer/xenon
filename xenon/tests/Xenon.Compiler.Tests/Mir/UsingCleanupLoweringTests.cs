using System.Text.RegularExpressions;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Text;
using Xunit;
using Xunit.Abstractions;

namespace Xenon.Compiler.Tests.Mir;

public sealed class UsingCleanupLoweringTests(ITestOutputHelper output)
{
    private static Compilation Compile(string source)
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace Cleanup; " + source, "cleanup.xe"));
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        return compilation;
    }

    [Theory]
    [InlineData("int*", "new int(42)", "")]
    [InlineData("int[]", "new int[4]", "")]
    [InlineData("Item*", "new Item()", "struct Item { public int Value; public ~Item() { Value = 0; } }")]
    public void NonThrowingUsingMatchesManualDelete(string type, string initializer, string declarations)
    {
        Compilation manual = Compile(declarations + $" void Test() {{ {type} p = {initializer}; delete(p); }}");
        Compilation automatic = Compile(declarations + $" void Test() {{ using {type} p = {initializer}; }}");
        MirFunction before = manual.GetMirFunctions().Single(f => f.Symbol.Name == "Test");
        MirFunction after = automatic.GetMirFunctions().Single(f => f.Symbol.Name == "Test");
        string manualIr = new LlvmIrGenerator().GenerateForTarget(manual, LlvmTargetOptions.CreateHost());
        string usingIr = new LlvmIrGenerator().GenerateForTarget(automatic, LlvmTargetOptions.CreateHost());
        output.WriteLine($"MIR blocks/locals: manual={before.Blocks.Length}/{before.Locals.Length}, using={after.Blocks.Length}/{after.Locals.Length}; LLVM lines: manual={manualIr.Split('\n').Length}, using={usingIr.Split('\n').Length}");
        Assert.False(after.HasExceptionRegions);
        Assert.Equal(before.Locals.Length, after.Locals.Length);
        Assert.InRange(after.Blocks.Length, before.Blocks.Length, before.Blocks.Length + 1);
        Assert.False(LlvmIrGenerator.RequiresNativeExceptionRuntime(automatic));
        Assert.DoesNotContain("__xenon_eh_", usingIr);
        Assert.DoesNotContain("invoke ", usingIr);
        Assert.Equal(NormalOperations(before), NormalOperations(after));
        Assert.Equal(Instructions(manualIr), Instructions(usingIr));
    }

    private static string[] NormalOperations(MirFunction function)
    {
        var graph = new MirControlFlow(function, (_, edge) => edge.Kind != MirEdgeKind.Unwind);
        return function.Blocks.Where(b => graph.Reachable.Contains(b.Id))
            .SelectMany(b => b.Statements.Where(s => s is not (MirStorageLive or MirStorageDead))
                .Select(s => s is MirAssign a ? a.Value.GetType().Name : s.GetType().Name)
                .Append(b.Terminator is MirIntrinsicCall i ? i.Intrinsic.ToString() : b.Terminator.GetType().Name))
            .Where(operation => operation != nameof(MirGoto)).Order().ToArray();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void NonThrowingCallsAndMultipleResourcesDoNotAddExceptionStorage(int count, bool exceptionsElsewhere)
    {
        string helper = "void Touch(int* value) { *value = *value + 1; } " +
            (exceptionsElsewhere ? "void UnrelatedFailure() { throw 7; } " : "");
        string declarations = string.Join(" ", Enumerable.Range(0, count).Select(i => $"int* p{i} = new int({i}); Touch(p{i});"));
        string automaticDeclarations = declarations.Replace("int* p", "using int* p");
        string deletions = string.Join(" ", Enumerable.Range(0, count).Reverse().Select(i => $"delete(p{i});"));
        Compilation manual = Compile(helper + $"void Test() {{ {declarations} {deletions} }}");
        Compilation automatic = Compile(helper + $"void Test() {{ {automaticDeclarations} }}");
        Assert.Equal(exceptionsElsewhere, LlvmIrGenerator.RequiresNativeExceptionRuntime(automatic));
        Assert.Equal(NormalOperations(manual.GetMirFunctions().Single(f => f.Symbol.Name == "Test")),
            NormalOperations(automatic.GetMirFunctions().Single(f => f.Symbol.Name == "Test")));
        string manualIr = new LlvmIrGenerator().GenerateForTarget(manual, LlvmTargetOptions.CreateHost());
        string usingIr = new LlvmIrGenerator().GenerateForTarget(automatic, LlvmTargetOptions.CreateHost());
        if (!exceptionsElsewhere) Assert.DoesNotContain("__xenon_eh_", usingIr);
        output.WriteLine($"Manual allocas={Instructions(manualIr).Count(i => i == "alloca")}, using allocas={Instructions(usingIr).Count(i => i == "alloca")}");
        // Lexical storage boundaries may retain trivial branches before LLVM's
        // simplification; there must be no extra loads, stores, slots, or calls.
        Assert.Equal(Instructions(manualIr).Where(i => i != "br"), Instructions(usingIr).Where(i => i != "br"));
        var optimized = LlvmTargetOptions.CreateHost(optimizationLevel: 2);
        Assert.Equal(Instructions(new LlvmIrGenerator().GenerateForTarget(manual, optimized)),
            Instructions(new LlvmIrGenerator().GenerateForTarget(automatic, optimized)));
    }

    [Fact]
    public void EarlyReturnUsesTheSameDeleteInstructions()
    {
        Compilation manual = Compile("int Test(bool stop) { int* p = new int(1); if (stop) { delete(p); return 42; } delete(p); return 0; }");
        Compilation automatic = Compile("int Test(bool stop) { using int* p = new int(1); if (stop) return 42; return 0; }");
        Assert.False(LlvmIrGenerator.RequiresNativeExceptionRuntime(automatic));
        Assert.Equal(NormalOperations(manual.GetMirFunctions().Single(f => f.Symbol.Name == "Test")),
            NormalOperations(automatic.GetMirFunctions().Single(f => f.Symbol.Name == "Test")));
        Assert.Equal(Instructions(new LlvmIrGenerator().GenerateForTarget(manual, LlvmTargetOptions.CreateHost())),
            Instructions(new LlvmIrGenerator().GenerateForTarget(automatic, LlvmTargetOptions.CreateHost())));
    }

    [Fact]
    public void UnknownCallsProtectedByUsingStillRequireExceptionRuntime()
    {
        Compilation compilation = Compile("extern void Unknown(); void Test() { using var p = new int(1); Unknown(); }");
        Assert.True(LlvmIrGenerator.RequiresNativeExceptionRuntime(compilation));
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.Contains("invoke ", ir);
        Assert.Contains("__xenon_eh_", ir);
    }

    // Compare unoptimized instructions, including allocas and branches; block/local
    // numbering and function attributes are intentionally irrelevant to this budget.
    private static string[] Instructions(string ir) => ir.Split('\n')
        .Where(line => line.StartsWith("  ") && !line.TrimStart().StartsWith(';'))
        .Select(line => Regex.Replace(line.Trim(), @"^%[^=]+ = ", ""))
        .Select(line => line.Split(' ')[0]).Order().ToArray();
}
