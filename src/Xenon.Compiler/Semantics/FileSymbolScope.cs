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

    public FileSymbolScope(NamespaceSymbol globalNamespace, NamespaceSymbol containingNamespace)
    {
        GlobalNamespace = globalNamespace;
        ContainingNamespace = containingNamespace;
    }

    public NamespaceSymbol GlobalNamespace { get; }

    public NamespaceSymbol ContainingNamespace { get; }

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
                            : $"unknown namespace '{directive.Name}'");
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
                    $"using alias '{alias}' is already declared in this file");
                continue;
            }

            if (namespaceTarget is null && typeTarget is null)
            {
                diagnostics.Report(
                    directive.NameParts[0].Location,
                    $"unknown namespace or type '{directive.Name}'");
                continue;
            }

            if (namespaceTarget is not null && typeTarget is not null)
            {
                diagnostics.Report(
                    directive.NameParts[0].Location,
                    $"using alias target '{directive.Name}' is ambiguous between a namespace and a type");
                continue;
            }

            _aliases.Add(alias, new UsingAliasTarget(namespaceTarget, typeTarget));
        }
    }

    public TypeSymbol? ResolveType(string name, TextLocation location, DiagnosticBag diagnostics)
    {
        if (_aliases.TryGetValue(name, out UsingAliasTarget? alias) && alias is { Type: not null })
        {
            return alias.Type;
        }

        TypeSymbol? local = ContainingNamespace.FindAnyType(name);
        if (local is not null)
        {
            return local;
        }

        List<TypeSymbol> matches = _importedNamespaces
            .Select(@namespace => @namespace.FindAnyType(name))
            .Where(type => type is not null)
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
                $"type name '{name}' is ambiguous between {FormatTypeCandidates(matches)}");
            return BuiltinTypes.Error;
        }

        return null;
    }

    public FunctionSymbol? ResolveFunction(
        string name,
        TextLocation location,
        DiagnosticBag diagnostics,
        out bool diagnosticReported)
    {
        diagnosticReported = false;
        FunctionSymbol? local = ContainingNamespace.FindFunction(name);
        if (local is not null)
        {
            return local;
        }

        var publicMatches = new List<FunctionSymbol>();
        var privateMatches = new List<FunctionSymbol>();
        foreach (NamespaceSymbol imported in _importedNamespaces)
        {
            FunctionSymbol? function = imported.FindFunction(name);
            if (function is null)
            {
                continue;
            }

            if (function.IsPublic)
            {
                publicMatches.Add(function);
            }
            else
            {
                privateMatches.Add(function);
            }
        }


        if (publicMatches.Count == 1)
        {
            return publicMatches[0];
        }

        if (publicMatches.Count > 1)
        {
            diagnostics.Report(
                location,
                $"function name '{name}' is ambiguous between {FormatFunctionCandidates(publicMatches)}");
            diagnosticReported = true;
            return null;
        }


        if (privateMatches.Count == 1)
        {
            FunctionSymbol inaccessible = privateMatches[0];
            diagnostics.Report(
                location,
                $"function '{inaccessible.Name}' is private in namespace '{inaccessible.ContainingNamespace.FullName}'");
            diagnosticReported = true;
        }
        else if (privateMatches.Count > 1)
        {
            diagnostics.Report(
                location,
                $"function name '{name}' refers only to private functions in imported namespaces");
            diagnosticReported = true;
        }

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

            return ContainingNamespace.FindAnyType(parts[0]);
        }

        NamespaceSymbol? containingNamespace = ResolveNamespacePrefix(parts, parts.Count - 1);
        return containingNamespace?.FindAnyType(parts[^1]);
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
        FunctionSymbol? function = containingNamespace?.FindFunction(parts[^1]);
        if (function is null)
        {
            return null;
        }

        if (!ReferenceEquals(containingNamespace, ContainingNamespace) && !function.IsPublic)
        {
            diagnostics.Report(
                location,
                $"function '{function.Name}' is private in namespace '{containingNamespace!.FullName}'");
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
            types.Select(type => $"'{GetFullTypeName(type)}'"));

    private static string GetFullTypeName(TypeSymbol type) => type switch
    {
        StructTypeSymbol @struct => @struct.FullName,
        InterfaceTypeSymbol @interface => @interface.FullName,
        _ => type.Name,
    };

    private static string FormatFunctionCandidates(IEnumerable<FunctionSymbol> functions) =>
        string.Join(
            " and ",
            functions.Select(function => $"'{function.FullName}'"));

    private sealed record UsingAliasTarget(NamespaceSymbol? Namespace, TypeSymbol? Type);
}
