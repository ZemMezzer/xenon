using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class InterfaceTypeSymbol : DeclaredTypeSymbol
{
    private ImmutableArray<FunctionSymbol> _methods = [];
    private ImmutableArray<InterfacePropertySymbol> _properties = [];
    private ImmutableArray<InterfaceIndexerSymbol> _indexers = [];
    private Dictionary<FunctionSymbol, int> _methodSlots = [];

    internal InterfaceTypeSymbol(string name, NamespaceSymbol containingNamespace, InterfaceDeclarationSyntax declaration)
        : base(name, containingNamespace)
    {
        Declaration = declaration;
    }

    public override string DeclarationKind => "interface";
    public override IEnumerable<Symbol> GetMembers() => Methods.Cast<Symbol>().Concat(Properties).Concat(Indexers);
    public override IEnumerable<Symbol> LookupMembers(string name) =>
        SelfAndBaseInterfaces.SelectMany(type => type.GetMembers()).Where(member => member.Name == name).Distinct();
    public ImmutableArray<InterfaceTypeSymbol> BaseInterfaces { get; private set; } = [];
    public ImmutableArray<FunctionSymbol> Methods => _methods;
    public ImmutableArray<InterfacePropertySymbol> Properties => _properties;
    public ImmutableArray<InterfaceIndexerSymbol> Indexers => _indexers;
    public int DispatchId { get; private set; }
    public override InterfaceDeclarationSyntax Declaration { get; }

    internal void SetBaseInterfaces(ImmutableArray<InterfaceTypeSymbol> interfaces) => BaseInterfaces = interfaces;
    internal void SetMethods(ImmutableArray<FunctionSymbol> methods) => _methods = methods;
    internal void SetProperties(ImmutableArray<InterfacePropertySymbol> properties) => _properties = properties;
    internal void SetIndexers(ImmutableArray<InterfaceIndexerSymbol> indexers) => _indexers = indexers;
    internal void SetDispatchId(int dispatchId) => DispatchId = dispatchId;

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
            .DistinctBy(indexer => TypeSignature.Parameters(indexer.Parameters));

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
    }

    public InterfaceTypeSymbol ContainingInterface => GetContainingSymbol<InterfaceTypeSymbol>()!;
    public TypeSymbol Type { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; }
    public FunctionSymbol? Getter { get; private set; }
    public FunctionSymbol? Setter { get; private set; }
    internal InterfaceIndexerDeclarationSyntax Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => [new(Declaration)];

    internal void SetAccessors(FunctionSymbol? getter, FunctionSymbol? setter)
    {
        Getter = getter;
        Setter = setter;
    }

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
    }

    public InterfaceTypeSymbol ContainingInterface => GetContainingSymbol<InterfaceTypeSymbol>()!;
    public TypeSymbol Type { get; }
    public FunctionSymbol? Getter { get; private set; }
    public FunctionSymbol? Setter { get; private set; }
    internal InterfacePropertyDeclarationSyntax Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => [new(Declaration)];

    internal void SetAccessors(FunctionSymbol? getter, FunctionSymbol? setter)
    {
        Getter = getter;
        Setter = setter;
    }
}
