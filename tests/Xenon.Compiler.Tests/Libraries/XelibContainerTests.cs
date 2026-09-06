using System.Buffers.Binary;
using System.Text;
using Xenon.Compiler;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Libraries;

public sealed class XelibContainerTests
{
    [Fact]
    public void HeaderAndSectionsRoundTrip()
    {
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
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(10), 2);
        Assert.Equal(XelibErrorCode.UnsupportedContainerVersion,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(container)).Code);

        container = ValidContainer();
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(12), 2);
        Assert.Equal(XelibErrorCode.UnsupportedLibraryIrVersion,
            Assert.Throws<XelibFormatException>(() => XelibContainer.Read(container)).Code);

        container = ValidContainer();
        BinaryPrimitives.WriteUInt16LittleEndian(container.AsSpan(14), 2);
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
}
