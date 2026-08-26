using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class StructTypeSymbol : TypeSymbol
{
    private ImmutableArray<FieldSymbol> _fields = [];
    private ImmutableArray<FieldSymbol> _staticFields = [];
    private ImmutableArray<FunctionSymbol> _methods = [];
    private ImmutableArray<FunctionSymbol> _constructors = [];
    private ImmutableArray<FunctionSymbol> _virtualMethods = [];

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

    public ImmutableArray<FieldSymbol> StaticFields => _staticFields;

    public ImmutableArray<FieldSymbol> AllInstanceFields => BaseType is null ? _fields : BaseType.AllInstanceFields.AddRange(_fields);

    public ImmutableArray<FunctionSymbol> Methods => _methods;
    public ImmutableArray<FunctionSymbol> VirtualMethods => _virtualMethods;

    public FunctionSymbol? Constructor { get; private set; }
    public ImmutableArray<FunctionSymbol> Constructors => _constructors;

    public FunctionSymbol? Destructor { get; private set; }

    public StructTypeSymbol? BaseType { get; private set; }

    public ImmutableArray<InterfaceTypeSymbol> Interfaces { get; private set; } = [];

    public bool HasVirtualDispatch { get; private set; }

    public bool IsAbstract => _virtualMethods.Any(method => method.IsAbstract);

    internal StructDeclarationSyntax Declaration { get; }

    internal void SetFields(ImmutableArray<FieldSymbol> fields)
    {
        if (!_fields.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException($"fields for struct '{FullName}' are already defined");
        }

        _fields = fields;
    }

    internal void SetStaticFields(ImmutableArray<FieldSymbol> fields) => _staticFields = fields;

    internal void SetBaseType(StructTypeSymbol baseType) => BaseType = baseType;
    internal void ClearBaseType() => BaseType = null;

    internal void SetInterfaces(ImmutableArray<InterfaceTypeSymbol> interfaces) => Interfaces = interfaces;

    internal void SetHasVirtualDispatch() => HasVirtualDispatch = true;

    internal void SetVirtualMethods(ImmutableArray<FunctionSymbol> methods) => _virtualMethods = methods;

    internal void SetMethods(ImmutableArray<FunctionSymbol> methods)
    {
        if (!_methods.IsDefaultOrEmpty)
        {
            throw new InvalidOperationException($"methods for struct '{FullName}' are already defined");
        }

        _methods = methods;
    }

    internal void SetConstructor(FunctionSymbol constructor)
    {
        if (Constructor is not null)
        {
            throw new InvalidOperationException($"constructor for struct '{FullName}' is already defined");
        }

        Constructor = constructor;
    }

    internal void SetConstructors(ImmutableArray<FunctionSymbol> constructors)
    {
        _constructors = constructors;
        Constructor = constructors.FirstOrDefault();
        for (int index = 0; index < constructors.Length; index++)
            constructors[index].SetConstructorOverload(index, constructors.Length);
    }

    internal void SetDestructor(FunctionSymbol destructor)
    {
        if (Destructor is not null)
        {
            throw new InvalidOperationException($"destructor for struct '{FullName}' is already defined");
        }

        Destructor = destructor;
    }

    public FieldSymbol? FindField(string name) =>
        _fields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal)) ??
        BaseType?.FindField(name);

    public FieldSymbol? FindStaticField(string name) =>
        _staticFields.FirstOrDefault(field => string.Equals(field.Name, name, StringComparison.Ordinal));

    public FunctionSymbol? FindMethod(string name) =>
        _methods.FirstOrDefault(method => string.Equals(method.Name, name, StringComparison.Ordinal)) ??
        BaseType?.FindMethod(name);

    public FunctionSymbol? FindDestructor() => Destructor ?? BaseType?.FindDestructor();

    public bool Implements(InterfaceTypeSymbol target) =>
        Interfaces.Any(@interface => @interface.IsOrInherits(target)) ||
        (BaseType?.Implements(target) ?? false);

    public IEnumerable<InterfaceTypeSymbol> ImplementedInterfaces =>
        (BaseType?.ImplementedInterfaces ?? [])
        .Concat(Interfaces.SelectMany(@interface => @interface.SelfAndBaseInterfaces))
        .Distinct();

    public FunctionSymbol? FindInterfaceImplementation(FunctionSymbol required)
    {
        FunctionSymbol? declared = _methods.FirstOrDefault(candidate =>
            candidate.IsPublic &&
            !candidate.IsStatic &&
            candidate.Overrides(required));
        return declared ?? BaseType?.FindInterfaceImplementation(required);
    }
}

public sealed class FieldSymbol : Symbol
{
    internal FieldSymbol(
        string name,
        StructTypeSymbol containingType,
        TypeSymbol type,
        int ordinal,
        Accessibility accessibility,
        bool isStatic,
        object? constantValue,
        FieldDeclarationSyntax declaration)
        : base(name, SymbolKind.Field)
    {
        ContainingType = containingType;
        Type = type;
        Ordinal = ordinal;
        Accessibility = accessibility;
        IsStatic = isStatic;
        ConstantValue = constantValue;
        Declaration = declaration;
    }

    public StructTypeSymbol ContainingType { get; }

    public TypeSymbol Type { get; }

    public int Ordinal { get; }

    public Accessibility Accessibility { get; }

    public bool IsPublic => Accessibility == Accessibility.Public;

    public bool IsStatic { get; }

    public object? ConstantValue { get; }

    internal FieldDeclarationSyntax Declaration { get; }
}
