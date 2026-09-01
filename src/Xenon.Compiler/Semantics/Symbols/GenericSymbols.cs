using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

public enum GenericConstraintKind
{
    BaseStruct,
    Interface,
    StructuralTemplate,
}

public sealed class GenericConstraintSymbol
{
    internal GenericConstraintSymbol(GenericConstraintKind kind, Symbol target, GenericConstraintSyntax declaration)
    {
        Kind = kind;
        Target = target;
        Declaration = declaration;
    }

    public GenericConstraintKind Kind { get; }
    public Symbol Target { get; }
    public GenericConstraintSyntax Declaration { get; }
}

public sealed class GenericParameterSymbol : TypeSymbol
{
    private ImmutableArray<GenericConstraintSymbol> _constraints = [];

    internal GenericParameterSymbol(string name, int ordinal, Symbol containingSymbol, GenericParameterSyntax declaration)
        : base(name, containingSymbol)
    {
        Ordinal = ordinal;
        Declaration = declaration;
    }

    public int Ordinal { get; }
    public GenericParameterSyntax Declaration { get; }
    public ImmutableArray<GenericConstraintSymbol> Constraints => _constraints;
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => [new(Declaration)];
    public override bool IsDefinition => true;

    internal void SetConstraints(ImmutableArray<GenericConstraintSymbol> constraints) => _constraints = constraints;
    internal void SetDeclaringSymbol(Symbol symbol) => SetContainingSymbol(symbol);
}

public sealed class TemplateSymbol : Symbol
{
    private ImmutableArray<TemplateMemberRequirementSymbol> _members = [];

    internal TemplateSymbol(string name, NamespaceSymbol containingNamespace, TemplateDeclarationSyntax declaration)
        : base(name, SymbolKind.Template, containingNamespace)
    {
        Declaration = declaration;
        SelfType = new TemplateSelfTypeSymbol(this);
    }

    public NamespaceSymbol ContainingNamespace => GetContainingSymbol<NamespaceSymbol>()!;
    public TemplateDeclarationSyntax Declaration { get; }
    public ImmutableArray<TemplateMemberRequirementSymbol> Members => _members;
    internal TemplateSelfTypeSymbol SelfType { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => [new(Declaration)];
    public override bool IsDefinition => true;

    internal void SetMembers(ImmutableArray<TemplateMemberRequirementSymbol> members) => _members = members;
}

internal sealed class TemplateSelfTypeSymbol : TypeSymbol
{
    public TemplateSelfTypeSymbol(TemplateSymbol template) : base(template.Name, template)
    {
        Template = template;
    }

    public TemplateSymbol Template { get; }
}

public abstract class TemplateMemberRequirementSymbol : Symbol
{
    protected TemplateMemberRequirementSymbol(string name, SymbolKind kind, TemplateSymbol template,
        Accessibility accessibility, bool isStatic, bool isReadonly, SyntaxNode declaration)
        : base(name, kind, template)
    {
        Accessibility = accessibility;
        IsStatic = isStatic;
        IsReadonly = isReadonly;
        Declaration = declaration;
    }

    public TemplateSymbol Template => (TemplateSymbol)ContainingSymbol!;
    public Accessibility Accessibility { get; }
    public bool IsPublic => Accessibility == Accessibility.Public;
    public bool IsStatic { get; }
    public bool IsReadonly { get; }
    public SyntaxNode Declaration { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences => [new(Declaration)];
}

public sealed class TemplateMethodRequirementSymbol : TemplateMemberRequirementSymbol
{
    internal TemplateMethodRequirementSymbol(TemplateSymbol template, TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters, Accessibility accessibility, MethodDeclarationSyntax declaration)
        : base(declaration.IdentifierToken.Text, SymbolKind.Function, template, accessibility,
            declaration.IsStatic, declaration.IsReadonly, declaration)
    {
        ReturnType = returnType;
        Parameters = ParameterSymbol.Own(parameters, this);
    }

    public TypeSymbol ReturnType { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; }
}

public sealed class TemplateConstructorRequirementSymbol : TemplateMemberRequirementSymbol
{
    internal TemplateConstructorRequirementSymbol(TemplateSymbol template, ImmutableArray<ParameterSymbol> parameters,
        Accessibility accessibility, TemplateConstructorDeclarationSyntax declaration)
        : base(template.Name, SymbolKind.Function, template, accessibility, false, false, declaration)
    {
        Parameters = ParameterSymbol.Own(parameters, this);
    }

    public ImmutableArray<ParameterSymbol> Parameters { get; }
}

public sealed class TemplatePropertyRequirementSymbol : TemplateMemberRequirementSymbol
{
    internal TemplatePropertyRequirementSymbol(TemplateSymbol template, TypeSymbol type,
        Accessibility accessibility, PropertyDeclarationSyntax declaration)
        : base(declaration.IdentifierToken.Text, SymbolKind.Property, template, accessibility,
            declaration.IsStatic, declaration.IsReadonly, declaration)
    {
        Type = type;
        HasGetter = declaration.Getter is not null;
        HasSetter = declaration.Setter is not null;
    }

    public TypeSymbol Type { get; }
    public bool HasGetter { get; }
    public bool HasSetter { get; }
}

public sealed class TemplateIndexerRequirementSymbol : TemplateMemberRequirementSymbol
{
    internal TemplateIndexerRequirementSymbol(TemplateSymbol template, TypeSymbol type,
        ImmutableArray<ParameterSymbol> parameters, Accessibility accessibility, IndexerDeclarationSyntax declaration)
        : base("this", SymbolKind.Property, template, accessibility, declaration.IsStatic,
            declaration.IsReadonly, declaration)
    {
        Type = type;
        Parameters = ParameterSymbol.Own(parameters, this);
        HasGetter = declaration.Getter is not null;
        HasSetter = declaration.Setter is not null;
    }

    public TypeSymbol Type { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; }
    public bool HasGetter { get; }
    public bool HasSetter { get; }
    public override bool HasUserEditableIdentifier => false;
}
