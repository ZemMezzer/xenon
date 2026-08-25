using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class FunctionSymbol : Symbol
{
    internal FunctionSymbol(
        string name,
        NamespaceSymbol containingNamespace,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        FunctionDeclarationSyntax declaration)
        : base(name, SymbolKind.Function)
    {
        ContainingNamespace = containingNamespace;
        ReturnType = returnType;
        Parameters = parameters;
        Declaration = declaration;
    }

    public NamespaceSymbol ContainingNamespace { get; }

    public string FullName => $"{ContainingNamespace.FullName}.{Name}";

    public TypeSymbol ReturnType { get; }

    public ImmutableArray<ParameterSymbol> Parameters { get; }

    public bool IsExtern => Declaration.IsExtern;

    public bool IsExport => Declaration.IsExport;

    internal FunctionDeclarationSyntax Declaration { get; }
}

public abstract class VariableSymbol : Symbol
{
    protected VariableSymbol(string name, SymbolKind kind, TypeSymbol type)
        : base(name, kind)
    {
        Type = type;
    }

    public TypeSymbol Type { get; }
}

public sealed class ParameterSymbol : VariableSymbol
{
    internal ParameterSymbol(string name, TypeSymbol type, int ordinal)
        : base(name, SymbolKind.Parameter, type)
    {
        Ordinal = ordinal;
    }

    public int Ordinal { get; }
}

public sealed class LocalVariableSymbol : VariableSymbol
{
    internal LocalVariableSymbol(string name, TypeSymbol type)
        : base(name, SymbolKind.LocalVariable, type)
    {
    }
}
