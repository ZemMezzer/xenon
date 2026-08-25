using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class StructTypeSymbol : TypeSymbol
{
    private ImmutableArray<FieldSymbol> _fields = [];

    internal StructTypeSymbol(
        string name,
        NamespaceSymbol containingNamespace,
        StructDeclarationSyntax declaration)
        : base(name)
    {
        ContainingNamespace = containingNamespace;
        Declaration = declaration;
    }

    public NamespaceSymbol ContainingNamespace { get; }

    public string FullName => $"{ContainingNamespace.FullName}.{Name}";

    public ImmutableArray<FieldSymbol> Fields => _fields;

    internal StructDeclarationSyntax Declaration { get; }

    internal void SetFields(ImmutableArray<FieldSymbol> fields)
    {
        if (!_fields.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException($"fields for struct '{FullName}' are already defined");
        }

        _fields = fields;
    }

    public FieldSymbol? FindField(string name) =>
        _fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));
}

public sealed class FieldSymbol : Symbol
{
    internal FieldSymbol(
        string name,
        StructTypeSymbol containingType,
        TypeSymbol type,
        int ordinal,
        FieldDeclarationSyntax declaration)
        : base(name, SymbolKind.Field)
    {
        ContainingType = containingType;
        Type = type;
        Ordinal = ordinal;
        Declaration = declaration;
    }

    public StructTypeSymbol ContainingType { get; }

    public TypeSymbol Type { get; }

    public int Ordinal { get; }

    internal FieldDeclarationSyntax Declaration { get; }
}
