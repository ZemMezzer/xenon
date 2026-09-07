using System.Collections.Immutable;

namespace Xenon.Compiler.Libraries;

public sealed record XelibLibraryIdentity(string Name, string? Version, string ContentIdentity)
{
    public override string ToString() => Version is null ? $"{Name}@{ContentIdentity}" :
        $"{Name}/{Version}@{ContentIdentity}";
}

public sealed record XelibManifest(
    string Name,
    string? Version,
    string ContentIdentity,
    ushort LanguageVersion,
    ushort LibraryIrVersion);

public sealed record XelibDependency(
    int Id,
    string Name,
    string? Version,
    string ContentIdentity);

public enum XelibTypeKind : ushort
{
    Primitive = 1,
    Declared = 2,
    GenericParameter = 3,
    TemplateSelf = 4,
    ConstructedGeneric = 5,
    Pointer = 6,
    Reference = 7,
    Array = 8,
    FunctionPointer = 9,
    Atomic = 10,
    Unique = 11,
    Shared = 12,
    Weak = 13,
    Storage = 14,
    Pin = 15,
    Char = 16,
}

public enum XelibSymbolKind : ushort
{
    Namespace = 1,
    Struct = 2,
    Interface = 3,
    Enum = 4,
    EnumMember = 5,
    Template = 6,
    Function = 7,
    Field = 8,
    Property = 9,
    Indexer = 10,
    Constant = 11,
    Parameter = 12,
    GenericParameter = 13,
    TemplateMethod = 14,
    TemplateConstructor = 15,
    TemplateProperty = 16,
    TemplateIndexer = 17,
}

/// <summary>Stable wire representation of source accessibility.</summary>
public enum XelibAccessibility : byte
{
    Private = 1,
    Internal = 2,
    Protected = 3,
    ProtectedInternal = 4,
    Public = 5,
}

[Flags]
public enum XelibSymbolFlags : uint
{
    None = 0,
    Public = 1 << 0,
    Static = 1 << 1,
    Readonly = 1 << 2,
    Virtual = 1 << 3,
    Override = 1 << 4,
    Abstract = 1 << 5,
    Extern = 1 << 6,
    Export = 1 << 7,
    Definition = 1 << 8,
    ThreadLocal = 1 << 9,
    HasInitializer = 1 << 10,
    DelegatesToThisConstructor = 1 << 11,
    HasVirtualDispatch = 1 << 12,
    HasStackArrays = 1 << 13,
    HasScalarCleanup = 1 << 14,
    ReadonlyStruct = 1 << 15,
    StaticStruct = 1 << 16,
    Sealed = 1 << 17,
}

public enum XelibFunctionKind : ushort
{
    Ordinary = 1,
    Method = 2,
    Constructor = 3,
    InstanceInitializer = 4,
    ThreadLocalInitializer = 5,
    Destructor = 6,
    DestructorGlue = 7,
    OwnershipDestructor = 8,
    StorageDestructor = 9,
}

public enum XelibAccessorKind : byte
{
    None = 0,
    Getter = 1,
    Setter = 2,
}

public enum XelibGenericConstraintKind : byte
{
    BaseStruct = 1,
    Interface = 2,
    StructuralTemplate = 3,
}

public enum XelibReferenceReturnOriginKind : byte
{
    Parameter = 1,
    Receiver = 2,
    Static = 3,
    Unknown = 4,
}

public enum XelibSharedReturnOriginKind : byte
{
    Fresh = 1,
    Parameter = 2,
    Unknown = 3,
}

public enum XelibArrayStorageKind : byte
{
    Unknown = 0,
    Heap = 1,
    Stack = 2,
}

public enum XelibMovedPlaceReinitializationKind : byte
{
    Live = 0,
    DefinitelyMoved = 1,
    MaybeMoved = 2,
}

public sealed record XelibSymbolReference(int LocalId, int DependencyId, string? ExportKey)
{
    public static XelibSymbolReference Local(int id) => new(id, 0, null);
    public static XelibSymbolReference External(int dependencyId, string exportKey) =>
        new(0, dependencyId, exportKey);
}

public sealed record XelibTypeRecord(
    int Id,
    XelibTypeKind Kind,
    string? PrimitiveName = null,
    XelibSymbolReference? Symbol = null,
    int ElementTypeId = 0,
    int ReturnTypeId = 0,
    ImmutableArray<int> ParameterTypeIds = default,
    ImmutableArray<int> TypeArgumentIds = default,
    bool IsReadonly = false,
    int Rank = 0);

public sealed record XelibParameterRecord(
    int Id,
    string Name,
    int TypeId,
    int Ordinal,
    bool IsReadonly);

public sealed record XelibConstraintRecord(
    XelibGenericConstraintKind Kind,
    XelibSymbolReference Target);

public sealed record XelibReferenceReturnOriginRecord(
    XelibReferenceReturnOriginKind Kind,
    int ParameterOrdinal,
    ImmutableArray<int> FieldOrdinals);

public sealed record XelibReferenceFieldOriginRecord(
    ImmutableArray<int> FieldOrdinals,
    XelibReferenceReturnOriginRecord Origin,
    bool IsReadonly);

public sealed record XelibSharedReturnOriginRecord(XelibSharedReturnOriginKind Kind, int ParameterOrdinal);

public sealed record XelibSymbolRecord
{
    public required int Id { get; init; }
    public required XelibSymbolKind Kind { get; init; }
    public required string Name { get; init; }
    public int ContainingSymbolId { get; init; }
    public int Order { get; init; }
    public XelibSymbolFlags Flags { get; init; }
    public XelibAccessibility Accessibility { get; init; }
    public int TypeId { get; init; }
    public int ReturnTypeId { get; init; }
    public int Ordinal { get; init; }
    public XelibFunctionKind FunctionKind { get; init; }
    public XelibAccessorKind AccessorKind { get; init; }
    public int? VTableSlot { get; init; }
    public int ConstructorOverload { get; init; }
    public int ConstructorOverloadCount { get; init; } = 1;
    public ImmutableArray<int> ParameterIds { get; init; } = [];
    public ImmutableArray<int> TypeParameterIds { get; init; } = [];
    public ImmutableArray<XelibConstraintRecord> Constraints { get; init; } = [];
    public int BaseTypeId { get; init; }
    public ImmutableArray<int> InterfaceTypeIds { get; init; } = [];
    public ImmutableArray<int> RelatedSymbolIds { get; init; } = [];
    public int GetterId { get; init; }
    public int SetterId { get; init; }
    public bool HasGetter { get; init; }
    public bool HasSetter { get; init; }
    public XelibConstantValue? ConstantValue { get; init; }
    public XelibBodyNode? ConstantExpression { get; init; }
    public ImmutableArray<ImmutableArray<int>> ReceiverMoveEffects { get; init; } = [];
    public ImmutableArray<XelibReferenceReturnOriginRecord> ReferenceReturnOrigins { get; init; } = [];
    public ImmutableArray<XelibSharedReturnOriginRecord> SharedReturnOrigins { get; init; } = [];
    public ImmutableArray<XelibReferenceFieldOriginRecord> ReferenceFieldOrigins { get; init; } = [];
}

public enum XelibConstantKind : byte
{
    Null = 0,
    Boolean = 1,
    SignedInteger = 2,
    UnsignedInteger = 3,
    FloatingPoint = 4,
    String = 5,
    UnicodeScalar = 6,
}

public sealed record XelibConstantValue(XelibConstantKind Kind, string? Value);

public sealed record XelibExport(string Key, int SymbolId);

public sealed record XelibDocumentation(
    int SymbolId,
    string? Summary,
    string? Remarks,
    string? Returns,
    ImmutableSortedDictionary<string, string> Parameters,
    ImmutableSortedDictionary<string, string> TypeParameters);

public sealed record XelibBodyRecord(int Id, int FunctionSymbolId, XelibBodyNode Root);

public enum XelibBodyOpcode : ushort
{
    Block = 1,
    VariableDeclaration = 2,
    Return = 3,
    ExpressionStatement = 4,
    If = 5,
    While = 6,
    For = 7,
    Switch = 8,
    SwitchSection = 9,
    Break = 10,
    Continue = 11,
    Literal = 20,
    Variable = 21,
    This = 22,
    Unary = 23,
    Move = 24,
    Copy = 25,
    FullExpression = 26,
    Binary = 27,
    Assignment = 28,
    CompareExchange = 29,
    Swap = 30,
    CompoundAccessorAssignment = 31,
    Call = 32,
    IndirectCall = 33,
    FunctionAddress = 34,
    MethodCall = 35,
    PropertySet = 36,
    InterfacePropertySet = 37,
    IndexerSet = 38,
    InterfaceIndexerSet = 39,
    MemberAccess = 40,
    StaticField = 41,
    TypeLayout = 42,
    Cast = 43,
    InterfaceConversion = 44,
    ReferenceConversion = 45,
    ReferenceDereference = 46,
    LifetimeValue = 47,
    DefaultValue = 48,
    StorageConstruct = 49,
    ExplicitDestruct = 50,
    StorageMove = 51,
    InterfaceMethodCall = 52,
    Index = 53,
    StructConstruction = 54,
    ConstructorCall = 55,
    BaseLifecycleCall = 56,
    DestroyFields = 57,
    OwnershipDestruction = 58,
    StorageDestruction = 59,
    UniqueAdoption = 60,
    SharedAdoption = 61,
    WeakConversion = 62,
    Lock = 63,
    ArrayCreation = 64,
    ArrayMetadata = 65,
    New = 66,
    Free = 67,
    DeferredConstant = 68,
    DeferredGenericMethodCall = 69,
    DeferredGenericFunctionCall = 70,
    DeferredGenericFieldGet = 71,
    DeferredGenericFieldSet = 72,
    DeferredGenericPropertyGet = 73,
    DeferredGenericPropertySet = 74,
    DeferredGenericIndexerGet = 75,
    DeferredGenericIndexerSet = 76,
    DeferredGenericConstruction = 77,
    DeferredGenericAllocation = 78,
}

public enum XelibOperator : ushort
{
    None = 0,
    Plus = 1,
    Minus = 2,
    Multiply = 3,
    Divide = 4,
    Remainder = 5,
    Assign = 6,
    Equal = 7,
    Not = 8,
    NotEqual = 9,
    Less = 10,
    LessOrEqual = 11,
    Greater = 12,
    GreaterOrEqual = 13,
    BitAnd = 14,
    LogicalAnd = 15,
    BitOr = 16,
    LogicalOr = 17,
    Xor = 18,
    Complement = 19,
    ShiftLeft = 20,
    ShiftRight = 21,
    Increment = 22,
    Decrement = 23,
    AddAssign = 24,
    SubtractAssign = 25,
    MultiplyAssign = 26,
    DivideAssign = 27,
    RemainderAssign = 28,
    AndAssign = 29,
    OrAssign = 30,
    XorAssign = 31,
    ShiftLeftAssign = 32,
    ShiftRightAssign = 33,
    SizeOf = 40,
    AlignOf = 41,
    OffsetOf = 42,
    Cast = 43,
    BitCast = 44,
}

public sealed record XelibLocalRecord(
    int Id,
    string Name,
    int TypeId,
    bool IsReadonly,
    XelibArrayStorageKind ArrayStorage,
    bool RequiresArrayCleanupTransfer,
    XelibSymbolReference? Destructor);

public sealed record XelibTemporaryRecord(XelibBodyNode Value, XelibSymbolReference Destructor);

public sealed record XelibBodyNode
{
    public required XelibBodyOpcode Opcode { get; init; }
    public int TypeId { get; init; }
    public int AuxTypeId { get; init; }
    public XelibOperator Operator { get; init; }
    public XelibConstantValue? Constant { get; init; }
    public XelibSymbolReference? Symbol { get; init; }
    public XelibSymbolReference? Symbol2 { get; init; }
    public XelibSymbolReference? Symbol3 { get; init; }
    public int LocalId { get; init; }
    public string? Text { get; init; }
    public int Integer { get; init; }
    public XelibArrayStorageKind ArrayStorage { get; init; }
    public XelibMovedPlaceReinitializationKind MovedPlaceReinitialization { get; init; }
    public bool Flag1 { get; init; }
    public bool Flag2 { get; init; }
    public bool Flag3 { get; init; }
    public ImmutableArray<int> Integers { get; init; } = [];
    public ImmutableArray<XelibSymbolReference> Symbols { get; init; } = [];
    public ImmutableArray<XelibLocalRecord> Locals { get; init; } = [];
    public ImmutableArray<XelibTemporaryRecord> Temporaries { get; init; } = [];
    public ImmutableArray<XelibBodyNode> Children { get; init; } = [];
}

public sealed record XelibGenericImplementation(
    int DefinitionSymbolId,
    int BodyId,
    bool IsStruct,
    ImmutableArray<XelibGenericFieldInitializer> StaticFieldInitializers = default,
    ImmutableArray<XelibGenericConstantImplementation> Constants = default);

public sealed record XelibGenericFieldInitializer(int FieldSymbolId, int BodyId);

public sealed record XelibGenericConstantImplementation(int ConstantSymbolId, XelibBodyNode Expression);
