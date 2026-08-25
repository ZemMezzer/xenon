using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics.Binding;

internal sealed class BoundScope
{
    private readonly Dictionary<string, VariableSymbol> _variables = new(StringComparer.Ordinal);

    public BoundScope(BoundScope? parent)
    {
        Parent = parent;
    }

    public BoundScope? Parent { get; }

    public bool TryDeclare(VariableSymbol variable) => _variables.TryAdd(variable.Name, variable);

    public VariableSymbol? Lookup(string name)
    {
        for (BoundScope? scope = this; scope is not null; scope = scope.Parent)
        {
            if (scope._variables.TryGetValue(name, out VariableSymbol? variable))
            {
                return variable;
            }
        }

        return null;
    }
}
