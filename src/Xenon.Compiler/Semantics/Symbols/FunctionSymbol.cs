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
        Accessibility = declaration.IsPublic ? Accessibility.Public : Accessibility.Private;
        FunctionKind = FunctionKind.Ordinary;
    }

    internal FunctionSymbol(
        string name,
        StructTypeSymbol containingType,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        MethodDeclarationSyntax declaration)
        : base(name, SymbolKind.Function)
    {
        FunctionKind = FunctionKind.Method;
        ContainingType = containingType;
        ContainingNamespace = containingType.ContainingNamespace;
        ReturnType = returnType;
        Parameters = parameters;
        Declaration = declaration;
        Accessibility = declaration.IsPublic ? Accessibility.Public : Accessibility.Private;
    }

    internal FunctionSymbol(
        FunctionKind functionKind,
        StructTypeSymbol containingType,
        ImmutableArray<ParameterSymbol> parameters,
        SyntaxNode declaration,
        Accessibility accessibility)
        : base(functionKind == FunctionKind.Constructor ? containingType.Name : $"~{containingType.Name}", SymbolKind.Function)
    {
        if (functionKind is FunctionKind.Ordinary or FunctionKind.Method)
        {
            throw new ArgumentOutOfRangeException(nameof(functionKind));
        }

        FunctionKind = functionKind;
        ContainingType = containingType;
        ContainingNamespace = containingType.ContainingNamespace;
        ReturnType = BuiltinTypes.Void;
        Parameters = parameters;
        Declaration = declaration;
        Accessibility = accessibility;
    }

    public NamespaceSymbol ContainingNamespace { get; }

    public StructTypeSymbol? ContainingType { get; }

    public string FullName => FunctionKind switch
    {
        FunctionKind.Method => $"{ContainingType!.FullName}.{Name}",
        FunctionKind.Constructor => $"{ContainingType!.FullName}.__ctor",
        FunctionKind.Destructor => $"{ContainingType!.FullName}.__dtor",
        _ => $"{ContainingNamespace.FullName}.{Name}",
    };

    public TypeSymbol ReturnType { get; }

    public ImmutableArray<ParameterSymbol> Parameters { get; }

    public FunctionKind FunctionKind { get; }

    public Accessibility Accessibility { get; }

    public bool IsExtern => Declaration is FunctionDeclarationSyntax { IsExtern: true };

    public bool IsExport => Declaration is FunctionDeclarationSyntax { IsExport: true };

    public bool IsPublic => Accessibility == Accessibility.Public;

    public bool HasImplicitThis => ContainingType is not null;

    internal SyntaxNode Declaration { get; }
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

    public ArrayStorageKind ArrayStorage { get; internal set; }
}
