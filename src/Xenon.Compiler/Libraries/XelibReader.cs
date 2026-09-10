using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Libraries;

public static class XelibReader
{
    public static LibraryCompilationReference ReadFile(string path,
        IEnumerable<LibraryCompilationReference>? availableDependencies = null,
        bool metadataOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        return Read(XelibContainer.ReadFile(fullPath), fullPath, availableDependencies, metadataOnly);
    }

    public static LibraryCompilationReference Read(ReadOnlySpan<byte> bytes,
        IEnumerable<LibraryCompilationReference>? availableDependencies = null,
        bool metadataOnly = false, string? path = null) =>
        Read(XelibContainer.Read(bytes, path), path, availableDependencies, metadataOnly);

    private static LibraryCompilationReference Read(XelibContainer container, string? path,
        IEnumerable<LibraryCompilationReference>? availableDependencies, bool metadataOnly)
    {
        XelibMetadata metadata = XelibMetadataReader.Read(container, path);
        ImmutableArray<string> strings = ReadSection<ImmutableArray<string>>(
            container, XelibSectionKind.Strings, path);
        if (strings.Any(string.IsNullOrEmpty) ||
            strings.Distinct(StringComparer.Ordinal).Count() != strings.Length)
            throw new XelibFormatException(XelibErrorCode.InvalidRecord,
                "string table contains an empty or duplicate value", path);
        LibraryCompilationReference[] available = availableDependencies?.ToArray() ?? [];
        var dependencies = new Dictionary<int, LibraryCompilationReference>();
        foreach (XelibDependency dependency in metadata.Dependencies)
        {
            LibraryCompilationReference[] matches = available.Where(item =>
                item.LibraryIdentity.ContentIdentity == dependency.ContentIdentity).ToArray();
            if (matches.Length == 0)
                throw new XelibFormatException(XelibErrorCode.DependencyMissing,
                    $"required dependency '{dependency.Name}' ({dependency.ContentIdentity}) is unavailable", path);
            if (matches.Length != 1 || matches[0].LibraryIdentity.Name != dependency.Name ||
                matches[0].LibraryIdentity.Version != dependency.Version)
                throw new XelibFormatException(XelibErrorCode.DependencyIdentityMismatch,
                    $"dependency identity mismatch for '{dependency.Name}'", path);
            dependencies.Add(dependency.Id, matches[0]);
        }

        ImmutableArray<XelibTypeRecord> types = ReadSection<ImmutableArray<XelibTypeRecord>>(
            container, XelibSectionKind.Types, path);
        ImmutableArray<XelibSymbolRecord> symbols = ReadSection<ImmutableArray<XelibSymbolRecord>>(
            container, XelibSectionKind.Symbols, path);
        ImmutableArray<XelibExport> exports = ReadSection<ImmutableArray<XelibExport>>(
            container, XelibSectionKind.Exports, path);
        ImmutableArray<XelibDocumentation> documentation = ReadSection<ImmutableArray<XelibDocumentation>>(
            container, XelibSectionKind.Documentation, path);
        ValidateIds(types, symbols, exports, documentation, path);

        try
        {
            var reconstruction = new XelibSemanticReconstruction(metadata.Manifest, types, symbols,
                exports, documentation, dependencies, path);
            reconstruction.ReconstructMetadata();
            ImmutableArray<XelibGenericImplementation> generics =
                ReadSection<ImmutableArray<XelibGenericImplementation>>(
                    container, XelibSectionKind.GenericImplementations, path);
            // Tooling discards ordinary native bodies, but retains the portable generic payloads
            // needed to diagnose and specialize generic calls in a consumer source snapshot.
            if (!metadataOnly || !generics.IsEmpty)
            {
                ImmutableArray<XelibBodyRecord> bodies = metadataOnly
                    ? ReadGenericBodies(container, generics, symbols, path)
                    : ReadSection<ImmutableArray<XelibBodyRecord>>(container, XelibSectionKind.Bodies, path);
                reconstruction.ReconstructBodies(bodies, generics, includeOrdinaryBodies: !metadataOnly);
            }
            return reconstruction.CreateReference(path);
        }
        catch (XelibFormatException) { throw; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            InvalidCastException or IndexOutOfRangeException or KeyNotFoundException or OverflowException)
        {
            throw new XelibFormatException(XelibErrorCode.InvalidReference,
                $"invalid semantic graph: {exception.Message}", path, exception);
        }
    }

    private static T ReadSection<T>(XelibContainer container, XelibSectionKind kind, string? path) =>
        XelibJson.Deserialize<T>(container.GetRequiredSection(kind).AsSpan(), path);

    private static ImmutableArray<XelibBodyRecord> ReadGenericBodies(XelibContainer container,
        ImmutableArray<XelibGenericImplementation> generics,
        ImmutableArray<XelibSymbolRecord> symbols, string? path)
    {
        var requiredIds = generics.SelectMany(item =>
            (item.BodyId == 0 ? Enumerable.Empty<int>() : [item.BodyId])
            .Concat((item.StaticFieldInitializers.IsDefault ? [] : item.StaticFieldInitializers)
                .Select(initializer => initializer.BodyId))).ToHashSet();
        ReadOnlySpan<byte> bytes = container.GetRequiredSection(XelibSectionKind.Bodies).AsSpan();
        var result = ImmutableArray.CreateBuilder<XelibBodyRecord>();
        try
        {
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                throw Invalid("body section is not an array", path);
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.StartObject)
                    throw Invalid("body section contains a non-object record", path);
                int start = checked((int)reader.TokenStartIndex);
                var header = reader;
                int id = ReadBodyId(ref header, path);
                reader.Skip();
                int length = checked((int)reader.BytesConsumed) - start;
                XelibBodyRecord record = XelibJson.Deserialize<XelibBodyRecord>(
                    bytes.Slice(start, length), path);
                if (requiredIds.Contains(id) || symbols.Any(symbol =>
                        symbol.Id == record.FunctionSymbolId && symbol.Kind == XelibSymbolKind.Function &&
                        (symbol.Flags & XelibSymbolFlags.Lambda) != 0))
                    result.Add(record);
            }
            if (reader.TokenType != JsonTokenType.EndArray)
                throw Invalid("unterminated body section", path);
        }
        catch (XelibFormatException) { throw; }
        catch (JsonException exception)
        {
            throw new XelibFormatException(XelibErrorCode.InvalidRecord,
                $"invalid Library IR body section: {exception.Message}", path, exception);
        }
        return result.ToImmutable();
    }

    private static int ReadBodyId(ref Utf8JsonReader reader, string? path)
    {
        int? id = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                return id ?? throw Invalid("body record does not contain an ID", path);
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw Invalid("body record contains an invalid property", path);
            bool isId = reader.ValueTextEquals("id"u8);
            if (!reader.Read()) throw Invalid("body record contains an incomplete property", path);
            if (isId)
            {
                if (id is not null || reader.TokenType != JsonTokenType.Number ||
                    !reader.TryGetInt32(out int parsed) || parsed <= 0)
                    throw Invalid("body record contains an invalid or duplicate ID", path);
                id = parsed;
            }
            reader.Skip();
        }
        throw Invalid("unterminated body record", path);
    }

    private static void ValidateIds(ImmutableArray<XelibTypeRecord> types,
        ImmutableArray<XelibSymbolRecord> symbols, ImmutableArray<XelibExport> exports,
        ImmutableArray<XelibDocumentation> documentation, string? path)
    {
        if (types.Any(item => item.Id <= 0) || types.Select(item => item.Id).Distinct().Count() != types.Length)
            throw Invalid("type IDs must be positive and unique", path);
        if (symbols.Any(item => item.Id <= 0) || symbols.Select(item => item.Id).Distinct().Count() != symbols.Length)
            throw Invalid("symbol IDs must be positive and unique", path);
        var symbolIds = symbols.Select(item => item.Id).ToHashSet();
        if (exports.Any(item => !symbolIds.Contains(item.SymbolId)) ||
            exports.Select(item => item.Key).Distinct(StringComparer.Ordinal).Count() != exports.Length)
            throw Invalid("export records contain an invalid symbol ID or duplicate key", path);
        if (documentation.Any(item => !symbolIds.Contains(item.SymbolId)) ||
            documentation.Select(item => item.SymbolId).Distinct().Count() != documentation.Length)
            throw Invalid("documentation records contain an invalid or duplicate symbol ID", path);
    }

    internal static XelibFormatException Invalid(string message, string? path) =>
        new(XelibErrorCode.InvalidReference, message, path);
}

internal sealed class XelibSemanticReconstruction
{
    private readonly XelibManifest _manifest;
    private readonly ImmutableArray<XelibTypeRecord> _typeRecords;
    private readonly ImmutableArray<XelibSymbolRecord> _symbolRecords;
    private readonly ImmutableArray<XelibExport> _exportRecords;
    private readonly Dictionary<int, XelibDocumentation> _documentation;
    private readonly IReadOnlyDictionary<int, LibraryCompilationReference> _dependencies;
    private readonly string? _path;
    private readonly NamespaceSymbol _globalNamespace = new(string.Empty, null);
    private readonly TypeFactory _typeFactory = new();
    private readonly Dictionary<int, Symbol> _symbols = [];
    private readonly Dictionary<int, TypeSymbol> _types = [];
    private readonly Dictionary<int, XelibSymbolRecord> _records;
    private ImmutableArray<BoundFunction> _functions = [];
    private GenericImplementationStore _genericImplementations = GenericImplementationStore.Empty;

    public XelibSemanticReconstruction(XelibManifest manifest,
        ImmutableArray<XelibTypeRecord> types, ImmutableArray<XelibSymbolRecord> symbols,
        ImmutableArray<XelibExport> exports, ImmutableArray<XelibDocumentation> documentation,
        IReadOnlyDictionary<int, LibraryCompilationReference> dependencies, string? path)
    {
        _manifest = manifest;
        _typeRecords = types;
        _symbolRecords = symbols;
        _exportRecords = exports;
        _documentation = documentation.ToDictionary(item => item.SymbolId);
        _dependencies = dependencies;
        _path = path;
        _records = symbols.ToDictionary(item => item.Id);
    }

    public void ReconstructMetadata()
    {
        CreateNamespaces();
        CreateDeclaredSymbols();
        CreateGenericParameters();
        RegisterDeclaredSymbols();
        foreach (XelibTypeRecord record in _typeRecords) _ = ResolveType(record.Id);
        CreateNonFunctionMembers();
        CreateFunctions();
        CompleteRelations();
        CompleteConstraints();
        CompleteConstantExpressions();
    }

    public void ReconstructBodies(ImmutableArray<XelibBodyRecord> bodies,
        ImmutableArray<XelibGenericImplementation> generics, bool includeOrdinaryBodies = true)
    {
        if (bodies.Any(item => item.Id <= 0) || bodies.Select(item => item.Id).Distinct().Count() != bodies.Length ||
            bodies.Select(item => item.FunctionSymbolId).Distinct().Count() != bodies.Length)
            throw XelibReader.Invalid("body IDs and function associations must be unique", _path);
        static ImmutableArray<XelibGenericFieldInitializer> StaticInitializers(
            XelibGenericImplementation item) => item.StaticFieldInitializers.IsDefault
                ? [] : item.StaticFieldInitializers;
        static ImmutableArray<XelibGenericConstantImplementation> Constants(
            XelibGenericImplementation item) => item.Constants.IsDefault ? [] : item.Constants;
        if (generics.Any(item => item.DefinitionSymbolId <= 0 || item.BodyId < 0 ||
                !item.IsStruct && (item.BodyId == 0 || !StaticInitializers(item).IsEmpty ||
                    !Constants(item).IsEmpty) ||
                StaticInitializers(item).Select(value => value.FieldSymbolId).Distinct().Count() !=
                    StaticInitializers(item).Length ||
                Constants(item).Select(value => value.ConstantSymbolId).Distinct().Count() !=
                    Constants(item).Length) ||
            generics.Select(item => item.DefinitionSymbolId).Distinct().Count() != generics.Length ||
            generics.SelectMany(item => (item.BodyId == 0 ? Enumerable.Empty<int>() : [item.BodyId])
                    .Concat(StaticInitializers(item).Select(value => value.BodyId)))
                .GroupBy(id => id).Any(group => group.Key <= 0 || group.Count() != 1))
            throw XelibReader.Invalid("generic implementation records are invalid or unsupported", _path);
        var bodiesById = bodies.ToDictionary(item => item.Id);
        var lambdaBodies = new Dictionary<FunctionSymbol, XelibBodyNode>(ReferenceEqualityComparer.Instance);
        foreach (XelibBodyRecord item in bodies)
            if (Symbol(item.FunctionSymbolId) is FunctionSymbol { IsLambda: true } lambda)
                lambdaBodies.Add(lambda, item.Root);
        var genericBodyIds = generics.SelectMany(item =>
                (item.BodyId == 0 ? Enumerable.Empty<int>() : [item.BodyId])
                .Concat(StaticInitializers(item).Select(value => value.BodyId))).ToHashSet();
        _functions = includeOrdinaryBodies
            ? bodies.Where(item => !genericBodyIds.Contains(item.Id)).OrderBy(item => item.Id).Select(item =>
            {
                if (Symbol(item.FunctionSymbolId) is not FunctionSymbol function)
                    throw XelibReader.Invalid($"body {item.Id} does not reference a function", _path);
                return new BoundFunction(function, XelibBodyCodec.Decode(item.Root, function,
                    ResolveType, Resolve));
            }).ToImmutableArray()
            : [];
        var implementations = new GenericImplementationStoreBuilder([]);
        foreach (XelibGenericImplementation generic in generics)
        {
            if (generic.BodyId != 0 && !bodiesById.ContainsKey(generic.BodyId))
                throw XelibReader.Invalid("generic implementation references an invalid body", _path);
            if (generic.IsStruct)
            {
                XelibBodyRecord? body = generic.BodyId == 0 ? null : bodiesById[generic.BodyId];
                FunctionSymbol? initializer = body is null ? null : Symbol(body.FunctionSymbolId) as FunctionSymbol;
                if (Symbol(generic.DefinitionSymbolId) is not StructTypeSymbol { IsGenericDefinition: true } structure ||
                    initializer is not null && (initializer.FunctionKind != FunctionKind.InstanceInitializer ||
                        !ReferenceEquals(initializer.ContainingStruct, structure)))
                    throw XelibReader.Invalid("generic struct implementation references an invalid definition", _path);
                var staticInitializers = ImmutableArray.CreateBuilder<(
                    FieldSymbol Field, FunctionSymbol Definition, XelibBodyNode Body)>();
                foreach (XelibGenericFieldInitializer item in StaticInitializers(generic))
                {
                    if (!bodiesById.TryGetValue(item.BodyId, out XelibBodyRecord? staticBody) ||
                        Symbol(item.FieldSymbolId) is not FieldSymbol
                        {
                            IsStatic: true, IsThreadLocal: true,
                        } field || !ReferenceEquals(field.ContainingType, structure) ||
                        Symbol(staticBody.FunctionSymbolId) is not FunctionSymbol
                        {
                            FunctionKind: FunctionKind.ThreadLocalInitializer,
                        } function || !ReferenceEquals(function.ThreadLocalField, field))
                        throw XelibReader.Invalid(
                            "generic static initializer references an invalid field or body", _path);
                    staticInitializers.Add((field, function, staticBody.Root));
                }
                var constants = ImmutableArray.CreateBuilder<(
                    ConstantSymbol Constant, XelibBodyNode Expression)>();
                var constantExpressions = new Dictionary<int, XelibBodyNode>();
                foreach (XelibSymbolRecord item in _symbolRecords.Where(item =>
                             item.Kind == XelibSymbolKind.Constant &&
                             item.ContainingSymbolId == generic.DefinitionSymbolId &&
                             item.ConstantExpression is not null))
                    constantExpressions.Add(item.Id, item.ConstantExpression!);
                // Accept the legacy per-generic payload while keeping the symbol record as the
                // canonical representation emitted by current writers.
                foreach (XelibGenericConstantImplementation item in Constants(generic))
                    if (!constantExpressions.TryAdd(item.ConstantSymbolId, item.Expression))
                        throw XelibReader.Invalid(
                            "generic constant implementation is represented more than once", _path);
                foreach ((int constantId, XelibBodyNode expression) in constantExpressions)
                {
                    if (Symbol(constantId) is not ConstantSymbol constant ||
                        !ReferenceEquals(constant.ContainingType, structure) || expression is null)
                        throw XelibReader.Invalid(
                            "generic constant implementation references an invalid constant", _path);
                    constants.Add((constant, expression));
                }
                implementations.AddStruct(structure, new LibraryGenericStructImplementation(
                    initializer, body?.Root, staticInitializers.ToImmutable(),
                    constants.ToImmutable(), ResolveType, Resolve, lambdaBodies));
            }
            else
            {
                XelibBodyRecord body = bodiesById[generic.BodyId];
                if (Symbol(generic.DefinitionSymbolId) is not FunctionSymbol definition ||
                    !definition.IsGenericDefinition || body.FunctionSymbolId != generic.DefinitionSymbolId)
                    throw XelibReader.Invalid("generic function implementation references an invalid definition", _path);
                implementations.AddFunction(definition, new LibraryGenericFunctionImplementation(
                    definition, body.Root, ResolveType, Resolve, lambdaBodies));
            }
        }
        _genericImplementations = implementations.ToImmutable();
    }

    public LibraryCompilationReference CreateReference(string? path)
    {
        var exports = ImmutableDictionary.CreateBuilder<string, Symbol>(StringComparer.Ordinal);
        foreach (XelibExport export in _exportRecords)
            exports.Add(export.Key, Symbol(export.SymbolId));
        return new LibraryCompilationReference(
            new XelibLibraryIdentity(_manifest.Name, _manifest.Version, _manifest.ContentIdentity),
            _globalNamespace, _genericImplementations, _functions, exports.ToImmutable(),
            _dependencies.OrderBy(item => item.Key).Select(item => item.Value).ToImmutableArray(), path);
    }

    private void CreateNamespaces()
    {
        foreach (XelibSymbolRecord record in _symbolRecords.Where(item => item.Kind == XelibSymbolKind.Namespace))
            _ = Namespace(record.Id, new HashSet<int>());
    }

    private NamespaceSymbol Namespace(int id, HashSet<int> stack)
    {
        if (_symbols.TryGetValue(id, out Symbol? existing)) return (NamespaceSymbol)existing;
        if (!_records.TryGetValue(id, out XelibSymbolRecord? record) || record.Kind != XelibSymbolKind.Namespace)
            throw XelibReader.Invalid($"symbol ID {id} is not a namespace", _path);
        if (!stack.Add(id)) throw XelibReader.Invalid("namespace containment cycle", _path);
        NamespaceSymbol parent = record.ContainingSymbolId == 0 ? _globalNamespace :
            Namespace(record.ContainingSymbolId, stack);
        NamespaceSymbol created = parent.GetOrAddNamespace(record.Name);
        created.SetMetadata(Origin(record.Id), Documentation(record.Id));
        _symbols.Add(id, created);
        stack.Remove(id);
        return created;
    }

    private void CreateDeclaredSymbols()
    {
        foreach (XelibSymbolRecord record in _symbolRecords.Where(item => item.Kind is
            XelibSymbolKind.Struct or XelibSymbolKind.Interface or XelibSymbolKind.Enum or XelibSymbolKind.Template))
        {
            NamespaceSymbol owner = OwnerNamespace(record);
            Symbol created = record.Kind switch
            {
                XelibSymbolKind.Struct => new StructTypeSymbol(record.Name, owner,
                    Has(record, XelibSymbolFlags.Abstract), Origin(record.Id), Documentation(record.Id),
                    Accessibility(record), Has(record, XelibSymbolFlags.ReadonlyStruct),
                    Has(record, XelibSymbolFlags.StaticStruct), Has(record, XelibSymbolFlags.Sealed)),
                XelibSymbolKind.Interface => new InterfaceTypeSymbol(record.Name, owner,
                    Origin(record.Id), Documentation(record.Id), Accessibility(record)),
                XelibSymbolKind.Enum => new EnumTypeSymbol(record.Name, owner,
                    Origin(record.Id), Documentation(record.Id), Accessibility(record)),
                XelibSymbolKind.Template => new TemplateSymbol(record.Name, owner,
                    Origin(record.Id), Documentation(record.Id), Accessibility(record)),
                _ => throw new InvalidOperationException(),
            };
            _symbols.Add(record.Id, created);
        }
    }

    private void CreateGenericParameters()
    {
        foreach (IGrouping<int, XelibSymbolRecord> group in _symbolRecords
            .Where(item => item.Kind == XelibSymbolKind.GenericParameter)
            .GroupBy(item => item.ContainingSymbolId))
        {
            Symbol temporaryOwner = _symbols.GetValueOrDefault(group.Key) ?? _globalNamespace;
            GenericParameterSymbol[] parameters = group.OrderBy(item => item.Ordinal).Select(record =>
            {
                var created = new GenericParameterSymbol(record.Name, record.Ordinal, temporaryOwner,
                    Origin(record.Id), Documentation(record.Id));
                _symbols.Add(record.Id, created);
                return created;
            }).ToArray();
            if (_symbols.GetValueOrDefault(group.Key) is StructTypeSymbol structure)
                structure.SetTypeParameters([.. parameters]);
        }
    }

    private void RegisterDeclaredSymbols()
    {
        foreach (XelibSymbolRecord record in _symbolRecords.Where(item => item.Kind is
            XelibSymbolKind.Struct or XelibSymbolKind.Interface or XelibSymbolKind.Enum or XelibSymbolKind.Template))
        {
            Symbol created = _symbols[record.Id];
            NamespaceSymbol owner = (NamespaceSymbol)created.ContainingSymbol!;
            bool declared = created switch
            {
                DeclaredTypeSymbol type => owner.TryDeclareType(type),
                TemplateSymbol template => owner.TryDeclareTemplate(template),
                _ => false,
            };
            if (!declared) throw XelibReader.Invalid(
                $"duplicate declaration '{created.QualifiedName}'", _path);
        }
    }

    private void CreateNonFunctionMembers()
    {
        foreach (XelibSymbolRecord record in _symbolRecords.Where(item => item.Kind is
            XelibSymbolKind.Field or XelibSymbolKind.Property or XelibSymbolKind.Indexer or
            XelibSymbolKind.Constant or XelibSymbolKind.EnumMember or
            XelibSymbolKind.TemplateMethod or XelibSymbolKind.TemplateConstructor or
            XelibSymbolKind.TemplateProperty or XelibSymbolKind.TemplateIndexer))
        {
            Symbol owner = Symbol(record.ContainingSymbolId);
            Symbol created = record.Kind switch
            {
                XelibSymbolKind.Field => CreateField(record, (DeclaredTypeSymbol)owner),
                XelibSymbolKind.Property when owner is InterfaceTypeSymbol @interface =>
                    new InterfacePropertySymbol(record.Name, @interface, ResolveType(record.TypeId),
                        Has(record, XelibSymbolFlags.Readonly), Origin(record.Id), Documentation(record.Id)),
                XelibSymbolKind.Property => new PropertySymbol(record.Name, (DeclaredTypeSymbol)owner,
                    ResolveType(record.TypeId), Accessibility(record), Has(record, XelibSymbolFlags.Static),
                    Has(record, XelibSymbolFlags.Readonly), Has(record, XelibSymbolFlags.Virtual),
                    Has(record, XelibSymbolFlags.Override), Has(record, XelibSymbolFlags.Abstract),
                    Origin(record.Id), Documentation(record.Id)),
                XelibSymbolKind.Indexer when owner is InterfaceTypeSymbol @interface =>
                    new InterfaceIndexerSymbol(@interface, ResolveType(record.TypeId), Parameters(record),
                        Has(record, XelibSymbolFlags.Readonly), Origin(record.Id), Documentation(record.Id)),
                XelibSymbolKind.Indexer => new IndexerSymbol((DeclaredTypeSymbol)owner,
                    ResolveType(record.TypeId), Parameters(record), Accessibility(record),
                    Has(record, XelibSymbolFlags.Static), Has(record, XelibSymbolFlags.Readonly),
                    Has(record, XelibSymbolFlags.Virtual), Has(record, XelibSymbolFlags.Override),
                    Has(record, XelibSymbolFlags.Abstract), Origin(record.Id), Documentation(record.Id)),
                XelibSymbolKind.Constant or XelibSymbolKind.EnumMember =>
                    new ConstantSymbol(record.Name, ResolveType(record.TypeId), owner,
                        ParseConstant(record.ConstantValue, ResolveType(record.TypeId)),
                        record.ConstantValue is not null, Origin(record.Id), Documentation(record.Id),
                        accessibility: Accessibility(record)),
                XelibSymbolKind.TemplateMethod => CreateTemplateMethod(record, (TemplateSymbol)owner),
                XelibSymbolKind.TemplateConstructor => new TemplateConstructorRequirementSymbol(
                    (TemplateSymbol)owner, Parameters(record), Accessibility(record),
                    Origin(record.Id), Documentation(record.Id)),
                XelibSymbolKind.TemplateProperty => new TemplatePropertyRequirementSymbol(record.Name,
                    (TemplateSymbol)owner, ResolveType(record.TypeId), Accessibility(record),
                    Has(record, XelibSymbolFlags.Static), Has(record, XelibSymbolFlags.Readonly),
                    hasGetter: record.HasGetter, hasSetter: record.HasSetter,
                    Origin(record.Id), Documentation(record.Id)),
                XelibSymbolKind.TemplateIndexer => new TemplateIndexerRequirementSymbol(
                    (TemplateSymbol)owner, ResolveType(record.TypeId), Parameters(record), Accessibility(record),
                    Has(record, XelibSymbolFlags.Static), Has(record, XelibSymbolFlags.Readonly),
                    hasGetter: record.HasGetter, hasSetter: record.HasSetter,
                    Origin(record.Id), Documentation(record.Id)),
                _ => throw new InvalidOperationException(),
            };
            if (created is ConstantSymbol constant && record.ConstantValue is not null)
                constant.SetBoundValue(new BoundLiteralExpression(constant.Value, constant.Type));
            _symbols.Add(record.Id, created);
            MapOwnedParameters(record, created);
        }
    }

    private void CompleteConstantExpressions()
    {
        foreach (XelibSymbolRecord record in _symbolRecords.Where(item => item.ConstantExpression is not null))
        {
            if (record.Kind is not (XelibSymbolKind.Constant or XelibSymbolKind.EnumMember) ||
                Symbol(record.Id) is not ConstantSymbol constant || record.ConstantValue is not null)
                throw XelibReader.Invalid("constant expression is attached to an invalid symbol", _path);
            constant.SetBoundValue(XelibBodyCodec.DecodeExpression(
                record.ConstantExpression!, ResolveType, Resolve));
        }
        foreach (XelibSymbolRecord record in _symbolRecords.Where(item =>
                     item.Kind is XelibSymbolKind.Constant or XelibSymbolKind.EnumMember))
            if ((record.ConstantValue is null) == (record.ConstantExpression is null))
                throw XelibReader.Invalid(
                    $"constant symbol {record.Id} must contain exactly one value representation", _path);
    }

    private void CreateFunctions()
    {
        foreach (XelibSymbolRecord record in _symbolRecords.Where(item => item.Kind == XelibSymbolKind.Function))
        {
            Symbol owner = record.ContainingSymbolId == 0 ? _globalNamespace : Symbol(record.ContainingSymbolId);
            ImmutableArray<GenericParameterSymbol> typeParameters = record.TypeParameterIds
                .Select(id => (GenericParameterSymbol)Symbol(id)).ToImmutableArray();
            FunctionSymbol created;
            if (Map(record.FunctionKind) == FunctionKind.ThreadLocalInitializer)
            {
                if (record.RelatedSymbolIds is not [int fieldId] ||
                    Symbol(fieldId) is not FieldSymbol { IsStatic: true, IsThreadLocal: true } field ||
                    !ReferenceEquals(field.ContainingSymbol, owner))
                    throw XelibReader.Invalid(
                        "thread-local initializer does not reference its owning field", _path);
                created = new FunctionSymbol(field);
                created.SetMetadata(Origin(record.Id), Documentation(record.Id));
            }
            else
            {
                created = new FunctionSymbol(record.Name, owner, Map(record.FunctionKind),
                    ResolveType(record.ReturnTypeId), Parameters(record), Accessibility(record),
                    Has(record, XelibSymbolFlags.Static), Has(record, XelibSymbolFlags.Readonly),
                    Has(record, XelibSymbolFlags.Virtual), Has(record, XelibSymbolFlags.Override),
                    Has(record, XelibSymbolFlags.Abstract), Has(record, XelibSymbolFlags.Extern),
                    Has(record, XelibSymbolFlags.Export), Has(record, XelibSymbolFlags.Definition),
                    Has(record, XelibSymbolFlags.DelegatesToThisConstructor), typeParameters,
                    Origin(record.Id), Documentation(record.Id), accessorKind: Map(record.AccessorKind),
                    operatorKind: XelibOperatorKinds.Decode(record.OperatorKind, record.ParameterIds.Length));
            }
            created.HasStackArrays = Has(record, XelibSymbolFlags.HasStackArrays);
            created.HasScalarCleanup = Has(record, XelibSymbolFlags.HasScalarCleanup);
            created.IsLambda = Has(record, XelibSymbolFlags.Lambda);
            created.IsCapturingLambda = Has(record, XelibSymbolFlags.CapturingLambda);
            created.LambdaCaptures = record.Captures.OrderBy(capture => capture.Ordinal)
                .Select(capture => new CaptureVariableSymbol(capture.Name,
                    ResolveType(capture.TypeId), ResolveType(capture.StorageTypeId),
                    XelibStableMappings.FromXelib(capture.Kind), capture.Ordinal, created,
                    Origin(record.Id))).ToImmutableArray();
            if (record.VTableSlot is { } slot) created.SetVTableSlot(slot);
            created.SetConstructorOverload(record.ConstructorOverload, record.ConstructorOverloadCount);
            created.SetReceiverMoveEffects(record.ReceiverMoveEffects.Select(item =>
                new ReceiverMoveEffect(item)).ToImmutableArray());
            created.SetReferenceReturnOrigins(record.ReferenceReturnOrigins.Select(item =>
                new ReferenceReturnOrigin(XelibStableMappings.FromXelib(item.Kind),
                    item.ParameterOrdinal, item.FieldOrdinals)).ToImmutableArray());
            created.SetSharedReturnOrigins(record.SharedReturnOrigins.Select(item =>
                new SharedReturnOrigin(XelibStableMappings.FromXelib(item.Kind),
                    item.ParameterOrdinal)).ToImmutableArray());
            created.SetReferenceFieldOrigins(record.ReferenceFieldOrigins.Select(item =>
                new ReferenceFieldOrigin(item.FieldOrdinals,
                    new ReferenceReturnOrigin(XelibStableMappings.FromXelib(item.Origin.Kind),
                        item.Origin.ParameterOrdinal, item.Origin.FieldOrdinals),
                    item.IsReadonly)).ToImmutableArray());
            _symbols.Add(record.Id, created);
            MapOwnedParameters(record, created);
        }
    }

    private void CompleteRelations()
    {
        foreach (XelibSymbolRecord record in _symbolRecords)
        {
            switch (Symbol(record.Id))
            {
                case StructTypeSymbol type:
                    type.SetFields(Children<FieldSymbol>(record.Id, static item => !item.IsStatic));
                    type.SetStaticFields(Children<FieldSymbol>(record.Id, static item => item.IsStatic));
                    type.SetProperties(Children<PropertySymbol>(record.Id));
                    type.SetIndexers(Children<IndexerSymbol>(record.Id));
                    type.SetConstants(Children<ConstantSymbol>(record.Id));
                    type.SetMethods(Children<FunctionSymbol>(record.Id,
                        static item => item.FunctionKind == FunctionKind.Method));
                    type.SetConstructors(Children<FunctionSymbol>(record.Id,
                        static item => item.FunctionKind == FunctionKind.Constructor));
                    if (Children<FunctionSymbol>(record.Id,
                        static item => item.FunctionKind == FunctionKind.InstanceInitializer).FirstOrDefault()
                        is { } initializer) type.SetInstanceInitializer(initializer);
                    if (Children<FunctionSymbol>(record.Id,
                        static item => item.FunctionKind == FunctionKind.Destructor).FirstOrDefault()
                        is { } destructor) type.SetDestructor(destructor);
                    if (Children<FunctionSymbol>(record.Id,
                        static item => item.FunctionKind == FunctionKind.DestructorGlue).FirstOrDefault()
                        is { } destructorGlue) type.SetDestructorGlue(destructorGlue);
                    if (record.BaseTypeId != 0) type.SetBaseType((StructTypeSymbol)ResolveType(record.BaseTypeId));
                    type.SetInterfaces(record.InterfaceTypeIds.Select(id =>
                        (InterfaceTypeSymbol)ResolveType(id)).ToImmutableArray());
                    if (Has(record, XelibSymbolFlags.HasVirtualDispatch)) type.SetHasVirtualDispatch();
                    type.SetVirtualMethods(record.RelatedSymbolIds.Select(id =>
                        (FunctionSymbol)Symbol(id)).ToImmutableArray());
                    break;
                case InterfaceTypeSymbol type:
                    type.SetBaseInterfaces(record.InterfaceTypeIds.Select(id =>
                        (InterfaceTypeSymbol)ResolveType(id)).ToImmutableArray());
                    type.SetMethods(Children<FunctionSymbol>(record.Id,
                        static item => item.ContainingSymbol is InterfaceTypeSymbol));
                    type.SetProperties(Children<InterfacePropertySymbol>(record.Id));
                    type.SetIndexers(Children<InterfaceIndexerSymbol>(record.Id));
                    type.SetMethodSlots(record.RelatedSymbolIds.Select(id => (FunctionSymbol)Symbol(id)));
                    break;
                case EnumTypeSymbol type:
                    type.UnderlyingType = (PrimitiveTypeSymbol)ResolveType(record.TypeId);
                    type.Members = record.RelatedSymbolIds.Select(id =>
                        (ConstantSymbol)Symbol(id)).ToImmutableArray();
                    type.StaticFields = Children<FieldSymbol>(record.Id, static item => item.IsStatic);
                    break;
                case TemplateSymbol template:
                    template.SetMembers(_symbolRecords.Where(item => item.ContainingSymbolId == record.Id)
                        .OrderBy(item => item.Order).Select(item => Symbol(item.Id))
                        .OfType<TemplateMemberRequirementSymbol>().ToImmutableArray());
                    break;
                case PropertySymbol property:
                    property.SetAccessors(OptionalFunction(record.GetterId), OptionalFunction(record.SetterId));
                    break;
                case InterfacePropertySymbol property:
                    property.SetAccessors(OptionalFunction(record.GetterId), OptionalFunction(record.SetterId));
                    break;
                case IndexerSymbol indexer:
                    indexer.SetAccessors(OptionalFunction(record.GetterId), OptionalFunction(record.SetterId));
                    break;
                case InterfaceIndexerSymbol indexer:
                    indexer.SetAccessors(OptionalFunction(record.GetterId), OptionalFunction(record.SetterId));
                    break;
                case FunctionSymbol function when function.ContainingSymbol is NamespaceSymbol:
                    if (!function.ContainingNamespace.TryDeclareFunction(function))
                        throw XelibReader.Invalid($"duplicate function '{function.FullName}'", _path);
                    break;
                case ConstantSymbol constant when constant.ContainingSymbol is NamespaceSymbol:
                    if (!constant.ContainingNamespace.TryDeclareConstant(constant))
                        throw XelibReader.Invalid($"duplicate constant '{constant.QualifiedName}'", _path);
                    break;
            }
        }

        foreach (FunctionSymbol function in _symbols.Values.OfType<FunctionSymbol>())
        {
            if (function.Parameters.FirstOrDefault()?.Type is not PointerTypeSymbol pointer) continue;
            if (function.FunctionKind == FunctionKind.OwnershipDestructor &&
                pointer.ElementType is OwnershipTypeSymbol ownership)
                ownership.CompleteDestructor = function;
            else if (function.FunctionKind == FunctionKind.StorageDestructor &&
                     pointer.ElementType is StorageTypeSymbol storage)
                storage.CompleteDestructor = function;
            else if (function.FunctionKind == FunctionKind.FunctionValueDestructor &&
                     pointer.ElementType is FunctionValueTypeSymbol functionValue)
                functionValue.CompleteDestructor = function;
        }

        // Open generic ownership/storage/function-value destructors have no portable body and
        // are intentionally omitted from the library function table.  Keep their semantic
        // destructor association nevertheless: specialization in a consuming compilation uses
        // it to synthesize the concrete destructor that owns a substituted closure capture.
        // Without this association a source-less shared<T> capture becomes shared<Resource>
        // with no destructor and its final strong reference is leaked.
        foreach (OwnershipTypeSymbol ownership in _typeFactory.OwnershipTypes)
            _typeFactory.EnsureOwnershipDestructor(ownership, _globalNamespace,
                GeneratedDestructorOrigin(ownership));
        foreach (StorageTypeSymbol storage in _typeFactory.StorageTypes)
            _typeFactory.EnsureStorageDestructor(storage, _globalNamespace,
                GeneratedDestructorOrigin(storage));
        foreach (FunctionValueTypeSymbol functionValue in _typeFactory.FunctionValueTypes)
            _typeFactory.EnsureFunctionValueDestructor(functionValue, _globalNamespace,
                GeneratedDestructorOrigin(functionValue));
    }

    private SymbolOrigin GeneratedDestructorOrigin(TypeSymbol type) => SymbolOrigin.FromLibrary(
        _manifest.ContentIdentity, $"generated-destructor:{TypeSignature.Get(type)}");

    private void CompleteConstraints()
    {
        foreach (XelibSymbolRecord record in _symbolRecords.Where(item =>
            item.Kind == XelibSymbolKind.GenericParameter))
        {
            var parameter = (GenericParameterSymbol)Symbol(record.Id);
            parameter.SetConstraints(record.Constraints.Select(item => new GenericConstraintSymbol(
                XelibStableMappings.FromXelib(item.Kind), Resolve(item.Target), Origin(record.Id))).ToImmutableArray());
        }
    }

    private TypeSymbol ResolveType(int id)
    {
        if (id == 0) throw XelibReader.Invalid("type ID zero is not valid here", _path);
        if (_types.TryGetValue(id, out TypeSymbol? existing)) return existing;
        XelibTypeRecord record = _typeRecords.FirstOrDefault(item => item.Id == id) ??
            throw XelibReader.Invalid($"unknown type ID {id}", _path);
        // Recursive value layouts are rejected by semantic analysis. Registering is therefore
        // only needed after a complete child has been reconstructed.
        TypeSymbol created = record.Kind switch
        {
            XelibTypeKind.Char => BuiltinTypes.Char,
            XelibTypeKind.Primitive => Builtin(record.PrimitiveName),
            XelibTypeKind.Declared => (TypeSymbol)Resolve(Required(record.Symbol)),
            XelibTypeKind.GenericParameter => (GenericParameterSymbol)Resolve(Required(record.Symbol)),
            XelibTypeKind.TemplateSelf => ((TemplateSymbol)Resolve(Required(record.Symbol))).SelfType,
            XelibTypeKind.ConstructedGeneric => Constructed(record),
            XelibTypeKind.Pointer => _typeFactory.PointerTo(ResolveType(record.ElementTypeId), record.IsReadonly),
            XelibTypeKind.Reference => _typeFactory.ReferenceTo(ResolveType(record.ElementTypeId), record.IsReadonly),
            XelibTypeKind.FunctionPointer => _typeFactory.FunctionPointer(ResolveType(record.ReturnTypeId),
                record.ParameterTypeIds.Select(ResolveType)),
            XelibTypeKind.FunctionValue => _typeFactory.FunctionValue(ResolveType(record.ReturnTypeId),
                record.ParameterTypeIds.Select(ResolveType)),
            XelibTypeKind.Array => _typeFactory.ArrayOf(ResolveType(record.ElementTypeId), record.Rank),
            XelibTypeKind.Atomic => _typeFactory.AtomicOf(ResolveType(record.ElementTypeId)),
            XelibTypeKind.Unique => _typeFactory.UniqueOf(ResolveType(record.ElementTypeId)),
            XelibTypeKind.Shared => _typeFactory.SharedOf(ResolveType(record.ElementTypeId)),
            XelibTypeKind.Weak => _typeFactory.WeakOf(ResolveType(record.ElementTypeId)),
            XelibTypeKind.Storage => _typeFactory.StorageOf(ResolveType(record.ElementTypeId)),
            XelibTypeKind.Pin => _typeFactory.PinOf(ResolveType(record.ElementTypeId)),
            _ => throw XelibReader.Invalid($"unknown type kind {(ushort)record.Kind}", _path),
        };
        _types.Add(id, created);
        return created;
    }

    private StructTypeSymbol Constructed(XelibTypeRecord record)
    {
        var definition = (StructTypeSymbol)Resolve(Required(record.Symbol));
        var created = record.ConstructedSymbol is { } reference
            ? (StructTypeSymbol)Resolve(reference)
            : new StructTypeSymbol(definition.Name, definition.ContainingNamespace,
            definition.IsAbstract, Origin(definition), definition.Documentation, definition.Accessibility,
            definition.IsReadonly, definition.IsStatic, definition.IsSealed);
        created.SetGenericSpecialization(definition,
            record.TypeArgumentIds.Select(ResolveType).ToImmutableArray());
        return created;
    }

    private Symbol Resolve(XelibSymbolReference reference)
    {
        if (reference.LocalId > 0 && reference.DependencyId == 0 && reference.ExportKey is null)
            return Symbol(reference.LocalId);
        if (reference.LocalId != 0 || reference.DependencyId <= 0 ||
            string.IsNullOrWhiteSpace(reference.ExportKey))
            throw XelibReader.Invalid("malformed symbol reference", _path);
        if (!_dependencies.TryGetValue(reference.DependencyId, out LibraryCompilationReference? dependency))
            throw XelibReader.Invalid($"unknown dependency ID {reference.DependencyId}", _path);
        if (!dependency.TryResolveExport(reference.ExportKey, out Symbol? symbol))
            throw XelibReader.Invalid(
                $"dependency '{dependency.LibraryIdentity.Name}' has no export '{reference.ExportKey}'", _path);
        return symbol;
    }

    private Symbol Symbol(int id) => _symbols.TryGetValue(id, out Symbol? value) ? value :
        throw XelibReader.Invalid($"unknown or not-yet-created symbol ID {id}", _path);
    private static XelibSymbolReference Required(XelibSymbolReference? reference) => reference ??
        throw new XelibFormatException(XelibErrorCode.InvalidReference, "missing symbol reference");

    private NamespaceSymbol OwnerNamespace(XelibSymbolRecord record) => record.ContainingSymbolId == 0
        ? _globalNamespace
        : Symbol(record.ContainingSymbolId) as NamespaceSymbol ??
          throw XelibReader.Invalid($"symbol '{record.Name}' must be contained by a namespace", _path);

    private ImmutableArray<ParameterSymbol> Parameters(XelibSymbolRecord owner) => owner.ParameterIds.Select(id =>
    {
        XelibSymbolRecord record = Record(id, XelibSymbolKind.Parameter);
        return new ParameterSymbol(record.Name, ResolveType(record.TypeId), record.Ordinal,
            Has(record, XelibSymbolFlags.Readonly));
    }).ToImmutableArray();

    private void MapOwnedParameters(XelibSymbolRecord owner, Symbol created)
    {
        ImmutableArray<ParameterSymbol> actual = created switch
        {
            FunctionSymbol value => value.Parameters,
            IndexerSymbol value => value.Parameters,
            InterfaceIndexerSymbol value => value.Parameters,
            TemplateMethodRequirementSymbol value => value.Parameters,
            TemplateConstructorRequirementSymbol value => value.Parameters,
            TemplateIndexerRequirementSymbol value => value.Parameters,
            _ => [],
        };
        if (actual.Length != owner.ParameterIds.Length)
            throw XelibReader.Invalid($"parameter count mismatch for symbol '{owner.Name}'", _path);
        for (int index = 0; index < actual.Length; index++)
        {
            int id = owner.ParameterIds[index];
            if (_symbols.TryGetValue(id, out Symbol? previous) && !ReferenceEquals(previous, actual[index]))
                throw XelibReader.Invalid($"parameter ID {id} is owned more than once", _path);
            actual[index].SetMetadata(Origin(id), Documentation(id));
            _symbols[id] = actual[index];
        }
    }

    private FieldSymbol CreateField(XelibSymbolRecord record, DeclaredTypeSymbol owner) => new(
        record.Name, owner, ResolveType(record.TypeId), record.Ordinal, Accessibility(record),
        Has(record, XelibSymbolFlags.Static), Has(record, XelibSymbolFlags.Readonly),
        Has(record, XelibSymbolFlags.ThreadLocal), Has(record, XelibSymbolFlags.HasInitializer),
        ParseConstant(record.ConstantValue, ResolveType(record.TypeId)), Origin(record.Id), Documentation(record.Id));

    private TemplateMethodRequirementSymbol CreateTemplateMethod(XelibSymbolRecord record,
        TemplateSymbol owner) => new(record.Name, owner, ResolveType(record.ReturnTypeId), Parameters(record),
            Accessibility(record), Has(record, XelibSymbolFlags.Static), Has(record, XelibSymbolFlags.Readonly),
            Origin(record.Id), Documentation(record.Id));

    private T[] ChildrenArray<T>(int ownerId, Func<T, bool>? predicate = null) where T : Symbol =>
        _symbolRecords.Where(item => item.ContainingSymbolId == ownerId)
            .OrderBy(item => item.Order).ThenBy(item => item.Id)
            .Select(item => _symbols.GetValueOrDefault(item.Id)).OfType<T>()
            .Where(item => predicate?.Invoke(item) ?? true).ToArray();
    private ImmutableArray<T> Children<T>(int ownerId, Func<T, bool>? predicate = null) where T : Symbol =>
        [.. ChildrenArray(ownerId, predicate)];

    private XelibSymbolRecord Record(int id, XelibSymbolKind expected) =>
        _records.TryGetValue(id, out XelibSymbolRecord? value) && value.Kind == expected ? value :
            throw XelibReader.Invalid($"symbol ID {id} is not a {expected} record", _path);
    private FunctionSymbol? OptionalFunction(int id) => id == 0 ? null : (FunctionSymbol)Symbol(id);
    private SymbolDocumentation Documentation(int id) => _documentation.TryGetValue(id, out XelibDocumentation? value)
        ? new SymbolDocumentation(value.Summary, value.Remarks, value.Returns,
            value.Parameters.ToImmutableDictionary(StringComparer.Ordinal),
            value.TypeParameters.ToImmutableDictionary(StringComparer.Ordinal))
        : SymbolDocumentation.Empty;
    private SymbolOrigin Origin(int id) => SymbolOrigin.FromLibrary(_manifest.ContentIdentity,
        _exportRecords.FirstOrDefault(item => item.SymbolId == id)?.Key ?? $"local:{id}");
    private static SymbolOrigin Origin(Symbol symbol) => symbol.Origin.Kind == SymbolOriginKind.Library
        ? symbol.Origin : SymbolOrigin.Library;
    private static bool Has(XelibSymbolRecord record, XelibSymbolFlags flag) => (record.Flags & flag) != 0;
    private static Accessibility Accessibility(XelibSymbolRecord record) =>
        XelibStableMappings.FromXelib(record.Accessibility);

    private static FunctionKind Map(XelibFunctionKind kind) => XelibStableMappings.FromXelib(kind);

    private static AccessorKind Map(XelibAccessorKind kind) => XelibStableMappings.FromXelib(kind);

    private static PrimitiveTypeSymbol Builtin(string? name) => name switch
    {
        "void" => BuiltinTypes.Void, "bool" => BuiltinTypes.Bool,
        "byte" => BuiltinTypes.Byte, "sbyte" => BuiltinTypes.SByte,
        "short" => BuiltinTypes.Short, "ushort" => BuiltinTypes.UShort,
        "int" => BuiltinTypes.Int, "uint" => BuiltinTypes.UInt,
        "long" => BuiltinTypes.Long, "ulong" => BuiltinTypes.ULong,
        "float" => BuiltinTypes.Float, "double" => BuiltinTypes.Double,
        "nint" => BuiltinTypes.NInt, "nuint" => BuiltinTypes.NUInt,
        "clong" => BuiltinTypes.CLong, "culong" => BuiltinTypes.CULong,
        _ => throw new XelibFormatException(XelibErrorCode.InvalidRecord,
            $"unknown primitive type '{name}'"),
    };

    private static object? ParseConstant(XelibConstantValue? value, TypeSymbol type)
    {
        if (value is null || value.Kind == XelibConstantKind.Null) return null;
        string text = value.Value ?? throw new XelibFormatException(
            XelibErrorCode.InvalidRecord, "constant value text is missing");
        try
        {
            return value.Kind switch
            {
                XelibConstantKind.Boolean => bool.Parse(text),
                XelibConstantKind.UnicodeScalar when TypeFacts.IsCharacter(type) => ParseUnicodeScalar(text),
                XelibConstantKind.SignedInteger => ParseSigned(text, type),
                XelibConstantKind.UnsignedInteger => ParseUnsigned(text, type),
                XelibConstantKind.FloatingPoint when ReferenceEquals(type, BuiltinTypes.Float) =>
                    float.Parse(text, CultureInfo.InvariantCulture),
                XelibConstantKind.FloatingPoint => double.Parse(text, CultureInfo.InvariantCulture),
                XelibConstantKind.String => text,
                _ => throw new FormatException(),
            };
        }
        catch (FormatException exception)
        {
            throw new XelibFormatException(XelibErrorCode.InvalidRecord,
                $"invalid constant '{text}'", innerException: exception);
        }
    }

    private static object ParseSigned(string text, TypeSymbol type)
    {
        long value = long.Parse(text, CultureInfo.InvariantCulture);
        if (ReferenceEquals(type, BuiltinTypes.SByte)) return checked((sbyte)value);
        if (ReferenceEquals(type, BuiltinTypes.Short)) return checked((short)value);
        if (ReferenceEquals(type, BuiltinTypes.Int)) return checked((int)value);
        return value;
    }

    private static object ParseUnsigned(string text, TypeSymbol type)
    {
        ulong value = ulong.Parse(text, CultureInfo.InvariantCulture);
        if (ReferenceEquals(type, BuiltinTypes.Byte)) return checked((byte)value);
        if (ReferenceEquals(type, BuiltinTypes.UShort)) return checked((ushort)value);
        if (ReferenceEquals(type, BuiltinTypes.UInt)) return checked((uint)value);
        return value;
    }

    private static ulong ParseUnicodeScalar(string text)
    {
        if (!ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value))
            throw new FormatException();
        if (value > uint.MaxValue || !UnicodeScalarFacts.IsValid((uint)value))
            throw new FormatException();
        return value;
    }
}
