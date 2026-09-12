using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Libraries;

public sealed class XelibContainerTests
{
    [Fact]
    public void ReadonlyLibraryMethodCanBeCalledThroughStaticReadonlyField()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Xenon.IO;
            struct Text {}
            struct ConsoleWriter
            {
                public void readonly WriteLine(Text& value) {}
            }
            struct Console
            {
                public static readonly ConsoleWriter Out;
            }
            """, "console.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));

        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Console")), metadataOnly: true);
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Xenon.IO;
            namespace App;
            void Main(Text& text)
            {
                Console.Out.WriteLine(text);
            }
            """, "app.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        FunctionSymbol writeLine = Assert.IsType<StructTypeSymbol>(reference.GlobalNamespace.Namespaces.Single()
            .Namespaces.Single().Types
            .Single(type => type.Name == "ConsoleWriter")).Methods.Single();
        Assert.True(writeLine.IsReadonly);
    }

    [Fact]
    public void CallableOverloadsRoundTripAndReachOnlySelectedXelibBodies()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace OverloadedLibrary;
            struct Writer
            {
                public int Write(int value) { return 10; }
                public int Write(char value) { return 20; }
                public int Write(readonly byte* value) { return 30; }
                public static int Parse(int value) { return 40; }
                public static int Parse(char value) { return 50; }
                public int Probe(int value) { return 90; }
                public int readonly Probe(int value) { return 100; }
            }
            template ParserLike { int Parse(readonly byte* value); }
            struct ParserBase
            {
                public virtual int Parse(readonly byte* value) { return 105; }
            }
            struct Parser : ParserBase
            {
                public override int Parse(readonly byte* value) { return 110; }
                public int Parse(byte* value) { return 120; }
            }
            public int Invoke<T>(T value, byte* text) where T : ParserLike { return value.Parse(text); }
            public int Pick(int value) { return 60; }
            public int Pick(char value) { return 70; }
            public int Pick(readonly byte* value) { return 80; }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));

        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("OverloadedLibrary")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using OverloadedLibrary;
            namespace App;
            int Read(readonly Writer& writer) { return writer.Probe(1); }
            int Main()
            {
                Writer writer = Writer();
                Parser parser = Parser();
                byte* text = null;
                function int(int)* callback = &Pick;
                return writer.Write(1) + Writer.Parse('A') + Pick("x") + callback(2) +
                    writer.Probe(1) + Read(writer) + Invoke<Parser>(parser, text);
            }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));

        BoundFunction[] reachable = app.GetStaticImplementationFunctions().ToArray();
        Assert.Contains(reachable, body => body.Symbol.Name == "Write" &&
            TypeIdentity.AreSame(body.Symbol.Parameters[0].Type, BuiltinTypes.Int));
        Assert.DoesNotContain(reachable, body => body.Symbol.Name == "Write" &&
            TypeIdentity.AreSame(body.Symbol.Parameters[0].Type, BuiltinTypes.Char));
        Assert.Contains(reachable, body => body.Symbol.Name == "Parse" &&
            TypeIdentity.AreSame(body.Symbol.Parameters[0].Type, BuiltinTypes.Char));
        Assert.Contains(reachable, body => body.Symbol.Name == "Pick" &&
            body.Symbol.Parameters[0].Type is PointerTypeSymbol);
        Assert.Contains(reachable, body => body.Symbol.Name == "Pick" &&
            TypeIdentity.AreSame(body.Symbol.Parameters[0].Type, BuiltinTypes.Int));
        Assert.Equal(2, reachable.Count(body => body.Symbol.Name == "Probe"));
        Assert.Contains(reachable, body => body.Symbol.Name == "Probe" && !body.Symbol.IsReadonly);
        Assert.Contains(reachable, body => body.Symbol.Name == "Probe" && body.Symbol.IsReadonly);
        Assert.True(reachable.Any(body => body.Symbol.Name == "Parse" &&
            body.Symbol.ContainingType?.Name == "Parser" &&
            body.Symbol.Parameters[0].Type is PointerTypeSymbol { IsReadonly: true }),
            string.Join(Environment.NewLine, reachable.Select(body =>
                $"{body.Symbol.ContainingType?.Name}.{body.Symbol.ToDisplayString(SymbolDisplayFormat.Signature)}")));
        Assert.DoesNotContain(reachable, body => body.Symbol.Name == "Parse" &&
            body.Symbol.ContainingType?.Name == "Parser" &&
            body.Symbol.Parameters[0].Type is PointerTypeSymbol { IsReadonly: false });

        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();
        Compilation targeted = LlvmIrGenerator.BindForTarget(app, target);
        Assert.False(targeted.HasErrors, string.Join(Environment.NewLine, targeted.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(targeted, target);
    }

    [Fact]
    public void PersistedSemanticKindsRoundTripThroughExplicitXelibMappings()
    {
        foreach (FunctionKind value in Enum.GetValues<FunctionKind>())
            Assert.Equal(value, XelibStableMappings.FromXelib(XelibStableMappings.ToXelib(value)));
        foreach (AccessorKind value in Enum.GetValues<AccessorKind>())
            Assert.Equal(value, XelibStableMappings.FromXelib(XelibStableMappings.ToXelib(value)));
        foreach (GenericConstraintKind value in Enum.GetValues<GenericConstraintKind>())
            Assert.Equal(value, XelibStableMappings.FromXelib(XelibStableMappings.ToXelib(value)));
        foreach (ReferenceReturnOriginKind value in Enum.GetValues<ReferenceReturnOriginKind>())
            Assert.Equal(value, XelibStableMappings.FromXelib(XelibStableMappings.ToXelib(value)));
        foreach (SharedReturnOriginKind value in Enum.GetValues<SharedReturnOriginKind>())
            Assert.Equal(value, XelibStableMappings.FromXelib(XelibStableMappings.ToXelib(value)));
        foreach (ArrayStorageKind value in Enum.GetValues<ArrayStorageKind>())
            Assert.Equal(value, XelibStableMappings.FromXelib(XelibStableMappings.ToXelib(value)));
        foreach (MovedPlaceReinitializationState value in Enum.GetValues<MovedPlaceReinitializationState>())
            Assert.Equal(value, XelibStableMappings.FromXelib(XelibStableMappings.ToXelib(value)));
    }

    [Fact]
    public void ExportKeysUseStableXelibKindTags()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Keys;
            template Shape {
                int Run();
                Shape(int value);
                int Value { get; set; }
                int this[int index] { get; set; }
            }
            struct Access {
                public int Method() { return 1; }
                public int Value { get { return 1; } set { } }
                public int this[int index] { get { return index; } set { } }
            }
            public int Value() { return 1; }
            """, "keys.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        XelibContainer container = XelibContainer.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Keys")));
        ImmutableArray<XelibExport> exports = XelibJson.Deserialize<ImmutableArray<XelibExport>>(
            container.GetRequiredSection(XelibSectionKind.Exports).AsSpan(), null);
        XelibExport export = Assert.Single(exports,
            item => item.Key.StartsWith("F:", StringComparison.Ordinal) &&
                item.Key.Contains(":Value:0:", StringComparison.Ordinal));

        Assert.EndsWith(":1:0:False:False", export.Key, StringComparison.Ordinal);
        Assert.Contains(exports, item => item.Key.StartsWith(
            $"R:{(ushort)XelibSymbolKind.TemplateMethod}:", StringComparison.Ordinal));
        Assert.Contains(exports, item => item.Key.StartsWith(
            $"R:{(ushort)XelibSymbolKind.TemplateConstructor}:", StringComparison.Ordinal));
        Assert.Contains(exports, item => item.Key.StartsWith(
            $"R:{(ushort)XelibSymbolKind.TemplateProperty}:", StringComparison.Ordinal));
        Assert.Contains(exports, item => item.Key.StartsWith(
            $"R:{(ushort)XelibSymbolKind.TemplateIndexer}:", StringComparison.Ordinal));
        Assert.Contains(exports, item => item.Key.StartsWith("P:", StringComparison.Ordinal));
        Assert.Contains(exports, item => item.Key.StartsWith("I:", StringComparison.Ordinal));
        Assert.Contains(exports, item => item.Key.StartsWith("F:", StringComparison.Ordinal) &&
            item.Key.Contains($":{(ushort)XelibFunctionKind.Method}:", StringComparison.Ordinal));
        Assert.DoesNotContain(exports, item => item.Key.Contains("Template", StringComparison.Ordinal));
        Assert.NotEqual((int)FunctionKind.Ordinary, (int)XelibFunctionKind.Ordinary);
    }

    [Fact]
    public void UnambiguousGenericTypeKeepsLegacyV1KeyThroughTransitiveXelibWrite()
    {
        Compilation dependency = Compilation.Create(SourceText.From("""
            namespace LegacyKeys;
            public struct Box<T> { public T Value; }
            """, "dependency.xe"));
        Assert.False(dependency.HasErrors, string.Join(Environment.NewLine, dependency.Diagnostics));
        byte[] dependencyBytes = XelibWriter.Write(dependency, new XelibWriteOptions("LegacyKeys"));
        XelibContainer dependencyContainer = XelibContainer.Read(dependencyBytes);
        ImmutableArray<XelibExport> dependencyExports = XelibJson.Deserialize<ImmutableArray<XelibExport>>(
            dependencyContainer.GetRequiredSection(XelibSectionKind.Exports).AsSpan(), null);
        string legacyKey = $"T:{(ushort)XelibSymbolKind.Struct}:LegacyKeys.Box";
        Assert.Contains(dependencyExports, item => item.Key == legacyKey);
        Assert.DoesNotContain(dependencyExports, item => item.Key == legacyKey + ":1");

        LibraryCompilationReference dependencyReference = XelibReader.Read(dependencyBytes);
        Compilation intermediate = Compilation.Create(new CompilationOptions(), [dependencyReference],
            SourceText.From("""
                using LegacyKeys;
                namespace Intermediate;
                public struct Holder { public Box<int> Value; }
                """, "intermediate.xe"));
        Assert.False(intermediate.HasErrors, string.Join(Environment.NewLine, intermediate.Diagnostics));
        byte[] intermediateBytes = XelibWriter.Write(intermediate, new XelibWriteOptions(
            "Intermediate", Dependencies: [new XelibDependencyInput(
                dependencyReference, dependencyReference.LibraryIdentity)]));

        LibraryCompilationReference roundTripped = XelibReader.Read(intermediateBytes, [dependencyReference]);
        Assert.Contains(roundTripped.GlobalNamespace.Namespaces.Single(scope => scope.Name == "Intermediate").Structs,
            type => type.Name == "Holder");
    }

    [Fact]
    public void SameNamedGenericTypeFamilyUsesAritySuffixedXelibKeys()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace ArityKeys;
            public struct Function<T> { }
            public struct Function<T1, T2> { }
            public struct Mixed<T> { }
            internal struct Mixed<T1, T2> { }
            internal struct Hidden<T> { }
            internal struct Hidden<T1, T2> { }
            """, "arity-keys.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        XelibContainer container = XelibContainer.Read(
            XelibWriter.Write(library, new XelibWriteOptions("ArityKeys")));
        ImmutableArray<XelibExport> exports = XelibJson.Deserialize<ImmutableArray<XelibExport>>(
            container.GetRequiredSection(XelibSectionKind.Exports).AsSpan(), null);
        string prefix = $"T:{(ushort)XelibSymbolKind.Struct}:ArityKeys.Function:";

        Assert.Contains(exports, item => item.Key == prefix + "1");
        Assert.Contains(exports, item => item.Key == prefix + "2");
        Assert.DoesNotContain(exports, item => item.Key == prefix[..^1]);

        NamespaceSymbol scope = library.SemanticModel.GlobalNamespace.Namespaces.Single();
        Assert.Equal(2, scope.Structs.Where(type => type.Name == "Mixed")
            .Select(XelibExportKey.Create).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, scope.Structs.Where(type => type.Name == "Hidden")
            .Select(XelibExportKey.Create).Distinct(StringComparer.Ordinal).Count());
        Assert.EndsWith(":1", XelibExportKey.Create(scope.Structs.Single(type =>
            type.Name == "Mixed" && type.GenericArity == 1)), StringComparison.Ordinal);
        Assert.EndsWith(":2", XelibExportKey.Create(scope.Structs.Single(type =>
            type.Name == "Hidden" && type.GenericArity == 2)), StringComparison.Ordinal);
    }

    [Fact]
    public void UniqueGenericDefinitionAliasResolvesFromSourceLessXelib()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace AliasLibrary;
            public struct Box<T> { public T Value; }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("AliasLibrary")), metadataOnly: true);

        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using B = AliasLibrary.Box;
            namespace App;
            struct Uses { B<int> aliased; AliasLibrary.Box<int> direct; }
            """, "app.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        StructTypeSymbol uses = app.SemanticModel.GlobalNamespace.FindNamespace("App")!.Structs.Single();
        Assert.Same(uses.Fields[0].Type, uses.Fields[1].Type);
        Assert.Equal(SymbolOriginKind.Library,
            Assert.IsType<StructTypeSymbol>(uses.Fields[0].Type).GenericDefinition!.Origin.Kind);
    }

    [Fact]
    public void GenericAliasFamilyAmbiguityIsPreservedAcrossXelibAndMixedSource()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace AliasLibrary;
            public struct Function<T> { }
            public struct Function<T1, T2> { }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("AliasLibrary")), metadataOnly: true);

        Compilation xelibOnly = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using F = AliasLibrary.Function;
            namespace App;
            """, "xelib-only.xe"));
        Diagnostic xelibDiagnostic = Assert.Single(xelibOnly.Diagnostics,
            item => item.Id == DiagnosticIds.AmbiguousName);
        Assert.Contains("matching generic arities: 1, 2", xelibDiagnostic.Message, StringComparison.Ordinal);

        Compilation unaryLibrary = Compilation.Create(SourceText.From("""
            namespace MixedLibrary;
            public struct Function<T> { }
            """, "unary-library.xe"));
        LibraryCompilationReference unaryReference = XelibReader.Read(
            XelibWriter.Write(unaryLibrary, new XelibWriteOptions("MixedLibrary")), metadataOnly: true);
        Compilation mixed = Compilation.Create(new CompilationOptions(), [unaryReference], SourceText.From("""
            using F = MixedLibrary.Function;
            namespace MixedLibrary;
            struct Function<T1, T2> { }
            struct Uses { Function<int> unary; Function<int, float> binary; }
            """, "mixed.xe"));

        Diagnostic mixedDiagnostic = Assert.Single(mixed.Diagnostics,
            item => item.Id == DiagnosticIds.AmbiguousName);
        Assert.Contains("matching generic arities: 1, 2", mixedDiagnostic.Message, StringComparison.Ordinal);
        StructTypeSymbol uses = mixed.SemanticModel.GlobalNamespace.FindNamespace("MixedLibrary")!.Structs
            .Single(type => type.Name == "Uses");
        Assert.Equal([1, 2], uses.Fields.Select(field =>
            Assert.IsType<StructTypeSymbol>(field.Type).GenericDefinition!.GenericArity));
    }

    [Fact]
    public void ContentIdentityIncludesSemanticVersionsButNotPhysicalMetadata()
    {
        (XelibSectionKind, ReadOnlyMemory<byte>)[] sections =
            [(XelibSectionKind.Symbols, new byte[] { 1, 2, 3 })];
        string baseline = XelibWriter.ComputeContentIdentity("Library", "1.0", sections, 1, 1);

        Assert.NotEqual(baseline, XelibWriter.ComputeContentIdentity("Library", "1.0", sections, 2, 1));
        Assert.NotEqual(baseline, XelibWriter.ComputeContentIdentity("Library", "1.0", sections, 1, 2));
        Assert.Equal(baseline, XelibWriter.ComputeContentIdentity("Library", "1.0", sections, 1, 1));
    }

    [Fact]
    public void XelibPathComparisonMatchesPlatformFilesystemPolicy()
    {
        Assert.Equal(OperatingSystem.IsWindows(),
            XelibReferenceLoader.PathComparer.Equals("C:/lib/Foo.xelib", "C:/lib/foo.xelib"));
    }

    [Fact]
    public void HeaderAndSectionsRoundTrip()
    {
        Assert.Equal((ushort)1, XelibVersions.Container);
        Assert.Equal((ushort)1, XelibVersions.LibraryIr);
        Assert.Equal((ushort)1, XelibVersions.Language);
        byte[] bytes = XelibContainer.Write([
            new XelibSection(XelibSectionKind.Manifest, XelibSectionFlags.Required,
                Encoding.UTF8.GetBytes("manifest")),
            new XelibSection(XelibSectionKind.Strings, XelibSectionFlags.Required,
                Encoding.UTF8.GetBytes("strings")),
        ]);

        XelibContainer container = XelibContainer.Read(bytes);

        Assert.Equal(XelibVersions.Container, container.Header.ContainerVersion);
        Assert.Equal(XelibVersions.LibraryIr, container.Header.LibraryIrVersion);
        Assert.Equal(XelibVersions.Language, container.Header.LanguageVersion);
        Assert.Equal("manifest", Encoding.UTF8.GetString(
            container.GetRequiredSection(XelibSectionKind.Manifest).AsSpan()));
        Assert.Equal("strings", Encoding.UTF8.GetString(
            container.GetRequiredSection(XelibSectionKind.Strings).AsSpan()));
    }

    [Fact]
    public void OutputIsDeterministicAndSectionOrderIndependent()
    {
        var manifest = new XelibSection(XelibSectionKind.Manifest, XelibSectionFlags.Required, [1, 2, 3]);
        var strings = new XelibSection(XelibSectionKind.Strings, XelibSectionFlags.Required, [4, 5]);

        Assert.Equal(XelibContainer.Write([manifest, strings]), XelibContainer.Write([strings, manifest]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(39)]
    public void TruncatedHeaderIsRejected(int length)
    {
        XelibFormatException exception = Assert.Throws<XelibFormatException>(() =>
            XelibContainer.Read(new byte[length]));

        Assert.Equal(XelibErrorCode.TruncatedHeader, exception.Code);
    }

    [Fact]
    public void InvalidSignatureIsRejected()
    {
        byte[] bytes = ValidContainer();
        bytes[0] ^= 0xff;

        Assert.Equal(XelibErrorCode.InvalidSignature,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(bytes)).Code);
    }

    [Fact]
    public void UnsupportedVersionsAreReportedSeparately()
    {
        byte[] container = ValidContainer();
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(10), checked((ushort)(XelibVersions.Container + 1)));
        Assert.Equal(XelibErrorCode.UnsupportedContainerVersion,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(container)).Code);

        container = ValidContainer();
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(12), checked((ushort)(XelibVersions.LibraryIr + 1)));
        Assert.Equal(XelibErrorCode.UnsupportedLibraryIrVersion,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(container)).Code);

        container = ValidContainer();
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(14), checked((ushort)(XelibVersions.Language + 1)));
        Assert.Equal(XelibErrorCode.UnsupportedLanguageVersion,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(container)).Code);
    }

    [Fact]
    public void OutOfBoundsAndOverlappingSectionsAreRejected()
    {
        byte[] container = XelibContainer.Write([
            new XelibSection(XelibSectionKind.Manifest, XelibSectionFlags.Required, [1]),
            new XelibSection(XelibSectionKind.Strings, XelibSectionFlags.Required, [2]),
        ]);
        BinaryPrimitives.WriteUInt64LittleEndian(container.AsSpan(40 + 8), ulong.MaxValue);
        Assert.Equal(XelibErrorCode.CorruptSectionTable,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(container)).Code);

        container = XelibContainer.Write([
            new XelibSection(XelibSectionKind.Manifest, XelibSectionFlags.Required, [1]),
            new XelibSection(XelibSectionKind.Strings, XelibSectionFlags.Required, [2]),
        ]);
        ulong firstOffset = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(40 + 8));
        BinaryPrimitives.WriteUInt64LittleEndian(container.AsSpan(40 + 32 + 8), firstOffset);
        Assert.Equal(XelibErrorCode.OverlappingSections,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(container)).Code);
    }

    [Fact]
    public void UnknownOptionalSectionIsSkippedAndRequiredOneIsRejected()
    {
        byte[] optional = XelibContainer.Write([
            new XelibSection(999, XelibSectionFlags.None, [1]),
        ]);
        Assert.Empty(XelibContainer.Read(optional).Sections);

        byte[] required = XelibContainer.Write([
            new XelibSection(999, XelibSectionFlags.Required, [1]),
        ]);
        Assert.Equal(XelibErrorCode.UnknownRequiredSection,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(required)).Code);
    }

    private static byte[] ValidContainer() => XelibContainer.Write([
        new XelibSection(XelibSectionKind.Manifest, XelibSectionFlags.Required, [1]),
    ]);

    [Fact]
    public void SemanticMetadataAndDocumentationRoundTripWithoutSource()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Library;
            /// <summary>A useful value.</summary>
            struct Value {
                /// <summary>Stored number.</summary>
                public int Number;
                /// <summary>Reads the number.</summary>
                public int readonly Read() { return Number; }
            }
            /// <summary>Adds two numbers.</summary>
            /// <param name="left">First value.</param>
            /// <param name="right">Second value.</param>
            /// <returns>Their sum.</returns>
            public int Add(int left, int right) { return left + right; }
            """, "C:/machine/source/library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine,
            library.Diagnostics.Select(item => item.Message)));

        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("Library", "1.2.3"));
        LibraryCompilationReference reference = XelibReader.Read(bytes, metadataOnly: true);
        NamespaceSymbol scope = Assert.Single(reference.GlobalNamespace.Namespaces);
        StructTypeSymbol type = Assert.Single(scope.Structs);
        FunctionSymbol function = Assert.Single(scope.Functions);

        Assert.Equal(SymbolOriginKind.Library, type.Origin.Kind);
        Assert.Empty(type.DeclaringSyntaxReferences);
        Assert.Equal("A useful value.", type.Documentation.Summary);
        Assert.Equal("Stored number.", Assert.Single(type.Fields).Documentation.Summary);
        Assert.Equal("Reads the number.", Assert.Single(type.Methods).Documentation.Summary);
        Assert.Equal("Adds two numbers.", function.Documentation.Summary);
        Assert.Equal("First value.", function.Documentation.Parameters["left"]);
        Assert.Equal("Their sum.", function.Documentation.Returns);
        Assert.Equal(64, reference.LibraryIdentity.ContentIdentity.Length);
    }

    [Fact]
    public void ReadonlyPropertyAndIndexerDimensionsRoundTripWithoutChangingTheFormat()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Library;
            struct Buffer
            {
                private byte value;
                public readonly byte* View { get { return &value; } }
                public byte* readonly Pointer { get { return null; } }
                public readonly byte* readonly Data { get { return &value; } }
                public readonly byte* readonly this[int index] { get { return &value; } }
            }
            interface IBuffer
            {
                readonly byte* readonly Data { get; }
                readonly byte* readonly this[int index] { get; }
            }
            template BufferShape
            {
                readonly byte* readonly Data { get; }
                readonly byte* readonly this[int index] { get; }
            }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));

        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("ReadonlyAccessors")));
        NamespaceSymbol scope = Assert.Single(reference.GlobalNamespace.Namespaces);
        StructTypeSymbol buffer = Assert.Single(scope.Structs);
        PropertySymbol view = buffer.Properties.Single(property => property.Name == "View");
        PropertySymbol pointer = buffer.Properties.Single(property => property.Name == "Pointer");
        PropertySymbol property = buffer.Properties.Single(property => property.Name == "Data");
        IndexerSymbol indexer = Assert.Single(buffer.Indexers);
        InterfacePropertySymbol interfaceProperty = Assert.Single(Assert.Single(scope.Interfaces).Properties);
        InterfaceIndexerSymbol interfaceIndexer = Assert.Single(Assert.Single(scope.Interfaces).Indexers);
        TemplateSymbol template = Assert.Single(scope.Templates);
        TemplatePropertyRequirementSymbol templateProperty =
            Assert.Single(template.Members.OfType<TemplatePropertyRequirementSymbol>());
        TemplateIndexerRequirementSymbol templateIndexer =
            Assert.Single(template.Members.OfType<TemplateIndexerRequirementSymbol>());

        Assert.True(Assert.IsType<PointerTypeSymbol>(view.Type).IsReadonly);
        Assert.False(view.IsReadonly);
        Assert.False(Assert.IsType<PointerTypeSymbol>(pointer.Type).IsReadonly);
        Assert.True(pointer.IsReadonly);

        foreach ((TypeSymbol Type, bool IsReadonly) member in new[]
        {
            (property.Type, property.IsReadonly),
            (indexer.Type, indexer.IsReadonly),
            (interfaceProperty.Type, interfaceProperty.IsReadonly),
            (interfaceIndexer.Type, interfaceIndexer.IsReadonly),
            (templateProperty.Type, templateProperty.IsReadonly),
            (templateIndexer.Type, templateIndexer.IsReadonly),
        })
        {
            Assert.True(Assert.IsType<PointerTypeSymbol>(member.Type).IsReadonly);
            Assert.True(member.IsReadonly);
        }
        Assert.True(property.Getter!.IsReadonly);
        Assert.True(Assert.IsType<PointerTypeSymbol>(property.Getter.ReturnType).IsReadonly);
        Assert.True(indexer.Getter!.IsReadonly);
        Assert.True(Assert.IsType<PointerTypeSymbol>(indexer.Getter.ReturnType).IsReadonly);
    }

    [Fact]
    public void ReadonlyPropertyTemplateMatchingWorksAcrossXelib()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Contracts;
            template BufferShape { readonly byte* readonly Data { get; } }
            public void Inspect<T>(readonly T& value) where T : BufferShape
            {
                readonly byte* pointer = value.Data;
            }
            """, "contracts.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("ReadonlyContracts")));

        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Contracts;
            namespace App;
            struct Buffer
            {
                private byte value;
                public readonly byte* readonly Data { get { return &value; } }
            }
            void Run()
            {
                Buffer buffer = Buffer();
                Inspect<Buffer>(buffer);
            }
            """, "consumer.xe"));

        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
    }

    [Fact]
    public void SemanticBytesIgnoreAbsoluteSourcePathAndTargetChoice()
    {
        const string text = "namespace Stable; public int Value() { return 42; }";
        Compilation first = Compilation.Create(SourceText.From(text, "C:/one/value.xe"));
        Compilation second = Compilation.Create(SourceText.From(text, "D:/two/value.xe"));

        byte[] left = XelibWriter.Write(first, new XelibWriteOptions("Stable"));
        byte[] right = XelibWriter.Write(second, new XelibWriteOptions("Stable"));

        Assert.Equal(left, right);
        Assert.DoesNotContain("C:/one", Encoding.UTF8.GetString(left), StringComparison.Ordinal);
        Assert.DoesNotContain("D:/two", Encoding.UTF8.GetString(right), StringComparison.Ordinal);
    }

    [Fact]
    public void TargetLayoutInLibraryBodyBindsOnlyForEachConsumerTarget()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace PortableLayout; public nuint PointerBytes() { return sizeof(int*); }",
            "library.xe"));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("PortableLayout"));
        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using PortableLayout; namespace App; int Main() { return cast<int>(PointerBytes()); }",
            "app.xe"));
        var narrowTarget = new LlvmTargetOptions("i686-pc-windows-msvc");
        var wideTarget = new LlvmTargetOptions("x86_64-pc-windows-msvc");

        Compilation narrow = LlvmIrGenerator.BindForTarget(app, narrowTarget);
        Compilation wide = LlvmIrGenerator.BindForTarget(app, wideTarget);
        string narrowIr = new LlvmIrGenerator().GenerateForTarget(narrow, narrowTarget);
        string wideIr = new LlvmIrGenerator().GenerateForTarget(wide, wideTarget);

        Assert.Contains("ret i32 4", narrowIr, StringComparison.Ordinal);
        Assert.Contains("ret i64 8", wideIr, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericLayoutConstantsRemainDeferredAndBindForEachConsumerTarget()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace GenericLayout;
            struct State<T> {
                const nuint Width = sizeof(T);
                const nuint Alignment = alignof(T);
            }
            """, "library.xe"));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("GenericLayout"));
        XelibContainer container = XelibContainer.Read(bytes);
        ImmutableArray<XelibSymbolRecord> symbols = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(
            container.GetRequiredSection(XelibSectionKind.Symbols).AsSpan(), null);
        XelibSymbolRecord[] constants = symbols.Where(symbol =>
            symbol.Kind == XelibSymbolKind.Constant && symbol.ConstantExpression is not null).ToArray();
        Assert.Equal(2, constants.Length);
        Assert.All(constants, constant =>
            Assert.True(ContainsOpcode(constant.ConstantExpression!, XelibBodyOpcode.TypeLayout)));

        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            namespace App;
            int Main() {
                return cast<int>(GenericLayout.State<int*>.Width +
                    GenericLayout.State<int*>.Alignment);
            }
            """, "app.xe"));
        var narrowTarget = new LlvmTargetOptions("i686-pc-windows-msvc");
        var wideTarget = new LlvmTargetOptions("x86_64-pc-windows-msvc");

        string narrowIr = new LlvmIrGenerator().GenerateForTarget(
            LlvmIrGenerator.BindForTarget(app, narrowTarget), narrowTarget);
        string wideIr = new LlvmIrGenerator().GenerateForTarget(
            LlvmIrGenerator.BindForTarget(app, wideTarget), wideTarget);

        Assert.Contains("ret i32 8", narrowIr, StringComparison.Ordinal);
        Assert.Contains("ret i32 16", wideIr, StringComparison.Ordinal);

        static bool ContainsOpcode(XelibBodyNode node, XelibBodyOpcode opcode) =>
            node.Opcode == opcode || node.Children.Any(child => ContainsOpcode(child, opcode));
    }

    [Fact]
    public void QualifiedGenericStaticAccessAcrossXelibPreservesAccessibility()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace GenericAccess;
            struct State<T> {
                private static int Hidden = 100;
                public static int Value = 40;
                private static int Secret() { return 100; }
                public static int Read() { return 2; }
            }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("GenericAccess")));

        Compilation valid = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            namespace App;
            int Main() {
                return GenericAccess.State<int>.Value + GenericAccess.State<int>.Read();
            }
            """, "valid.xe"));
        Assert.False(valid.HasErrors, string.Join(Environment.NewLine, valid.Diagnostics));
        _ = new LlvmIrGenerator().Generate(valid);

        Compilation invalid = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            namespace App;
            int Main() {
                return GenericAccess.State<int>.Hidden + GenericAccess.State<int>.Secret();
            }
            """, "invalid.xe"));
        Assert.Equal(2, invalid.Diagnostics.Count(diagnostic =>
            diagnostic.Id == Xenon.Compiler.Diagnostics.DiagnosticIds.InaccessibleSymbol));
    }

    [Fact]
    public void OrdinaryLayoutConstantsAndLibraryFunctionsBindForEachConsumerTarget()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace OrdinaryLayout;
            const nuint Width = sizeof(int*);
            const nuint Alignment = alignof(int*);
            public int LibraryMetric() { return cast<int>(Width + Alignment); }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("OrdinaryLayout"));
        XelibContainer container = XelibContainer.Read(bytes);
        ImmutableArray<XelibSymbolRecord> symbols = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(
            container.GetRequiredSection(XelibSectionKind.Symbols).AsSpan(), null);
        Assert.Equal(2, symbols.Count(symbol => symbol.Kind == XelibSymbolKind.Constant &&
            symbol.ConstantExpression is { Opcode: XelibBodyOpcode.TypeLayout }));

        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using OrdinaryLayout;
            namespace App;
            int Main() { return cast<int>(Width + Alignment) + LibraryMetric(); }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        var narrowTarget = new LlvmTargetOptions("i686-pc-windows-msvc");
        var wideTarget = new LlvmTargetOptions("x86_64-pc-windows-msvc");

        string narrowIr = new LlvmIrGenerator().GenerateForTarget(
            LlvmIrGenerator.BindForTarget(app, narrowTarget), narrowTarget);
        string wideIr = new LlvmIrGenerator().GenerateForTarget(
            LlvmIrGenerator.BindForTarget(app, wideTarget), wideTarget);

        Assert.Contains("ret i32 8", narrowIr, StringComparison.Ordinal);
        Assert.Contains("add i32 8", narrowIr, StringComparison.Ordinal);
        Assert.Contains("ret i32 16", wideIr, StringComparison.Ordinal);
        Assert.Contains("add i32 16", wideIr, StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataOnlyGenericBodyScanAcceptsAnyJsonPropertyOrderAndRejectsMissingIds()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Ordered; public T Identity<T>(T value) { return move value; }", "library.xe"));
        byte[] valid = XelibWriter.Write(library, new XelibWriteOptions("Ordered"));
        XelibContainer container = XelibContainer.Read(valid);
        byte[] bodies = container.GetRequiredSection(XelibSectionKind.Bodies).ToArray();
        byte[] reordered = RewriteBodyJson(bodies, omitIds: false);
        byte[] reorderedImage = RewriteSection(valid, XelibSectionKind.Bodies, reordered);

        LibraryCompilationReference full = XelibReader.Read(reorderedImage);
        LibraryCompilationReference metadataOnly = XelibReader.Read(reorderedImage, metadataOnly: true);
        foreach (LibraryCompilationReference reference in new[] { full, metadataOnly })
        {
            Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
                "using Ordered; namespace App; int Main() { return Identity<int>(42); }", "app.xe"));
            Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        }

        byte[] missingIds = RewriteBodyJson(bodies, omitIds: true);
        XelibFormatException exception = Assert.Throws<XelibFormatException>(() =>
            XelibReader.Read(RewriteSection(valid, XelibSectionKind.Bodies, missingIds), metadataOnly: true));
        Assert.Equal(XelibErrorCode.InvalidReference, exception.Code);

        static byte[] RewriteBodyJson(byte[] bytes, bool omitIds)
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                writer.WriteStartArray();
                foreach (JsonElement body in document.RootElement.EnumerateArray())
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("root");
                    body.GetProperty("root").WriteTo(writer);
                    writer.WriteNumber("functionSymbolId", body.GetProperty("functionSymbolId").GetInt32());
                    if (!omitIds) writer.WriteNumber("id", body.GetProperty("id").GetInt32());
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return output.ToArray();
        }
    }

    [Fact]
    public void StaticConsumptionSelectsOnlyReachableLibraryBodies()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Selective;
            public int Used() { return Helper(); }
            int Helper() { return 42; }
            public int Unused() { return 99; }
            """, "library.xe"));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("Selective"));
        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Compilation app = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("using Selective; namespace App; int Main() { return Used(); }", "app.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        string[] selected = app.GetStaticImplementationFunctions().Select(function => function.Symbol.Name).ToArray();
        Assert.Contains("Used", selected);
        Assert.Contains("Helper", selected);
        Assert.DoesNotContain("Unused", selected);
    }

    [Fact]
    public void ConstructingOrdinaryImportedStructSelectsItsHiddenInstanceInitializer()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Initializers;
            int Seed() { return 42; }
            struct Value { public int Number = Seed(); }
            struct Unused { public int Number = 99; }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Initializers")));

        AssertInitializerSelected("""
            using Initializers;
            namespace DirectApp;
            int Main() { Value value = Value(); return value.Number; }
            """);
        AssertInitializerSelected("""
            using Initializers;
            namespace HeapApp;
            int Main() {
                Value* value = new Value();
                int result = value->Number;
                delete(value);
                return result;
            }
            """);
        AssertInitializerSelected("""
            using Initializers;
            namespace StorageApp;
            int Main() {
                storage<Value> value = Value();
                return value.Number;
            }
            """);

        void AssertInitializerSelected(string source)
        {
            Compilation app = Compilation.Create(new CompilationOptions(), [reference],
                SourceText.From(source, "app.xe"));
            Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
            var target = new LlvmTargetOptions(LlvmTargetPlatform.HostTriple);
            app = LlvmIrGenerator.BindForTarget(app, target);
            Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
            BoundFunction initializer = Assert.Single(app.GetStaticImplementationFunctions(), function =>
                function.Symbol.FunctionKind == FunctionKind.InstanceInitializer &&
                function.Symbol.ContainingStruct?.Name == "Value");
            Assert.DoesNotContain(app.GetStaticImplementationFunctions(), function =>
                function.Symbol.FunctionKind == FunctionKind.InstanceInitializer &&
                function.Symbol.ContainingStruct?.Name == "Unused");
            Assert.Contains(app.GetStaticImplementationFunctions(), function =>
                function.Symbol.Name == "Seed");
            string ir = new LlvmIrGenerator().GenerateForTarget(app, target);
            Assert.Contains("call void @__xenon_function_", ir, StringComparison.Ordinal);
            Assert.Contains($"_{Convert.ToHexString(Encoding.UTF8.GetBytes(initializer.Symbol.QualifiedName))}",
                ir, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ImportedReachabilitySeparatesTypeReferencesLifecycleAndDispatch()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Precision;
            interface IExtra { int Extra(); }
            int Seed() { return 42; }
            void Cleanup() { }
            struct Heavy : IExtra {
                public int Value = Seed();
                public int Extra() { return 99; }
                public virtual int Read() { return Value; }
                public ~Heavy() { Cleanup(); }
            }
            public Heavy* Echo(Heavy* value) { return value; }
            public int Invoke(Heavy* value) { return value->Read(); }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Precision")));
        StructTypeSymbol heavy = Assert.Single(reference.GlobalNamespace.Namespaces).Structs.Single();

        Compilation referenceOnly = CreateApp("""
            using Precision;
            namespace ReferenceApp;
            Heavy* Relay(Heavy* value) { return Echo(value); }
            int Main() { Heavy untouched; return cast<int>(sizeof(Heavy)); }
            """);
        BoundFunction[] referenced = referenceOnly.GetStaticImplementationFunctions().ToArray();
        Assert.Contains(referenced, function => function.Symbol.Name == "Echo");
        Assert.DoesNotContain(referenced, IsHeavyRuntimeBody);
        Assert.True(referenceOnly.IsImportedSymbolNativeReachable(heavy));
        Assert.False(referenceOnly.IsImportedDispatchTypeNativeReachable(heavy));
        Assert.False(referenceOnly.IsImportedInterfaceDispatchTypeNativeReachable(heavy));

        Compilation virtualCall = CreateApp("""
            using Precision;
            namespace VirtualApp;
            int Call(Heavy* value) { return Invoke(value); }
            int Main() { return 0; }
            """);
        BoundFunction[] virtualBodies = virtualCall.GetStaticImplementationFunctions().ToArray();
        Assert.Contains(virtualBodies, function => function.Symbol.Name == "Invoke");
        Assert.Contains(virtualBodies, function => function.Symbol.Name == "Read");
        Assert.DoesNotContain(virtualBodies, function => function.Symbol.Name == "Extra");
        Assert.True(virtualCall.IsImportedDispatchTypeNativeReachable(heavy));
        Assert.False(virtualCall.IsImportedInterfaceDispatchTypeNativeReachable(heavy));

        Compilation construction = CreateApp("""
            using Precision;
            namespace ConstructionApp;
            int Main() { Heavy value = Heavy(); return value.Value; }
            """);
        BoundFunction[] constructed = construction.GetStaticImplementationFunctions().ToArray();
        Assert.Contains(constructed, function => function.Symbol.Name == "Seed");
        Assert.Contains(constructed, function => function.Symbol.Name == "Cleanup");
        Assert.Contains(constructed, function => function.Symbol.FunctionKind == FunctionKind.InstanceInitializer &&
            function.Symbol.ContainingStruct?.Name == "Heavy");
        Assert.Contains(constructed, function => function.Symbol.FunctionKind == FunctionKind.Destructor &&
            function.Symbol.ContainingStruct?.Name == "Heavy");
        Assert.True(construction.IsImportedDispatchTypeNativeReachable(heavy));
        Assert.False(construction.IsImportedInterfaceDispatchTypeNativeReachable(heavy));

        Compilation CreateApp(string source)
        {
            Compilation app = Compilation.Create(new CompilationOptions(), [reference],
                SourceText.From(source, "app.xe"));
            Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
            return app;
        }

        static bool IsHeavyRuntimeBody(BoundFunction function) =>
            function.Symbol.ContainingStruct?.Name == "Heavy" ||
            function.Symbol.Name is "Seed" or "Cleanup";
    }

    [Fact]
    public void AssignmentAndAtomicReplacementSelectOnlyRequiredXelibDestructors()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace DestructorClosure;
            void ResourceCleanup() { }
            void OtherCleanup() { }
            void GenericCleanup() { }
            struct Resource {
                public int Value;
                public ~Resource() { ResourceCleanup(); }
            }
            struct Other {
                public ~Other() { OtherCleanup(); }
            }
            struct Holder {
                public Resource A;
                public Resource B;
                public void Replace() { A = move B; B = Resource(); }
            }
            struct FirstInitializationOnly {
                public Resource Value;
                public FirstInitializationOnly() { Value = Resource(); }
            }
            struct Payload { public int Value; }
            struct OwnershipHolder { public shared<Payload> Owner; }
            struct Box<T> {
                public T Value;
                public ~Box() { GenericCleanup(); }
            }
            public void AssignAfterDeclaration() {
                Resource value;
                value = Resource();
            }
            public void Replace(storage<Holder>& holder) { holder.Replace(); }
            public FirstInitializationOnly* AllocateWithoutDestruction() {
                return new FirstInitializationOnly();
            }
            public void AssignOwnershipHolder() {
                OwnershipHolder value;
                value = OwnershipHolder();
            }
            public void AssignAtomic(atomic<Resource>& target, Resource& replacement) {
                target = replacement;
            }
            public bool CompareAtomic(
                atomic<Resource>& target,
                Resource& expected,
                Resource& desired) {
                return target : expected --> desired;
            }
            public void SwapAtomic(atomic<Resource>& target, Resource& replacement) {
                target <-> replacement;
            }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("DestructorClosure")));

        AssertClosure("int Main() { AssignAfterDeclaration(); return 0; }", "Resource", "ResourceCleanup");
        AssertClosure("void Run(storage<Holder>& value) { Replace(value); }", "Resource", "ResourceCleanup");
        Compilation firstInitialization = CreateApp(
            "FirstInitializationOnly* Run() { return AllocateWithoutDestruction(); }");
        BoundFunction[] firstInitializationBodies = firstInitialization.GetStaticImplementationFunctions().ToArray();
        Assert.Contains(firstInitializationBodies, function => function.Symbol.Name == "AllocateWithoutDestruction");
        Assert.DoesNotContain(firstInitializationBodies, function =>
            function.Symbol.FunctionKind is FunctionKind.Destructor or FunctionKind.DestructorGlue ||
            function.Symbol.Name == "ResourceCleanup");
        GenerateForHost(firstInitialization);
        Compilation ownership = CreateApp("int Main() { AssignOwnershipHolder(); return 0; }");
        BoundFunction[] ownershipBodies = ownership.GetStaticImplementationFunctions().ToArray();
        StructTypeSymbol ownershipHolder = Assert.Single(reference.GlobalNamespace.Namespaces).Structs
            .Single(type => type.Name == "OwnershipHolder");
        Assert.Contains(ownershipBodies, function =>
            ReferenceEquals(function.Symbol, ownershipHolder.CompleteDestructor));
        Assert.DoesNotContain(ownershipBodies, function => function.Symbol.Name == "OtherCleanup");
        GenerateForHost(ownership);
        AssertClosure("""
            void Run(atomic<Resource>& target, Resource& value) {
                AssignAtomic(target, value);
            }
            """, "Resource", "ResourceCleanup");
        AssertClosure("""
            bool Run(atomic<Resource>& target, Resource& expected, Resource& desired) {
                return CompareAtomic(target, expected, desired);
            }
            """, "Resource", "ResourceCleanup");
        Compilation atomicSwap = CreateApp("""
            void Run(atomic<Resource>& target, Resource& replacement) {
                SwapAtomic(target, replacement);
            }
            """);
        BoundFunction[] atomicSwapBodies = atomicSwap.GetStaticImplementationFunctions().ToArray();
        Assert.Contains(atomicSwapBodies, function => function.Symbol.Name == "SwapAtomic");
        Assert.DoesNotContain(atomicSwapBodies, function =>
            function.Symbol.ContainingStruct?.Name == "Resource" ||
            function.Symbol.Name == "ResourceCleanup");
        GenerateForHost(atomicSwap);

        Compilation generic = CreateApp("int Main() { Box<int> value; value = Box<int>(); return 0; }");
        BoundFunction[] genericBodies = generic.GetStaticImplementationFunctions().ToArray();
        BoundFunction genericDestructor = Assert.Single(genericBodies, function =>
            function.Symbol.ContainingStruct is { } owner &&
            owner.Name.StartsWith("Box", StringComparison.Ordinal) &&
            ReferenceEquals(function.Symbol, owner.CompleteDestructor));
        Assert.Contains(genericBodies, function => function.Symbol.Name == "GenericCleanup");
        Assert.DoesNotContain(genericBodies, function => function.Symbol.Name is "OtherCleanup");
        GenerateForHost(generic);
        Assert.NotNull(genericDestructor.Symbol.ContainingStruct);

        void AssertClosure(string body, string destructorOwner, string dependency)
        {
            Compilation app = CreateApp(body);
            BoundFunction[] selected = app.GetStaticImplementationFunctions().ToArray();
            Assert.Contains(selected, function => function.Symbol.FunctionKind == FunctionKind.Destructor &&
                function.Symbol.ContainingStruct?.Name == destructorOwner);
            Assert.Contains(selected, function => function.Symbol.Name == dependency);
            Assert.DoesNotContain(selected, function => function.Symbol.Name is "OtherCleanup");
            GenerateForHost(app);
        }

        static void GenerateForHost(Compilation compilation)
        {
            var target = LlvmTargetOptions.CreateHost();
            Compilation targeted = LlvmIrGenerator.BindForTarget(compilation, target);
            Assert.False(targeted.HasErrors, string.Join(Environment.NewLine, targeted.Diagnostics));
            _ = new LlvmIrGenerator().GenerateForTarget(targeted, target);
        }

        Compilation CreateApp(string body)
        {
            Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From($$"""
                using DestructorClosure;
                namespace App;
                {{body}}
                """, "app.xe"));
            Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
            return app;
        }
    }

    [Fact]
    public void StackArrayReachabilitySelectsOnlyRequiredXelibElementDestructors()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace StackArrayClosure;
            void ResourceCleanup() { }
            void StructInitialize() { }
            void StructCleanup() { }
            void OtherCleanup() { }
            int InitializeElement() { StructInitialize(); return 1; }
            struct Resource {
                public int Value;
                public ~Resource() { ResourceCleanup(); }
            }
            struct Element {
                public int Value = InitializeElement();
                public ~Element() { StructCleanup(); }
            }
            struct Other {
                public ~Other() { OtherCleanup(); }
            }
            public int StackAtomicOwned(int count) {
                atomic<shared<Resource>>[] values = atomic<shared<Resource>>[count];
                return count;
            }
            public int StackStruct(int count) {
                Element[] values = Element[count];
                return count;
            }
            public int StackTrivial(int count) {
                int[] values = int[count];
                return count;
            }
            public int HeapAtomicOwned(int count) {
                atomic<shared<Resource>>[] values = new atomic<shared<Resource>>[count];
                return count;
            }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("StackArrayClosure")));

        Compilation atomic = CreateApp("StackAtomicOwned");
        BoundFunction[] atomicBodies = atomic.GetStaticImplementationFunctions().ToArray();
        FunctionSymbol atomicElementDestructor = GetArrayElementDestructor(atomicBodies, "StackAtomicOwned");
        Assert.Contains(atomicBodies, function => ReferenceEquals(function.Symbol, atomicElementDestructor));
        Assert.Contains(atomicBodies, function => function.Symbol.Name == "ResourceCleanup");
        Assert.DoesNotContain(atomicBodies, function => function.Symbol.Name is "OtherCleanup" or "StructCleanup");
        GenerateForHost(atomic);

        Compilation structure = CreateApp("StackStruct");
        BoundFunction[] structBodies = structure.GetStaticImplementationFunctions().ToArray();
        FunctionSymbol structElementDestructor = GetArrayElementDestructor(structBodies, "StackStruct");
        Assert.Contains(structBodies, function => ReferenceEquals(function.Symbol, structElementDestructor));
        Assert.Contains(structBodies, function => function.Symbol.Name == "StructInitialize");
        Assert.Contains(structBodies, function => function.Symbol.Name == "StructCleanup");
        Assert.DoesNotContain(structBodies, function => function.Symbol.Name == "OtherCleanup");
        GenerateForHost(structure);

        Compilation trivial = CreateApp("StackTrivial");
        BoundFunction[] trivialBodies = trivial.GetStaticImplementationFunctions().ToArray();
        Assert.Null(GetArrayElementDestructorOrNull(trivialBodies, "StackTrivial"));
        Assert.DoesNotContain(trivialBodies, function =>
            function.Symbol.FunctionKind is FunctionKind.Destructor or FunctionKind.DestructorGlue ||
            function.Symbol.Name.EndsWith("Cleanup", StringComparison.Ordinal));
        GenerateForHost(trivial);

        Compilation heap = CreateApp("HeapAtomicOwned");
        BoundFunction[] heapBodies = heap.GetStaticImplementationFunctions().ToArray();
        FunctionSymbol heapElementDestructor = GetArrayElementDestructor(heapBodies, "HeapAtomicOwned");
        Assert.DoesNotContain(heapBodies, function => ReferenceEquals(function.Symbol, heapElementDestructor));
        Assert.DoesNotContain(heapBodies, function => function.Symbol.Name.EndsWith("Cleanup", StringComparison.Ordinal));
        GenerateForHost(heap);

        Compilation CreateApp(string entry)
        {
            Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From($$"""
                using StackArrayClosure;
                namespace App;
                int Main() { return {{entry}}(1); }
                """, "app.xe"));
            Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
            return app;
        }

        static FunctionSymbol GetArrayElementDestructor(BoundFunction[] bodies, string functionName) =>
            Assert.IsType<FunctionSymbol>(GetArrayElementDestructorOrNull(bodies, functionName));

        static FunctionSymbol? GetArrayElementDestructorOrNull(BoundFunction[] bodies, string functionName)
        {
            BoundFunction body = Assert.Single(bodies, function => function.Symbol.Name == functionName);
            BoundArrayCreationExpression? creation = null;
            XelibBodyCodec.Collect(body.Body, _ => { }, _ => { }, node =>
            {
                if (node is BoundArrayCreationExpression array)
                    creation = array;
            });
            return TypeFacts.GetCompleteDestructor(Assert.IsType<BoundArrayCreationExpression>(creation).ElementType);
        }

        static void GenerateForHost(Compilation compilation)
        {
            var target = LlvmTargetOptions.CreateHost();
            Compilation targeted = LlvmIrGenerator.BindForTarget(compilation, target);
            Assert.False(targeted.HasErrors, string.Join(Environment.NewLine, targeted.Diagnostics));
            _ = new LlvmIrGenerator().GenerateForTarget(targeted, target);
        }
    }

    [Fact]
    public void ImportedGenericDestructorCleanupUsesConcreteOwner()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace GenericCleanup;
            struct Box<T>
            {
                public storage<byte> MoveOnly;
                public ~Box() { }
            }
            """, "generic-cleanup.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("GenericCleanup")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using GenericCleanup;
            namespace App;
            int Main()
            {
                Box<int> value = Box<int>();
                return 0;
            }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));

        BoundFunction destructor = Assert.Single(app.GetStaticImplementationFunctions(), function =>
            function.Symbol.FunctionKind == FunctionKind.Destructor &&
            function.Symbol.ContainingStruct?.GenericDefinition is not null);
        BoundDestroyFieldsExpression cleanup = Assert.IsType<BoundDestroyFieldsExpression>(
            destructor.Body.ExitCleanup);
        Assert.Same(destructor.Symbol.ContainingStruct, cleanup.StructType);

        var target = LlvmTargetOptions.CreateHost();
        Compilation targeted = LlvmIrGenerator.BindForTarget(app, target);
        Assert.False(targeted.HasErrors, string.Join(Environment.NewLine, targeted.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(targeted, target);
    }

    [Fact]
    public void ImportedGenericExplicitDestructUsesSpecializedValueDestructor()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace GenericDestruct;
            public void Destroy<T>(T* value) { destruct(*value); }
            """, "generic-destruct.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("GenericDestruct")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using GenericDestruct;
            namespace App;
            struct Resource { public ~Resource() { } }
            void Exercise(Resource* value) { Destroy<Resource>(value); }
            int Main() { return 0; }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));

        BoundFunction destroy = Assert.Single(app.GetStaticImplementationFunctions(), function =>
            function.Symbol.GenericDefinition?.Name == "Destroy");
        BoundExplicitDestructExpression? explicitDestruct = null;
        XelibBodyCodec.Collect(destroy.Body, _ => { }, _ => { }, node =>
        {
            if (node is BoundExplicitDestructExpression destruction)
                explicitDestruct = destruction;
        });
        Assert.NotNull(explicitDestruct?.Destructor);
        Assert.Same(TypeFacts.GetCompleteDestructor(explicitDestruct!.ValueType),
            explicitDestruct.Destructor);

        var target = LlvmTargetOptions.CreateHost();
        Compilation targeted = LlvmIrGenerator.BindForTarget(app, target);
        Assert.False(targeted.HasErrors, string.Join(Environment.NewLine, targeted.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(targeted, target);
    }

    [Fact]
    public void RawMemoryOperationsAndPlacementFlagsRoundTripThroughXelibBodies()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace RawBodies;
            struct Resource { public int Value; public ~Resource() { Value = 0; } }
            public int Exercise()
            {
                Resource* memory = cast<Resource*>(malloc(sizeof(Resource), alignof(Resource)));
                *memory = Resource();
                destruct(*memory);
                free(memory);
                byte* zeroed = cast<byte*>(calloc(4, sizeof(byte)));
                free(zeroed);
                Resource* owned = new Resource();
                delete(owned);
                return 42;
            }
            """, "raw-bodies.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("RawBodies")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using RawBodies;
            namespace App;
            int Main() { return Exercise(); }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));

        BoundFunction exercise = Assert.Single(app.GetStaticImplementationFunctions(), function => function.Symbol.Name == "Exercise");
        var allocationKinds = new HashSet<RawAllocationKind>();
        bool hasFree = false;
        bool hasDelete = false;
        bool hasPlacement = false;
        XelibBodyCodec.Collect(exercise.Body, _ => { }, _ => { }, node =>
        {
            if (node is BoundRawAllocationExpression allocation) allocationKinds.Add(allocation.AllocationKind);
            if (node is BoundFreeExpression) hasFree = true;
            if (node is BoundDeleteExpression) hasDelete = true;
            if (node is BoundAssignmentExpression { IsRawPlacement: true }) hasPlacement = true;
        });

        Assert.Contains(RawAllocationKind.AlignedMalloc, allocationKinds);
        Assert.Contains(RawAllocationKind.Calloc, allocationKinds);
        Assert.True(hasFree);
        Assert.True(hasDelete);
        Assert.True(hasPlacement);
    }

    [Fact]
    public void UsedNonGenericXelibTlsSelectsItsDestructorButUnusedSiblingDoesNot()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace TlsDestructorClosure;
            void UsedCleanup() { }
            void UnusedCleanup() { }
            struct Resource {
                public int Value = 42;
                public ~Resource() { UsedCleanup(); }
            }
            struct UnusedResource {
                public int Value = 99;
                public ~UnusedResource() { UnusedCleanup(); }
            }
            struct State {
                public static threadlocal Resource Current = Resource();
                public static threadlocal UnusedResource Unused = UnusedResource();
            }
            public int Read() { return State.Current.Value; }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("TlsDestructorClosure")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using TlsDestructorClosure;
            namespace App;
            int Main() { return Read(); }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));

        BoundFunction[] selected = app.GetStaticImplementationFunctions().ToArray();
        Assert.Contains(selected, function => function.Symbol.FunctionKind == FunctionKind.ThreadLocalInitializer &&
            function.Symbol.ThreadLocalField?.Name == "Current");
        Assert.Contains(selected, function => function.Symbol.FunctionKind == FunctionKind.Destructor &&
            function.Symbol.ContainingStruct?.Name == "Resource");
        Assert.Contains(selected, function => function.Symbol.Name == "UsedCleanup");
        Assert.DoesNotContain(selected, function => function.Symbol.FunctionKind == FunctionKind.ThreadLocalInitializer &&
            function.Symbol.ThreadLocalField?.Name == "Unused");
        Assert.DoesNotContain(selected, function => function.Symbol.ContainingStruct?.Name == "UnusedResource" ||
            function.Symbol.Name == "UnusedCleanup");
        var target = LlvmTargetOptions.CreateHost();
        app = LlvmIrGenerator.BindForTarget(app, target);
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(app, target);
    }

    [Fact]
    public void StaticConsumptionRootsOnlyUsedDispatchStaticTlsAndPrivateDependenciesPlusExports()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Reachability;
            interface IFoo { int Read(); }
            interface IBar { int Read(); }
            struct Used : IFoo { public int Read() { return PrivateHelper(); } }
            struct Unused : IBar { public int Read() { return 99; } }
            struct UnusedBase { public virtual int Value() { return 10; } }
            struct UnusedDerived : UnusedBase { public override int Value() { return 11; } }
            struct State {
                public static threadlocal int UsedTls = 40;
                public static threadlocal int UnusedTls = 100;
            }
            int PrivateHelper() { return 2; }
            public int Entry() { Used value = Used(); return value.Read() + State.UsedTls; }
            export int NativeExport() { return 7; }
            public int NeverCalled() { return 8; }
            """, "library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Reachability")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Reachability; namespace App; int Main() { return Entry(); }", "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));

        BoundFunction[] selected = app.GetStaticImplementationFunctions().ToArray();
        string[] names = selected.Select(item => item.Symbol.Name).ToArray();
        Assert.Contains("Entry", names);
        Assert.Contains("PrivateHelper", names);
        Assert.Contains(selected, item => item.Symbol.FunctionKind == FunctionKind.ThreadLocalInitializer &&
            item.Symbol.ThreadLocalField?.Name == "UsedTls");
        Assert.Contains("NativeExport", names);
        Assert.DoesNotContain("NeverCalled", names);
        Assert.DoesNotContain(selected, item => item.Symbol.ContainingStruct?.Name is "Unused" or "UnusedBase" or "UnusedDerived");

        NamespaceSymbol scope = Assert.Single(reference.GlobalNamespace.Namespaces);
        StructTypeSymbol state = scope.Structs.Single(type => type.Name == "State");
        Assert.True(app.IsImportedSymbolNativeReachable(state.StaticFields.Single(field => field.Name == "UsedTls")));
        Assert.False(app.IsImportedSymbolNativeReachable(state.StaticFields.Single(field => field.Name == "UnusedTls")));
        Assert.False(app.IsImportedDispatchTypeNativeReachable(
            scope.Structs.Single(type => type.Name == "UnusedDerived")));
    }

    [Fact]
    public void ExplicitlyReferencedButUnusedXelibContributesNoImplementationBodies()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace BigLibrary;
            public int First() { return 1; }
            public int Second() { return 2; }
            struct VirtualType { public virtual int Read() { return 3; } }
            """, "library.xe"));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("BigLibrary")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("namespace App; int Main() { return 0; }", "app.xe"));

        Assert.DoesNotContain(app.GetStaticImplementationFunctions(),
            function => app.GetOwningLibrary(function.Symbol) is not null);
        Assert.False(app.IsImportedDispatchTypeNativeReachable(
            Assert.Single(Assert.Single(reference.GlobalNamespace.Namespaces).Structs)));
    }

    [Fact]
    public void UncalledXelibNativeExportRemainsInFinalLlvmModule()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Exported; export int RequiredNativeSymbol() { return 42; }", "library.xe"));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Exported")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("namespace App; int Main() { return 0; }", "app.xe"));

        string ir = new LlvmIrGenerator().Generate(app);

        Assert.Contains("@Exported_RequiredNativeSymbol", ir, StringComparison.Ordinal);
        Assert.Contains(app.GetStaticImplementationFunctions(),
            function => function.Symbol.Name == "RequiredNativeSymbol");
    }

    [Fact]
    public void RepeatedXelibGenericCallsShareOneConsumerSpecialization()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace GenericDedup; public T Identity<T>(T value) { return move value; }", "library.xe"));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("GenericDedup")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using GenericDedup;
            namespace App;
            int Main() { return Identity<int>(20) + Identity<int>(22); }
            """, "app.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        Assert.Single(app.SemanticModel.Functions, function =>
            function.Symbol.GenericDefinition?.Name == "Identity" &&
            function.Symbol.TypeArguments is [PrimitiveTypeSymbol { Name: "int" }]);
    }

    [Fact]
    public void TamperedSemanticPayloadFailsContentIdentityValidation()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Integrity; public int Value() { return 42; }", "library.xe"));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("Integrity"));
        XelibSectionDescriptor body = XelibContainer.Read(bytes).SectionDescriptors.Single(descriptor =>
            descriptor.KnownKind == XelibSectionKind.Bodies);
        bytes[checked((int)body.Offset)] ^= 1;

        XelibFormatException exception = Assert.Throws<XelibFormatException>(() =>
            XelibMetadataReader.Read(bytes));
        Assert.Equal(XelibErrorCode.ContentIdentityMismatch, exception.Code);
    }

    [Fact]
    public void SameFqnInterfacesFromDifferentLibrariesKeepNominalIdentity()
    {
        Compilation first = Compilation.Create(SourceText.From("""
            namespace Shared;
            /// <summary>First contract.</summary>
            interface IService { int Read(); }
            """, "first.xe"));
        Compilation second = Compilation.Create(SourceText.From("""
            namespace Shared;
            /// <summary>Second contract.</summary>
            interface IService { int Read(); }
            """, "second.xe"));

        LibraryCompilationReference left = XelibReader.Read(
            XelibWriter.Write(first, new XelibWriteOptions("First")), metadataOnly: true);
        LibraryCompilationReference right = XelibReader.Read(
            XelibWriter.Write(second, new XelibWriteOptions("Second")), metadataOnly: true);
        InterfaceTypeSymbol leftType = Assert.Single(Assert.Single(left.GlobalNamespace.Namespaces).Interfaces);
        InterfaceTypeSymbol rightType = Assert.Single(Assert.Single(right.GlobalNamespace.Namespaces).Interfaces);

        Assert.Equal(leftType.FullName, rightType.FullName);
        Assert.False(TypeIdentity.AreSame(leftType, rightType));
        Assert.NotEqual(leftType.Origin.LibraryContentIdentity, rightType.Origin.LibraryContentIdentity);
    }

    [Fact]
    public void InvalidSemanticReferencesAndBodyOpcodesAreControlledFormatErrors()
    {
        Compilation library = Compilation.Create(SourceText.From(
            "namespace Defensive; public int Value() { return 42; }", "library.xe"));
        byte[] valid = XelibWriter.Write(library, new XelibWriteOptions("Defensive"));
        XelibContainer container = XelibContainer.Read(valid);

        ImmutableArray<XelibSymbolRecord> symbols = XelibJson.Deserialize<ImmutableArray<XelibSymbolRecord>>(
            container.GetRequiredSection(XelibSectionKind.Symbols).AsSpan(), null);
        int functionIndex = Array.FindIndex(symbols.ToArray(),
            symbol => symbol.Kind == XelibSymbolKind.Function);
        ImmutableArray<XelibSymbolRecord> invalidSymbols = symbols.SetItem(functionIndex,
            symbols[functionIndex] with { ReturnTypeId = int.MaxValue });
        XelibFormatException invalidReference = Assert.Throws<XelibFormatException>(() =>
        {
            _ = XelibReader.Read(RewriteSection(valid, XelibSectionKind.Symbols,
                XelibJson.Serialize(invalidSymbols)));
        });
        Assert.Equal(XelibErrorCode.InvalidReference, invalidReference.Code);

        ImmutableArray<XelibBodyRecord> bodies = XelibJson.Deserialize<ImmutableArray<XelibBodyRecord>>(
            container.GetRequiredSection(XelibSectionKind.Bodies).AsSpan(), null);
        ImmutableArray<XelibBodyRecord> invalidBodies = bodies.SetItem(0,
            bodies[0] with { Root = bodies[0].Root with { Opcode = (XelibBodyOpcode)ushort.MaxValue } });
        XelibFormatException invalidOpcode = Assert.Throws<XelibFormatException>(() =>
        {
            _ = XelibReader.Read(RewriteSection(valid, XelibSectionKind.Bodies,
                XelibJson.Serialize(invalidBodies)));
        });
        Assert.Equal(XelibErrorCode.InvalidRecord, invalidOpcode.Code);
    }

    [Fact]
    public void CorruptSemanticPayloadFuzzNeverLeaksRawReaderExceptions()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Fuzz;
            struct Pair { public int Left; public int Right; }
            public int Add(int left, int right) { return left + right; }
            """, "library.xe"));
        byte[] valid = XelibWriter.Write(library, new XelibWriteOptions("Fuzz"));
        var random = new Random(0x58454C49);
        XelibSectionKind[] mutableSections =
        [
            XelibSectionKind.Strings, XelibSectionKind.Types, XelibSectionKind.Symbols,
            XelibSectionKind.Exports, XelibSectionKind.Documentation, XelibSectionKind.Bodies,
            XelibSectionKind.GenericImplementations,
        ];

        for (int iteration = 0; iteration < 64; iteration++)
        {
            XelibContainer container = XelibContainer.Read(valid);
            XelibSectionKind kind = mutableSections[random.Next(mutableSections.Length)];
            byte[] payload = container.GetRequiredSection(kind).ToArray();
            if (payload.Length == 0) continue;
            payload[random.Next(payload.Length)] ^= (byte)random.Next(1, 256);
            byte[] mutated = RewriteSection(valid, kind, payload);

            Exception? exception = Record.Exception(() =>
            {
                _ = XelibReader.Read(mutated);
            });
            Assert.True(exception is null or XelibFormatException,
                $"iteration {iteration} leaked {exception?.GetType().Name}: {exception?.Message}");
        }
    }

    [Fact]
    public void ExplicitLoaderDeduplicatesExactArtifactsRejectsConflictsAndNeverScansDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "xelib-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Compilation first = Compilation.Create(SourceText.From(
                "namespace Loader; public int Value() { return 1; }", "first.xe"));
            Compilation second = Compilation.Create(SourceText.From(
                "namespace Loader; public int Value() { return 2; }", "second.xe"));
            byte[] firstBytes = XelibWriter.Write(first, new XelibWriteOptions("Loader", "1.0"));
            string firstPath = Path.Combine(root, "first.xelib");
            string duplicatePath = Path.Combine(root, "duplicate.xelib");
            string conflictPath = Path.Combine(root, "conflict.xelib");
            File.WriteAllBytes(firstPath, firstBytes);
            File.WriteAllBytes(duplicatePath, firstBytes);
            File.WriteAllBytes(conflictPath,
                XelibWriter.Write(second, new XelibWriteOptions("Loader", "1.0")));
            File.WriteAllBytes(Path.Combine(root, "unrelated.xelib"), "invalid"u8.ToArray());

            Assert.Single(XelibReferenceLoader.LoadFiles([firstPath, duplicatePath]));
            Assert.Single(XelibReferenceLoader.LoadFiles([firstPath]));
            XelibFormatException conflict = Assert.Throws<XelibFormatException>(() =>
            {
                _ = XelibReferenceLoader.LoadFiles([firstPath, conflictPath]);
            });
            Assert.Equal(XelibErrorCode.DuplicateLibraryIdentity, conflict.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] RewriteSection(byte[] original, XelibSectionKind replacementKind,
        byte[] replacement)
    {
        XelibContainer container = XelibContainer.Read(original);
        XelibManifest manifest = XelibJson.Deserialize<XelibManifest>(
            container.GetRequiredSection(XelibSectionKind.Manifest).AsSpan(), null);
        var sections = container.Sections
            .Where(pair => pair.Key != (uint)XelibSectionKind.Manifest)
            .ToDictionary(pair => (XelibSectionKind)pair.Key, pair => pair.Value.ToArray());
        sections[replacementKind] = replacement;
        string identity = XelibWriter.ComputeContentIdentity(manifest.Name, manifest.Version,
            sections.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
        sections[XelibSectionKind.Manifest] = XelibJson.Serialize(manifest with
        {
            ContentIdentity = identity,
        });
        return XelibContainer.Write(sections.Select(pair => new XelibSection(pair.Key,
            XelibSectionFlags.Required, pair.Value)));
    }

}
