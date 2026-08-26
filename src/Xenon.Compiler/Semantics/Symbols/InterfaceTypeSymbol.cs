using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public sealed class InterfaceTypeSymbol : TypeSymbol
{
    private ImmutableArray<FunctionSymbol> _methods = [];
    private Dictionary<FunctionSymbol, int> _methodSlots = [];

    internal InterfaceTypeSymbol(string name, NamespaceSymbol containingNamespace, InterfaceDeclarationSyntax declaration)
        : base(name)
    {
        ContainingNamespace = containingNamespace;
        Declaration = declaration;
    }

    public NamespaceSymbol ContainingNamespace { get; }
    public string FullName => $"{ContainingNamespace.FullName}.{Name}";
    public ImmutableArray<InterfaceTypeSymbol> BaseInterfaces { get; private set; } = [];
    public ImmutableArray<FunctionSymbol> Methods => _methods;
    public int DispatchId { get; private set; }
    internal InterfaceDeclarationSyntax Declaration { get; }

    internal void SetBaseInterfaces(ImmutableArray<InterfaceTypeSymbol> interfaces) => BaseInterfaces = interfaces;
    internal void SetMethods(ImmutableArray<FunctionSymbol> methods) => _methods = methods;
    internal void SetDispatchId(int dispatchId) => DispatchId = dispatchId;

    internal void SetMethodSlots(IEnumerable<FunctionSymbol> methods) =>
        _methodSlots = methods.Select((method, slot) => (method, slot)).ToDictionary(pair => pair.method, pair => pair.slot);

    public ImmutableArray<FunctionSymbol> AllMethods
    {
        get
        {
            var seen = new HashSet<FunctionSymbol>(ReferenceEqualityComparer.Instance);
            return BaseInterfaces.SelectMany(@interface => @interface.AllMethods)
                .Concat(_methods)
                .Where(seen.Add)
                .ToImmutableArray();
        }
    }

    public int GetMethodSlot(FunctionSymbol method) => _methodSlots.TryGetValue(method, out int slot)
        ? slot
        : throw new InvalidOperationException($"method '{method.Name}' does not belong to interface '{FullName}'");
    public FunctionSymbol? FindMethod(string name) => _methods.FirstOrDefault(m => m.Name == name) ?? BaseInterfaces.Select(i => i.FindMethod(name)).FirstOrDefault(m => m is not null);

    public bool IsOrInherits(InterfaceTypeSymbol target) =>
        ReferenceEquals(this, target) || BaseInterfaces.Any(@interface => @interface.IsOrInherits(target));

    public IEnumerable<InterfaceTypeSymbol> SelfAndBaseInterfaces =>
        BaseInterfaces.SelectMany(@interface => @interface.SelfAndBaseInterfaces).Append(this).Distinct();
}
