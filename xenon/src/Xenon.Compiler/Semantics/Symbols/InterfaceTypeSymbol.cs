using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class InterfaceTypeSymbol : DeclaredTypeSymbol
{
    private ImmutableArray<FunctionSymbol> _methods = [];
    private ImmutableArray<InterfacePropertySymbol> _properties = [];
    private ImmutableArray<InterfaceIndexerSymbol> _indexers = [];
    private ImmutableArray<GenericParameterSymbol> _typeParameters = [];
    private Dictionary<FunctionSymbol, int> _methodSlots = [];

    internal InterfaceTypeSymbol(string name, NamespaceSymbol containingNamespace, InterfaceDeclarationSyntax declaration)
        : base(name, containingNamespace, "interface", origin: SymbolOrigin.Source(declaration),
            documentation: SymbolDocumentation.FromDeclaration(declaration),
            accessibility: AccessibilityFacts.FromSyntax(declaration.AccessModifierToken,
                declaration.SecondaryAccessModifierToken, Accessibility.Public))
    {
        Declaration = declaration;
        SetImplementation(new SourceSymbolImplementation(declaration));
    }

    internal InterfaceTypeSymbol(string name, NamespaceSymbol containingNamespace,
        SymbolOrigin? origin = null, SymbolDocumentation? documentation = null,
        Accessibility accessibility = Accessibility.Public)
        : base(name, containingNamespace, "interface", origin: origin, documentation: documentation,
            accessibility: accessibility) { }

    public override IEnumerable<Symbol> GetMembers() => Methods.Cast<Symbol>().Concat(Properties).Concat(Indexers);
    public override string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short)
    {
        string name = base.ToDisplayString(format);
        return TypeParameters.IsEmpty ? name : $"{name}<{string.Join(", ", TypeParameters.Select(parameter => parameter.Name))}>";
    }
    public override IEnumerable<Symbol> LookupMembers(string name) =>
        SelfAndBaseInterfaces.SelectMany(type => type.GetMembers()).Where(member => member.Name == name).Distinct();
    public ImmutableArray<InterfaceTypeSymbol> BaseInterfaces { get; private set; } = [];
    public ImmutableArray<FunctionSymbol> Methods => _methods;
    public ImmutableArray<InterfacePropertySymbol> Properties => _properties;
    public ImmutableArray<InterfaceIndexerSymbol> Indexers => _indexers;
    public ImmutableArray<GenericParameterSymbol> TypeParameters => _typeParameters;
    public override int GenericArity => GenericDefinition?.GenericArity ?? _typeParameters.Length;
    public bool IsGenericDefinition => !_typeParameters.IsEmpty;
    public InterfaceTypeSymbol? GenericDefinition { get; private set; }
    public ImmutableArray<TypeSymbol> TypeArguments { get; private set; } = [];
    public bool IsGenericSpecialization => GenericDefinition is not null;
    public bool IsOpenGenericType => IsGenericDefinition ||
        IsGenericSpecialization && TypeArguments.Any(global::Xenon.Compiler.Semantics.GenericTypeFacts.ContainsGenericParameter);
    public bool IsConcreteType => !IsOpenGenericType;
    public override bool IsCompilerGenerated => IsGenericSpecialization;
    public override bool IsUserVisible => !IsGenericSpecialization;
    internal InterfaceDeclarationSyntax Declaration { get; } = null!;

    internal void SetBaseInterfaces(ImmutableArray<InterfaceTypeSymbol> interfaces) => BaseInterfaces = interfaces;
    internal void SetMethods(ImmutableArray<FunctionSymbol> methods) => _methods = methods;
    internal void SetProperties(ImmutableArray<InterfacePropertySymbol> properties) => _properties = properties;
    internal void SetIndexers(ImmutableArray<InterfaceIndexerSymbol> indexers) => _indexers = indexers;
    internal void SetTypeParameters(ImmutableArray<GenericParameterSymbol> parameters)
    {
        _typeParameters = parameters;
        foreach (GenericParameterSymbol parameter in parameters) parameter.SetDeclaringSymbol(this);
    }
    internal void SetGenericSpecialization(InterfaceTypeSymbol definition, ImmutableArray<TypeSymbol> typeArguments)
    {
        GenericDefinition = definition;
        TypeArguments = typeArguments;
    }
    internal void SetMethodSlots(IEnumerable<FunctionSymbol> methods) =>
        _methodSlots = methods.Select((method, slot) => (method, slot)).ToDictionary(pair => pair.method, pair => pair.slot);

    public ImmutableArray<FunctionSymbol> AllMethods
    {
        get
        {
            var seen = new HashSet<FunctionSymbol>(ReferenceEqualityComparer.Instance);
            return BaseInterfaces.SelectMany(@interface => @interface.AllMethods)
                .Concat(_methods)
                .Concat(_properties.SelectMany(property => new[] { property.Getter, property.Setter }.OfType<FunctionSymbol>()))
                .Concat(_indexers.SelectMany(indexer => new[] { indexer.Getter, indexer.Setter }.OfType<FunctionSymbol>()))
                .Where(seen.Add)
                .ToImmutableArray();
        }
    }

    public int GetMethodSlot(FunctionSymbol method) => _methodSlots.TryGetValue(method, out int slot)
        ? slot
        : throw new InvalidOperationException($"method '{method.Name}' does not belong to interface '{FullName}'");
    public IEnumerable<FunctionSymbol> FindMethods(string name) =>
        SelfAndBaseInterfaces.SelectMany(type => type.Methods)
            .Where(method => method.Name == name)
            .OrderBy(method => method.FullName, StringComparer.Ordinal)
            .DistinctBy(TypeSignature.Method);

    public FunctionSymbol? FindMethod(string name)
    {
        FunctionSymbol[] candidates = FindMethods(name).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal IEnumerable<InterfacePropertySymbol> AllProperties =>
        SelfAndBaseInterfaces.SelectMany(type => type.Properties);

    public InterfacePropertySymbol? FindProperty(string name) =>
        AllProperties.Where(property => property.Name == name)
            .OrderBy(property => property.ContainingInterface.FullName, StringComparer.Ordinal).FirstOrDefault();
    public IEnumerable<InterfaceIndexerSymbol> AllIndexers =>
        SelfAndBaseInterfaces.SelectMany(type => type.Indexers)
            .OrderBy(indexer => indexer.ContainingInterface.FullName, StringComparer.Ordinal)
            .DistinctBy(indexer => TypeSignature.Indexer(indexer.Parameters, indexer.IsReadonly));

    public bool IsOrInherits(InterfaceTypeSymbol target) =>
        TypeIdentity.AreSame(this, target) || BaseInterfaces.Any(@interface => @interface.IsOrInherits(target));

    public IEnumerable<InterfaceTypeSymbol> SelfAndBaseInterfaces =>
        BaseInterfaces.SelectMany(@interface => @interface.SelfAndBaseInterfaces).Append(this).Distinct();
}

public sealed class InterfaceIndexerSymbol : Symbol
{
    internal InterfaceIndexerSymbol(
        InterfaceTypeSymbol containingInterface,
        TypeSymbol type,
        ImmutableArray<ParameterSymbol> parameters,
        InterfaceIndexerDeclarationSyntax declaration)
        : base("this", SymbolKind.Property, containingInterface)
    {
        Type = type;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        IsReadonly = declaration.IsReadonly;
        SetSourceOrigin(declaration);
        foreach (ParameterSymbol parameter in Parameters)
            if (Documentation.Parameters.TryGetValue(parameter.Name, out string? text))
                parameter.SetMetadata(parameter.Origin,
                    SymbolDocumentation.Empty with { Summary = text });
    }

    internal InterfaceIndexerSymbol(InterfaceTypeSymbol containingInterface, TypeSymbol type,
        ImmutableArray<ParameterSymbol> parameters, bool isReadonly,
        SymbolOrigin? origin = null, SymbolDocumentation? documentation = null)
        : base("this", SymbolKind.Property, containingInterface)
    {
        Type = type;
        Parameters = ParameterSymbol.Own(parameters, this);
        IsReadonly = isReadonly;
        SetMetadata(origin ?? SymbolOrigin.CompilerGenerated, documentation);
    }

    public InterfaceTypeSymbol ContainingInterface => GetContainingSymbol<InterfaceTypeSymbol>()!;
    public TypeSymbol Type { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; }
    public bool IsReadonly { get; }
    public FunctionSymbol? Getter { get; private set; }
    public FunctionSymbol? Setter { get; private set; }
    public InterfaceIndexerSymbol? GenericDefinition { get; private set; }
    internal InterfaceIndexerDeclarationSyntax Declaration { get; } = null!;
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Origin.Kind != SymbolOriginKind.Source ? base.DeclaringSyntaxReferences : [new(Declaration)];
    public override bool HasUserEditableIdentifier => false;

    internal void SetAccessors(FunctionSymbol? getter, FunctionSymbol? setter)
    {
        Getter = getter;
        Setter = setter;
    }

    internal void SetGenericSpecialization(InterfaceIndexerSymbol definition) =>
        GenericDefinition = definition;

    internal string GetAccessorName(bool getter) =>
        IndexerSymbol.CreateAccessorName(getter ? "get_Item" : "set_Item", Parameters);
}

public sealed class InterfacePropertySymbol : Symbol
{
    internal InterfacePropertySymbol(
        string name,
        InterfaceTypeSymbol containingInterface,
        TypeSymbol type,
        InterfacePropertyDeclarationSyntax declaration)
        : base(name, SymbolKind.Property, containingInterface)
    {
        Type = type;
        Declaration = declaration;
        IsReadonly = declaration.IsReadonly;
        SetSourceOrigin(declaration);
    }

    internal InterfacePropertySymbol(string name, InterfaceTypeSymbol containingInterface, TypeSymbol type,
        bool isReadonly, SymbolOrigin? origin = null, SymbolDocumentation? documentation = null)
        : base(name, SymbolKind.Property, containingInterface)
    {
        Type = type;
        IsReadonly = isReadonly;
        SetMetadata(origin ?? SymbolOrigin.CompilerGenerated, documentation);
    }

    public InterfaceTypeSymbol ContainingInterface => GetContainingSymbol<InterfaceTypeSymbol>()!;
    public TypeSymbol Type { get; }
    public bool IsReadonly { get; }
    public FunctionSymbol? Getter { get; private set; }
    public FunctionSymbol? Setter { get; private set; }
    public InterfacePropertySymbol? GenericDefinition { get; private set; }
    internal InterfacePropertyDeclarationSyntax Declaration { get; } = null!;
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Origin.Kind != SymbolOriginKind.Source ? base.DeclaringSyntaxReferences : [new(Declaration)];

    internal void SetAccessors(FunctionSymbol? getter, FunctionSymbol? setter)
    {
        Getter = getter;
        Setter = setter;
    }

    internal void SetGenericSpecialization(InterfacePropertySymbol definition) =>
        GenericDefinition = definition;
}
