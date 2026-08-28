using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class NamespaceSymbol : Symbol
{
    private readonly Dictionary<string, NamespaceSymbol> _namespaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionSymbol> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeclaredTypeSymbol> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConstantSymbol> _constants = new(StringComparer.Ordinal);

    internal NamespaceSymbol(string name, NamespaceSymbol? parent)
        : base(name, SymbolKind.Namespace, parent)
    {
    }

    private ImmutableArray<SyntaxReference> _declarations = [];
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => _declarations;

    internal void AddDeclaration(NamespaceDeclarationSyntax declaration, int partIndex) =>
        _declarations = _declarations.Add(new SyntaxReference(declaration, partIndex));

    public NamespaceSymbol? Parent => ContainingSymbol as NamespaceSymbol;

    public string FullName => QualifiedName;

    public IReadOnlyCollection<NamespaceSymbol> Namespaces => _namespaces.Values;

    public IReadOnlyCollection<FunctionSymbol> Functions => _functions.Values;

    public IReadOnlyCollection<DeclaredTypeSymbol> Types => _types.Values;

    public IReadOnlyCollection<StructTypeSymbol> Structs => _types.Values.OfType<StructTypeSymbol>().ToArray();

    public IReadOnlyCollection<InterfaceTypeSymbol> Interfaces => _types.Values.OfType<InterfaceTypeSymbol>().ToArray();
    public IReadOnlyCollection<ConstantSymbol> Constants => _constants.Values;
    public IReadOnlyCollection<EnumTypeSymbol> Enums => _types.Values.OfType<EnumTypeSymbol>().ToArray();

    internal NamespaceSymbol? FindNamespace(string name) => _namespaces.GetValueOrDefault(name);

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
    internal bool TryDeclareConstant(ConstantSymbol constant) => _constants.TryAdd(constant.Name, constant);
    internal ConstantSymbol? FindConstant(string name) => _constants.GetValueOrDefault(name);

    internal bool TryDeclareType(DeclaredTypeSymbol type) => _types.TryAdd(type.Name, type);

    internal DeclaredTypeSymbol? FindAnyType(string name) => _types.GetValueOrDefault(name);
}
