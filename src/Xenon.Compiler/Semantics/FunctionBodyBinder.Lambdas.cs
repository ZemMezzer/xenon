using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

internal sealed partial class FunctionBodyBinder
{
    private sealed record PreparedLambdaCapture(
        LambdaCaptureSyntax Syntax,
        VariableSymbol Source,
        TypeSymbol StorageType,
        BoundExpression Initializer);

    private FunctionBodyBinder? _lambdaParent;
    private readonly Dictionary<LambdaExpressionSyntax, ImmutableArray<PreparedLambdaCapture>>
        _preparedLambdaCaptures = new(ReferenceEqualityComparer.Instance);
    private FunctionSymbol LexicalAccessContext => _lambdaParent?.LexicalAccessContext ?? _function;

    private BoundExpression BindDeferredLambdaExpression(LambdaExpressionSyntax syntax)
    {
        // Captures are evaluated when the lambda argument is evaluated, even though
        // its signature cannot be contextualized until overload resolution finishes.
        // Preparing them here preserves source-order ownership and borrow effects.
        PrepareLambdaCaptures(syntax);
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
                _diagnostics.Report(syntax.IntroducerToken.Location,
                    $"lambda signature does not match contextual type '{contextual.ToDisplayString()}'",
                    DiagnosticIds.TypeMismatch);
        }

        FunctionValueTypeSymbol? functionValue = valueTarget;
        if (functionValue is null && pointerTarget is null && syntax.ExplicitReturnType is not null)
            pointerTarget = _fileScope.TypeFactory.FunctionPointer(returnType, parameterTypes);
        if (!syntax.Captures.IsEmpty && pointerTarget is not null)
        {
            _diagnostics.Report(syntax.OpenBracketToken!.Location,
                "capturing lambda cannot convert to raw function pointer",
                DiagnosticIds.LambdaCaptureNotSupported);
            return new BoundErrorExpression();
        }

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
            var captured = new CaptureVariableSymbol(prepared.Source, prepared.StorageType,
                capture.CaptureKind, captureSymbols.Count, function, capture);
            captureSymbols.Add(captured);
            boundCaptures.Add(new BoundFunctionValueCapture(captured, prepared.Initializer));
        }
        function.LambdaCaptures = captureSymbols.ToImmutable();

        foreach ((ParameterSyntax parameter, ParameterSymbol symbol) in syntax.Parameters.Zip(function.Parameters))
            _semanticInfo.Declarations[parameter] = symbol;

        var binder = new FunctionBodyBinder(function, _fileScope, _diagnostics, _constants,
            _semanticInfo, _genericSpecializer, _cancellationToken) { _lambdaParent = this };
        foreach (CaptureVariableSymbol capture in function.LambdaCaptures)
        {
            binder._scope.TryDeclare(capture);
            binder._definitelyAssigned.Add(capture);
        }
        BoundBlockStatement body = binder.BindBody(syntax.Body);
        _semanticInfo.LambdaFunctions.Add(new BoundFunction(function, body));
        foreach (var entry in binder.ExpressionLocations) _expressionLocations.TryAdd(entry.Key, entry.Value);

        if (pointerTarget is not null)
            return new BoundFunctionAddressExpression(function, pointerTarget);
        functionValue ??= _fileScope.TypeFactory.FunctionValue(returnType, parameterTypes);
        _fileScope.TypeFactory.EnsureFunctionValueDestructor(functionValue, _fileScope.GlobalNamespace, syntax);
        return new BoundFunctionValueExpression(function, functionValue, boundCaptures.ToImmutable());
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

            _semanticInfo.Symbols[capture] = SymbolInfo.FromSymbol(source);
            TypeSymbol storageType = capture.CaptureKind switch
            {
                LambdaCaptureKind.MutableBorrow => _fileScope.TypeFactory.ReferenceTo(source.Type),
                LambdaCaptureKind.ReadonlyBorrow => _fileScope.TypeFactory.ReferenceTo(source.Type, isReadonly: true),
                _ => source.Type,
            };
            BoundExpression sourceExpression = new BoundVariableExpression(source);
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
        bool usesUserConversion = false;
        if (pointerTarget is null && valueTarget is null)
        {
            valueTarget = GetLambdaUserConversionInput(destination);
            usesUserConversion = valueTarget is not null;
        }

        TypeSymbol? returnType = valueTarget?.ReturnType ?? pointerTarget?.ReturnType;
        ImmutableArray<TypeSymbol> parameterTypes = valueTarget?.ParameterTypes ??
            pointerTarget?.ParameterTypes ?? [];
        if (returnType is null || parameterTypes.Length != syntax.Parameters.Length)
            return null;
        if (pointerTarget is not null && !syntax.Captures.IsEmpty)
            return null;

        var diagnostics = new DiagnosticBag();
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

        if (usesUserConversion)
            return int.MaxValue - 1;
        // A first-class function value is the lambda's native representation.
        // Conversion to a raw pointer is viable only for non-capturing lambdas
        // and ranks one standard step after the native contextual conversion.
        return valueTarget is not null ? 0 : 1;
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

    private bool TryBindNamedFunctionValue(
        NameExpressionSyntax syntax,
        FunctionValueTypeSymbol expected,
        out BoundExpression? value)
    {
        value = null;
        FunctionSymbol[] candidates = (_function.ContainingType is { } containing
                ? containing.LookupMethods(syntax.IdentifierToken.Text)
                : [])
            .Concat(_fileScope.ResolveFunctions(syntax.IdentifierToken.Text))
            .Distinct()
            .ToArray();
        if (candidates.Length == 0) return false;
        FunctionSymbol[] matches = candidates.Where(candidate =>
            !candidate.HasImplicitThis && !candidate.IsGenericDefinition &&
            TypeIdentity.AreSame(candidate.ReturnType, expected.ReturnType) &&
            candidate.Parameters.Length == expected.ParameterTypes.Length &&
            candidate.Parameters.Zip(expected.ParameterTypes).All(pair =>
                TypeIdentity.AreSame(pair.First.Type, pair.Second))).ToArray();
        if (matches.Length != 1)
        {
            RecordCandidates(syntax, null, candidates,
                matches.Length > 1 ? CandidateReason.Ambiguous : CandidateReason.NotInvocable);
            _diagnostics.Report(syntax.IdentifierToken.Location,
                matches.Length > 1
                    ? $"function value '{syntax.IdentifierToken.Text}' is ambiguous between: {FormatCallableCandidates(matches)}"
                    : $"no overload of '{syntax.IdentifierToken.Text}' matches '{expected.ToDisplayString()}'",
                matches.Length > 1 ? DiagnosticIds.AmbiguousCall : DiagnosticIds.NoMatchingCandidate);
            value = new BoundErrorExpression();
            return true;
        }
        FunctionSymbol function = matches[0];
        if (!IsAccessible(function))
        {
            _diagnostics.Report(syntax.IdentifierToken.Location,
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
