using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed class SemanticInfoStore
{
    public Dictionary<SyntaxNode, Symbol> Declarations { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<SyntaxNode, SymbolInfo> Symbols { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<SyntaxNode, TypeInfo> Types { get; } = new(ReferenceEqualityComparer.Instance);
    public Dictionary<ExpressionSyntax, ReceiverInfo> Receivers { get; } = new(ReferenceEqualityComparer.Instance);
    public List<PositionScope> Scopes { get; } = [];
    public List<TypeRegion> TypeRegions { get; } = [];
    public Dictionary<SourceText, FileSymbolScope> FileScopes { get; } = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ArrayTypeSymbol, ImmutableArray<SyntheticMemberSymbol>> _arrayMembers = new(ReferenceEqualityComparer.Instance);

    public ImmutableArray<SyntheticMemberSymbol> GetArrayMembers(ArrayTypeSymbol array)
    {
        if (_arrayMembers.TryGetValue(array, out ImmutableArray<SyntheticMemberSymbol> members)) return members;
        members =
        [
            new SyntheticMemberSymbol("Length", SyntheticMemberKind.Property, array, BuiltinTypes.Int),
            new SyntheticMemberSymbol("Rank", SyntheticMemberKind.Property, array, BuiltinTypes.Int),
            new SyntheticMemberSymbol("GetLength", SyntheticMemberKind.Method, array, BuiltinTypes.Int,
                [new ParameterSymbol("dimension", BuiltinTypes.Int, 0)]),
        ];
        _arrayMembers.Add(array, members);
        return members;
    }

    public void RecordType(TypeSyntax syntax, TypeSymbol type)
    {
        Types[syntax] = new TypeInfo(type, type);
        switch (syntax)
        {
            case PointerTypeSyntax pointer when type is PointerTypeSymbol pointerType:
                RecordType(pointer.ElementType, pointerType.ElementType);
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
