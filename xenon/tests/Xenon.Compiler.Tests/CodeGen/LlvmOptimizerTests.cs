using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using LLVMSharp.Interop;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler;
using Xunit;
using LLVMApi = LLVMSharp.Interop.LLVM;

namespace Xenon.Compiler.Tests.CodeGen;

public sealed unsafe class LlvmOptimizerTests
{
    private static readonly LLVMDiagnosticHandler CountingHandler = CountDiagnostic;

    [Fact]
    public void Run_RestoresPreviousDiagnosticHandlerAndContext()
    {
        var count = new StrongBox<int>();
        GCHandle countHandle = GCHandle.Alloc(count);
        LLVMContextRef context = LLVMContextRef.Create();
        LLVMModuleRef module = CreateModule(context);
        using NativeTargetMachine target = NativeTargetMachine.Create(
            LlvmTargetOptions.CreateHost(optimizationLevel: 3));
        context.SetDiagnosticHandler(CountingHandler, (void*)GCHandle.ToIntPtr(countHandle));
        nint expectedHandler = (nint)LLVMApi.ContextGetDiagnosticHandler(context);
        nint expectedContext = (nint)LLVMApi.ContextGetDiagnosticContext(context);

        try
        {
            var remarks = new List<string>();
            LlvmOptimizer.Run(module, target, remarks.Add);

            Assert.Equal(expectedHandler, (nint)LLVMApi.ContextGetDiagnosticHandler(context));
            Assert.Equal(expectedContext, (nint)LLVMApi.ContextGetDiagnosticContext(context));

            delegate* unmanaged[Cdecl]<LLVMOpaqueDiagnosticInfo*, void*, void> restored =
                LLVMApi.ContextGetDiagnosticHandler(context);
            restored(null, LLVMApi.ContextGetDiagnosticContext(context));
            Assert.Equal(1, count.Value);
        }
        finally
        {
            context.SetDiagnosticHandler(
                (delegate* unmanaged[Cdecl]<LLVMOpaqueDiagnosticInfo*, void*, void>)null,
                null);
            module.Dispose();
            context.Dispose();
            countHandle.Free();
        }
    }

    [Fact]
    public void Run_RestoresPreviousDiagnosticHandlerWhenOptimizationFails()
    {
        var count = new StrongBox<int>();
        GCHandle countHandle = GCHandle.Alloc(count);
        LLVMContextRef context = LLVMContextRef.Create();
        LLVMModuleRef module = CreateModule(context);
        using NativeTargetMachine target = NativeTargetMachine.Create(
            LlvmTargetOptions.CreateHost(optimizationLevel: 3));
        context.SetDiagnosticHandler(CountingHandler, (void*)GCHandle.ToIntPtr(countHandle));
        nint expectedHandler = (nint)LLVMApi.ContextGetDiagnosticHandler(context);
        nint expectedContext = (nint)LLVMApi.ContextGetDiagnosticContext(context);

        try
        {
            Assert.Throws<LlvmCodeGenerationException>(() =>
                LlvmOptimizer.Run(module, target, _ => { }, "not-a-valid-pass-pipeline"));

            Assert.Equal(expectedHandler, (nint)LLVMApi.ContextGetDiagnosticHandler(context));
            Assert.Equal(expectedContext, (nint)LLVMApi.ContextGetDiagnosticContext(context));

            delegate* unmanaged[Cdecl]<LLVMOpaqueDiagnosticInfo*, void*, void> restored =
                LLVMApi.ContextGetDiagnosticHandler(context);
            restored(null, LLVMApi.ContextGetDiagnosticContext(context));
            Assert.Equal(1, count.Value);
        }
        finally
        {
            context.SetDiagnosticHandler(
                (delegate* unmanaged[Cdecl]<LLVMOpaqueDiagnosticInfo*, void*, void>)null,
                null);
            module.Dispose();
            context.Dispose();
            countHandle.Free();
        }
    }

    [Fact]
    public void Remarks_AreScopedToRequestedCompilationAndRemainStableAcrossRuns()
    {
        var first = new List<string>();
        var second = new List<string>();
        Compilation compilation = TestCompilation.Create("""
            namespace Example;
            int AddOne(int value) { return value + 1; }
            export int Use(int value) { return AddOne(value); }
            """);

        _ = GenerateWithRemarks(compilation, first.Add);

        TextWriter previousError = Console.Error;
        using var error = new StringWriter();
        try
        {
            Console.SetError(error);
            _ = new LlvmIrGenerator().GenerateForTarget(
                compilation,
                LlvmTargetOptions.CreateHost(optimizationLevel: 3),
                "remarks-disabled");
        }
        finally
        {
            Console.SetError(previousError);
        }

        _ = GenerateWithRemarks(compilation, second.Add);

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first, second);
        Assert.Equal(string.Empty, error.ToString());
    }

    private static string GenerateWithRemarks(Compilation compilation, Action<string> sink) =>
        new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(optimizationLevel: 3),
            "remarks-enabled",
            new LlvmCodeGenerationOptions(
                "remarks-enabled",
                optimizationDiagnostics: new LlvmOptimizationDiagnostics
                {
                    OptimizationRemark = sink,
                }));

    private static LLVMModuleRef CreateModule(LLVMContextRef context)
    {
        LLVMModuleRef module = context.CreateModuleWithName("optimizer-handler-test");
        using NativeTargetMachine target = NativeTargetMachine.Create(
            LlvmTargetOptions.CreateHost(optimizationLevel: 3));
        module.Target = target.Triple;
        module.DataLayout = target.DataLayout;
        LLVMTypeRef functionType = LLVMTypeRef.CreateFunction(context.Int32Type, [], false);
        LLVMValueRef function = module.AddFunction("answer", functionType);
        LLVMBasicBlockRef entry = function.AppendBasicBlock("entry");
        using LLVMBuilderRef builder = context.CreateBuilder();
        builder.PositionAtEnd(entry);
        builder.BuildRet(LLVMValueRef.CreateConstInt(context.Int32Type, 42, false));
        return module;
    }

    private static void CountDiagnostic(LLVMOpaqueDiagnosticInfo* _, void* context)
    {
        if (context is not null &&
            GCHandle.FromIntPtr((nint)context).Target is StrongBox<int> count)
            count.Value++;
    }
}

internal static class TestCompilation
{
    public static Compilation Create(string source) => Compilation.Create(
        Xenon.Compiler.Text.SourceText.From(source, "optimizer-test.xe"));
}
