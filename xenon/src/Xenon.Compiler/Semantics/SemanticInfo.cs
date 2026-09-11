using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

public enum CandidateReason
{
    None,
    NotFound,
    Ambiguous,
    Inaccessible,
    Incomplete,
    WrongArity,
    NotInvocable,
}

public readonly record struct SymbolInfo(
    Symbol? Symbol,
    ImmutableArray<Symbol> CandidateSymbols,
    CandidateReason CandidateReason)
{
    public static SymbolInfo None { get; } = new(null, [], CandidateReason.NotFound);

    public static SymbolInfo FromSymbol(Symbol symbol) => new(symbol, [], CandidateReason.None);
}

public readonly record struct TypeInfo(TypeSymbol Type, TypeSymbol ConvertedType)
{
    public bool IsError => Type is ErrorTypeSymbol || ConvertedType is ErrorTypeSymbol;
}

public readonly record struct ReceiverInfo(
    TypeSymbol Type,
    bool IsStatic,
    bool IsReadonly,
    bool IsWritable);

/// <summary>The tooling-relevant role of one successfully resolved source reference.</summary>
public enum ResolvedReferenceKind
{
    Reference,
    Type,
    Call,
    Member,
}

/// <summary>A source occurrence that the semantic snapshot resolved to an exact symbol.</summary>
public readonly record struct ResolvedSymbolReference(
    Symbol Symbol,
    Xenon.Compiler.Text.TextLocation Location,
    ResolvedReferenceKind Kind);

public enum MemberAccessKind
{
    Instance,
    Static,
    Any,
}

public readonly record struct MemberLookupOptions(
    MemberAccessKind AccessKind = MemberAccessKind.Instance,
    bool IncludeInaccessible = false,
    bool IsReadonlyReceiver = false);

public enum CompletionReceiverKind
{
    None,
    Namespace,
    Type,
    Value,
    Ambiguous,
}

/// <summary>Compiler-resolved semantic category of a completion receiver.</summary>
public readonly record struct CompletionReceiverInfo(
    CompletionReceiverKind Kind,
    NamespaceSymbol? Namespace = null,
    TypeSymbol? Type = null);
