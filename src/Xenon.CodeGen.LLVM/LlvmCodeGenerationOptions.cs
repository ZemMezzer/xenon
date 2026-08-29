using System.Collections.Immutable;
using Xenon.Compiler;

namespace Xenon.CodeGen.LLVM;

/// <summary>The native linkage used for one semantic compilation reference.</summary>
public enum LlvmNativeReferenceKind
{
    Static,
    Shared,
}

public sealed class LlvmNativeReference
{
    public LlvmNativeReference(
        Compilation compilation,
        LlvmNativeReferenceKind kind,
        string abiIdentity)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        ArgumentException.ThrowIfNullOrWhiteSpace(abiIdentity);
        Compilation = compilation;
        Kind = kind;
        AbiIdentity = abiIdentity;
    }

    public Compilation Compilation { get; }
    public LlvmNativeReferenceKind Kind { get; }
    public string AbiIdentity { get; }
}

/// <summary>
/// Authoritative build-only ABI identity and exact native linkage metadata.
/// It deliberately does not participate in semantic visibility or symbol binding.
/// </summary>
public sealed class LlvmCodeGenerationOptions
{
    public LlvmCodeGenerationOptions(
        string abiIdentity,
        IEnumerable<LlvmNativeReference>? nativeReferences = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(abiIdentity);
        AbiIdentity = abiIdentity;
        NativeReferences = nativeReferences?.ToImmutableArray() ?? [];
        if (NativeReferences.Any(reference => reference is null))
            throw new ArgumentException("Native references cannot contain null entries.", nameof(nativeReferences));
        var seen = new HashSet<Compilation>(ReferenceEqualityComparer.Instance);
        if (NativeReferences.Any(reference => !seen.Add(reference.Compilation)))
            throw new ArgumentException(
                "Duplicate native metadata for the same compilation snapshot.",
                nameof(nativeReferences));
    }

    public string AbiIdentity { get; }
    public ImmutableArray<LlvmNativeReference> NativeReferences { get; }
}
