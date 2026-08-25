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
    private readonly List<(FunctionSymbol Symbol, BlockStatementSyntax Body)> _functionBodies = [];

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
        DeclareStructMethods();
        DeclareStructLifecycleFunctions();
        DeclareFunctions();

        var functions = ImmutableArray.CreateBuilder<BoundFunction>();
        foreach ((FunctionSymbol symbol, BlockStatementSyntax body) in _functionBodies)
        {
            var binder = new FunctionBodyBinder(symbol, _diagnostics);
            functions.Add(new BoundFunction(symbol, binder.BindBody(body)));
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
            foreach (FieldDeclarationSyntax fieldSyntax in declaration.Fields)
            {
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
                    fields.Count,
                    fieldSyntax.IsPublic ? Accessibility.Public : Accessibility.Private,
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
                if (ContainsStructByValue(field.Type, type, []))
                {
                    _diagnostics.Report(
                        field.Declaration.Type.NameToken.Location,
                        $"struct '{type.Name}' has a recursive by-value field '{field.Name}'; use a pointer or array handle instead");
                }
            }
        }
    }

    private static bool ContainsStructByValue(
        TypeSymbol candidate,
        StructTypeSymbol target,
        HashSet<StructTypeSymbol> visited)
    {
        if (candidate is not StructTypeSymbol structType)
        {
            return false;
        }

        if (ReferenceEquals(structType, target))
        {
            return true;
        }

        if (!visited.Add(structType))
        {
            return false;
        }

        return structType.Fields.Any(field => ContainsStructByValue(field.Type, target, visited));
    }

    private void DeclareStructMethods()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            var methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (MethodDeclarationSyntax methodSyntax in declaration.Methods)
            {
                if (!names.Add(methodSyntax.IdentifierToken.Text))
                {
                    _diagnostics.Report(
                        methodSyntax.IdentifierToken.Location,
                        $"method overloading is not supported yet; struct '{type.Name}' may declare only one method named '{methodSyntax.IdentifierToken.Text}'");
                    continue;
                }

                if (type.FindField(methodSyntax.IdentifierToken.Text) is not null)
                {
                    _diagnostics.Report(
                        methodSyntax.IdentifierToken.Location,
                        $"struct '{type.Name}' already contains field '{methodSyntax.IdentifierToken.Text}'");
                }

                TypeSymbol returnType = TypeResolver.Resolve(
                    methodSyntax.ReturnType,
                    type.ContainingNamespace,
                    _diagnostics);
                ImmutableArray<ParameterSymbol> parameters = BindParameters(
                    methodSyntax.Parameters,
                    type.ContainingNamespace);

                var method = new FunctionSymbol(
                    methodSyntax.IdentifierToken.Text,
                    type,
                    returnType,
                    parameters,
                    methodSyntax);

                methods.Add(method);
                _functionBodies.Add((method, methodSyntax.Body));
            }

            type.SetMethods(methods.ToImmutable());
        }
    }

    private void DeclareStructLifecycleFunctions()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            if (declaration.Constructors.Length > 1)
            {
                foreach (ConstructorDeclarationSyntax duplicate in declaration.Constructors.Skip(1))
                {
                    _diagnostics.Report(
                        duplicate.IdentifierToken.Location,
                        $"constructor overloading is not supported yet; struct '{type.Name}' may declare only one constructor");
                }
            }

            ConstructorDeclarationSyntax? constructorSyntax = declaration.Constructors.FirstOrDefault();
            if (constructorSyntax is not null)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(
                    constructorSyntax.Parameters,
                    type.ContainingNamespace);
                var constructor = new FunctionSymbol(
                    FunctionKind.Constructor,
                    type,
                    parameters,
                    constructorSyntax,
                    constructorSyntax.IsPublic ? Accessibility.Public : Accessibility.Private);
                type.SetConstructor(constructor);
                _functionBodies.Add((constructor, constructorSyntax.Body));
            }

            DestructorDeclarationSyntax[] destructors = declaration.Members
                .OfType<DestructorDeclarationSyntax>()
                .ToArray();
            if (destructors.Length > 1)
            {
                foreach (DestructorDeclarationSyntax duplicate in destructors.Skip(1))
                {
                    _diagnostics.Report(
                        duplicate.TildeToken.Location,
                        $"struct '{type.Name}' may declare only one destructor");
                }
            }

            DestructorDeclarationSyntax? destructorSyntax = destructors.FirstOrDefault();
            if (destructorSyntax is not null)
            {
                if (!string.Equals(destructorSyntax.IdentifierToken.Text, type.Name, StringComparison.Ordinal))
                {
                    _diagnostics.Report(
                        destructorSyntax.IdentifierToken.Location,
                        $"destructor name must match containing struct '{type.Name}'");
                }

                var destructor = new FunctionSymbol(
                    FunctionKind.Destructor,
                    type,
                    [],
                    destructorSyntax,
                    destructorSyntax.IsPublic ? Accessibility.Public : Accessibility.Private);
                type.SetDestructor(destructor);
                _functionBodies.Add((destructor, destructorSyntax.Body));
            }
        }
    }

    private void DeclareFunctions()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            foreach (FunctionDeclarationSyntax declaration in tree.Root.Members.OfType<FunctionDeclarationSyntax>())
            {
                if (declaration.IsExtern && declaration.IdentifierToken.Text is "malloc" or "free")
                {
                    _diagnostics.Report(
                        declaration.IdentifierToken.Location,
                        $"native symbol '{declaration.IdentifierToken.Text}' is reserved for Xenon memory operations");
                }

                TypeSymbol returnType = TypeResolver.Resolve(declaration.ReturnType, @namespace, _diagnostics);
                ImmutableArray<ParameterSymbol> parameters = BindParameters(declaration.Parameters, @namespace);
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
                if (declaration.Body is not null)
                {
                    _functionBodies.Add((function, declaration.Body));
                }
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

        if (function.ReturnType is ArrayTypeSymbol)
        {
            _diagnostics.Report(
                declaration.ReturnType.NameToken.Location,
                "external ABI does not yet support Xenon array types directly; use a pointer and explicit length");
        }

        for (int index = 0; index < function.Parameters.Length; index++)
        {
            TypeSymbol parameterType = function.Parameters[index].Type;
            if (parameterType is StructTypeSymbol parameterStruct)
            {
                _diagnostics.Report(
                    declaration.Parameters[index].Type.NameToken.Location,
                    $"external ABI does not yet support struct '{parameterStruct.Name}' by value; use a pointer instead");
            }
            else if (parameterType is ArrayTypeSymbol)
            {
                _diagnostics.Report(
                    declaration.Parameters[index].Type.NameToken.Location,
                    "external ABI does not yet support Xenon array types directly; use a pointer and explicit length");
            }
        }
    }

    private ImmutableArray<ParameterSymbol> BindParameters(
        ImmutableArray<ParameterSyntax> parameterSyntax,
        NamespaceSymbol containingNamespace)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < parameterSyntax.Length; index++)
        {
            ParameterSyntax syntax = parameterSyntax[index];
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
