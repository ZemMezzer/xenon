namespace Xenon.Compiler.Semantics.Symbols;

public sealed class NamespaceSymbol : Symbol
{
    private readonly Dictionary<string, NamespaceSymbol> _namespaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionSymbol> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StructTypeSymbol> _types = new(StringComparer.Ordinal);

    internal NamespaceSymbol(string name, NamespaceSymbol? parent)
        : base(name, SymbolKind.Namespace)
    {
        Parent = parent;
    }

    public NamespaceSymbol? Parent { get; }

    public string FullName => Parent is null || string.IsNullOrEmpty(Parent.FullName)
        ? Name
        : $"{Parent.FullName}.{Name}";

    public IReadOnlyCollection<NamespaceSymbol> Namespaces => _namespaces.Values;

    public IReadOnlyCollection<FunctionSymbol> Functions => _functions.Values;

    public IReadOnlyCollection<StructTypeSymbol> Types => _types.Values;

    internal NamespaceSymbol GetOrAddNamespace(string name)
    {
        if (!_namespaces.TryGetValue(name, out NamespaceSymbol? @namespace))
        {
            @namespace = new NamespaceSymbol(name, this);
            _namespaces.Add(name, @namespace);
        }

        return @namespace;
    }

    internal bool TryDeclareFunction(FunctionSymbol function) => _functions.TryAdd(function.Name, function);

    internal FunctionSymbol? FindFunction(string name) => _functions.GetValueOrDefault(name);

    internal bool TryDeclareType(StructTypeSymbol type) => _types.TryAdd(type.Name, type);

    internal StructTypeSymbol? FindType(string name) => _types.GetValueOrDefault(name);
}
