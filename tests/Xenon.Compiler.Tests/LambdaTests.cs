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
    public void EquallyCompatibleContextualLambdaOverloadsAreAmbiguous()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Run(function int(int) callback) { }
            void Run(function long(int) callback) { }
            void Test() { Run((int value) => { return value; }); }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousCall);
        Assert.DoesNotContain(compilation.SemanticModel.Functions, function => function.Symbol.IsLambda);
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

    private static Compilation Compile(string source) => Compilation.Create(SourceText.From(source, "lambda.xe"));
}
