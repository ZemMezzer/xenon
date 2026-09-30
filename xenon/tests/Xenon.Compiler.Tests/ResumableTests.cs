using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class ResumableTests
{
    private const string Protocol = """
        namespace Example;
        struct Result
        {
            public static void operator resolve(Result& target, int value) {}
            public static void operator resolve(Result& target) {}
            public static void operator reject(Result& target, int error) {}
        }
        struct Operation
        {
            public static bool operator await(readonly Operation& value, storage<int>& result, function void() continuation)
            { result = 42; return true; }
        }
        """;

    [Fact]
    public void AwaitRetainsDedicatedBoundNode()
    {
        Compilation compilation = Compile("async Result Use() { Operation op = Operation(); int value = await op; return value; }");
        Assert.Empty(compilation.Diagnostics);
        BoundBlockStatement body = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use").Body;
        Assert.True(body.IsResumable);
        Assert.Single(SyntaxNavigator.DescendantNodesAndSelf(compilation.SyntaxTrees.Single().Root).OfType<AwaitExpressionSyntax>());
    }

    [Fact]
    public void CoroutineCodegenUsesSuspensionAndRetry()
    {
        Compilation compilation = Compile("async Result Use() { int value = await Operation(); return value; }");
        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.Contains(".resumable.resume", ir);
        Assert.Contains("await.retry", ir);
        Assert.DoesNotContain("call i8 @llvm.coro.suspend", ir);
    }

    [Theory]
    [InlineData("int", "storage<int>&", "function void()")]
    [InlineData("bool", "int&", "function void()")]
    [InlineData("bool", "readonly storage<int>&", "function void()")]
    [InlineData("bool", "storage<int>&", "function int()")]
    public void MalformedAwaitIsDiagnosed(string result, string storage, string continuation)
    {
        Compilation compilation = Compilation.Create(SourceText.From($"namespace Example; struct Op {{ public static {result} operator await(Op& op, {storage} slot, {continuation} next) {{ return 0; }} }}"));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidOperatorDeclaration);
    }

    [Fact]
    public void ConstructorsDoNotAcquireHiddenDefaultOverload()
    {
        Compilation compilation = Compile("struct Bad { public Bad(int x) {} } async Bad Use() { await Operation(); return; }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidResumableReturn);
    }

    [Fact]
    public void ImportedGenericResumableBodyPreservesOperatorsAndSuspension()
    {
        Compilation library = Compilation.Create(SourceText.From(Protocol.Replace("struct Result", "public struct Result")
            .Replace("struct Operation", "public struct Operation") +
            "public async Result Forward<T>(T input) { int value = await Operation(); return value; }"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("Resumable"));
        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Example; namespace App; Result Use() { return Forward<int>(1); }"));
        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(consumer, LlvmTargetOptions.CreateHost());
        Assert.Contains(".resumable.resume", ir);
    }

    [Fact]
    public void GenericAwaitAndReturnTypesUseOrdinarySpecialization()
    {
        Compilation compilation = Compile("""
            struct GenericResult<T>
            {
                public static void operator resolve(GenericResult<T>& value, T result) {}
                public static void operator reject(GenericResult<T>& value, int error) {}
            }
            struct GenericOperation<T>
            {
                public static bool operator await(GenericOperation<T>& value, storage<int>& result, function void() next)
                { result = 42; return true; }
            }
            async GenericResult<int> Use() { GenericOperation<int> op = GenericOperation<int>(); return await op; }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void CompletionOperationsUseOperators()
    {
        Compilation compilation = Compile("void Use() { Result result = Result(); resolve(result); resolve(result, 1); reject(result, 2); }");
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("await Operation(); throw true;")]
    [InlineData("await Operation(); Fail(); return 1;")]
    [InlineData("Bomb bomb = Bomb(); await Operation(); return 1;")]
    public void MissingTypedRejectionIsDiagnosed(string body)
    {
        Compilation compilation = Compile("void Fail() { throw true; } struct Bomb { public ~Bomb() { throw true; } } async Result Use() { " + body + " }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MissingCompletionOperator);
    }

    [Fact]
    public void HandledExceptionDoesNotRequireRejectionOverload()
    {
        Compilation compilation = Compile("async Result Use() { await Operation(); try { throw true; } catch (readonly bool& error) { return 1; } }");
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("async Result Use() { try { throw 1; } catch (readonly int& error) { await Operation(); } return 1; }")]
    [InlineData("async Result Use() { try { return 1; } finally { await Operation(); } }")]
    public void UnprovenSuspensionLifetimesAreDiagnosed(string source)
    {
        Compilation compilation = Compile(source);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id is DiagnosticIds.BorrowAcrossAwait or DiagnosticIds.InvalidAwaitContext);
    }

    [Fact]
    public void AwaitPrefixPrecedenceAndCallArgumentsParse()
    {
        SyntaxTree syntax = SyntaxTree.Parse(SourceText.From("namespace Example; async Result Use() { await operation; int x = await operation + 1; Consume(await operation); return x; }"));
        Assert.Empty(syntax.Diagnostics);
        Assert.Equal(3, SyntaxNavigator.DescendantNodesAndSelf(syntax.Root).OfType<AwaitExpressionSyntax>().Count());
    }

    [Theory]
    [InlineData("struct Missing {} async Result Use() { await Missing(); return 1; }", DiagnosticIds.MissingAwaitOperator)]
    [InlineData("struct Ambiguous { public static bool operator await(readonly Ambiguous& a, storage<int>& r, function void() c) { r = 1; return true; } public static bool operator await(readonly Ambiguous& a, function void() c) { return true; } } async Result Use() { await Ambiguous(); return 1; }", DiagnosticIds.AmbiguousCall)]
    [InlineData("struct Bad { public Bad() {} public static void operator reject(Bad& a, int e) {} } async Bad Use() { await Operation(); return 1; }", DiagnosticIds.MissingCompletionOperator)]
    [InlineData("struct Bad { public static void operator resolve(Bad& a, int v) {} } async Bad Use() { await Operation(); throw 1; }", DiagnosticIds.MissingCompletionOperator)]
    [InlineData("struct Bad { private Bad() {} } async Bad Use() { await Operation(); return 1; }", DiagnosticIds.InvalidResumableReturn)]
    [InlineData("struct Bad { public static int operator resolve(Bad& a) { return 1; } }", DiagnosticIds.InvalidOperatorDeclaration)]
    [InlineData("struct Bad { public static void operator resolve(readonly Bad& a) {} }", DiagnosticIds.InvalidOperatorDeclaration)]
    [InlineData("struct Bad { public static int operator reject(Bad& a, int e) { return 1; } }", DiagnosticIds.InvalidOperatorDeclaration)]
    [InlineData("struct Bad { public static void operator reject(Bad a, int e) {} }", DiagnosticIds.InvalidOperatorDeclaration)]
    public void ProtocolFailuresAreOrdinaryDiagnostics(string source, string expected)
    {
        Compilation compilation = Compile(source);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == expected);
    }

    [Fact]
    public void ImportedGenericAwaitOperandAndReturnAreSpecializedWithoutSource()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Example;
            public struct Task<T>
            {
                public static bool operator await(readonly Task<T>& task, storage<T>& result, function void() next) { throw 1; }
                public static void operator resolve(Task<T>& task, T value) {}
                public static void operator reject(Task<T>& task, int error) {}
            }
            public async Task<T> Forward<T>(Task<T> input) { return await input; }
            """));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("AwaitGeneric")));
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Example; namespace App; Task<int> Use() { return Forward<int>(Task<int>()); }"));
        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(consumer, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void CompletionCannotBorrowDestroyedFrameLocal()
    {
        Compilation compilation = Compile("""
            struct Resource { public int Value; public ~Resource() {} }
            struct Handle
            {
                public static void operator resolve(Handle& target, readonly Resource& value) {}
                public static void operator reject(Handle& target, int error) {}
            }
            async Handle Use() { Resource resource = Resource(); await Operation(); return resource; }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.EscapingLocalReference);
    }

    [Theory]
    [InlineData("async Result Use(int& r) { int value = r; await Operation(); return value; }")]
    [InlineData("async Result Use(int& r, bool branch) { int value = 1; if (branch) { value = r; } await Operation(); return value; }")]
    [InlineData("async Result Use(int[]& r) { int value = r[0]; await Operation(); return value; }")]
    [InlineData("async Result Use() { int[] values = int[16]; values[0] = 42; await Operation(); return values[0]; }")]
    public void PreSuspensionBorrowsAndInlineArraysAreAccepted(string source)
    {
        Compilation compilation = Compile(source);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData("async Result Use(int[]& r) { await Operation(); return r[0]; }")]
    [InlineData("async Result Use(int& r) { int& alias = r; await Operation(); return alias; }")]
    [InlineData("async Result Use(int& r) { for (int i = 0; i < 2; i++) { int value = r; await Operation(); } return 1; }")]
    [InlineData("async Result Use(int& r) { try { await Operation(); } finally { r = 1; } return 1; }")]
    [InlineData("struct Reader { public int Value; public async Result Read() { await Operation(); return this.Value; } }")]
    public void ExternalBorrowsInferSuspensionDependencies(string source)
    {
        Compilation compilation = Compile(source);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.Contains(compilation.SemanticModel.Functions, function => function.Body.IsResumable && !function.Symbol.ResultLifetimeDependencies.IsEmpty);
    }

    [Fact]
    public void DynamicArrayAcrossAwaitStillRequiresFixedLayout()
    {
        Compilation compilation = Compile("async Result Use(int count) { int[] values = int[count]; await Operation(); return values[0]; }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowAcrossAwait);
    }

    [Fact]
    public void FrameUsesExistingPinRelocationRulesAndPerAwaitLiveness()
    {
        Compilation compilation = Compile("async Result Use(int& input) { int value = input; await Operation(); { int later = 1; await Operation(); value += later; } return value; }");
        Assert.Empty(compilation.Diagnostics);
        BoundFunction function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        ResumableFrameAnalysis plan = ResumableFrameAnalysis.Analyze(function.Symbol, function.Body, compilation.TypeFactory);
        Assert.False(TypeFacts.CanRelocate(plan.FrameType));
        Assert.False(TypeFacts.CanMove(plan.FrameType));
        Assert.False(TypeFacts.CanCopy(plan.FrameType));
        Assert.DoesNotContain(plan.LiveAcross, symbol => symbol.Name == "input");
        Assert.Equal(2, plan.LiveValues.Count);
        Assert.Single(plan.LiveValues.Values.Where(live => live.Any(symbol => symbol.Name == "later")));
    }

    [Fact]
    public void ReplacedArrayStorageDoesNotCrossTheLaterSuspension()
    {
        Compilation compilation = Compile("async Result Use(int count) { int[] values = int[count]; values[0] = 1; values = int[16]; await Operation(); return values[0]; }");
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        BoundFunction function = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var plan = ResumableFrameAnalysis.Analyze(function.Symbol, function.Body, compilation.TypeFactory);
        Assert.Single(plan.RetainedArrays);
        Assert.True(ResumableFrameAnalysis.TryGetConstantLength(plan.RetainedArrays.Single(), out ulong length));
        Assert.Equal(16UL, length);
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void BorrowOfOwnedHeapValueCanCrossSuspension()
    {
        Compilation compilation = Compile("struct Item { public int Value; } async Result Use(unique<Item> owner) { Item& reference = *owner; await Operation(); return reference.Value; }");
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void ImportedGenericResumableArraysRecomputePinnedFrameLayout()
    {
        Compilation library = Compilation.Create(SourceText.From(Protocol.Replace("struct Result", "public struct Result").Replace("struct Operation", "public struct Operation") +
            "public async Result Forward<T>(T input) { int[] values = int[16]; values[3] = 42; int[]& view = values; await Operation(); return view[3]; }"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("InlineArrays")));
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Example; namespace App; Result Use() { return Forward<int>(1); }"));
        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(consumer, LlvmTargetOptions.CreateHost());
        Assert.Contains(".resumable.resume", ir);
    }

    [Fact]
    public void RuntimeSizedArrayTemporaryCannotEscapeItsNativeSegment()
    {
        Compilation compilation = Compile("async Result Use(int count) { return (int[count])[await Operation()]; }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowAcrossAwait);
    }

    [Fact]
    public void FixedArrayTemporaryCanRemainInThePinnedFrame()
    {
        Compilation compilation = Compile("async Result Use() { return (int[64])[await Operation()]; }");
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportedGenericBorrowLivenessUsesConcreteTypes(bool acrossAwait)
    {
        string statements = acrossAwait ? "await Operation(); T copy = move input;" : "T copy = move input; await Operation();";
        Compilation library = Compilation.Create(SourceText.From(Protocol.Replace("struct Result", "public struct Result").Replace("struct Operation", "public struct Operation") +
            "public async Result Forward<T>(T input) { " + statements + " return 1; }"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("BorrowLiveness")));
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Example;
            namespace App;
            struct Borrowed { public int& Value; public Borrowed(int& value) { Value = value; } }
            Result Use(int& input) { Borrowed borrowed = Borrowed(input); return Forward<Borrowed>(move borrowed); }
            """));
        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        Assert.Equal(acrossAwait, consumer.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use").Symbol.ResultLifetimeDependencies.Length > 0);
        _ = new LlvmIrGenerator().GenerateForTarget(consumer, LlvmTargetOptions.CreateHost());
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(Protocol + source, "resumable.xe"));
}
