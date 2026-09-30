using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class ResumableLifetimeTests
{
    internal const string Protocol = """
        namespace Example;
        public struct Result
        {
            public static void operator resolve(Result& value, int result) {}
            public static void operator reject(Result& value, int error) {}
            public static bool operator await(readonly Result& value, storage<int>& result, function void() next)
            { result = 42; return true; }
        }
        public struct Operation
        {
            public static bool operator await(readonly Operation& value, storage<int>& result, function void() next)
            { result = 1; return true; }
        }
        public async Result Borrow(int& value) { await Operation(); return value; }
        public async Result ArrayBorrow(int[]& values) { await Operation(); return values[0]; }
        """;
    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(Protocol + source, "lifetimes.xe"));
    private static void Valid(Compilation compilation)
    {
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }
    [Fact]
    public void ExternalBorrowIsAnInferredResultContract()
    {
        Compilation compilation = Compile("Result Forward(int& value) { return Borrow(value); }");
        Valid(compilation);
        foreach (string name in new[] { "Borrow", "Forward" })
            Assert.Contains(compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == name).Symbol.ResultLifetimeDependencies,
                dependency => dependency.Kind == LifetimeDependencyKind.ParameterBorrow && dependency.Ordinal == 0);
    }
    [Theory]
    [InlineData("async Result Use() { int value = 42; Result task = Borrow(value); return await task; }")]
    [InlineData("async Result Use() { int[] values = int[16]; values[0] = 42; int[]& view = values; Result task = ArrayBorrow(view); return await task; }")]
    [InlineData("struct Obj { public int Value; public async Result Read() { await Operation(); return this.Value; } } async Result Use() { Obj obj = Obj(); Result task = obj.Read(); return await task; }")]
    [InlineData("async Result Use() { int value = 42; Result a = Borrow(value); Result b = a; return await b; }")]
    [InlineData("async Result Use() { int value = 42; Result a = Borrow(value); Result b = move a; return await b; }")]
    [InlineData("T Identity<T>(T value) { return move value; } async Result Use() { int value = 42; Result task = Identity<Result>(Borrow(value)); return await task; }")]
    [InlineData("async Result Use() { int value = 42; function Result() fn = async [&value]() => { await Operation(); return value; }; Result task = fn(); return await task; }")]
    public void ScopedExternalOperationsAreValid(string source) => Valid(Compile(source));

    [Theory]
    [InlineData("Result Make() { int value = 42; return Borrow(value); }")]
    [InlineData("Result Make() { int[] values = int[16]; return ArrayBorrow(values); }")]
    [InlineData("static struct Global { public static Result Task; } void Start() { int value = 1; Global.Task = Borrow(value); }")]
    [InlineData("struct Obj { public int Value; public async Result Read() { await Operation(); return this.Value; } } async Result Use() { Result task; { Obj obj = Obj(); task = obj.Read(); } return await task; }")]
    [InlineData("struct Holder { public Result Task; public void Store() { int value = 1; this.Task = Borrow(value); } }")]
    [InlineData("T Identity<T>(T value) { return move value; } Result Make() { int value = 42; return Identity<Result>(Borrow(value)); }")]
    [InlineData("Result Make() { int value = 42; Result task = Borrow(value); Result copy = task; return copy; }")]
    [InlineData("Result Make() { int value = 42; Result task = Borrow(value); return move task; }")]
    [InlineData("Result Make() { int value = 42; function Result() fn = async [&value]() => { await Operation(); return value; }; return fn(); }")]
    public void DependentValuesCannotEscape(string source)
    {
        Compilation compilation = Compile(source);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }
    [Theory]
    [InlineData("struct Holder { public Result Task; } async Result Use() { int value = 1; Holder h = Holder(); h.Task = Borrow(value); return await h.Task; }")]
    [InlineData("struct Holder { public Result Task; public Result Then() { return Task; } } async Result Use() { int value = 1; Holder h = Holder(); h.Task = Borrow(value); Result task = h.Then(); return await task; }")]
    [InlineData("int& Ref(int& value) { return value; } Result Forward(int& value) { return Borrow(Ref(value)); }")]
    [InlineData("async Result Use(int* pointer) { await Operation(); return *pointer; }")]
    public void FieldsForwardingAndRawPointersPreserveTheirSemantics(string source) => Valid(Compile(source));

    [Theory]
    [InlineData("struct Holder { public Result Task; } void Store(Holder& h, Result task) { h.Task = task; } void Use(Holder& h) { int value = 1; Store(h, Borrow(value)); }")]
    [InlineData("struct Holder { public Result Task; public Result Then() { return Task; } } Result Make() { int value = 1; Holder h = Holder(); h.Task = Borrow(value); return h.Then(); }")]
    [InlineData("struct Holder { public Result Task; } void Use(Holder& external) { int value = 1; Holder& alias = external; alias.Task = Borrow(value); }")]
    [InlineData("async Result Use(bool branch) { Result task = Result(); if (branch) { int value = 1; task = Borrow(value); } return await task; }")]
    public void IndirectStoresAndChainedCallsCannotEraseDependencies(string source) =>
        Assert.Contains(Compile(source).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);

    [Fact]
    public void UnionIncludesOnlyParametersLiveAtSomeSuspension()
    {
        Compilation compilation = Compile("async Result Many(int& a, int& b, int& c) { int saved = a; await Operation(); saved += b; await Operation(); return saved + c; }");
        Valid(compilation);
        var contract = compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == "Many").Symbol.ResultLifetimeDependencies;
        Assert.DoesNotContain(contract, dependency => dependency.Ordinal == 0);
        Assert.Contains(contract, dependency => dependency.Kind == LifetimeDependencyKind.ParameterBorrow && dependency.Ordinal == 1);
        Assert.Contains(contract, dependency => dependency.Kind == LifetimeDependencyKind.ParameterBorrow && dependency.Ordinal == 2);
    }

    [Theory]
    [InlineData("async Result Use(bool branch) { int value = 1; if (branch) { Result task = Borrow(value); await task; } return 1; }")]
    [InlineData("async Result Use() { int value = 1; for (int i = 0; i < 2; i++) { Result task = Borrow(value); await task; } return 1; }")]
    [InlineData("async Result Use(int branch) { int value = 1; Result task = Borrow(value); switch (branch) { case 0: { await task; break; } default: { await task; break; } } return 1; }")]
    public void CompletionIsTrackedPerExecutedPath(string source) => Valid(Compile(source));

    [Theory]
    [InlineData("async Result Use() { int value = 1; Result task = Borrow(value); value = 2; return await task; }")]
    [InlineData("async Result Use() { int value = 1; Result task = Borrow(value); int moved = move value; return await task; }")]
    public void PendingBorrowPreventsOwnerReplacement(string source) =>
        Assert.Contains(Compile(source).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PendingBorrowedOperation);

    [Fact]
    public void ExceptionalExitCannotAbandonPendingBorrow()
    {
        var compilation = Compile("void Fail() { throw 1; } async Result Use() { int value = 1; Result task = Borrow(value); Fail(); return await task; }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PendingBorrowedOperation);
    }

    [Fact]
    public void HandledFailureCanKeepOwnerAliveUntilAwait()
    {
        Valid(Compile("void Fail() { throw 1; } async Result Use() { int value = 1; Result task = Borrow(value); try { Fail(); } catch (readonly int& error) { } return await task; }"));
    }

    [Fact]
    public void InterfaceDispatchCannotEraseOperationReceiverLifetime()
    {
        var compilation = Compile("""
            interface Reader { Result Read(); }
            struct Concrete : Reader { public int Value; public async Result Read() { await Operation(); return Value; } }
            Result Make() { Concrete value = Concrete(); Reader& alias = value; return alias.Read(); }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }

    [Fact]
    public void VirtualDispatchIncludesOverrideLifetimeRequirements()
    {
        var compilation = Compile("""
            struct Base { public virtual Result Read() { return Result(); } }
            struct Derived : Base { public int Value; public override async Result Read() { await Operation(); return Value; } }
            Result Make() { Derived value = Derived(); Base& alias = value; return alias.Read(); }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }

    [Theory]
    [InlineData("Result Make() { return Read(Thing()); }")]
    [InlineData("void Start() { Result task = Read(Thing()); }")]
    [InlineData("void Start() { Read(Thing()); }")]
    public void TemporaryReferentCannotEscapeItsFullExpression(string caller)
    {
        var compilation = Compile("struct Thing { public int Value; } async Result Read(readonly Thing& thing) { await Operation(); return thing.Value; } " + caller);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id is DiagnosticIds.ValueLifetimeEscape or DiagnosticIds.PendingBorrowedOperation);
    }

    [Theory]
    [InlineData("struct Pair { public Result First; public Result Second; } async Result Use() { int a = 1; int b = 2; Pair pair = Pair(); pair.First = Borrow(a); pair.Second = Borrow(b); return await pair.First; }")]
    [InlineData("async Result Unrelated(Result input) { await Operation(); return 1; } async Result Use() { int value = 1; Result task = Borrow(value); return await Unrelated(task); }")]
    public void CompletingAnotherOperationCannotDischargePendingBorrow(string source) =>
        Assert.Contains(Compile(source).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PendingBorrowedOperation);

    [Fact]
    public void IndependentFieldOperationsCanBothBeCompleted()
    {
        Valid(Compile("struct Pair { public Result First; public Result Second; } async Result Use() { int a = 1; int b = 2; Pair pair = Pair(); pair.First = Borrow(a); pair.Second = Borrow(b); int first = await pair.First; return first + await pair.Second; }"));
    }

    [Fact]
    public void OwningWrapperDoesNotEraseBorrowedContents()
    {
        Compilation compilation = Compile("""
            struct Box { public Result Task; public Box(Result task) { Task = task; } }
            unique<Box> Make() { int value = 1; return new Box(Borrow(value)); }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }

    [Fact]
    public void IgnoringDependentOperationInAWrapperCannotHideItsEscape()
    {
        Compilation compilation = Compile("void Detach(int& value) { Borrow(value); } void Start() { int local = 1; Detach(local); }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }

    [Fact]
    public void BorrowedAwaitResultKeepsReferentProvenance()
    {
        Compilation compilation = Compile("""
            struct View { public readonly int& Value; public View(readonly int& value) { Value = value; } }
            struct ViewOperation
            {
                public readonly int& Value;
                public ViewOperation(readonly int& value) { Value = value; }
                public static bool operator await(readonly ViewOperation& operation, storage<View>& result, function void() next)
                { result = View(operation.Value); return true; }
            }
            async Result Use(int& value) { View view = await ViewOperation(value); return view.Value; }
            """);
        Valid(compilation);
        Assert.Contains(compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == "Use").Symbol.ResultLifetimeDependencies,
            dependency => dependency.Kind == LifetimeDependencyKind.ParameterBorrow && dependency.Ordinal == 0);
    }

    [Fact]
    public void MetadataOnlyXelibChecksEscapesWithoutOrdinaryBodies()
    {
        var reference = XelibReader.Read(XelibWriter.Write(Compile(""), new XelibWriteOptions("Contracts")), metadataOnly: true);
        Assert.Empty(reference.ImplementationFunctions);
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Example; namespace App; Result Make() { int value = 1; return Borrow(value); }"));
        Assert.Contains(consumer.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }

    [Fact]
    public void DeadBorrowDoesNotConstrainResult()
    {
        Compilation compilation = Compile("async Result Before(int& value) { int saved = value; await Operation(); return saved; } Result Make() { int value = 42; return Before(value); }");
        Valid(compilation);
        Assert.Empty(compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == "Before").Symbol.ResultLifetimeDependencies);
    }
    [Theory]
    [InlineData("void Start() { int value = 42; Result task = Borrow(value); }")]
    [InlineData("async Result Use(bool ready) { int value = 42; Result task = Borrow(value); if (ready) { await task; } return 1; }")]
    public void DroppingHandleDoesNotProveOperationCompletion(string source) =>
        Assert.Contains(Compile(source).Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PendingBorrowedOperation);

    [Fact]
    public void XelibExportsContractsAndGenericForwarding()
    {
        Compilation library = Compile("public T Identity<T>(T value) { return move value; } public Result Forward(int& value) { return Borrow(value); }");
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Lifetimes")));
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Example; namespace App;
            Result Make() { int value = 42; return Identity<Result>(Forward(value)); }
            """));
        Assert.Contains(consumer.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueLifetimeEscape);
    }
}
