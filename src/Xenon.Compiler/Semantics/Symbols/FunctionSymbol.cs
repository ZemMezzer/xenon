using System.Collections.Immutable;
using System.Text;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class FunctionSymbol : Symbol
{
    public bool HasStackArrays { get; internal set; }
    public bool HasScalarCleanup { get; internal set; }
    public bool HasScopeCleanup => HasStackArrays || HasScalarCleanup;
    public ImmutableArray<ReceiverMoveEffect> ReceiverMoveEffects { get; private set; } = [];

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
        Accessibility = declaration.IsPublic ? Accessibility.Public : Accessibility.Private;
        FunctionKind = FunctionKind.Ordinary;
        IsReadonly = declaration.IsReadonly;
        TypeParameters = typeParameters.IsDefault ? [] : typeParameters;
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
        IsReadonly = declaration.IsGetter && containingProperty.Declaration.IsReadonly;
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
        IsReadonly = declaration.IsGetter && containingIndexer.Declaration.IsReadonly;
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
        : base(name, SymbolKind.Function, containingProperty)
    {
        PropertyDeclarationSyntax property = containingProperty.Declaration;
        FunctionKind = FunctionKind.Method;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
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
        : base(name, SymbolKind.Function, containingIndexer)
    {
        IndexerDeclarationSyntax indexer = containingIndexer.Declaration;
        FunctionKind = FunctionKind.Method;
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
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
        DeclaredTypeSymbol containingType,
        ImmutableArray<ParameterSymbol> parameters,
        SyntaxNode declaration,
        Accessibility accessibility)
        : base(functionKind switch
        {
            FunctionKind.Constructor => containingType.Name,
            FunctionKind.InstanceInitializer => "__init_fields",
            FunctionKind.Destructor => $"~{containingType.Name}",
            FunctionKind.DropGlue => "__drop",
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
    }

    internal FunctionSymbol(
        OwnershipTypeSymbol ownershipType,
        NamespaceSymbol containingNamespace,
        PointerTypeSymbol addressType,
        SyntaxNode declaration)
        : base(
            $"__drop_ownership_{Convert.ToHexString(Encoding.UTF8.GetBytes(TypeSignature.Get(ownershipType)))}",
            SymbolKind.Function,
            containingNamespace)
    {
        FunctionKind = FunctionKind.OwnershipDrop;
        ReturnType = BuiltinTypes.Void;
        Parameters = ParameterSymbol.Own([new ParameterSymbol("value", addressType, 0)], this);
        Declaration = declaration;
        Accessibility = Accessibility.Private;
        OwnershipType = ownershipType;
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

    public string FullName => FunctionKind switch
    {
        FunctionKind.Method when ContainingInterface is null => IsReadonly
            ? $"{ContainingType!.FullName}.{Name}.__readonly"
            : $"{ContainingType!.FullName}.{Name}",
        FunctionKind.Method => $"{ContainingInterface!.FullName}.{Name}",
        FunctionKind.Constructor => ConstructorOverloadCount == 1 ? $"{ContainingType!.FullName}.__ctor" : $"{ContainingType!.FullName}.__ctor.{ConstructorOverload}",
        FunctionKind.InstanceInitializer => $"{ContainingType!.FullName}.__init_fields",
        FunctionKind.Destructor => $"{ContainingType!.FullName}.__dtor",
        FunctionKind.DropGlue => $"{ContainingType!.FullName}.__drop",
        FunctionKind.OwnershipDrop => $"{ContainingNamespace.FullName}.{Name}",
        _ => $"{ContainingNamespace.FullName}.{Name}",
    };

    public TypeSymbol ReturnType { get; }

    public ImmutableArray<ParameterSymbol> Parameters { get; }

    public ImmutableArray<GenericParameterSymbol> TypeParameters { get; } = [];

    public FunctionSymbol? GenericDefinition { get; private set; }
    public ImmutableArray<TypeSymbol> TypeArguments { get; private set; } = [];
    public bool IsGenericSpecialization => GenericDefinition is not null;

    public bool IsGenericDefinition => !TypeParameters.IsEmpty || ContainingStruct?.IsOpenGenericType == true;

    public FunctionKind FunctionKind { get; }

    public Accessibility Accessibility { get; }

    public bool IsExtern => Declaration is FunctionDeclarationSyntax { IsExtern: true };

    public bool IsExport => Declaration is FunctionDeclarationSyntax { IsExport: true };

    public bool IsPublic => Accessibility == Accessibility.Public;

    public bool HasImplicitThis => ContainingType is not null && ContainingInterface is null && !IsStatic;

    public bool IsStatic { get; }
    public bool IsVirtual { get; }
    public bool IsOverride { get; }
    public bool IsAbstract { get; }
    public bool IsReadonly { get; }

    public bool IsAccessor => ContainingProperty is not null || ContainingInterfaceProperty is not null ||
        ContainingIndexer is not null || ContainingInterfaceIndexer is not null;

    public override bool IsCompilerGenerated => FunctionKind is FunctionKind.InstanceInitializer or FunctionKind.DropGlue or FunctionKind.OwnershipDrop;
    public override bool IsUserVisible => FunctionKind is not (FunctionKind.InstanceInitializer or FunctionKind.DropGlue or FunctionKind.OwnershipDrop) && !IsAccessor;
    public override bool HasUserEditableIdentifier => base.HasUserEditableIdentifier && !IsAccessor;
    public override bool IsDefinition => Declaration switch
    {
        FunctionDeclarationSyntax syntax => syntax.Body is not null,
        MethodDeclarationSyntax syntax => syntax.Body is not null,
        ConstructorDeclarationSyntax => true,
        DestructorDeclarationSyntax => true,
        _ when FunctionKind == FunctionKind.DropGlue => true,
        _ when FunctionKind == FunctionKind.OwnershipDrop => true,
        PropertyAccessorDeclarationSyntax syntax => syntax.Body is not null,
        _ => false,
    };

    public int? VTableSlot { get; private set; }
    public int ConstructorOverload { get; private set; }
    public int ConstructorOverloadCount { get; private set; } = 1;

    internal void SetVTableSlot(int slot) => VTableSlot = slot;
    internal void SetReceiverMoveEffects(ImmutableArray<ReceiverMoveEffect> effects) =>
        ReceiverMoveEffects = effects;
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

    public bool HasSameSignature(FunctionSymbol candidate) =>
        FunctionKind == candidate.FunctionKind &&
        (ContainingProperty is not null || ContainingInterfaceProperty is not null) ==
            (candidate.ContainingProperty is not null || candidate.ContainingInterfaceProperty is not null) &&
        (ContainingIndexer is not null || ContainingInterfaceIndexer is not null) ==
            (candidate.ContainingIndexer is not null || candidate.ContainingInterfaceIndexer is not null) &&
        string.Equals(Name, candidate.Name, StringComparison.Ordinal) &&
        IsStatic == candidate.IsStatic &&
        IsReadonly == candidate.IsReadonly &&
        Parameters.Length == candidate.Parameters.Length &&
        Parameters.Zip(candidate.Parameters).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type));

    public bool Overrides(FunctionSymbol candidate) =>
        HasSameSignature(candidate) && TypeIdentity.AreSame(ReturnType, candidate.ReturnType);

    internal SyntaxNode Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Declaration is TypeDeclarationSyntax || FunctionKind == FunctionKind.OwnershipDrop ? [] : [new(Declaration)];
}

public readonly record struct ReceiverMoveEffect(ImmutableArray<int> FieldOrdinals);

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
    }

    public int Ordinal { get; }
    internal ParameterSyntax? Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Declaration is null ? [] : [new(Declaration)];

    // Each declaration owns its own parameters, including indexers and their accessors.
    internal static ImmutableArray<ParameterSymbol> Own(ImmutableArray<ParameterSymbol> parameters, Symbol owner) =>
        parameters.Select(parameter => new ParameterSymbol(parameter.Name, parameter.Type, parameter.Ordinal, parameter.IsReadonly, owner, parameter.Declaration)).ToImmutableArray();
}

public sealed class LocalVariableSymbol : VariableSymbol
{
    internal LocalVariableSymbol(string name, TypeSymbol type, FunctionSymbol containingFunction, bool isReadonly = false, VariableDeclarationStatementSyntax? declaration = null)
        : base(name, SymbolKind.LocalVariable, type, containingFunction, isReadonly)
    {
        Declaration = declaration;
    }

    internal VariableDeclarationStatementSyntax? Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Declaration is null ? [] : [new(Declaration)];
    public override bool IsDefinition => Declaration is not null;

    public ArrayStorageKind ArrayStorage { get; internal set; }
    public bool RequiresArrayCleanupTransfer { get; internal set; }
    public FunctionSymbol? Destructor { get; internal set; }
    internal Binding.BoundExpression? ConstantValue { get; set; }
}
