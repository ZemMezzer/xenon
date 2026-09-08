using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class GenericConversionDeclarationTests
{
    private const string Property = "public int Value { get { return 1; } }";
    private const string Indexer = "public int this[int index] { get { return index; } }";
    private const string ManyMembers = """
        public int Value { get { return 1; } set {} }
        public int this[int index] { get { return index; } set {} }
        public int Read() { return 1; }
        """;

    [Theory]
    [InlineData(Property)]
    [InlineData(Indexer)]
    [InlineData(ManyMembers)]
    public void AccessorsDoNotChangeConflictDeclarationsOrLocations(string members)
    {
        Compilation compilation = Create($$"""
            public struct Source<T>
            {
                {{members}}
                public static Destination<T> operator implicit(Source<T> value) { return Destination<T>(); }
            }
            public struct Destination<T>
            {
                {{members}}
                public static Destination<T> operator explicit(Source<T> value) { return Destination<T>(); }
            }
            Destination<int> Use(Source<int> source) { return cast<Destination<int>>(source); }
            Destination<long> Other(Source<long> source) { return cast<Destination<long>>(source); }
            """);
        Diagnostic conflict = Assert.Single(Conflicts(compilation));
        FunctionSymbol source = Type(compilation, "Source").Methods.Single(method => method.IsConversionOperator);
        FunctionSymbol destination = Type(compilation, "Destination").Methods.Single(method => method.IsConversionOperator);
        AssertDeclarationPair(conflict, source, destination);
    }

    [Fact]
    public void MultipleConversionsKeepTheirOwnDeclarationAndConflictIdentity()
    {
        Compilation compilation = Create($$"""
            public struct Source<T>
            {
                {{ManyMembers}}
                public static A<T> operator implicit(Source<T> value) { return A<T>(); }
                public static B<T> operator implicit(Source<T> value) { return B<T>(); }
                public static C<T> operator implicit(Source<T> value) { return C<T>(); }
            }
            public struct A<T> { public static A<T> operator explicit(Source<T> value) { return A<T>(); } }
            public struct B<T> { public static B<T> operator explicit(Source<T> value) { return B<T>(); } }
            public struct C<T> { public static C<T> operator explicit(Source<T> value) { return C<T>(); } }
            void Use(Source<int> source, A<int> a, B<int> b, C<int> c) {}
            void Other(Source<long> source, A<long> a, B<long> b, C<long> c) {}
            """);
        Diagnostic[] conflicts = Conflicts(compilation);
        Assert.Equal(3, conflicts.Length);
        foreach (string name in new[] { "A", "B", "C" })
        {
            FunctionSymbol declaration = Type(compilation, "Source").Methods.Single(method =>
                method.ReturnType is StructTypeSymbol { GenericDefinition: { } definition } && definition.Name == name);
            FunctionSymbol opposite = Type(compilation, name).Methods.Single(method => method.IsConversionOperator);
            Diagnostic conflict = Assert.Single(conflicts.Where(diagnostic => diagnostic.Message.Contains(
                opposite.ToDisplayString(SymbolDisplayFormat.QualifiedSignature), StringComparison.Ordinal)));
            AssertDeclarationPair(conflict, declaration, opposite);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctGenericConversionsWithAccessorsRemainValid(bool fromLibrary)
    {
        const string definitions = """
            public struct A<T> {}
            public struct B<T> {}
            public struct C<T> {}
            public struct Source<T>
            {
                public int Value { get { return 1; } set {} }
                public int this[int index] { get { return index; } set {} }
                public int Read() { return 1; }
                public static A<T> operator implicit(Source<T> value) { return A<T>(); }
                public static B<T> operator implicit(Source<T> value) { return B<T>(); }
                public static C<T> operator implicit(Source<T> value) { return C<T>(); }
            }
            """;
        const string use = """
            void Use()
            {
                Source<int> source = Source<int>();
                A<int> a = source;
                B<int> b = source;
                C<int> c = source;
            }
            """;
        Compilation compilation = fromLibrary ? Consume(Library(definitions), use) : Create(definitions + use);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData(Property)]
    [InlineData(Indexer)]
    [InlineData(ManyMembers)]
    public void ImportedGenericDeclarationIsRecoveredDespiteAccessorOrdering(string members)
    {
        LibraryCompilationReference reference = Library($$"""
            public struct Destination<T>
            {
                {{members}}
                public static Destination<T> operator implicit(T value) { return Destination<T>(); }
            }
            """);
        FunctionSymbol imported = reference.GlobalNamespace.Namespaces.Single().Structs.Single()
            .Methods.Single(method => method.IsConversionOperator);
        Assert.Empty(imported.DeclaringSyntaxReferences);
        Compilation compilation = Consume(reference, """
            public struct Source
            {
                public static Destination<Source> operator explicit(Source value) { return Destination<Source>(); }
            }
            """);
        Diagnostic conflict = Assert.Single(Conflicts(compilation));
        FunctionSymbol source = Type(compilation, "Source").Methods.Single(method => method.IsConversionOperator);
        AssertDeclarationPair(conflict, imported, source);
        Assert.Equal(source.Locations.Single(), conflict.Location);
    }

    [Fact]
    public void ReorderedImportedConversionsMapToTheExactDeclaration()
    {
        LibraryCompilationReference reference = Library($$"""
            public struct A<T> {}
            public struct B<T> {}
            public struct Source<T>
            {
                {{ManyMembers}}
                public static A<T> operator implicit(Source<T> value) { return A<T>(); }
                public static T operator implicit(Source<T> value) { throw 7; }
                public static B<T> operator implicit(Source<T> value) { return B<T>(); }
            }
            """, reverseMethods: true);
        StructTypeSymbol owner = reference.GlobalNamespace.Namespaces.Single().Structs.Single(type => type.Name == "Source");
        FunctionSymbol imported = owner.Methods.Single(method => method.ReturnType is GenericParameterSymbol);
        Compilation compilation = Consume(reference, """
            public struct Destination
            {
                public static Destination operator explicit(Source<Destination> value) { return Destination(); }
            }
            void Use(Source<int> a, Source<long> b, Source<Destination> c) {}
            """);
        Diagnostic conflict = Assert.Single(Conflicts(compilation));
        FunctionSymbol source = Type(compilation, "Destination").Methods.Single(method => method.IsConversionOperator);
        AssertDeclarationPair(conflict, imported, source);
    }

    private static void AssertDeclarationPair(Diagnostic diagnostic, FunctionSymbol left, FunctionSymbol right)
    {
        Assert.Contains(left.ToDisplayString(SymbolDisplayFormat.QualifiedSignature), diagnostic.Message);
        Assert.Contains(right.ToDisplayString(SymbolDisplayFormat.QualifiedSignature), diagnostic.Message);
        Assert.Contains(diagnostic.Location, left.Locations.Concat(right.Locations));
        Assert.DoesNotContain("get_", diagnostic.Message);
        Assert.DoesNotContain("set_", diagnostic.Message);
    }

    private static Diagnostic[] Conflicts(Compilation compilation) => compilation.Diagnostics.Where(diagnostic =>
        diagnostic.Id == DiagnosticIds.InvalidConversionDeclaration &&
        diagnostic.Message.Contains("conflicting implicit and explicit", StringComparison.Ordinal)).ToArray();
    private static StructTypeSymbol Type(Compilation compilation, string name) =>
        compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Structs.Single(type => type.Name == name);
    private static Compilation Create(string source) => Compilation.Create(SourceText.From("namespace Mapping; " + source, "mapping.xe"));
    private static LibraryCompilationReference Library(string source, bool reverseMethods = false)
    {
        Compilation compilation = Create(source);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        byte[] bytes = XelibWriter.Write(compilation, new XelibWriteOptions("Mapping"));
        if (reverseMethods)
        {
            // Imported member order is not a semantic contract. Change only method order in the fixture.
            XelibContainer container = XelibContainer.Read(bytes);
            var sections = container.Sections.Where(pair => pair.Key != (uint)XelibSectionKind.Manifest)
                .ToDictionary(pair => (XelibSectionKind)pair.Key, pair => pair.Value.ToArray());
            var symbols = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(sections[XelibSectionKind.Symbols], null);
            sections[XelibSectionKind.Symbols] = XelibJson.Serialize(symbols.Select(symbol =>
                symbol.Kind == XelibSymbolKind.Function && symbol.FunctionKind == XelibFunctionKind.Method
                    ? symbol with { Order = symbols.Length - symbol.Order } : symbol).ToImmutableArray());
            var manifest = XelibJson.Deserialize<XelibManifest>(container.GetRequiredSection(XelibSectionKind.Manifest).AsSpan(), null);
            sections[XelibSectionKind.Manifest] = XelibJson.Serialize(manifest with
            {
                ContentIdentity = XelibWriter.ComputeContentIdentity(manifest.Name, manifest.Version,
                    sections.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)pair.Value))),
            });
            bytes = XelibContainer.Write(sections.Select(pair => new XelibSection(pair.Key, XelibSectionFlags.Required, pair.Value)));
        }
        return XelibReader.Read(bytes);
    }
    private static Compilation Consume(LibraryCompilationReference reference, string source) =>
        Compilation.Create(new CompilationOptions(), [reference], SourceText.From("namespace Mapping; " + source, "consumer.xe"));
}
