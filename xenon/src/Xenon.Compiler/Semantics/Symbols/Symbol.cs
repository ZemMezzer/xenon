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
        Origin = SymbolOrigin.CompilerGenerated;
        Documentation = SymbolDocumentation.Empty;
    }

    public virtual string Name { get; }

    public SymbolKind Kind { get; }

    public Symbol? ContainingSymbol { get; private set; }

    public SymbolOrigin Origin { get; private set; }

    public SymbolDocumentation Documentation { get; private set; }

    public SymbolImplementation? Implementation { get; private set; }

    protected void SetContainingSymbol(Symbol containingSymbol) => ContainingSymbol = containingSymbol;

    protected void SetSourceOrigin(SyntaxNode declaration, bool includeDocumentation = true)
    {
        Origin = SymbolOrigin.Source(declaration);
        Implementation = new SourceSymbolImplementation(declaration);
        if (includeDocumentation) Documentation = SymbolDocumentation.FromDeclaration(declaration);
    }

    internal void SetMetadata(SymbolOrigin origin, SymbolDocumentation? documentation = null)
    {
        Origin = origin;
        Documentation = documentation ?? SymbolDocumentation.Empty;
    }

    protected void SetImplementation(SymbolImplementation? implementation) => Implementation = implementation;

    /// <summary>Source-layer escape hatch for binding/diagnostics; semantic code must not require it.</summary>
    internal T GetSourceDeclaration<T>() where T : SyntaxNode =>
        Origin.Kind == SymbolOriginKind.Source && Origin.SyntaxReferences.FirstOrDefault()?.Declaration is T declaration
            ? declaration
            : throw new InvalidOperationException($"symbol '{QualifiedName}' has no source declaration");

    /// <summary>Explicit declarations only; built-ins and synthesized symbols have no references.</summary>
    public virtual ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => Origin.SyntaxReferences;

    /// <summary>Name/keyword locations, using the same coordinates as compiler diagnostics.</summary>
    public ImmutableArray<TextLocation> Locations =>
        DeclaringSyntaxReferences.Select(reference => reference.Location).ToImmutableArray();

    public bool IsSourceDefined => !DeclaringSyntaxReferences.IsEmpty;

    /// <summary>Whether an editor should offer this symbol in ordinary user-facing discovery.</summary>
    public virtual bool IsUserVisible => true;

    /// <summary>True for implementation artifacts synthesized by the compiler.</summary>
    public virtual bool IsCompilerGenerated => false;

    /// <summary>Whether this source declaration supplies the symbol's implementation/storage definition.</summary>
    public virtual bool IsDefinition => false;

    /// <summary>Whether the declaration/reference location denotes an identifier a user may edit.</summary>
    public virtual bool HasUserEditableIdentifier => IsSourceDefined;

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
