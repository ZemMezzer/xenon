using System.Collections.Immutable;

namespace Xenon.Compiler.Semantics.Symbols;

public enum SyntheticMemberKind
{
    Property,
    Method,
}

/// <summary>A compiler-owned member with no fabricated source declaration.</summary>
public sealed class SyntheticMemberSymbol : Symbol
{
    internal SyntheticMemberSymbol(string name, SyntheticMemberKind memberKind, TypeSymbol containingType,
        TypeSymbol type, ImmutableArray<ParameterSymbol> parameters = default,
        bool isStatic = false, bool isReadonly = true)
        : base(name, memberKind == SyntheticMemberKind.Method ? SymbolKind.Function : SymbolKind.Property, containingType)
    {
        MemberKind = memberKind;
        Type = type;
        Parameters = parameters.IsDefault ? [] : parameters;
        IsStatic = isStatic;
        IsReadonly = isReadonly;
    }

    public SyntheticMemberKind MemberKind { get; }
    public TypeSymbol ContainingType => (TypeSymbol)ContainingSymbol!;
    public TypeSymbol Type { get; }
    public TypeSymbol ReturnType => Type;
    public ImmutableArray<ParameterSymbol> Parameters { get; }
    public bool IsStatic { get; }
    public bool IsReadonly { get; }
}
