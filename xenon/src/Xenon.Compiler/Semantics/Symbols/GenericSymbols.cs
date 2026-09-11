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
        : this(kind, target, new SymbolOrigin(SymbolOriginKind.Source, []))
    {
        Declaration = declaration!;
    }

    internal GenericConstraintSymbol(GenericConstraintKind kind, Symbol target, SymbolOrigin? origin = null)
    {
        Kind = kind;
        Target = target;
        Origin = origin ?? SymbolOrigin.CompilerGenerated;
    }

    public GenericConstraintKind Kind { get; }
    public Symbol Target { get; }
    public SymbolOrigin Origin { get; }
    internal GenericConstraintSyntax Declaration { get; } = null!;
}

public sealed class GenericParameterSymbol : TypeSymbol
{
    private ImmutableArray<GenericConstraintSymbol> _constraints = [];

    internal GenericParameterSymbol(string name, int ordinal, Symbol containingSymbol, GenericParameterSyntax declaration)
        : this(name, ordinal, containingSymbol, SymbolOrigin.Source(declaration))
    {
        Declaration = declaration;
        SetImplementation(new SourceSymbolImplementation(declaration));
    }

    internal GenericParameterSymbol(string name, int ordinal, Symbol containingSymbol,
        SymbolOrigin? origin = null, SymbolDocumentation? documentation = null)
        : base(name, containingSymbol)
    {
        Ordinal = ordinal;
        SetMetadata(origin ?? SymbolOrigin.CompilerGenerated, documentation);
    }

    public int Ordinal { get; }
    internal GenericParameterSyntax Declaration { get; } = null!;
    public ImmutableArray<GenericConstraintSymbol> Constraints => _constraints;
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Origin.Kind != SymbolOriginKind.Source ? base.DeclaringSyntaxReferences : [new(Declaration)];
    public override bool IsDefinition => true;

    internal void SetConstraints(ImmutableArray<GenericConstraintSymbol> constraints) => _constraints = constraints;
    internal void SetDeclaringSymbol(Symbol symbol)
    {
        SetContainingSymbol(symbol);
        if (symbol.Documentation.TypeParameters.TryGetValue(Name, out string? text))
            SetMetadata(Origin, SymbolDocumentation.Empty with { Summary = text });
    }
}

public sealed class TemplateSymbol : Symbol
{
    private ImmutableArray<TemplateMemberRequirementSymbol> _members = [];

    internal TemplateSymbol(string name, NamespaceSymbol containingNamespace, TemplateDeclarationSyntax declaration)
        : this(name, containingNamespace, SymbolOrigin.Source(declaration),
            SymbolDocumentation.FromDeclaration(declaration),
            AccessibilityFacts.FromSyntax(declaration.AccessModifierToken,
                declaration.SecondaryAccessModifierToken, Accessibility.Public))
    {
        Declaration = declaration;
        SetImplementation(new SourceSymbolImplementation(declaration));
    }

    internal TemplateSymbol(string name, NamespaceSymbol containingNamespace,
        SymbolOrigin? origin = null, SymbolDocumentation? documentation = null,
        Accessibility accessibility = Accessibility.Public)
        : base(name, SymbolKind.Template, containingNamespace)
    {
        SelfType = new TemplateSelfTypeSymbol(this);
        Accessibility = accessibility;
        SetMetadata(origin ?? SymbolOrigin.CompilerGenerated, documentation);
    }

    public NamespaceSymbol ContainingNamespace => GetContainingSymbol<NamespaceSymbol>()!;
    public Accessibility Accessibility { get; }
    public bool IsPublic => Accessibility == Accessibility.Public;
    internal TemplateDeclarationSyntax Declaration { get; } = null!;
    public ImmutableArray<TemplateMemberRequirementSymbol> Members => _members;
    internal TemplateSelfTypeSymbol SelfType { get; }
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Origin.Kind != SymbolOriginKind.Source ? base.DeclaringSyntaxReferences : [new(Declaration)];
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
        Accessibility accessibility, bool isStatic, bool isReadonly, SyntaxNode? declaration,
        SymbolOrigin? origin = null, SymbolDocumentation? documentation = null)
        : base(name, kind, template)
    {
        Accessibility = accessibility;
        IsStatic = isStatic;
        IsReadonly = isReadonly;
        Declaration = declaration!;
        SetMetadata(origin ?? (declaration is null ? SymbolOrigin.CompilerGenerated : SymbolOrigin.Source(declaration)),
            documentation ?? (declaration is null ? null : SymbolDocumentation.FromDeclaration(declaration)));
    }

    public TemplateSymbol Template => (TemplateSymbol)ContainingSymbol!;
    public Accessibility Accessibility { get; }
    public bool IsPublic => Accessibility == Accessibility.Public;
    public bool IsStatic { get; }
    public bool IsReadonly { get; }
    internal SyntaxNode Declaration { get; } = null!;
    public override ImmutableArray<SyntaxReference> DeclaringSyntaxReferences =>
        Origin.Kind != SymbolOriginKind.Source ? base.DeclaringSyntaxReferences : [new(Declaration)];
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

    internal TemplateMethodRequirementSymbol(string name, TemplateSymbol template, TypeSymbol returnType,
        ImmutableArray<ParameterSymbol> parameters, Accessibility accessibility, bool isStatic, bool isReadonly,
        SymbolOrigin? origin = null, SymbolDocumentation? documentation = null)
        : base(name, SymbolKind.Function, template, accessibility, isStatic, isReadonly, null, origin, documentation)
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

    internal TemplateConstructorRequirementSymbol(TemplateSymbol template, ImmutableArray<ParameterSymbol> parameters,
        Accessibility accessibility, SymbolOrigin? origin = null, SymbolDocumentation? documentation = null)
        : base(template.Name, SymbolKind.Function, template, accessibility, false, false, null, origin, documentation) =>
        Parameters = ParameterSymbol.Own(parameters, this);

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

    internal TemplatePropertyRequirementSymbol(string name, TemplateSymbol template, TypeSymbol type,
        Accessibility accessibility, bool isStatic, bool isReadonly, bool hasGetter, bool hasSetter,
        SymbolOrigin? origin = null, SymbolDocumentation? documentation = null)
        : base(name, SymbolKind.Property, template, accessibility, isStatic, isReadonly, null, origin, documentation)
    {
        Type = type;
        HasGetter = hasGetter;
        HasSetter = hasSetter;
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

    internal TemplateIndexerRequirementSymbol(TemplateSymbol template, TypeSymbol type,
        ImmutableArray<ParameterSymbol> parameters, Accessibility accessibility, bool isStatic, bool isReadonly,
        bool hasGetter, bool hasSetter, SymbolOrigin? origin = null, SymbolDocumentation? documentation = null)
        : base("this", SymbolKind.Property, template, accessibility, isStatic, isReadonly, null, origin, documentation)
    {
        Type = type;
        Parameters = ParameterSymbol.Own(parameters, this);
        HasGetter = hasGetter;
        HasSetter = hasSetter;
    }

    public TypeSymbol Type { get; }
    public ImmutableArray<ParameterSymbol> Parameters { get; }
    public bool HasGetter { get; }
    public bool HasSetter { get; }
    public override bool HasUserEditableIdentifier => false;
}
