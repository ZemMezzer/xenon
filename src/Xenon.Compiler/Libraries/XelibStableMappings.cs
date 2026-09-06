using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Libraries;

/// <summary>Explicit boundary between compiler-internal enums and stable XELIB wire tags.</summary>
public static class XelibStableMappings
{
    public static XelibFunctionKind ToXelib(FunctionKind value) => value switch
    {
        FunctionKind.Ordinary => XelibFunctionKind.Ordinary,
        FunctionKind.Method => XelibFunctionKind.Method,
        FunctionKind.Constructor => XelibFunctionKind.Constructor,
        FunctionKind.InstanceInitializer => XelibFunctionKind.InstanceInitializer,
        FunctionKind.ThreadLocalInitializer => XelibFunctionKind.ThreadLocalInitializer,
        FunctionKind.Destructor => XelibFunctionKind.Destructor,
        FunctionKind.DestructorGlue => XelibFunctionKind.DestructorGlue,
        FunctionKind.OwnershipDestructor => XelibFunctionKind.OwnershipDestructor,
        FunctionKind.StorageDestructor => XelibFunctionKind.StorageDestructor,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static FunctionKind FromXelib(XelibFunctionKind value) => value switch
    {
        XelibFunctionKind.Ordinary => FunctionKind.Ordinary,
        XelibFunctionKind.Method => FunctionKind.Method,
        XelibFunctionKind.Constructor => FunctionKind.Constructor,
        XelibFunctionKind.InstanceInitializer => FunctionKind.InstanceInitializer,
        XelibFunctionKind.ThreadLocalInitializer => FunctionKind.ThreadLocalInitializer,
        XelibFunctionKind.Destructor => FunctionKind.Destructor,
        XelibFunctionKind.DestructorGlue => FunctionKind.DestructorGlue,
        XelibFunctionKind.OwnershipDestructor => FunctionKind.OwnershipDestructor,
        XelibFunctionKind.StorageDestructor => FunctionKind.StorageDestructor,
        _ => throw Invalid(nameof(value), value),
    };

    public static XelibAccessorKind ToXelib(AccessorKind value) => value switch
    {
        AccessorKind.None => XelibAccessorKind.None,
        AccessorKind.Getter => XelibAccessorKind.Getter,
        AccessorKind.Setter => XelibAccessorKind.Setter,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static AccessorKind FromXelib(XelibAccessorKind value) => value switch
    {
        XelibAccessorKind.None => AccessorKind.None,
        XelibAccessorKind.Getter => AccessorKind.Getter,
        XelibAccessorKind.Setter => AccessorKind.Setter,
        _ => throw Invalid(nameof(value), value),
    };

    public static XelibGenericConstraintKind ToXelib(GenericConstraintKind value) => value switch
    {
        GenericConstraintKind.BaseStruct => XelibGenericConstraintKind.BaseStruct,
        GenericConstraintKind.Interface => XelibGenericConstraintKind.Interface,
        GenericConstraintKind.StructuralTemplate => XelibGenericConstraintKind.StructuralTemplate,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static GenericConstraintKind FromXelib(XelibGenericConstraintKind value) => value switch
    {
        XelibGenericConstraintKind.BaseStruct => GenericConstraintKind.BaseStruct,
        XelibGenericConstraintKind.Interface => GenericConstraintKind.Interface,
        XelibGenericConstraintKind.StructuralTemplate => GenericConstraintKind.StructuralTemplate,
        _ => throw Invalid(nameof(value), value),
    };

    public static XelibReferenceReturnOriginKind ToXelib(ReferenceReturnOriginKind value) => value switch
    {
        ReferenceReturnOriginKind.Parameter => XelibReferenceReturnOriginKind.Parameter,
        ReferenceReturnOriginKind.Receiver => XelibReferenceReturnOriginKind.Receiver,
        ReferenceReturnOriginKind.Static => XelibReferenceReturnOriginKind.Static,
        ReferenceReturnOriginKind.Unknown => XelibReferenceReturnOriginKind.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static ReferenceReturnOriginKind FromXelib(XelibReferenceReturnOriginKind value) => value switch
    {
        XelibReferenceReturnOriginKind.Parameter => ReferenceReturnOriginKind.Parameter,
        XelibReferenceReturnOriginKind.Receiver => ReferenceReturnOriginKind.Receiver,
        XelibReferenceReturnOriginKind.Static => ReferenceReturnOriginKind.Static,
        XelibReferenceReturnOriginKind.Unknown => ReferenceReturnOriginKind.Unknown,
        _ => throw Invalid(nameof(value), value),
    };

    public static XelibSharedReturnOriginKind ToXelib(SharedReturnOriginKind value) => value switch
    {
        SharedReturnOriginKind.Fresh => XelibSharedReturnOriginKind.Fresh,
        SharedReturnOriginKind.Parameter => XelibSharedReturnOriginKind.Parameter,
        SharedReturnOriginKind.Unknown => XelibSharedReturnOriginKind.Unknown,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static SharedReturnOriginKind FromXelib(XelibSharedReturnOriginKind value) => value switch
    {
        XelibSharedReturnOriginKind.Fresh => SharedReturnOriginKind.Fresh,
        XelibSharedReturnOriginKind.Parameter => SharedReturnOriginKind.Parameter,
        XelibSharedReturnOriginKind.Unknown => SharedReturnOriginKind.Unknown,
        _ => throw Invalid(nameof(value), value),
    };

    public static XelibArrayStorageKind ToXelib(ArrayStorageKind value) => value switch
    {
        ArrayStorageKind.Unknown => XelibArrayStorageKind.Unknown,
        ArrayStorageKind.Heap => XelibArrayStorageKind.Heap,
        ArrayStorageKind.Stack => XelibArrayStorageKind.Stack,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static ArrayStorageKind FromXelib(XelibArrayStorageKind value) => value switch
    {
        XelibArrayStorageKind.Unknown => ArrayStorageKind.Unknown,
        XelibArrayStorageKind.Heap => ArrayStorageKind.Heap,
        XelibArrayStorageKind.Stack => ArrayStorageKind.Stack,
        _ => throw Invalid(nameof(value), value),
    };

    public static XelibMovedPlaceReinitializationKind ToXelib(MovedPlaceReinitializationState value) => value switch
    {
        MovedPlaceReinitializationState.Live => XelibMovedPlaceReinitializationKind.Live,
        MovedPlaceReinitializationState.DefinitelyMoved => XelibMovedPlaceReinitializationKind.DefinitelyMoved,
        MovedPlaceReinitializationState.MaybeMoved => XelibMovedPlaceReinitializationKind.MaybeMoved,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static MovedPlaceReinitializationState FromXelib(XelibMovedPlaceReinitializationKind value) => value switch
    {
        XelibMovedPlaceReinitializationKind.Live => MovedPlaceReinitializationState.Live,
        XelibMovedPlaceReinitializationKind.DefinitelyMoved => MovedPlaceReinitializationState.DefinitelyMoved,
        XelibMovedPlaceReinitializationKind.MaybeMoved => MovedPlaceReinitializationState.MaybeMoved,
        _ => throw Invalid(nameof(value), value),
    };

    private static XelibFormatException Invalid<T>(string name, T value) where T : Enum =>
        new(XelibErrorCode.InvalidRecord, $"unknown {name} tag {Convert.ToUInt64(value)}");
}
