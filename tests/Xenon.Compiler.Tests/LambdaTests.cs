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
