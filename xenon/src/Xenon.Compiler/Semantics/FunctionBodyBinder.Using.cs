using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

internal sealed partial class FunctionBodyBinder
{
    private BoundStatement BindLocalStatement(VariableDeclarationStatementSyntax syntax)
    {
        if (syntax.UsingKeyword is { } keyword)
            _diagnostics.Report(keyword.Location,
                "a 'using' declaration must appear directly in a block or switch section",
                DiagnosticIds.InvalidUsingContext);
        return BindVariableDeclaration(syntax);
    }

    private BoundBlockStatement BindStatementList(ImmutableArray<StatementSyntax> syntax)
    {
        var statements = new List<BoundStatement>();
        var cleanups = new List<(int Index, BoundExpression Expression,
            ReferenceFinalizerBindingContext Flow, List<ExceptionalReferenceBinding> Exceptions)>();
        foreach (StatementSyntax child in syntax)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (child is VariableDeclarationStatementSyntax { UsingKeyword: { } keyword } declaration)
            {
                BoundVariableDeclarationStatement local = BindVariableDeclaration(declaration);
                statements.Add(local);
                BoundExpression deletion = BindDeleteOperand(
                    DereferenceReference(new BoundVariableExpression(local.Variable)), keyword.Location);
                _expressionLocations[deletion] = keyword.Location;
                // Track the tail just like a protected region. Cleanup runs after
                // its normal and abrupt exits, not at the declaration's binding state.
                var flow = new ReferenceFinalizerBindingContext(_loopDepth, _switchDepth, []);
                var exceptions = new List<ExceptionalReferenceBinding>();
                _referenceFinalizerBindings.Push(flow);
                _tryExceptionalReferenceBindings.Push(exceptions);
                cleanups.Add((statements.Count, CompleteFullExpression(deletion, resultConsumed: false), flow, exceptions));
            }
            else statements.Add(BindStatement(child));
        }

        // Initialization stays outside its own cleanup scope. The tail retains the
        // existing block cleanup representation, including in source-free libraries.
        for (int index = cleanups.Count - 1; index >= 0; index--)
        {
            (int start, BoundExpression cleanup, ReferenceFinalizerBindingContext flow,
                List<ExceptionalReferenceBinding> exceptions) = cleanups[index];
            var tail = new BoundBlockStatement(statements.Skip(start).ToImmutableArray()) { ExitCleanup = cleanup };
            _referenceFinalizerBindings.Pop();
            _tryExceptionalReferenceBindings.Pop();
            PropagateExceptionalReferenceBindings(exceptions);
            if (CanThrowDuringEvaluation(cleanup is BoundFullExpression full ? full.Expression : cleanup))
            {
                // A destructor can replace an exception as well as interrupt a
                // return/break/continue or ordinary fallthrough. Preserve the
                // reference-field bindings at each of those actual exits.
                IEnumerable<ReferenceBindingState> exits = exceptions.Select(exception => exception.State).Concat(flow.Exits);
                if (!TerminatesCase(tail)) exits = exits.Append(CaptureReferenceBindingState());
                ReferenceBindingState? replacement = exits.Aggregate((ReferenceBindingState?)null,
                    (merged, state) => merged is null ? state : MergeReferenceBindingState(merged, state));
                if (replacement is not null)
                    PropagateExceptionalReferenceBindings([new ExceptionalReferenceBinding(replacement, null)]);
            }
            statements.RemoveRange(start, statements.Count - start);
            statements.Add(tail);
        }
        return new BoundBlockStatement(statements.ToImmutableArray());
    }
}
