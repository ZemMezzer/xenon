using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics;

internal sealed class SemanticAnalyzer
{
    private readonly ImmutableArray<SyntaxTree> _syntaxTrees;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly NamespaceSymbol _globalNamespace = new(string.Empty, null);
    private readonly Dictionary<SyntaxTree, NamespaceSymbol> _treeNamespaces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionDeclarationSyntax, FunctionSymbol> _functionSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructDeclarationSyntax, StructTypeSymbol> _structSymbols = new(ReferenceEqualityComparer.Instance);

    private SemanticAnalyzer(ImmutableArray<SyntaxTree> syntaxTrees)
    {
        _syntaxTrees = syntaxTrees;
    }

    public static SemanticModel Analyze(ImmutableArray<SyntaxTree> syntaxTrees)
    {
        var analyzer = new SemanticAnalyzer(syntaxTrees);
        return analyzer.Analyze();
    }

    private SemanticModel Analyze()
    {
        DeclareNamespaces();
        DeclareStructs();
        BindStructFields();
        ValidateStructLayouts();
        DeclareFunctions();

        var functions = ImmutableArray.CreateBuilder<BoundFunction>();
        foreach ((FunctionDeclarationSyntax declaration, FunctionSymbol symbol) in _functionSymbols)
        {
            if (declaration.Body is null)
            {
                continue;
            }

            var binder = new FunctionBodyBinder(symbol, _diagnostics);
            functions.Add(new BoundFunction(symbol, binder.BindBody(declaration.Body)));
        }

        return new SemanticModel(_globalNamespace, functions.ToImmutable(), [.. _diagnostics]);
    }

    private void DeclareStructs()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            foreach (StructDeclarationSyntax declaration in tree.Root.Members.OfType<StructDeclarationSyntax>())
            {
                var type = new StructTypeSymbol(declaration.IdentifierToken.Text, @namespace, declaration);
                if (!@namespace.TryDeclareType(type))
                {
                    _diagnostics.Report(
                        declaration.IdentifierToken.Location,
                        $"type '{@namespace.FullName}.{type.Name}' is already declared");
                    continue;
                }

                _structSymbols.Add(declaration, type);
            }
        }
    }

    private void BindStructFields()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < declaration.Fields.Length; index++)
            {
                FieldDeclarationSyntax fieldSyntax = declaration.Fields[index];
                TypeSymbol fieldType = TypeResolver.Resolve(
                    fieldSyntax.Type,
                    type.ContainingNamespace,
                    _diagnostics);
                if (ReferenceEquals(fieldType, BuiltinTypes.Void))
                {
                    _diagnostics.Report(fieldSyntax.Type.NameToken.Location, "field type cannot be 'void'");
                }

                if (!names.Add(fieldSyntax.IdentifierToken.Text))
                {
                    _diagnostics.Report(
                        fieldSyntax.IdentifierToken.Location,
                        $"field '{fieldSyntax.IdentifierToken.Text}' is already declared in struct '{type.Name}'");
                }

                fields.Add(new FieldSymbol(
                    fieldSyntax.IdentifierToken.Text,
                    type,
                    fieldType,
                    index,
                    fieldSyntax));
            }

            type.SetFields(fields.ToImmutable());
        }
    }

    private void ValidateStructLayouts()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        {
            foreach (FieldSymbol field in type.Fields)
            {
                if (field.Type is StructTypeSymbol fieldStruct && ContainsByValue(fieldStruct, type, []))
                {
                    _diagnostics.Report(
                        field.Declaration.Type.NameToken.Location,
                        $"struct '{type.Name}' has a recursive by-value field '{field.Name}'; use a pointer instead");
                }
            }
        }
    }

    private static bool ContainsByValue(
        StructTypeSymbol candidate,
        StructTypeSymbol target,
        HashSet<StructTypeSymbol> visited)
    {
        if (ReferenceEquals(candidate, target))
        {
            return true;
        }

        if (!visited.Add(candidate))
        {
            return false;
        }

        return candidate.Fields.Any(field =>
            field.Type is StructTypeSymbol nested && ContainsByValue(nested, target, visited));
    }

    private void DeclareNamespaces()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol current = _globalNamespace;
            foreach (SyntaxToken part in tree.Root.Namespace.NameParts)
            {
                current = current.GetOrAddNamespace(part.Text);
            }

            _treeNamespaces.Add(tree, current);
        }
    }

    private void DeclareFunctions()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            foreach (FunctionDeclarationSyntax declaration in tree.Root.Members.OfType<FunctionDeclarationSyntax>())
            {
                if (declaration.IsExtern && declaration.IdentifierToken.Text == "malloc")
                {
                    _diagnostics.Report(
                        declaration.IdentifierToken.Location,
                        "native symbol 'malloc' is reserved for the built-in 'new' operation");
                }

                TypeSymbol returnType = TypeResolver.Resolve(declaration.ReturnType, @namespace, _diagnostics);
                ImmutableArray<ParameterSymbol> parameters = BindParameters(declaration, @namespace);
                var function = new FunctionSymbol(
                    declaration.IdentifierToken.Text,
                    @namespace,
                    returnType,
                    parameters,
                    declaration);

                ValidateExternalStructAbi(declaration, function);

                if (!@namespace.TryDeclareFunction(function))
                {
                    _diagnostics.Report(
                        declaration.IdentifierToken.Location,
                        $"function '{@namespace.FullName}.{function.Name}' is already declared");
                    continue;
                }

                _functionSymbols.Add(declaration, function);
            }
        }
    }

    private void ValidateExternalStructAbi(
        FunctionDeclarationSyntax declaration,
        FunctionSymbol function)
    {
        if (!declaration.IsExtern && !declaration.IsExport)
        {
            return;
        }

        if (function.ReturnType is StructTypeSymbol returnStruct)
        {
            _diagnostics.Report(
                declaration.ReturnType.NameToken.Location,
                $"external ABI does not yet support struct '{returnStruct.Name}' by value; use a pointer instead");
        }

        for (int index = 0; index < function.Parameters.Length; index++)
        {
            if (function.Parameters[index].Type is StructTypeSymbol parameterStruct)
            {
                _diagnostics.Report(
                    declaration.Parameters[index].Type.NameToken.Location,
                    $"external ABI does not yet support struct '{parameterStruct.Name}' by value; use a pointer instead");
            }
        }
    }

    private ImmutableArray<ParameterSymbol> BindParameters(
        FunctionDeclarationSyntax declaration,
        NamespaceSymbol containingNamespace)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < declaration.Parameters.Length; index++)
        {
            ParameterSyntax syntax = declaration.Parameters[index];
            TypeSymbol type = TypeResolver.Resolve(syntax.Type, containingNamespace, _diagnostics);

            if (ReferenceEquals(type, BuiltinTypes.Void))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, "parameter type cannot be 'void'");
            }

            if (!names.Add(syntax.IdentifierToken.Text))
            {
                _diagnostics.Report(
                    syntax.IdentifierToken.Location,
                    $"parameter '{syntax.IdentifierToken.Text}' is already declared");
            }

            parameters.Add(new ParameterSymbol(syntax.IdentifierToken.Text, type, index));
        }

        return parameters.ToImmutable();
    }
}
