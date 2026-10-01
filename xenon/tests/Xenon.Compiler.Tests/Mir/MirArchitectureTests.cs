using System.Reflection;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirArchitectureTests
{
    [Fact]
    public void BackendCannotAcceptOrStoreBoundNodes()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in typeof(LlvmIrGenerator).Assembly.GetTypes())
        {
            foreach (var field in type.GetFields(flags)) Check(field.FieldType);
            foreach (var method in type.GetMethods(flags))
            {
                Check(method.ReturnType);
                foreach (var parameter in method.GetParameters()) Check(parameter.ParameterType);
                if (method.GetMethodBody() is { } body)
                    foreach (var local in body.LocalVariables) Check(local.LocalType);
            }
            foreach (var constructor in type.GetConstructors(flags))
                foreach (var parameter in constructor.GetParameters()) Check(parameter.ParameterType);
        }
        static void Check(Type type)
        {
            Assert.False(type.Namespace == "Xenon.Compiler.Semantics.Binding", type.FullName);
            if (type.HasElementType) Check(type.GetElementType()!);
            foreach (var argument in type.GetGenericArguments()) Check(argument);
        }
    }

    [Fact]
    public void CompilationOwnsVerifiedMirForEveryExecutableFunction()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Pipeline;
            struct Item { public int Value; public Item(int value) { Value = value; } }
            int Add(int x) { return x + 1; }
            int Main() { Item item = Item(Add(3)); return item.Value; }
            """));
        Assert.Empty(compilation.Diagnostics);
        var initial = compilation.GetMirFunctions(lowered: false);
        var lowered = compilation.GetMirFunctions();
        Assert.Equal(compilation.GetStaticImplementationSymbols(), initial.Select(function => function.Symbol));
        Assert.Equal(initial.Select(function => function.Symbol), lowered.Select(function => function.Symbol));
        Assert.All(initial.Concat(lowered), function => Assert.Empty(MirVerifier.Verify(function)));
        Assert.Equal(lowered, compilation.GetMirFunctions());
        Assert.Equal(compilation.DumpMir(), compilation.DumpMir());
    }

    [Fact]
    public void ClosureCleanupHasExplicitReverseDropsFreeAndUnwind()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Pipeline;
            struct Item { public ~Item() { } }
            void Run() {
                Item first = Item(); Item second = Item();
                function void() callback = [move first, move second]() => { };
            }
            """));
        Assert.Empty(compilation.Diagnostics);
        var helper = Assert.Single(compilation.GetMirFunctions(lowered: false),
            function => function.Symbol.ClosureEnvironmentOwner is not null);
        Assert.Empty(MirVerifier.Verify(helper));
        var blocks = helper.Blocks.ToDictionary(block => block.Id);
        var last = Assert.IsType<MirDrop>(blocks[helper.Entry].Terminator);
        Assert.Equal("second", helper.Locals.Single(local => local.Id == last.Place.Local).Name);
        var first = Assert.IsType<MirDrop>(blocks[last.Normal].Terminator);
        Assert.Equal("first", helper.Locals.Single(local => local.Id == first.Place.Local).Name);
        var normalFree = Assert.IsType<MirIntrinsicCall>(blocks[first.Normal].Terminator);
        Assert.Equal(MirIntrinsicKind.Free, normalFree.Intrinsic);
        Assert.IsType<MirReturn>(blocks[normalFree.Normal].Terminator);
        var remaining = Assert.IsType<MirDrop>(blocks[last.Unwind].Terminator);
        Assert.Equal(first.Place, remaining.Place);
        Assert.IsType<MirAbort>(blocks[remaining.Unwind].Terminator);
        var exceptionalFree = Assert.IsType<MirIntrinsicCall>(blocks[remaining.Normal].Terminator);
        Assert.Equal(MirIntrinsicKind.Free, exceptionalFree.Intrinsic);
        Assert.IsType<MirResumeUnwind>(blocks[exceptionalFree.Normal].Terminator);
        Assert.Contains(helper.Symbol.FullName, compilation.DumpMir());
        Assert.Contains(helper.Symbol, compilation.GetMirFunctions().Select(function => function.Symbol));
    }
    [Fact]
    public void GeneratedClosureCleanupRootsItsImportedDestructor()
    {
        var library = Compilation.Create(SourceText.From("""
            namespace ClosureLibrary;
            public struct Item { public ~Item() { } }
            """));
        Assert.Empty(library.Diagnostics);
        var reference = Xenon.Compiler.Libraries.XelibReader.Read(
            Xenon.Compiler.Libraries.XelibWriter.Write(library,
                new Xenon.Compiler.Libraries.XelibWriteOptions("ClosureLibrary")));
        var consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using ClosureLibrary;
            namespace Consumer;
            void Run() { if (false) {
                Item value = Item();
                function void() callback = [move value]() => { };
            } }
            """));
        Assert.Empty(consumer.Diagnostics);
        var helper = Assert.Single(consumer.GetMirFunctions(lowered: false),
            function => function.Symbol.ClosureEnvironmentOwner is not null);
        var destructor = helper.Blocks.Select(block => block.Terminator).OfType<MirDrop>().First().Destructor;
        Assert.Contains(destructor, consumer.GetStaticImplementationSymbols());
        _ = new LlvmIrGenerator().GenerateForTarget(consumer, LlvmTargetOptions.CreateHost());
    }
    [Fact]
    public void FailedAndCancelledCompilationsDoNotPublishExecutableMir()
    {
        var invalid = Compilation.Create(SourceText.From("namespace Pipeline; int Main() { return missing; }"));
        Assert.Throws<InvalidOperationException>(() => invalid.GetMirFunctions());
        var valid = Compilation.Create(SourceText.From("namespace Pipeline; int Main() { return 1; }"));
        Assert.Throws<OperationCanceledException>(() => valid.GetMirFunctions(cancellation: new CancellationToken(true)));
        Assert.NotEmpty(valid.GetMirFunctions());
    }
}