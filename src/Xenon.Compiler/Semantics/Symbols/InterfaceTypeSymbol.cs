using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class InterfaceTypeSymbol : TypeSymbol
{
    private ImmutableArray<FunctionSymbol> _methods = [];
    private ImmutableArray<InterfacePropertySymbol> _properties = [];
    private ImmutableArray<InterfaceIndexerSymbol> _indexers = [];
    private Dictionary<FunctionSymbol, int> _methodSlots = [];

    internal InterfaceTypeSymbol(string name, NamespaceSymbol containingNamespace, InterfaceDeclarationSyntax declaration)
        : base(name)
    {
        ContainingNamespace = containingNamespace;
        Declaration = declaration;
    }

    public NamespaceSymbol ContainingNamespace { get; }
    public string FullName => $"{ContainingNamespace.FullName}.{Name}";
    public ImmutableArray<InterfaceTypeSymbol> BaseInterfaces { get; private set; } = [];
    public ImmutableArray<FunctionSymbol> Methods => _methods;
    public ImmutableArray<InterfacePropertySymbol> Properties => _properties;
    public ImmutableArray<InterfaceIndexerSymbol> Indexers => _indexers;
    public int DispatchId { get; private set; }
    internal InterfaceDeclarationSyntax Declaration { get; }

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
    public FunctionSymbol? FindMethod(string name) => _methods.FirstOrDefault(m => m.Name == name) ?? BaseInterfaces.Select(i => i.FindMethod(name)).FirstOrDefault(m => m is not null);
    public InterfacePropertySymbol? FindProperty(string name) =>
        _properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal)) ??
        BaseInterfaces.Select(@interface => @interface.FindProperty(name)).FirstOrDefault(property => property is not null);
    public IEnumerable<InterfaceIndexerSymbol> AllIndexers =>
        _indexers.Concat(BaseInterfaces.SelectMany(@interface => @interface.AllIndexers));

    public bool IsOrInherits(InterfaceTypeSymbol target) =>
        ReferenceEquals(this, target) || BaseInterfaces.Any(@interface => @interface.IsOrInherits(target));

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
        : base("this", SymbolKind.Property)
    {
        ContainingInterface = containingInterface;
        Type = type;
        Parameters = parameters;
        Declaration = declaration;
    }

    public InterfaceTypeSymbol ContainingInterface { get; }
    public TypeSymbol Type { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; }
    public FunctionSymbol? Getter { get; private set; }
    public FunctionSymbol? Setter { get; private set; }
    internal InterfaceIndexerDeclarationSyntax Declaration { get; }

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
        : base(name, SymbolKind.Property)
    {
        ContainingInterface = containingInterface;
        Type = type;
        Declaration = declaration;
    }

    public InterfaceTypeSymbol ContainingInterface { get; }
    public TypeSymbol Type { get; }
    public FunctionSymbol? Getter { get; private set; }
    public FunctionSymbol? Setter { get; private set; }
    internal InterfacePropertyDeclarationSyntax Declaration { get; }

    internal void SetAccessors(FunctionSymbol? getter, FunctionSymbol? setter)
    {
        Getter = getter;
        Setter = setter;
    }
}
