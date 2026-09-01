using System.Collections.Immutable;
using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<ArrayTypeSymbol, ImmutableArray<SyntheticMemberSymbol>> _arrayMembers =
        new(ReferenceEqualityComparer.Instance);

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
