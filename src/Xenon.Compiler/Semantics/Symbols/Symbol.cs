namespace Xenon.Compiler.Semantics.Symbols;

public abstract class Symbol
{
    protected Symbol(string name, SymbolKind kind)
    {
        Name = name;
        Kind = kind;
    }

    public string Name { get; }

    public SymbolKind Kind { get; }
}
