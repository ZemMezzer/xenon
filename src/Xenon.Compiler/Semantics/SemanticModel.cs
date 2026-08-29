using System.Collections.Immutable;
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
        return _semanticInfo.Declarations.TryGetValue(syntax, out Symbol? declared)
            ? SymbolInfo.FromSymbol(declared) : SymbolInfo.None;
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

    public ImmutableArray<Symbol> LookupSymbols(int position, CancellationToken cancellationToken = default) =>
        LookupSymbols(GetPrimarySource(), position, cancellationToken);

    public ImmutableArray<Symbol> LookupSymbols(SyntaxTree tree, int position,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tree);
        EnsureTree(tree);
        return LookupSymbols(tree.Source, position, cancellationToken);
    }

    private ImmutableArray<Symbol> LookupSymbols(SourceText source, int position, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, source.Length);
        cancellationToken.ThrowIfCancellationRequested();
        PositionScope[] scopes = _semanticInfo.Scopes
            .Where(scope => ReferenceEquals(scope.Source, source) && Contains(scope.Span, position, scope.IncludeEnd))
            .OrderBy(scope => scope.Span.Length).ToArray();
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
        _ => false,
    };

    private static bool IsAccessible(Symbol symbol, DeclaredTypeSymbol? withinType) => symbol switch
    {
        FieldSymbol field => field.IsPublic || ReferenceEquals(field.ContainingType, withinType),
        FunctionSymbol function => function.IsPublic || ReferenceEquals(function.ContainingType, withinType),
        PropertySymbol property => property.IsPublic || ReferenceEquals(property.ContainingType, withinType),
        IndexerSymbol indexer => indexer.IsPublic || ReferenceEquals(indexer.ContainingType, withinType),
        _ => true,
    };

    private SourceText GetPrimarySource()
    {
        if (_primaryTree is not null) return _primaryTree.Source;
        if (_syntaxTrees.Length == 1) return _syntaxTrees[0].Source;
        throw new InvalidOperationException("Use Compilation.GetSemanticModel(tree) or LookupSymbols(tree, position) for a multi-file compilation.");
    }

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
}
