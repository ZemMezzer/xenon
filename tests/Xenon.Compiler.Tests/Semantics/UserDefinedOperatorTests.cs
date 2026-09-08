using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class UserDefinedOperatorTests
{
    [Fact]
    public void StandardNullConversionBeatsUserConversion()
    {
        Compilation compilation = Create("""
            struct S { public static S operator implicit(int* p) { return S(); } }
            int Pick(int* p) { return 1; }
            int Pick(S value) { return 2; }
            int Use() { return Pick(null); }
            """);
        Assert.Empty(compilation.Diagnostics);
        var call = Assert.IsType<BoundCallExpression>(Assert.IsType<BoundReturnStatement>(
            compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use").Body.Statements.Single()).Expression);
        Assert.IsType<PointerTypeSymbol>(call.Function.Parameters[0].Type);
    }

    [Fact]
    public void ReferenceReturningOperatorsAlsoWorkInCompoundAssignments()
    {
        Compilation compilation = Create("""
            struct S { public int Value; public static S& operator +(S& left, int right) { return left; } }
            void Use() { S a = S(); S b = a + 1; a += 1; }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void ReadonlyOperatorsPreserveCompoundPlaceOrigins()
    {
        Compilation valid = Create("""
            struct S { public static S readonly operator +(readonly S& a, int b) { return S(); } }
            void readonly Use() { S value = S(); value += 1; }
            """);
        Assert.Empty(valid.Diagnostics);
        Compilation invalid = Create("""
            struct S { public static S readonly operator +(readonly S& a, int b) { return S(); } }
            struct State { public static S Value; }
            void readonly Use() { State.Value += 1; }
            """);
        Assert.NotEmpty(invalid.Diagnostics);
    }

    [Fact]
    public void ExplicitPointerConversionUsesOrdinaryLifetimeRules()
    {
        Compilation valid = Create("""
            struct Handle
            {
                public void* Raw;
                public static void* operator explicit(readonly Handle& value) { return value.Raw; }
            }
            void* Use(Handle h) { return cast<void*>(h); }
            """);
        Assert.Empty(valid.Diagnostics);
    }

    [Fact]
    public void GenericOperatorCannotClaimADifferentClosedInstantiationAsItsOperand()
    {
        Compilation compilation = Create("""
            struct Box<T> { public static int operator +(Box<int> a, int b) { return b; } }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidOperatorDeclaration);
    }

    [Fact]
    public void OperatorOperandsUseOrdinaryDeferredMoveFlow()
    {
        Compilation compilation = Create("""
            struct S
            {
                public int Id;
                public ~S() {}
                public static int operator +(S left, int right) { return right; }
            }
            int Throwing() { throw 7; }
            void Use()
            {
                S value = S();
                try { int result = (move value) + Throwing(); }
                catch (...) { int stillAlive = value.Id; }
            }
            """);
        // The catch can also observe an exception from the operator after it has accepted ownership,
        // just as it can for an ordinary non-extern callable.
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }
    [Theory]
    [InlineData("+")][InlineData("-")][InlineData("*")][InlineData("/")][InlineData("%")]
    [InlineData("&")][InlineData("|")][InlineData("^")][InlineData("<<")][InlineData(">>")]
    [InlineData("==")][InlineData("!=")][InlineData("<")][InlineData(">")][InlineData("<=")][InlineData(">=")]
    public void EveryBinaryOperatorUsesItsDeclaredImplementation(string token)
    {
        Compilation compilation = Create($$"""
            struct S { public static int operator {{token}}(readonly S& a, readonly S& b) { return 42; } }
            int Use(S a, S b) { return a {{token}} b; }
            """);
        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(OperatorFacts.FromSource(token, 2), Assert.IsType<BoundCallExpression>(Assert.IsType<BoundReturnStatement>(
            compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use").Body.Statements.Single()).Expression).Function.OperatorKind);
    }

    [Theory]
    [InlineData("+")][InlineData("-")][InlineData("!")][InlineData("~")]
    public void EveryUnaryOperatorUsesItsDeclaredImplementation(string token)
    {
        Compilation compilation = Create($$"""
            struct S { public static int operator {{token}}(readonly S& a) { return 42; } }
            int Use(S a) { return {{token}}a; }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("+")][InlineData("-")][InlineData("*")][InlineData("/")][InlineData("%")]
    [InlineData("&")][InlineData("|")][InlineData("^")][InlineData("<<")][InlineData(">>")]
    public void CompoundAssignmentsUseBaseOperatorAndCaptureThePlace(string token)
    {
        Compilation compilation = Create($$"""
            struct S { public static S operator {{token}}(readonly S& a, int b) { return S(); } }
            void Use(S a) { a {{token}}= 1; }
            """);
        Assert.Empty(compilation.Diagnostics);
        var assignment = Assert.IsType<BoundAssignmentExpression>(Assert.IsType<BoundExpressionStatement>(
            compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use").Body.Statements.Single()).Expression);
        Assert.True(assignment.CapturesTarget);
        Assert.Equal(SyntaxKind.EqualsToken, assignment.OperatorKind);
    }

    [Fact]
    public void EquallyGoodOperandOperatorsDoNotFallBackToBuiltins()
    {
        Compilation compilation = Create("""
            struct A { public static int operator +(A a, B b) { return 1; } }
            struct B { public static int operator +(A a, B b) { return 2; } }
            int Use(A a, B b) { return a + b; }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id is DiagnosticIds.AmbiguousName or DiagnosticIds.AmbiguousCall);
    }

    [Fact]
    public void SourceAndDestinationConversionAmbiguityIsReported()
    {
        Compilation compilation = Create("""
            struct A { public static B operator implicit(A a) { return B(); } }
            struct B { public static B operator implicit(A a) { return B(); } }
            void Use(A a) { B b = a; }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousConversion);
    }

    [Fact]
    public void SameSourceCanConvertToDifferentDestinationsButCannotDuplicateAPair()
    {
        Compilation valid = Create("""
            struct S
            {
                public static int operator explicit(S value) { return 1; }
                public static bool operator explicit(S value) { return true; }
            }
            int Use(S value) { bool b = cast<bool>(value); return cast<int>(value); }
            """);
        Assert.Empty(valid.Diagnostics);
        Compilation invalid = Create("""
            struct S
            {
                public static int operator implicit(S value) { return 1; }
                public static int operator explicit(S value) { return 2; }
            }
            """);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidConversionDeclaration);
    }

    [Fact]
    public void OperatorsDoNotHideMovesAndConstructorsDoNotBecomeConversions()
    {
        Compilation compilation = Create("""
            struct Resource {}
            struct Owner
            {
                public unique<Resource> Value;
                public static int operator +(Owner a, int b) { return b; }
            }
            struct S { public S(int value) {} }
            int Use(Owner a) { S s = 1; return a + 1; }
            """);
        Assert.True(compilation.Diagnostics.Length >= 2);
    }
    private static Compilation Create(string source) => Compilation.Create([
        SyntaxTree.Parse(SourceText.From("namespace Example; " + source, "operators.xe"))]);

    [Fact]
    public void OperatorsAndStringStyleConversionsBindAsOrdinaryCalls()
    {
        Compilation compilation = Create("""
            sealed struct Text
            {
                public readonly byte* Data;
                public Text(readonly byte* value) { Data = value; }
                public static Text operator implicit(readonly byte* value) { return Text(value); }
                public static Text operator +(readonly Text& a, readonly Text& b) { return Text(a.Data); }
                public static bool operator ==(readonly Text& a, readonly Text& b) { return true; }
                public static bool operator !=(readonly Text& a, readonly Text& b) { return !(a == b); }
                public static int operator explicit(readonly Text& value) { return 42; }
            }
            int Use()
            {
                Text name = "Mira";
                name = "World";
                Text greeting = "Hello " + name;
                bool same = greeting == "Hello";
                Text casted = cast<Text>("Hello");
                return cast<int>(casted);
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        Assert.Contains(compilation.SemanticModel.Functions, function => function.Symbol.OperatorKind == OperatorKind.ImplicitConversion);
        BinaryExpressionSyntax equality = SyntaxNavigator.DescendantNodesAndSelf(compilation.SyntaxTrees.Single().Root)
            .OfType<BinaryExpressionSyntax>().Last(expression => expression.OperatorToken.Text == "==");
        Assert.Equal(OperatorKind.Equal, Assert.IsType<FunctionSymbol>(compilation.SemanticModel.GetSymbolInfo(equality).Symbol).OperatorKind);
    }

    [Fact]
    public void ExactCallWinsOverUserConversionAndRequiredConversionWorks()
    {
        Compilation compilation = Create("""
            struct S { public static S operator implicit(readonly byte* value) { return S(); } }
            int Pick(readonly byte* value) { return 1; }
            int Pick(S value) { return 2; }
            void Accept(S value) {}
            int Use() { Accept("x"); return Pick("x"); }
            """);
        Assert.Empty(compilation.Diagnostics);
        var use = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use");
        var call = Assert.IsType<BoundCallExpression>(Assert.IsType<BoundReturnStatement>(use.Body.Statements.Last()).Expression);
        Assert.IsType<PointerTypeSymbol>(call.Function.Parameters[0].Type);
    }

    [Theory]
    [InlineData("public S operator +(S a, S b) { return a; }")]
    [InlineData("public static S operator +(int a, int b) { return S(); }")]
    [InlineData("public static S operator +=(S a, S b) { return a; }")]
    [InlineData("public static S operator &(S a) { return a; }")]
    [InlineData("public static S operator implicit(S a) { return a; }")]
    [InlineData("public static S* operator implicit(S& a) { return &a; }")]
    [InlineData("public static S& operator explicit(S& a) { return a; }")]
    public void InvalidDeclarationsAreDiagnosed(string member)
    {
        Compilation compilation = Create("struct S { " + member + " }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id is
            DiagnosticIds.InvalidOperatorDeclaration or DiagnosticIds.InvalidConversionDeclaration);
    }

    [Fact]
    public void UserConversionChainsAndImplicitUseOfExplicitAreRejected()
    {
        Compilation compilation = Create("""
            struct A { public static B operator implicit(A value) { return B(); } }
            struct B { public static C operator implicit(B value) { return C(); } }
            struct C { public static int operator explicit(C value) { return 1; } }
            void Use(A a, C c) { C invalid = a; int other = c; }
            """);
        Assert.True(compilation.Diagnostics.Length >= 2);
    }

    [Fact]
    public void GenericStructOperatorsSpecialize()
    {
        Compilation compilation = Create("""
            struct Box<T>
            {
                public T Value;
                public Box(T value) { Value = move value; }
                public static bool operator ==(readonly Box<T>& a, readonly Box<T>& b) { return true; }
                public static Box<T> operator implicit(T value) { return Box<T>(move value); }
            }
            bool Use() { Box<int> a = 1; Box<int> b = 2; return a == b; }
            """);
        Assert.Empty(compilation.Diagnostics);
    }
}
