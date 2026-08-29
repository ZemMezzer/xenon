using System.Collections.Immutable;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics.Symbols;

public abstract class Symbol
{
    protected Symbol(string name, SymbolKind kind, Symbol? containingSymbol = null)
    {
        Name = name;
        Kind = kind;
        ContainingSymbol = containingSymbol;
    }

    public virtual string Name { get; }

    public SymbolKind Kind { get; }

    public Symbol? ContainingSymbol { get; }

    /// <summary>Explicit declarations only; built-ins and synthesized symbols have no references.</summary>
    public virtual ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => [];

    /// <summary>Name/keyword locations, using the same coordinates as compiler diagnostics.</summary>
    public ImmutableArray<TextLocation> Locations =>
        DeclaringSyntaxReferences.Select(reference => reference.Location).ToImmutableArray();

    public bool IsSourceDefined => !DeclaringSyntaxReferences.IsEmpty;

    public virtual string ToDisplayString(SymbolDisplayFormat format) => SymbolDisplay.ToDisplayString(this, format);

    public string QualifiedName
    {
        get
        {
            var parts = new Stack<string>();
            for (Symbol? symbol = this; symbol is not null; symbol = symbol.ContainingSymbol)
                if (!string.IsNullOrEmpty(symbol.Name)) parts.Push(symbol.Name);
            return string.Join('.', parts);
        }
    }

    public T? GetContainingSymbol<T>() where T : Symbol
    {
        for (Symbol? owner = ContainingSymbol; owner is not null; owner = owner.ContainingSymbol)
            if (owner is T typed) return typed;
        return null;
    }
}

public sealed class ErrorSymbol : Symbol
{
    public ErrorSymbol(string name = "<error>") : base(name, SymbolKind.Error) { }
}

public sealed class AliasSymbol : Symbol
{
    internal AliasSymbol(string name, Symbol target, SyntaxNode declaration)
        : base(name, SymbolKind.Alias, target.ContainingSymbol)
    {
        Target = target;
        Declaration = declaration;
    }

    public Symbol Target { get; }
    private SyntaxNode Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => [new(Declaration)];
}
