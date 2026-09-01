using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class NamespaceSymbol : Symbol
{
    private readonly Dictionary<string, NamespaceSymbol> _namespaces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<FunctionSymbol>> _functions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DeclaredTypeSymbol>> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ConstantSymbol>> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<TemplateSymbol>> _templates = new(StringComparer.Ordinal);

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

    public IReadOnlyCollection<FunctionSymbol> Functions => _functions.Values.SelectMany(items => items).ToArray();

    public IReadOnlyCollection<DeclaredTypeSymbol> Types => _types.Values.SelectMany(items => items).ToArray();

    public IReadOnlyCollection<StructTypeSymbol> Structs => Types.OfType<StructTypeSymbol>().ToArray();

    public IReadOnlyCollection<InterfaceTypeSymbol> Interfaces => Types.OfType<InterfaceTypeSymbol>().ToArray();
    public IReadOnlyCollection<ConstantSymbol> Constants => _constants.Values.SelectMany(items => items).ToArray();
    public IReadOnlyCollection<EnumTypeSymbol> Enums => Types.OfType<EnumTypeSymbol>().ToArray();
    public IReadOnlyCollection<TemplateSymbol> Templates => _templates.Values.SelectMany(items => items).ToArray();

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

    /// <summary>Builds a compilation-local namespace facade over public symbols from a referenced snapshot.</summary>
    internal void ImportPublicMembers(NamespaceSymbol source)
    {
        foreach (NamespaceSymbol child in source.Namespaces.OrderBy(item => item.Name, StringComparer.Ordinal))
            GetOrAddNamespace(child.Name).ImportPublicMembers(child);
        foreach (DeclaredTypeSymbol type in source.Types.OrderBy(item => item.Name, StringComparer.Ordinal))
            AddCandidate(_types, type.Name, type);
        foreach (FunctionSymbol function in source.Functions.Where(item => item.IsPublic)
            .OrderBy(item => item.Name, StringComparer.Ordinal))
            AddCandidate(_functions, function.Name, function);
        foreach (ConstantSymbol constant in source.Constants.OrderBy(item => item.Name, StringComparer.Ordinal))
            AddCandidate(_constants, constant.Name, constant);
        foreach (TemplateSymbol template in source.Templates.OrderBy(item => item.Name, StringComparer.Ordinal))
            AddCandidate(_templates, template.Name, template);
    }

    internal bool TryDeclareFunction(FunctionSymbol function) =>
        TryDeclare(_functions, function.Name, function);

    internal FunctionSymbol? FindFunction(string name) =>
        FindSingle(_functions, name);
    internal IReadOnlyList<FunctionSymbol> FindFunctions(string name) =>
        _functions.GetValueOrDefault(name) ?? [];
    internal bool TryDeclareConstant(ConstantSymbol constant) =>
        TryDeclare(_constants, constant.Name, constant);
    internal ConstantSymbol? FindConstant(string name) => FindSingle(_constants, name);
    internal IReadOnlyList<ConstantSymbol> FindConstants(string name) => _constants.GetValueOrDefault(name) ?? [];

    internal bool TryDeclareType(DeclaredTypeSymbol type) => !_templates.ContainsKey(type.Name) && TryDeclare(_types, type.Name, type);

    internal bool TryDeclareTemplate(TemplateSymbol template) =>
        !_types.ContainsKey(template.Name) && TryDeclare(_templates, template.Name, template);

    internal TemplateSymbol? FindTemplate(string name) => FindSingle(_templates, name);
    internal IReadOnlyList<TemplateSymbol> FindTemplates(string name) => _templates.GetValueOrDefault(name) ?? [];

    internal DeclaredTypeSymbol? FindAnyType(string name) => FindSingle(_types, name);
    internal IReadOnlyList<DeclaredTypeSymbol> FindTypes(string name) => _types.GetValueOrDefault(name) ?? [];

    private static void AddCandidate<T>(Dictionary<string, List<T>> dictionary, string name, T value)
    {
        if (!dictionary.TryGetValue(name, out List<T>? candidates))
            dictionary.Add(name, [value]);
        else if (!candidates.Contains(value))
            candidates.Add(value);
    }

    private static bool TryDeclare<T>(Dictionary<string, List<T>> dictionary, string name, T value)
    {
        if (dictionary.ContainsKey(name)) return false;
        dictionary.Add(name, [value]);
        return true;
    }

    private static T? FindSingle<T>(Dictionary<string, List<T>> dictionary, string name) where T : class =>
        dictionary.TryGetValue(name, out List<T>? candidates) && candidates.Count == 1 ? candidates[0] : null;
}
