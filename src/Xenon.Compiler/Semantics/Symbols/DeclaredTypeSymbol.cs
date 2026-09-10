using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>A nominal type. Source syntax, when present, is optional origin metadata.</summary>
public abstract class DeclaredTypeSymbol : TypeSymbol
{
    protected DeclaredTypeSymbol(string name, NamespaceSymbol containingNamespace,
        string declarationKind, bool isDefinition = true, SymbolOrigin? origin = null,
        SymbolDocumentation? documentation = null, Accessibility accessibility = Accessibility.Public)
        : base(name, containingNamespace)
    {
        DeclarationKind = declarationKind;
        IsSemanticDefinition = isDefinition;
        Accessibility = accessibility;
        SetMetadata(origin ?? SymbolOrigin.CompilerGenerated, documentation);
    }

    public NamespaceSymbol ContainingNamespace => GetContainingSymbol<NamespaceSymbol>()!;
    public string FullName => QualifiedName;
    public override string ToDisplayString(TypeDisplayFormat format = TypeDisplayFormat.Short) =>
        format == TypeDisplayFormat.FullyQualified ? FullName : Name;
    public string DeclarationKind { get; }
    /// <summary>The number of generic parameters in this nominal declaration.</summary>
    public virtual int GenericArity => 0;
    public Accessibility Accessibility { get; }
    public bool IsPublic => Accessibility == Accessibility.Public;
    public override bool IsDefinition => IsSemanticDefinition;
    private bool IsSemanticDefinition { get; }
    public abstract IEnumerable<Symbol> GetMembers();

    /// <summary>Visible members, including inherited declarations where the type's semantics permit them.</summary>
    public virtual IEnumerable<Symbol> LookupMembers(string name) => GetMembers().Where(member => member.Name == name);

    public FieldSymbol? FindStaticField(string name) =>
        GetMembers().OfType<FieldSymbol>().FirstOrDefault(field => field.IsStatic && field.Name == name);

    public FieldSymbol? FindInstanceField(string name) =>
        LookupMembers(name).OfType<FieldSymbol>().FirstOrDefault(field => !field.IsStatic);

    public IEnumerable<FunctionSymbol> LookupMethods(string name) =>
        LookupMembers(name).OfType<FunctionSymbol>().Where(method => method.FunctionKind == FunctionKind.Method);

    public T? FindMember<T>(string name) where T : Symbol => LookupMembers(name).OfType<T>().FirstOrDefault();
}
