using System.Collections.Immutable;
using System.Text;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class UserDefinedOperatorReviewTests
{
    [Fact]
    public void PrivateEqualityDoesNotSuppressBuiltinFallbackOutsideItsOwner()
    {
        Compilation compilation = Create("""
            struct S
            {
                public int Value;
                private static bool operator ==(readonly S& a, readonly S& b) { return false; }
                public static bool Inside(S a, S b) { return a == b; }
            }
            bool Use(S a, S b) { return a == b; }
            """);
        AssertValid(compilation);
        Assert.IsType<BoundBinaryExpression>(Result(compilation, "Use"));
        Assert.Equal(OperatorKind.Equal, Assert.IsType<BoundCallExpression>(Result(compilation, "Inside")).Function.OperatorKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InaccessibleOperatorCannotTieOrDefeatAccessibleCandidate(bool requiresConversion)
    {
        Compilation compilation = Create($$"""
            struct A
            {
                public static int operator +(A a, {{(requiresConversion ? "int" : "B")}} b) { return 42; }
            }
            struct B
            {
                private static int operator +(A a, B b) { return 0; }
                public static int operator implicit(B b) { return 1; }
            }
            int Use(A a, B b) { return a + b; }
            """);
        AssertValid(compilation);
        Assert.Equal("A", Assert.IsType<BoundCallExpression>(Result(compilation, "Use")).Function.ContainingStruct!.Name);
    }

    [Fact]
    public void PrivateDerivedOperatorCannotHideAnAccessibleBaseOperator()
    {
        Compilation compilation = Create("""
            struct Base
            {
                public static int operator +(readonly Base& a, readonly Derived& b) { return 42; }
            }
            struct Derived : Base
            {
                private static int operator +(readonly Base& a, readonly Derived& b) { return 0; }
                public static int Inside(Derived a, Derived b) { return a + b; }
            }
            int Use(Derived a, Derived b) { return a + b; }
            """);
        AssertValid(compilation);
        Assert.Equal("Base", Assert.IsType<BoundCallExpression>(Result(compilation, "Use")).Function.ContainingStruct!.Name);
        Assert.Equal("Derived", Assert.IsType<BoundCallExpression>(Result(compilation, "Inside")).Function.ContainingStruct!.Name);
    }

    [Theory]
    [InlineData("implicit", false)]
    [InlineData("implicit", true)]
    [InlineData("explicit", false)]
    [InlineData("explicit", true)]
    public void InaccessibleConversionCannotTieOrDefeatAccessibleCandidate(string kind, bool requiresReference)
    {
        Compilation compilation = Create($$"""
            struct A
            {
                public static B operator {{kind}}({{(requiresReference ? "readonly A&" : "A")}} value) { return B(); }
            }
            struct B { private static B operator {{kind}}(A value) { return B(); } }
            B Use(A a) { return {{(kind == "implicit" ? "a" : "cast<B>(a)")}}; }
            """);
        AssertValid(compilation);
        Assert.Equal("A", Assert.IsType<BoundCallExpression>(Result(compilation, "Use")).Function.ContainingStruct!.Name);
    }

    [Theory]
    [InlineData("implicit")]
    [InlineData("explicit")]
    public void PrivateDerivedConversionCannotHideAnAccessibleBaseConversion(string kind)
    {
        Compilation compilation = Create($$"""
            struct Base { public static Derived operator {{kind}}(readonly Base& a) { return Derived(); } }
            struct Derived : Base { private static Derived operator {{kind}}(readonly Base& a) { return Derived(); } }
            struct Grandchild : Derived {}
            Derived Use(Grandchild a) { return {{(kind == "implicit" ? "a" : "cast<Derived>(a)")}}; }
            """);
        AssertValid(compilation);
        Assert.Equal("Base", Assert.IsType<BoundCallExpression>(Result(compilation, "Use")).Function.ContainingStruct!.Name);
    }

    [Fact]
    public void PrivateConversionCannotMakeAnOrdinaryOverloadApplicable()
    {
        Compilation compilation = Create("""
            struct A {}
            struct B { private static B operator implicit(A a) { return B(); } }
            struct C { public static C operator implicit(A a) { return C(); } }
            int Pick(B b) { return 0; }
            int Pick(C c) { return 42; }
            int Use(A a) { return Pick(a); }
            """);
        AssertValid(compilation);
        Assert.Equal("C", Assert.IsType<BoundCallExpression>(Result(compilation, "Use")).Function.Parameters[0].Type.Name);
    }

    [Theory]
    [InlineData("implicit", "explicit", "A")]
    [InlineData("explicit", "implicit", "A")]
    [InlineData("implicit", "explicit", "readonly A&")]
    public void CrossTypeConflictingConversionsAreDeclarationErrors(string sourceKind, string destinationKind, string sourceParameter)
    {
        Compilation compilation = Create($$"""
            struct A { public static B operator {{sourceKind}}({{sourceParameter}} a) { return B(); } }
            struct B { public static B operator {{destinationKind}}(A a) { return B(); } }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidConversionDeclaration &&
            diagnostic.Message.Contains("conflicting implicit and explicit"));
    }

    [Theory]
    [InlineData("implicit")]
    [InlineData("explicit")]
    public void SameKindDuplicatesStillFollowOrdinaryDeclarationRules(string kind)
    {
        Compilation duplicate = Create($$"""
            struct A
            {
                public static B operator {{kind}}(A a) { return B(); }
                public static B operator {{kind}}(A other) { return B(); }
            }
            struct B {}
            """);
        Assert.True(duplicate.HasErrors);
        Compilation distinct = Create($$"""
            struct A
            {
                public static B operator {{kind}}(A a) { return B(); }
                public static C operator {{kind}}(A a) { return C(); }
            }
            struct D { public static B operator {{kind}}(D d) { return B(); } }
            struct B {}
            struct C {}
            """);
        AssertValid(distinct);
    }

    [Theory]
    [InlineData("A<T>", "B<T>", "A<U>", "B<U>")]
    [InlineData("A<T>", "B<int>", "A<int>", "B<U>")]
    public void GenericConversionPairsAreComparedAcrossOwnersAndSpecializations(
        string aSource, string aDestination, string bSource, string bDestination)
    {
        Compilation compilation = Create($$"""
            struct A<T> { public static {{aDestination}} operator implicit({{aSource}} a) { return {{aDestination}}(); } }
            struct B<U> { public static {{bDestination}} operator explicit({{bSource}} a) { return {{bDestination}}(); } }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidConversionDeclaration);
    }

    [Theory]
    [InlineData("implicit", "explicit")]
    [InlineData("explicit", "implicit")]
    public void SourceAndImportedGenericConversionPairsConflict(string libraryKind, string sourceKind)
    {
        Compilation library = Create($$"""
            public struct Box<T> { public static Box<T> operator {{libraryKind}}(T value) { return Box<T>(); } }
            """);
        AssertValid(library);
        LibraryCompilationReference reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Review")));
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From($$"""
            using Review;
            namespace App;
            struct S { public static Box<S> operator {{sourceKind}}(S value) { return Box<S>(); } }
            """));
        Assert.Contains(consumer.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidConversionDeclaration);
    }

    [Fact]
    public void NativeOperatorNamesAreStableDistinctAndIndependentOfSourcePunctuation()
    {
        const string source = """
            public struct S
            {
                public static S operator -(S a) { return a; }
                public static S operator -(S a, S b) { return a; }
                public static S operator +(S a, S b) { return a; }
                public static S operator +(S a, int b) { return a; }
                public static S op_add(S a, S b) { return a; }
                public static S op_add(S a, int b) { return a; }
                public static bool operator ==(S a, S b) { return true; }
                public static S operator <<(S a, int b) { return a; }
                public static S operator implicit(int a) { return S(); }
                public static int operator explicit(S a) { return 1; }
            }
            public struct Box<T> { public static bool operator ==(Box<T> a, Box<T> b) { return true; } }
            bool Use(Box<int> a, Box<int> b, Box<long> c, Box<long> d) { return (a == b) && (c == d); }
            """;
        Compilation compilation = Create(source);
        AssertValid(compilation);
        static string[] Names(Compilation value) => value.SemanticModel.Functions.Where(f => f.Symbol.IsOperator)
            .Select(f => NativeSymbolNames.Get(f.Symbol)).Order(StringComparer.Ordinal).ToArray();
        string[] names = Names(compilation);
        Assert.Equal(10, names.Length);
        Assert.Equal(names.Length, names.Distinct().Count());
        string[] allNames = compilation.SemanticModel.Functions.Select(f => NativeSymbolNames.Get(f.Symbol)).ToArray();
        Assert.Equal(allNames.Length, allNames.Distinct().Count());
        Assert.Equal(names, Names(Create(source)));
        foreach (string name in names)
        {
            Assert.DoesNotContain("operator ", name);
            Assert.Matches(@"\.op_[a-z_]+\.__overload_[0-9A-F]+$", name);
        }
        Assert.Contains(names, name => name.Contains(".op_unary_minus."));
        Assert.Contains(names, name => name.Contains(".op_subtract."));
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();
        string ir = new LlvmIrGenerator().GenerateForTarget(LlvmIrGenerator.BindForTarget(compilation, target), target);
        foreach (string name in names)
            Assert.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(name)), ir);
        Assert.DoesNotContain("operator +", ir);
    }

    [Fact]
    public void XelibOperatorIdentifiersRoundTripWithoutDependingOnEnumOrdinals()
    {
        foreach (OperatorKind kind in Enum.GetValues<OperatorKind>().Where(kind => kind != OperatorKind.Invalid))
        {
            string id = XelibOperatorKinds.Encode(kind)!;
            Assert.Matches("^[a-z_]+$", id);
            int arity = OperatorFacts.IsAllowed(kind, 1) ? 1 : 2;
            Assert.Equal(kind, XelibOperatorKinds.Decode(id, arity));
        }
        Assert.Throws<XelibFormatException>(() => XelibOperatorKinds.Decode("future_unknown_kind", 2));
        Assert.Throws<XelibFormatException>(() => XelibOperatorKinds.Decode("add", 1));
        Assert.Equal((ushort)1, XelibVersions.Container);
        Assert.Equal((ushort)1, XelibVersions.LibraryIr);
        Assert.Equal((ushort)1, XelibVersions.Language);
    }

    [Fact]
    public void ConflictingPairsInImportedMetadataAreRejectedWithoutACallSite()
    {
        // Model an artifact produced before declaration-level conflict validation existed.
        Compilation library = Create("""
            public struct A { public static B operator implicit(A a) { return B(); } }
            public struct B { public static B operator implicit(A a) { return B(); } }
            """);
        AssertValid(library);
        XelibContainer container = XelibContainer.Read(XelibWriter.Write(library, new XelibWriteOptions("Review")));
        var sections = container.Sections.Where(pair => pair.Key != (uint)XelibSectionKind.Manifest)
            .ToDictionary(pair => (XelibSectionKind)pair.Key, pair => pair.Value.ToArray());
        var symbols = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(sections[XelibSectionKind.Symbols], null);
        int owner = symbols.Single(symbol => symbol.Kind == XelibSymbolKind.Struct && symbol.Name == "B").Id;
        sections[XelibSectionKind.Symbols] = XelibJson.Serialize(symbols.Select(symbol =>
            symbol.ContainingSymbolId == owner && symbol.OperatorKind == "implicit_conversion"
                ? symbol with { OperatorKind = "explicit_conversion", Name = "operator explicit" } : symbol).ToImmutableArray());
        var exports = XelibJson.Deserialize<ImmutableArray<XelibExport>>(sections[XelibSectionKind.Exports], null);
        sections[XelibSectionKind.Exports] = XelibJson.Serialize(exports.Select(export => export with
            { Key = export.Key.Replace("Review.B:operator implicit:", "Review.B:operator explicit:", StringComparison.Ordinal) }).ToImmutableArray());
        XelibManifest manifest = XelibJson.Deserialize<XelibManifest>(container.GetRequiredSection(XelibSectionKind.Manifest).AsSpan(), null);
        sections[XelibSectionKind.Manifest] = XelibJson.Serialize(manifest with
        {
            ContentIdentity = XelibWriter.ComputeContentIdentity(manifest.Name, manifest.Version,
                sections.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)pair.Value))),
        });
        LibraryCompilationReference reference = XelibReader.Read(XelibContainer.Write(sections.Select(pair =>
            new XelibSection(pair.Key, XelibSectionFlags.Required, pair.Value))));
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("namespace App;"));
        Assert.Contains(consumer.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidConversionDeclaration);
    }

    private static Compilation Create(string source) => Compilation.Create(SourceText.From("namespace Review; " + source, "review.xe"));
    private static void AssertValid(Compilation compilation) => Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    private static BoundExpression Result(Compilation compilation, string name) =>
        Assert.IsType<BoundReturnStatement>(compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == name).Body.Statements.Single()).Expression!;
}
