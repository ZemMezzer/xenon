using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Analysis;

// Shared vocabulary for operations, as distinct from the state observed at exit.
// Borrow checking consumes invalidating operations even when later reconstruction
// makes the pointer's final object state live again.
public enum MirLifetimeTransition
{
    ObjectConstruction, ObjectDestruction, StorageAllocation, StorageDeallocation,
    PlacementReconstruction, OwnershipTransfer, BorrowCreation, BorrowInvalidation,
    ReferenceEscape, StorageRead,
}
public enum MirLifetimeEffectPhase { Exit, Operation }
public sealed record MirLifetimeExit(MirPointerParameterGuard? Guard, MirEdgeKind Exit, MirConstant? Result = null, TypeSymbol? ExceptionType = null);
// Missing operation coverage is separate from incomplete exit-state proofs.
// Null coverage denotes a legacy contract with no target information.
public sealed record MirLifetimeUncertainty(int Parameter, string Path, bool UnknownOffset,
    MirPointerParameterGuard? Guard, MirLifetimeTransition Transition, string Reason);
public sealed record MirLifetimeSummary(ImmutableHashSet<MirLifetimeEffect> Effects, ImmutableHashSet<MirLifetimeExit> Exits,
    ImmutableSortedSet<string> IncompleteReasons, int Contexts = 0)
{
    public ImmutableHashSet<MirLifetimeUncertainty>? Uncertainties { get; init; }
    public bool IsImported { get; init; }
    public bool ReadCoverageComplete { get; init; } = true;
    public bool Complete => IncompleteReasons.IsEmpty;
    public static MirLifetimeSummary Empty { get; } = new([], [], ImmutableSortedSet.Create<string>(StringComparer.Ordinal)) { Uncertainties = [] };
}

public sealed record MirLifetimeEffect(int Parameter, string Path, bool UnknownOffset, bool Storage,
    MirPointerParameterGuard? Guard, MirEdgeKind Exit, MirSourceInfo Source, MirConstant? Result = null,
    TypeSymbol? ExceptionType = null, MirLifetimeEffectPhase Phase = MirLifetimeEffectPhase.Exit,
    MirLifetimeTransition Transition = MirLifetimeTransition.ObjectDestruction)
{
    public MirLifetimeTransition Kind => Storage ? MirLifetimeTransition.StorageDeallocation : Transition;
    public bool InvalidatesBorrow => Kind is MirLifetimeTransition.ObjectDestruction or
        MirLifetimeTransition.StorageDeallocation or MirLifetimeTransition.OwnershipTransfer or MirLifetimeTransition.BorrowInvalidation;
}

internal static class MirLifetimeOperations
{
    internal static IEnumerable<MirLifetimeUncertainty> Coverage(MirCall call, MirLifetimeSummary? summary) =>
        MirPointerLifetimeAnalysis.DirectCallee(call) is { } function ? Coverage(function, summary) :
        call.Arguments.SelectMany((argument, ordinal) => Paths(argument.Type, "", false, 0)
            .Select(path => new MirLifetimeUncertainty(ordinal, path, true, null,
                MirLifetimeTransition.BorrowInvalidation, "indirect-callee-contract")));

    internal static IEnumerable<MirLifetimeUncertainty> Coverage(FunctionSymbol function, MirLifetimeSummary? summary)
    {
        if (summary?.Uncertainties is { } scoped) return scoped;
        if (summary?.Complete == true) return [];
        return function.Parameters.SelectMany(parameter => Paths(parameter.Type, "", false, 0)
            .Select(path => new MirLifetimeUncertainty(parameter.Ordinal, path, true, null,
                MirLifetimeTransition.BorrowInvalidation, summary is null ? "missing-callee-contract" : "legacy-incomplete-contract")));
    }

    // Conservative fallback for old metadata. Follow handle loads as well as
    // carrier fields; a T*& or T** can invalidate a loan of the loaded T.
    private static IEnumerable<string> Paths(TypeSymbol type, string path, bool load, int depth)
    {
        if (depth >= 7) { yield return MirReferenceOrigin.Append(path, "*"); yield break; }
        if (type is LifetimeModifierTypeSymbol modifier)
        { foreach (var item in Paths(modifier.ElementType, path, load, depth)) yield return item; yield break; }
        if (type is FunctionValueTypeSymbol) { yield return path; yield break; }
        if (type is ReferenceTypeSymbol reference)
        {
            if (!reference.IsReadonly) yield return path;
            foreach (var item in Paths(reference.ElementType, path, true, depth + 1)) yield return item;
        }
        else if (type is PointerTypeSymbol or OwnershipTypeSymbol or ArrayTypeSymbol)
        {
            if (load) path = MirReferenceOrigin.Append(path, "^");
            yield return path;
            var element = type switch { PointerTypeSymbol p => p.ElementType, OwnershipTypeSymbol p => p.ElementType, ArrayTypeSymbol p => p.ElementType, _ => type };
            foreach (var item in Paths(element, path, true, depth + 1)) yield return item;
        }
        else if (type is IFieldStorageTypeSymbol structure)
            foreach (var field in structure.AllInstanceFields)
                foreach (var item in Paths(field.Type, MirReferenceOrigin.Append(path, field.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)), load, depth + 1)) yield return item;
    }

    public static MirLifetimeTransition? Of(MirStatement statement) => statement switch
    {
        MirAssign { WriteKind: MirWriteKind.RawPlacement } => MirLifetimeTransition.PlacementReconstruction,
        MirAssign { IsMoveRead: true } => MirLifetimeTransition.OwnershipTransfer,
        MirAssign { Value: MirBorrow { Kind: not MirBorrowKind.Raw } } => MirLifetimeTransition.BorrowCreation,
        MirStorageLive => MirLifetimeTransition.StorageAllocation,
        MirStorageDead => MirLifetimeTransition.StorageDeallocation,
        _ => null,
    };
    public static MirLifetimeTransition? Of(MirTerminator terminator) => terminator switch
    {
        MirDrop => MirLifetimeTransition.ObjectDestruction,
        MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Free } => MirLifetimeTransition.StorageDeallocation,
        MirIntrinsicCall { Intrinsic: MirIntrinsicKind.Allocate or MirIntrinsicKind.AllocateHeapArray or
            MirIntrinsicKind.AllocateStackArray or MirIntrinsicKind.Malloc or MirIntrinsicKind.Calloc or MirIntrinsicKind.AlignedMalloc } => MirLifetimeTransition.StorageAllocation,
        MirReturn { Value: not null } => MirLifetimeTransition.ReferenceEscape,
        _ => null,
    };
}
