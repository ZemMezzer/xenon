using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed partial class FunctionBodyBinder
{
    private sealed record PreparedLambdaCapture(
        LambdaCaptureSyntax Syntax,
        VariableSymbol Source,
        TypeSymbol StorageType,
        BoundExpression Initializer);
    private sealed record LambdaBodyProbe(
        bool IsApplicable,
        int ReturnConversionCost);

    private FunctionBodyBinder? _lambdaParent;
    private bool _isLambdaCompatibilityProbe;
    private bool _lambdaProbeReturnsCompatible = true;
    private int _lambdaProbeReturnConversionCost;
    private readonly Dictionary<LambdaExpressionSyntax, ImmutableArray<PreparedLambdaCapture>>
        _preparedLambdaCaptures = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LambdaExpressionSyntax, ExpressionSyntax> _deferredLambdaArtifactRoots =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LambdaExpressionSyntax, Dictionary<TypeSymbol, LambdaBodyProbe>> _lambdaBodyProbes =
        new(ReferenceEqualityComparer.Instance);
    private FunctionSymbol LexicalAccessContext => _lambdaParent?.LexicalAccessContext ?? _function;

    private static bool TryGetLambdaExpression(ExpressionSyntax syntax, out LambdaExpressionSyntax lambda)
    {
        while (syntax is ParenthesizedExpressionSyntax parenthesized)
            syntax = parenthesized.Expression;
        if (syntax is LambdaExpressionSyntax result)
        {
            lambda = result;
            return true;
        }
        lambda = null!;
        return false;
    }

    private bool TryGetNamedFunctionExpression(ExpressionSyntax syntax, out ExpressionSyntax function)
    {
        while (syntax is ParenthesizedExpressionSyntax parenthesized)
            syntax = parenthesized.Expression;
        if (syntax is NameExpressionSyntax or MemberAccessExpressionSyntax &&
            GetNamedFunctionCandidates(syntax).Length != 0)
        {
            function = syntax;
            return true;
        }
        function = null!;
        return false;
    }

    private BoundExpression BindDeferredLambdaExpression(
        LambdaExpressionSyntax syntax,
        ExpressionSyntax artifactRoot)
    {
        // Captures are evaluated when the lambda argument is evaluated, even though
        // its signature cannot be contextualized until overload resolution finishes.
        // Preparing them here preserves source-order ownership and borrow effects.
        ExpressionFlow? before = _argumentFlowTransactions.Count == 0
            ? null : CaptureExpressionFlow();
        ArgumentFlowTransaction? transaction = _argumentFlowTransactions.TryPeek(out var activeTransaction)
            ? activeTransaction : null;
        var arrayCleanupBefore = new Dictionary<LocalVariableSymbol, bool>();
        foreach (LambdaCaptureSyntax capture in syntax.Captures)
            if (_scope.Lookup(capture.IdentifierToken.Text) is LocalVariableSymbol
                { Type: ArrayTypeSymbol } variable)
                arrayCleanupBefore.TryAdd(variable, variable.RequiresArrayCleanupTransfer);
        Dictionary<MovePlace, TextLocation>? loopSites = _loopMoveContexts.TryPeek(out var loopContext)
            ? loopContext.Sites
            : null;
        HashSet<MovePlace>? loopSitesBefore = loopSites?.Keys.ToHashSet();
        PrepareLambdaCaptures(syntax);
        if (before is not null)
        {
            _deferredLambdaArtifactRoots[syntax] = artifactRoot;
            transaction ??= _argumentFlowTransactions.Peek();
            var borrows = ImmutableArray.CreateBuilder<ArgumentFlowTransaction.DeferredCaptureBorrow>();
            foreach (PreparedLambdaCapture capture in _preparedLambdaCaptures[syntax])
                if (capture.Syntax.CaptureKind is LambdaCaptureKind.MutableBorrow or LambdaCaptureKind.ReadonlyBorrow &&
                    TryGetBorrowPlace(capture.Initializer, out BorrowPlace place, out _))
                    borrows.Add(new ArgumentFlowTransaction.DeferredCaptureBorrow(
                        place, capture.Syntax.CaptureKind == LambdaCaptureKind.ReadonlyBorrow));
            KeyValuePair<MovePlace, TextLocation>[] addedLoopSites = loopSites is null
                ? []
                : loopSites.Where(entry => !loopSitesBefore!.Contains(entry.Key)).ToArray();
            ImmutableArray<PreparedLambdaCapture> prepared = _preparedLambdaCaptures[syntax];
            void RollbackAuxiliaryState(
                IReadOnlyDictionary<MovePlace, TextLocation?> supersedingMutations)
            {
                foreach (var entry in arrayCleanupBefore)
                {
                    KeyValuePair<MovePlace, TextLocation?>? latest = supersedingMutations
                        .Where(candidate => ReferenceEquals(candidate.Key.RootVariable, entry.Key))
                        .Select(candidate => (KeyValuePair<MovePlace, TextLocation?>?)candidate)
                        .LastOrDefault();
                    entry.Key.RequiresArrayCleanupTransfer = latest?.Value is not null || entry.Value;
                }
                if (loopSites is not null)
                    foreach (var entry in addedLoopSites)
                        if (loopSites.TryGetValue(entry.Key, out TextLocation current) && current.Equals(entry.Value))
                        {
                            KeyValuePair<MovePlace, TextLocation?>? latest = supersedingMutations
                                .Where(candidate => PlacesOverlap(candidate.Key, entry.Key))
                                .Select(candidate => (KeyValuePair<MovePlace, TextLocation?>?)candidate)
                                .LastOrDefault();
                            if (latest?.Value is { } moveLocation)
                                loopSites[entry.Key] = moveLocation;
                            else
                                loopSites.Remove(entry.Key);
                        }
                foreach (PreparedLambdaCapture capture in prepared)
                    _argumentFlowCandidates.Remove(capture.Initializer);
                _preparedLambdaCaptures.Remove(syntax);
                _deferredLambdaArtifactRoots.Remove(syntax);
                _deferredLambdaCaptureTransactions.Remove(syntax);
            }
            transaction.RecordDeferredLambdaCapture(
                syntax, before, CaptureExpressionFlow(), borrows.ToImmutable(), RollbackAuxiliaryState);
            _deferredLambdaCaptureTransactions[syntax] = transaction;
        }
        return new BoundUnboundLambdaExpression(syntax);
    }

    private BoundExpression BindLambdaExpression(LambdaExpressionSyntax syntax)
    {
        FunctionPointerTypeSymbol? pointerTarget = _expectedFunctionPointerType;
        FunctionValueTypeSymbol? valueTarget = _expectedFunctionValueType;
        TypeSymbol returnType;
        if (syntax.ExplicitReturnType is { } explicitReturn)
            returnType = TypeResolver.ResolveReturnType(explicitReturn, _fileScope, _diagnostics);
        else if (valueTarget is not null)
            returnType = valueTarget.ReturnType;
        else if (pointerTarget is not null)
            returnType = pointerTarget.ReturnType;
        else
        {
            _diagnostics.Report(syntax.IntroducerToken.Location,
                "lambda requires a contextual 'function R(...)' or 'function R(...)*' type",
                DiagnosticIds.UnsupportedLambdaContext);
            return new BoundErrorExpression();
        }

        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ParameterSyntax parameter in syntax.Parameters)
        {
            TypeSymbol type = TypeResolver.Resolve(parameter.Type, _fileScope, _diagnostics);
            if (TypeIdentity.AreSame(type, BuiltinTypes.Void))
                _diagnostics.Report(parameter.Type.NameToken.Location, "parameter type cannot be 'void'",
                    DiagnosticIds.VoidParameterType);
            if (!names.Add(parameter.IdentifierToken.Text))
                _diagnostics.Report(parameter.IdentifierToken.Location,
                    $"parameter '{parameter.IdentifierToken.Text}' is already declared",
                    DiagnosticIds.DuplicateDeclaration);
            parameters.Add(new ParameterSymbol(parameter.IdentifierToken.Text, type, parameters.Count,
                parameter.Type.IsBindingReadonly(), declaration: parameter));
        }

        ImmutableArray<TypeSymbol> parameterTypes = parameters.Select(parameter => parameter.Type).ToImmutableArray();
        TypeSymbol? contextual = valueTarget ?? (TypeSymbol?)pointerTarget;
        if (contextual is not null)
        {
            TypeSymbol contextualReturn = contextual is FunctionValueTypeSymbol fv ? fv.ReturnType : pointerTarget!.ReturnType;
            ImmutableArray<TypeSymbol> contextualParameters = contextual is FunctionValueTypeSymbol fvp
                ? fvp.ParameterTypes : pointerTarget!.ParameterTypes;
            if (!TypeIdentity.AreSame(returnType, contextualReturn) ||
                contextualParameters.Length != parameterTypes.Length ||
                !contextualParameters.Zip(parameterTypes).All(pair => TypeIdentity.AreSame(pair.First, pair.Second)))
            {
                _diagnostics.Report(syntax.IntroducerToken.Location,
                    $"lambda signature does not match contextual type '{contextual.ToDisplayString()}'",
                    DiagnosticIds.TypeMismatch);
                RollbackRejectedDeferredLambda(syntax);
                return new BoundErrorExpression();
            }
        }

        FunctionValueTypeSymbol? functionValue = valueTarget;
        if (functionValue is null && pointerTarget is null && syntax.ExplicitReturnType is not null)
            pointerTarget = _fileScope.TypeFactory.FunctionPointer(returnType, parameterTypes);
        if (!syntax.Captures.IsEmpty && pointerTarget is not null)
        {
            _diagnostics.Report(syntax.OpenBracketToken!.Location,
                "capturing lambda cannot convert to raw function pointer",
                DiagnosticIds.LambdaCaptureNotSupported);
            RollbackRejectedDeferredLambda(syntax);
            return new BoundErrorExpression();
        }
        _deferredLambdaCaptureTransactions.TryGetValue(syntax,
            out ArgumentFlowTransaction? captureTransaction);
        captureTransaction?.EnsureTypeFactorySnapshot(_fileScope.TypeFactory);
        SemanticInfoStore.Snapshot? semanticSnapshot = captureTransaction is null
            ? null : _semanticInfo.CaptureSnapshot();
        (GenericStructSpecializer? transactionalStructSpecializer,
            GenericFunctionSpecializer? transactionalFunctionSpecializer) = captureTransaction is null
            ? (null, null)
            : captureTransaction.GetTransactionalSpecializers(
                _fileScope.GenericStructSpecializer, _genericSpecializer, _diagnostics);
        FileSymbolScope lambdaScope = transactionalStructSpecializer is null
            ? _fileScope
            : _fileScope.WithGenericStructSpecializer(transactionalStructSpecializer);

        string name = CreateLambdaName(syntax);
        TypeSyntax returnSyntax = syntax.ExplicitReturnType ?? new NamedTypeSyntax(
            [new SyntaxToken(SyntaxKind.VoidKeyword, syntax.IntroducerToken.Location, "void")], []);
        var declaration = new FunctionDeclarationSyntax(null, null, returnSyntax,
            new SyntaxToken(SyntaxKind.IdentifierToken, syntax.IntroducerToken.Location, name), null,
            syntax.OpenParenthesisToken, syntax.Parameters, syntax.CommaTokens,
            syntax.CloseParenthesisToken, [], syntax.Body, null);
        var function = new FunctionSymbol(name, _function.ContainingNamespace, returnType,
            parameters.ToImmutable(), declaration) { IsLambda = true, IsCapturingLambda = !syntax.Captures.IsEmpty };

        ImmutableArray<PreparedLambdaCapture> preparedCaptures = PrepareLambdaCaptures(syntax);
        var boundCaptures = ImmutableArray.CreateBuilder<BoundFunctionValueCapture>(preparedCaptures.Length);
        var captureSymbols = ImmutableArray.CreateBuilder<CaptureVariableSymbol>();
        foreach (PreparedLambdaCapture prepared in preparedCaptures)
        {
            LambdaCaptureSyntax capture = prepared.Syntax;
            _semanticInfo.Symbols[capture] = SymbolInfo.FromSymbol(prepared.Source);
            var captured = new CaptureVariableSymbol(prepared.Source, prepared.StorageType,
                capture.CaptureKind, captureSymbols.Count, function, capture);
            captureSymbols.Add(captured);
            boundCaptures.Add(new BoundFunctionValueCapture(captured, prepared.Initializer));
        }
        function.LambdaCaptures = captureSymbols.ToImmutable();

        foreach ((ParameterSyntax parameter, ParameterSymbol symbol) in syntax.Parameters.Zip(function.Parameters))
            _semanticInfo.Declarations[parameter] = symbol;

        var binder = new FunctionBodyBinder(function, lambdaScope, _diagnostics, _constants,
            _semanticInfo, transactionalFunctionSpecializer ?? _genericSpecializer, _cancellationToken)
        {
            _lambdaParent = this,
            _isLambdaCompatibilityProbe = this._isLambdaCompatibilityProbe,
        };
        foreach (CaptureVariableSymbol capture in function.LambdaCaptures)
        {
            binder._scope.TryDeclare(capture);
            binder._definitelyAssigned.Add(capture);
        }
        BoundBlockStatement body = binder.BindBody(syntax.Body);
        _semanticInfo.LambdaFunctions.Add(new BoundFunction(function, body));
        foreach (var entry in binder.ExpressionLocations) _expressionLocations.TryAdd(entry.Key, entry.Value);
        if (captureTransaction is not null && semanticSnapshot is not null)
        {
            SemanticInfoStore.Delta semanticDelta = _semanticInfo.CaptureDelta(semanticSnapshot);
            ExpressionSyntax artifactRoot = _deferredLambdaArtifactRoots.GetValueOrDefault(syntax, syntax);
            captureTransaction.RecordMaterializedLambda(
                syntax,
                commitArtifacts: static () => { },
                rollbackArtifacts: () =>
                {
                    _semanticInfo.Rollback(semanticDelta);
                    _semanticInfo.RollbackSyntax(artifactRoot);
                });
        }

        if (pointerTarget is not null)
            return new BoundFunctionAddressExpression(function, pointerTarget);
        functionValue ??= _fileScope.TypeFactory.FunctionValue(returnType, parameterTypes);
        if (!_isLambdaCompatibilityProbe)
            _fileScope.TypeFactory.EnsureFunctionValueDestructor(functionValue, _fileScope.GlobalNamespace, syntax);
        return new BoundFunctionValueExpression(function, functionValue, boundCaptures.ToImmutable());
    }

    private void RollbackRejectedDeferredLambda(LambdaExpressionSyntax syntax)
    {
        if (!_deferredLambdaCaptureTransactions.TryGetValue(syntax,
                out ArgumentFlowTransaction? transaction))
            return;
        ExpressionFlow flow = transaction.RollbackUnmaterializedLambdaCaptures(
            CaptureExpressionFlow(), _diagnostics, OnTransactionalDiagnosticRemoved);
        RestoreExpressionFlow(flow);
    }

    private ImmutableArray<PreparedLambdaCapture> PrepareLambdaCaptures(LambdaExpressionSyntax syntax)
    {
        if (_preparedLambdaCaptures.TryGetValue(syntax, out ImmutableArray<PreparedLambdaCapture> prepared))
            return prepared;

        var captures = ImmutableArray.CreateBuilder<PreparedLambdaCapture>();
        var captureNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (LambdaCaptureSyntax capture in syntax.Captures)
        {
            if (capture.ReadonlyKeyword is not null && capture.AmpersandToken is null ||
                capture.MoveKeyword is not null &&
                (capture.ReadonlyKeyword is not null || capture.AmpersandToken is not null))
            {
                _diagnostics.Report(capture.IdentifierToken.Location,
                    "capture must use 'value', '&value', 'readonly &value', or 'move value'",
                    DiagnosticIds.InvalidLambdaCapture);
                continue;
            }

            string captureName = capture.IdentifierToken.Text;
            VariableSymbol? source = _scope.Lookup(captureName);
            if (source is null)
            {
                _diagnostics.Report(capture.IdentifierToken.Location,
                    $"cannot capture unknown local '{captureName}'", DiagnosticIds.UnknownIdentifier);
                continue;
            }
            if (!captureNames.Add(captureName))
            {
                _diagnostics.Report(capture.IdentifierToken.Location,
                    $"'{captureName}' is already present in the capture list", DiagnosticIds.DuplicateDeclaration);
                continue;
            }

            TypeSymbol storageType = capture.CaptureKind switch
            {
                LambdaCaptureKind.MutableBorrow => _fileScope.TypeFactory.ReferenceTo(source.Type),
                LambdaCaptureKind.ReadonlyBorrow => _fileScope.TypeFactory.ReferenceTo(source.Type, isReadonly: true),
                _ => source.Type,
            };
            BoundExpression sourceExpression = BindNameExpression(
                new NameExpressionSyntax(capture.IdentifierToken));
            BoundExpression initializer;
            if (capture.CaptureKind == LambdaCaptureKind.Move)
            {
                initializer = BindMoveExpression(new MoveExpressionSyntax(capture.MoveKeyword!,
                    new NameExpressionSyntax(capture.IdentifierToken)));
            }
            else
            {
                initializer = ContextualizeConversion(sourceExpression, storageType,
                    capture.IdentifierToken.Location);
                if (capture.CaptureKind is LambdaCaptureKind.MutableBorrow or LambdaCaptureKind.ReadonlyBorrow &&
                    TryGetBorrowPlace(initializer, out BorrowPlace place, out LocalVariableSymbol? alias))
                    ValidateBorrowCreation(place, capture.CaptureKind == LambdaCaptureKind.ReadonlyBorrow,
                        alias, capture.IdentifierToken.Location);
            }
            captures.Add(new PreparedLambdaCapture(capture, source, storageType, initializer));
        }

        prepared = captures.ToImmutable();
        _preparedLambdaCaptures.Add(syntax, prepared);
        return prepared;
    }

    private string CreateLambdaName(LambdaExpressionSyntax syntax)
    {
        var source = syntax.IntroducerToken.Location.Source;
        string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            source.Path + "\0" + source.Text + "\0" + NativeSymbolNames.Get(_function))));
        return $"<lambda_{identity}_{syntax.IntroducerToken.Location.Span.Start}>";
    }

    private int? GetLambdaArgumentConversionCost(TypeSymbol destination, LambdaExpressionSyntax syntax)
    {
        FunctionPointerTypeSymbol? pointerTarget = destination as FunctionPointerTypeSymbol;
        FunctionValueTypeSymbol? valueTarget = destination as FunctionValueTypeSymbol;
        if (pointerTarget is null && valueTarget is null)
        {
            return FindLambdaUserConversions(syntax, destination).Length == 0
                ? null
                : int.MaxValue - 1;
        }

        TypeSymbol? returnType = valueTarget?.ReturnType ?? pointerTarget?.ReturnType;
        ImmutableArray<TypeSymbol> parameterTypes = valueTarget?.ParameterTypes ??
            pointerTarget?.ParameterTypes ?? [];
        if (returnType is null || parameterTypes.Length != syntax.Parameters.Length)
            return null;
        if (pointerTarget is not null && !syntax.Captures.IsEmpty)
            return null;

        var diagnostics = new DiagnosticBag();
        TypeFactory.Snapshot typeFactorySnapshot = _fileScope.TypeFactory.CaptureSnapshot();
        try
        {
        for (int index = 0; index < syntax.Parameters.Length; index++)
        {
            TypeSymbol parameterType = TypeResolver.Resolve(syntax.Parameters[index].Type,
                _fileScope, diagnostics);
            if (!TypeIdentity.AreSame(parameterTypes[index], parameterType))
                return null;
        }
        if (syntax.ExplicitReturnType is { } explicitReturn)
        {
            TypeSymbol explicitReturnType = TypeResolver.ResolveReturnType(explicitReturn,
                _fileScope, diagnostics);
            if (!TypeIdentity.AreSame(returnType, explicitReturnType))
                return null;
        }
        if (diagnostics.Count != 0)
            return null;

        int? returnCost = GetLambdaBodyReturnConversionCost(syntax, returnType);
        if (returnCost is null)
            return null;

        // A first-class function value is the lambda's native representation.
        // Conversion to a raw pointer is viable only for non-capturing lambdas
        // and ranks one standard step after the native contextual conversion.
        int representationPenalty = valueTarget is not null ? 0 : 1;
        int rankedReturnCost = Math.Min(returnCost.Value, int.MaxValue - 3);
        return rankedReturnCost + representationPenalty;
        }
        finally
        {
            _fileScope.TypeFactory.Rollback(typeFactorySnapshot);
        }
    }

    private int? GetLambdaBodyReturnConversionCost(LambdaExpressionSyntax syntax, TypeSymbol returnType)
    {
        LambdaBodyProbe probe = GetLambdaBodyProbe(syntax, returnType);
        return probe.IsApplicable ? probe.ReturnConversionCost : null;
    }

    private LambdaBodyProbe GetLambdaBodyProbe(LambdaExpressionSyntax syntax, TypeSymbol returnType)
    {
        if (!_lambdaBodyProbes.TryGetValue(syntax, out Dictionary<TypeSymbol, LambdaBodyProbe>? probes))
        {
            probes = new Dictionary<TypeSymbol, LambdaBodyProbe>(ReferenceEqualityComparer.Instance);
            _lambdaBodyProbes.Add(syntax, probes);
        }
        if (probes.TryGetValue(returnType, out LambdaBodyProbe? cached))
            return cached;

        var diagnostics = new DiagnosticBag();
        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>(syntax.Parameters.Length);
        foreach (ParameterSyntax parameter in syntax.Parameters)
        {
            TypeSymbol type = TypeResolver.Resolve(parameter.Type, _fileScope, diagnostics);
            parameters.Add(new ParameterSymbol(parameter.IdentifierToken.Text, type, parameters.Count,
                parameter.Type.IsBindingReadonly(), declaration: parameter));
        }

        string name = CreateLambdaName(syntax) + "#compatibility";
        TypeSyntax returnSyntax = syntax.ExplicitReturnType ?? new NamedTypeSyntax(
            [new SyntaxToken(SyntaxKind.VoidKeyword, syntax.IntroducerToken.Location, "void")], []);
        var declaration = new FunctionDeclarationSyntax(null, null, returnSyntax,
            new SyntaxToken(SyntaxKind.IdentifierToken, syntax.IntroducerToken.Location, name), null,
            syntax.OpenParenthesisToken, syntax.Parameters, syntax.CommaTokens,
            syntax.CloseParenthesisToken, [], syntax.Body, null);
        var function = new FunctionSymbol(name, _function.ContainingNamespace, returnType,
            parameters.ToImmutable(), declaration) { IsLambda = true, IsCapturingLambda = !syntax.Captures.IsEmpty };

        var captureSymbols = ImmutableArray.CreateBuilder<CaptureVariableSymbol>();
        var captureNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (LambdaCaptureSyntax capture in syntax.Captures)
        {
            if (!captureNames.Add(capture.IdentifierToken.Text))
                continue;
            VariableSymbol? source = _scope.Lookup(capture.IdentifierToken.Text);
            if (source is null)
                continue;
            TypeSymbol storageType = capture.CaptureKind switch
            {
                LambdaCaptureKind.MutableBorrow => _fileScope.TypeFactory.ReferenceTo(source.Type),
                LambdaCaptureKind.ReadonlyBorrow => _fileScope.TypeFactory.ReferenceTo(source.Type, isReadonly: true),
                _ => source.Type,
            };
            captureSymbols.Add(new CaptureVariableSymbol(source, storageType, capture.CaptureKind,
                captureSymbols.Count, function, capture));
        }
        function.LambdaCaptures = captureSymbols.ToImmutable();

        GenericStructSpecializer? speculativeStructSpecializer =
            _fileScope.GenericStructSpecializer?.CreateSpeculative(diagnostics);
        GenericFunctionSpecializer? speculativeSpecializer =
            _genericSpecializer?.CreateSpeculative(diagnostics, speculativeStructSpecializer);
        FileSymbolScope speculativeScope = speculativeStructSpecializer is null
            ? _fileScope
            : _fileScope.WithGenericStructSpecializer(speculativeStructSpecializer);
        var binder = new FunctionBodyBinder(function, speculativeScope, diagnostics, _constants,
            new SemanticInfoStore(), speculativeSpecializer, _cancellationToken)
        {
            _lambdaParent = this,
            _isLambdaCompatibilityProbe = true,
        };
        foreach (CaptureVariableSymbol capture in function.LambdaCaptures)
        {
            binder._scope.TryDeclare(capture);
            binder._definitelyAssigned.Add(capture);
        }
        BoundBlockStatement body = binder.BindBody(syntax.Body);
        bool applicable = binder._lambdaProbeReturnsCompatible &&
            (TypeIdentity.AreSame(returnType, BuiltinTypes.Void) || AlwaysReturns(body));
        cached = new LambdaBodyProbe(applicable, binder._lambdaProbeReturnConversionCost);
        probes.Add(returnType, cached);
        return cached;
    }

    private bool TryInferLambdaGenericTypes(TypeSymbol pattern, LambdaExpressionSyntax syntax,
        IDictionary<GenericParameterSymbol, TypeSymbol> inferred)
    {
        TypeSymbol? returnPattern;
        ImmutableArray<TypeSymbol> parameterPatterns;
        switch (pattern)
        {
            case FunctionValueTypeSymbol value:
                returnPattern = value.ReturnType;
                parameterPatterns = value.ParameterTypes;
                break;
            case FunctionPointerTypeSymbol pointer when syntax.Captures.IsEmpty:
                returnPattern = pointer.ReturnType;
                parameterPatterns = pointer.ParameterTypes;
                break;
            default:
                // Another argument can still infer the containing generic type;
                // candidate applicability will then inspect the substituted type.
                return true;
        }
        if (parameterPatterns.Length != syntax.Parameters.Length)
            return false;

        var diagnostics = new DiagnosticBag();
        for (int index = 0; index < syntax.Parameters.Length; index++)
        {
            TypeSymbol actual = TypeResolver.Resolve(syntax.Parameters[index].Type,
                _fileScope, diagnostics);
            if (!TryInferGenericType(parameterPatterns[index], actual, inferred))
                return false;
        }
        if (syntax.ExplicitReturnType is { } explicitReturn)
        {
            TypeSymbol actualReturn = TypeResolver.ResolveReturnType(explicitReturn,
                _fileScope, diagnostics);
            if (!TryInferGenericType(returnPattern, actualReturn, inferred))
                return false;
        }
        return diagnostics.Count == 0;
    }

    private FunctionSymbol[] GetNamedFunctionCandidates(ExpressionSyntax syntax)
    {
        while (syntax is ParenthesizedExpressionSyntax parenthesized)
            syntax = parenthesized.Expression;
        IEnumerable<FunctionSymbol> candidates;
        if (syntax is NameExpressionSyntax name)
        {
            if (HasValueSymbol(name.IdentifierToken.Text, name.IdentifierToken.Location))
                return [];
            FunctionSymbol[] containingCandidates = _function.ContainingType is { } containing
                ? containing.LookupMethods(name.IdentifierToken.Text).ToArray()
                : [];
            candidates = containingCandidates.Length != 0
                ? containingCandidates
                : _fileScope.ResolveFunctions(name.IdentifierToken.Text);
        }
        else if (syntax is MemberAccessExpressionSyntax member &&
                 TryGetDottedName(member, out ImmutableArray<SyntaxToken> parts) && parts.Length >= 2)
        {
            string firstName = parts[0].Text;
            if (HasValueSymbol(firstName, parts[0].Location))
                return [];
            string[] receiverParts = parts.Take(parts.Length - 1).Select(part => part.Text).ToArray();
            TypeSymbol? receiverType = receiverParts.Length == 1
                ? ResolveUnqualifiedTypeForExpression(receiverParts[0], parts[0].Location, new DiagnosticBag())
                : _fileScope.ResolveQualifiedType(receiverParts);
            candidates = receiverType is DeclaredTypeSymbol declaredType
                ? declaredType.LookupMethods(parts[^1].Text)
                : _fileScope.ResolveQualifiedFunctions(parts.Select(part => part.Text).ToArray());
        }
        else
        {
            return [];
        }
        return candidates.Where(candidate => !candidate.HasImplicitThis && !candidate.IsGenericDefinition)
            .Distinct().ToArray();
    }

    private static SyntaxToken GetNamedFunctionToken(ExpressionSyntax syntax) => syntax switch
    {
        NameExpressionSyntax name => name.IdentifierToken,
        MemberAccessExpressionSyntax member => member.MemberToken,
        ParenthesizedExpressionSyntax parenthesized => GetNamedFunctionToken(parenthesized.Expression),
        _ => throw new InvalidOperationException("Expected a named function expression."),
    };

    private FunctionSymbol[] GetNamedFunctionMatches(
        ExpressionSyntax syntax,
        TypeSymbol returnType,
        ImmutableArray<TypeSymbol> parameterTypes) =>
        GetNamedFunctionCandidates(syntax).Where(candidate =>
            TypeIdentity.AreSame(candidate.ReturnType, returnType) &&
            candidate.Parameters.Length == parameterTypes.Length &&
            candidate.Parameters.Zip(parameterTypes).All(pair =>
                TypeIdentity.AreSame(pair.First.Type, pair.Second))).ToArray();

    private int? GetNamedFunctionArgumentConversionCost(TypeSymbol destination, ExpressionSyntax syntax) =>
        destination switch
        {
            FunctionValueTypeSymbol value when
                GetNamedFunctionMatches(syntax, value.ReturnType, value.ParameterTypes).Length == 1 => 0,
            FunctionValueTypeSymbol or FunctionPointerTypeSymbol => null,
            _ => FindNamedFunctionUserConversions(syntax, destination).Length == 0
                ? null
                : int.MaxValue - 1,
        };

    private int? GetNamedFunctionUserConversionInputCost(TypeSymbol destination, ExpressionSyntax syntax) =>
        destination switch
        {
            FunctionValueTypeSymbol value when
                GetNamedFunctionMatches(syntax, value.ReturnType, value.ParameterTypes).Length == 1 => 0,
            FunctionPointerTypeSymbol pointer when
                GetNamedFunctionMatches(syntax, pointer.ReturnType, pointer.ParameterTypes).Length == 1 => 1,
            _ => null,
        };

    private bool TryInferNamedFunctionGenericTypes(TypeSymbol pattern, ExpressionSyntax syntax,
        IDictionary<GenericParameterSymbol, TypeSymbol> inferred)
    {
        TypeSymbol returnPattern;
        ImmutableArray<TypeSymbol> parameterPatterns;
        switch (pattern)
        {
            case FunctionValueTypeSymbol value:
                returnPattern = value.ReturnType;
                parameterPatterns = value.ParameterTypes;
                break;
            case FunctionPointerTypeSymbol pointer:
                returnPattern = pointer.ReturnType;
                parameterPatterns = pointer.ParameterTypes;
                break;
            default:
                return true;
        }

        var successful = new List<Dictionary<GenericParameterSymbol, TypeSymbol>>();
        foreach (FunctionSymbol candidate in GetNamedFunctionCandidates(syntax).Where(candidate =>
                     candidate.Parameters.Length == parameterPatterns.Length))
        {
            var candidateInference = new Dictionary<GenericParameterSymbol, TypeSymbol>(inferred);
            if (!TryInferGenericType(returnPattern, candidate.ReturnType, candidateInference)) continue;
            bool compatible = parameterPatterns.Zip(candidate.Parameters).All(pair =>
                TryInferGenericType(pair.First, pair.Second.Type, candidateInference));
            if (compatible) successful.Add(candidateInference);
        }
        if (successful.Count != 1) return false;
        foreach (var pair in successful[0]) inferred[pair.Key] = pair.Value;
        return true;
    }

    private bool TryBindNamedFunctionValue(
        ExpressionSyntax syntax,
        FunctionValueTypeSymbol expected,
        out BoundExpression? value)
    {
        value = null;
        SyntaxToken nameToken = GetNamedFunctionToken(syntax);
        FunctionSymbol[] candidates = GetNamedFunctionCandidates(syntax);
        if (candidates.Length == 0) return false;
        FunctionSymbol[] matches = GetNamedFunctionMatches(syntax, expected.ReturnType, expected.ParameterTypes);
        if (matches.Length != 1)
        {
            RecordCandidates(syntax, null, candidates,
                matches.Length > 1 ? CandidateReason.Ambiguous : CandidateReason.NotInvocable);
            _diagnostics.Report(nameToken.Location,
                matches.Length > 1
                    ? $"function value '{nameToken.Text}' is ambiguous between: {FormatCallableCandidates(matches)}"
                    : $"no overload of '{nameToken.Text}' matches '{expected.ToDisplayString()}'",
                matches.Length > 1 ? DiagnosticIds.AmbiguousCall : DiagnosticIds.NoMatchingCandidate);
            value = new BoundErrorExpression();
            return true;
        }
        FunctionSymbol function = matches[0];
        if (!IsAccessible(function))
        {
            _diagnostics.Report(nameToken.Location,
                $"function '{function.Name}' is inaccessible from this context",
                DiagnosticIds.InaccessibleSymbol);
            value = new BoundErrorExpression();
            return true;
        }
        RecordCandidates(syntax, function, candidates, CandidateReason.None);
        _fileScope.TypeFactory.EnsureFunctionValueDestructor(expected, _fileScope.GlobalNamespace, syntax);
        value = new BoundFunctionValueExpression(function, expected, []);
        return true;
    }

    private bool TryBindNamedFunctionPointerForUserConversion(
        ExpressionSyntax syntax,
        FunctionPointerTypeSymbol expected,
        out BoundExpression? value)
    {
        value = null;
        SyntaxToken nameToken = GetNamedFunctionToken(syntax);
        FunctionSymbol[] candidates = GetNamedFunctionCandidates(syntax);
        if (candidates.Length == 0) return false;
        FunctionSymbol[] matches = GetNamedFunctionMatches(syntax, expected.ReturnType, expected.ParameterTypes);
        if (matches.Length != 1)
        {
            RecordCandidates(syntax, null, candidates,
                matches.Length > 1 ? CandidateReason.Ambiguous : CandidateReason.NotInvocable);
            _diagnostics.Report(nameToken.Location,
                matches.Length > 1
                    ? $"function address '{nameToken.Text}' is ambiguous between: {FormatCallableCandidates(matches)}"
                    : $"no overload of '{nameToken.Text}' matches '{expected.ToDisplayString()}'",
                matches.Length > 1 ? DiagnosticIds.AmbiguousCall : DiagnosticIds.NoMatchingCandidate);
            value = new BoundErrorExpression();
            return true;
        }
        FunctionSymbol function = matches[0];
        if (!IsAccessible(function))
        {
            _diagnostics.Report(nameToken.Location,
                $"function '{function.Name}' is inaccessible from this context",
                DiagnosticIds.InaccessibleSymbol);
            value = new BoundErrorExpression();
            return true;
        }
        RecordCandidates(syntax, function, candidates, CandidateReason.None);
        value = new BoundFunctionAddressExpression(function, expected);
        return true;
    }

    private bool ReportUnsupportedLambdaCapture(SyntaxToken name)
    {
        if (_scope.Lookup(name.Text) is not null) return false;
        for (FunctionBodyBinder? parent = _lambdaParent; parent is not null; parent = parent._lambdaParent)
        {
            if (parent._scope.Lookup(name.Text) is null &&
                !(name.Kind == SyntaxKind.ThisKeyword && parent._function.HasImplicitThis) &&
                parent._function.ContainingType?.FindInstanceField(name.Text) is null)
                continue;
            _diagnostics.Report(name.Location, $"'{name.Text}' was not explicitly captured",
                DiagnosticIds.LambdaCaptureNotSupported);
            return true;
        }
        return false;
    }
}
