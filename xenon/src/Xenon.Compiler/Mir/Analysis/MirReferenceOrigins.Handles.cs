using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

public sealed partial class MirReferenceOrigins
{
    private static bool IsHandle(TypeSymbol type) => type is PointerTypeSymbol or OwnershipTypeSymbol;
    private static bool IsPointee(MirReferenceOrigin origin) => origin.Kind is MirReferenceOriginKind.RawPointee or MirReferenceOriginKind.UniquePointee or MirReferenceOriginKind.SharedPointee;
    private static TypeSymbol? Element(TypeSymbol type) => type switch
    {
        PointerTypeSymbol pointer => pointer.ElementType,
        OwnershipTypeSymbol owner => owner.ElementType,
        _ => null,
    };
    private static MirReferenceValue Handle(TypeSymbol type, MirLocalId local, string identity, bool fresh = false, int? parameter = null) =>
        MirReferenceValue.Of(new(type switch
        {
            UniqueTypeSymbol => MirReferenceOriginKind.UniquePointee,
            SharedTypeSymbol or WeakTypeSymbol => MirReferenceOriginKind.SharedPointee,
            _ => MirReferenceOriginKind.RawPointee,
        }, local.Value)
        {
            IsFresh = fresh, HandleParameter = parameter, HandleIdentity = identity + ":" + local.Value,
            PointeeType = Element(type),
        });
    // Seed opaque owning fields at value creation. Read must preserve lattice bottom:
    // manufacturing an origin only when a read is empty makes loop transfer non-monotone.
    private static MirReferenceValue OwnedFields(TypeSymbol type, MirLocalId site, string identity)
    {
        MirReferenceValue result = MirReferenceValue.Empty;
        Visit(type, "", []);
        return result;
        void Visit(TypeSymbol current, string path, HashSet<TypeSymbol> seen)
        {
            if (current is OwnershipTypeSymbol)
            {
                result = result.Store(path, Handle(current, site, identity + ":" + path), true);
                return;
            }
            if (current is LifetimeModifierTypeSymbol modifier) { Visit(modifier.ElementType, path, seen); return; }
            if (current is not IFieldStorageTypeSymbol structure || !seen.Add(current)) return;
            foreach (var field in structure.AllInstanceFields)
                Visit(field.Type, MirReferenceOrigin.Append(path, field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)), seen);
            seen.Remove(current);
        }
    }
    private static MirReferenceValue StoredHandle(TypeSymbol type, MirLocalId local, MirReferenceValue value) =>
        !value.Roots.IsEmpty && value.Roots.All(IsPointee) ? value : Handle(type, local, "storage");
    private MirReferenceValue Cast(MirCast cast, ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        var value = Operand(cast.Operand, state);
        if (cast.TargetType is ReferenceTypeSymbol { IsReadonly: true }) return Readonly(value);
        if (IsHandle(cast.TargetType)) return value with
        {
            Roots = value.Roots.Select(origin => IsPointee(origin) ? origin with { PointeeType = Element(cast.TargetType) } : origin).ToImmutableHashSet(),
        };
        return value;
    }
    private MirReferenceValue CallHandle(FunctionSymbol callee, MirCall call, MirLocalId result,
        ImmutableDictionary<MirLocalId, MirReferenceValue> state)
    {
        if (callee.ReturnType is not SharedTypeSymbol) return Handle(callee.ReturnType, result, "call", fresh: true);
        var origins = callee.SharedReturnOrigins.IsEmpty && callee.GenericDefinition is { } definition ? definition.SharedReturnOrigins : callee.SharedReturnOrigins;
        if (!callee.IsVirtual && !callee.IsOverride && call.InterfaceType is null && origins.Length == 1)
        {
            var origin = origins[0];
            if (origin.Kind == SharedReturnOriginKind.Fresh) return Handle(callee.ReturnType, result, "call", fresh: true);
            if (origin.Kind == SharedReturnOriginKind.Parameter && origin.ParameterOrdinal >= 0 && origin.ParameterOrdinal < call.Arguments.Length)
                return Operand(call.Arguments[origin.ParameterOrdinal], state);
        }
        return Handle(callee.ReturnType, result, "call");
    }
    public ImmutableArray<SharedReturnOrigin> SharedReturns(CancellationToken cancellation = default) =>
        Returns(cancellation).Select(origin => origin.IsFresh ? new SharedReturnOrigin(SharedReturnOriginKind.Fresh, -1) :
            origin.HandleParameter is { } parameter ? new SharedReturnOrigin(SharedReturnOriginKind.Parameter, parameter) :
            new SharedReturnOrigin(SharedReturnOriginKind.Unknown, -1)).Distinct().OrderBy(origin => origin.Kind)
            .ThenBy(origin => origin.ParameterOrdinal).ToImmutableArray();
}