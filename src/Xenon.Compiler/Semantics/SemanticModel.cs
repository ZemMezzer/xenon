using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>An immutable, tooling-facing semantic snapshot for a compilation.</summary>
public sealed class SemanticModel
{
    private readonly ImmutableArray<SyntaxTree> _syntaxTrees;
    private readonly SemanticInfoStore _semanticInfo;
    private readonly SyntaxTree? _primaryTree;

    internal SemanticModel(NamespaceSymbol globalNamespace, TypeFactory typeFactory,
        ImmutableArray<BoundFunction> functions, ImmutableArray<Diagnostic> semanticDiagnostics,
        ImmutableArray<SyntaxTree> syntaxTrees, SemanticInfoStore semanticInfo,
        bool requiresTargetLayout = false, SyntaxTree? primaryTree = null)
    {
        GlobalNamespace = globalNamespace;
        TypeFactory = typeFactory;
        Functions = functions;
        SemanticDiagnostics = semanticDiagnostics;
        _syntaxTrees = syntaxTrees;
        _semanticInfo = semanticInfo;
        _primaryTree = primaryTree;
        Diagnostics = syntaxTrees.SelectMany(tree => tree.Diagnostics).Concat(semanticDiagnostics).ToImmutableArray();
        RequiresTargetLayout = requiresTargetLayout;
    }

    public NamespaceSymbol GlobalNamespace { get; }
    public TypeFactory TypeFactory { get; }
    public ImmutableArray<BoundFunction> Functions { get; }
    public ImmutableArray<Diagnostic> SemanticDiagnostics { get; }
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public bool RequiresTargetLayout { get; }
    public SyntaxTree? SyntaxTree => _primaryTree;

    internal SemanticModel ForTree(SyntaxTree tree) => new(GlobalNamespace, TypeFactory, Functions,
        SemanticDiagnostics, _syntaxTrees, _semanticInfo, RequiresTargetLayout, tree);

    public Symbol? GetDeclaredSymbol(SyntaxNode declaration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        cancellationToken.ThrowIfCancellationRequested();
        return _semanticInfo.Declarations.GetValueOrDefault(declaration);
    }

    public SymbolInfo GetSymbolInfo(SyntaxNode syntax, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        cancellationToken.ThrowIfCancellationRequested();
        if (_semanticInfo.Symbols.TryGetValue(syntax, out SymbolInfo info)) return info;
        if (_semanticInfo.Declarations.TryGetValue(syntax, out Symbol? declared))
            return SymbolInfo.FromSymbol(declared);
        return syntax is TypeSyntax && _semanticInfo.Types.TryGetValue(syntax, out TypeInfo typeInfo) &&
            TryGetDeclaredType(typeInfo.Type, out DeclaredTypeSymbol type)
            ? SymbolInfo.FromSymbol(type) : SymbolInfo.None;
    }

    public TypeInfo GetTypeInfo(SyntaxNode syntax, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        cancellationToken.ThrowIfCancellationRequested();
        return _semanticInfo.Types.GetValueOrDefault(syntax,
            new TypeInfo(BuiltinTypes.Error, BuiltinTypes.Error));
    }

    public ReceiverInfo? GetReceiverInfo(ExpressionSyntax receiver, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        cancellationToken.ThrowIfCancellationRequested();
        return _semanticInfo.Receivers.TryGetValue(receiver, out ReceiverInfo info) ? info : null;
    }

    /// <summary>Resolves the exact semantic identity under an editor position.</summary>
    public SymbolInfo GetSymbolInfoAtPosition(SyntaxTree tree, int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        EnsureTree(tree);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, tree.Source.Length);
        cancellationToken.ThrowIfCancellationRequested();

        var matches = _semanticInfo.Symbols
            .Select(pair => (Pair: pair, Location: TryGetReferenceLocation(pair.Key, out TextLocation location)
                ? location : (TextLocation?)null))
            .Where(item => item.Location is { } location &&
                location.Source.FileId == tree.Source.FileId &&
                IsPositionMatch(location.Span, position))
            .Select(item => (Info: item.Pair.Value, Span: item.Location!.Value.Span))
            .Concat(_semanticInfo.Declarations
                .SelectMany(pair => pair.Value.DeclaringSyntaxReferences
                    .Where(reference => ReferenceEquals(reference.Declaration, pair.Key) &&
                        reference.Source.FileId == tree.Source.FileId)
                    .Select(reference => (Info: SymbolInfo.FromSymbol(pair.Value), Span: reference.Span)))
                .Where(item => IsPositionMatch(item.Span, position)))
            .Concat(_semanticInfo.Types
                .Where(pair => pair.Key is TypeSyntax &&
                    TryGetDeclaredType(pair.Value.Type, out _) &&
                    TryGetReferenceLocation(pair.Key, out TextLocation location) &&
                    location.Source.FileId == tree.Source.FileId &&
                    IsPositionMatch(location.Span, position))
                .Select(pair =>
                {
                    _ = TryGetDeclaredType(pair.Value.Type, out DeclaredTypeSymbol type);
                    _ = TryGetReferenceLocation(pair.Key, out TextLocation location);
                    return (Info: SymbolInfo.FromSymbol(type!), Span: location.Span);
                }))
            .OrderBy(item => item.Span.Length)
            .ThenByDescending(item => item.Info.Symbol is not null)
            .ToArray();
        return matches.FirstOrDefault(item => item.Info.Symbol is not null).Info is { Symbol: not null } resolved
            ? resolved
            : matches.FirstOrDefault().Info;

        static bool IsPositionMatch(TextSpan span, int value) => value >= span.Start &&
            (value < span.End || span.Length == 0 && value == span.Start);
    }

    public SymbolInfo GetSymbolInfoAtPosition(int position,
        CancellationToken cancellationToken = default) =>
        GetSymbolInfoAtPosition(_primaryTree ?? throw new InvalidOperationException(
            "Use the SyntaxTree overload for a multi-file compilation."), position, cancellationToken);

    /// <summary>All explicit declarations contributed by the selected source tree.</summary>
    public ImmutableArray<Symbol> GetDeclaredSymbols(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var seen = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
        IEnumerable<Symbol> symbols = _semanticInfo.Declarations.Values.Where(seen.Add);
        if (_primaryTree is not null)
            symbols = symbols.Where(symbol => symbol.DeclaringSyntaxReferences.Any(reference =>
                ReferenceEquals(reference.Source, _primaryTree.Source)));
        return symbols.OrderBy(symbol => symbol.Locations.FirstOrDefault().Span.Start)
            .ThenBy(symbol => symbol.QualifiedName, StringComparer.Ordinal).ToImmutableArray();
    }

    public ImmutableArray<Symbol> LookupSymbols(int position, CancellationToken cancellationToken = default) =>
        LookupSymbols(GetPrimarySource(), position, cancellationToken);

    public ImmutableArray<Symbol> LookupSymbols(SyntaxTree tree, int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        EnsureTree(tree);
        return LookupSymbols(tree.Source, position, cancellationToken);
    }

    /// <summary>
    /// User-visible symbols valid for ordinary unqualified source lookup at a position.
    /// Unlike raw scope inspection this applies callable context, inheritance,
    /// accessibility, static/instance, readonly, and compiler-generated filtering.
    /// </summary>
    public ImmutableArray<Symbol> GetCompletionSymbols(int position,
        CancellationToken cancellationToken = default) =>
        GetCompletionSymbols(_primaryTree ?? throw new InvalidOperationException(
            "Use the SyntaxTree overload for a multi-file compilation."), position, cancellationToken);

    public ImmutableArray<Symbol> GetCompletionSymbols(SyntaxTree tree, int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        EnsureTree(tree);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, tree.Source.Length);
        cancellationToken.ThrowIfCancellationRequested();
        PositionScope[] scopes = GetScopes(tree.Source, position);
        var result = ImmutableArray.CreateBuilder<Symbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PositionScope scope in scopes)
            foreach (VariableSymbol variable in scope.Variables)
            {
                bool visible = variable is ParameterSymbol || variable.Locations.IsEmpty ||
                    variable.Locations[0].Span.Start <= position;
                if (visible && variable.IsUserVisible && names.Add(variable.Name)) result.Add(variable);
            }

        FunctionSymbol? function = scopes.FirstOrDefault()?.Function;
        if (function?.ContainingType is DeclaredTypeSymbol containingType)
            foreach (Symbol member in AllMembers(containingType)
                .Where(member => IsUnqualifiedCompletionMember(member, function))
                .Where(member => IsAccessible(member, containingType))
                .OrderBy(member => member.Name, StringComparer.Ordinal))
                if (names.Add(member.Name)) result.Add(member);

        if (_semanticInfo.FileScopes.TryGetValue(tree.Source, out FileSymbolScope? fileScope))
        {
            HashSet<string> shadowedNames = names.ToHashSet(StringComparer.Ordinal);
            var seenFileSymbols = new HashSet<Symbol>(ReferenceEqualityComparer.Instance);
            foreach (IGrouping<string, Symbol> group in fileScope.GetFileSymbols()
                .Where(EditorSymbolClassifier.IsEditorVisible)
                .OrderBy(symbol => symbol.QualifiedName, StringComparer.Ordinal)
                .GroupBy(symbol => symbol.Name, StringComparer.Ordinal))
            {
                if (shadowedNames.Contains(group.Key)) continue;
                foreach (Symbol symbol in group)
                    if (seenFileSymbols.Add(symbol)) result.Add(symbol);
                names.Add(group.Key);
            }
        }
        return result.ToImmutable();
    }

    public CompletionReceiverInfo GetCompletionReceiver(ExpressionSyntax receiver,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        cancellationToken.ThrowIfCancellationRequested();
        bool hasSemanticReceiver = _semanticInfo.Receivers.TryGetValue(receiver, out ReceiverInfo semanticReceiver) &&
            semanticReceiver.Type is not ErrorTypeSymbol;
        if (hasSemanticReceiver && !semanticReceiver.IsStatic)
            return new CompletionReceiverInfo(CompletionReceiverKind.Value, Type: semanticReceiver.Type);
        SourceText? source = SyntaxNavigator.GetTokens(receiver).FirstOrDefault()?.Location.Source;
        FileSymbolScope? fileScope = source is not null
            ? _semanticInfo.FileScopes.GetValueOrDefault(source) : null;
        string[]? dottedParts = TryGetDottedName(receiver, out string[] parts) ? parts : null;
        NamespaceSymbol? namespaceCandidate = fileScope is not null && dottedParts is not null
            ? fileScope.ResolveNamespaceForTooling(dottedParts) : null;
        TypeSymbol? typeCandidate = fileScope is not null && dottedParts is not null
            ? fileScope.ResolveTypeForTooling(dottedParts) : null;
        if (namespaceCandidate is not null && typeCandidate is not null)
            return new CompletionReceiverInfo(CompletionReceiverKind.Ambiguous);
        if (hasSemanticReceiver)
            return new CompletionReceiverInfo(CompletionReceiverKind.Type, Type: semanticReceiver.Type);
        if (_semanticInfo.Symbols.TryGetValue(receiver, out SymbolInfo symbolInfo))
        {
            if (symbolInfo.Symbol is NamespaceSymbol @namespace)
                return new CompletionReceiverInfo(CompletionReceiverKind.Namespace, Namespace: @namespace);
            if (symbolInfo.Symbol is TypeSymbol type)
                return new CompletionReceiverInfo(CompletionReceiverKind.Type, Type: type);
        }
        if (namespaceCandidate is not null)
            return new CompletionReceiverInfo(CompletionReceiverKind.Namespace, Namespace: namespaceCandidate);
        return typeCandidate is null ? default :
            new CompletionReceiverInfo(CompletionReceiverKind.Type, Type: typeCandidate);
    }

    public ImmutableArray<Symbol> GetCompletionSymbols(MemberAccessExpressionSyntax access, int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        CompletionReceiverInfo receiver = GetCompletionReceiver(access.Receiver, cancellationToken);
        if (receiver.Kind == CompletionReceiverKind.Namespace && receiver.Namespace is not null)
        {
            SourceText? source = SyntaxNavigator.GetTokens(access).FirstOrDefault()?.Location.Source;
            if (source is not null && _semanticInfo.FileScopes.TryGetValue(source, out FileSymbolScope? fileScope))
                return fileScope.GetNamespaceSymbolsForTooling(receiver.Namespace)
                    .OrderBy(symbol => symbol.Name, StringComparer.Ordinal)
                    .ThenBy(symbol => symbol.QualifiedName, StringComparer.Ordinal).ToImmutableArray();
            return [];
        }
        if (receiver.Type is null) return [];
        if (receiver.Kind == CompletionReceiverKind.Value &&
            _semanticInfo.Receivers.TryGetValue(access.Receiver, out ReceiverInfo value))
            return LookupMembers(value.Type, position, new MemberLookupOptions(MemberAccessKind.Instance,
                IncludeInaccessible: false, IsReadonlyReceiver: value.IsReadonly), cancellationToken);
        return receiver.Kind == CompletionReceiverKind.Type
            ? LookupMembers(receiver.Type, position, new MemberLookupOptions(MemberAccessKind.Static), cancellationToken)
            : [];
    }

    private ImmutableArray<Symbol> LookupSymbols(SourceText source, int position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, source.Length);
        cancellationToken.ThrowIfCancellationRequested();
        PositionScope[] scopes = GetScopes(source, position);
        var result = ImmutableArray.CreateBuilder<Symbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PositionScope scope in scopes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (VariableSymbol variable in scope.Variables)
            {
                bool visible = variable is ParameterSymbol || variable.Locations.IsEmpty ||
                    variable.Locations[0].Span.Start <= position;
                if (visible && names.Add(variable.Name)) result.Add(variable);
            }
        }
        FunctionSymbol? function = scopes.FirstOrDefault()?.Function;
        DeclaredTypeSymbol? containingType = function?.ContainingType ?? _semanticInfo.TypeRegions
            .Where(region => ReferenceEquals(region.Source, source) && Contains(region.Span, position, region.IncludeEnd))
            .OrderBy(region => region.Span.Length).Select(region => region.Type).FirstOrDefault();
        if (containingType is not null)
            foreach (Symbol member in containingType.GetMembers().OrderBy(member => member.Name, StringComparer.Ordinal))
                if (names.Add(member.Name)) result.Add(member);
        if (_semanticInfo.FileScopes.TryGetValue(source, out FileSymbolScope? fileScope))
            foreach (Symbol symbol in fileScope.GetFileSymbols().OrderBy(symbol => symbol.QualifiedName, StringComparer.Ordinal))
                if (names.Add(symbol.Name)) result.Add(symbol);
        return result.ToImmutable();
    }

    public ImmutableArray<Symbol> LookupMembers(TypeSymbol receiverType, int position,
        MemberLookupOptions options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receiverType);
        cancellationToken.ThrowIfCancellationRequested();
        if (receiverType is ArrayTypeSymbol array)
            return options.AccessKind == MemberAccessKind.Static ? [] : _semanticInfo.GetArrayMembers(array).Cast<Symbol>()
                .OrderBy(member => member.Name, StringComparer.Ordinal).ToImmutableArray();
        if (receiverType is GenericParameterSymbol genericParameter)
            return GenericConstraintMemberLookup.GetMembers(genericParameter)
                .Where(member => member.IsUserVisible)
                .Where(member => IsApplicableMember(member, options))
                .Where(member => options.IncludeInaccessible || IsAccessible(member, GetContainingTypeAtPosition(position)))
                .DistinctBy(member => member.ToDisplayString(SymbolDisplayFormat.Signature))
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .ThenBy(member => member.ToDisplayString(SymbolDisplayFormat.Signature), StringComparer.Ordinal)
                .ToImmutableArray();

        DeclaredTypeSymbol? type = receiverType switch
        {
            PointerTypeSymbol pointer => pointer.ElementType as DeclaredTypeSymbol,
            ReferenceTypeSymbol reference => reference.ElementType as DeclaredTypeSymbol,
            DeclaredTypeSymbol declared => declared,
            _ => null,
        };
        if (type is null) return [];
        DeclaredTypeSymbol? withinType = GetContainingTypeAtPosition(position);
        return AllMembers(type).Distinct()
            .Where(member => member.IsUserVisible)
            .Where(member => IsApplicableMember(member, options))
            .Where(member => options.IncludeInaccessible || IsAccessible(member, withinType))
            .OrderBy(member => member.Name, StringComparer.Ordinal)
            .ThenBy(member => member.ToDisplayString(SymbolDisplayFormat.Signature), StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public ImmutableArray<Symbol> LookupMembers(TypeSymbol receiverType,
        MemberLookupOptions options = default, CancellationToken cancellationToken = default) =>
        LookupMembers(receiverType, 0, options, cancellationToken);

    public ImmutableArray<Symbol> LookupMembers(ExpressionSyntax receiver, int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_semanticInfo.Receivers.TryGetValue(receiver, out ReceiverInfo info)) return [];
        return LookupMembers(info.Type, position,
            new MemberLookupOptions(
                info.IsStatic ? MemberAccessKind.Static : MemberAccessKind.Instance,
                IncludeInaccessible: false,
                IsReadonlyReceiver: info.IsReadonly),
            cancellationToken);
    }

    public ImmutableArray<Diagnostic> GetDiagnostics(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return FilterDiagnostics(null);
    }

    public ImmutableArray<Diagnostic> GetDiagnostics(TextSpan span, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return FilterDiagnostics(span);
    }

    /// <summary>
    /// Returns only successfully bound source occurrences. Candidate-only, ambiguous and
    /// unresolved nodes are deliberately excluded so tooling never treats spelling as identity.
    /// Compiler-provided setter value parameters are included because their references are
    /// explicit source tokens even though the parameter itself has no declaration token.
    /// </summary>
    public ImmutableArray<ResolvedSymbolReference> GetResolvedReferences(
        CancellationToken cancellationToken = default)
    {
        var result = ImmutableArray.CreateBuilder<ResolvedSymbolReference>();
        var seen = new HashSet<(Symbol Symbol, SourceFileId File, TextSpan Span)>(
            ResolvedReferenceIdentityComparer.Instance);
        foreach ((SyntaxNode syntax, SymbolInfo info) in _semanticInfo.Symbols)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (info.Symbol is not { } symbol || !IsReferenceableSourceSymbol(symbol) ||
                !TryGetReferenceLocation(syntax, out TextLocation location) ||
                _primaryTree is not null && !ReferenceEquals(location.Source, _primaryTree.Source) ||
                !seen.Add((symbol, location.Source.FileId, location.Span)))
                continue;
            result.Add(new ResolvedSymbolReference(symbol, location, GetReferenceKind(syntax)));
            if (symbol is FunctionSymbol { FunctionKind: FunctionKind.Constructor,
                    ContainingType: DeclaredTypeSymbol constructedType } &&
                seen.Add((constructedType, location.Source.FileId, location.Span)))
                result.Add(new ResolvedSymbolReference(constructedType, location, ResolvedReferenceKind.Type));
        }
        foreach ((SyntaxNode syntax, TypeInfo info) in _semanticInfo.Types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (syntax is not TypeSyntax || !TryGetDeclaredType(info.Type, out DeclaredTypeSymbol symbol) ||
                !TryGetReferenceLocation(syntax, out TextLocation location) ||
                _primaryTree is not null && !ReferenceEquals(location.Source, _primaryTree.Source) ||
                !seen.Add((symbol, location.Source.FileId, location.Span)))
                continue;
            result.Add(new ResolvedSymbolReference(symbol, location, ResolvedReferenceKind.Type));
        }
        return result.OrderBy(item => item.Location.Source.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Location.Span.Start).ThenBy(item => item.Location.Span.Length)
            .ThenBy(item => item.Symbol.QualifiedName, StringComparer.Ordinal).ToImmutableArray();
    }

    private static bool IsReferenceableSourceSymbol(Symbol symbol)
    {
        if (symbol.IsSourceDefined) return true;
        if (symbol is not ParameterSymbol { Name: "value", ContainingSymbol: FunctionSymbol accessor })
            return false;
        return ReferenceEquals(accessor.ContainingProperty?.Setter, accessor) ||
               ReferenceEquals(accessor.ContainingIndexer?.Setter, accessor);
    }

    /// <summary>The semantic type associated with a symbol for editor navigation.</summary>
    public static TypeSymbol? GetAssociatedType(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol switch
        {
            DeclaredTypeSymbol type => type,
            FunctionSymbol { FunctionKind: FunctionKind.Constructor or FunctionKind.Destructor,
                ContainingType: not null } function => function.ContainingType,
            FunctionSymbol function => function.ReturnType,
            VariableSymbol variable => variable.Type,
            FieldSymbol field => field.Type,
            PropertySymbol property => property.Type,
            InterfacePropertySymbol property => property.Type,
            ConstantSymbol constant => constant.Type,
            IndexerSymbol indexer => indexer.Type,
            InterfaceIndexerSymbol indexer => indexer.Type,
            SyntheticMemberSymbol member => member.Type,
            _ => null,
        };
    }

    public static DeclaredTypeSymbol? GetAssociatedDeclaredType(Symbol symbol)
    {
        TypeSymbol? type = GetAssociatedType(symbol);
        return type is not null && TryGetDeclaredType(type, out DeclaredTypeSymbol declared)
            ? declared : null;
    }

    /// <summary>Checks rename collisions against the exact semantic declaration scope/container.</summary>
    public bool HasRenameConflict(Symbol symbol, string newName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        cancellationToken.ThrowIfCancellationRequested();
        if (symbol is ParameterSymbol parameter)
            return ((FunctionSymbol)parameter.ContainingSymbol!).Parameters.Any(candidate =>
                !ReferenceEquals(candidate, parameter) && candidate.Name == newName) ||
                ScopeVariables(parameter).Any(candidate => candidate.Name == newName);
        if (symbol is LocalVariableSymbol local)
            return ScopeVariables(local).Any(candidate => candidate.Name == newName);
        if (symbol.ContainingSymbol is DeclaredTypeSymbol containingType)
            return containingType.GetMembers().Any(candidate =>
                !ReferenceEquals(candidate, symbol) && ReferenceEquals(candidate.ContainingSymbol, containingType) &&
                candidate.Name == newName);
        if (symbol.ContainingSymbol is EnumTypeSymbol enumeration)
            return enumeration.Members.Any(candidate => !ReferenceEquals(candidate, symbol) && candidate.Name == newName);
        if (symbol.ContainingSymbol is NamespaceSymbol @namespace)
        {
            IEnumerable<Symbol> siblings = symbol switch
            {
                DeclaredTypeSymbol => @namespace.Types,
                FunctionSymbol => @namespace.Functions,
                ConstantSymbol => @namespace.Constants,
                _ => [],
            };
            return siblings.Any(candidate => !ReferenceEquals(candidate, symbol) && candidate.Name == newName);
        }
        return false;

        IEnumerable<VariableSymbol> ScopeVariables(VariableSymbol variable)
        {
            PositionScope? declarationScope = _semanticInfo.Scopes
                .Where(scope => scope.Variables.Any(candidate => ReferenceEquals(candidate, variable)))
                .OrderBy(scope => scope.Span.Length).FirstOrDefault();
            return declarationScope?.Variables.Where(candidate => !ReferenceEquals(candidate, variable)) ?? [];
        }
    }

    private ImmutableArray<Diagnostic> FilterDiagnostics(TextSpan? span)
    {
        IEnumerable<Diagnostic> diagnostics = Diagnostics;
        if (_primaryTree is not null)
            diagnostics = diagnostics.Where(diagnostic => ReferenceEquals(diagnostic.Location.Source, _primaryTree.Source));
        if (span is { } filter)
            diagnostics = diagnostics.Where(diagnostic => Overlaps(diagnostic.Location.Span, filter));
        return diagnostics.ToImmutableArray();
    }

    private DeclaredTypeSymbol? GetContainingTypeAtPosition(int position)
    {
        if (_primaryTree is null) return null;
        return _semanticInfo.Scopes
            .Where(scope => ReferenceEquals(scope.Source, _primaryTree.Source) && Contains(scope.Span, position, scope.IncludeEnd))
            .OrderBy(scope => scope.Span.Length).Select(scope => scope.Function.ContainingType)
            .FirstOrDefault(type => type is not null) ?? _semanticInfo.TypeRegions
            .Where(region => ReferenceEquals(region.Source, _primaryTree.Source) && Contains(region.Span, position, region.IncludeEnd))
            .OrderBy(region => region.Span.Length).Select(region => region.Type).FirstOrDefault();
    }

    private static IEnumerable<Symbol> AllMembers(DeclaredTypeSymbol type) => type switch
    {
        StructTypeSymbol structure => structure.GetMembers().Concat(structure.BaseType is null ? [] : AllMembers(structure.BaseType)),
        InterfaceTypeSymbol @interface => @interface.SelfAndBaseInterfaces.SelectMany(item => item.GetMembers()),
        _ => type.GetMembers(),
    };

    private static bool IsUnqualifiedCompletionMember(Symbol member, FunctionSymbol function) => member switch
    {
        ConstantSymbol => true,
        FieldSymbol field => !function.IsStatic || field.IsStatic,
        PropertySymbol property => !function.IsStatic && property.Getter is not null &&
            (!function.IsReadonly || property.Getter.IsReadonly),
        InterfacePropertySymbol property => !function.IsStatic && property.Getter is not null &&
            (!function.IsReadonly || property.Getter.IsReadonly),
        FunctionSymbol method => method.FunctionKind == FunctionKind.Method &&
            (!function.IsStatic || method.IsStatic) &&
            method.ContainingProperty is null && method.ContainingInterfaceProperty is null &&
            method.ContainingIndexer is null && method.ContainingInterfaceIndexer is null &&
            (!function.IsReadonly || method.IsStatic || method.IsReadonly),
        _ => false,
    };

    private static bool IsApplicableMember(Symbol member, MemberLookupOptions options) => member switch
    {
        FieldSymbol field => options.AccessKind == MemberAccessKind.Any ||
            (options.AccessKind == MemberAccessKind.Static) == field.IsStatic,
        FunctionSymbol function => function.FunctionKind == FunctionKind.Method &&
            (options.AccessKind == MemberAccessKind.Any || (options.AccessKind == MemberAccessKind.Static) == function.IsStatic) &&
            (!options.IsReadonlyReceiver || function.IsReadonly),
        ConstantSymbol => options.AccessKind != MemberAccessKind.Instance,
        PropertySymbol property => options.AccessKind != MemberAccessKind.Static &&
            (!options.IsReadonlyReceiver || property.Getter?.IsReadonly == true),
        InterfacePropertySymbol property => !options.IsReadonlyReceiver || property.Getter?.IsReadonly == true,
        SyntheticMemberSymbol synthetic => options.AccessKind != MemberAccessKind.Static &&
            (!options.IsReadonlyReceiver || synthetic.IsReadonly),
        IndexerSymbol indexer => options.AccessKind != MemberAccessKind.Static &&
            (!options.IsReadonlyReceiver || indexer.Getter?.IsReadonly == true),
        InterfaceIndexerSymbol indexer => options.AccessKind != MemberAccessKind.Static &&
            (!options.IsReadonlyReceiver || indexer.Getter?.IsReadonly == true),
        TemplateMethodRequirementSymbol method =>
            (options.AccessKind == MemberAccessKind.Any || (options.AccessKind == MemberAccessKind.Static) == method.IsStatic) &&
            (!options.IsReadonlyReceiver || method.IsReadonly),
        TemplatePropertyRequirementSymbol property =>
            (options.AccessKind == MemberAccessKind.Any || (options.AccessKind == MemberAccessKind.Static) == property.IsStatic) &&
            property.HasGetter && (!options.IsReadonlyReceiver || property.IsReadonly),
        TemplateIndexerRequirementSymbol indexer => options.AccessKind != MemberAccessKind.Static &&
            indexer.HasGetter && (!options.IsReadonlyReceiver || indexer.IsReadonly),
        _ => false,
    };

    private static bool IsAccessible(Symbol symbol, DeclaredTypeSymbol? withinType) => symbol switch
    {
        FieldSymbol field => field.IsPublic || ReferenceEquals(field.ContainingType, withinType),
        FunctionSymbol function => function.IsPublic || ReferenceEquals(function.ContainingType, withinType),
        PropertySymbol property => property.IsPublic || ReferenceEquals(property.ContainingType, withinType),
        IndexerSymbol indexer => indexer.IsPublic || ReferenceEquals(indexer.ContainingType, withinType),
        TemplateMemberRequirementSymbol requirement => requirement.IsPublic,
        _ => true,
    };

    private SourceText GetPrimarySource()
    {
        if (_primaryTree is not null) return _primaryTree.Source;
        if (_syntaxTrees.Length == 1) return _syntaxTrees[0].Source;
        throw new InvalidOperationException("Use Compilation.GetSemanticModel(tree) or LookupSymbols(tree, position) for a multi-file compilation.");
    }

    private PositionScope[] GetScopes(SourceText source, int position) => _semanticInfo.Scopes
        .Where(scope => ReferenceEquals(scope.Source, source) && Contains(scope.Span, position, scope.IncludeEnd))
        .OrderBy(scope => scope.Span.Length).ToArray();

    private void EnsureTree(SyntaxTree tree)
    {
        if (!_syntaxTrees.Any(candidate => ReferenceEquals(candidate, tree)))
            throw new ArgumentException("The syntax tree does not belong to this compilation.", nameof(tree));
    }

    private static bool Contains(TextSpan span, int position, bool includeEnd = false) =>
        position >= span.Start && (position < span.End || includeEnd && position == span.End);

    private static bool Overlaps(TextSpan left, TextSpan right)
    {
        if (left.Length == 0 && right.Length == 0) return left.Start == right.Start;
        if (left.Length == 0) return left.Start >= right.Start && left.Start < right.End;
        if (right.Length == 0) return right.Start >= left.Start && right.Start < left.End;
        return left.Start < right.End && right.Start < left.End;
    }

    private static ResolvedReferenceKind GetReferenceKind(SyntaxNode syntax) => syntax switch
    {
        TypeSyntax or NewExpressionSyntax or StructPositionalConstructionExpressionSyntax =>
            ResolvedReferenceKind.Type,
        CallExpressionSyntax => ResolvedReferenceKind.Call,
        MemberAccessExpressionSyntax or IndexExpressionSyntax => ResolvedReferenceKind.Member,
        _ => ResolvedReferenceKind.Reference,
    };

    private static bool TryGetReferenceLocation(SyntaxNode syntax, out TextLocation location)
    {
        SyntaxToken? token = syntax switch
        {
            NameExpressionSyntax value => value.IdentifierToken,
            MemberAccessExpressionSyntax value => value.MemberToken,
            NamedTypeSyntax value => value.NameToken,
            UnaryTypeSyntax value => value.NameToken,
            NewExpressionSyntax value => value.Type.NameToken,
            StructPositionalConstructionExpressionSyntax value => value.Type.NameToken,
            CallExpressionSyntax value => GetReferenceToken(value.Target),
            IndexExpressionSyntax value => value.OpenBracketToken,
            ThisExpressionSyntax value => value.ThisKeyword,
            TypeLayoutExpressionSyntax value when value.FieldToken is not null => value.FieldToken,
            _ => null,
        };
        if (token is null)
        {
            location = default;
            return false;
        }
        location = token.Location;
        return true;
    }

    private static SyntaxToken? GetReferenceToken(ExpressionSyntax syntax) => syntax switch
    {
        NameExpressionSyntax value => value.IdentifierToken,
        MemberAccessExpressionSyntax value => value.MemberToken,
        CallExpressionSyntax value => GetReferenceToken(value.Target),
        ParenthesizedExpressionSyntax value => GetReferenceToken(value.Expression),
        _ => null,
    };

    private static bool TryGetDottedName(ExpressionSyntax syntax, out string[] parts)
    {
        var result = new List<string>();
        bool success = Append(syntax);
        parts = success ? result.ToArray() : [];
        return success;

        bool Append(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case NameExpressionSyntax name when !name.IdentifierToken.IsMissing:
                    result.Add(name.IdentifierToken.Text);
                    return true;
                case MemberAccessExpressionSyntax member when member.OperatorToken.Kind == SyntaxKind.DotToken &&
                    !member.MemberToken.IsMissing && Append(member.Receiver):
                    result.Add(member.MemberToken.Text);
                    return true;
                default:
                    return false;
            }
        }
    }

    private static bool TryGetDeclaredType(TypeSymbol type, out DeclaredTypeSymbol declared)
    {
        while (true)
        {
            switch (type)
            {
                case PointerTypeSymbol pointer: type = pointer.ElementType; continue;
                case ReferenceTypeSymbol reference: type = reference.ElementType; continue;
                case ArrayTypeSymbol array: type = array.ElementType; continue;
                case StructTypeSymbol { GenericDefinition: { } definition }:
                    declared = definition;
                    return true;
                case DeclaredTypeSymbol result: declared = result; return true;
                default: declared = null!; return false;
            }
        }
    }

    private sealed class ResolvedReferenceIdentityComparer :
        IEqualityComparer<(Symbol Symbol, SourceFileId File, TextSpan Span)>
    {
        public static ResolvedReferenceIdentityComparer Instance { get; } = new();

        public bool Equals((Symbol Symbol, SourceFileId File, TextSpan Span) x,
            (Symbol Symbol, SourceFileId File, TextSpan Span) y) =>
            ReferenceEquals(x.Symbol, y.Symbol) && x.File == y.File && x.Span == y.Span;

        public int GetHashCode((Symbol Symbol, SourceFileId File, TextSpan Span) value) =>
            HashCode.Combine(RuntimeHelpers.GetHashCode(value.Symbol), value.File, value.Span);
    }
}
