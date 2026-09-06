using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Xenon.CodeGen.LLVM;
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
