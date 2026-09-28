using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

// Stable exported identities. Value means the incoming value's dependencies;
// Borrow means the actual referent, not the argument's temporary descriptor.
public enum LifetimeDependencyKind : byte
{
    ParameterValue = 1, ParameterBorrow = 2,
    ReceiverValue = 3, ReceiverBorrow = 4,
    CaptureValue = 5, CaptureBorrow = 6,
}

public readonly record struct LifetimeDependency(LifetimeDependencyKind Kind, int Ordinal = -1, string FieldPath = "");

/// <param name="Destination">Parameter ordinal, -1 for receiver, -2 for static/unknown storage.</param>
public readonly record struct LifetimeStore(int Destination, LifetimeDependency Source, string FieldPath = "");

/// <summary>Compile-time provenance; never part of a value's runtime layout.</summary>
public readonly record struct ValueLifetimeDependency(Symbol? LocalOwner, LifetimeDependency? Input);

public sealed record ValueLifetimeDependencies(ImmutableArray<ValueLifetimeDependency> Origins);
