using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Libraries;

public sealed record XelibDependencyInput(
    CompilationReference Reference,
    XelibLibraryIdentity Identity);

public sealed record XelibWriteOptions(
    string Name,
    string? Version = null,
    ImmutableArray<XelibDependencyInput> Dependencies = default);

public static class XelibWriter
{
    public static byte[] Write(Compilation compilation, XelibWriteOptions options)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Name);
        if (compilation.HasErrors)
            throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
                "a library cannot be emitted while the compilation contains errors");
        if (compilation.TargetLayout is not null)
            throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
                "a portable XELIB must be emitted before target layout binding");

        ImmutableArray<XelibDependencyInput> inputs = options.Dependencies.IsDefault ? [] : options.Dependencies;
        ValidateDependencies(compilation, inputs);
        var builder = new XelibIrBuilder(compilation, inputs);
        XelibIrPayload payload = builder.Build();

        var sections = new SortedDictionary<XelibSectionKind, byte[]>
        {
            [XelibSectionKind.Strings] = XelibJson.Serialize(payload.Strings),
            [XelibSectionKind.Dependencies] = XelibJson.Serialize(payload.Dependencies),
            [XelibSectionKind.Types] = XelibJson.Serialize(payload.Types),
            [XelibSectionKind.Symbols] = XelibJson.Serialize(payload.Symbols),
            [XelibSectionKind.Exports] = XelibJson.Serialize(payload.Exports),
            [XelibSectionKind.Documentation] = XelibJson.Serialize(payload.Documentation),
            [XelibSectionKind.Bodies] = XelibJson.Serialize(payload.Bodies),
            [XelibSectionKind.GenericImplementations] = XelibJson.Serialize(payload.GenericImplementations),
        };
        string contentIdentity = ComputeContentIdentity(options.Name, options.Version,
            sections.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)pair.Value)),
            XelibVersions.LibraryIr, XelibVersions.Language);
        var manifest = new XelibManifest(options.Name, options.Version, contentIdentity,
            XelibVersions.Language, XelibVersions.LibraryIr);
        sections.Add(XelibSectionKind.Manifest, XelibJson.Serialize(manifest));

        return XelibContainer.Write(sections.Select(pair => new XelibSection(
            pair.Key, XelibSectionFlags.Required, pair.Value)));
    }

    public static XelibLibraryIdentity WriteFile(string path, Compilation compilation,
        XelibWriteOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = Write(compilation, options);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        XelibManifest manifest = XelibMetadataReader.Read(bytes, fullPath).Manifest;
        return new XelibLibraryIdentity(manifest.Name, manifest.Version, manifest.ContentIdentity);
    }

    internal static string ComputeContentIdentity(string name, string? version,
        IEnumerable<(XelibSectionKind Kind, ReadOnlyMemory<byte> Data)> sections,
        ushort libraryIrVersion = XelibVersions.LibraryIr,
        ushort languageVersion = XelibVersions.Language)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append("XELIB-CONTENT-IDENTITY-V2"u8);
        Span<byte> encodedVersions = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(encodedVersions, libraryIrVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(encodedVersions[2..], languageVersion);
        Append(encodedVersions);
        Append(Encoding.UTF8.GetBytes(name));
        Append(Encoding.UTF8.GetBytes(version ?? string.Empty));
        Span<byte> encodedKind = stackalloc byte[4];
        foreach ((XelibSectionKind kind, ReadOnlyMemory<byte> data) in sections.OrderBy(item => item.Kind))
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(encodedKind, (uint)kind);
            hash.AppendData(encodedKind);
            Append(data.Span);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();

        void Append(ReadOnlySpan<byte> data)
        {
            Span<byte> length = stackalloc byte[8];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(length, (ulong)data.Length);
            hash.AppendData(length);
            hash.AppendData(data);
        }
    }

    private static void ValidateDependencies(Compilation compilation,
        ImmutableArray<XelibDependencyInput> dependencies)
    {
        if (dependencies.Any(item => item.Reference is null || item.Identity is null))
            throw new ArgumentException("XELIB dependency inputs cannot contain null values.", nameof(dependencies));
        if (dependencies.Select(item => item.Identity.ContentIdentity).Distinct(StringComparer.Ordinal).Count() !=
            dependencies.Length)
            throw new XelibFormatException(XelibErrorCode.DuplicateLibraryIdentity,
                "duplicate XELIB dependency identity");
        foreach (XelibDependencyInput dependency in dependencies)
            if (!compilation.References.Contains(dependency.Reference))
                throw new ArgumentException("Every XELIB dependency must be an exact compilation reference.",
                    nameof(dependencies));
    }
}

public sealed record XelibMetadata(XelibManifest Manifest, ImmutableArray<XelibDependency> Dependencies);

public static class XelibMetadataReader
{
    public static XelibMetadata ReadFile(string path)
    {
        XelibContainer container = XelibContainer.ReadFile(path);
        return Read(container, Path.GetFullPath(path));
    }

    public static XelibMetadata Read(ReadOnlySpan<byte> bytes, string? path = null) =>
        Read(XelibContainer.Read(bytes, path), path);

    internal static XelibMetadata Read(XelibContainer container, string? path = null)
    {
        XelibManifest manifest = XelibJson.Deserialize<XelibManifest>(
            container.GetRequiredSection(XelibSectionKind.Manifest).AsSpan(), path);
        ImmutableArray<XelibDependency> dependencies = XelibJson.Deserialize<ImmutableArray<XelibDependency>>(
            container.GetRequiredSection(XelibSectionKind.Dependencies).AsSpan(), path);
        if (string.IsNullOrWhiteSpace(manifest.Name) ||
            manifest.ContentIdentity is not { Length: 64 } ||
            !manifest.ContentIdentity.All(Uri.IsHexDigit))
            throw new XelibFormatException(XelibErrorCode.InvalidRecord, "invalid manifest identity", path);
        if (manifest.LanguageVersion != container.Header.LanguageVersion ||
            manifest.LibraryIrVersion != container.Header.LibraryIrVersion)
            throw new XelibFormatException(XelibErrorCode.InvalidRecord,
                "manifest versions do not match the XELIB header", path);
        if (dependencies.Any(item => item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) ||
                item.ContentIdentity is not { Length: 64 } ||
                !item.ContentIdentity.All(Uri.IsHexDigit)) ||
            dependencies.Select(item => item.Id).Distinct().Count() != dependencies.Length ||
            dependencies.Select(item => item.ContentIdentity).Distinct(StringComparer.Ordinal).Count() != dependencies.Length)
            throw new XelibFormatException(XelibErrorCode.InvalidRecord,
                "dependency IDs and content identities must be unique", path);
        string actualIdentity = XelibWriter.ComputeContentIdentity(manifest.Name, manifest.Version,
            container.Sections.Where(pair => pair.Key != (uint)XelibSectionKind.Manifest)
                .Select(pair => ((XelibSectionKind)pair.Key,
                    (ReadOnlyMemory<byte>)pair.Value.ToArray())),
            manifest.LibraryIrVersion, manifest.LanguageVersion);
        if (!string.Equals(actualIdentity, manifest.ContentIdentity, StringComparison.Ordinal))
            throw new XelibFormatException(XelibErrorCode.ContentIdentityMismatch,
                "content digest does not match the manifest identity", path);
        return new XelibMetadata(manifest, dependencies);
    }
}

internal static class XelibJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };
    private static readonly XelibJsonSerializerContext Context = new(Options);

    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, GetTypeInfo<T>());

    public static T Deserialize<T>(ReadOnlySpan<byte> bytes, string? path)
    {
        try
        {
            return JsonSerializer.Deserialize(bytes, GetTypeInfo<T>()) ??
                throw new XelibFormatException(XelibErrorCode.InvalidRecord, "section contains null", path);
        }
        catch (XelibFormatException) { throw; }
        catch (JsonException exception)
        {
            throw new XelibFormatException(XelibErrorCode.InvalidRecord,
                $"invalid Library IR record: {exception.Message}", path, exception);
        }
    }

    private static JsonTypeInfo<T> GetTypeInfo<T>()
        => Context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ??
           throw new InvalidOperationException($"XELIB JSON type '{typeof(T)}' is not registered.");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    NumberHandling = JsonNumberHandling.Strict,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<XelibDependency>))]
[JsonSerializable(typeof(ImmutableArray<XelibTypeRecord>))]
[JsonSerializable(typeof(ImmutableArray<XelibSymbolRecord>))]
[JsonSerializable(typeof(ImmutableArray<XelibExport>))]
[JsonSerializable(typeof(ImmutableArray<XelibDocumentation>))]
[JsonSerializable(typeof(ImmutableArray<XelibBodyRecord>))]
[JsonSerializable(typeof(ImmutableArray<XelibGenericImplementation>))]
[JsonSerializable(typeof(XelibBodyRecord))]
[JsonSerializable(typeof(XelibManifest))]
internal sealed partial class XelibJsonSerializerContext : JsonSerializerContext;

internal sealed record XelibIrPayload(
    ImmutableArray<string> Strings,
    ImmutableArray<XelibDependency> Dependencies,
    ImmutableArray<XelibTypeRecord> Types,
    ImmutableArray<XelibSymbolRecord> Symbols,
    ImmutableArray<XelibExport> Exports,
    ImmutableArray<XelibDocumentation> Documentation,
    ImmutableArray<XelibBodyRecord> Bodies,
    ImmutableArray<XelibGenericImplementation> GenericImplementations);

internal static class XelibExportKey
{
    public static string Create(Symbol symbol) => symbol switch
    {
        NamespaceSymbol value => $"N:{Tag(XelibSymbolKind.Namespace)}:{value.FullName}",
        StructTypeSymbol { GenericDefinition: not null } value => TypeKey(value),
        StructTypeSymbol value => $"T:{Tag(XelibSymbolKind.Struct)}:{value.FullName}" +
            (RequiresAritySuffix(value) ? $":{value.GenericArity}" : string.Empty),
        InterfaceTypeSymbol value => $"T:{Tag(XelibSymbolKind.Interface)}:{value.FullName}",
        EnumTypeSymbol value => $"T:{Tag(XelibSymbolKind.Enum)}:{value.FullName}",
        TemplateSymbol value => $"T:{Tag(XelibSymbolKind.Template)}:{value.QualifiedName}",
        FunctionSymbol value => $"F:{Owner(value)}:{value.Name}:{value.TypeParameters.Length}:" +
            $"({string.Join(',', value.Parameters.Select(parameter => TypeKey(parameter.Type)))})" +
            $"->{TypeKey(value.ReturnType)}:{(ushort)XelibStableMappings.ToXelib(value.FunctionKind)}:" +
            $"{(byte)XelibStableMappings.ToXelib(value.AccessorKind)}:{value.IsStatic}:{value.IsReadonly}",
        FieldSymbol value => $"D:{Owner(value)}:{value.Name}:{TypeKey(value.Type)}",
        PropertySymbol value => $"P:{Owner(value)}:{value.Name}:{TypeKey(value.Type)}",
        InterfacePropertySymbol value => $"P:{Owner(value)}:{value.Name}:{TypeKey(value.Type)}",
        IndexerSymbol value => $"I:{Owner(value)}:({string.Join(',', value.Parameters.Select(p => TypeKey(p.Type)))})" +
            $"->{TypeKey(value.Type)}",
        InterfaceIndexerSymbol value => $"I:{Owner(value)}:({string.Join(',', value.Parameters.Select(p => TypeKey(p.Type)))})" +
            $"->{TypeKey(value.Type)}",
        ConstantSymbol value => $"C:{Owner(value)}:{value.Name}:{TypeKey(value.Type)}",
        GenericParameterSymbol value => $"G:{Owner(value)}:{value.Ordinal}",
        ParameterSymbol value => $"A:{Create(value.ContainingSymbol!)}:{value.Ordinal}:{value.Name}:{TypeKey(value.Type)}",
        TemplateMethodRequirementSymbol value => $"R:{Tag(XelibSymbolKind.TemplateMethod)}:{Owner(value)}:" +
            $"{value.Name}:({string.Join(',', value.Parameters.Select(parameter => TypeKey(parameter.Type)))})" +
            $"->{TypeKey(value.ReturnType)}:{value.IsStatic}:{value.IsReadonly}",
        TemplateConstructorRequirementSymbol value => $"R:{Tag(XelibSymbolKind.TemplateConstructor)}:{Owner(value)}:" +
            $"({string.Join(',', value.Parameters.Select(parameter => TypeKey(parameter.Type)))})",
        TemplatePropertyRequirementSymbol value => $"R:{Tag(XelibSymbolKind.TemplateProperty)}:{Owner(value)}:" +
            $"{value.Name}:{TypeKey(value.Type)}:{value.HasGetter}:{value.HasSetter}",
        TemplateIndexerRequirementSymbol value => $"R:{Tag(XelibSymbolKind.TemplateIndexer)}:{Owner(value)}:" +
            $"({string.Join(',', value.Parameters.Select(parameter => TypeKey(parameter.Type)))})" +
            $"->{TypeKey(value.Type)}:{value.HasGetter}:{value.HasSetter}",
        _ => throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
            $"symbol '{symbol.QualifiedName}' has no stable XELIB export key"),
    };

    private static string Tag(XelibSymbolKind kind) => ((ushort)kind).ToString(CultureInfo.InvariantCulture);

    // Preserve the v1 key for the overwhelmingly common unambiguous declaration.
    // All declarations participate because these keys also order non-exported symbols and types.
    private static bool RequiresAritySuffix(StructTypeSymbol type) =>
        type.ContainingNamespace.Structs.Count(candidate =>
            !candidate.IsGenericSpecialization && candidate.Name == type.Name) > 1;

    public static string TypeKey(TypeSymbol type) => type switch
    {
        StructTypeSymbol { GenericDefinition: not null } value =>
            $"constructed({Create(value.GenericDefinition)};{string.Join(',', value.TypeArguments.Select(TypeKey))})",
        DeclaredTypeSymbol value => Create(value),
        GenericParameterSymbol value => Create(value),
        TemplateSelfTypeSymbol value => $"self({Create(value.Template)})",
        PointerTypeSymbol value => $"ptr:{value.IsReadonly}:{TypeKey(value.ElementType)}",
        ReferenceTypeSymbol value => $"ref:{value.IsReadonly}:{TypeKey(value.ElementType)}",
        FunctionPointerTypeSymbol value => $"fn:{TypeKey(value.ReturnType)}:" +
            string.Join(',', value.ParameterTypes.Select(TypeKey)),
        FunctionValueTypeSymbol value => $"function:{TypeKey(value.ReturnType)}:" +
            string.Join(',', value.ParameterTypes.Select(TypeKey)),
        ArrayTypeSymbol value => $"array:{value.Rank}:{TypeKey(value.ElementType)}",
        AtomicTypeSymbol value => $"atomic:{TypeKey(value.ElementType)}",
        UniqueTypeSymbol value => $"unique:{TypeKey(value.ElementType)}",
        SharedTypeSymbol value => $"shared:{TypeKey(value.ElementType)}",
        WeakTypeSymbol value => $"weak:{TypeKey(value.ElementType)}",
        StorageTypeSymbol value => $"storage:{TypeKey(value.ElementType)}",
        PinTypeSymbol value => $"pin:{TypeKey(value.ElementType)}",
        PrimitiveTypeSymbol value => $"primitive:{value.Name}",
        _ => throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
            $"type '{type}' cannot be represented by Library IR version {XelibVersions.LibraryIr}"),
    };

    private static string Owner(Symbol symbol) => symbol.ContainingSymbol is null ? string.Empty :
        symbol.ContainingSymbol is NamespaceSymbol or DeclaredTypeSymbol or TemplateSymbol
            ? Create(symbol.ContainingSymbol)
            : Owner(symbol.ContainingSymbol) + "/" + symbol.ContainingSymbol.Name;
}

internal sealed class XelibIrBuilder
{
    private readonly Compilation _compilation;
    private readonly ImmutableArray<XelibDependencyInput> _dependencyInputs;
    private readonly Dictionary<NamespaceSymbol, int> _dependencyByRoot = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<Symbol> _symbols = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TypeSymbol> _types = new(TypeIdentity.Comparer);
    private Dictionary<Symbol, int> _symbolIds = null!;
    private Dictionary<TypeSymbol, int> _typeIds = null!;

    public XelibIrBuilder(Compilation compilation, ImmutableArray<XelibDependencyInput> dependencyInputs)
    {
        _compilation = compilation;
        _dependencyInputs = dependencyInputs;
        for (int index = 0; index < dependencyInputs.Length; index++)
            _dependencyByRoot.Add(dependencyInputs[index].Reference.GlobalNamespace, index + 1);
    }

    public XelibIrPayload Build()
    {
        VisitNamespace(_compilation.SemanticModel.GlobalNamespace);
        var genericFunctions = _compilation.GenericImplementations.Functions
            .Where(entry => IsOwned(entry.Key) && entry.Value.PortableBody is not null)
            .OrderBy(entry => XelibExportKey.Create(entry.Key), StringComparer.Ordinal).ToArray();
        var genericStructs = _compilation.GenericImplementations.Structs
            .Where(entry => IsOwned(entry.Key))
            .OrderBy(entry => XelibExportKey.Create(entry.Key), StringComparer.Ordinal).ToArray();
        BoundFunction[] ownedFunctions = _compilation.SemanticModel.Functions
            .Where(function => _compilation.IsSymbolDefinedHere(function.Symbol))
            .OrderBy(function => XelibExportKey.Create(function.Symbol), StringComparer.Ordinal).ToArray();
        foreach (BoundFunction function in ownedFunctions)
        {
            VisitFunction(function.Symbol);
            XelibBodyCodec.Collect(function.Body, AddType, VisitExternalOrLocal);
        }
        foreach (var entry in genericFunctions)
        {
            VisitFunction(entry.Key);
            XelibBodyCodec.Collect(entry.Value.PortableBody!, AddType, VisitExternalOrLocal);
        }
        foreach (var entry in genericStructs)
        {
            if (entry.Value.PortableInstanceInitializer is { } initializer)
            {
                VisitFunction(initializer.Symbol);
                XelibBodyCodec.Collect(initializer.Body, AddType, VisitExternalOrLocal);
            }
            foreach (BoundFunction staticInitializer in entry.Value.PortableStaticFieldInitializers)
            {
                VisitFunction(staticInitializer.Symbol);
                XelibBodyCodec.Collect(staticInitializer.Body, AddType, VisitExternalOrLocal);
            }
            foreach (ConstantSymbol constant in entry.Key.Constants.Where(constant =>
                         constant.EvaluationState == ConstantEvaluationState.Deferred && constant.BoundValue is not null))
            {
                VisitSymbol(constant);
                XelibBodyCodec.Collect(constant.BoundValue!, AddType, VisitExternalOrLocal);
            }
        }
        foreach (ConstantSymbol constant in _symbols.OfType<ConstantSymbol>()
                     .Where(constant => constant.EvaluationState == ConstantEvaluationState.Deferred)
                     .ToArray())
        {
            if (constant.BoundValue is null)
                throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
                    $"deferred constant '{constant.QualifiedName}' has no semantic expression");
            XelibBodyCodec.Collect(constant.BoundValue, AddType, VisitExternalOrLocal);
        }

        Symbol[] orderedSymbols = _symbols.OrderBy(XelibExportKey.Create, StringComparer.Ordinal).ToArray();
        _symbolIds = new Dictionary<Symbol, int>(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < orderedSymbols.Length; index++)
            _symbolIds.Add(orderedSymbols[index], index + 1);
        foreach (Symbol symbol in orderedSymbols) CollectSymbolTypes(symbol);
        TypeSymbol[] orderedTypes = _types.OrderBy(XelibExportKey.TypeKey, StringComparer.Ordinal).ToArray();
        _typeIds = orderedTypes.Select((type, index) => (type, id: index + 1))
            .ToDictionary(pair => pair.type, pair => pair.id, TypeIdentity.Comparer);

        ImmutableArray<XelibSymbolRecord> symbols = orderedSymbols.Select(CreateSymbolRecord).ToImmutableArray();
        ImmutableArray<XelibTypeRecord> types = orderedTypes.Select(CreateTypeRecord).ToImmutableArray();
        ImmutableArray<XelibExport> exports = orderedSymbols.Where(IsExported)
            .Select(symbol => new XelibExport(XelibExportKey.Create(symbol), Id(symbol)))
            .OrderBy(item => item.Key, StringComparer.Ordinal).ToImmutableArray();
        ImmutableArray<XelibDocumentation> documentation = orderedSymbols
            .Where(symbol => IsExported(symbol) && !symbol.Documentation.IsEmpty)
            .Select(CreateDocumentation).ToImmutableArray();
        ImmutableArray<string> strings = CollectStrings(symbols, exports, documentation);
        ImmutableArray<XelibDependency> dependencies = _dependencyInputs.Select((input, index) =>
            new XelibDependency(index + 1, input.Identity.Name, input.Identity.Version,
                input.Identity.ContentIdentity)).ToImmutableArray();

        var bodyBuilder = ImmutableArray.CreateBuilder<XelibBodyRecord>(
            ownedFunctions.Length + genericFunctions.Length);
        foreach (BoundFunction function in ownedFunctions)
            bodyBuilder.Add(new XelibBodyRecord(bodyBuilder.Count + 1, Id(function.Symbol),
                XelibBodyCodec.Encode(function.Body, function.Symbol, TypeId, Reference)));
        var genericBuilder = ImmutableArray.CreateBuilder<XelibGenericImplementation>(genericFunctions.Length);
        foreach (var entry in genericFunctions)
        {
            int bodyId = bodyBuilder.Count + 1;
            bodyBuilder.Add(new XelibBodyRecord(bodyId, Id(entry.Key), XelibBodyCodec.Encode(
                entry.Value.PortableBody!, entry.Key, TypeId, Reference)));
            genericBuilder.Add(new XelibGenericImplementation(Id(entry.Key), bodyId, IsStruct: false));
        }
        foreach (var entry in genericStructs)
        {
            int bodyId = 0;
            if (entry.Value.PortableInstanceInitializer is { } initializer)
            {
                bodyId = bodyBuilder.Count + 1;
                bodyBuilder.Add(new XelibBodyRecord(bodyId, Id(initializer.Symbol), XelibBodyCodec.Encode(
                    initializer.Body, initializer.Symbol, TypeId, Reference)));
            }
            var staticInitializers = ImmutableArray.CreateBuilder<XelibGenericFieldInitializer>();
            foreach (BoundFunction staticInitializer in entry.Value.PortableStaticFieldInitializers)
            {
                int initializerBodyId = bodyBuilder.Count + 1;
                bodyBuilder.Add(new XelibBodyRecord(initializerBodyId, Id(staticInitializer.Symbol),
                    XelibBodyCodec.Encode(staticInitializer.Body, staticInitializer.Symbol, TypeId, Reference)));
                FieldSymbol field = staticInitializer.Symbol.ThreadLocalField ??
                    throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
                        "portable generic static initializer is not associated with a field");
                staticInitializers.Add(new XelibGenericFieldInitializer(Id(field), initializerBodyId));
            }
            // Deferred constants use the same symbol-level representation for generic and
            // ordinary owners. The per-generic list remains reader-compatible only.
            ImmutableArray<XelibGenericConstantImplementation> constants = [];
            genericBuilder.Add(new XelibGenericImplementation(Id(entry.Key), bodyId, IsStruct: true,
                staticInitializers.ToImmutable(), constants));
        }
        ImmutableArray<XelibBodyRecord> bodies = bodyBuilder.ToImmutable();
        return new XelibIrPayload(strings, dependencies, types, symbols, exports, documentation,
            bodies, genericBuilder.ToImmutable());
    }

    private void VisitNamespace(NamespaceSymbol @namespace)
    {
        foreach (NamespaceSymbol child in @namespace.Namespaces.OrderBy(value => value.Name, StringComparer.Ordinal))
            VisitNamespace(child);
        foreach (DeclaredTypeSymbol type in @namespace.Types.Where(IsOwned)) VisitSymbol(type);
        foreach (FunctionSymbol function in @namespace.Functions.Where(IsOwned)) VisitFunction(function);
        foreach (ConstantSymbol constant in @namespace.Constants.Where(IsOwned)) VisitSymbol(constant);
        foreach (TemplateSymbol template in @namespace.Templates.Where(IsOwned)) VisitSymbol(template);
    }

    private void VisitSymbol(Symbol symbol)
    {
        if (!_symbols.Add(symbol)) return;
        if (symbol.ContainingSymbol is NamespaceSymbol { Parent: not null } parent) VisitSymbol(parent);
        else if (symbol.ContainingSymbol is { } owner && owner is not NamespaceSymbol) VisitSymbol(owner);
        switch (symbol)
        {
            case StructTypeSymbol type:
                foreach (GenericParameterSymbol item in type.TypeParameters) VisitSymbol(item);
                foreach (FieldSymbol item in type.Fields) VisitSymbol(item);
                foreach (FieldSymbol item in type.StaticFields) VisitSymbol(item);
                foreach (PropertySymbol item in type.Properties) VisitSymbol(item);
                foreach (IndexerSymbol item in type.Indexers) VisitSymbol(item);
                foreach (ConstantSymbol item in type.Constants) VisitSymbol(item);
                foreach (FunctionSymbol item in type.Methods) VisitFunction(item);
                foreach (FunctionSymbol item in type.Constructors) VisitFunction(item);
                if (type.InstanceInitializer is { } initializer) VisitFunction(initializer);
                if (type.Destructor is { } destructor) VisitFunction(destructor);
                break;
            case InterfaceTypeSymbol type:
                foreach (FunctionSymbol item in type.Methods) VisitFunction(item);
                foreach (InterfacePropertySymbol item in type.Properties) VisitSymbol(item);
                foreach (InterfaceIndexerSymbol item in type.Indexers) VisitSymbol(item);
                break;
            case EnumTypeSymbol type:
                foreach (ConstantSymbol item in type.Members) VisitSymbol(item);
                foreach (FieldSymbol item in type.StaticFields) VisitSymbol(item);
                break;
            case TemplateSymbol template:
                foreach (TemplateMemberRequirementSymbol item in template.Members) VisitSymbol(item);
                break;
            case FunctionSymbol function:
                foreach (ParameterSymbol item in function.Parameters) VisitSymbol(item);
                foreach (GenericParameterSymbol item in function.TypeParameters) VisitSymbol(item);
                break;
            case PropertySymbol property:
                if (property.Getter is { } propertyGetter) VisitFunction(propertyGetter);
                if (property.Setter is { } propertySetter) VisitFunction(propertySetter);
                break;
            case IndexerSymbol indexer:
                foreach (ParameterSymbol item in indexer.Parameters) VisitSymbol(item);
                if (indexer.Getter is { } indexerGetter) VisitFunction(indexerGetter);
                if (indexer.Setter is { } indexerSetter) VisitFunction(indexerSetter);
                break;
            case InterfacePropertySymbol property:
                if (property.Getter is { } interfacePropertyGetter) VisitFunction(interfacePropertyGetter);
                if (property.Setter is { } interfacePropertySetter) VisitFunction(interfacePropertySetter);
                break;
            case InterfaceIndexerSymbol indexer:
                foreach (ParameterSymbol item in indexer.Parameters) VisitSymbol(item);
                if (indexer.Getter is { } interfaceIndexerGetter) VisitFunction(interfaceIndexerGetter);
                if (indexer.Setter is { } interfaceIndexerSetter) VisitFunction(interfaceIndexerSetter);
                break;
            case TemplateMethodRequirementSymbol requirement:
                foreach (ParameterSymbol item in requirement.Parameters) VisitSymbol(item);
                break;
            case TemplateConstructorRequirementSymbol requirement:
                foreach (ParameterSymbol item in requirement.Parameters) VisitSymbol(item);
                break;
            case TemplateIndexerRequirementSymbol requirement:
                foreach (ParameterSymbol item in requirement.Parameters) VisitSymbol(item);
                break;
        }
    }

    private void VisitFunction(FunctionSymbol function)
    {
        if (!_symbols.Add(function)) return;
        if (function.ContainingSymbol is NamespaceSymbol { Parent: not null } parent) VisitSymbol(parent);
        else if (function.ContainingSymbol is { } owner && owner is not NamespaceSymbol) VisitSymbol(owner);
        foreach (ParameterSymbol item in function.Parameters) VisitSymbol(item);
        foreach (GenericParameterSymbol item in function.TypeParameters) VisitSymbol(item);
    }

    private bool IsOwned(Symbol symbol)
    {
        Symbol root = symbol;
        while (root.ContainingSymbol is { } owner) root = owner;
        return ReferenceEquals(root, _compilation.SemanticModel.GlobalNamespace);
    }

    private void CollectSymbolTypes(Symbol symbol)
    {
        switch (symbol)
        {
            case TypeSymbol type: AddType(type); break;
            case FunctionSymbol function:
                AddType(function.ReturnType);
                foreach (ParameterSymbol parameter in function.Parameters) AddType(parameter.Type);
                foreach (CaptureVariableSymbol capture in function.LambdaCaptures)
                {
                    AddType(capture.Type);
                    AddType(capture.StorageType);
                }
                break;
            case FieldSymbol field: AddType(field.Type); break;
            case VariableSymbol variable: AddType(variable.Type); break;
            case ConstantSymbol constant: AddType(constant.Type); break;
            case PropertySymbol property: AddType(property.Type); break;
            case InterfacePropertySymbol property: AddType(property.Type); break;
            case IndexerSymbol indexer:
                AddType(indexer.Type);
                foreach (ParameterSymbol parameter in indexer.Parameters) AddType(parameter.Type);
                break;
            case InterfaceIndexerSymbol indexer:
                AddType(indexer.Type);
                foreach (ParameterSymbol parameter in indexer.Parameters) AddType(parameter.Type);
                break;
            case TemplateMethodRequirementSymbol requirement:
                AddType(requirement.ReturnType);
                foreach (ParameterSymbol parameter in requirement.Parameters) AddType(parameter.Type);
                break;
            case TemplatePropertyRequirementSymbol requirement: AddType(requirement.Type); break;
            case TemplateIndexerRequirementSymbol requirement:
                AddType(requirement.Type);
                foreach (ParameterSymbol parameter in requirement.Parameters) AddType(parameter.Type);
                break;
        }
        if (symbol is StructTypeSymbol structure)
        {
            if (structure.BaseType is { } baseType) AddType(baseType);
            foreach (InterfaceTypeSymbol item in structure.Interfaces) AddType(item);
        }
        if (symbol is InterfaceTypeSymbol @interface)
            foreach (InterfaceTypeSymbol item in @interface.BaseInterfaces) AddType(item);
        if (symbol is EnumTypeSymbol enumeration) AddType(enumeration.UnderlyingType);
        if (symbol is GenericParameterSymbol generic)
            foreach (GenericConstraintSymbol constraint in generic.Constraints)
                if (constraint.Target is TypeSymbol type) AddType(type);
    }

    private void AddType(TypeSymbol type)
    {
        if (type is ErrorTypeSymbol)
            throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
                "ErrorTypeSymbol cannot be emitted into a XELIB");
        if (!_types.Add(type)) return;
        switch (type)
        {
            case StructTypeSymbol { GenericDefinition: not null } structure:
                VisitExternalOrLocal(structure.GenericDefinition);
                foreach (TypeSymbol argument in structure.TypeArguments) AddType(argument);
                if (!structure.IsOpenGenericType) VisitExternalOrLocal(structure);
                break;
            case DeclaredTypeSymbol declared: VisitExternalOrLocal(declared); break;
            case GenericParameterSymbol parameter: VisitExternalOrLocal(parameter); break;
            case TemplateSelfTypeSymbol self: VisitExternalOrLocal(self.Template); break;
            case PointerTypeSymbol pointer: AddType(pointer.ElementType); break;
            case ReferenceTypeSymbol reference: AddType(reference.ElementType); break;
            case FunctionPointerTypeSymbol function:
                AddType(function.ReturnType);
                foreach (TypeSymbol parameter in function.ParameterTypes) AddType(parameter);
                break;
            case FunctionValueTypeSymbol function:
                AddType(function.ReturnType);
                foreach (TypeSymbol parameter in function.ParameterTypes) AddType(parameter);
                break;
            case ArrayTypeSymbol array: AddType(array.ElementType); break;
            case AtomicTypeSymbol atomic: AddType(atomic.ElementType); break;
            case OwnershipTypeSymbol ownership: AddType(ownership.ElementType); break;
            case LifetimeModifierTypeSymbol modifier: AddType(modifier.ElementType); break;
        }
    }

    private void VisitExternalOrLocal(Symbol symbol)
    {
        if (IsOwned(symbol)) VisitSymbol(symbol);
        else _ = Reference(symbol);
    }

    private XelibTypeRecord CreateTypeRecord(TypeSymbol type)
    {
        int id = TypeId(type);
        return type switch
        {
            StructTypeSymbol { GenericDefinition: not null } value => new(id, XelibTypeKind.ConstructedGeneric,
                Symbol: Reference(value.GenericDefinition),
                TypeArgumentIds: value.TypeArguments.Select(TypeId).ToImmutableArray(),
                ConstructedSymbol: !value.IsOpenGenericType || _symbolIds.ContainsKey(value) ? Reference(value) : null),
            DeclaredTypeSymbol value => new(id, XelibTypeKind.Declared, Symbol: Reference(value)),
            GenericParameterSymbol value => new(id, XelibTypeKind.GenericParameter, Symbol: Reference(value)),
            TemplateSelfTypeSymbol value => new(id, XelibTypeKind.TemplateSelf, Symbol: Reference(value.Template)),
            PrimitiveTypeSymbol { IsCharacter: true } => new(id, XelibTypeKind.Char),
            PrimitiveTypeSymbol value => new(id, XelibTypeKind.Primitive, PrimitiveName: value.Name),
            PointerTypeSymbol value => new(id, XelibTypeKind.Pointer, ElementTypeId: TypeId(value.ElementType),
                IsReadonly: value.IsReadonly),
            ReferenceTypeSymbol value => new(id, XelibTypeKind.Reference, ElementTypeId: TypeId(value.ElementType),
                IsReadonly: value.IsReadonly),
            FunctionPointerTypeSymbol value => new(id, XelibTypeKind.FunctionPointer,
                ReturnTypeId: TypeId(value.ReturnType),
                ParameterTypeIds: value.ParameterTypes.Select(TypeId).ToImmutableArray()),
            FunctionValueTypeSymbol value => new(id, XelibTypeKind.FunctionValue,
                ReturnTypeId: TypeId(value.ReturnType),
                ParameterTypeIds: value.ParameterTypes.Select(TypeId).ToImmutableArray()),
            ArrayTypeSymbol value => new(id, XelibTypeKind.Array, ElementTypeId: TypeId(value.ElementType),
                Rank: value.Rank),
            AtomicTypeSymbol value => new(id, XelibTypeKind.Atomic, ElementTypeId: TypeId(value.ElementType)),
            UniqueTypeSymbol value => new(id, XelibTypeKind.Unique, ElementTypeId: TypeId(value.ElementType)),
            SharedTypeSymbol value => new(id, XelibTypeKind.Shared, ElementTypeId: TypeId(value.ElementType)),
            WeakTypeSymbol value => new(id, XelibTypeKind.Weak, ElementTypeId: TypeId(value.ElementType)),
            StorageTypeSymbol value => new(id, XelibTypeKind.Storage, ElementTypeId: TypeId(value.ElementType)),
            PinTypeSymbol value => new(id, XelibTypeKind.Pin, ElementTypeId: TypeId(value.ElementType)),
            _ => throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
                $"type '{type}' cannot be emitted"),
        };
    }

    private XelibSymbolRecord CreateSymbolRecord(Symbol symbol)
    {
        var record = new XelibSymbolRecord
        {
            Id = Id(symbol),
            Kind = GetKind(symbol),
            Name = symbol.Name,
            ContainingSymbolId = symbol.ContainingSymbol is null ||
                ReferenceEquals(symbol.ContainingSymbol, _compilation.SemanticModel.GlobalNamespace)
                ? 0 : Id(symbol.ContainingSymbol),
            Order = GetOrder(symbol),
            Flags = GetFlags(symbol),
            Accessibility = GetAccessibility(symbol),
        };
        return symbol switch
        {
            StructTypeSymbol value => record with
            {
                TypeParameterIds = value.TypeParameters.Select(Id).ToImmutableArray(),
                BaseTypeId = value.BaseType is null ? 0 : TypeId(value.BaseType),
                InterfaceTypeIds = value.Interfaces.Select(TypeId).ToImmutableArray(),
                RelatedSymbolIds = value.VirtualMethods.Select(Id).ToImmutableArray(),
            },
            InterfaceTypeSymbol value => record with
            {
                InterfaceTypeIds = value.BaseInterfaces.Select(TypeId).ToImmutableArray(),
                RelatedSymbolIds = value.AllMethods.Select(Id).ToImmutableArray(),
            },
            EnumTypeSymbol value => record with
            {
                TypeId = TypeId(value.UnderlyingType),
                RelatedSymbolIds = value.Members.Select(Id).ToImmutableArray(),
            },
            FunctionSymbol value => record with
            {
                ReturnTypeId = TypeId(value.ReturnType),
                ParameterIds = value.Parameters.Select(Id).ToImmutableArray(),
                TypeParameterIds = value.TypeParameters.Select(Id).ToImmutableArray(),
                FunctionKind = Map(value.FunctionKind),
                AccessorKind = Map(value.AccessorKind),
                OperatorKind = XelibOperatorKinds.Encode(value.OperatorKind),
                RelatedSymbolIds = value.ThreadLocalField is null ? [] : [Id(value.ThreadLocalField)],
                VTableSlot = value.VTableSlot,
                ConstructorOverload = value.ConstructorOverload,
                ConstructorOverloadCount = value.ConstructorOverloadCount,
                ReceiverMoveEffects = value.ReceiverMoveEffects.Select(effect => effect.FieldOrdinals)
                    .ToImmutableArray(),
                ReferenceReturnOrigins = value.ReferenceReturnOrigins.Select(origin =>
                    new XelibReferenceReturnOriginRecord(XelibStableMappings.ToXelib(origin.Kind), origin.ParameterOrdinal,
                        origin.FieldOrdinals)).ToImmutableArray(),
                SharedReturnOrigins = value.SharedReturnOrigins.Select(origin =>
                    new XelibSharedReturnOriginRecord(XelibStableMappings.ToXelib(origin.Kind),
                        origin.ParameterOrdinal)).ToImmutableArray(),
                ReferenceFieldOrigins = value.ReferenceFieldOrigins.Select(origin =>
                    new XelibReferenceFieldOriginRecord(origin.FieldOrdinals,
                        new XelibReferenceReturnOriginRecord(XelibStableMappings.ToXelib(origin.Origin.Kind),
                            origin.Origin.ParameterOrdinal, origin.Origin.FieldOrdinals),
                        origin.IsReadonly)).ToImmutableArray(),
                Captures = value.LambdaCaptures.Select(capture => new XelibCaptureRecord(
                    capture.Name, TypeId(capture.Type), TypeId(capture.StorageType),
                    XelibStableMappings.ToXelib(capture.CaptureKind), capture.Ordinal)).ToImmutableArray(),
            },
            FieldSymbol value => record with
            {
                TypeId = TypeId(value.Type), Ordinal = value.Ordinal,
                ConstantValue = Constant(value.ConstantValue, value.Type),
            },
            PropertySymbol value => record with
            {
                TypeId = TypeId(value.Type), GetterId = OptionalId(value.Getter), SetterId = OptionalId(value.Setter),
            },
            InterfacePropertySymbol value => record with
            {
                TypeId = TypeId(value.Type), GetterId = OptionalId(value.Getter), SetterId = OptionalId(value.Setter),
            },
            IndexerSymbol value => record with
            {
                TypeId = TypeId(value.Type), ParameterIds = value.Parameters.Select(Id).ToImmutableArray(),
                GetterId = OptionalId(value.Getter), SetterId = OptionalId(value.Setter),
            },
            InterfaceIndexerSymbol value => record with
            {
                TypeId = TypeId(value.Type), ParameterIds = value.Parameters.Select(Id).ToImmutableArray(),
                GetterId = OptionalId(value.Getter), SetterId = OptionalId(value.Setter),
            },
            ConstantSymbol value => record with
            {
                TypeId = TypeId(value.Type), ConstantValue =
                    value.EvaluationState == ConstantEvaluationState.Evaluated ? Constant(value.Value, value.Type) : null,
                ConstantExpression = value.EvaluationState == ConstantEvaluationState.Deferred
                    ? XelibBodyCodec.EncodeExpression(value.BoundValue ?? throw new XelibFormatException(
                        XelibErrorCode.FeatureNotRepresentable,
                        $"deferred constant '{value.QualifiedName}' has no semantic expression"), TypeId, Reference)
                    : value.EvaluationState == ConstantEvaluationState.Unresolved
                        ? throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
                            $"unresolved constant '{value.QualifiedName}' cannot be emitted")
                        : null,
            },
            ParameterSymbol value => record with { TypeId = TypeId(value.Type), Ordinal = value.Ordinal },
            GenericParameterSymbol value => record with
            {
                Ordinal = value.Ordinal,
                Constraints = value.Constraints.Select(constraint => new XelibConstraintRecord(
                    XelibStableMappings.ToXelib(constraint.Kind), Reference(constraint.Target))).ToImmutableArray(),
            },
            TemplateMethodRequirementSymbol value => record with
            {
                ReturnTypeId = TypeId(value.ReturnType),
                ParameterIds = value.Parameters.Select(Id).ToImmutableArray(),
            },
            TemplateConstructorRequirementSymbol value => record with
            {
                ParameterIds = value.Parameters.Select(Id).ToImmutableArray(),
            },
            TemplatePropertyRequirementSymbol value => record with
            {
                TypeId = TypeId(value.Type), HasGetter = value.HasGetter, HasSetter = value.HasSetter,
            },
            TemplateIndexerRequirementSymbol value => record with
            {
                TypeId = TypeId(value.Type), ParameterIds = value.Parameters.Select(Id).ToImmutableArray(),
                HasGetter = value.HasGetter, HasSetter = value.HasSetter,
            },
            _ => record,
        };
    }

    private XelibDocumentation CreateDocumentation(Symbol symbol)
    {
        SymbolDocumentation value = symbol.Documentation;
        return new XelibDocumentation(Id(symbol), value.Summary, value.Remarks, value.Returns,
            value.Parameters.ToImmutableSortedDictionary(StringComparer.Ordinal),
            value.TypeParameters.ToImmutableSortedDictionary(StringComparer.Ordinal));
    }

    private static ImmutableArray<string> CollectStrings(
        ImmutableArray<XelibSymbolRecord> symbols,
        ImmutableArray<XelibExport> exports,
        ImmutableArray<XelibDocumentation> documentation) => symbols.Select(item => item.Name)
        .Concat(exports.Select(item => item.Key))
        .Concat(documentation.SelectMany(item => new[] { item.Summary, item.Remarks, item.Returns }
            .Concat(item.Parameters.Select(pair => pair.Key)).Concat(item.Parameters.Select(pair => pair.Value))
            .Concat(item.TypeParameters.Select(pair => pair.Key)).Concat(item.TypeParameters.Select(pair => pair.Value))))
        .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();

    private bool IsExported(Symbol symbol) => symbol switch
    {
        NamespaceSymbol => true,
        DeclaredTypeSymbol type => type.ContainingSymbol is NamespaceSymbol && type.IsPublic,
        TemplateSymbol template => template.ContainingSymbol is NamespaceSymbol && template.IsPublic,
        FunctionSymbol function => function.ContainingInterface is not null ||
            AccessibilityFacts.IsExternallyInheritable(function.Accessibility) && HasPublicApiOwner(function),
        FieldSymbol field => AccessibilityFacts.IsExternallyInheritable(field.Accessibility) && HasPublicApiOwner(field),
        PropertySymbol property => AccessibilityFacts.IsExternallyInheritable(property.Accessibility) && HasPublicApiOwner(property),
        InterfacePropertySymbol => true,
        IndexerSymbol indexer => AccessibilityFacts.IsExternallyInheritable(indexer.Accessibility) && HasPublicApiOwner(indexer),
        InterfaceIndexerSymbol => true,
        ConstantSymbol constant => constant.ContainingSymbol is EnumTypeSymbol { IsPublic: true } ||
            (constant.ContainingSymbol is NamespaceSymbol && constant.IsPublic) ||
            AccessibilityFacts.IsExternallyInheritable(constant.Accessibility) && HasPublicApiOwner(constant),
        TemplateMemberRequirementSymbol requirement =>
            requirement.Template.IsPublic && AccessibilityFacts.IsExternallyInheritable(requirement.Accessibility),
        _ => false,
    };

    private static bool HasPublicApiOwner(Symbol symbol) => symbol.ContainingSymbol switch
    {
        NamespaceSymbol => true,
        DeclaredTypeSymbol type => type.IsPublic,
        PropertySymbol property => property.ContainingType.IsPublic,
        IndexerSymbol indexer => indexer.ContainingType.IsPublic,
        TemplateSymbol template => template.IsPublic,
        _ => false,
    };

    private static XelibAccessibility GetAccessibility(Symbol symbol) =>
        XelibStableMappings.ToXelib(symbol switch
        {
            DeclaredTypeSymbol value => value.Accessibility,
            TemplateSymbol value => value.Accessibility,
            FunctionSymbol value => value.Accessibility,
            FieldSymbol value => value.Accessibility,
            PropertySymbol value => value.Accessibility,
            IndexerSymbol value => value.Accessibility,
            TemplateMemberRequirementSymbol value => value.Accessibility,
            ConstantSymbol value => value.Accessibility,
            _ => Accessibility.Public,
        });

    private XelibSymbolReference Reference(Symbol symbol)
    {
        if (_symbolIds is not null && _symbolIds.TryGetValue(symbol, out int id))
            return XelibSymbolReference.Local(id);
        if (symbol.Origin is { Kind: SymbolOriginKind.Library, LibraryContentIdentity: not null,
            LibrarySymbolKey: not null } origin)
        {
            int byIdentity = -1;
            for (int index = 0; index < _dependencyInputs.Length; index++)
                if (_dependencyInputs[index].Identity.ContentIdentity == origin.LibraryContentIdentity)
                {
                    byIdentity = index;
                    break;
                }
            if (byIdentity >= 0)
                return XelibSymbolReference.External(byIdentity + 1, origin.LibrarySymbolKey);
        }
        Symbol root = symbol;
        while (root.ContainingSymbol is { } owner) root = owner;
        if (root is NamespaceSymbol @namespace && _dependencyByRoot.TryGetValue(@namespace, out int dependencyId))
            return XelibSymbolReference.External(dependencyId, XelibExportKey.Create(symbol));
        throw new XelibFormatException(XelibErrorCode.InvalidReference,
            $"symbol '{symbol.QualifiedName}' is not owned by this library or a declared dependency");
    }

    private int Id(Symbol symbol) => _symbolIds.TryGetValue(symbol, out int value) ? value :
        throw new XelibFormatException(XelibErrorCode.InvalidReference,
            $"symbol '{symbol.QualifiedName}' has no local ID");
    private int OptionalId(Symbol? symbol) => symbol is null ? 0 : Id(symbol);
    private int TypeId(TypeSymbol type) => _typeIds.TryGetValue(type, out int value) ? value :
        throw new XelibFormatException(XelibErrorCode.InvalidReference,
            $"type '{type}' has no type ID");

    private static int GetOrder(Symbol symbol) => symbol switch
    {
        ParameterSymbol value => value.Ordinal,
        GenericParameterSymbol value => value.Ordinal,
        FieldSymbol value => value.Ordinal,
        ConstantSymbol value when value.ContainingSymbol is EnumTypeSymbol enumeration =>
            enumeration.Members.IndexOf(value),
        _ => 0,
    };

    private static XelibSymbolKind GetKind(Symbol symbol) => symbol switch
    {
        NamespaceSymbol => XelibSymbolKind.Namespace,
        StructTypeSymbol => XelibSymbolKind.Struct,
        InterfaceTypeSymbol => XelibSymbolKind.Interface,
        EnumTypeSymbol => XelibSymbolKind.Enum,
        ConstantSymbol { ContainingSymbol: EnumTypeSymbol } => XelibSymbolKind.EnumMember,
        TemplateSymbol => XelibSymbolKind.Template,
        FunctionSymbol => XelibSymbolKind.Function,
        FieldSymbol => XelibSymbolKind.Field,
        PropertySymbol or InterfacePropertySymbol => XelibSymbolKind.Property,
        IndexerSymbol or InterfaceIndexerSymbol => XelibSymbolKind.Indexer,
        ConstantSymbol => XelibSymbolKind.Constant,
        ParameterSymbol => XelibSymbolKind.Parameter,
        GenericParameterSymbol => XelibSymbolKind.GenericParameter,
        TemplateMethodRequirementSymbol => XelibSymbolKind.TemplateMethod,
        TemplateConstructorRequirementSymbol => XelibSymbolKind.TemplateConstructor,
        TemplatePropertyRequirementSymbol => XelibSymbolKind.TemplateProperty,
        TemplateIndexerRequirementSymbol => XelibSymbolKind.TemplateIndexer,
        _ => throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
            $"symbol '{symbol.QualifiedName}' cannot be represented"),
    };

    private static XelibSymbolFlags GetFlags(Symbol symbol)
    {
        XelibSymbolFlags result = symbol.IsDefinition ? XelibSymbolFlags.Definition : 0;
        switch (symbol)
        {
            case FunctionSymbol value:
                if (value.IsPublic) result |= XelibSymbolFlags.Public;
                if (value.IsStatic) result |= XelibSymbolFlags.Static;
                if (value.IsReadonly) result |= XelibSymbolFlags.Readonly;
                if (value.IsVirtual) result |= XelibSymbolFlags.Virtual;
                if (value.IsOverride) result |= XelibSymbolFlags.Override;
                if (value.IsAbstract) result |= XelibSymbolFlags.Abstract;
                if (value.IsExtern) result |= XelibSymbolFlags.Extern;
                if (value.IsExport) result |= XelibSymbolFlags.Export;
                if (value.DelegatesToThisConstructor) result |= XelibSymbolFlags.DelegatesToThisConstructor;
                if (value.HasStackArrays) result |= XelibSymbolFlags.HasStackArrays;
                if (value.HasScalarCleanup) result |= XelibSymbolFlags.HasScalarCleanup;
                if (value.IsLambda) result |= XelibSymbolFlags.Lambda;
                if (value.IsCapturingLambda) result |= XelibSymbolFlags.CapturingLambda;
                break;
            case FieldSymbol value:
                if (value.IsPublic) result |= XelibSymbolFlags.Public;
                if (value.IsStatic) result |= XelibSymbolFlags.Static;
                if (value.IsReadonly) result |= XelibSymbolFlags.Readonly;
                if (value.IsThreadLocal) result |= XelibSymbolFlags.ThreadLocal;
                if (value.HasInitializer) result |= XelibSymbolFlags.HasInitializer;
                break;
            case PropertySymbol value:
                if (value.IsPublic) result |= XelibSymbolFlags.Public;
                if (value.IsStatic) result |= XelibSymbolFlags.Static;
                if (value.IsReadonly) result |= XelibSymbolFlags.Readonly;
                if (value.IsVirtual) result |= XelibSymbolFlags.Virtual;
                if (value.IsOverride) result |= XelibSymbolFlags.Override;
                if (value.IsAbstract) result |= XelibSymbolFlags.Abstract;
                break;
            case IndexerSymbol value:
                if (value.IsPublic) result |= XelibSymbolFlags.Public;
                if (value.IsStatic) result |= XelibSymbolFlags.Static;
                if (value.IsReadonly) result |= XelibSymbolFlags.Readonly;
                if (value.IsVirtual) result |= XelibSymbolFlags.Virtual;
                if (value.IsOverride) result |= XelibSymbolFlags.Override;
                if (value.IsAbstract) result |= XelibSymbolFlags.Abstract;
                break;
            case InterfacePropertySymbol value when value.IsReadonly:
                result |= XelibSymbolFlags.Public | XelibSymbolFlags.Readonly;
                break;
            case InterfacePropertySymbol:
                result |= XelibSymbolFlags.Public;
                break;
            case InterfaceIndexerSymbol value when value.IsReadonly:
                result |= XelibSymbolFlags.Public | XelibSymbolFlags.Readonly;
                break;
            case InterfaceIndexerSymbol:
                result |= XelibSymbolFlags.Public;
                break;
            case TemplateMemberRequirementSymbol value:
                if (value.IsPublic) result |= XelibSymbolFlags.Public;
                if (value.IsStatic) result |= XelibSymbolFlags.Static;
                if (value.IsReadonly) result |= XelibSymbolFlags.Readonly;
                break;
            case StructTypeSymbol value:
                if (value.IsAbstract) result |= XelibSymbolFlags.Abstract;
                if (value.IsReadonly) result |= XelibSymbolFlags.ReadonlyStruct;
                if (value.IsStatic) result |= XelibSymbolFlags.StaticStruct;
                if (value.IsSealed) result |= XelibSymbolFlags.Sealed;
                if (value.HasVirtualDispatch) result |= XelibSymbolFlags.HasVirtualDispatch;
                break;
            case ParameterSymbol value when value.IsReadonly:
                result |= XelibSymbolFlags.Readonly;
                break;
        }
        return result;
    }

    private static XelibFunctionKind Map(FunctionKind kind) => XelibStableMappings.ToXelib(kind);

    private static XelibAccessorKind Map(AccessorKind kind) => XelibStableMappings.ToXelib(kind);

    internal static XelibConstantValue Constant(object? value, TypeSymbol? type = null) => value switch
    {
        null => new(XelibConstantKind.Null, null),
        bool item => new(XelibConstantKind.Boolean, item ? "true" : "false"),
        ulong item when TypeFacts.IsCharacter(type ?? BuiltinTypes.Error) =>
            new(XelibConstantKind.UnicodeScalar, item.ToString(CultureInfo.InvariantCulture)),
        sbyte or short or int or long => new(XelibConstantKind.SignedInteger,
            Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
        byte or ushort or uint or ulong => new(XelibConstantKind.UnsignedInteger,
            Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)),
        float item => new(XelibConstantKind.FloatingPoint, item.ToString("R", CultureInfo.InvariantCulture)),
        double item => new(XelibConstantKind.FloatingPoint, item.ToString("R", CultureInfo.InvariantCulture)),
        string item => new(XelibConstantKind.String, item),
        _ => throw new XelibFormatException(XelibErrorCode.FeatureNotRepresentable,
            $"constant value type '{value.GetType().Name}' is not supported"),
    };
}
