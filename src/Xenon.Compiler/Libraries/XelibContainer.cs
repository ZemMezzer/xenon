using System.Buffers.Binary;
using System.Collections.Immutable;

namespace Xenon.Compiler.Libraries;

public static class XelibVersions
{
    public const ushort Container = 1;
    public const ushort LibraryIr = 1;
    public const ushort Language = 1;
}

[Flags]
public enum XelibHeaderFlags : uint
{
    None = 0,
}

[Flags]
public enum XelibSectionFlags : uint
{
    None = 0,
    Required = 1,
    Compressed = 2,
}

public enum XelibSectionKind : uint
{
    Manifest = 1,
    Strings = 2,
    Dependencies = 3,
    Types = 4,
    Symbols = 5,
    Exports = 6,
    Documentation = 7,
    Bodies = 8,
    GenericImplementations = 9,
}

public enum XelibErrorCode
{
    TruncatedHeader,
    InvalidSignature,
    UnsupportedContainerVersion,
    UnsupportedLibraryIrVersion,
    UnsupportedLanguageVersion,
    InvalidHeader,
    InvalidSectionCount,
    CorruptSectionTable,
    DuplicateSection,
    MissingRequiredSection,
    UnknownRequiredSection,
    UnsupportedCompression,
    SectionOutOfBounds,
    OverlappingSections,
    SectionTooLarge,
    InvalidUtf8,
    InvalidRecord,
    InvalidReference,
    ContentIdentityMismatch,
    DependencyMissing,
    DependencyIdentityMismatch,
    DependencyCycle,
    DuplicateLibraryIdentity,
    FeatureNotRepresentable,
}

public sealed class XelibFormatException : Exception
{
    public XelibFormatException(XelibErrorCode code, string message, string? path = null,
        Exception? innerException = null)
        : base(path is null ? message : $"XELIB '{path}': {message}", innerException)
    {
        Code = code;
        Path = path;
    }

    public XelibErrorCode Code { get; }
    public string? Path { get; }
}

public readonly record struct XelibHeader(
    ushort HeaderSize,
    ushort ContainerVersion,
    ushort LibraryIrVersion,
    ushort LanguageVersion,
    XelibHeaderFlags Flags,
    uint SectionCount,
    ulong SectionTableOffset);

public readonly record struct XelibSectionDescriptor(
    uint Kind,
    XelibSectionFlags Flags,
    ulong Offset,
    ulong StoredLength,
    ulong UncompressedLength)
{
    public bool IsKnown => Enum.IsDefined(typeof(XelibSectionKind), Kind);
    public XelibSectionKind KnownKind => (XelibSectionKind)Kind;
}

public sealed record XelibSection(uint Kind, XelibSectionFlags Flags, ImmutableArray<byte> Data)
{
    public XelibSection(XelibSectionKind kind, XelibSectionFlags flags, ReadOnlySpan<byte> data)
        : this((uint)kind, flags, ImmutableArray.Create(data.ToArray())) { }
}

public sealed class XelibContainer
{
    private static readonly byte[] Signature = "XELIB\r\n\x1A"u8.ToArray();
    public const ushort FixedHeaderSize = 40;
    public const int SectionDescriptorSize = 32;
    public const int MaximumSectionCount = 64;
    public const int MaximumSectionSize = 128 * 1024 * 1024;
    public const int MaximumFileSize = 256 * 1024 * 1024;

    private XelibContainer(XelibHeader header, ImmutableArray<XelibSectionDescriptor> descriptors,
        ImmutableDictionary<uint, ImmutableArray<byte>> sections)
    {
        Header = header;
        SectionDescriptors = descriptors;
        Sections = sections;
    }

    public XelibHeader Header { get; }
    public ImmutableArray<XelibSectionDescriptor> SectionDescriptors { get; }
    public ImmutableDictionary<uint, ImmutableArray<byte>> Sections { get; }

    public bool TryGetSection(XelibSectionKind kind, out ImmutableArray<byte> data) =>
        Sections.TryGetValue((uint)kind, out data);

    public ImmutableArray<byte> GetRequiredSection(XelibSectionKind kind) =>
        TryGetSection(kind, out ImmutableArray<byte> data) ? data :
            throw new XelibFormatException(XelibErrorCode.MissingRequiredSection,
                $"required section '{kind}' is missing");

    public static byte[] Write(IEnumerable<XelibSection> sections,
        ushort containerVersion = XelibVersions.Container,
        ushort libraryIrVersion = XelibVersions.LibraryIr,
        ushort languageVersion = XelibVersions.Language,
        XelibHeaderFlags flags = XelibHeaderFlags.None)
    {
        ArgumentNullException.ThrowIfNull(sections);
        XelibSection[] ordered = sections.OrderBy(section => section.Kind).ToArray();
        ValidateSectionInputs(ordered);

        ulong tableOffset = FixedHeaderSize;
        ulong dataOffset = checked(tableOffset + (ulong)ordered.Length * SectionDescriptorSize);
        ulong fileLength = dataOffset;
        foreach (XelibSection section in ordered)
            fileLength = checked(fileLength + (ulong)section.Data.Length);
        if (fileLength > MaximumFileSize)
            throw new XelibFormatException(XelibErrorCode.SectionTooLarge,
                $"container size {fileLength} exceeds the {MaximumFileSize}-byte limit");

        byte[] result = GC.AllocateUninitializedArray<byte>(checked((int)fileLength));
        result.AsSpan().Clear();
        Signature.CopyTo(result, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(8), FixedHeaderSize);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(10), containerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(12), libraryIrVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), languageVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), (uint)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20), checked((uint)ordered.Length));
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(24), tableOffset);

        ulong nextOffset = dataOffset;
        for (int index = 0; index < ordered.Length; index++)
        {
            XelibSection section = ordered[index];
            int descriptorOffset = checked((int)tableOffset + index * SectionDescriptorSize);
            Span<byte> descriptor = result.AsSpan(descriptorOffset, SectionDescriptorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, section.Kind);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[4..], (uint)section.Flags);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor[8..], nextOffset);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor[16..], (ulong)section.Data.Length);
            BinaryPrimitives.WriteUInt64LittleEndian(descriptor[24..], (ulong)section.Data.Length);
            section.Data.AsSpan().CopyTo(result.AsSpan(checked((int)nextOffset)));
            nextOffset += (ulong)section.Data.Length;
        }
        return result;
    }

    public static XelibContainer Read(ReadOnlySpan<byte> bytes, string? path = null)
    {
        try
        {
            if (bytes.Length < FixedHeaderSize)
                throw Error(XelibErrorCode.TruncatedHeader,
                    $"header is truncated; expected at least {FixedHeaderSize} bytes", path);
            if (!bytes[..Signature.Length].SequenceEqual(Signature))
                throw Error(XelibErrorCode.InvalidSignature, "invalid XELIB signature", path);

            var header = new XelibHeader(
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]),
                (XelibHeaderFlags)BinaryPrimitives.ReadUInt32LittleEndian(bytes[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]),
                BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]));
            ValidateHeader(header, bytes.Length, path);

            ulong tableLength = checked((ulong)header.SectionCount * SectionDescriptorSize);
            ulong tableEnd = checked(header.SectionTableOffset + tableLength);
            if (tableEnd > (ulong)bytes.Length)
                throw Error(XelibErrorCode.CorruptSectionTable, "section table extends beyond the file", path);

            var descriptors = ImmutableArray.CreateBuilder<XelibSectionDescriptor>((int)header.SectionCount);
            var seen = new HashSet<uint>();
            for (int index = 0; index < header.SectionCount; index++)
            {
                int offset = checked((int)header.SectionTableOffset + index * SectionDescriptorSize);
                ReadOnlySpan<byte> encoded = bytes.Slice(offset, SectionDescriptorSize);
                var descriptor = new XelibSectionDescriptor(
                    BinaryPrimitives.ReadUInt32LittleEndian(encoded),
                    (XelibSectionFlags)BinaryPrimitives.ReadUInt32LittleEndian(encoded[4..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(encoded[8..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(encoded[16..]),
                    BinaryPrimitives.ReadUInt64LittleEndian(encoded[24..]));
                if (!seen.Add(descriptor.Kind))
                    throw Error(XelibErrorCode.DuplicateSection,
                        $"section kind {descriptor.Kind} appears more than once", path);
                ValidateDescriptor(descriptor, tableEnd, bytes.Length, path);
                descriptors.Add(descriptor);
            }

            XelibSectionDescriptor[] byOffset = descriptors.OrderBy(item => item.Offset).ToArray();
            for (int index = 1; index < byOffset.Length; index++)
            {
                ulong previousEnd = checked(byOffset[index - 1].Offset + byOffset[index - 1].StoredLength);
                if (previousEnd > byOffset[index].Offset)
                    throw Error(XelibErrorCode.OverlappingSections, "sections overlap", path);
            }

            var sectionData = ImmutableDictionary.CreateBuilder<uint, ImmutableArray<byte>>();
            foreach (XelibSectionDescriptor descriptor in descriptors)
            {
                if (!descriptor.IsKnown) continue;
                int offset = checked((int)descriptor.Offset);
                int length = checked((int)descriptor.StoredLength);
                sectionData.Add(descriptor.Kind, ImmutableArray.Create(bytes.Slice(offset, length).ToArray()));
            }
            return new XelibContainer(header, descriptors.ToImmutable(), sectionData.ToImmutable());
        }
        catch (OverflowException exception)
        {
            throw Error(XelibErrorCode.CorruptSectionTable,
                "integer overflow while validating offsets or sizes", path, exception);
        }
    }

    public static XelibContainer ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);
            if (stream.Length > MaximumFileSize)
                throw Error(XelibErrorCode.SectionTooLarge,
                    $"file size {stream.Length} exceeds the {MaximumFileSize}-byte limit", fullPath);
            byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            stream.ReadExactly(bytes);
            return Read(bytes, fullPath);
        }
        catch (XelibFormatException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new XelibFormatException(XelibErrorCode.InvalidHeader,
                $"cannot read XELIB: {exception.Message}", fullPath, exception);
        }
    }

    private static void ValidateSectionInputs(XelibSection[] sections)
    {
        if (sections.Length is 0 or > MaximumSectionCount)
            throw new XelibFormatException(XelibErrorCode.InvalidSectionCount,
                $"section count must be between 1 and {MaximumSectionCount}");
        if (sections.Select(section => section.Kind).Distinct().Count() != sections.Length)
            throw new XelibFormatException(XelibErrorCode.DuplicateSection,
                "section kinds must be unique");
        foreach (XelibSection section in sections)
        {
            if (section.Data.IsDefault)
                throw new ArgumentException("Section data cannot be default.", nameof(sections));
            if (section.Data.Length > MaximumSectionSize)
                throw new XelibFormatException(XelibErrorCode.SectionTooLarge,
                    $"section kind {section.Kind} exceeds the {MaximumSectionSize}-byte limit");
            if ((section.Flags & XelibSectionFlags.Compressed) != 0)
                throw new XelibFormatException(XelibErrorCode.UnsupportedCompression,
                    "compressed sections are not supported by container version 1");
        }
    }

    private static void ValidateHeader(XelibHeader header, int fileLength, string? path)
    {
        if (header.HeaderSize != FixedHeaderSize || header.SectionTableOffset < header.HeaderSize)
            throw Error(XelibErrorCode.InvalidHeader, "invalid header size or section-table offset", path);
        if (header.ContainerVersion != XelibVersions.Container)
            throw Error(XelibErrorCode.UnsupportedContainerVersion,
                $"unsupported container version {header.ContainerVersion}; expected {XelibVersions.Container}", path);
        if (header.LibraryIrVersion != XelibVersions.LibraryIr)
            throw Error(XelibErrorCode.UnsupportedLibraryIrVersion,
                $"unsupported Library IR version {header.LibraryIrVersion}; expected {XelibVersions.LibraryIr}", path);
        if (header.LanguageVersion != XelibVersions.Language)
            throw Error(XelibErrorCode.UnsupportedLanguageVersion,
                $"unsupported language version {header.LanguageVersion}; expected {XelibVersions.Language}", path);
        if (header.SectionCount is 0 or > MaximumSectionCount)
            throw Error(XelibErrorCode.InvalidSectionCount,
                $"section count {header.SectionCount} is outside the supported range", path);
        if (header.SectionTableOffset > (ulong)fileLength)
            throw Error(XelibErrorCode.CorruptSectionTable, "section table starts beyond the file", path);
    }

    private static void ValidateDescriptor(XelibSectionDescriptor descriptor, ulong tableEnd,
        int fileLength, string? path)
    {
        if (!descriptor.IsKnown && (descriptor.Flags & XelibSectionFlags.Required) != 0)
            throw Error(XelibErrorCode.UnknownRequiredSection,
                $"unknown required section kind {descriptor.Kind}", path);
        if ((descriptor.Flags & XelibSectionFlags.Compressed) != 0 ||
            descriptor.StoredLength != descriptor.UncompressedLength)
            throw Error(XelibErrorCode.UnsupportedCompression,
                $"section kind {descriptor.Kind} uses unsupported compression", path);
        if (descriptor.StoredLength > MaximumSectionSize)
            throw Error(XelibErrorCode.SectionTooLarge,
                $"section kind {descriptor.Kind} exceeds the {MaximumSectionSize}-byte limit", path);
        ulong end = checked(descriptor.Offset + descriptor.StoredLength);
        if (descriptor.Offset < tableEnd || end > (ulong)fileLength)
            throw Error(XelibErrorCode.SectionOutOfBounds,
                $"section kind {descriptor.Kind} is outside file bounds", path);
    }

    private static XelibFormatException Error(XelibErrorCode code, string message,
        string? path, Exception? inner = null) => new(code, message, path, inner);
}
