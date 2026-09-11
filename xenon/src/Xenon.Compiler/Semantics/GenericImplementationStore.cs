using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

/// <summary>
/// Immutable compile-time implementations exported by a compilation reference.
/// The payload is intentionally opaque to callers; future library references can
/// populate the same store from serialized library IR.
/// </summary>
public sealed class GenericImplementationStore
{
    private readonly ImmutableDictionary<FunctionSymbol, IGenericFunctionImplementation> _functions;
    private readonly ImmutableDictionary<StructTypeSymbol, IGenericStructImplementation> _structs;

    internal GenericImplementationStore(
        ImmutableDictionary<FunctionSymbol, IGenericFunctionImplementation> functions,
        ImmutableDictionary<StructTypeSymbol, IGenericStructImplementation> structs)
    {
        _functions = functions;
        _structs = structs;
    }

    public static GenericImplementationStore Empty { get; } = new(
        ImmutableDictionary.Create<FunctionSymbol, IGenericFunctionImplementation>(
            ReferenceEqualityComparer.Instance),
        ImmutableDictionary.Create<StructTypeSymbol, IGenericStructImplementation>(
            ReferenceEqualityComparer.Instance));

    public bool IsEmpty => _functions.IsEmpty && _structs.IsEmpty;
    internal int FunctionCount => _functions.Count;
    internal int StructCount => _structs.Count;

    internal bool TryGetFunction(FunctionSymbol definition,
        out IGenericFunctionImplementation implementation) =>
        _functions.TryGetValue(definition, out implementation!);

    internal bool TryGetStruct(StructTypeSymbol definition,
        out IGenericStructImplementation implementation) =>
        _structs.TryGetValue(definition, out implementation!);

    internal IEnumerable<KeyValuePair<FunctionSymbol, IGenericFunctionImplementation>> Functions => _functions;
    internal IEnumerable<KeyValuePair<StructTypeSymbol, IGenericStructImplementation>> Structs => _structs;
}

internal sealed class GenericImplementationStoreBuilder : IGenericImplementationProvider
{
    private readonly Dictionary<FunctionSymbol, IGenericFunctionImplementation> _functions =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructTypeSymbol, IGenericStructImplementation> _structs =
        new(ReferenceEqualityComparer.Instance);

    public GenericImplementationStoreBuilder(IEnumerable<GenericImplementationStore> referencedStores)
    {
        foreach (GenericImplementationStore store in referencedStores)
        {
            foreach (var entry in store.Functions)
                _functions.TryAdd(entry.Key, entry.Value);
            foreach (var entry in store.Structs)
                _structs.TryAdd(entry.Key, entry.Value);
        }
    }

    public void AddFunction(FunctionSymbol definition, IGenericFunctionImplementation implementation) =>
        _functions.TryAdd(definition, implementation);

    public void AddStruct(StructTypeSymbol definition, IGenericStructImplementation implementation) =>
        _structs.TryAdd(definition, implementation);

    public bool TryGetFunction(FunctionSymbol definition,
        out IGenericFunctionImplementation implementation) =>
        _functions.TryGetValue(definition, out implementation!);

    public bool TryGetStruct(StructTypeSymbol definition,
        out IGenericStructImplementation implementation) =>
        _structs.TryGetValue(definition, out implementation!);

    public GenericImplementationStore ToImmutable() => new(
        _functions.ToImmutableDictionary(ReferenceEqualityComparer.Instance),
        _structs.ToImmutableDictionary(ReferenceEqualityComparer.Instance));
}

internal interface IGenericImplementationProvider
{
    bool TryGetFunction(FunctionSymbol definition, out IGenericFunctionImplementation implementation);
    bool TryGetStruct(StructTypeSymbol definition, out IGenericStructImplementation implementation);
}
