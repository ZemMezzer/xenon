using System.Collections.Immutable;
using System.Collections.Concurrent;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed class SemanticInfoStore
{
    internal sealed record Snapshot(
        HashSet<SyntaxNode> DeclarationKeys,
        HashSet<SyntaxNode> SymbolKeys,
        HashSet<SyntaxNode> ConversionKeys,
        HashSet<SyntaxNode> TypeKeys,
        HashSet<ExpressionSyntax> ReceiverKeys,
        int LambdaFunctionCount,
        int ExplicitReferenceCount,
        int ScopeCount,
        int TypeRegionCount);

    internal sealed record Delta(
        ImmutableArray<SyntaxNode> DeclarationKeys,
        ImmutableArray<SyntaxNode> SymbolKeys,
        ImmutableArray<SyntaxNode> ConversionKeys,
        ImmutableArray<SyntaxNode> TypeKeys,
        ImmutableArray<ExpressionSyntax> ReceiverKeys,
        ImmutableArray<Xenon.Compiler.Semantics.Binding.BoundFunction> LambdaFunctions,
        ImmutableArray<ResolvedSymbolReference> ExplicitReferences,
        ImmutableArray<PositionScope> Scopes,
        ImmutableArray<TypeRegion> TypeRegions);

    // Anonymous bodies belong to the same binding pass as their editor information.
    // Speculative summary passes use a separate store and never enter the emitted set.
    public List<Xenon.Compiler.Semantics.Binding.BoundFunction> LambdaFunctions { get; } = [];
    public Dictionary<SyntaxNode, Symbol> Declarations { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<SyntaxNode, SymbolInfo> Symbols { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<SyntaxNode, SymbolInfo> Conversions { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<SyntaxNode, TypeInfo> Types { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<ExpressionSyntax, ReceiverInfo> Receivers { get; } = new(ReferenceEqualityComparer.Instance);
    public List<ResolvedSymbolReference> ExplicitReferences { get; } = [];
    public List<PositionScope> Scopes { get; } = [];
    public List<TypeRegion> TypeRegions { get; } = [];
    public Dictionary<SourceText, FileSymbolScope> FileScopes { get; } = new(ReferenceEqualityComparer.Instance);
    private readonly ConcurrentDictionary<ArrayTypeSymbol, ImmutableArray<SyntheticMemberSymbol>> _arrayMembers =
        new(ReferenceEqualityComparer.Instance);

    internal Snapshot CaptureSnapshot() => new(
        new HashSet<SyntaxNode>(Declarations.Keys, ReferenceEqualityComparer.Instance),
        new HashSet<SyntaxNode>(Symbols.Keys, ReferenceEqualityComparer.Instance),
        new HashSet<SyntaxNode>(Conversions.Keys, ReferenceEqualityComparer.Instance),
        new HashSet<SyntaxNode>(Types.Keys, ReferenceEqualityComparer.Instance),
        new HashSet<ExpressionSyntax>(Receivers.Keys, ReferenceEqualityComparer.Instance),
        LambdaFunctions.Count,
        ExplicitReferences.Count,
        Scopes.Count,
        TypeRegions.Count);

    internal Delta CaptureDelta(Snapshot before) => new(
        Declarations.Keys.Where(key => !before.DeclarationKeys.Contains(key)).ToImmutableArray(),
        Symbols.Keys.Where(key => !before.SymbolKeys.Contains(key)).ToImmutableArray(),
        Conversions.Keys.Where(key => !before.ConversionKeys.Contains(key)).ToImmutableArray(),
        Types.Keys.Where(key => !before.TypeKeys.Contains(key)).ToImmutableArray(),
        Receivers.Keys.Where(key => !before.ReceiverKeys.Contains(key)).ToImmutableArray(),
        LambdaFunctions.Skip(before.LambdaFunctionCount).ToImmutableArray(),
        ExplicitReferences.Skip(before.ExplicitReferenceCount).ToImmutableArray(),
        Scopes.Skip(before.ScopeCount).ToImmutableArray(),
        TypeRegions.Skip(before.TypeRegionCount).ToImmutableArray());

    internal void Rollback(Delta delta)
    {
        foreach (SyntaxNode key in delta.DeclarationKeys) Declarations.Remove(key);
        foreach (SyntaxNode key in delta.SymbolKeys) Symbols.Remove(key);
        foreach (SyntaxNode key in delta.ConversionKeys) Conversions.Remove(key);
        foreach (SyntaxNode key in delta.TypeKeys) Types.Remove(key);
        foreach (ExpressionSyntax key in delta.ReceiverKeys) Receivers.Remove(key);
        foreach (var function in delta.LambdaFunctions)
            LambdaFunctions.RemoveAll(candidate => ReferenceEquals(candidate, function));
        foreach (ResolvedSymbolReference reference in delta.ExplicitReferences)
        {
            int index = ExplicitReferences.FindLastIndex(candidate => candidate.Equals(reference));
            if (index >= 0) ExplicitReferences.RemoveAt(index);
        }
        foreach (PositionScope scope in delta.Scopes)
            Scopes.RemoveAll(candidate => ReferenceEquals(candidate, scope));
        foreach (TypeRegion region in delta.TypeRegions)
            TypeRegions.RemoveAll(candidate => ReferenceEquals(candidate, region));
    }

    internal void RollbackSyntax(SyntaxNode root)
    {
        var nodes = new HashSet<SyntaxNode>(
            SyntaxNavigator.DescendantNodesAndSelf(root), ReferenceEqualityComparer.Instance);
        foreach (SyntaxNode key in Declarations.Keys.Where(nodes.Contains).ToArray()) Declarations.Remove(key);
        foreach (SyntaxNode key in Symbols.Keys.Where(nodes.Contains).ToArray()) Symbols.Remove(key);
        foreach (SyntaxNode key in Conversions.Keys.Where(nodes.Contains).ToArray()) Conversions.Remove(key);
        foreach (SyntaxNode key in Types.Keys.Where(nodes.Contains).ToArray()) Types.Remove(key);
        foreach (ExpressionSyntax key in Receivers.Keys.Where(nodes.Contains).ToArray()) Receivers.Remove(key);

        TextSpan span = SyntaxNavigator.GetSpan(root);
        SourceText source = SyntaxNavigator.GetTokens(root).First().Location.Source;
        bool IsInside(TextLocation location) => ReferenceEquals(location.Source, source) &&
            location.Span.Start >= span.Start && location.Span.End <= span.End;
        ExplicitReferences.RemoveAll(reference => IsInside(reference.Location));
        Scopes.RemoveAll(scope => ReferenceEquals(scope.Source, source) &&
            scope.Span.Start >= span.Start && scope.Span.End <= span.End);
        TypeRegions.RemoveAll(region => ReferenceEquals(region.Source, source) &&
            region.Span.Start >= span.Start && region.Span.End <= span.End);
    }

    public ImmutableArray<SyntheticMemberSymbol> GetArrayMembers(ArrayTypeSymbol array)
    {
        return _arrayMembers.GetOrAdd(array, static value =>
        [
            new SyntheticMemberSymbol("Length", SyntheticMemberKind.Property, value, BuiltinTypes.Int),
            new SyntheticMemberSymbol("Rank", SyntheticMemberKind.Property, value, BuiltinTypes.Int),
            new SyntheticMemberSymbol("GetLength", SyntheticMemberKind.Method, value, BuiltinTypes.Int,
                [new ParameterSymbol("dimension", BuiltinTypes.Int, 0)]),
        ]);
    }

    public void RecordType(TypeSyntax syntax, TypeSymbol type)
    {
        Types[syntax] = new TypeInfo(type, type);
        if (syntax is NamedTypeSyntax && type is GenericParameterSymbol parameter)
            Symbols[syntax] = SymbolInfo.FromSymbol(parameter);
        else if (syntax is NamedTypeSyntax && type is TemplateSelfTypeSymbol selfType)
            Symbols[syntax] = SymbolInfo.FromSymbol(selfType.Template);
        switch (syntax)
        {
            case PointerTypeSyntax pointer when type is PointerTypeSymbol pointerType:
                RecordType(pointer.ElementType, pointerType.ElementType);
                break;
            case FunctionPointerTypeSyntax function when type is FunctionPointerTypeSymbol functionType:
                RecordType(function.ReturnType, functionType.ReturnType);
                foreach ((TypeSyntax parameterSyntax, TypeSymbol parameterType) in function.ParameterTypes.Zip(functionType.ParameterTypes))
                    RecordType(parameterSyntax, parameterType);
                break;
            case FunctionValueTypeSyntax function when type is FunctionValueTypeSymbol functionType:
                RecordType(function.ReturnType, functionType.ReturnType);
                foreach ((TypeSyntax parameterSyntax, TypeSymbol parameterType) in function.ParameterTypes.Zip(functionType.ParameterTypes))
                    RecordType(parameterSyntax, parameterType);
                break;
            case ReferenceTypeSyntax reference when type is ReferenceTypeSymbol referenceType:
                RecordType(reference.ElementType, referenceType.ElementType);
                break;
            case ArrayTypeSyntax array when type is ArrayTypeSymbol arrayType:
                RecordType(array.ElementType, arrayType.ElementType);
                break;
            case QualifiedTypeSyntax qualified:
                RecordType(qualified.ElementType, type);
                break;
            case NamedTypeSyntax { NameToken.Kind: SyntaxKind.UniqueKeyword or SyntaxKind.SharedKeyword or SyntaxKind.WeakKeyword, TypeArguments: { } arguments }
                when type is OwnershipTypeSymbol ownership && arguments.Arguments.Length == 1:
                Types[arguments] = new TypeInfo(BuiltinTypes.Error, BuiltinTypes.Error);
                RecordType(arguments.Arguments[0], ownership.ElementType);
                break;
            case NamedTypeSyntax { NameToken.Kind: SyntaxKind.AtomicKeyword, TypeArguments: { } arguments }
                when type is AtomicTypeSymbol atomic && arguments.Arguments.Length == 1:
                Types[arguments] = new TypeInfo(BuiltinTypes.Error, BuiltinTypes.Error);
                RecordType(arguments.Arguments[0], atomic.ElementType);
                break;
            case NamedTypeSyntax { NameToken.Kind: SyntaxKind.StorageKeyword or SyntaxKind.PinKeyword, TypeArguments: { } arguments }
                when type is LifetimeModifierTypeSymbol modifier && arguments.Arguments.Length == 1:
                Types[arguments] = new TypeInfo(BuiltinTypes.Error, BuiltinTypes.Error);
                RecordType(arguments.Arguments[0], modifier.ElementType);
                break;
            case NamedTypeSyntax { TypeArguments: { } arguments }:
                Types[arguments] = new TypeInfo(BuiltinTypes.Error, BuiltinTypes.Error);
                foreach (TypeSyntax argument in arguments.Arguments)
                    RecordType(argument, BuiltinTypes.Error);
                break;
        }
    }
}

internal sealed record PositionScope(
    SourceText Source,
    TextSpan Span,
    FunctionSymbol Function,
    IReadOnlyList<VariableSymbol> Variables,
    bool IncludeEnd = false);

internal sealed record TypeRegion(SourceText Source, TextSpan Span, DeclaredTypeSymbol Type, bool IncludeEnd = false);
