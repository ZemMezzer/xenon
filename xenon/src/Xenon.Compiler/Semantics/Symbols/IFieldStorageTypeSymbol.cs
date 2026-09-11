using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>Aggregate storage projected through instance fields. This capability does not imply struct inheritance or virtual dispatch.</summary>
public interface IFieldStorageTypeSymbol
{
    ImmutableArray<FieldSymbol> AllInstanceFields { get; }
}
