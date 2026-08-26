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
        InterfaceTypeSymbol containingInterface,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        InterfaceMethodDeclarationSyntax declaration)
        : base(name, SymbolKind.Function)
    {
        FunctionKind = FunctionKind.Method;
        ContainingInterface = containingInterface;
        ContainingNamespace = containingInterface.ContainingNamespace;
        ReturnType = returnType;
        Parameters = parameters;
        Declaration = declaration;
        Accessibility = Accessibility.Public;
        IsAbstract = true;
        IsReadonly = declaration.IsReadonly;
    }

    internal FunctionSymbol(
        string name,
        InterfacePropertySymbol containingProperty,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        PropertyAccessorDeclarationSyntax declaration)
        : base(name, SymbolKind.Function)
    {
        FunctionKind = FunctionKind.Method;
        ContainingInterface = containingProperty.ContainingInterface;
        ContainingInterfaceProperty = containingProperty;
        ContainingNamespace = containingProperty.ContainingInterface.ContainingNamespace;
        ReturnType = returnType;
        Parameters = parameters;
        Declaration = declaration;
        Accessibility = Accessibility.Public;
        IsAbstract = true;
        IsReadonly = declaration.IsGetter && containingProperty.Declaration.IsReadonly;
    }

    internal FunctionSymbol(
        string name,
        InterfaceIndexerSymbol containingIndexer,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        PropertyAccessorDeclarationSyntax declaration)
        : base(name, SymbolKind.Function)
    {
        FunctionKind = FunctionKind.Method;
        ContainingInterface = containingIndexer.ContainingInterface;
        ContainingInterfaceIndexer = containingIndexer;
        ContainingNamespace = containingIndexer.ContainingInterface.ContainingNamespace;
        ReturnType = returnType;
        Parameters = parameters;
        Declaration = declaration;
        Accessibility = Accessibility.Public;
        IsAbstract = true;
        IsReadonly = declaration.IsGetter && containingIndexer.Declaration.IsReadonly;
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
        IsStatic = declaration.IsStatic;
        IsVirtual = declaration.IsVirtual;
        IsOverride = declaration.IsOverride;
        IsAbstract = declaration.IsAbstract;
        IsReadonly = declaration.IsReadonly;
    }

    internal FunctionSymbol(
        string name,
        PropertySymbol containingProperty,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        PropertyAccessorDeclarationSyntax declaration)
        : base(name, SymbolKind.Function)
    {
        PropertyDeclarationSyntax property = containingProperty.Declaration;
        FunctionKind = FunctionKind.Method;
        ContainingType = containingProperty.ContainingType;
        ContainingProperty = containingProperty;
        ContainingNamespace = containingProperty.ContainingType.ContainingNamespace;
        ReturnType = returnType;
        Parameters = parameters;
        Declaration = declaration;
        Accessibility = property.IsPublic ? Accessibility.Public : Accessibility.Private;
        IsStatic = property.IsStatic;
        IsVirtual = property.IsVirtual;
        IsOverride = property.IsOverride;
        IsAbstract = property.IsAbstract;
        IsReadonly = declaration.IsGetter && property.IsReadonly;
    }

    internal FunctionSymbol(
        string name,
        IndexerSymbol containingIndexer,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        PropertyAccessorDeclarationSyntax declaration)
        : base(name, SymbolKind.Function)
    {
        IndexerDeclarationSyntax indexer = containingIndexer.Declaration;
        FunctionKind = FunctionKind.Method;
        ContainingType = containingIndexer.ContainingType;
        ContainingIndexer = containingIndexer;
        ContainingNamespace = containingIndexer.ContainingType.ContainingNamespace;
        ReturnType = returnType;
        Parameters = parameters;
        Declaration = declaration;
        Accessibility = indexer.IsPublic ? Accessibility.Public : Accessibility.Private;
        IsStatic = indexer.IsStatic;
        IsVirtual = indexer.IsVirtual;
        IsOverride = indexer.IsOverride;
        IsAbstract = indexer.IsAbstract;
        IsReadonly = declaration.IsGetter && indexer.IsReadonly;
    }

    internal FunctionSymbol(
        FunctionKind functionKind,
        StructTypeSymbol containingType,
        ImmutableArray<ParameterSymbol> parameters,
        SyntaxNode declaration,
        Accessibility accessibility)
        : base(functionKind switch
        {
            FunctionKind.Constructor => containingType.Name,
            FunctionKind.InstanceInitializer => "__init_fields",
            FunctionKind.Destructor => $"~{containingType.Name}",
            _ => throw new ArgumentOutOfRangeException(nameof(functionKind)),
        }, SymbolKind.Function)
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
        IsVirtual = declaration is DestructorDeclarationSyntax { IsVirtual: true };
    }

    public NamespaceSymbol ContainingNamespace { get; }

    public StructTypeSymbol? ContainingType { get; }
    public InterfaceTypeSymbol? ContainingInterface { get; }
    public PropertySymbol? ContainingProperty { get; }
    public InterfacePropertySymbol? ContainingInterfaceProperty { get; }
    public IndexerSymbol? ContainingIndexer { get; }
    public InterfaceIndexerSymbol? ContainingInterfaceIndexer { get; }

    public string FullName => FunctionKind switch
    {
        FunctionKind.Method when ContainingType is not null => IsReadonly
            ? $"{ContainingType.FullName}.{Name}.__readonly"
            : $"{ContainingType.FullName}.{Name}",
        FunctionKind.Method => $"{ContainingInterface!.FullName}.{Name}",
        FunctionKind.Constructor => ConstructorOverloadCount == 1 ? $"{ContainingType!.FullName}.__ctor" : $"{ContainingType!.FullName}.__ctor.{ConstructorOverload}",
        FunctionKind.InstanceInitializer => $"{ContainingType!.FullName}.__init_fields",
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

    public bool HasImplicitThis => ContainingType is not null && !IsStatic;

    public bool IsStatic { get; }
    public bool IsVirtual { get; }
    public bool IsOverride { get; }
    public bool IsAbstract { get; }
    public bool IsReadonly { get; }

    public int? VTableSlot { get; private set; }
    public int ConstructorOverload { get; private set; }
    public int ConstructorOverloadCount { get; private set; } = 1;

    internal void SetVTableSlot(int slot) => VTableSlot = slot;
    internal void SetConstructorOverload(int index, int count)
    {
        ConstructorOverload = index;
        ConstructorOverloadCount = count;
    }

    public bool Overrides(FunctionSymbol candidate) =>
        string.Equals(Name, candidate.Name, StringComparison.Ordinal) &&
        IsReadonly == candidate.IsReadonly &&
        ReferenceEquals(ReturnType, candidate.ReturnType) &&
        Parameters.Length == candidate.Parameters.Length &&
        Parameters.Zip(candidate.Parameters).All(pair => ReferenceEquals(pair.First.Type, pair.Second.Type));

    internal SyntaxNode Declaration { get; }
}

public abstract class VariableSymbol : Symbol
{
    protected VariableSymbol(string name, SymbolKind kind, TypeSymbol type, bool isReadonly = false)
        : base(name, kind)
    {
        Type = type;
        IsReadonly = isReadonly;
    }

    public TypeSymbol Type { get; }
    public bool IsReadonly { get; }
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
    internal LocalVariableSymbol(string name, TypeSymbol type, bool isReadonly = false)
        : base(name, SymbolKind.LocalVariable, type, isReadonly)
    {
    }

    public ArrayStorageKind ArrayStorage { get; internal set; }
}
