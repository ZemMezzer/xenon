using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

/// <summary>
/// File-local name-resolution context. A using directive only affects the file
/// that declares it; it never changes namespace contents or compilation inputs.
/// </summary>
internal sealed class FileSymbolScope
{
    private readonly List<NamespaceSymbol> _importedNamespaces = [];
    private readonly Dictionary<string, UsingAliasTarget> _aliases = new(StringComparer.Ordinal);
    private readonly List<AliasSymbol> _aliasSymbols = [];
    private readonly Dictionary<string, TypeSymbol> _localTypes = new(StringComparer.Ordinal);

    public FileSymbolScope(NamespaceSymbol globalNamespace, NamespaceSymbol containingNamespace, TypeFactory typeFactory,
        SemanticInfoStore? semanticInfo = null)
    {
        GlobalNamespace = globalNamespace;
        TypeFactory = typeFactory;
        ContainingNamespace = containingNamespace;
        SemanticInfo = semanticInfo;
    }

    public NamespaceSymbol GlobalNamespace { get; }

    public TypeFactory TypeFactory { get; }

    public NamespaceSymbol ContainingNamespace { get; }

    internal SemanticInfoStore? SemanticInfo { get; }
    internal GenericStructSpecializer? GenericStructSpecializer { get; private set; }

    internal void SetGenericStructSpecializer(GenericStructSpecializer specializer) =>
        GenericStructSpecializer = specializer;

    internal IEnumerable<NamespaceSymbol> ImportedNamespaces => _importedNamespaces;

    internal IEnumerable<Symbol> GetFileSymbols() =>
        _localTypes.Values.Cast<Symbol>()
            .Concat(GlobalNamespace.Namespaces)
            .Concat(ContainingNamespace.Namespaces)
            .Concat(ContainingNamespace.Types)
            .Concat(ContainingNamespace.Templates)
            .Concat(ContainingNamespace.Functions)
            .Concat(ContainingNamespace.Constants)
            .Concat(_importedNamespaces.SelectMany(ns => ns.Namespaces.Cast<Symbol>()
                .Concat(ns.Types).Concat(ns.Templates).Concat(ns.Functions.Where(function => function.IsPublic)).Concat(ns.Constants)))
            .Concat(_aliasSymbols)
            .Distinct();

    internal NamespaceSymbol? ResolveNamespaceForTooling(IReadOnlyList<string> parts) =>
        ResolveNamespacePath(parts);

    internal TypeSymbol? ResolveTypeForTooling(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1) return ResolveTypePath(parts);
        if (_localTypes.TryGetValue(parts[0], out TypeSymbol? localType)) return localType;
        if (_aliases.TryGetValue(parts[0], out UsingAliasTarget? alias) && alias?.Type is not null)
            return alias.Type;
        DeclaredTypeSymbol[] candidates = ContainingNamespace.FindTypes(parts[0])
            .Concat(_importedNamespaces.SelectMany(@namespace => @namespace.FindTypes(parts[0])))
            .Distinct().ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal IEnumerable<Symbol> GetNamespaceSymbolsForTooling(NamespaceSymbol @namespace) =>
        @namespace.Namespaces.Cast<Symbol>()
            .Concat(@namespace.Types)
            .Concat(@namespace.Templates)
            .Concat(@namespace.Functions.Where(function =>
                ReferenceEquals(@namespace, ContainingNamespace) || function.IsPublic))
            .Concat(@namespace.Constants)
            .Where(symbol => symbol.IsUserVisible)
            .Distinct();

    public void BindUsings(ImmutableArray<UsingDirectiveSyntax> directives, DiagnosticBag diagnostics)
    {
        foreach (UsingDirectiveSyntax directive in directives)
        {
            if (directive.NameParts.Any(part => part.IsMissing))
            {
                continue;
            }

            string[] parts = directive.NameParts.Select(part => part.Text).ToArray();
            NamespaceSymbol? namespaceTarget = ResolveNamespacePath(parts);
            TypeSymbol? typeTarget = ResolveTypePath(parts);

            if (!directive.HasAlias)
            {
                if (namespaceTarget is null)
                {
                    diagnostics.Report(
                        directive.NameParts[0].Location,
                        typeTarget is not null
                            ? $"using directive '{directive.Name}' names a type; use an alias such as 'using Name = {directive.Name};'"
                            : $"unknown namespace '{directive.Name}'",
                        typeTarget is not null ? DiagnosticIds.UsingDirectiveTargetsType : DiagnosticIds.UnknownNamespace);
                    continue;
                }

                if (!_importedNamespaces.Contains(namespaceTarget, ReferenceEqualityComparer.Instance))
                {
                    _importedNamespaces.Add(namespaceTarget);
                }

                continue;
            }

            string alias = directive.AliasToken!.Text;
            if (_aliases.ContainsKey(alias))
            {
                diagnostics.Report(
                    directive.AliasToken.Location,
                    $"using alias '{alias}' is already declared in this file",
                    DiagnosticIds.DuplicateDeclaration);
                continue;
            }

            if (namespaceTarget is null && typeTarget is null)
            {
                diagnostics.Report(
                    directive.NameParts[0].Location,
                    $"unknown namespace or type '{directive.Name}'",
                    DiagnosticIds.UnknownNamespaceOrType);
                continue;
            }

            if (namespaceTarget is not null && typeTarget is not null)
            {
                diagnostics.Report(
                    directive.NameParts[0].Location,
                    $"using alias target '{directive.Name}' is ambiguous between a namespace and a type",
                    DiagnosticIds.AmbiguousName);
                continue;
            }

            _aliases.Add(alias, new UsingAliasTarget(namespaceTarget, typeTarget));
            Symbol target = (Symbol?)typeTarget ?? namespaceTarget!;
            _aliasSymbols.Add(new AliasSymbol(alias, target, directive));
            SemanticInfo?.Declarations[directive] = _aliasSymbols[^1];
        }
    }

    public TypeSymbol? ResolveType(string name, TextLocation location, DiagnosticBag diagnostics)
    {
        if (_localTypes.TryGetValue(name, out TypeSymbol? localType))
        {
            return localType;
        }

        if (_aliases.TryGetValue(name, out UsingAliasTarget? alias) && alias is { Type: not null })
        {
            return alias.Type;
        }

        IReadOnlyList<DeclaredTypeSymbol> local = ContainingNamespace.FindTypes(name);
        if (local.Count == 1)
        {
            return local[0];
        }
        if (local.Count > 1)
        {
            diagnostics.Report(location, $"type name '{name}' is ambiguous between {FormatTypeCandidates(local)}",
                DiagnosticIds.AmbiguousName);
            return BuiltinTypes.Error;
        }

        List<TypeSymbol> matches = _importedNamespaces
            .SelectMany(@namespace => @namespace.FindTypes(name))
            .Cast<TypeSymbol>()
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0];
        }

        if (matches.Count > 1)
        {
            diagnostics.Report(
                location,
                $"type name '{name}' is ambiguous between {FormatTypeCandidates(matches)}",
                DiagnosticIds.AmbiguousName);
            return BuiltinTypes.Error;
        }

        return null;
    }

    public TemplateSymbol? ResolveTemplate(string name, TextLocation location, DiagnosticBag diagnostics)
    {
        IReadOnlyList<TemplateSymbol> local = ContainingNamespace.FindTemplates(name);
        if (local.Count == 1) return local[0];
        if (local.Count > 1)
        {
            diagnostics.Report(location, $"template name '{name}' is ambiguous", DiagnosticIds.AmbiguousName);
            return null;
        }

        TemplateSymbol[] matches = _importedNamespaces
            .SelectMany(@namespace => @namespace.FindTemplates(name))
            .Distinct()
            .ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length > 1)
            diagnostics.Report(location, $"template name '{name}' is ambiguous between imported namespaces",
                DiagnosticIds.AmbiguousName);
        return null;
    }

    public TemplateSymbol? ResolveQualifiedTemplate(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0) return null;
        if (parts.Count == 1) return ContainingNamespace.FindTemplate(parts[0]);
        NamespaceSymbol? containingNamespace = ResolveNamespacePrefix(parts, parts.Count - 1);
        IReadOnlyList<TemplateSymbol>? candidates = containingNamespace?.FindTemplates(parts[^1]);
        return candidates?.Count == 1 ? candidates[0] : null;
    }

    public Symbol? ResolveConstraintTarget(IReadOnlyList<string> parts, TextLocation location,
        DiagnosticBag diagnostics)
    {
        if (parts.Count == 0) return null;
        if (parts.Count == 1)
        {
            string name = parts[0];
            if (_localTypes.TryGetValue(name, out TypeSymbol? localType)) return localType;
            if (_aliases.TryGetValue(name, out UsingAliasTarget? alias) && alias.Type is not null) return alias.Type;

            Symbol[] local = ContainingNamespace.FindTypes(name).Cast<Symbol>()
                .Concat(ContainingNamespace.FindTemplates(name)).ToArray();
            if (local.Length == 1) return local[0];
            if (local.Length > 1)
            {
                diagnostics.Report(location, $"constraint name '{name}' is ambiguous", DiagnosticIds.AmbiguousName);
                return null;
            }

            Symbol[] imported = _importedNamespaces.SelectMany(@namespace =>
                @namespace.FindTypes(name).Cast<Symbol>().Concat(@namespace.FindTemplates(name)))
                .Distinct().ToArray();
            if (imported.Length == 1) return imported[0];
            if (imported.Length > 1)
                diagnostics.Report(location, $"constraint name '{name}' is ambiguous between imported namespaces",
                    DiagnosticIds.AmbiguousName);
            return null;
        }

        NamespaceSymbol? containingNamespace = ResolveNamespacePrefix(parts, parts.Count - 1);
        if (containingNamespace is null) return null;
        Symbol[] qualified = containingNamespace.FindTypes(parts[^1]).Cast<Symbol>()
            .Concat(containingNamespace.FindTemplates(parts[^1])).ToArray();
        if (qualified.Length == 1) return qualified[0];
        if (qualified.Length > 1)
            diagnostics.Report(location, $"constraint name '{string.Join('.', parts)}' is ambiguous",
                DiagnosticIds.AmbiguousName);
        return null;
    }

    public FileSymbolScope WithTypeParameters(IEnumerable<GenericParameterSymbol> parameters)
    {
        var scope = new FileSymbolScope(GlobalNamespace, ContainingNamespace, TypeFactory, SemanticInfo);
        scope.GenericStructSpecializer = GenericStructSpecializer;
        scope._importedNamespaces.AddRange(_importedNamespaces);
        foreach ((string name, UsingAliasTarget target) in _aliases) scope._aliases.Add(name, target);
        scope._aliasSymbols.AddRange(_aliasSymbols);
        foreach ((string name, TypeSymbol localType) in _localTypes)
            scope._localTypes.Add(name, localType);
        foreach (GenericParameterSymbol parameter in parameters)
            scope._localTypes.TryAdd(parameter.Name, parameter);
        return scope;
    }

    public FileSymbolScope WithTemplateSelf(TemplateSymbol template)
    {
        FileSymbolScope scope = WithTypeParameters([]);
        scope._localTypes.TryAdd(template.Name, template.SelfType);
        return scope;
    }

    public FileSymbolScope WithTypeSubstitutions(
        IEnumerable<KeyValuePair<GenericParameterSymbol, TypeSymbol>> substitutions,
        SemanticInfoStore? semanticInfo = null)
    {
        var scope = new FileSymbolScope(GlobalNamespace, ContainingNamespace, TypeFactory,
            semanticInfo ?? SemanticInfo);
        scope.GenericStructSpecializer = GenericStructSpecializer;
        scope._importedNamespaces.AddRange(_importedNamespaces);
        foreach ((string name, UsingAliasTarget target) in _aliases) scope._aliases.Add(name, target);
        scope._aliasSymbols.AddRange(_aliasSymbols);
        foreach ((string name, TypeSymbol localType) in _localTypes)
            scope._localTypes.Add(name, localType);
        foreach ((GenericParameterSymbol parameter, TypeSymbol type) in substitutions)
            scope._localTypes[parameter.Name] = TypeFactory.Intern(type);
        return scope;
    }

    public FunctionSymbol? ResolveFunction(
        string name,
        TextLocation location,
        DiagnosticBag diagnostics,
        out bool diagnosticReported)
    {
        diagnosticReported = false;
        IReadOnlyList<FunctionSymbol> local = ContainingNamespace.FindFunctions(name);
        if (local.Count == 1)
        {
            return local[0];
        }
        if (local.Count > 1)
        {
            diagnostics.Report(location, $"function name '{name}' is ambiguous between {FormatFunctionCandidates(local)}",
                DiagnosticIds.AmbiguousName);
            diagnosticReported = true;
            return null;
        }

        var publicMatches = new List<FunctionSymbol>();
        var privateMatches = new List<FunctionSymbol>();
        foreach (NamespaceSymbol imported in _importedNamespaces)
        {
            foreach (FunctionSymbol function in imported.FindFunctions(name))
                if (function.IsPublic) publicMatches.Add(function); else privateMatches.Add(function);
        }


        if (publicMatches.Count == 1)
        {
            return publicMatches[0];
        }

        if (publicMatches.Count > 1)
        {
            diagnostics.Report(
                location,
                $"function name '{name}' is ambiguous between {FormatFunctionCandidates(publicMatches)}",
                DiagnosticIds.AmbiguousName);
            diagnosticReported = true;
            return null;
        }


        if (privateMatches.Count == 1)
        {
            FunctionSymbol inaccessible = privateMatches[0];
            diagnostics.Report(
                location,
                $"function '{inaccessible.Name}' is private in namespace '{inaccessible.ContainingNamespace.FullName}'",
                DiagnosticIds.InaccessibleSymbol);
            diagnosticReported = true;
        }
        else if (privateMatches.Count > 1)
        {
            diagnostics.Report(
                location,
                $"function name '{name}' refers only to private functions in imported namespaces",
                DiagnosticIds.InaccessibleSymbol);
            diagnosticReported = true;
        }

        return null;
    }

    public ConstantSymbol? ResolveConstant(string name, TextLocation location, DiagnosticBag diagnostics)
    {
        IReadOnlyList<ConstantSymbol> local = ContainingNamespace.FindConstants(name);
        if (local.Count == 1) return local[0];
        if (local.Count > 1)
        {
            diagnostics.Report(location, $"constant name '{name}' is ambiguous", DiagnosticIds.AmbiguousName);
            return null;
        }

        ConstantSymbol[] matches = _importedNamespaces
            .SelectMany(@namespace => @namespace.FindConstants(name))
            .ToArray();
        if (matches.Length == 1)
            return matches[0];
        if (matches.Length > 1)
            diagnostics.Report(location, $"constant name '{name}' is ambiguous between imported namespaces",
                DiagnosticIds.AmbiguousName);
        return null;
    }

    public TypeSymbol? ResolveQualifiedType(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts.Count == 1)
        {
            if (_aliases.TryGetValue(parts[0], out UsingAliasTarget? alias) && alias is { Type: not null })
            {
                return alias.Type;
            }

            IReadOnlyList<DeclaredTypeSymbol> candidates = ContainingNamespace.FindTypes(parts[0]);
            return candidates.Count == 1 ? candidates[0] : null;
        }

        NamespaceSymbol? containingNamespace = ResolveNamespacePrefix(parts, parts.Count - 1);
        IReadOnlyList<DeclaredTypeSymbol>? qualifiedCandidates = containingNamespace?.FindTypes(parts[^1]);
        return qualifiedCandidates?.Count == 1 ? qualifiedCandidates[0] : null;
    }

    public FunctionSymbol? ResolveQualifiedFunction(
        IReadOnlyList<string> parts,
        TextLocation location,
        DiagnosticBag diagnostics,
        out bool diagnosticReported)
    {
        diagnosticReported = false;
        if (parts.Count < 2)
        {
            return null;
        }

        NamespaceSymbol? containingNamespace = ResolveNamespacePrefix(parts, parts.Count - 1);
        IReadOnlyList<FunctionSymbol>? candidates = containingNamespace?.FindFunctions(parts[^1]);
        if (candidates is null || candidates.Count == 0)
        {
            return null;
        }
        if (candidates.Count > 1)
        {
            diagnostics.Report(location,
                $"function name '{string.Join('.', parts)}' is ambiguous between {FormatFunctionCandidates(candidates)}",
                DiagnosticIds.AmbiguousName);
            diagnosticReported = true;
            return null;
        }
        FunctionSymbol function = candidates[0];

        if (!ReferenceEquals(containingNamespace, ContainingNamespace) && !function.IsPublic)
        {
            diagnostics.Report(
                location,
                $"function '{function.Name}' is private in namespace '{containingNamespace!.FullName}'",
                DiagnosticIds.InaccessibleSymbol);
            diagnosticReported = true;
            return null;
        }

        return function;
    }

    public bool CanStartQualifiedName(string name) =>
        (_aliases.TryGetValue(name, out UsingAliasTarget? target) && target is { Namespace: not null }) ||
        GlobalNamespace.FindNamespace(name) is not null;

    public NamespaceSymbol? ResolveNamespaceAlias(string alias) =>
        _aliases.TryGetValue(alias, out UsingAliasTarget? target) && target is not null ? target.Namespace : null;

    private NamespaceSymbol? ResolveNamespacePrefix(IReadOnlyList<string> parts, int count)
    {
        if (count <= 0)
        {
            return null;
        }

        int index = 0;
        NamespaceSymbol? current;
        if (_aliases.TryGetValue(parts[0], out UsingAliasTarget? alias) && alias is { Namespace: not null })
        {
            current = alias.Namespace;
            index = 1;
        }
        else
        {
            current = GlobalNamespace.FindNamespace(parts[0]);
            index = 1;
        }

        while (current is not null && index < count)
        {
            current = current.FindNamespace(parts[index]);
            index++;
        }

        return current;
    }

    private NamespaceSymbol? ResolveNamespacePath(IReadOnlyList<string> parts) =>
        ResolveNamespacePrefix(parts, parts.Count);

    private TypeSymbol? ResolveTypePath(IReadOnlyList<string> parts)
    {
        if (parts.Count == 0)
        {
            return null;
        }

        if (parts.Count == 1)
        {
            return GlobalNamespace.FindAnyType(parts[0]) ?? ContainingNamespace.FindAnyType(parts[0]);
        }

        NamespaceSymbol? @namespace = ResolveNamespacePrefix(parts, parts.Count - 1);
        return @namespace?.FindAnyType(parts[^1]);
    }

    private static string FormatTypeCandidates(IEnumerable<TypeSymbol> types) =>
        string.Join(
            " and ",
            types.Select(type => $"'{type.ToDisplayString(TypeDisplayFormat.FullyQualified)}'"));

    private static string FormatFunctionCandidates(IEnumerable<FunctionSymbol> functions) =>
        string.Join(
            " and ",
            functions.Select(function => $"'{function.ToDisplayString(SymbolDisplayFormat.QualifiedName)}'"));

    private sealed record UsingAliasTarget(NamespaceSymbol? Namespace, TypeSymbol? Type);
}
