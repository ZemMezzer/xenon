using System.Collections.Immutable;
using System.Text;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class FunctionSymbol : Symbol
{
    private ImmutableArray<GenericParameterSymbol> _typeParameters = [];
    public bool HasStackArrays { get; internal set; }
    public bool HasScalarCleanup { get; internal set; }
    public bool HasScopeCleanup => HasStackArrays || HasScalarCleanup;
    public bool DelegatesToThisConstructor { get; }
    public ImmutableArray<ReceiverMoveEffect> ReceiverMoveEffects { get; private set; } = [];
    public ImmutableArray<ReferenceReturnOrigin> ReferenceReturnOrigins { get; private set; } = [];
    public ImmutableArray<SharedReturnOrigin> SharedReturnOrigins { get; private set; } = [];
    public ImmutableArray<ReferenceFieldOrigin> ReferenceFieldOrigins { get; private set; } = [];

    internal FunctionSymbol(
        string name,
        NamespaceSymbol containingNamespace,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        FunctionDeclarationSyntax declaration,
        ImmutableArray<GenericParameterSymbol> typeParameters = default)
        : base(name, SymbolKind.Function, containingNamespace)
    {
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        Accessibility = declaration.IsExport ? Accessibility.Public : AccessibilityFacts.FromSyntax(
            declaration.AccessModifierToken, declaration.SecondaryAccessModifierToken, Accessibility.Private);
        FunctionKind = FunctionKind.Ordinary;
        IsReadonly = declaration.IsReadonly;
        SetTypeParameters(typeParameters.IsDefault ? [] : typeParameters);
        IsExtern = declaration.IsExtern;
        IsExport = declaration.IsExport;
        IsDefinition = declaration.Body is not null;
        SetSourceOrigin(declaration);
        ApplyParameterDocumentation();
    }

    internal FunctionSymbol(
        string name,
        InterfaceTypeSymbol containingInterface,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        InterfaceMethodDeclarationSyntax declaration)
        : base(name, SymbolKind.Function, containingInterface)
    {
        FunctionKind = FunctionKind.Method;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        Accessibility = Accessibility.Public;
        IsAbstract = true;
        IsReadonly = declaration.IsReadonly;
        IsDefinition = false;
        SetSourceOrigin(declaration);
        ApplyParameterDocumentation();
    }

    internal FunctionSymbol(
        string name,
        InterfacePropertySymbol containingProperty,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        PropertyAccessorDeclarationSyntax declaration)
        : base(name, SymbolKind.Function, containingProperty)
    {
        FunctionKind = FunctionKind.Method;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        Accessibility = Accessibility.Public;
        IsAbstract = true;
        AccessorKind = declaration.IsGetter ? AccessorKind.Getter : AccessorKind.Setter;
        IsReadonly = declaration.IsGetter && containingProperty.IsReadonly;
        IsDefinition = declaration.Body is not null;
        SetSourceOrigin(declaration, includeDocumentation: false);
    }

    internal FunctionSymbol(
        string name,
        InterfaceIndexerSymbol containingIndexer,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        PropertyAccessorDeclarationSyntax declaration)
        : base(name, SymbolKind.Function, containingIndexer)
    {
        FunctionKind = FunctionKind.Method;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        Accessibility = Accessibility.Public;
        IsAbstract = true;
        AccessorKind = declaration.IsGetter ? AccessorKind.Getter : AccessorKind.Setter;
        IsReadonly = declaration.IsGetter && containingIndexer.IsReadonly;
        IsDefinition = declaration.Body is not null;
        SetSourceOrigin(declaration, includeDocumentation: false);
    }

    internal FunctionSymbol(
        string name,
        DeclaredTypeSymbol containingType,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        MethodDeclarationSyntax declaration)
        : base(name, SymbolKind.Function, containingType)
    {
        FunctionKind = FunctionKind.Method;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        Accessibility = AccessibilityFacts.FromSyntax(declaration.AccessModifierToken,
            declaration.SecondaryAccessModifierToken, Accessibility.Private);
        IsStatic = declaration.IsStatic;
        IsVirtual = declaration.IsVirtual;
        IsOverride = declaration.IsOverride;
        IsAbstract = declaration.IsAbstract;
        IsReadonly = declaration.IsReadonly || containingType is StructTypeSymbol { IsReadonly: true } && !declaration.IsStatic;
        IsDefinition = declaration.Body is not null;
        SetSourceOrigin(declaration);
        ApplyParameterDocumentation();
    }

    internal FunctionSymbol(
        string name,
        PropertySymbol containingProperty,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        PropertyAccessorDeclarationSyntax declaration)
        : base(name, SymbolKind.Function, containingProperty)
    {
        FunctionKind = FunctionKind.Method;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        Accessibility = containingProperty.Accessibility;
        IsStatic = containingProperty.IsStatic;
        IsVirtual = containingProperty.IsVirtual;
        IsOverride = containingProperty.IsOverride;
        IsAbstract = containingProperty.IsAbstract;
        AccessorKind = declaration.IsGetter ? AccessorKind.Getter : AccessorKind.Setter;
        IsReadonly = declaration.IsGetter && containingProperty.IsReadonly ||
            containingProperty.ContainingType is StructTypeSymbol { IsReadonly: true } && !containingProperty.IsStatic;
        IsDefinition = declaration.Body is not null;
        SetSourceOrigin(declaration, includeDocumentation: false);
    }

    internal FunctionSymbol(
        string name,
        IndexerSymbol containingIndexer,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        PropertyAccessorDeclarationSyntax declaration)
        : base(name, SymbolKind.Function, containingIndexer)
    {
        FunctionKind = FunctionKind.Method;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        Accessibility = containingIndexer.Accessibility;
        IsStatic = containingIndexer.IsStatic;
        IsVirtual = containingIndexer.IsVirtual;
        IsOverride = containingIndexer.IsOverride;
        IsAbstract = containingIndexer.IsAbstract;
        AccessorKind = declaration.IsGetter ? AccessorKind.Getter : AccessorKind.Setter;
        IsReadonly = declaration.IsGetter && containingIndexer.IsReadonly ||
            containingIndexer.ContainingType is StructTypeSymbol { IsReadonly: true } && !containingIndexer.IsStatic;
        IsDefinition = declaration.Body is not null;
        SetSourceOrigin(declaration, includeDocumentation: false);
    }

    internal FunctionSymbol(
        FunctionKind functionKind,
        DeclaredTypeSymbol containingType,
        ImmutableArray<ParameterSymbol> parameters,
        SyntaxNode declaration,
        Accessibility accessibility)
        : base(functionKind switch
        {
            FunctionKind.Constructor => containingType.Name,
            FunctionKind.InstanceInitializer => "__init_fields",
            FunctionKind.Destructor => $"~{containingType.Name}",
            FunctionKind.DestructorGlue => "__destructor",
            _ => throw new ArgumentOutOfRangeException(nameof(functionKind)),
        }, SymbolKind.Function, containingType)
    {
        if (functionKind is FunctionKind.Ordinary or FunctionKind.Method)
        {
            throw new ArgumentOutOfRangeException(nameof(functionKind));
        }

        FunctionKind = functionKind;
        ReturnType = BuiltinTypes.Void;
        Parameters = ParameterSymbol.Own(parameters, this);
        Declaration = declaration;
        Accessibility = accessibility;
        IsVirtual = declaration is DestructorDeclarationSyntax { IsVirtual: true };
        IsOverride = declaration is DestructorDeclarationSyntax { IsOverride: true };
        DelegatesToThisConstructor = declaration is ConstructorDeclarationSyntax { HasThisInitializer: true };
        IsDefinition = functionKind is FunctionKind.Constructor or FunctionKind.Destructor or
            FunctionKind.InstanceInitializer or FunctionKind.DestructorGlue;
        if (declaration is not TypeDeclarationSyntax && functionKind is FunctionKind.Constructor or FunctionKind.Destructor)
        {
            SetSourceOrigin(declaration);
            ApplyParameterDocumentation();
        }
    }

    internal FunctionSymbol(
        string name,
        Symbol containingSymbol,
        FunctionKind functionKind,
        TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters,
        Accessibility accessibility,
        bool isStatic = false,
        bool isReadonly = false,
        bool isVirtual = false,
        bool isOverride = false,
        bool isAbstract = false,
        bool isExtern = false,
        bool isExport = false,
        bool isDefinition = false,
        bool delegatesToThisConstructor = false,
        ImmutableArray<GenericParameterSymbol> typeParameters = default,
        SymbolOrigin? origin = null,
        SymbolDocumentation? documentation = null,
        SymbolImplementation? implementation = null,
        AccessorKind accessorKind = AccessorKind.None)
        : base(name, SymbolKind.Function, containingSymbol)
    {
        FunctionKind = functionKind;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
        Accessibility = accessibility;
        IsStatic = isStatic;
        IsReadonly = isReadonly;
        IsVirtual = isVirtual;
        IsOverride = isOverride;
        IsAbstract = isAbstract;
        IsExtern = isExtern;
        IsExport = isExport;
        IsDefinition = isDefinition;
        DelegatesToThisConstructor = delegatesToThisConstructor;
        AccessorKind = accessorKind;
        SetTypeParameters(typeParameters.IsDefault ? [] : typeParameters);
        SetMetadata(origin ?? SymbolOrigin.CompilerGenerated, documentation);
        SetImplementation(implementation);
        if (implementation is SourceSymbolImplementation source) Declaration = source.Declaration;
    }

    internal FunctionSymbol(FunctionKind functionKind, DeclaredTypeSymbol containingType,
        ImmutableArray<ParameterSymbol> parameters, SymbolOrigin origin, Accessibility accessibility)
        : this(functionKind switch
            {
                FunctionKind.Constructor => containingType.Name,
                FunctionKind.InstanceInitializer => "__init_fields",
                FunctionKind.Destructor => $"~{containingType.Name}",
                FunctionKind.DestructorGlue => "__destructor",
                _ => throw new ArgumentOutOfRangeException(nameof(functionKind)),
            }, containingType, functionKind, BuiltinTypes.Void, parameters, accessibility,
            isDefinition: true, origin: origin) { }

    internal FunctionSymbol(FieldSymbol threadLocalField)
        : base($"__init_threadlocal_{threadLocalField.Name}", SymbolKind.Function,
            threadLocalField.ContainingType)
    {
        FunctionKind = FunctionKind.ThreadLocalInitializer;
        ReturnType = BuiltinTypes.Void;
        Parameters = [];
        Accessibility = Accessibility.Private;
        IsStatic = true;
        ThreadLocalField = threadLocalField;
        IsDefinition = true;
        SetMetadata(SymbolOrigin.CompilerGenerated);
    }

    internal FunctionSymbol(
        OwnershipTypeSymbol ownershipType,
        NamespaceSymbol containingNamespace,
        PointerTypeSymbol addressType,
        SyntaxNode declaration)
        : base(
            $"__ownership_destructor_{Convert.ToHexString(Encoding.UTF8.GetBytes(TypeSignature.Get(ownershipType)))}",
            SymbolKind.Function,
            containingNamespace)
    {
        FunctionKind = FunctionKind.OwnershipDestructor;
        ReturnType = BuiltinTypes.Void;
        Parameters = ParameterSymbol.Own([new ParameterSymbol("value", addressType, 0)], this);
        Declaration = declaration;
        Accessibility = Accessibility.Private;
        OwnershipType = ownershipType;
        IsDefinition = true;
    }

    internal FunctionSymbol(OwnershipTypeSymbol ownershipType, NamespaceSymbol containingNamespace,
        PointerTypeSymbol addressType, SymbolOrigin origin)
        : base($"__ownership_destructor_{Convert.ToHexString(Encoding.UTF8.GetBytes(TypeSignature.Get(ownershipType)))}",
            SymbolKind.Function, containingNamespace)
    {
        FunctionKind = FunctionKind.OwnershipDestructor;
        ReturnType = BuiltinTypes.Void;
        Parameters = ParameterSymbol.Own([new ParameterSymbol("value", addressType, 0)], this);
        Accessibility = Accessibility.Private;
        OwnershipType = ownershipType;
        IsDefinition = true;
        SetMetadata(origin);
    }

    internal FunctionSymbol(
        StorageTypeSymbol storageType,
        NamespaceSymbol containingNamespace,
        PointerTypeSymbol addressType,
        SyntaxNode declaration)
        : base(
            $"__storage_destructor_{Convert.ToHexString(Encoding.UTF8.GetBytes(TypeSignature.Get(storageType)))}",
            SymbolKind.Function,
            containingNamespace)
    {
        FunctionKind = FunctionKind.StorageDestructor;
        ReturnType = BuiltinTypes.Void;
        Parameters = ParameterSymbol.Own([new ParameterSymbol("value", addressType, 0)], this);
        Declaration = declaration;
        Accessibility = Accessibility.Private;
        StorageType = storageType;
        IsDefinition = true;
    }

    internal FunctionSymbol(StorageTypeSymbol storageType, NamespaceSymbol containingNamespace,
        PointerTypeSymbol addressType, SymbolOrigin origin)
        : base($"__storage_destructor_{Convert.ToHexString(Encoding.UTF8.GetBytes(TypeSignature.Get(storageType)))}",
            SymbolKind.Function, containingNamespace)
    {
        FunctionKind = FunctionKind.StorageDestructor;
        ReturnType = BuiltinTypes.Void;
        Parameters = ParameterSymbol.Own([new ParameterSymbol("value", addressType, 0)], this);
        Accessibility = Accessibility.Private;
        StorageType = storageType;
        IsDefinition = true;
        SetMetadata(origin);
    }

    public NamespaceSymbol ContainingNamespace => GetContainingSymbol<NamespaceSymbol>()!;

    public DeclaredTypeSymbol? ContainingType => GetContainingSymbol<DeclaredTypeSymbol>();
    public StructTypeSymbol? ContainingStruct => ContainingType as StructTypeSymbol;
    public InterfaceTypeSymbol? ContainingInterface => ContainingType as InterfaceTypeSymbol;
    public PropertySymbol? ContainingProperty => ContainingSymbol as PropertySymbol;
    public InterfacePropertySymbol? ContainingInterfaceProperty => ContainingSymbol as InterfacePropertySymbol;
    public IndexerSymbol? ContainingIndexer => ContainingSymbol as IndexerSymbol;
    public InterfaceIndexerSymbol? ContainingInterfaceIndexer => ContainingSymbol as InterfaceIndexerSymbol;
    public OwnershipTypeSymbol? OwnershipType { get; }
    public StorageTypeSymbol? StorageType { get; }
    public FieldSymbol? ThreadLocalField { get; }

    public string FullName => FunctionKind switch
    {
        FunctionKind.Method when ContainingInterface is null => IsReadonly
            ? $"{ContainingType!.FullName}.{Name}.__readonly"
            : $"{ContainingType!.FullName}.{Name}",
        FunctionKind.Method => $"{ContainingInterface!.FullName}.{Name}",
        FunctionKind.Constructor => ConstructorOverloadCount == 1 ? $"{ContainingType!.FullName}.__ctor" : $"{ContainingType!.FullName}.__ctor.{ConstructorOverload}",
        FunctionKind.InstanceInitializer => $"{ContainingType!.FullName}.__init_fields",
        FunctionKind.ThreadLocalInitializer => $"{ContainingType!.FullName}.__init_threadlocal.{ThreadLocalField!.Name}",
        FunctionKind.Destructor => $"{ContainingType!.FullName}.__dtor",
        FunctionKind.DestructorGlue => $"{ContainingType!.FullName}.__destructor",
        FunctionKind.OwnershipDestructor => $"{ContainingNamespace.FullName}.{Name}",
        FunctionKind.StorageDestructor => $"{ContainingNamespace.FullName}.{Name}",
        _ => $"{ContainingNamespace.FullName}.{Name}",
    };

    public TypeSymbol ReturnType { get; }

    public ImmutableArray<ParameterSymbol> Parameters { get; }

    public ImmutableArray<GenericParameterSymbol> TypeParameters => _typeParameters;

    public FunctionSymbol? GenericDefinition { get; private set; }
    public ImmutableArray<TypeSymbol> TypeArguments { get; private set; } = [];
    public bool IsGenericSpecialization => GenericDefinition is not null;

    public bool IsGenericDefinition => !TypeParameters.IsEmpty || ContainingStruct?.IsOpenGenericType == true;

    public FunctionKind FunctionKind { get; }

    public Accessibility Accessibility { get; }

    public bool IsExtern { get; }

    public bool IsExport { get; }

    public bool IsPublic => Accessibility == Accessibility.Public;

    public bool HasImplicitThis => ContainingType is not null && ContainingInterface is null && !IsStatic;

    public bool IsStatic { get; }
    public bool IsVirtual { get; }
    public bool IsOverride { get; }
    public bool IsAbstract { get; }
    public bool IsReadonly { get; }

    public AccessorKind AccessorKind { get; }

    public bool IsAccessor => AccessorKind != AccessorKind.None;

    public override bool IsCompilerGenerated => FunctionKind is FunctionKind.InstanceInitializer or FunctionKind.ThreadLocalInitializer or FunctionKind.DestructorGlue or FunctionKind.OwnershipDestructor or FunctionKind.StorageDestructor;
    public override bool IsUserVisible => FunctionKind is not (FunctionKind.InstanceInitializer or FunctionKind.ThreadLocalInitializer or FunctionKind.DestructorGlue or FunctionKind.OwnershipDestructor or FunctionKind.StorageDestructor) && !IsAccessor;
    public override bool HasUserEditableIdentifier => base.HasUserEditableIdentifier && !IsAccessor;
    public override bool IsDefinition { get; }

    public int? VTableSlot { get; private set; }
    public int ConstructorOverload { get; private set; }
    public int ConstructorOverloadCount { get; private set; } = 1;

    internal void SetVTableSlot(int slot) => VTableSlot = slot;
    internal void SetTypeParameters(ImmutableArray<GenericParameterSymbol> parameters)
    {
        _typeParameters = parameters;
        foreach (GenericParameterSymbol parameter in parameters) parameter.SetDeclaringSymbol(this);
    }
    internal void SetReceiverMoveEffects(ImmutableArray<ReceiverMoveEffect> effects) =>
        ReceiverMoveEffects = effects;
    internal void SetReferenceReturnOrigins(ImmutableArray<ReferenceReturnOrigin> origins) =>
        ReferenceReturnOrigins = origins;
    internal void SetSharedReturnOrigins(ImmutableArray<SharedReturnOrigin> origins) =>
        SharedReturnOrigins = origins;
    internal void SetReferenceFieldOrigins(ImmutableArray<ReferenceFieldOrigin> origins) =>
        ReferenceFieldOrigins = origins;
    internal void SetGenericSpecialization(FunctionSymbol definition, ImmutableArray<TypeSymbol> typeArguments)
    {
        GenericDefinition = definition;
        TypeArguments = typeArguments;
    }
    internal void SetConstructorOverload(int index, int count)
    {
        ConstructorOverload = index;
        ConstructorOverloadCount = count;
    }

    private void ApplyParameterDocumentation()
    {
        foreach (ParameterSymbol parameter in Parameters)
            if (Documentation.Parameters.TryGetValue(parameter.Name, out string? text))
                parameter.SetMetadata(parameter.Origin,
                    SymbolDocumentation.Empty with { Summary = text });
    }

    public bool HasSameSignature(FunctionSymbol candidate) =>
        FunctionKind == candidate.FunctionKind &&
        AccessorKind == candidate.AccessorKind &&
        (ContainingProperty is not null || ContainingInterfaceProperty is not null) ==
            (candidate.ContainingProperty is not null || candidate.ContainingInterfaceProperty is not null) &&
        (ContainingIndexer is not null || ContainingInterfaceIndexer is not null) ==
            (candidate.ContainingIndexer is not null || candidate.ContainingInterfaceIndexer is not null) &&
        string.Equals(Name, candidate.Name, StringComparison.Ordinal) &&
        TypeParameters.Length == candidate.TypeParameters.Length &&
        IsStatic == candidate.IsStatic &&
        IsReadonly == candidate.IsReadonly &&
        Parameters.Length == candidate.Parameters.Length &&
        TypeSignature.Parameters(this) == TypeSignature.Parameters(candidate);

    /// <summary>The source-level identity used to reject duplicate callable declarations.
    /// Return type, parameter names, static state, and receiver readonly state are deliberately excluded.</summary>
    public bool HasSameOverloadSignature(FunctionSymbol candidate) =>
        FunctionKind == candidate.FunctionKind &&
        AccessorKind == candidate.AccessorKind &&
        (ContainingProperty is not null || ContainingInterfaceProperty is not null) ==
            (candidate.ContainingProperty is not null || candidate.ContainingInterfaceProperty is not null) &&
        (ContainingIndexer is not null || ContainingInterfaceIndexer is not null) ==
            (candidate.ContainingIndexer is not null || candidate.ContainingInterfaceIndexer is not null) &&
        string.Equals(Name, candidate.Name, StringComparison.Ordinal) &&
        TypeParameters.Length == candidate.TypeParameters.Length &&
        Parameters.Length == candidate.Parameters.Length &&
        TypeSignature.Parameters(this) == TypeSignature.Parameters(candidate);

    public bool ConflictsWithOverloadName(FunctionSymbol candidate, string proposedName)
    {
        if (!string.Equals(proposedName, candidate.Name, StringComparison.Ordinal) ||
            TypeParameters.Length != candidate.TypeParameters.Length ||
            Parameters.Length != candidate.Parameters.Length ||
            TypeSignature.Parameters(this) != TypeSignature.Parameters(candidate))
            return false;
        bool readonlyReceiverPair = FunctionKind == FunctionKind.Method && candidate.FunctionKind == FunctionKind.Method &&
            !IsStatic && !candidate.IsStatic && IsReadonly != candidate.IsReadonly;
        return !readonlyReceiverPair;
    }

    public bool Overrides(FunctionSymbol candidate) =>
        HasSameSignature(candidate) && TypeIdentity.AreSame(ReturnType, candidate.ReturnType);

    internal SyntaxNode Declaration { get; } = null!;
    internal SyntaxNode? ImplementationDeclaration => Implementation is SourceSymbolImplementation source
        ? source.Declaration : GenericDefinition?.ImplementationDeclaration;
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Origin.Kind != SymbolOriginKind.Source || Declaration is TypeDeclarationSyntax ||
            FunctionKind is FunctionKind.OwnershipDestructor or FunctionKind.StorageDestructor
            ? base.DeclaringSyntaxReferences : [new(Declaration)];
}

public readonly record struct ReceiverMoveEffect(ImmutableArray<int> FieldOrdinals);

public enum ReferenceReturnOriginKind
{
    Parameter,
    Receiver,
    Static,
    Unknown,
}

public readonly record struct ReferenceReturnOrigin(
    ReferenceReturnOriginKind Kind,
    int ParameterOrdinal,
    ImmutableArray<int> FieldOrdinals);

public enum SharedReturnOriginKind
{
    Fresh,
    Parameter,
    Unknown,
}

public readonly record struct SharedReturnOrigin(
    SharedReturnOriginKind Kind,
    int ParameterOrdinal);

public readonly record struct ReferenceFieldOrigin(
    ImmutableArray<int> FieldOrdinals,
    ReferenceReturnOrigin Origin,
    bool IsReadonly);

public abstract class VariableSymbol : Symbol
{
    protected VariableSymbol(string name, SymbolKind kind, TypeSymbol type, Symbol? containingSymbol, bool isReadonly = false)
        : base(name, kind, containingSymbol)
    {
        Type = type;
        IsReadonly = isReadonly;
    }

    public TypeSymbol Type { get; }
    public bool IsReadonly { get; }
}

public sealed class ParameterSymbol : VariableSymbol
{
    internal ParameterSymbol(string name, TypeSymbol type, int ordinal, bool isReadonly = false, Symbol? containingSymbol = null, ParameterSyntax? declaration = null)
        : base(name, SymbolKind.Parameter, type, containingSymbol, isReadonly)
    {
        Ordinal = ordinal;
        Declaration = declaration;
        if (declaration is not null) SetSourceOrigin(declaration, includeDocumentation: false);
    }

    public int Ordinal { get; }
    internal ParameterSyntax? Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Declaration is null ? [] : [new(Declaration)];

    // Each declaration owns its own parameters, including indexers and their accessors.
    internal static ImmutableArray<ParameterSymbol> Own(ImmutableArray<ParameterSymbol> parameters, Symbol owner) =>
        parameters.Select(parameter =>
        {
            var owned = new ParameterSymbol(parameter.Name, parameter.Type, parameter.Ordinal,
                parameter.IsReadonly, owner, parameter.Declaration);
            if (owner.Documentation.Parameters.TryGetValue(parameter.Name, out string? text))
                owned.SetMetadata(owned.Origin, SymbolDocumentation.Empty with { Summary = text });
            return owned;
        }).ToImmutableArray();
}

public sealed class LocalVariableSymbol : VariableSymbol
{
    internal LocalVariableSymbol(string name, TypeSymbol type, FunctionSymbol containingFunction, bool isReadonly = false, SyntaxNode? declaration = null)
        : base(name, SymbolKind.LocalVariable, type, containingFunction, isReadonly)
    {
        Declaration = declaration;
    }

    internal SyntaxNode? Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Declaration is null ? [] : [new(Declaration)];
    public override bool IsDefinition => Declaration is not null;

    public ArrayStorageKind ArrayStorage { get; internal set; }
    public bool RequiresArrayCleanupTransfer { get; internal set; }
    public FunctionSymbol? Destructor { get; internal set; }
    internal Binding.BoundExpression? ConstantValue { get; set; }
}
