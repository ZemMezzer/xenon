using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class StructTypeSymbol : TypeSymbol
{
    private ImmutableArray<FieldSymbol> _fields = [];
    private ImmutableArray<FieldSymbol> _staticFields = [];
    private ImmutableArray<PropertySymbol> _properties = [];
    private ImmutableArray<IndexerSymbol> _indexers = [];
    private ImmutableArray<ConstantSymbol> _constants = [];
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

    public ImmutableArray<PropertySymbol> Properties => _properties;
    public ImmutableArray<IndexerSymbol> Indexers => _indexers;
    public ImmutableArray<ConstantSymbol> Constants => _constants;

    public ImmutableArray<FieldSymbol> AllInstanceFields => BaseType is null ? _fields : BaseType.AllInstanceFields.AddRange(_fields);

    public ImmutableArray<FunctionSymbol> Methods => _methods;
    public ImmutableArray<FunctionSymbol> VirtualMethods => _virtualMethods;

    public FunctionSymbol? Constructor { get; private set; }
    public ImmutableArray<FunctionSymbol> Constructors => _constructors;

    public FunctionSymbol? InstanceInitializer { get; private set; }

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

    internal void SetProperties(ImmutableArray<PropertySymbol> properties) => _properties = properties;
    internal void SetIndexers(ImmutableArray<IndexerSymbol> indexers) => _indexers = indexers;
    internal void SetConstants(ImmutableArray<ConstantSymbol> constants) => _constants = constants;

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

    internal void SetInstanceInitializer(FunctionSymbol initializer) => InstanceInitializer = initializer;

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

    public PropertySymbol? FindProperty(string name) =>
        _properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal)) ??
        BaseType?.FindProperty(name);

    public IEnumerable<IndexerSymbol> AllIndexers
    {
        get
        {
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            return _indexers.Concat(BaseType?.AllIndexers ?? [])
                .Where(indexer => signatures.Add(IndexerSymbol.CreateAccessorName("Item", indexer.Parameters)));
        }
    }

    public ConstantSymbol? FindConstant(string name) =>
        _constants.FirstOrDefault(constant => string.Equals(constant.Name, name, StringComparison.Ordinal)) ??
        BaseType?.FindConstant(name);

    public FunctionSymbol? FindMethod(string name, bool isReadonly) =>
        _methods.FirstOrDefault(method =>
            string.Equals(method.Name, name, StringComparison.Ordinal) &&
            method.IsReadonly == isReadonly) ??
        BaseType?.FindMethod(name, isReadonly);

    public FunctionSymbol? FindInstanceMethod(string name, bool receiverIsReadonly)
    {
        if (receiverIsReadonly)
        {
            return FindMethod(name, isReadonly: true) is { IsStatic: false } method
                ? method
                : null;
        }

        FunctionSymbol? mutable = FindMethod(name, isReadonly: false);
        if (mutable is { IsStatic: false })
            return mutable;

        return FindMethod(name, isReadonly: true) is { IsStatic: false } readOnly
            ? readOnly
            : null;
    }

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
            (candidate.ContainingProperty is not null) == (required.ContainingInterfaceProperty is not null) &&
            (candidate.ContainingIndexer is not null) == (required.ContainingInterfaceIndexer is not null) &&
            candidate.Overrides(required));
        return declared ?? BaseType?.FindInterfaceImplementation(required);
    }
}

public sealed class ConstantSymbol : Symbol
{
    internal ConstantSymbol(
        string name,
        TypeSymbol type,
        NamespaceSymbol containingNamespace,
        StructTypeSymbol? containingType,
        ExpressionSyntax initializer,
        SyntaxToken identifierToken)
        : base(name, SymbolKind.Constant)
    {
        Type = type;
        ContainingNamespace = containingNamespace;
        ContainingType = containingType;
        Initializer = initializer;
        IdentifierToken = identifierToken;
    }

    public TypeSymbol Type { get; }
    public NamespaceSymbol ContainingNamespace { get; }
    public StructTypeSymbol? ContainingType { get; }
    public object? Value { get; private set; }
    public BoundExpression? BoundValue { get; private set; }
    public bool HasValue { get; private set; }
    internal ExpressionSyntax Initializer { get; }
    internal SyntaxToken IdentifierToken { get; }

    internal void SetValue(object? value)
    {
        Value = value;
        HasValue = true;
    }

    internal void SetBoundValue(BoundExpression value)
    {
        BoundValue = value;
        HasValue = true;
    }
}

public sealed class IndexerSymbol : Symbol
{
    internal IndexerSymbol(
        StructTypeSymbol containingType,
        TypeSymbol type,
        ImmutableArray<ParameterSymbol> parameters,
        Accessibility accessibility,
        IndexerDeclarationSyntax declaration)
        : base("this", SymbolKind.Property)
    {
        ContainingType = containingType;
        Type = type;
        Parameters = parameters;
        Accessibility = accessibility;
        Declaration = declaration;
    }

    public StructTypeSymbol ContainingType { get; }
    public TypeSymbol Type { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; }
    public Accessibility Accessibility { get; }
    public bool IsPublic => Accessibility == Accessibility.Public;
    public FunctionSymbol? Getter { get; private set; }
    public FunctionSymbol? Setter { get; private set; }
    internal IndexerDeclarationSyntax Declaration { get; }

    internal void SetAccessors(FunctionSymbol? getter, FunctionSymbol? setter)
    {
        Getter = getter;
        Setter = setter;
    }

    internal string GetAccessorName(bool getter) =>
        CreateAccessorName(getter ? "get_Item" : "set_Item", Parameters);

    internal static string CreateAccessorName(string prefix, ImmutableArray<ParameterSymbol> parameters)
    {
        string signature = parameters.IsEmpty
            ? "none"
            : string.Join("__", parameters.Select(parameter => EncodeTypeName(GetTypeIdentity(parameter.Type))));
        return $"{prefix}__{signature}";
    }

    private static string GetTypeIdentity(TypeSymbol type) => type switch
    {
        StructTypeSymbol structure => structure.FullName,
        InterfaceTypeSymbol @interface => @interface.FullName,
        PointerTypeSymbol pointer => $"{(pointer.IsReadonly ? "readonly_" : string.Empty)}ptr_{GetTypeIdentity(pointer.ElementType)}",
        ReferenceTypeSymbol reference => $"{(reference.IsReadonly ? "readonly_" : string.Empty)}ref_{GetTypeIdentity(reference.ElementType)}",
        ArrayTypeSymbol array => $"array_{GetTypeIdentity(array.ElementType)}",
        _ => type.Name,
    };

    private static string EncodeTypeName(string name) => string.Concat(name.Select(character =>
        char.IsAsciiLetterOrDigit(character) ? character.ToString() : $"_{(int)character:X2}"));
}

public sealed class PropertySymbol : Symbol
{
    internal PropertySymbol(
        string name,
        StructTypeSymbol containingType,
        TypeSymbol type,
        Accessibility accessibility,
        PropertyDeclarationSyntax declaration)
        : base(name, SymbolKind.Property)
    {
        ContainingType = containingType;
        Type = type;
        Accessibility = accessibility;
        Declaration = declaration;
    }

    public StructTypeSymbol ContainingType { get; }
    public TypeSymbol Type { get; }
    public Accessibility Accessibility { get; }
    public bool IsPublic => Accessibility == Accessibility.Public;
    public FunctionSymbol? Getter { get; private set; }
    public FunctionSymbol? Setter { get; private set; }
    internal PropertyDeclarationSyntax Declaration { get; }

    internal void SetAccessors(FunctionSymbol? getter, FunctionSymbol? setter)
    {
        Getter = getter;
        Setter = setter;
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
        bool isReadonly,
        object? constantValue,
        FieldDeclarationSyntax declaration)
        : base(name, SymbolKind.Field)
    {
        ContainingType = containingType;
        Type = type;
        Ordinal = ordinal;
        Accessibility = accessibility;
        IsStatic = isStatic;
        IsReadonly = isReadonly;
        ConstantValue = constantValue;
        Declaration = declaration;
    }

    public StructTypeSymbol ContainingType { get; }

    public TypeSymbol Type { get; }

    public int Ordinal { get; }

    public Accessibility Accessibility { get; }

    public bool IsPublic => Accessibility == Accessibility.Public;

    public bool IsStatic { get; }

    public bool IsReadonly { get; }

    public object? ConstantValue { get; }

    public BoundExpression? Initializer { get; private set; }

    internal FieldDeclarationSyntax Declaration { get; }

    internal void SetInitializer(BoundExpression initializer) => Initializer = initializer;
}
