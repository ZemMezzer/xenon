using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>Exports MIR reference contracts and maps escape failures to source diagnostics.</summary>
internal static class MirReferenceDiagnostics
{
    public static void Analyze(BoundFunction function, TypeFactory types, DiagnosticBag diagnostics,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation)
    {
        bool referenceCompletion = function.Symbol.IsAsync && BoundTree.DescendantsAndSelf(function.Body).Any(node => node switch
        {
            BoundCallExpression call => call.Function.OperatorKind == OperatorKind.Resolve && call.Arguments.Length == 2 && call.Arguments[1].Type is ReferenceTypeSymbol,
            BoundDeferredGenericOperationExpression { Requirement: FunctionSymbol callable } call => callable.OperatorKind == OperatorKind.Resolve && call.Arguments.Length == 2 && call.Arguments[1].Type is ReferenceTypeSymbol,
            _ => false,
        });
        if ((!referenceCompletion && function.Symbol.ReturnType is not ReferenceTypeSymbol) || function.Symbol.Parameters.Any(parameter =>
            TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void))) return;
        MirFunction mir = MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true);
        var analysis = new MirReferenceOrigins(mir);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(mir, cancellation), analysis, cancellation);
        var origins = new HashSet<MirReferenceOrigin>();
        var reported = new HashSet<TextLocation>();
        foreach (MirBasicBlock block in mir.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
        {
            MirOperand? value = block.Terminator switch
            {
                MirReturn returned when function.Symbol.ReturnType is ReferenceTypeSymbol => returned.Value,
                MirCall { Callee: MirFunctionOperand { Function.OperatorKind: OperatorKind.Resolve } } call when referenceCompletion && call.Arguments.Length == 2 && call.Arguments[1].Type is ReferenceTypeSymbol => call.Arguments[1],
                MirCall { Callee: MirRequirementOperand { Requirement: FunctionSymbol { OperatorKind: OperatorKind.Resolve } } } call when referenceCompletion && call.Arguments.Length == 2 && call.Arguments[1].Type is ReferenceTypeSymbol => call.Arguments[1],
                _ => null,
            };
            if (value is null) continue;
            var roots = analysis.Operand(value, flow.Output[block.Id]).Roots;
            origins.UnionWith(roots);
            MirReferenceOrigin? invalid = roots.FirstOrDefault(origin => !origin.IsSafeReference);
            if (invalid is null || !reported.Add(block.Terminator.Source.Location)) continue;
            MirLocal? local = invalid.Kind == MirReferenceOriginKind.Local ? mir.Locals.First(item => item.Id.Value == invalid.Ordinal) : null;
            string subject = local?.Variable switch
            {
                ParameterSymbol parameter => $"by-value parameter '{parameter.Name}'",
                LocalVariableSymbol variable => $"local variable '{variable.Name}'",
                _ => invalid.Kind != MirReferenceOriginKind.Local ? "a reference with unknown lifetime" : "a temporary value",
            };
            string reason = invalid.Kind != MirReferenceOriginKind.Local
                ? "the referenced storage may belong to the current function's stack frame and may not outlive the function call"
                : "the referenced storage belongs to the current function's stack frame and does not outlive the function call";
            diagnostics.Report(block.Terminator.Source.Location, $"cannot return a reference to {subject} because {reason}", DiagnosticIds.EscapingLocalReference);
        }
        if (function.Symbol.ReturnType is ReferenceTypeSymbol) function.Symbol.SetReferenceReturnOrigins(origins.Select(origin => origin.Contract)
            .DistinctBy(Key).OrderBy(Key, StringComparer.Ordinal).ToImmutableArray());
    }
    public static void AnalyzeAggregates(BoundFunction function, TypeFactory types, DiagnosticBag diagnostics,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation)
    {
        bool constructor = function.Symbol.FunctionKind is FunctionKind.Constructor or FunctionKind.InstanceInitializer;
        TypeSymbol resultType = constructor ? function.Symbol.ContainingType! : function.Symbol.ReturnType;
        var leaves = MirReferenceOrigins.ReferenceLeaves(resultType).ToArray();
        bool completion = function.Symbol.IsAsync && BoundTree.DescendantsAndSelf(function.Body).Any(node => node switch
        {
            BoundCallExpression call => call.Function.OperatorKind == OperatorKind.Resolve && call.Arguments.Length == 2 && call.Arguments[1].Type is not ReferenceTypeSymbol && MirReferenceOrigins.ReferenceLeaves(call.Arguments[1].Type).Any(),
            BoundDeferredGenericOperationExpression { Requirement: FunctionSymbol callable } call => callable.OperatorKind == OperatorKind.Resolve && call.Arguments.Length == 2 && call.Arguments[1].Type is not ReferenceTypeSymbol && MirReferenceOrigins.ReferenceLeaves(call.Arguments[1].Type).Any(),
            _ => false,
        });
        if (resultType is ReferenceTypeSymbol || (!completion && leaves.Length == 0) || function.Symbol.Parameters.Any(parameter =>
            TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void))) return;
        MirFunction mir = MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true);
        var analysis = new MirReferenceOrigins(mir);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(mir, cancellation), analysis, cancellation);
        if (!function.Symbol.IsAsync) function.Symbol.SetReferenceFieldOrigins(analysis.ReferenceFields(cancellation, flow));
        var reported = new HashSet<(TextLocation, string)>();
        foreach (MirBasicBlock block in mir.Blocks.Where(block => flow.Graph.Reachable.Contains(block.Id)))
        {
            if (!constructor && !function.Symbol.IsAsync && block.Terminator is MirReturn { Value: { } operand })
                Check(analysis.Operand(operand, flow.Output[block.Id]), resultType, block.Terminator.Source, false);
            if (completion && block.Terminator is MirCall { Arguments.Length: 2 } call && call.Arguments[1].Type is not ReferenceTypeSymbol &&
                (call.Callee is MirFunctionOperand { Function.OperatorKind: OperatorKind.Resolve } ||
                 call.Callee is MirRequirementOperand { Requirement: FunctionSymbol { OperatorKind: OperatorKind.Resolve } }))
                Check(analysis.Operand(call.Arguments[1], flow.Output[block.Id]), call.Arguments[1].Type, call.Source, false);
            if (!constructor) continue;
            for (int index = 0; index < block.Statements.Length; index++)
            {
                if (block.Statements[index] is not MirAssign { IsSemanticWrite: true } assign) continue;
                var state = flow.Before[new(block.Id, index)];
                if (analysis.Address(assign.Destination, state).Any(origin => origin.Kind == MirReferenceOriginKind.Receiver))
                    Check(analysis.Value(assign.Value, state), assign.Value.Type, assign.Source, true);
            }
        }
        void Check(MirReferenceValue value, TypeSymbol type, MirSourceInfo source, bool store)
        {
            var invalid = MirReferenceOrigins.ReferenceLeaves(type).SelectMany(leaf => value.Project(string.Join('/', leaf.Path)).Roots)
                .FirstOrDefault(origin => !origin.IsSafeReference);
            if (invalid is null) return;
            string message = function.Symbol.IsAsync
                ? "resumable completion cannot retain a value borrowing storage owned by the completed frame"
                : "cannot return a value containing a reference whose storage does not outlive the function call";
            if (store)
            {
                MirLocal? local = invalid.Kind == MirReferenceOriginKind.Local ? mir.Locals.First(item => item.Id.Value == invalid.Ordinal) : null;
                string subject = local?.Variable is { } variable ? $"local '{variable.Name}'" :
                    invalid.Kind != MirReferenceOriginKind.Local ? "storage with unknown lifetime" : "a temporary value";
                message = $"constructor cannot store a reference to {subject} because it does not outlive the constructed value";
            }
            if (reported.Add((source.Location, message))) diagnostics.Report(source.Location, message, DiagnosticIds.AggregateReferenceEscape);
        }
    }
    public static void AnalyzeShared(BoundFunction function, TypeFactory types,
        IReadOnlyDictionary<BoundExpression, TextLocation> locations, CancellationToken cancellation)
    {
        if (function.Symbol.ReturnType is not SharedTypeSymbol || function.Symbol.Parameters.Any(parameter =>
            TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Error) || TypeIdentity.AreSame(parameter.Type, BuiltinTypes.Void))) return;
        var mir = MirLowerer.Lower(function, types, locations, cancellation, diagnosticRecovery: true);
        function.Symbol.SetSharedReturnOrigins(new MirReferenceOrigins(mir).SharedReturns(cancellation));
    }
    private static string Key(ReferenceReturnOrigin origin) => $"{origin.Kind}:{origin.ParameterOrdinal}:{string.Join('/', origin.FieldOrdinals)}";
}