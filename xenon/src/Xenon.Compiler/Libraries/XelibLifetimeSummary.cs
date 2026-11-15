using System.Collections.Immutable;
using System.Globalization;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Libraries;

// Optional, independently versioned contract. Parameter ordinals, structural
// paths and type-table references are stable; no MIR local or allocation IDs.
public sealed record XelibLifetimeGuard(int Parameter, bool Value, string? Operator, XelibConstantValue? Constant);
public sealed record XelibLifetimeExit(ImmutableArray<XelibLifetimeGuard> Guards, string Exit,
    XelibConstantValue? Result, int ResultType, int ExceptionType);
public sealed record XelibLifetimeEffect(int Parameter, string Path, bool UnknownOffset, string Transition,
    string Phase, XelibLifetimeExit When);
public sealed record XelibPointerReturn(string Field, int Parameter, string Path, bool UnknownOffset);
public sealed record XelibLifetimeUncertainty(int Parameter, string Path, bool UnknownOffset, string Transition,
    XelibLifetimeExit When, string Reason);
public sealed record XelibLifetimeSummaryRecord(int Version, ImmutableArray<XelibLifetimeEffect> Effects,
    ImmutableArray<XelibLifetimeExit> Exits, ImmutableArray<string> IncompleteReasons,
    ImmutableArray<XelibPointerReturn> PointerReturns, bool HasPointerReturn)
{
    public ImmutableArray<XelibLifetimeUncertainty>? Uncertainties { get; init; }
    // Old v1 records described writes/lifetime transitions, not raw reads.
    // Absence must not certify an exclusive loan across a call.
    public bool ReadCoverageComplete { get; init; }
}

internal static class XelibLifetimeSummary
{
    internal static IEnumerable<TypeSymbol> Types(MirLifetimeSummary summary) =>
        summary.Effects.SelectMany(effect => new[] { effect.Result?.Type, effect.ExceptionType })
            .Concat(summary.Exits.SelectMany(exit => new[] { exit.Result?.Type, exit.ExceptionType })).OfType<TypeSymbol>();

    internal static XelibLifetimeSummaryRecord? Write(FunctionSymbol function, Func<TypeSymbol, int> typeId)
    {
        if (function.MirLifetimeSummary is not { } summary) return null;
        bool Supported(MirConstant? result) => result is null ||
            result.Value is null && result.Type is PointerTypeSymbol or ArrayTypeSymbol ||
            result.Value is bool && TypeIdentity.AreSame(result.Type, BuiltinTypes.Bool) ||
            TypeFacts.IsInteger(result.Type) || TypeFacts.IsCharacter(result.Type) ||
            TypeIdentity.AreSame(result.Type, BuiltinTypes.Float) || TypeIdentity.AreSame(result.Type, BuiltinTypes.Double);
        ImmutableArray<XelibLifetimeGuard> Guards(MirPointerParameterGuard? guard)
        {
            var result = ImmutableArray.CreateBuilder<XelibLifetimeGuard>();
            for (; guard is not null; guard = guard.Next)
                result.Add(new(guard.Parameter, guard.Value, guard.Operator?.ToString(),
                    guard.Operator is null ? null : XelibIrBuilder.Constant(guard.Constant, function.Parameters[guard.Parameter].Type)));
            return result.ToImmutable();
        }
        XelibLifetimeExit Exit(MirPointerParameterGuard? guard, MirEdgeKind exit, MirConstant? result, TypeSymbol? error)
        {
            if (!Supported(result)) result = null;
            return new(Guards(guard), exit.ToString(), result is null ? null : XelibIrBuilder.Constant(result.Value, result.Type),
                result is null ? 0 : typeId(result.Type), error is null ? 0 : typeId(error));
        }
        var returns = ImmutableArray.CreateBuilder<XelibPointerReturn>();
        if (function.MirPointerReturnSummary is { } value)
        {
            Add("", value.Roots);
            foreach (var (field, roots) in value.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal)) Add(field, roots);
        }
        void Add(string field, IEnumerable<MirReferenceOrigin> roots)
        {
            if (!IsTypePath(function.ReturnType, field)) return;
            foreach (var root in roots)
            {
                int parameter = root.HandleParameter ?? (root.Kind == MirReferenceOriginKind.Parameter ? root.Ordinal : -1);
                if (parameter >= 0 && !IsTypePath(function.Parameters[parameter].Type, root.Path)) parameter = -1;
                // Fresh/static identities are deliberately opaque across this
                // contract version. Retain an unknown alternative, never drop it.
                returns.Add(new(field, parameter, parameter < 0 ? "" : root.Path, parameter >= 0 && root.UnknownObjectOffset));
            }
        }
        var effects = summary.Effects.Where(effect => Supported(effect.Result)).ToArray();
        var reasons = effects.Length == summary.Effects.Count ? summary.IncompleteReasons : summary.IncompleteReasons.Add("unrepresentable-result");
        return new(1, [.. effects.OrderBy(MirPointerLifetimeAnalysis.EffectKey, StringComparer.Ordinal).Select(effect =>
            new XelibLifetimeEffect(effect.Parameter, effect.Path, effect.UnknownOffset, effect.Kind.ToString(), effect.Phase.ToString(),
                Exit(effect.Guard, effect.Exit, effect.Result, effect.ExceptionType)))],
            [.. summary.Exits.OrderBy(MirPointerLifetimeAnalysis.ExitKey, StringComparer.Ordinal).Select(exit => Exit(exit.Guard, exit.Exit, exit.Result, exit.ExceptionType))],
            [.. reasons], [.. returns.Distinct().OrderBy(value => value.Field, StringComparer.Ordinal)
                .ThenBy(value => value.Parameter).ThenBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.UnknownOffset)],
            function.MirPointerReturnSummary is not null)
        {
            ReadCoverageComplete = summary.ReadCoverageComplete,
            Uncertainties = summary.Uncertainties is null ? null : [.. summary.Uncertainties
                .OrderBy(item => item.Parameter).ThenBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => MirPointerLifetimeAnalysis.GuardKey(item.Guard), StringComparer.Ordinal)
                .ThenBy(item => item.Transition).ThenBy(item => item.UnknownOffset).ThenBy(item => item.Reason, StringComparer.Ordinal)
                .Select(item => new XelibLifetimeUncertainty(item.Parameter, item.Path, item.UnknownOffset, item.Transition.ToString(),
                    Exit(item.Guard, MirEdgeKind.Normal, null, null), item.Reason))],
        };
    }

    internal static void Read(XelibLifetimeSummaryRecord record, FunctionSymbol function,
        Func<int, TypeSymbol> type, Func<XelibConstantValue?, TypeSymbol, object?> constant)
    {
        void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid)
        { if (!valid) throw XelibReader.Invalid("invalid or unsupported lifetime summary", null); }
        Require(record.Version == 1 && !record.Effects.IsDefault && !record.Exits.IsDefault &&
            !record.IncompleteReasons.IsDefault && !record.PointerReturns.IsDefault);
        Require(record.Effects.Length <= 4096 && record.Exits.Length <= 4096 && record.PointerReturns.Length <= 4096 &&
            record.IncompleteReasons.Length <= 32 && record.IncompleteReasons.All(reason => !string.IsNullOrWhiteSpace(reason) && reason.Length <= 128));
        Require(record.HasPointerReturn || record.PointerReturns.IsEmpty);
        void Parameter(int ordinal) => Require(ordinal >= 0 && ordinal < function.Parameters.Length);
        void Path(string path)
        {
            Require(path is not null && path.Length <= 256);
            if (path.Length == 0) return;
            var parts = path.Split('/');
            Require(parts.Length <= 8 && parts.All(part => part is "^" or "*" ||
                int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int field) && field >= 0 && field.ToString(CultureInfo.InvariantCulture) == part ||
                part.Length > 2 && part[0] == '[' && part[^1] == ']' && part[1..^1].Split(',').All(index =>
                    long.TryParse(index, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) && value.ToString(CultureInfo.InvariantCulture) == index)));
        }
        void TargetPath(TypeSymbol current, string path)
        {
            Path(path);
            if (!IsTypePath(current, path)) throw XelibReader.Invalid($"invalid lifetime path '{path}' for '{current}' in '{function.FullName}'", null);
        }
        object? Constant(XelibConstantValue literal, TypeSymbol target)
        {
            if (target is PrimitiveTypeSymbol { IsInteger: true } integer)
            {
                Require(literal.Kind == (integer.IsSigned ? XelibConstantKind.SignedInteger : XelibConstantKind.UnsignedInteger));
                Require(System.Numerics.BigInteger.TryParse(literal.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number));
                int bits = integer.BitWidth ?? 64;
                var limit = System.Numerics.BigInteger.One << (integer.IsSigned ? bits - 1 : bits);
                Require(number < limit && number >= (integer.IsSigned ? -limit : System.Numerics.BigInteger.Zero));
            }
            return constant(literal, target);
        }
        MirLifetimeExit Exit(XelibLifetimeExit value)
        {
            Require(value is not null && !value.Guards.IsDefault && value.Guards.Length <= 64);
            Require(value.Exit is "Normal" or "Unwind");
            Require((value.Result is null) == (value.ResultType == 0) && value.ResultType >= 0 && value.ExceptionType >= 0 &&
                (value.Exit == "Normal" ? value.ExceptionType == 0 : value.Result is null));
            MirPointerParameterGuard? guard = null;
            foreach (var item in value.Guards.Reverse())
            {
                Require(item is not null); Parameter(item.Parameter);
                var parameterType = function.Parameters[item.Parameter].Type;
                MirBinaryOperator? op = item.Operator switch
                {
                    null => null, "Equal" => MirBinaryOperator.Equal, "NotEqual" => MirBinaryOperator.NotEqual,
                    "Less" => MirBinaryOperator.Less, "LessOrEqual" => MirBinaryOperator.LessOrEqual,
                    "Greater" => MirBinaryOperator.Greater, "GreaterOrEqual" => MirBinaryOperator.GreaterOrEqual,
                    _ => throw XelibReader.Invalid("invalid lifetime guard operator", null),
                };
                Require(op is null ? TypeIdentity.AreSame(parameterType, BuiltinTypes.Bool) && item.Constant is null :
                    TypeFacts.IsInteger(parameterType) && item.Constant?.Kind is XelibConstantKind.SignedInteger or XelibConstantKind.UnsignedInteger ||
                    op is not null && TypeFacts.IsCharacter(parameterType) && item.Constant?.Kind == XelibConstantKind.UnicodeScalar);
                guard = new(item.Parameter, item.Value, guard, op, op is null ? null : Constant(item.Constant!, parameterType));
            }
            MirConstant? result = null;
            if (value.Result is { } literal)
            {
                var resultType = type(value.ResultType);
                Require(TypeIdentity.AreSame(resultType, function.ReturnType));
                Require(literal.Kind switch
                {
                    XelibConstantKind.Null => resultType is PointerTypeSymbol or ArrayTypeSymbol,
                    XelibConstantKind.Boolean => TypeIdentity.AreSame(resultType, BuiltinTypes.Bool),
                    XelibConstantKind.SignedInteger or XelibConstantKind.UnsignedInteger => TypeFacts.IsInteger(resultType),
                    XelibConstantKind.UnicodeScalar => TypeFacts.IsCharacter(resultType),
                    XelibConstantKind.FloatingPoint => TypeIdentity.AreSame(resultType, BuiltinTypes.Float) || TypeIdentity.AreSame(resultType, BuiltinTypes.Double),
                    _ => false,
                });
                result = new(Constant(literal, resultType), resultType);
            }
            var exception = value.ExceptionType == 0 ? null : type(value.ExceptionType);
            Require(exception is null || TypeFacts.IsThrowableType(exception));
            return new(guard, value.Exit == "Normal" ? MirEdgeKind.Normal : MirEdgeKind.Unwind, result, exception);
        }
        var effects = ImmutableHashSet.CreateBuilder<MirLifetimeEffect>();
        foreach (var item in record.Effects)
        {
            Require(item is not null); Parameter(item.Parameter); Path(item.Path);
            TargetPath(function.Parameters[item.Parameter].Type, item.Path);
            Require(item.Phase is "Exit" or "Operation");
            var transition = item.Transition switch
            {
                "ObjectDestruction" => MirLifetimeTransition.ObjectDestruction,
                "StorageDeallocation" => MirLifetimeTransition.StorageDeallocation,
                "PlacementReconstruction" => MirLifetimeTransition.PlacementReconstruction,
                "StorageRead" => MirLifetimeTransition.StorageRead,
                _ => throw XelibReader.Invalid("invalid lifetime transition", null),
            };
            Require(MirPointerLifetimeAnalysis.CarriesPointer(function.Parameters[item.Parameter].Type) ||
                function.Parameters[item.Parameter].Type is ReferenceTypeSymbol);
            Require(transition != MirLifetimeTransition.StorageRead || item.Phase == "Operation");
            Require(transition == MirLifetimeTransition.StorageDeallocation || item.Phase == "Operation" && transition is MirLifetimeTransition.PlacementReconstruction or MirLifetimeTransition.StorageRead ||
                !item.UnknownOffset && !item.Path.Contains('*'));
            var exit = Exit(item.When);
            effects.Add(new(item.Parameter, item.Path, item.UnknownOffset, transition == MirLifetimeTransition.StorageDeallocation,
                exit.Guard, exit.Exit, MirSourceInfo.Generated, exit.Result, exit.ExceptionType,
                item.Phase == "Exit" ? MirLifetimeEffectPhase.Exit : MirLifetimeEffectPhase.Operation, transition));
        }
        var returns = MirReferenceValue.Empty;
        foreach (var item in record.PointerReturns)
        {
            Require(item is not null); Path(item.Field); Path(item.Path);
            Require(item.Parameter >= -1);
            if (item.Parameter >= 0)
            {
                Parameter(item.Parameter);
                TargetPath(function.Parameters[item.Parameter].Type, item.Path);
            }
            else Require(item.Path.Length == 0 && !item.UnknownOffset);
            TargetPath(function.ReturnType, item.Field);
            var origin = item.Parameter < 0 ? new MirReferenceOrigin(MirReferenceOriginKind.Unknown) :
                new MirReferenceOrigin(MirReferenceOriginKind.Parameter, item.Parameter, item.Path) { UnknownObjectOffset = item.UnknownOffset };
            returns = item.Field.Length == 0 ? returns.Union(MirReferenceValue.Of(origin)) : returns.Store(item.Field, MirReferenceValue.Of(origin), false);
        }
        ImmutableHashSet<MirLifetimeUncertainty>? uncertainties = null;
        if (record.Uncertainties is { } coverage)
        {
            Require(!coverage.IsDefault && coverage.Length <= 65536);
            var builder = ImmutableHashSet.CreateBuilder<MirLifetimeUncertainty>();
            foreach (var item in coverage)
            {
                Require(item is not null); Parameter(item.Parameter);
                TargetPath(function.Parameters[item.Parameter].Type, item.Path);
                Require(!string.IsNullOrWhiteSpace(item.Reason) && item.Reason.Length <= 128);
                Require(Enum.TryParse<MirLifetimeTransition>(item.Transition, out var transition) &&
                    Enum.GetName(transition) == item.Transition &&
                    transition is MirLifetimeTransition.ObjectDestruction or MirLifetimeTransition.StorageDeallocation or
                        MirLifetimeTransition.PlacementReconstruction or MirLifetimeTransition.BorrowInvalidation or MirLifetimeTransition.StorageRead);
                var when = Exit(item.When);
                Require(when.Exit == MirEdgeKind.Normal && when.Result is null && when.ExceptionType is null);
                builder.Add(new(item.Parameter, item.Path, item.UnknownOffset, when.Guard, transition, item.Reason));
            }
            uncertainties = builder.ToImmutable();
            Require(uncertainties.IsEmpty || !record.IncompleteReasons.IsEmpty);
        }
        var summary = new MirLifetimeSummary(effects.ToImmutable(), record.Exits.Select(Exit).ToImmutableHashSet(),
            record.IncompleteReasons.ToImmutableSortedSet(StringComparer.Ordinal))
        { Uncertainties = uncertainties, IsImported = true, ReadCoverageComplete = record.ReadCoverageComplete };
        if (!record.ReadCoverageComplete)
        {
            var reads = MirLifetimeOperations.Coverage(function, null)
                .Select(item => item with { Transition = MirLifetimeTransition.StorageRead, Reason = "legacy-read-coverage" }).ToImmutableHashSet();
            if (!reads.IsEmpty)
                summary = summary with
                {
                    // Preserve legacy unknown mutations as well as the added
                    // read uncertainty when old scoped coverage is absent.
                    Uncertainties = MirLifetimeOperations.Coverage(function, summary).ToImmutableHashSet().Union(reads),
                    IncompleteReasons = summary.IncompleteReasons.Add("legacy-read-coverage"),
                };
        }
        function.MirLifetimeSummary = summary;
        if (record.HasPointerReturn) function.MirPointerReturnSummary = returns;
    }

    private static bool IsTypePath(TypeSymbol current, string path)
    {
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            while (current is LifetimeModifierTypeSymbol modifier) current = modifier.ElementType;
            if (current is ReferenceTypeSymbol reference) current = reference.ElementType;
            if (part == "*") return true;
            if (part == "^" || part.StartsWith('['))
            {
                TypeSymbol? element = current switch
                {
                    PointerTypeSymbol pointer => pointer.ElementType,
                    ArrayTypeSymbol array => array.ElementType,
                    OwnershipTypeSymbol owner => owner.ElementType,
                    _ => null,
                };
                if (element is null) return false;
                current = element;
                continue;
            }
            if (current is PointerTypeSymbol pointee) current = pointee.ElementType;
            if (current is OwnershipTypeSymbol owned) current = owned.ElementType;
            if (current is StructTypeSymbol { AllInstanceFields.IsEmpty: true, GenericDefinition: { } definition }) current = definition;
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) ||
                current is not IFieldStorageTypeSymbol structure || structure.AllInstanceFields.FirstOrDefault(field => field.Ordinal == ordinal) is not { } field) return false;
            current = field.Type;
        }
        return true;
    }
}
