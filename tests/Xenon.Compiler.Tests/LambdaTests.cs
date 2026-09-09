using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class LambdaTests
{
    [Fact]
    public void DirectFunctionValueAndPointerCallsContextualizeDeferredArguments()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Accept(function void(int) callback) { }
            void AcceptPointer(function void(int)* callback) { }
            void Handle(int value) { }
            void Test()
            {
                function void(function void(int)) viaValue = Accept;
                function void(function void(int)*)* viaPointer = &AcceptPointer;
                viaValue((int value) => { });
                viaValue(Handle);
                viaPointer((int value) => { });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(2, compilation.SemanticModel.Functions.Count(function => function.Symbol.IsLambda));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void QualifiedGenericCallsReuseContextualFunctionInference()
    {
        Compilation library = Compile("""
            namespace Library;
            public void Run<T>(function void(T) callback) { }
            """);
        Assert.Empty(library.Diagnostics);
        Compilation application = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From("""
                namespace Application;
                void Handle(int value) { }
                void Test()
                {
                    Library.Run((int value) => { });
                    Library.Run(Handle);
                }
                """, "application.xe"));

        Assert.Empty(application.Diagnostics);
        Assert.Single(application.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void LaterArgumentsDisambiguateNamedFunctionsDuringGenericInference()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            void Handle(int value) { }
            void Handle(String value) { }
            void Use<T>(function void(T) callback, T value) { }
            void Test() { Use(Handle, 1); }
            """);

        Assert.Empty(compilation.Diagnostics);
        CallExpressionSyntax call = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root).OfType<CallExpressionSyntax>()
            .Single(candidate => candidate.Target is NameExpressionSyntax { IdentifierToken.Text: "Use" });
        FunctionSymbol selected = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(selected.Parameters[0].Type).ParameterTypes[0]));
    }

    [Fact]
    public void NamedFunctionReturnCostRanksOuterLambdaOverloads()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function void() callback) { return Wrapper(); }
            }
            void Handle() { }
            void Run(function function void()() factory) { }
            void Run(function Wrapper() factory) { }
            void Test() { Run([]() => { return Handle; }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol outer = Assert.IsType<FunctionValueTypeSymbol>(SelectedRun(compilation).Parameters[0].Type);
        Assert.IsType<FunctionValueTypeSymbol>(outer.ReturnType);
    }

    [Fact]
    public void QualifiedNamespaceAndStaticFunctionsBecomeFunctionValues()
    {
        Compilation library = Compile("""
            namespace Library;
            public void Handle(int value) { }
            public void Invoke(function void(int) callback) { }
            """);
        Assert.Empty(library.Diagnostics);
        Compilation application = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From("""
                namespace Application;
                static struct Math
                {
                    public static int Twice(int value) { return value * 2; }
                }
                struct Wrapper
                {
                    public static Wrapper operator implicit(function void(int) callback) { return Wrapper(); }
                }
                void Use(function void(int) callback) { }
                void Test()
                {
                    function void(int) callback = Library.Handle;
                    Use(Library.Handle);
                    function int(int) transform = Math.Twice;
                    Wrapper wrapped = Library.Handle;
                }
                """, "application.xe"));

        Assert.Empty(application.Diagnostics);
    }

    [Fact]
    public void InstanceValueMembersShadowNamedFunctionContext()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            void Handle(int value) { }
            void Use(function void(int) callback) { }
            struct Container
            {
                public function void(String) Handle;
                public void Test() { Use(Handle); }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch);
    }

    [Fact]
    public void QualifiedValueMembersShadowNamespaceFunctionContext()
    {
        Compilation library = Compile("""
            namespace Library;
            public void Handle(int value) { }
            """);
        Compilation application = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From("""
                namespace Application;
                struct String { }
                struct Receiver
                {
                    public function void(String) Handle;
                    public void Invoke(function void(String) callback) { }
                }
                void Use(function void(int) callback) { }
                struct Container
                {
                    public Receiver Library { get { return Receiver(); } }
                    public void Test()
                    {
                        Use(Library.Handle);
                        Library.Invoke((int value) => { });
                    }
                }
                """, "application.xe"));

        Assert.Contains(application.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch);
        Assert.Empty(application.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void RejectedCallsRollBackDeferredMoveCaptures()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Resource { public void Use() { } }
            struct Receiver { }
            void Run(function void(int) callback) { }
            void Generic<T>(function void(T) callback) { }
            void Test()
            {
                unique<Resource> first = new Resource();
                Missing<int>([move first]() => { first->Use(); });
                first->Use();
                unique<Resource> second = new Resource();
                Receiver receiver = Receiver();
                receiver.Missing([move second]() => { second->Use(); });
                second->Use();
                unique<Resource> third = new Resource();
                Run([move third](String value) => { third->Use(); });
                third->Use();
                unique<Resource> fourth = new Resource();
                Generic<int>([move fourth](String value) => { fourth->Use(); });
                fourth->Use();
            }
            """);

        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void ReadonlyCallAndIndexerFailuresRollBackDeferredMoveCaptures()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            struct Holder
            {
                public int this[function void() callback] { get { return 0; } set { } }
            }
            struct Container
            {
                public void Run(function void() callback) { }
                public void readonly Test(readonly Holder holder)
                {
                    unique<Resource> first = new Resource();
                    Run([move first]() => { first->Use(); });
                    first->Use();
                    unique<Resource> second = new Resource();
                    holder[[move second]() => { second->Use(); }] = 1;
                    second->Use();
                }
            }
            """);

        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void DirectCallableMismatchRollsBackDeferredMoveCapture()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            void Accept(function void()* callback) { }
            void Test()
            {
                function void(function void()*)* via = &Accept;
                unique<Resource> resource = new Resource();
                via([move resource]() => { resource->Use(); });
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.LambdaCaptureNotSupported);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void CallableExpressionsCannotLeakIntoArrayIndexCodegen()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Handle() { }
            void Test()
            {
                int[] values = int[1];
                int first = values[() => { return 0; }];
                int second = values[Handle];
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.IndexMustBeInteger));
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void CompoundIndexerRejectsNoncopyableByValueArguments()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Key { }
            struct Registry
            {
                public int this[unique<Key> key] { get { return 1; } set { } }
            }
            template RegistryLike
            {
                int this[unique<Key> key] { get; set; }
            }
            void Update<T>(T registry, unique<Key> key) where T : RegistryLike
            {
                registry[move key] += 2;
            }
            void Test()
            {
                Registry registry = Registry();
                unique<Key> key = new Key();
                registry[move key] += 2;
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(
            diagnostic => diagnostic.Id == DiagnosticIds.ValueNotCopyable));
    }

    [Fact]
    public void ConstructorsContextualizeLambdaArgumentsAndSelectOverloads()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Handler
            {
                public Handler(function void(int) callback) { }
                public Handler(function void(String) callback) { }
            }
            void Test() { Handler* handler = new Handler((int value) => { }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        NewExpressionSyntax creation = SyntaxNavigator.DescendantNodesAndSelf(
            compilation.SyntaxTrees.Single().Root).OfType<NewExpressionSyntax>().Single();
        FunctionSymbol constructor = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetSymbolInfo(creation).Symbol);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(constructor.Parameters[0].Type).ParameterTypes[0]));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void CapturingLambdaEliminatesRawPointerConstructor()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Handler
            {
                public Handler(function void(int) callback) { }
                public Handler(function void(int)* callback) { }
            }
            void Test()
            {
                int offset = 1;
                Handler* handler = new Handler([offset](int value) => { int result = value + offset; });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        NewExpressionSyntax creation = SyntaxNavigator.DescendantNodesAndSelf(
            compilation.SyntaxTrees.Single().Root).OfType<NewExpressionSyntax>().Single();
        FunctionSymbol constructor = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetSymbolInfo(creation).Symbol);
        Assert.IsType<FunctionValueTypeSymbol>(constructor.Parameters[0].Type);
    }

    [Fact]
    public void SpecializedGenericConstructorContextualizesLambda()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Holder<T>
            {
                public Holder(function void(T) callback) { }
            }
            void Test() { Holder<int>* holder = new Holder<int>((int value) => { }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void ConstructorChainingContextualizesLambdaArguments()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Base
            {
                public Base(function void(int) callback) { }
            }
            struct Handler : Base
            {
                public Handler() : this([](int value) => { }) { }
                public Handler(function void(int) callback) : base(callback) { }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void BaseConstructorChainingContextualizesLambdaArguments()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Base
            {
                public Base(function void(int) callback) { }
            }
            struct Derived : Base
            {
                public Derived() : base([](int value) => { }) { }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void IndexersContextualizeLambdaArgumentsAndSelectOverloads()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Registry
            {
                public int this[function bool(int) predicate] { get { return 1; } }
                public int this[function bool(String) predicate] { get { return 2; } }
            }
            int Test(Registry registry)
            {
                return registry[(int value) => { return value > 0; }];
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        IndexExpressionSyntax access = SyntaxNavigator.DescendantNodesAndSelf(
            compilation.SyntaxTrees.Single().Root).OfType<IndexExpressionSyntax>().Single();
        IndexerSymbol indexer = Assert.IsType<IndexerSymbol>(
            compilation.SemanticModel.GetSymbolInfo(access).Symbol);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(indexer.Parameters[0].Type).ParameterTypes[0]));
    }

    [Fact]
    public void CompoundIndexersCopyOwnedFunctionArgumentsForGetterReuse()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface IAccessor
            {
                int this[function int() callback] { get; set; }
            }
            struct Accessor : IAccessor
            {
                public int this[function int() callback]
                {
                    get { return callback(); }
                    set { }
                }
            }
            void Test(Accessor concrete, IAccessor abstraction)
            {
                int captured = 1;
                concrete[[captured]() => { return captured; }] += 2;
                abstraction[[captured]() => { return captured; }] += 3;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.True(ir.Split("function.copy.control", StringSplitOptions.None).Length > 2);
        Assert.Contains("compound.argument.0.guard", ir);
    }

    [Fact]
    public void IndexerSetterContextualizesFunctionValue()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Registry
            {
                public function void(int) this[int index]
                {
                    get { return Handle; }
                    set { }
                }
            }
            void Handle(int value) { }
            void Test(Registry registry) { registry[0] = (int value) => { }; }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void NamedFunctionFlowsThroughFunctionValueUserConversion()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Wrapper
            {
                public int Marker;
                public static Wrapper operator implicit(function void(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function void(String) callback) { return Wrapper(); }
            }
            void Handle(int value) { }
            void Test() { Wrapper wrapper = Handle; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NameExpressionSyntax handle = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root).OfType<NameExpressionSyntax>()
            .Single(name => name.IdentifierToken.Text == "Handle");
        FunctionSymbol conversion = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetConversionSymbolInfo(handle).Symbol);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(conversion.Parameters[0].Type).ParameterTypes[0]));
    }

    [Fact]
    public void NamedFunctionPrefersFunctionValueOverRawPointerConversion()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public int Marker;
                public static Wrapper operator implicit(function void(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function void(int)* callback) { return Wrapper(); }
            }
            void Handle(int value) { }
            void Test() { Wrapper wrapper = Handle; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NameExpressionSyntax handle = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root).OfType<NameExpressionSyntax>()
            .Single(name => name.IdentifierToken.Text == "Handle");
        FunctionSymbol conversion = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetConversionSymbolInfo(handle).Symbol);
        Assert.IsType<FunctionValueTypeSymbol>(conversion.Parameters[0].Type);
    }

    [Fact]
    public void NamedFunctionCanUseRawPointerConversionOperatorWithoutRelaxingPointerAssignment()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function void(int)* callback) { return Wrapper(); }
            }
            void Handle(int value) { }
            void Test() { Wrapper wrapper = Handle; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NameExpressionSyntax handle = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root).OfType<NameExpressionSyntax>()
            .Single(name => name.IdentifierToken.Text == "Handle");
        FunctionSymbol conversion = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetConversionSymbolInfo(handle).Symbol);
        Assert.IsType<FunctionPointerTypeSymbol>(conversion.Parameters[0].Type);
    }

    [Fact]
    public void NamedFunctionSelectsOrdinaryCallbackOverload()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            void Run(function void(int) callback) { }
            void Run(function void(String) callback) { }
            void Handle(int value) { }
            void Test() { Run(Handle); }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(SelectedRun(compilation).Parameters[0].Type).ParameterTypes[0]));
    }

    [Fact]
    public void ContextualFunctionValuesWorkInPropertiesAssignmentsAndReturns()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public int Marker;
                public static Wrapper operator implicit(function void(int) callback) { return Wrapper(); }
            }
            struct Container
            {
                public function void(int) Callback { get { return Handle; } set { } }
                public Wrapper Wrapped { get { return Wrapper(); } set { } }
            }
            void Handle(int value) { }
            Wrapper Make() { return Handle; }
            Container MakeContainer() { return Container(); }
            void Test(Container container, Container[] items)
            {
                function void(int) callback = Handle;
                callback = Handle;
                container.Callback = (int value) => { };
                container.Callback(1);
                MakeContainer().Callback(2);
                (container).Callback(3);
                items[0].Callback(4);
                container.Wrapped = Handle;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void FailedLambdaResolutionRollsBackMoveCapture()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            void Ambiguous(function void(int) callback, int* marker) { }
            void Ambiguous(function void(int) callback, float* marker) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Ambiguous([move resource](int value) => { resource->Use(); }, null);
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
        LambdaCaptureSyntax capture = SyntaxNavigator.DescendantNodesAndSelf(
            compilation.SyntaxTrees.Single().Root).OfType<LambdaCaptureSyntax>().Single();
        Assert.Null(compilation.SemanticModel.GetSymbolInfo(capture).Symbol);
    }

    [Theory]
    [InlineData("Box<Bad>([move resource]() => { resource->Use(); });")]
    [InlineData("new Box<Bad>([move resource]() => { resource->Use(); });")]
    public void InvalidGenericStructConstraintsRollBackDeferredMoveCapture(string construction)
    {
        Compilation compilation = Compile($$"""
            namespace Example;
            template Runnable { void Run(); }
            struct Bad { }
            struct Resource { public void Use() { } }
            struct Box<T> where T : Runnable
            {
                public Box(function void() callback) { }
            }
            void Test()
            {
                unique<Resource> resource = new Resource();
                {{construction}}
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions,
            function => function.Symbol.IsLambda);
    }

    [Fact]
    public void MoveCapturedParameterCannotBeUsedAfterClosureCreation()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            void Test(unique<Resource> resource)
            {
                function void() callback = [move resource]() => { resource->Use(); };
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void InvalidContextualGenericTypeDoesNotMaterializeLambdaConversion()
    {
        Compilation compilation = Compile("""
            namespace Example;
            template Runnable { void Run(); }
            struct Bad { }
            struct Resource { public void Use() { } }
            struct Box<T> where T : Runnable
            {
                public static Box<T> operator implicit(function void() callback) { return Box<T>(); }
            }
            void Handle() { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Box<Bad> value = [move resource]() => { resource->Use(); };
                Box<Bad> named = Handle;
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions,
            function => function.Symbol.IsLambda);
        NameExpressionSyntax handle = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root).OfType<NameExpressionSyntax>()
            .Single(name => name.IdentifierToken.Text == "Handle");
        Assert.Null(compilation.SemanticModel.GetConversionSymbolInfo(handle).Symbol);
    }

    [Fact]
    public void FailedConstructorAndIndexerResolutionRollBackMoveCaptures()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            struct Handler
            {
                public Handler(function void(int) callback, int* marker) { }
                public Handler(function void(int) callback, float* marker) { }
            }
            struct Registry
            {
                public int this[function void(int) callback, int* marker] { get { return 1; } }
                public int this[function void(int) callback, float* marker] { get { return 2; } }
            }
            void Test(Registry registry)
            {
                unique<Resource> first = new Resource();
                Handler* handler = new Handler([move first](int value) => { first->Use(); }, null);
                first->Use();
                unique<Resource> second = new Resource();
                int result = registry[[move second](int value) => { second->Use(); }, null];
                second->Use();
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall));
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void MissingCallRollsBackMoveCapture()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Missing([move resource](int value) => { resource->Use(); });
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UnknownFunction);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Theory]
    [InlineData("&")]
    [InlineData("readonly &")]
    public void FailedLambdaResolutionDoesNotLeakBorrowCapture(string capture)
    {
        Compilation compilation = Compile($$"""
            namespace Example;
            void Ambiguous(function void(int) callback, int* marker) { }
            void Ambiguous(function void(int) callback, float* marker) { }
            void Test()
            {
                int value = 0;
                Ambiguous([{{capture}}value](int item) => { int copy = value + item; }, null);
                value = 1;
            }
            """);

        Assert.Single(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void LambdaReturnBodySelectsCompatibleOverload()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            void Run(function int(int) callback) { }
            void Run(function String(int) callback) { }
            void Test() { Run((int value) => { return value + 1; }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol callback = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedRun(compilation).Parameters[0].Type);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, callback.ReturnType));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void LambdaReturnBodySelectsVoidOrValueOverload()
    {
        Compilation voidCompilation = Compile("""
            namespace Example;
            void Run(function void(int) callback) { }
            void Run(function int(int) callback) { }
            void Test() { Run((int value) => { int copy = value; }); }
            """);
        Compilation valueCompilation = Compile("""
            namespace Example;
            void Run(function void(int) callback) { }
            void Run(function int(int) callback) { }
            void Test() { Run((int value) => { return value; }); }
            """);

        Assert.Empty(voidCompilation.Diagnostics);
        Assert.Empty(valueCompilation.Diagnostics);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Void,
            Assert.IsType<FunctionValueTypeSymbol>(SelectedRun(voidCompilation).Parameters[0].Type).ReturnType));
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(SelectedRun(valueCompilation).Parameters[0].Type).ReturnType));
    }

    [Fact]
    public void MultipleFunctionConversionOperatorsUseLambdaParameterType()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Wrapper
            {
                public static Wrapper operator implicit(function void(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function void(String) callback) { return Wrapper(); }
            }
            void Test() { Wrapper value = (int item) => { }; }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionSymbol selected = SelectedLambdaConversion(compilation);
        FunctionValueTypeSymbol input = Assert.IsType<FunctionValueTypeSymbol>(selected.Parameters[0].Type);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, input.ParameterTypes[0]));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void MultipleFunctionConversionOperatorsWorkForCallArguments()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Wrapper
            {
                public static Wrapper operator implicit(function void(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function void(String) callback) { return Wrapper(); }
            }
            void Accept(Wrapper callback) { }
            void Test() { Accept((int item) => { }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol input = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedLambdaConversion(compilation).Parameters[0].Type);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, input.ParameterTypes[0]));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void MultipleFunctionConversionOperatorsUseLambdaReturnBody()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Wrapper
            {
                public static Wrapper operator implicit(function int(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function String(int) callback) { return Wrapper(); }
            }
            void Test() { Wrapper value = (int item) => { return item + 1; }; }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol input = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedLambdaConversion(compilation).Parameters[0].Type);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, input.ReturnType));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData("[offset]", true)]
    [InlineData("[]", false)]
    public void FunctionValueConversionOperatorWinsOverRawPointer(string captures, bool declaresOffset)
    {
        Compilation compilation = Compile($$"""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function void(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function void(int)* callback) { return Wrapper(); }
            }
            void Test()
            {
                {{(declaresOffset ? "int offset = 1;" : string.Empty)}}
                Wrapper value = {{captures}}(int item) => { };
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.IsType<FunctionValueTypeSymbol>(SelectedLambdaConversion(compilation).Parameters[0].Type);
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void AllLambdaReturnPathsParticipateInOverloadApplicability()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            void Run(function int(bool) callback) { }
            void Run(function String(bool) callback) { }
            void Test()
            {
                Run((bool flag) =>
                {
                    if (flag) { return 10; }
                    return 20;
                });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(SelectedRun(compilation).Parameters[0].Type).ReturnType));
    }

    [Fact]
    public void ExactLambdaReturnConversionWinsOverInterfaceConversion()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface IValue { }
            struct Value : IValue { }
            void Run(function Value(int) callback) { }
            void Run(function IValue(int) callback) { }
            void Test() { Run((int item) => { return Value(); }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol callback = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedRun(compilation).Parameters[0].Type);
        Assert.IsType<StructTypeSymbol>(callback.ReturnType);
    }

    [Fact]
    public void EquallyConvertibleLambdaReturnsRemainAmbiguous()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface ILeft { }
            interface IRight { }
            struct Value : ILeft, IRight { }
            void Run(function ILeft(int) callback) { }
            void Run(function IRight(int) callback) { }
            void Test() { Run((int item) => { return Value(); }); }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void EquallyConvertibleLambdaConversionOperatorsAreAmbiguous()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface ILeft { }
            interface IRight { }
            struct Value : ILeft, IRight { }
            struct Wrapper
            {
                public static Wrapper operator implicit(function ILeft(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function IRight(int) callback) { return Wrapper(); }
            }
            void Test() { Wrapper value = (int item) => { return Value(); }; }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void ContextualOverloadProbeMovesCaptureExactlyOnce()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Resource { public void Use() { } }
            void Run(function void(int) callback) { }
            void Run(function void(String) callback) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Run([move resource](int item) => { resource.Use(); });
                resource.Use();
            }
            """);

        Assert.Single(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Theory]
    [InlineData("&")]
    [InlineData("readonly &")]
    public void ContextualOverloadProbeDoesNotLeakBorrowCapture(string modifier)
    {
        Compilation compilation = Compile($$"""
            namespace Example;
            struct String { }
            void Run(function void(int) callback) { }
            void Run(function void(String) callback) { }
            void Test()
            {
                int total = 0;
                Run([{{modifier}}total](int item) => { int copy = total + item; });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void MultipleFunctionConversionOperatorsSurviveSourceFreeLibraryRoundTrip()
    {
        Compilation library = Compile("""
            namespace Library;
            public struct String { }
            public struct Wrapper
            {
                public static Wrapper operator implicit(function int(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function String(int) callback) { return Wrapper(); }
            }
            """);
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library,
            new XelibWriteOptions("ContextualConversions")));
        Compilation application = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Library;
            namespace Application;
            void Test() { Wrapper value = (int item) => { return item + 1; }; }
            """, "application.xe"));

        Assert.Empty(application.Diagnostics);
        FunctionSymbol selected = SelectedLambdaConversion(application);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(selected.Parameters[0].Type).ReturnType));
        Assert.Single(application.SemanticModel.Functions, function => function.Symbol.IsLambda);
        _ = new LlvmIrGenerator().GenerateForTarget(application, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void AmbiguousLambdaConversionsRemainAmbiguousFromSourceFreeLibrary()
    {
        Compilation library = Compile("""
            namespace Library;
            public interface ILeft { }
            public interface IRight { }
            public struct Value : ILeft, IRight { }
            public struct Wrapper
            {
                public int Marker;
                public static Wrapper operator implicit(function ILeft() callback) { return Wrapper(); }
                public static Wrapper operator implicit(function IRight() callback) { return Wrapper(); }
            }
            """);
        Assert.Empty(library.Diagnostics);
        CompilationReference reference = XelibReader.Read(XelibWriter.Write(library,
            new XelibWriteOptions("AmbiguousContextualConversions")));
        Compilation application = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Library;
            namespace Application;
            void Test() { Wrapper value = []() => { return Value(); }; }
            """, "application.xe"));

        Assert.Contains(application.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.Empty(application.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void GenericLambdaConversionsResolveFromSourceFreeLibrary()
    {
        Compilation library = Compile("""
            namespace Library;
            public struct Action<T>
            {
                public int Marker;
                public static Action<T> operator implicit(function void(T) callback) { return Action<T>(); }
                public static Action<T> operator implicit(function int(T) callback) { return Action<T>(); }
            }
            """);
        Assert.Empty(library.Diagnostics);
        CompilationReference reference = XelibReader.Read(XelibWriter.Write(library,
            new XelibWriteOptions("GenericContextualConversions")));
        Compilation application = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Library;
            namespace Application;
            void Test() { Action<int> value = (int item) => { }; }
            """, "application.xe"));

        Assert.Empty(application.Diagnostics);
        FunctionValueTypeSymbol input = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedLambdaConversion(application).Parameters[0].Type);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Void, input.ReturnType));
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, input.ParameterTypes[0]));
        Assert.Single(application.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void GenericDestinationResolvesMultipleFunctionConversionOperators()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Action<T>
            {
                public static Action<T> operator implicit(function void(T) callback) { return Action<T>(); }
                public static Action<T> operator implicit(function int(T) callback) { return Action<T>(); }
            }
            void Test() { Action<int> action = (int item) => { }; }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol input = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedLambdaConversion(compilation).Parameters[0].Type);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Void, input.ReturnType));
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, input.ParameterTypes[0]));
    }

    [Fact]
    public void IncompatibleLambdaConversionBodyReportsOneSummaryDiagnostic()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            struct Wrapper
            {
                public static Wrapper operator implicit(function String(int) callback) { return Wrapper(); }
            }
            void Test() { Wrapper value = (int item) => { return item + 1; }; }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal(DiagnosticIds.TypeMismatch, diagnostic.Id);
        Assert.Contains("no implicit conversion", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(compilation.SemanticModel.Functions.Where(function => function.Symbol.IsLambda));
    }

    [Fact]
    public void LambdaBodyConversionIsIndependentFromOuterLambdaConversion()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Result
            {
                public int Value;
                public static Result operator implicit(int value) { return Result(); }
            }
            struct Wrapper
            {
                public static Wrapper operator implicit(function Result(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function void(int) callback) { return Wrapper(); }
            }
            void Test() { Wrapper value = (int item) => { return item; }; }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol input = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedLambdaConversion(compilation).Parameters[0].Type);
        Assert.Equal("Result", input.ReturnType.Name);
    }

    [Fact]
    public void UserConvertedReturnStillPrefersFunctionValueOverRawPointer()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Result
            {
                public int Value;
                public static Result operator implicit(int value) { return Result(); }
            }
            void Run(function Result() callback) { }
            void Run(function Result()* callback) { }
            void Test() { Run([]() => { return 1; }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.IsType<FunctionValueTypeSymbol>(SelectedRun(compilation).Parameters[0].Type);
    }

    [Fact]
    public void NestedLambdaReturnPreservesFunctionRepresentationCost()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function function void()() callback) { }
            void Run(function function void()*() callback) { }
            void Test() { Run([]() => { return []() => { }; }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol outer = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedRun(compilation).Parameters[0].Type);
        Assert.IsType<FunctionValueTypeSymbol>(outer.ReturnType);
    }

    [Fact]
    public void NestedLambdaReturnPrefersDirectOverUserConversion()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public int Value;
                public static Wrapper operator implicit(function void() callback) { return Wrapper(); }
            }
            void Run(function function void()() callback) { }
            void Run(function Wrapper() callback) { }
            void Test() { Run([]() => { return []() => { }; }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol outer = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedRun(compilation).Parameters[0].Type);
        Assert.IsType<FunctionValueTypeSymbol>(outer.ReturnType);
    }

    [Fact]
    public void ParenthesizedLambdasRemainContextualDuringOverloadResolution()
    {
        Compilation callCompilation = Compile("""
            namespace Example;
            void Run(function void() callback) { }
            void Run(function void(int) callback) { }
            void Test() { Run(([]() => { })); }
            """);
        Compilation returnCompilation = Compile("""
            namespace Example;
            void Run(function function void()() callback) { }
            void Run(function function void()*() callback) { }
            void Test() { Run([]() => { return ([]() => { }); }); }
            """);

        Assert.Empty(callCompilation.Diagnostics);
        Assert.Empty(returnCompilation.Diagnostics);
        Assert.Empty(Assert.IsType<FunctionValueTypeSymbol>(
            SelectedRun(callCompilation).Parameters[0].Type).ParameterTypes);
        FunctionValueTypeSymbol outer = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedRun(returnCompilation).Parameters[0].Type);
        Assert.IsType<FunctionValueTypeSymbol>(outer.ReturnType);
    }

    [Fact]
    public void ParenthesizedLambdaUsesUserConversionContext()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public int Value;
                public static Wrapper operator implicit(function void() callback) { return Wrapper(); }
            }
            void Test() { Wrapper value = ([]() => { }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.IsType<FunctionValueTypeSymbol>(
            SelectedLambdaConversion(compilation).Parameters[0].Type);
    }

    [Fact]
    public void ContextualNamedFunctionReturnSelectsLambdaOverload()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            int Transform(int value) { return value; }
            void Run(function function int(int)() callback) { }
            void Run(function function int(String)() callback) { }
            void Test() { Run([]() => { return Transform; }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        FunctionValueTypeSymbol outer = Assert.IsType<FunctionValueTypeSymbol>(
            SelectedRun(compilation).Parameters[0].Type);
        FunctionValueTypeSymbol returned = Assert.IsType<FunctionValueTypeSymbol>(outer.ReturnType);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, returned.ParameterTypes[0]));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void GenericCallReturnSelectsLambdaOverloadWithoutMainSpecializerPollution()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            T Identity<T>(T value) { return move value; }
            void Run(function int() callback) { }
            void Run(function String() callback) { }
            void Test() { Run([]() => { return Identity(1); }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(SelectedRun(compilation).Parameters[0].Type).ReturnType));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
        Assert.Single(compilation.SemanticModel.Functions,
            function => function.Symbol.GenericDefinition?.Name == "Identity");
    }

    [Fact]
    public void ExplicitLambdaParameterSelectsCompatibleOverload()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            void Run(function void(int) callback) { }
            void Run(function void(String) callback) { }
            void Test() { Run((int value) => { }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        CallExpressionSyntax call = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root)
            .OfType<CallExpressionSyntax>().Single();
        FunctionSymbol selected = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol);
        FunctionValueTypeSymbol parameter = Assert.IsType<FunctionValueTypeSymbol>(
            selected.Parameters[0].Type);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, parameter.ParameterTypes[0]));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void ExactLambdaParameterWinsNumericFunctionOverload()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function void(float) callback) { }
            void Run(function void(int) callback) { }
            void Test() { Run((int value) => { }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        CallExpressionSyntax call = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root)
            .OfType<CallExpressionSyntax>().Single();
        FunctionSymbol selected = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol);
        FunctionValueTypeSymbol parameter = Assert.IsType<FunctionValueTypeSymbol>(
            selected.Parameters[0].Type);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int, parameter.ParameterTypes[0]));
    }

    [Fact]
    public void CapturingLambdaEliminatesRawFunctionPointerOverload()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function void(int) callback) { }
            void Run(function void(int)* callback) { }
            void Test()
            {
                int offset = 1;
                Run([offset](int value) => { int result = value + offset; });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        CallExpressionSyntax call = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root)
            .OfType<CallExpressionSyntax>().Single();
        FunctionSymbol selected = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol);
        Assert.IsType<FunctionValueTypeSymbol>(selected.Parameters[0].Type);
    }

    [Fact]
    public void NonCapturingLambdaPrefersFirstClassFunctionValueOverRawPointer()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function void(int)* callback) { }
            void Run(function void(int) callback) { }
            void Test() { Run([](int value) => { }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        CallExpressionSyntax call = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root)
            .OfType<CallExpressionSyntax>().Single();
        FunctionSymbol selected = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol);
        Assert.IsType<FunctionValueTypeSymbol>(selected.Parameters[0].Type);
    }

    [Fact]
    public void IncompatibleLambdaSignatureRejectsOnlyCandidate()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct String { }
            void Run(function void(String) callback) { }
            void Test() { Run((int value) => { }); }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch);
    }

    [Fact]
    public void ExactLambdaReturnTypeEliminatesIncompatibleNumericReturn()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function int(int) callback) { }
            void Run(function long(int) callback) { }
            void Test() { Run((int value) => { return value; }); }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.True(TypeIdentity.AreSame(BuiltinTypes.Int,
            Assert.IsType<FunctionValueTypeSymbol>(SelectedRun(compilation).Parameters[0].Type).ReturnType));
        Assert.Single(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void ExplicitLambdaTypesParticipateInGenericInference()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Apply<T>(function void(T) callback) { }
            void Test()
            {
                Apply((int value) => { });
                Example.Apply((int value) => { });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Contains(compilation.SemanticModel.Functions,
            function => function.Symbol.IsGenericSpecialization &&
                        function.Symbol.GenericDefinition?.Name == "Apply" &&
                        TypeIdentity.AreSame(function.Symbol.TypeArguments[0], BuiltinTypes.Int));
    }

    [Fact]
    public void ContextualLambdaFlowsThroughOneUserConversionDuringOverloadResolution()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Action<T>
            {
                private function void(T) _callback;
                public Action(function void(T) callback) { _callback = callback; }
                public static Action<T> operator implicit(function void(T) callback)
                {
                    return Action<T>(callback);
                }
            }
            void Run(Action<int> callback) { }
            void Run(Action<float> callback) { }
            void Test()
            {
                int offset = 1;
                Run([offset](int value) => { int result = value + offset; });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        CallExpressionSyntax call = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root)
            .OfType<CallExpressionSyntax>()
            .Single(candidate => candidate.Target is NameExpressionSyntax { IdentifierToken.Text: "Run" });
        FunctionSymbol selected = Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol);
        Assert.Contains("Action<int>", selected.Parameters[0].Type.ToDisplayString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void EagerlySelectedCapturingLambdaPreservesArgumentMoveOrder()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { }
            void Run(function void() callback, unique<Resource> resource) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Run([move resource]() => { }, move resource);
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void DeferredCapturingLambdaPreservesArgumentMoveOrder()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource
            {
                public void Use() { }
            }
            struct Action
            {
                public static Action operator implicit(function void() callback)
                {
                    return Action();
                }
            }
            void Run(function void() callback, Resource value) { }
            void Run(Action callback, Resource value) { }
            void Test()
            {
                Resource resource = Resource();
                Run([move resource]() => { }, resource = Resource());
                resource.Use();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void FirstClassFunctionValuesAndCapturingArrowLambdasBindAndLower()
    {
        Compilation compilation = Compile("""
            namespace Example;
            int AddOne(int value) { return value + 1; }
            int Apply(function int(int) callback, int value) { return callback(value); }
            function int(int) MakeMultiplier(int factor)
            {
                return [factor](int value) => { return factor * value; };
            }
            int Main()
            {
                function int(int) direct = AddOne;
                function int(int) closure = MakeMultiplier(10);
                function int(int) copy = closure;
                return Apply(direct, 4) + copy(5);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.Contains("closure.environment", ir);
        Assert.Contains("function.call.closure", ir);
    }

    [Fact]
    public void ExplicitCaptureRulesAreEnforced()
    {
        Compilation valid = Compile("""
            namespace Example;
            int Test() {
                int value = 1;
                function int(int) byValue = [value](int x) => { return value + x; };
                function int(int) byBorrow = [readonly &value](int x) => { return value + x; };
                return byValue(1) + byBorrow(1);
            }
            """);
        Assert.Empty(valid.Diagnostics);

        Compilation implicitCapture = Compile("""
            namespace Example;
            int Test() {
                int value = 1;
                function int(int) f = [](int x) => { return value + x; };
                return f(1);
            }
            """);
        Assert.Contains(implicitCapture.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.LambdaCaptureNotSupported &&
            diagnostic.Message.Contains("not explicitly captured", StringComparison.Ordinal));

        Compilation rawCapture = Compile("""
            namespace Example;
            int Test() {
                int value = 1;
                function int(int)* f = [value](int x) => { return value + x; };
                return f(1);
            }
            """);
        Assert.Contains(rawCapture.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("capturing lambda cannot convert to raw function pointer", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("[readonly value]")]
    [InlineData("[move &value]")]
    [InlineData("[readonly move value]")]
    public void InvalidCaptureModifierCombinationsAreRejected(string capture)
    {
        Compilation compilation = Compile($$"""
            namespace Example;
            void Test()
            {
                int value = 1;
                function int() callback = {{capture}}() => { return value; };
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.InvalidLambdaCapture);
    }

    [Fact]
    public void CaptureOwnershipAndBorrowEscapeUseOrdinaryRules()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            function void() Own(unique<Resource> resource)
            {
                return [move resource]() => { resource->Use(); };
            }
            function int() Bad()
            {
                int value = 10;
                return [readonly &value]() => { return value; };
            }
            void Test()
            {
                unique<Resource> resource = new Resource();
                function void() invalid = [resource]() => { resource->Use(); };
                resource->Use();
            }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueNotCopyable);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AggregateReferenceEscape);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Own", StringComparison.Ordinal) &&
            diagnostic.Id == DiagnosticIds.TypeMismatch);
    }

    [Fact]
    public void ValueCapturePreservesTransitiveBorrowProvenance()
    {
        Compilation referenceAlias = Compile("""
            namespace Example;
            function int() Bad()
            {
                int value = 42;
                readonly int& alias = value;
                return [alias]() => { return alias; };
            }
            """);
        Assert.Contains(referenceAlias.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.AggregateReferenceEscape);

        Compilation nestedClosure = Compile("""
            namespace Example;
            function int() Bad()
            {
                int value = 42;
                function int() inner = [readonly &value]() => { return value; };
                return [inner]() => { return inner(); };
            }
            """);
        Assert.Contains(nestedClosure.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.AggregateReferenceEscape);
    }

    [Fact]
    public void FunctionValueMoveTransfersOwnershipAndInvalidatesTheSource()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Test()
            {
                int value = 1;
                function int() source = [value]() => { return value; };
                function int() destination = move source;
                destination();
                source();
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void MutableAndReadonlyBorrowCapturesKeepTheirCapabilities()
    {
        Compilation valid = Compile("""
            namespace Example;
            int Test()
            {
                int total = 0;
                {
                    function void(int) add = [&total](int value) => { total += value; };
                    add(2);
                }
                return total;
            }
            """);
        Assert.Empty(valid.Diagnostics);

        Compilation invalid = Compile("""
            namespace Example;
            void Test()
            {
                int total = 0;
                function void() mutate = [readonly &total]() => { total += 1; };
            }
            """);
        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Id is DiagnosticIds.InvalidAssignmentTarget or DiagnosticIds.BorrowedPlaceMutation);
    }

    [Fact]
    public void BorrowCapturingTemporaryConflictsWithOverlappingCallArgument()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function void() callback, int& value) { callback(); }
            void Test()
            {
                int value = 0;
                Run([&value]() => { value++; }, value);
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.BorrowConflict);
    }

    [Fact]
    public void BorrowCapturingClosureCannotEscapeThroughAField()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Holder { public function int() Callback; }
            struct Storage { public static Holder Saved; }
            void Store()
            {
                int value = 42;
                Storage.Saved.Callback = [readonly &value]() => { return value; };
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.AggregateReferenceEscape);
    }

    [Fact]
    public void BorrowCapturingClosureStoredInALocalFieldTracksDestructionOrder()
    {
        const string declarations = "struct Holder { public function int() Callback; }";
        Compilation valid = Compile($$"""
            namespace Example;
            {{declarations}}
            int Read()
            {
                int value = 42;
                Holder holder = Holder();
                holder.Callback = [readonly &value]() => { return value; };
                return holder.Callback();
            }
            """);
        Assert.Empty(valid.Diagnostics);

        Compilation invalid = Compile($$"""
            namespace Example;
            {{declarations}}
            void Store()
            {
                Holder holder = Holder();
                int value = 42;
                holder.Callback = [readonly &value]() => { return value; };
            }
            """);
        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.ReferenceDestructionOrder);
    }

    [Fact]
    public void LambdaContextFlowsThroughOneImplicitUserConversion()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Action<T>
            {
                private function void(T) _callback;
                public Action(function void(T) callback) { _callback = callback; }
                public static Action<T> operator implicit(function void(T) callback)
                {
                    return Action<T>(callback);
                }
                public void Invoke(T value) { _callback(value); }
            }
            void Print(int value) { }
            void Test()
            {
                Action<int> first = [](int value) => { Print(value); };
                int offset = 10;
                Action<int> second = [offset](int value) => { Print(value + offset); };
                first.Invoke(1);
                second.Invoke(2);
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void GenericFunctionValueSignaturesSpecialize()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Apply<T>(T value, function void(T) callback) { callback(value); }
            struct Holder<T> { public function void(T) Callback; }
            void Print(int value) { }
            void Test()
            {
                Apply<int>(1, [](int value) => { Print(value); });
                Holder<int> holder = Holder<int>();
                holder.Callback = Print;
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void CapturingClosureSurvivesSourceFreeLibraryRoundTrip()
    {
        Compilation library = Compile("""
            namespace Example;
            public function int(int) MakeMultiplier(int factor)
            {
                return [factor](int value) => { return factor * value; };
            }
            """);
        Assert.Empty(library.Diagnostics);
        CompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Closures")));
        Compilation application = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("""
                using Example;
                namespace App;
                int Main()
                {
                    function int(int) callback = MakeMultiplier(10);
                    return callback(5);
                }
                """, "app.xe"));
        Assert.Empty(application.Diagnostics);
        _ = new LlvmIrGenerator().GenerateForTarget(application, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void LambdasCanBeStoredPassedReturnedNestedAndImmediatelyInvoked()
    {
        Compilation compilation = Compile("""
            namespace Example;
            int Apply(function int(int)* callback, int value) { return callback(value); }
            function int(int)* Make() { return function int(int x) { return x + 1; }; }
            int Main()
            {
                function int(int)* add = function int(int x) { return x + 2; };
                int first = Apply(function int(int x) { return x * 2; }, 3);
                int second = (function int(int x) { return x + 3; })(4);
                int third = Make()(5);
                function int()* nested = function int() {
                    function int()* inner = function int() { return 7; };
                    return inner();
                };
                return add(first) + second + third + nested();
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(6, compilation.SemanticModel.Functions.Count(function => function.Symbol.Name.StartsWith("<lambda_")));
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        Assert.Contains("indirect.call", ir);
        Assert.NotEmpty(XelibWriter.Write(compilation, new XelibWriteOptions("Lambdas")));
    }

    [Theory]
    [InlineData("int value = 1; function int()* f = function int() { return value; };", DiagnosticIds.LambdaCaptureNotSupported)]
    [InlineData("function int()* outer = function int() { return 1; }; function int()* f = function int() { return outer(); };", DiagnosticIds.LambdaCaptureNotSupported)]
    [InlineData("function int(int)* f = function int(int x, int x) { return x; };", DiagnosticIds.DuplicateDeclaration)]
    [InlineData("function int()* f = function int() { };", DiagnosticIds.MissingReturn)]
    [InlineData("function int()* f = function int() { return true; };", DiagnosticIds.TypeMismatch)]
    [InlineData("function void()* f = function void() { break; };", DiagnosticIds.BreakOutsideLoopOrSwitch)]
    [InlineData("function void()* f = function void() { throw; };", DiagnosticIds.RethrowOutsideCatch)]
    [InlineData("function int(void)* f = function int(void x) { return 1; };", DiagnosticIds.VoidParameterType)]
    public void InvalidLambdaHasDiagnostic(string statements, string diagnosticId)
    {
        Compilation compilation = Compile("namespace Example; void Test() { " + statements + " }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("<lambda_", StringComparison.Ordinal));
    }

    [Fact]
    public void LambdaParameterShadowsOuterLocalAndHasSemanticIdentity()
    {
        var source = SourceText.From("""
            namespace Example;
            int Test(int x) {
                function int(int)* f = function int(int x) { return x + 1; };
                return f(x);
            }
            """, "lambda.xe");
        Compilation compilation = Compilation.Create(source);
        Assert.Empty(compilation.Diagnostics);
        var lambda = Assert.Single(compilation.SemanticModel.Functions.Where(function => function.Symbol.Name.StartsWith("<lambda_")));
        Assert.IsType<ParameterSymbol>(Assert.Single(lambda.Symbol.Parameters));
    }

    [Fact]
    public void EditorLookupStopsAtLambdaBoundaryAndKeepsParameterIdentity()
    {
        const string text = "namespace Example; int Test(int outer) { function int(int)* f = function int(int x) { return x; }; return f(outer); }";
        Compilation compilation = Compile(text);
        Assert.Empty(compilation.Diagnostics);
        int position = text.IndexOf("return x", StringComparison.Ordinal) + 7;
        var model = compilation.SemanticModel;
        Assert.DoesNotContain(model.LookupSymbols(position), symbol => symbol.Name == "outer" || symbol.Name == "f");
        Assert.DoesNotContain(model.GetCompletionSymbols(compilation.SyntaxTrees[0], position), symbol => symbol.Name == "outer" || symbol.Name == "f");
        var parameter = Assert.Single(model.LookupSymbols(position).OfType<ParameterSymbol>());
        Assert.Equal("x", parameter.Name);
        Assert.False(Assert.Single(model.Functions.Where(function => function.Symbol.IsLambda)).Symbol.HasUserEditableIdentifier);
    }

    [Fact]
    public void ThisCannotBeCaptured()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { public int Value; public int Test() {
                function int()* f = function int() { return this.Value; };
                return f();
            } }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.LambdaCaptureNotSupported);
    }

    [Fact]
    public void LambdaFieldInitializersAreEmittedOnce()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Holder {
                public function int()* Callback = function int() { return 4; };
                public static function int()* StaticCallback;
            }
            int Test() { Holder.StaticCallback = function int() { return 5; }; Holder a = Holder(); Holder b = Holder(); return a.Callback() + b.Callback() + Holder.StaticCallback(); }
            """);
        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(2, compilation.SemanticModel.Functions.Count(function => function.Symbol.IsLambda));
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void ImmediatelyInvokedLambdaSuppliesExpectedCallbackType()
    {
        Compilation compilation = Compile("""
            namespace Example;
            int Transform(int x) { return x; }
            bool Transform(bool x) { return x; }
            int Test() {
                return (function int(function int(int)* callback) { return callback(2); })(&Transform);
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void FunctionAddressDoesNotBypassCaptureRestrictionWhenOuterLocalShadowsFunction()
    {
        Compilation compilation = Compile("""
            namespace Example;
            int Value() { return 1; }
            void Test() {
                int Value = 4;
                function void()* f = function void() { function int()* address = &Value; };
            }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.LambdaCaptureNotSupported);
    }

    [Fact]
    public void LambdaSurvivesSourceFreeLibraryRoundTrip()
    {
        Compilation library = Compile("""
            namespace Example;
            public function int(int)* Make() { return function int(int x) { return x + 1; }; }
            """);
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Lambdas")));
        Compilation application = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Example; namespace App; int Main() { return Make()(41); }", "app.xe"));
        Assert.Empty(application.Diagnostics);
        _ = new LlvmIrGenerator().GenerateForTarget(application, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void LambdaKeepsLexicalPrivateAccessWithoutCapturingReceiver()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item {
                private static int Value() { return 42; }
                public function int()* Make() { return function int() { return Item.Value(); }; }
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void GenericContainingContextSupportsFunctionValuesAndLambdas()
    {
        Compilation compilation = Compile("""
            namespace Example;
            T Apply<T>(T value, function T(T) callback) { return callback(value); }
            int Test<T>() { function int() f = []() => { return 1; }; return f(); }
            int Run() { return Apply<int>(41, [](int value) => { return value + 1; }) + Test<int>(); }
            """);
        Assert.Empty(compilation.Diagnostics);
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Theory]
    [InlineData("function int(")]
    [InlineData("function int(int x) {")]
    [InlineData("function int(int x) { return ; }")]
    public void IncompleteLambdaDoesNotCrash(string expression)
    {
        Compilation compilation = Compile("namespace Example; void Test() { function int(int)* f = " + expression);
        Assert.NotEmpty(compilation.Diagnostics);
    }

    [Fact]
    public void FailedCallRemovesSameArgumentMoveCascadeAndLambdaArtifacts()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            void Invalid(function void(int) callback, unique<Resource> resource, int* marker) { }
            void Invalid(function void(int) callback, unique<Resource> resource, float* marker) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Invalid([move resource](int value) => { }, move resource, null);
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void LateConversionFailureRollsBackEveryLambdaAndPreservesBodyDiagnostics()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function int*(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function float*(int) callback) { return Wrapper(); }
            }
            struct Resource { public void Use() { } }
            void Consume(function void() first, Wrapper second) { }
            void Test()
            {
                unique<Resource> first = new Resource();
                unique<Resource> second = new Resource();
                Consume(([move first]() => { Missing(); }),
                    [move second](int value) => { return null; });
                first->Use();
                second->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownFunction);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
        foreach (LambdaExpressionSyntax lambda in SyntaxNavigator.DescendantNodesAndSelf(
                     compilation.SyntaxTrees.Single().Root).OfType<LambdaExpressionSyntax>())
        {
            Assert.True(TypeIdentity.AreSame(
                BuiltinTypes.Error, compilation.SemanticModel.GetTypeInfo(lambda).Type));
            Assert.Null(compilation.SemanticModel.GetConversionSymbolInfo(lambda).Symbol);
        }
    }

    [Fact]
    public void PositionalExtraLambdaRollsBackAllDeferredCaptures()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            struct Holder { public function void(int) Callback; }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Holder* holder = new Holder {
                    [](int value) => { },
                    [move resource](int value) => { }
                };
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.PositionalValueCountMismatch);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void AbstractAndPrivateConstructorFailuresPreserveErrorsAndRollbackCaptures()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            abstract struct AbstractHandler
            {
                public AbstractHandler(function void() callback) { }
            }
            struct PrivateHandler
            {
                private PrivateHandler(function void() callback) { }
            }
            struct Base
            {
                private Base(function void() callback) { }
            }
            struct Derived : Base
            {
                public Derived(unique<Resource> resource)
                    : base([move resource]() => { })
                {
                    resource->Use();
                }
            }
            void Test()
            {
                unique<Resource> first = new Resource();
                AbstractHandler* invalidAbstract = new AbstractHandler(
                    [move first]() => { Missing(); });
                first->Use();
                unique<Resource> second = new Resource();
                PrivateHandler* invalidPrivate = new PrivateHandler(
                    [move second]() => { });
                second->Use();
                unique<Resource> third = new Resource();
                Derived* derived = new Derived(move third);
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AbstractInstantiation);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InaccessibleSymbol);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void RecursiveThisInitializerRollsBackCaptureDiagnostics()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            struct Handler
            {
                public Handler(function void() callback, unique<Resource> resource)
                    : this([move resource]() => { }, move resource)
                {
                    resource->Use();
                }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MissingConstructor);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void DirectFunctionValueConversionFailureRollsBackCapture()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function int*(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function float*(int) callback) { return Wrapper(); }
            }
            struct Resource { public void Use() { } }
            void Consume(Wrapper callback) { }
            void Test()
            {
                function void(Wrapper) invoke = Consume;
                unique<Resource> resource = new Resource();
                invoke([move resource](int value) => { return null; });
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void GenericValidationConversionFailureRollsBackCapture()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function int*(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function float*(int) callback) { return Wrapper(); }
            }
            struct Resource { public void Use() { } }
            void Consume<T>(Wrapper callback) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Consume<int>([move resource](int value) => { return null; });
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void LateFailureRemovesBorrowConflictCausedByDeferredClosure()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function int*(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function float*(int) callback) { return Wrapper(); }
            }
            void Consume(function void() callback, int& value, Wrapper invalid) { }
            void Test()
            {
                int value = 0;
                Consume([&value]() => { value += 1; }, value,
                    [](int item) => { return null; });
                value = 2;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowConflict);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void SuccessfulBorrowCaptureStillConflictsAcrossDirectCallableArguments()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Consume(function void() callback, int& value) { }
            void Test()
            {
                function void(function void(), int&) invoke = Consume;
                int value = 0;
                invoke([&value]() => { value += 1; }, value);
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowConflict);
        Assert.Contains(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void NestedFailedCallRestoresCaptureBeforeOuterArgumentBinding()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { }
            int Inner(function void() callback, int* marker) { return 0; }
            int Inner(function void() callback, float* marker) { return 0; }
            void Outer(int value, unique<Resource> resource) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Outer(Inner([move resource]() => { }, null), move resource);
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void FailedCallRollsBackMultipleDeferredLambdasTogether()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            void Invalid(function void() first, function void() second, int* marker) { }
            void Invalid(function void() first, function void() second, float* marker) { }
            void Test()
            {
                unique<Resource> first = new Resource();
                unique<Resource> second = new Resource();
                Invalid([move first]() => { }, [move second]() => { }, null);
                first->Use();
                second->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void FailedIndexerRemovesSameArgumentMoveCascade()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { }
            struct Registry
            {
                public int this[function void() callback, unique<Resource> resource, int* marker]
                    { get { return 0; } }
                public int this[function void() callback, unique<Resource> resource, float* marker]
                    { get { return 0; } }
            }
            void Test(Registry registry)
            {
                unique<Resource> resource = new Resource();
                int value = registry[[move resource]() => { }, move resource, null];
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void RollbackKeepsUseAfterMoveThatPredatesDeferredCapture()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { }
            void Consume(unique<Resource> resource) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Consume(move resource);
                Missing([move resource]() => { });
                Consume(move resource);
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownFunction);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void RejectedCaptureDoesNotStealLaterLoopMoveSite()
    {
        const string source = """
            namespace Example;
            struct Resource { }
            void Consume(unique<Resource> resource) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                while (true)
                {
                    Missing([move resource]() => { });
                    Consume(move resource);
                }
            }
            """;
        Compilation compilation = Compile(source);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            candidate => candidate.Id == DiagnosticIds.MoveAcrossLoopBackedge);
        Assert.Equal(source.LastIndexOf("move resource", StringComparison.Ordinal),
            diagnostic.Location.Span.Start);
    }

    [Fact]
    public void RejectedArrayCaptureRestoresCleanupTransferFlag()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { }
            void Test()
            {
                Resource[] values = Resource[1];
                Missing([move values]() => { });
            }
            """);

        VariableDeclarationStatementSyntax declaration = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root)
            .OfType<VariableDeclarationStatementSyntax>()
            .Single(candidate => candidate.IdentifierToken.Text == "values");
        LocalVariableSymbol variable = Assert.IsType<LocalVariableSymbol>(
            compilation.SemanticModel.GetDeclaredSymbol(declaration));
        Assert.False(variable.RequiresArrayCleanupTransfer);
    }

    [Fact]
    public void RejectedLambdaRemovesGenericArtifactsCreatedByItsBody()
    {
        Compilation compilation = Compile("""
            namespace Example;
            T Identity<T>(T value) { return move value; }
            struct Box<T> { }
            struct Wrapper
            {
                public static Wrapper operator implicit(function int*(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function float*(int) callback) { return Wrapper(); }
            }
            void Consume(function void() first, Wrapper second) { }
            void Test()
            {
                Consume([]() => {
                    int value = Identity<int>(1);
                    Box<int> box = Box<int>();
                }, [](int value) => { return null; });
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function =>
            function.Symbol.IsGenericSpecialization &&
            function.Symbol.GenericDefinition?.Name == "Identity");
        NamespaceSymbol example = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces,
            candidate => candidate.Name == "Example");
        Assert.DoesNotContain(example.Types, type =>
            type is StructTypeSymbol { GenericDefinition.Name: "Box" });
    }

    [Fact]
    public void SuccessfulBorrowCaptureConflictsWithLaterOwnershipMove()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            void Consume(function void() callback, unique<Resource> resource) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Consume([&resource]() => { resource->Use(); }, move resource);
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MoveWhileBorrowed);
        Assert.Contains(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void LateFailureRemovesBorrowVersusMoveArgumentConflict()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { }
            struct Wrapper
            {
                public static Wrapper operator implicit(function int*(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function float*(int) callback) { return Wrapper(); }
            }
            void Consume(function void() callback, unique<Resource> resource, Wrapper invalid) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Consume([readonly &resource]() => { }, move resource,
                    [](int value) => { return null; });
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowConflict);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
    }

    [Fact]
    public void LaterValueCaptureReportsEarlierMoveInSuccessfulCall()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(int first, function int() callback) { }
            void Test()
            {
                int value = 1;
                Run(move value, [value]() => { return value; });
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void RejectedLambdaRollbackDoesNotUndoAFollowingIndependentMove()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public void Use() { } }
            void Run(function void(int) callback, unique<Resource> resource) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Run([move resource](float value) => { }, move resource);
                resource->Use();
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void MutableBorrowCaptureConflictsWithLaterOrdinaryRead()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function void() callback, int value) { }
            void Test()
            {
                int value = 1;
                Run([&value]() => { value += 1; }, value);
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.BorrowedPlaceAccess);
    }

    [Fact]
    public void ReadonlyBorrowCaptureConflictsWithLaterOrdinaryMutation()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function void() callback, int value) { }
            void Test()
            {
                int value = 1;
                Run([readonly &value]() => { }, value = 2);
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Fact]
    public void FailedCallRemovesProvisionalBorrowReadAndMutationDiagnostics()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Invalid(function void() callback, int value, int* marker) { }
            void Invalid(function void() callback, int value, float* marker) { }
            void Test()
            {
                int value = 1;
                Invalid([&value]() => { value += 1; }, value, null);
                Invalid([readonly &value]() => { }, value = 2, null);
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall));
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic =>
            diagnostic.Id is DiagnosticIds.BorrowedPlaceAccess or DiagnosticIds.BorrowedPlaceMutation);
    }

    [Fact]
    public void CaptureRollbackPreservesReinitializeThenMoveStateAndLocation()
    {
        const string source = """
            namespace Example;
            struct Resource { public void Use() { } }
            void Invalid(function void() callback, unique<Resource> replacement,
                unique<Resource> moved, int* marker) { }
            void Invalid(function void() callback, unique<Resource> replacement,
                unique<Resource> moved, float* marker) { }
            void Test()
            {
                unique<Resource> resource = new Resource();
                Invalid([move resource]() => { }, resource = new Resource(), move resource, null);
                resource->Use();
            }
            """;
        Compilation compilation = Compile(source);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Diagnostic useAfterMove = Assert.Single(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
        Assert.Equal(source.LastIndexOf("resource->Use", StringComparison.Ordinal),
            useAfterMove.Location.Span.Start);
    }

    [Fact]
    public void SuccessfulSiblingLambdasShareGenericSpecializations()
    {
        Compilation compilation = Compile("""
            namespace Example;
            T Identity<T>(T value) { return move value; }
            struct Box<T> { }
            void Run(function int() first, function int() second) { }
            void Test()
            {
                Run(
                    []() => { Box<int> box = Box<int>(); return Identity<int>(1); },
                    []() => { Box<int> box = Box<int>(); return Identity<int>(2); });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Single(compilation.SemanticModel.Functions, function =>
            function.Symbol.IsGenericSpecialization &&
            function.Symbol.GenericDefinition?.Name == "Identity");
        NamespaceSymbol example = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces,
            candidate => candidate.Name == "Example");
        Assert.Single(example.Types, type => type is StructTypeSymbol { GenericDefinition.Name: "Box" });
        _ = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
    }

    [Fact]
    public void RejectedLambdaRollsBackContainingGenericSpecialization()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function int*(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function float*(int) callback) { return Wrapper(); }
            }
            void Run<T>(Wrapper callback, T value) { }
            void Test()
            {
                Run<int>([](int value) => { return null; }, 1);
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function =>
            function.Symbol.IsGenericSpecialization && function.Symbol.GenericDefinition?.Name == "Run");
        CallExpressionSyntax call = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root).OfType<CallExpressionSyntax>()
            .Single(candidate => candidate.TypeArguments is not null);
        Assert.False(compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol is
            FunctionSymbol { IsGenericSpecialization: true });
    }

    [Fact]
    public void RejectedLambdaBodyTypesDoNotLeakGeneratedDestructors()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Wrapper
            {
                public static Wrapper operator implicit(function int*(int) callback) { return Wrapper(); }
                public static Wrapper operator implicit(function float*(int) callback) { return Wrapper(); }
            }
            void Consume(function void() accepted, Wrapper rejected) { }
            void Test()
            {
                Consume(
                    []() => { function long(long) bodyOnly = [](long value) => { return value; }; },
                    [](int value) => { return null; });
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
        Assert.DoesNotContain(compilation.TypeFactory.FunctionValueTypes, type =>
            TypeIdentity.AreSame(type.ReturnType, BuiltinTypes.Long) &&
            type.ParameterTypes.Length == 1 &&
            TypeIdentity.AreSame(type.ParameterTypes[0], BuiltinTypes.Long));
    }

    [Fact]
    public void LambdaProbeReusesExistingGenericStructIdentity()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Box<T> { }
            void Run(function Box<int>() callback) { }
            void Test()
            {
                Run([]() => { return Box<int>(); });
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    private static FunctionSymbol SelectedRun(Compilation compilation)
    {
        CallExpressionSyntax call = SyntaxNavigator.DescendantNodesAndSelf(
                compilation.SyntaxTrees.Single().Root)
            .OfType<CallExpressionSyntax>()
            .Single(candidate => candidate.Target is NameExpressionSyntax { IdentifierToken.Text: "Run" });
        return Assert.IsType<FunctionSymbol>(compilation.SemanticModel.GetSymbolInfo(call.Target).Symbol);
    }

    private static FunctionSymbol SelectedLambdaConversion(Compilation compilation)
    {
        LambdaExpressionSyntax lambda = SyntaxNavigator.DescendantNodesAndSelf(
            compilation.SyntaxTrees.Single().Root).OfType<LambdaExpressionSyntax>().Single();
        return Assert.IsType<FunctionSymbol>(
            compilation.SemanticModel.GetConversionSymbolInfo(lambda).Symbol);
    }

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(source, "lambda.xe"));
}
