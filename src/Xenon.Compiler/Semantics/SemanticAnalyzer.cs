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
                TypeSymbol returnType = TypeResolver.Resolve(declaration.ReturnType, _diagnostics);
                ImmutableArray<ParameterSymbol> parameters = BindParameters(declaration);
                var function = new FunctionSymbol(
                    declaration.IdentifierToken.Text,
                    @namespace,
                    returnType,
                    parameters,
                    declaration);

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

    private ImmutableArray<ParameterSymbol> BindParameters(FunctionDeclarationSyntax declaration)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < declaration.Parameters.Length; index++)
        {
            ParameterSyntax syntax = declaration.Parameters[index];
            TypeSymbol type = TypeResolver.Resolve(syntax.Type, _diagnostics);

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
