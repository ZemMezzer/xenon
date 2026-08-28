using System.Collections.Immutable;
using System.Numerics;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Semantics;

internal sealed class SemanticAnalyzer
{
    private readonly ImmutableArray<SyntaxTree> _syntaxTrees;
    private readonly TypeFactory _typeFactory;
    private readonly ConstantEvaluationContext _constants;
    private readonly DiagnosticBag _diagnostics = new();
    private readonly NamespaceSymbol _globalNamespace = new(string.Empty, null);
    private readonly Dictionary<SyntaxTree, NamespaceSymbol> _treeNamespaces = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<SyntaxTree, FileSymbolScope> _treeScopes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionDeclarationSyntax, FunctionSymbol> _functionSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructDeclarationSyntax, StructTypeSymbol> _structSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<InterfaceDeclarationSyntax, InterfaceTypeSymbol> _interfaceSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructDeclarationSyntax, FileSymbolScope> _structScopes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ConstantSymbol, FileSymbolScope> _constantScopes = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<ConstantSymbol> _evaluatingConstants = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<EnumDeclarationSyntax, (EnumTypeSymbol Type, SyntaxTree Tree)> _enums = [];
    private readonly Dictionary<ConstantSymbol, (EnumTypeSymbol Type, ConstantSymbol? Previous, bool Automatic)> _enumMembers = [];
    private readonly List<(FunctionSymbol Symbol, BlockStatementSyntax Body, FileSymbolScope Scope)> _functionBodies = [];
    private readonly List<BoundFunction> _synthesizedFunctions = [];
    private readonly Dictionary<BoundExpression, TextLocation> _expressionLocations = new(ReferenceEqualityComparer.Instance);

    private SemanticAnalyzer(ImmutableArray<SyntaxTree> syntaxTrees, TypeFactory typeFactory, ITargetTypeLayout? targetLayout)
    {
        _syntaxTrees = syntaxTrees;
        _typeFactory = typeFactory;
        _constants = new ConstantEvaluationContext(targetLayout);
    }

    public static SemanticModel Analyze(ImmutableArray<SyntaxTree> syntaxTrees, TypeFactory typeFactory, ITargetTypeLayout? targetLayout = null)
    {
        var analyzer = new SemanticAnalyzer(syntaxTrees, typeFactory, targetLayout);
        return analyzer.Analyze();
    }

    private SemanticModel Analyze()
    {
        DeclareNamespaces();
        DeclareStructs();
        DeclareInterfaces();
        DeclareEnums();
        BindUsingDirectives();
        BindTypeInheritance();
        ValidateInheritanceCycles();
        MarkVirtualDispatchRequirements();
        DeclareInterfaceMethods();
        ValidateInheritedInterfaceMembers();
        AssignInterfaceMethodSlots();
        BindStructFields();
        ValidateStructLayouts();
        DeclareConstants();
        DeclareEnumMembers();
        // Invalid by-value layouts must not be queried through a native ABI provider.
        if (_diagnostics.Count != 0) _constants.TargetLayout = null;
        EvaluateConstants();
        BindStaticFieldInitializers();
        DeclareStructProperties();
        DeclareStructIndexers();
        DeclareStructMethods();
        DeclareStructLifecycleFunctions();
        foreach (StructTypeSymbol type in _structSymbols.Values)
            if (type.Destructor is null && type.BaseType?.FindDestructor() is { IsPublic: false } inheritedDestructor)
                _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                    $"destructor '{inheritedDestructor.ContainingType!.Name}' is private");
        BuildVirtualMethodTables();
        ValidateInterfaceImplementations();
        DeclareFunctions();
        ValidateAbstractValueStorage();
        BindInstanceFieldInitializers();
        ValidateNativeSymbols();

        var functions = ImmutableArray.CreateBuilder<BoundFunction>();
        foreach ((FunctionSymbol symbol, BlockStatementSyntax body, FileSymbolScope scope) in _functionBodies)
        {
            var binder = new FunctionBodyBinder(symbol, scope, _diagnostics, _constants);
            functions.Add(new BoundFunction(symbol, binder.BindBody(body)));
            foreach (var entry in binder.ExpressionLocations) _expressionLocations.TryAdd(entry.Key, entry.Value);
        }
        functions.AddRange(_synthesizedFunctions);

        // Lifecycle/accessor checks need all bodies, including declarations that
        // occur after the readonly caller and synthesized field initializers.
        var bodies = functions.ToDictionary(bound => bound.Symbol, bound => bound.Body);
        ImmutableArray<StructTypeSymbol> types = [.. _structSymbols.Values];
        foreach ((FunctionSymbol symbol, BlockStatementSyntax body, _) in _functionBodies)
        {
            if (symbol.IsReadonly)
                new ReadonlyEffectAnalyzer(symbol, _diagnostics, _expressionLocations,
                    body.OpenBraceToken.Location, bodies, types).Analyze(bodies[symbol]);
        }

        return new SemanticModel(_globalNamespace, _typeFactory, functions.ToImmutable(), [.. _diagnostics], _constants.RequiresTargetLayout);
    }

    private void BindInstanceFieldInitializers()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            if (!type.Fields.Any(field => field.Declaration.Initializer is not null))
                continue;

            var initializer = new FunctionSymbol(
                FunctionKind.InstanceInitializer,
                type,
                [],
                declaration,
                Accessibility.Private);
            type.SetInstanceInitializer(initializer);
            var binder = new FunctionBodyBinder(
                initializer,
                _structScopes[declaration],
                _diagnostics,
                _constants);

            foreach (FieldSymbol field in type.Fields)
            {
                if (binder.BindFieldInitializer(field) is BoundExpression boundInitializer)
                    field.SetInitializer(boundInitializer);
            }
            foreach (var entry in binder.ExpressionLocations) _expressionLocations.TryAdd(entry.Key, entry.Value);

            _synthesizedFunctions.Add(new BoundFunction(
                initializer,
                new BoundBlockStatement(binder.CreateInstanceFieldInitializerStatements(type))));
        }
    }

    private void DeclareNamespaces()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol current = _globalNamespace;
            NamespaceDeclarationSyntax declaration = tree.Root.Namespace;
            for (int index = 0; index < declaration.NameParts.Length; index++)
            {
                current = current.GetOrAddNamespace(declaration.NameParts[index].Text);
                current.AddDeclaration(declaration, index);
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

    private void DeclareEnums()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        foreach (EnumDeclarationSyntax declaration in tree.Root.Members.OfType<EnumDeclarationSyntax>())
        {
            var type = new EnumTypeSymbol(declaration.IdentifierToken.Text, _treeNamespaces[tree], declaration);
            if (!type.ContainingNamespace.TryDeclareType(type))
                _diagnostics.Report(declaration.IdentifierToken.Location, $"type '{type.FullName}' is already declared");
            else
                _enums.Add(declaration, (type, tree));
        }
    }

    private void DeclareEnumMembers()
    {
        foreach ((EnumDeclarationSyntax syntax, (EnumTypeSymbol type, SyntaxTree tree)) in _enums)
        {
            TypeSymbol underlying = syntax.UnderlyingType is null ? BuiltinTypes.Int : TypeResolver.Resolve(syntax.UnderlyingType, _treeScopes[tree], _diagnostics);
            if (underlying is PrimitiveTypeSymbol { IsInteger: true } integer)
                type.UnderlyingType = integer;
            else
                _diagnostics.Report(syntax.IdentifierToken.Location, "enum underlying type must be an integer type");
            var members = ImmutableArray.CreateBuilder<ConstantSymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            ConstantSymbol? previous = null;
            foreach (EnumMemberDeclarationSyntax member in syntax.Members)
            {
                if (!names.Add(member.IdentifierToken.Text))
                {
                    _diagnostics.Report(member.IdentifierToken.Location, $"duplicate enum member '{member.IdentifierToken.Text}'");
                    continue;
                }
                ExpressionSyntax initializer = member.Value ?? new LiteralExpressionSyntax(
                    new SyntaxToken(SyntaxKind.IntegerLiteralToken, member.IdentifierToken.Location, "0", 0UL));
                var constant = new ConstantSymbol(member.IdentifierToken.Text, type, type, initializer, member);
                _constantScopes.Add(constant, _treeScopes[tree]);
                _enumMembers.Add(constant, (type, previous, member.Value is null));
                members.Add(constant);
                previous = constant;
            }
            type.Members = members.ToImmutable();
        }
    }

    private bool EvaluateEnumMember(ConstantSymbol member, EnumTypeSymbol type, ConstantSymbol? previous, bool automatic)
    {
        object? value;
        if (automatic)
        {
            if (previous is not null && !EvaluateConstant(previous)) return false;
            if (previous?.BoundValue is BoundDeferredConstantExpression)
            {
                member.SetBoundValue(new BoundDeferredConstantExpression(type));
                return true;
            }
            BigInteger number = previous is null ? BigInteger.Zero : ToInteger(previous.Value) + 1;
            if (!FitsInteger(number, type.UnderlyingType, _constants.TargetLayout))
            {
                _diagnostics.Report(member.IdentifierToken.Location, $"enum value is out of range for '{type.UnderlyingType.Name}'");
                return false;
            }
            value = IntegerValue(number, type.UnderlyingType, _constants.TargetLayout);
        }
        else
        {
            BoundExpression? expression = BindConstantExpression(member.Initializer, member);
            if (expression is null || !(TypeFacts.IsInteger(expression.Type) || TypeIdentity.AreSame(expression.Type, type)))
            {
                _diagnostics.Report(member.IdentifierToken.Location, "enum value must be an integer compile-time constant");
                return false;
            }
            ConstantFoldStatus status = _constants.Fold(expression, out value);
            if (status == ConstantFoldStatus.TargetDependent)
            {
                member.SetBoundValue(new BoundDeferredConstantExpression(type));
                return true;
            }
            if (status == ConstantFoldStatus.Invalid)
            {
                _diagnostics.Report(member.IdentifierToken.Location, "enum value must be an integer compile-time constant with valid operations");
                return false;
            }
            BigInteger number = ToInteger(value);
            if (!FitsInteger(number, type.UnderlyingType, _constants.TargetLayout))
            {
                _diagnostics.Report(member.IdentifierToken.Location, $"enum value is out of range for '{type.UnderlyingType.Name}'");
                return false;
            }
            value = IntegerValue(number, type.UnderlyingType, _constants.TargetLayout);
        }
        member.SetValue(value);
        member.SetBoundValue(new BoundLiteralExpression(value, type));
        return true;
    }

    internal static BigInteger ToInteger(object? value) => value switch
    {
        int number => number,
        long number => number,
        ulong number => number,
        _ => throw new InvalidOperationException("Expected an integer constant."),
    };

    internal static bool FitsInteger(BigInteger value, PrimitiveTypeSymbol type, ITargetTypeLayout? targetLayout = null)
    {
        int bits = type.BitWidth ?? targetLayout?.GetIntegerBitWidth(type) ?? 64;
        return type.IsSigned
            ? value >= -(BigInteger.One << (bits - 1)) && value < (BigInteger.One << (bits - 1))
            : value >= 0 && value < (BigInteger.One << bits);
    }

    internal static object IntegerValue(BigInteger value, PrimitiveTypeSymbol type, ITargetTypeLayout? targetLayout = null)
    {
        int bits = type.BitWidth ?? targetLayout?.GetIntegerBitWidth(type) ?? 64;
        if (!type.IsSigned && bits >= 32) return (ulong)value;
        if (bits > 32) return (long)value;
        return (int)value;
    }

    private void DeclareInterfaces()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            foreach (InterfaceDeclarationSyntax declaration in tree.Root.Members.OfType<InterfaceDeclarationSyntax>())
            {
                var type = new InterfaceTypeSymbol(declaration.IdentifierToken.Text, @namespace, declaration);
                if (!@namespace.TryDeclareType(type))
                {
                    _diagnostics.Report(declaration.IdentifierToken.Location, $"type '{@namespace.FullName}.{type.Name}' is already declared");
                    continue;
                }
                _interfaceSymbols.Add(declaration, type);
            }
        }
    }

    private void BindUsingDirectives()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            var scope = new FileSymbolScope(_globalNamespace, _treeNamespaces[tree], _typeFactory);
            scope.BindUsings(tree.Root.Usings, _diagnostics);
            _treeScopes.Add(tree, scope);

            foreach (StructDeclarationSyntax declaration in tree.Root.Members.OfType<StructDeclarationSyntax>())
            {
                if (_structSymbols.ContainsKey(declaration))
                {
                    _structScopes.Add(declaration, scope);
                }
            }
        }
    }

    private void BindStructFields()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var fields = ImmutableArray.CreateBuilder<FieldSymbol>();
            var staticFields = ImmutableArray.CreateBuilder<FieldSymbol>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (FieldDeclarationSyntax fieldSyntax in declaration.Fields)
            {
                TypeSymbol fieldType = TypeResolver.Resolve(
                    fieldSyntax.Type,
                    scope,
                    _diagnostics);
                if (TypeIdentity.AreSame(fieldType, BuiltinTypes.Void))
                {
                    _diagnostics.Report(fieldSyntax.Type.NameToken.Location, "field type cannot be 'void'");
                }

                if (!names.Add(fieldSyntax.IdentifierToken.Text))
                {
                    _diagnostics.Report(
                        fieldSyntax.IdentifierToken.Location,
                        $"field '{fieldSyntax.IdentifierToken.Text}' is already declared in struct '{type.Name}'");
                }

                var field = new FieldSymbol(
                    fieldSyntax.IdentifierToken.Text,
                    type,
                    fieldType,
                    fieldSyntax.IsStatic ? staticFields.Count : type.DeclaredFieldStart + fields.Count,
                    fieldSyntax.IsPublic ? Accessibility.Public : Accessibility.Private,
                    fieldSyntax.IsStatic,
                    fieldSyntax.IsReadonly,
                    null,
                    fieldSyntax);
                if (fieldSyntax.IsStatic)
                    staticFields.Add(field);
                else
                    fields.Add(field);
            }

            type.SetFields(fields.ToImmutable());
            type.SetStaticFields(staticFields.ToImmutable());
        }
    }

    private void BindStaticFieldInitializers()
    {
        // Layout queries are safe only after every struct's fields and layout are known.
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        foreach (FieldSymbol field in type.StaticFields.Where(field => field.Declaration.Initializer is not null))
        {
            FieldDeclarationSyntax syntax = field.Declaration;
            var context = new ConstantSymbol(field.Name, field.Type, type, syntax.Initializer!, syntax);
            _constantScopes.Add(context, _structScopes[declaration]);
            BoundExpression? initializer = BindConstantExpression(syntax.Initializer!, context);
            _constantScopes.Remove(context);
            object? value = null;
            ConstantFoldStatus status = initializer is null ? ConstantFoldStatus.Invalid : _constants.Fold(initializer, out value);
            TypeSymbol constantType = initializer?.Type ?? BuiltinTypes.Error;
            if (status == ConstantFoldStatus.Invalid)
                _diagnostics.Report(syntax.IdentifierToken.Location, "static field initializers must be compile-time constants");
            else if (TypeIdentity.AreSame(constantType, BuiltinTypes.Error) || !TypeFacts.CanAssign(field.Type, constantType))
                _diagnostics.Report(syntax.IdentifierToken.Location, $"cannot implicitly convert '{constantType.Name}' to '{field.Type.ToDisplayString()}'");
            else if (!IsSupportedStaticInitializer(field.Type, value))
                _diagnostics.Report(syntax.IdentifierToken.Location, $"static field type '{field.Type.ToDisplayString()}' does not support this constant initializer");
            else
                field.SetConstantValue(value);
        }
    }

    private void DeclareConstants()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            FileSymbolScope scope = _treeScopes[tree];
            foreach (ModuleConstantDeclarationSyntax syntax in tree.Root.Members.OfType<ModuleConstantDeclarationSyntax>())
            {
                TypeSymbol type = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var constant = new ConstantSymbol(syntax.IdentifierToken.Text, type, @namespace, syntax.Initializer, syntax);
                if (!@namespace.TryDeclareConstant(constant))
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"constant '{@namespace.FullName}.{constant.Name}' is already declared");
                else
                    _constantScopes.Add(constant, scope);
            }
        }

        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var constants = ImmutableArray.CreateBuilder<ConstantSymbol>();
            foreach (TypeConstantDeclarationSyntax syntax in declaration.Constants)
            {
                TypeSymbol constantType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var constant = new ConstantSymbol(syntax.IdentifierToken.Text, constantType, type, syntax.Initializer, syntax);
                if (constants.Any(existing => existing.Name == constant.Name))
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"constant '{constant.Name}' is already declared in struct '{type.Name}'");
                else
                {
                    constants.Add(constant);
                    _constantScopes.Add(constant, scope);
                }
            }
            type.SetConstants(constants.ToImmutable());
        }
    }

    private void EvaluateConstants()
    {
        IEnumerable<ConstantSymbol> constants = _constantScopes.Keys;
        foreach (ConstantSymbol constant in constants)
            EvaluateConstant(constant);
    }

    private bool EvaluateConstant(ConstantSymbol constant)
    {
        if (constant.HasValue)
            return true;
        if (!_evaluatingConstants.Add(constant))
        {
            _diagnostics.Report(constant.IdentifierToken.Location, $"circular constant dependency involving '{constant.Name}'");
            return false;
        }
        try
        {
            if (_enumMembers.TryGetValue(constant, out var enumMember))
                return EvaluateEnumMember(constant, enumMember.Type, enumMember.Previous, enumMember.Automatic);
            BoundExpression? value = BindConstantExpression(constant.Initializer, constant);
            if (value is null)
            {
                _diagnostics.Report(constant.IdentifierToken.Location, $"initializer of constant '{constant.Name}' is not a compile-time constant");
                return false;
            }
            if (!TypeFacts.CanAssign(constant.Type, value.Type))
            {
                if (TypeFacts.IsNumeric(constant.Type) && TypeFacts.IsNumeric(value.Type))
                    value = new BoundCastExpression(value, constant.Type);
                else
                {
                    _diagnostics.Report(constant.IdentifierToken.Location, $"cannot implicitly convert '{value.Type.ToDisplayString()}' to '{constant.Type.ToDisplayString()}'");
                    return false;
                }
            }

            ConstantFoldStatus foldStatus = _constants.Fold(value, out object? foldedValue);
            if (foldStatus == ConstantFoldStatus.Invalid)
            {
                _diagnostics.Report(
                    constant.IdentifierToken.Location,
                    $"initializer of constant '{constant.Name}' contains an invalid compile-time operation");
                return false;
            }

            if (foldStatus == ConstantFoldStatus.Folded)
            {
                constant.SetValue(foldedValue);
                constant.SetBoundValue(new BoundLiteralExpression(foldedValue, value.Type));
            }
            else
            {
                constant.SetBoundValue(value);
            }
            return true;
        }
        finally
        {
            _evaluatingConstants.Remove(constant);
        }
    }

    private BoundExpression? BindConstantExpression(ExpressionSyntax syntax, ConstantSymbol context)
    {
        switch (syntax)
        {
            case LiteralExpressionSyntax literal:
                return new BoundLiteralExpression(GetConstantLiteralValue(literal), GetConstantExpressionType(literal));
            case ParenthesizedExpressionSyntax parenthesized:
                return BindConstantExpression(parenthesized.Expression, context);
            case NameExpressionSyntax name:
            {
                ConstantSymbol? referenced = (_enumMembers.TryGetValue(context, out var enumContext) ? enumContext.Type.FindMember(name.IdentifierToken.Text) : null) ??
                    context.ContainingType?.FindMember<ConstantSymbol>(name.IdentifierToken.Text) ??
                    _constantScopes[context].ResolveConstant(name.IdentifierToken.Text, name.IdentifierToken.Location, _diagnostics);
                return referenced is not null && EvaluateConstant(referenced) ? referenced.BoundValue : null;
            }
            case MemberAccessExpressionSyntax member when member.Receiver is NameExpressionSyntax typeName &&
                _constantScopes[context].ResolveType(typeName.IdentifierToken.Text, typeName.IdentifierToken.Location, _diagnostics) is DeclaredTypeSymbol structType &&
                structType.FindMember<ConstantSymbol>(member.MemberToken.Text) is ConstantSymbol associated:
                return EvaluateConstant(associated) ? associated.BoundValue : null;
            case MemberAccessExpressionSyntax member:
            {
                var parts = new List<SyntaxToken>();
                ExpressionSyntax receiver = member;
                while (receiver is MemberAccessExpressionSyntax access && access.OperatorToken.Kind == SyntaxKind.DotToken)
                {
                    parts.Insert(0, access.MemberToken);
                    receiver = access.Receiver;
                }
                if (receiver is not NameExpressionSyntax name) return null;
                parts.Insert(0, name.IdentifierToken);
                TypeSymbol? resolved = parts.Count == 2
                    ? _constantScopes[context].ResolveType(parts[0].Text, parts[0].Location, _diagnostics)
                    : _constantScopes[context].ResolveQualifiedType(parts.Take(parts.Count - 1).Select(part => part.Text).ToArray());
                ConstantSymbol? referenced = resolved switch
                {
                    EnumTypeSymbol enumeration => enumeration.FindMember(parts[^1].Text),
                    StructTypeSymbol structure => structure.FindConstant(parts[^1].Text),
                    _ => null,
                };
                return referenced is not null && EvaluateConstant(referenced) ? referenced.BoundValue : null;
            }
            case UnaryExpressionSyntax unary:
            {
                BoundExpression? operand = BindConstantExpression(unary.Operand, context);
                if (operand is null)
                    return null;
                TypeSymbol? result = unary.OperatorToken.Kind switch
                {
                    SyntaxKind.PlusToken or SyntaxKind.MinusToken when TypeFacts.IsNumeric(operand.Type) => operand.Type,
                    SyntaxKind.BangToken when TypeIdentity.AreSame(operand.Type, BuiltinTypes.Bool) => BuiltinTypes.Bool,
                    SyntaxKind.TildeToken when TypeFacts.IsInteger(operand.Type) => operand.Type,
                    _ => null,
                };
                return result is null ? null : new BoundUnaryExpression(unary.OperatorToken.Kind, operand, result);
            }
            case BinaryExpressionSyntax binary:
            {
                BoundExpression? left = BindConstantExpression(binary.Left, context);
                BoundExpression? right = BindConstantExpression(binary.Right, context);
                if (left is null || right is null)
                    return null;
                bool shift = binary.OperatorToken.Kind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken;
                if (!TypeIdentity.AreSame(left.Type, right.Type) && !(shift && TypeFacts.IsInteger(left.Type) && TypeFacts.IsInteger(right.Type)))
                    return null;
                bool comparison = binary.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or
                    SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken;
                if (binary.OperatorToken.Kind is SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken &&
                    !TypeFacts.CanCompareEquality(left.Type, right.Type))
                    return null;
                bool logical = binary.OperatorToken.Kind is SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken;
                bool arithmetic = binary.OperatorToken.Kind is SyntaxKind.PlusToken or SyntaxKind.MinusToken or SyntaxKind.StarToken or SyntaxKind.SlashToken or SyntaxKind.PercentToken;
                bool bitwise = binary.OperatorToken.Kind is SyntaxKind.AmpersandToken or SyntaxKind.PipeToken or SyntaxKind.CaretToken or SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken;
                if ((logical && !TypeIdentity.AreSame(left.Type, BuiltinTypes.Bool)) ||
                    (arithmetic && !TypeFacts.IsNumeric(left.Type)) ||
                    (bitwise && !TypeFacts.IsInteger(left.Type)) ||
                    (!comparison && !logical && !arithmetic && !bitwise))
                    return null;
                return new BoundBinaryExpression(left, binary.OperatorToken.Kind, right, comparison || logical ? BuiltinTypes.Bool : left.Type);
            }
            case TypeLayoutExpressionSyntax layout:
            {
                TypeSymbol target = TypeResolver.Resolve(layout.Type, _constantScopes[context], _diagnostics);
                if (TypeIdentity.AreSame(target, BuiltinTypes.Void) || TypeIdentity.AreSame(target, BuiltinTypes.Error))
                    return null;
                FieldSymbol? field = null;
                if (layout.Keyword.Kind == SyntaxKind.OffsetOfKeyword)
                {
                    if (target is not StructTypeSymbol targetStruct ||
                        (field = targetStruct.FindField(layout.FieldToken!.Text)) is null)
                        return null;
                }
                return new BoundTypeLayoutExpression(layout.Keyword.Kind, target, field);
            }
            case CastExpressionSyntax cast:
            {
                BoundExpression? expression = BindConstantExpression(cast.Expression, context);
                TypeSymbol target = TypeResolver.Resolve(cast.Type, _constantScopes[context], _diagnostics);
                if (expression is null || !TypeFacts.CanExplicitlyCast(target, expression.Type))
                    return null;
                return new BoundCastExpression(expression, target);
            }
            default:
                return null;
        }
    }

    internal static ConstantFoldStatus FoldConstantExpression(BoundExpression expression, out object? value, ITargetTypeLayout? targetLayout)
    {
        switch (expression)
        {
            case BoundLiteralExpression literal:
                if (targetLayout is null && (literal.Type is PrimitiveTypeSymbol { IsInteger: true, BitWidth: null } or EnumTypeSymbol { UnderlyingType.BitWidth: null }))
                {
                    value = null;
                    return ConstantFoldStatus.TargetDependent;
                }
                value = literal.Value;
                return ConstantFoldStatus.Folded;
            case BoundDeferredConstantExpression:
                value = null;
                return ConstantFoldStatus.TargetDependent;
            case BoundTypeLayoutExpression layout:
                if (targetLayout is null)
                {
                    value = null;
                    return ConstantFoldStatus.TargetDependent;
                }
                value = layout.OperatorKind switch
                {
                    SyntaxKind.SizeOfKeyword => targetLayout.GetSize(layout.TargetType),
                    SyntaxKind.AlignOfKeyword => (ulong)targetLayout.GetAlignment(layout.TargetType),
                    SyntaxKind.OffsetOfKeyword => targetLayout.GetFieldOffset((StructTypeSymbol)layout.TargetType, layout.Field!),
                    _ => throw new InvalidOperationException("Unknown layout intrinsic."),
                };
                return ConstantFoldStatus.Folded;
            case BoundUnaryExpression unary:
            {
                ConstantFoldStatus operandStatus = FoldConstantExpression(unary.Operand, out object? operand, targetLayout);
                if (operandStatus != ConstantFoldStatus.Folded)
                {
                    value = null;
                    return operandStatus;
                }
                if (TryEvaluateUnaryConstant(unary.OperatorKind, operand, out object? unaryValue) &&
                    TryNormalizeFoldedValue(unaryValue, unary.Type, out value, targetLayout))
                {
                    return ConstantFoldStatus.Folded;
                }
                value = null;
                return ConstantFoldStatus.Invalid;
            }
            case BoundBinaryExpression binary:
            {
                ConstantFoldStatus leftStatus = FoldConstantExpression(binary.Left, out object? left, targetLayout);
                if (leftStatus == ConstantFoldStatus.Invalid)
                {
                    value = null;
                    return ConstantFoldStatus.Invalid;
                }
                if (leftStatus == ConstantFoldStatus.Folded && left is bool leftBoolean)
                {
                    if (binary.OperatorKind == SyntaxKind.AmpersandAmpersandToken && !leftBoolean)
                    {
                        value = false;
                        return ConstantFoldStatus.Folded;
                    }
                    if (binary.OperatorKind == SyntaxKind.PipePipeToken && leftBoolean)
                    {
                        value = true;
                        return ConstantFoldStatus.Folded;
                    }
                }

                ConstantFoldStatus rightStatus = FoldConstantExpression(binary.Right, out object? right, targetLayout);
                if (rightStatus == ConstantFoldStatus.Invalid ||
                    (binary.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken && IsIntegerZero(right)))
                {
                    value = null;
                    return ConstantFoldStatus.Invalid;
                }
                if (leftStatus == ConstantFoldStatus.TargetDependent || rightStatus == ConstantFoldStatus.TargetDependent)
                {
                    value = null;
                    return ConstantFoldStatus.TargetDependent;
                }

                try
                {
                    if (binary.OperatorKind is SyntaxKind.SlashToken or SyntaxKind.PercentToken &&
                        binary.Left.Type is PrimitiveTypeSymbol { IsInteger: true, IsSigned: true } signedType)
                    {
                        int? signedWidth = signedType.BitWidth ?? targetLayout?.GetIntegerBitWidth(signedType);
                        if (signedWidth is null)
                        {
                            value = null;
                            return ConstantFoldStatus.TargetDependent;
                        }
                        if (ToInteger(left) == -(BigInteger.One << (signedWidth.Value - 1)) && ToInteger(right) == -1)
                        {
                            value = null;
                            return ConstantFoldStatus.Invalid;
                        }
                    }
                    if (binary.OperatorKind is SyntaxKind.LessLessToken or SyntaxKind.GreaterGreaterToken)
                    {
                        var operandType = (PrimitiveTypeSymbol)binary.Left.Type;
                        int? width = operandType.BitWidth ?? targetLayout?.GetIntegerBitWidth(operandType);
                        BigInteger count = ToInteger(right);
                        if (width is null)
                        {
                            value = null;
                            return ConstantFoldStatus.TargetDependent;
                        }
                        if (count < 0 || count >= width)
                        {
                            value = null;
                            return ConstantFoldStatus.Invalid;
                        }
                        object shifted = (left, binary.OperatorKind) switch
                        {
                            (int integer, SyntaxKind.LessLessToken) => (object)(integer << (int)count),
                            (int integer, _) => integer >> (int)count,
                            (long integer, SyntaxKind.LessLessToken) => integer << (int)count,
                            (long integer, _) => integer >> (int)count,
                            (ulong integer, SyntaxKind.LessLessToken) => integer << (int)count,
                            (ulong integer, _) => integer >> (int)count,
                            _ => throw new InvalidOperationException("Invalid shift constant."),
                        };
                        return TryNormalizeFoldedValue(shifted, binary.Type, out value, targetLayout)
                            ? ConstantFoldStatus.Folded : ConstantFoldStatus.Invalid;
                    }
                    if (TryEvaluateBinaryConstant(left, binary.OperatorKind, right, out object? binaryValue) &&
                        TryNormalizeFoldedValue(binaryValue, binary.Type, out value, targetLayout))
                    {
                        return ConstantFoldStatus.Folded;
                    }
                }
                catch (Exception exception) when (exception is OverflowException or DivideByZeroException)
                {
                }

                value = null;
                return ConstantFoldStatus.Invalid;
            }
            case BoundCastExpression cast:
            {
                ConstantFoldStatus operandStatus = FoldConstantExpression(cast.Expression, out object? operand, targetLayout);
                if (operandStatus != ConstantFoldStatus.Folded)
                {
                    value = null;
                    return operandStatus;
                }
                if (targetLayout is null && (cast.TargetType is PrimitiveTypeSymbol { IsInteger: true, BitWidth: null } or EnumTypeSymbol { UnderlyingType.BitWidth: null }))
                {
                    value = null;
                    return ConstantFoldStatus.TargetDependent;
                }
                return TryFoldPrimitiveCast(operand, cast.TargetType, out value, targetLayout)
                    ? ConstantFoldStatus.Folded
                    : ConstantFoldStatus.Invalid;
            }
            default:
                value = null;
                return ConstantFoldStatus.Invalid;
        }
    }

    private static bool IsIntegerZero(object? value) => value switch
    {
        int integer => integer == 0,
        long integer => integer == 0,
        ulong integer => integer == 0,
        _ => false,
    };

    private static bool TryFoldPrimitiveCast(object? value, TypeSymbol targetType, out object? converted, ITargetTypeLayout? targetLayout)
    {
        if (targetType is EnumTypeSymbol enumeration) targetType = enumeration.UnderlyingType;
        if (targetType is PrimitiveTypeSymbol { IsInteger: true, BitWidth: null } native && targetLayout is not null)
        {
            int width = targetLayout.GetIntegerBitWidth(native);
            targetType = (width, native.IsSigned) switch
            {
                (32, true) => BuiltinTypes.Int,
                (32, false) => BuiltinTypes.UInt,
                (64, true) => BuiltinTypes.Long,
                (64, false) => BuiltinTypes.ULong,
                _ => throw new InvalidOperationException($"Unsupported native integer width {width}."),
            };
        }
        try
        {
            if (TypeIdentity.AreSame(targetType, BuiltinTypes.Float))
            {
                converted = Convert.ToSingle(value);
                return true;
            }
            if (TypeIdentity.AreSame(targetType, BuiltinTypes.Double))
            {
                converted = Convert.ToDouble(value);
                return true;
            }
            if (targetType is not PrimitiveTypeSymbol { IsInteger: true } integerType)
            {
                converted = null;
                return false;
            }

            if (value is float or double)
            {
                double number = Convert.ToDouble(value);
                if (!double.IsFinite(number)) { converted = null; return false; }
                BigInteger truncated = new(Math.Truncate(number));
                int width = integerType.BitWidth!.Value;
                BigInteger minimum = integerType.IsSigned ? -(BigInteger.One << (width - 1)) : BigInteger.Zero;
                BigInteger maximum = (BigInteger.One << (integerType.IsSigned ? width - 1 : width)) - 1;
                if (truncated < minimum || truncated > maximum) { converted = null; return false; }
                value = integerType.IsSigned ? (object)(long)truncated : (ulong)truncated;
            }
            ulong bits = value switch
            {
                int integer => unchecked((ulong)(long)integer),
                long integer => unchecked((ulong)integer),
                ulong integer => integer,
                _ => throw new InvalidCastException(),
            };
            converted = targetType.Name switch
            {
                "byte" => (int)unchecked((byte)bits),
                "sbyte" => (int)unchecked((sbyte)bits),
                "short" => (int)unchecked((short)bits),
                "ushort" => (int)unchecked((ushort)bits),
                "int" => unchecked((int)bits),
                "uint" => (ulong)unchecked((uint)bits),
                "long" => unchecked((long)bits),
                "ulong" => bits,
                _ => null,
            };
            return converted is not null;
        }
        catch (Exception exception) when (exception is OverflowException or InvalidCastException or FormatException)
        {
            converted = null;
            return false;
        }
    }

    private static bool TryNormalizeFoldedValue(object? value, TypeSymbol type, out object? normalized, ITargetTypeLayout? targetLayout)
    {
        if (TypeIdentity.AreSame(type, BuiltinTypes.Bool) && value is bool)
        {
            normalized = value;
            return true;
        }
        return TryFoldPrimitiveCast(value, type, out normalized, targetLayout);
    }

    private static bool IsSupportedStaticInitializer(TypeSymbol type, object? value) =>
        value is null ||
        (TypeIdentity.AreSame(type, BuiltinTypes.Bool) && value is bool) ||
        (type is PrimitiveTypeSymbol { IsInteger: true } && value is not bool) ||
        type is PrimitiveTypeSymbol { IsFloatingPoint: true };

    private static object? GetConstantLiteralValue(LiteralExpressionSyntax literal) => literal.LiteralToken switch
    {
        { Kind: SyntaxKind.TrueKeyword } => true,
        { Kind: SyntaxKind.FalseKeyword } => false,
        { Kind: SyntaxKind.IntegerLiteralToken, Value: ulong integer } when integer <= int.MaxValue => (int)integer,
        { Kind: SyntaxKind.IntegerLiteralToken, Value: ulong integer } when integer <= long.MaxValue => (long)integer,
        { Kind: SyntaxKind.IntegerLiteralToken, Value: ulong integer } => integer,
        _ => literal.LiteralToken.Value,
    };

    private static bool TryEvaluateUnaryConstant(SyntaxKind operation, object? operand, out object? value)
    {
        value = (operation, operand) switch
        {
            (SyntaxKind.PlusToken, int or long or ulong or float or double) => operand,
            (SyntaxKind.MinusToken, int integer) => unchecked(-integer),
            (SyntaxKind.MinusToken, long integer) => unchecked(-integer),
            (SyntaxKind.MinusToken, ulong integer) => unchecked(0UL - integer),
            (SyntaxKind.MinusToken, float number) => -number,
            (SyntaxKind.MinusToken, double number) => -number,
            (SyntaxKind.BangToken, bool boolean) => !boolean,
            (SyntaxKind.TildeToken, int integer) => ~integer,
            (SyntaxKind.TildeToken, long integer) => ~integer,
            (SyntaxKind.TildeToken, ulong integer) => ~integer,
            _ => null,
        };
        return value is not null;
    }

    private TypeSymbol GetConstantExpressionType(ExpressionSyntax syntax) => syntax switch
    {
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken, LiteralToken.Value: ulong value } when value <= int.MaxValue => BuiltinTypes.Int,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken, LiteralToken.Value: ulong value } when value <= long.MaxValue => BuiltinTypes.Long,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken } => BuiltinTypes.ULong,
        LiteralExpressionSyntax { LiteralToken.Value: float } => BuiltinTypes.Float,
        LiteralExpressionSyntax { LiteralToken.Value: double } => BuiltinTypes.Double,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword } => BuiltinTypes.Bool,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.StringLiteralToken } => _typeFactory.PointerTo(BuiltinTypes.Byte, isReadonly: true),
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.NullKeyword } => BuiltinTypes.Null,
        ParenthesizedExpressionSyntax parenthesized => GetConstantExpressionType(parenthesized.Expression),
        UnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.BangToken } => BuiltinTypes.Bool,
        UnaryExpressionSyntax unary => GetConstantExpressionType(unary.Operand),
        BinaryExpressionSyntax binary when binary.OperatorToken.Kind is
            SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or
            SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or
            SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken or
            SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken => BuiltinTypes.Bool,
        BinaryExpressionSyntax binary when TypeIdentity.AreSame(GetConstantExpressionType(binary.Left), GetConstantExpressionType(binary.Right)) => GetConstantExpressionType(binary.Left),
        _ => BuiltinTypes.Error,
    };

    private static bool TryEvaluateBinaryConstant(object? left, SyntaxKind operation, object? right, out object? value)
    {
        if (left is bool leftBool && right is bool rightBool)
        {
            value = operation switch
            {
                SyntaxKind.AmpersandAmpersandToken => leftBool && rightBool,
                SyntaxKind.PipePipeToken => leftBool || rightBool,
                SyntaxKind.EqualsEqualsToken => leftBool == rightBool,
                SyntaxKind.BangEqualsToken => leftBool != rightBool,
                _ => null,
            };
            return value is not null;
        }

        if (left is float leftFloat && right is float rightFloat)
        {
            value = operation switch
            {
                SyntaxKind.PlusToken => leftFloat + rightFloat,
                SyntaxKind.MinusToken => leftFloat - rightFloat,
                SyntaxKind.StarToken => leftFloat * rightFloat,
                SyntaxKind.SlashToken => leftFloat / rightFloat,
                SyntaxKind.PercentToken => leftFloat % rightFloat,
                SyntaxKind.EqualsEqualsToken => leftFloat == rightFloat,
                SyntaxKind.BangEqualsToken => leftFloat != rightFloat,
                SyntaxKind.LessToken => leftFloat < rightFloat,
                SyntaxKind.LessOrEqualsToken => leftFloat <= rightFloat,
                SyntaxKind.GreaterToken => leftFloat > rightFloat,
                SyntaxKind.GreaterOrEqualsToken => leftFloat >= rightFloat,
                _ => null,
            };
            return value is not null;
        }

        if (left is double leftDouble && right is double rightDouble)
        {
            value = operation switch
            {
                SyntaxKind.PlusToken => leftDouble + rightDouble,
                SyntaxKind.MinusToken => leftDouble - rightDouble,
                SyntaxKind.StarToken => leftDouble * rightDouble,
                SyntaxKind.SlashToken => leftDouble / rightDouble,
                SyntaxKind.PercentToken => leftDouble % rightDouble,
                SyntaxKind.EqualsEqualsToken => leftDouble == rightDouble,
                SyntaxKind.BangEqualsToken => leftDouble != rightDouble,
                SyntaxKind.LessToken => leftDouble < rightDouble,
                SyntaxKind.LessOrEqualsToken => leftDouble <= rightDouble,
                SyntaxKind.GreaterToken => leftDouble > rightDouble,
                SyntaxKind.GreaterOrEqualsToken => leftDouble >= rightDouble,
                _ => null,
            };
            return value is not null;
        }

        if (left is int leftInt && right is int rightInt)
            return TryEvaluateInt32Constant(leftInt, operation, rightInt, out value);
        if (left is long leftLong && right is long rightLong)
            return TryEvaluateInt64Constant(leftLong, operation, rightLong, out value);
        if (left is ulong leftULong && right is ulong rightULong)
            return TryEvaluateUInt64Constant(leftULong, operation, rightULong, out value);

        value = null;
        return false;
    }

    private static bool TryEvaluateInt32Constant(int left, SyntaxKind operation, int right, out object? value)
    {
        value = operation switch
        {
            SyntaxKind.PlusToken => unchecked(left + right),
            SyntaxKind.MinusToken => unchecked(left - right),
            SyntaxKind.StarToken => unchecked(left * right),
            SyntaxKind.SlashToken when right != 0 => left / right,
            SyntaxKind.PercentToken when right != 0 => left % right,
            SyntaxKind.AmpersandToken => left & right,
            SyntaxKind.PipeToken => left | right,
            SyntaxKind.CaretToken => left ^ right,
            SyntaxKind.LessLessToken => left << right,
            SyntaxKind.GreaterGreaterToken => left >> right,
            SyntaxKind.EqualsEqualsToken => left == right,
            SyntaxKind.BangEqualsToken => left != right,
            SyntaxKind.LessToken => left < right,
            SyntaxKind.LessOrEqualsToken => left <= right,
            SyntaxKind.GreaterToken => left > right,
            SyntaxKind.GreaterOrEqualsToken => left >= right,
            _ => null,
        };
        return value is not null;
    }

    private static bool TryEvaluateInt64Constant(long left, SyntaxKind operation, long right, out object? value)
    {
        value = operation switch
        {
            SyntaxKind.PlusToken => unchecked(left + right),
            SyntaxKind.MinusToken => unchecked(left - right),
            SyntaxKind.StarToken => unchecked(left * right),
            SyntaxKind.SlashToken when right != 0 => left / right,
            SyntaxKind.PercentToken when right != 0 => left % right,
            SyntaxKind.AmpersandToken => left & right,
            SyntaxKind.PipeToken => left | right,
            SyntaxKind.CaretToken => left ^ right,
            SyntaxKind.LessLessToken => left << (int)right,
            SyntaxKind.GreaterGreaterToken => left >> (int)right,
            SyntaxKind.EqualsEqualsToken => left == right,
            SyntaxKind.BangEqualsToken => left != right,
            SyntaxKind.LessToken => left < right,
            SyntaxKind.LessOrEqualsToken => left <= right,
            SyntaxKind.GreaterToken => left > right,
            SyntaxKind.GreaterOrEqualsToken => left >= right,
            _ => null,
        };
        return value is not null;
    }

    private static bool TryEvaluateUInt64Constant(ulong left, SyntaxKind operation, ulong right, out object? value)
    {
        value = operation switch
        {
            SyntaxKind.PlusToken => unchecked(left + right),
            SyntaxKind.MinusToken => unchecked(left - right),
            SyntaxKind.StarToken => unchecked(left * right),
            SyntaxKind.SlashToken when right != 0 => left / right,
            SyntaxKind.PercentToken when right != 0 => left % right,
            SyntaxKind.AmpersandToken => left & right,
            SyntaxKind.PipeToken => left | right,
            SyntaxKind.CaretToken => left ^ right,
            SyntaxKind.LessLessToken => left << (int)right,
            SyntaxKind.GreaterGreaterToken => left >> (int)right,
            SyntaxKind.EqualsEqualsToken => left == right,
            SyntaxKind.BangEqualsToken => left != right,
            SyntaxKind.LessToken => left < right,
            SyntaxKind.LessOrEqualsToken => left <= right,
            SyntaxKind.GreaterToken => left > right,
            SyntaxKind.GreaterOrEqualsToken => left >= right,
            _ => null,
        };
        return value is not null;
    }

    private void BindTypeInheritance()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            var interfaces = ImmutableArray.CreateBuilder<InterfaceTypeSymbol>();
            foreach (TypeSyntax baseSyntax in declaration.BaseTypes)
            {
                TypeSymbol resolved = TypeResolver.Resolve(baseSyntax, _structScopes[declaration], _diagnostics);
                if (resolved is StructTypeSymbol baseStruct)
                {
                    if (type.BaseType is not null)
                        _diagnostics.Report(baseSyntax.NameToken.Location, $"struct '{type.Name}' may inherit from at most one struct");
                    else if (TypeIdentity.AreSame(baseStruct, type))
                        _diagnostics.Report(baseSyntax.NameToken.Location, $"struct '{type.Name}' cannot inherit from itself");
                    else
                        type.SetBaseType(baseStruct);
                }
                else if (resolved is InterfaceTypeSymbol @interface)
                    interfaces.Add(@interface);
                else if (!TypeIdentity.AreSame(resolved, BuiltinTypes.Error))
                    _diagnostics.Report(baseSyntax.NameToken.Location, $"'{baseSyntax.Name}' is not a struct or interface type");
            }
            type.SetInterfaces(interfaces.ToImmutable());
        }

        foreach ((InterfaceDeclarationSyntax declaration, InterfaceTypeSymbol type) in _interfaceSymbols)
        {
            var bases = ImmutableArray.CreateBuilder<InterfaceTypeSymbol>();
            foreach (TypeSyntax baseSyntax in declaration.BaseInterfaces)
            {
                TypeSymbol resolved = TypeResolver.Resolve(baseSyntax, _treeScopes.First(pair => ReferenceEquals(pair.Key.Root.Members.OfType<InterfaceDeclarationSyntax>().FirstOrDefault(d => ReferenceEquals(d, declaration)), declaration)).Value, _diagnostics);
                if (resolved is InterfaceTypeSymbol @interface && !TypeIdentity.AreSame(@interface, type))
                    bases.Add(@interface);
                else if (!TypeIdentity.AreSame(resolved, BuiltinTypes.Error))
                    _diagnostics.Report(baseSyntax.NameToken.Location, $"interface '{type.Name}' may inherit only from interfaces");
            }
            type.SetBaseInterfaces(bases.ToImmutable());
        }
    }

    private void ValidateInheritedInterfaceMembers()
    {
        foreach (InterfaceTypeSymbol type in _interfaceSymbols.Values)
        {
            foreach (var group in type.AllMethods.GroupBy(TypeSignature.Method))
            {
                FunctionSymbol first = group.First();
                if (group.Any(method => !TypeIdentity.AreSame(method.ReturnType, first.ReturnType) || method.IsReadonly != first.IsReadonly))
                    _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                        $"interface '{type.Name}' inherits incompatible member '{first.Name}'");
            }
            foreach (var group in type.AllProperties.GroupBy(property => property.Name))
            {
                InterfacePropertySymbol first = group.First();
                if (group.Any(property => !TypeIdentity.AreSame(property.Type, first.Type) ||
                    (property.Getter is null) != (first.Getter is null) || (property.Setter is null) != (first.Setter is null)) ||
                    type.SelfAndBaseInterfaces.SelectMany(parent => parent.Methods).Any(method => method.Name == first.Name))
                    _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                        $"interface '{type.Name}' inherits incompatible member '{first.Name}'");
            }
            foreach (var group in type.SelfAndBaseInterfaces.SelectMany(parent => parent.Indexers)
                .GroupBy(indexer => TypeSignature.Parameters(indexer.Parameters)))
            {
                InterfaceIndexerSymbol first = group.First();
                if (group.Any(indexer => !TypeIdentity.AreSame(indexer.Type, first.Type) ||
                    (indexer.Getter is null) != (first.Getter is null) || (indexer.Setter is null) != (first.Setter is null)))
                    _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                        $"interface '{type.Name}' inherits incompatible indexers");
            }
        }
    }

    private void DeclareInterfaceMethods()
    {
        foreach ((InterfaceDeclarationSyntax declaration, InterfaceTypeSymbol type) in _interfaceSymbols)
        {
            FileSymbolScope scope = _treeScopes.First(pair => pair.Key.Root.Members.Contains(declaration)).Value;
            var methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
            foreach (InterfaceMethodDeclarationSyntax syntax in declaration.Methods)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(syntax.Parameters, scope);
                if (methods.Any(m => m.Name == syntax.IdentifierToken.Text && HaveSameParameterTypes(m.Parameters, parameters)))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface '{type.Name}' already declares method '{syntax.IdentifierToken.Text}'");
                    continue;
                }
                methods.Add(new FunctionSymbol(syntax.IdentifierToken.Text, type, TypeResolver.ResolveReturnType(syntax.ReturnType, scope, _diagnostics), parameters, syntax));
            }
            type.SetMethods(methods.ToImmutable());

            var properties = ImmutableArray.CreateBuilder<InterfacePropertySymbol>();
            foreach (InterfacePropertyDeclarationSyntax syntax in declaration.Properties)
            {
                if (properties.Any(property => string.Equals(property.Name, syntax.IdentifierToken.Text, StringComparison.Ordinal)) ||
                    declaration.Methods.Any(method => string.Equals(method.IdentifierToken.Text, syntax.IdentifierToken.Text, StringComparison.Ordinal)))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface '{type.Name}' already declares member '{syntax.IdentifierToken.Text}'");
                    continue;
                }

                TypeSymbol propertyType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var property = new InterfacePropertySymbol(syntax.IdentifierToken.Text, type, propertyType, syntax);
                if (syntax.Accessors.Count(accessor => accessor.IsGetter) > 1)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface property '{property.Name}' declares more than one getter");
                if (syntax.Accessors.Count(accessor => accessor.IsSetter) > 1)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface property '{property.Name}' declares more than one setter");
                if (syntax.Getter is null && syntax.Setter is null)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface property '{property.Name}' must declare a getter or setter");

                FunctionSymbol? getter = syntax.Getter is null
                    ? null
                    : new FunctionSymbol($"get_{property.Name}", property, propertyType, [], syntax.Getter);
                FunctionSymbol? setter = syntax.Setter is null
                    ? null
                    : new FunctionSymbol(
                        $"set_{property.Name}",
                        property,
                        BuiltinTypes.Void,
                        [new ParameterSymbol("value", propertyType, 0)],
                        syntax.Setter);
                property.SetAccessors(getter, setter);
                properties.Add(property);
            }
            type.SetProperties(properties.ToImmutable());

            var indexers = ImmutableArray.CreateBuilder<InterfaceIndexerSymbol>();
            foreach (InterfaceIndexerDeclarationSyntax syntax in declaration.Indexers)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(syntax.Parameters, scope);
                if (parameters.IsEmpty)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "an indexer must declare at least one parameter");
                if (indexers.Any(candidate => HaveSameParameterTypes(candidate.Parameters, parameters)))
                {
                    _diagnostics.Report(syntax.ThisKeyword.Location, $"interface '{type.Name}' already declares an indexer with the same parameter types");
                    continue;
                }
                TypeSymbol indexerType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var indexer = new InterfaceIndexerSymbol(type, indexerType, parameters, syntax);
                FunctionSymbol? getter = syntax.Getter is null
                    ? null
                    : new FunctionSymbol(indexer.GetAccessorName(getter: true), indexer, indexerType, parameters, syntax.Getter);
                FunctionSymbol? setter = syntax.Setter is null
                    ? null
                    : new FunctionSymbol(
                        indexer.GetAccessorName(getter: false),
                        indexer,
                        BuiltinTypes.Void,
                        [.. parameters, new ParameterSymbol("value", indexerType, parameters.Length)],
                        syntax.Setter);
                if (getter is null && setter is null)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "an interface indexer must declare a getter or setter");
                indexer.SetAccessors(getter, setter);
                indexers.Add(indexer);
            }
            type.SetIndexers(indexers.ToImmutable());
        }
    }

    private void ValidateInheritanceCycles()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        {
            if (type.BaseType is not null && ReachesStruct(type.BaseType, type, []))
            {
                _diagnostics.Report(type.Declaration.IdentifierToken.Location, $"struct inheritance cycle involving '{type.Name}'");
                type.ClearBaseType();
            }
        }

        foreach (InterfaceTypeSymbol type in _interfaceSymbols.Values)
        {
            ImmutableArray<InterfaceTypeSymbol> validBases = type.BaseInterfaces
                .Where(baseType => !ReachesInterface(baseType, type, []))
                .ToImmutableArray();
            if (validBases.Length != type.BaseInterfaces.Length)
            {
                _diagnostics.Report(type.Declaration.IdentifierToken.Location, $"interface inheritance cycle involving '{type.Name}'");
                type.SetBaseInterfaces(validBases);
            }
        }
    }

    private static bool ReachesStruct(StructTypeSymbol current, StructTypeSymbol target, HashSet<StructTypeSymbol> visited)
    {
        if (TypeIdentity.AreSame(current, target))
            return true;
        return visited.Add(current) && current.BaseType is not null && ReachesStruct(current.BaseType, target, visited);
    }

    private static bool ReachesInterface(InterfaceTypeSymbol current, InterfaceTypeSymbol target, HashSet<InterfaceTypeSymbol> visited)
    {
        if (TypeIdentity.AreSame(current, target))
            return true;
        return visited.Add(current) && current.BaseInterfaces.Any(baseType => ReachesInterface(baseType, target, visited));
    }

    private void AssignInterfaceMethodSlots()
    {
        int dispatchId = 0;
        foreach (InterfaceTypeSymbol @interface in _interfaceSymbols.Values)
        {
            @interface.SetDispatchId(dispatchId++);
            @interface.SetMethodSlots(@interface.AllMethods);
        }
    }

    private void MarkVirtualDispatchRequirements()
    {
        // Dispatch is a declaration/inherited contract, not a property of the set
        // of known descendants. Propagate only downwards before assigning fields.
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            if (!type.Interfaces.IsEmpty || declaration.Methods.Any(method => method.IsVirtual || method.IsOverride || method.IsAbstract) ||
                declaration.Properties.Any(property => property.IsVirtual || property.IsOverride || property.IsAbstract) ||
                declaration.Indexers.Any(indexer => indexer.IsVirtual || indexer.IsOverride || indexer.IsAbstract) ||
                declaration.Destructor?.IsVirtual == true)
            {
                type.SetHasVirtualDispatch();
            }
        }

        bool changed;
        do
        {
            changed = false;
            foreach (StructTypeSymbol type in _structSymbols.Values.Where(type => type.BaseType?.HasVirtualDispatch == true && !type.HasVirtualDispatch))
            {
                type.SetHasVirtualDispatch();
                changed = true;
            }
        } while (changed);
    }

    private void BuildVirtualMethodTables()
    {
        var built = new HashSet<StructTypeSymbol>();
        foreach (StructTypeSymbol type in _structSymbols.Values)
            BuildVirtualMethodTable(type, built);
    }

    private void BuildVirtualMethodTable(StructTypeSymbol type, HashSet<StructTypeSymbol> built)
    {
        if (!built.Add(type))
            return;
        if (type.BaseType is not null)
            BuildVirtualMethodTable(type.BaseType, built);

        var slots = type.BaseType?.VirtualMethods.ToBuilder() ?? ImmutableArray.CreateBuilder<FunctionSymbol>();
        var invalidAccessors = new HashSet<FunctionSymbol>();
        foreach (PropertySymbol property in type.Properties)
        {
            PropertySymbol? inherited = type.BaseType?.FindProperty(property.Name);
            ValidateAccessorOverride($"property '{property.ToDisplayString(SymbolDisplayFormat.Diagnostic)}'", property.Locations[0],
                property.Declaration.IsOverride, property.Getter, property.Setter, inherited?.Getter, inherited?.Setter, invalidAccessors);
        }
        foreach (IndexerSymbol indexer in type.Indexers)
        {
            IndexerSymbol? inherited = type.BaseType?.AllIndexers.FirstOrDefault(candidate => HaveSameParameterTypes(indexer.Parameters, candidate.Parameters));
            ValidateAccessorOverride($"indexer '{indexer.ToDisplayString(SymbolDisplayFormat.Diagnostic)}'",
                indexer.Locations[0], indexer.Declaration.IsOverride,
                indexer.Getter, indexer.Setter, inherited?.Getter, inherited?.Setter, invalidAccessors);
        }
        foreach (FunctionSymbol method in type.Methods)
        {
            // Search inherited slots by member kind and the complete signature,
            // not the first declaration with this name in an intermediate type.
            FunctionSymbol? inherited = type.BaseType?.VirtualMethods.FirstOrDefault(method.HasSameSignature);
            if (invalidAccessors.Contains(method)) continue;
            if (method.ContainingProperty is null && method.ContainingIndexer is null && !ValidateMethodOverride(method, inherited)) continue;
            if (method.IsStatic) continue;
            if (method.IsOverride && inherited?.VTableSlot is int slot && method.Overrides(inherited))
            {
                method.SetVTableSlot(slot);
                slots[slot] = method;
            }
            else if (method.IsVirtual || method.IsAbstract)
            {
                method.SetVTableSlot(slots.Count);
                slots.Add(method);
            }
        }

        if (type.Destructor is FunctionSymbol destructor)
        {
            FunctionSymbol? inheritedDestructor = type.BaseType?.FindDestructor();
            int? inheritedSlot = inheritedDestructor?.VTableSlot;
            if (destructor.IsOverride && inheritedSlot is null)
                _diagnostics.Report(MemberLocation(destructor), $"destructor '{type.Name}' does not override a virtual base destructor");
            else if (!destructor.IsOverride && inheritedSlot is not null)
                _diagnostics.Report(MemberLocation(destructor), $"destructor '{type.FullName}' overrides an inherited virtual destructor and must be declared 'override'");
            else if (destructor.IsOverride && inheritedDestructor is not null && !HasCompatibleOverrideAccessibility(destructor, inheritedDestructor))
                _diagnostics.Report(MemberLocation(destructor), "an override cannot reduce the accessibility of its inherited member");
            else if (destructor.IsOverride && inheritedSlot is int slot)
            {
                destructor.SetVTableSlot(slot);
                slots[slot] = destructor;
            }
            else if (destructor.IsVirtual)
            {
                destructor.SetVTableSlot(slots.Count);
                slots.Add(destructor);
            }
        }
        type.SetVirtualMethods(slots.ToImmutable());
        if (!type.IsAbstract)
        {
            foreach (FunctionSymbol member in type.VirtualMethods.Where(method => method.IsAbstract)
                .DistinctBy(method => (object?)method.ContainingProperty ?? method.ContainingIndexer ?? (object)method))
                _diagnostics.Report(type.Declaration.IdentifierToken.Location,
                    $"concrete struct '{type.FullName}' does not implement abstract member '{MemberName(member)}'; implement it or declare the struct 'abstract'");
        }
    }

    private bool ValidateMethodOverride(FunctionSymbol method, FunctionSymbol? inherited)
    {
        if (method.IsStatic && (method.IsVirtual || method.IsOverride || method.IsAbstract))
        {
            _diagnostics.Report(MemberLocation(method), "static methods cannot be virtual, override, or abstract");
            return false;
        }
        if (method.IsOverride && (inherited is null || !method.Overrides(inherited)))
        {
            _diagnostics.Report(MemberLocation(method), $"method '{MemberName(method)}' does not override a compatible virtual or abstract base method");
            return false;
        }
        if (!method.IsOverride && inherited is not null)
        {
            _diagnostics.Report(MemberLocation(method), $"method '{MemberName(method)}' overrides inherited member '{MemberName(inherited)}' and must be declared 'override'");
            return false;
        }
        if (method.IsOverride && inherited is not null && !HasCompatibleOverrideAccessibility(method, inherited))
        {
            _diagnostics.Report(MemberLocation(method), "an override cannot reduce the accessibility of its inherited member");
            return false;
        }
        return true;
    }

    private void ValidateAccessorOverride(string name, TextLocation location, bool isOverride,
        FunctionSymbol? getter, FunctionSymbol? setter, FunctionSymbol? baseGetter, FunctionSymbol? baseSetter,
        HashSet<FunctionSymbol> invalid)
    {
        bool inheritedVirtual = baseGetter?.VTableSlot is not null || baseSetter?.VTableSlot is not null;
        string? diagnostic = !isOverride && inheritedVirtual
            ? $"{name} overrides an inherited virtual or abstract member and must be declared 'override'"
            : isOverride && (!inheritedVirtual || !Compatible(getter, baseGetter) || !Compatible(setter, baseSetter))
                ? $"{name} does not override a compatible virtual or abstract base member; type, readonly qualifier and getter/setter contract must match"
                : null;
        if (diagnostic is null) return;
        _diagnostics.Report(location, diagnostic);
        if (getter is not null) invalid.Add(getter);
        if (setter is not null) invalid.Add(setter);

        static bool Compatible(FunctionSymbol? accessor, FunctionSymbol? inherited) => accessor is null
            ? inherited is null
            : inherited?.VTableSlot is not null && accessor.Overrides(inherited) && HasCompatibleOverrideAccessibility(accessor, inherited);
    }

    private static bool HasCompatibleOverrideAccessibility(FunctionSymbol member, FunctionSymbol inherited) =>
        !inherited.IsPublic || member.IsPublic;

    private static TextLocation MemberLocation(FunctionSymbol method) => method.Declaration switch
    {
        MethodDeclarationSyntax => method.Locations[0],
        DestructorDeclarationSyntax syntax => (syntax.OverrideKeyword ?? syntax.IdentifierToken).Location,
        _ => method.ContainingType!.Declaration.IdentifierToken.Location,
    };

    private static string MemberName(FunctionSymbol method) =>
        (method.ContainingProperty as Symbol ?? method.ContainingIndexer as Symbol ?? method)
            .ToDisplayString(SymbolDisplayFormat.Diagnostic);

    private void ValidateStructLayouts()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        {
            foreach (FieldSymbol field in type.StaticFields)
                if (field.Declaration.Initializer is null && TypeFacts.ContainsReferenceStorage(field.Type))
                    _diagnostics.Report(field.Declaration.Type.NameToken.Location,
                        $"static field '{field.Name}' contains a reference and requires explicit initialization");
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

    private void ValidateAbstractValueStorage()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        foreach (FieldSymbol field in type.Fields.Concat(type.StaticFields))
            if (field.Type is StructTypeSymbol { IsAbstract: true } abstractType)
                _diagnostics.Report(field.Declaration.Type.NameToken.Location,
                    $"abstract struct '{abstractType.Name}' cannot be stored in field '{field.Name}'");
        var signatures = _functionBodies.Select(entry => (entry.Symbol, Location: entry.Body.OpenBraceToken.Location))
            .Concat(_functionSymbols.Select(entry => (entry.Value, entry.Key.IdentifierToken.Location)))
            .Concat(_structSymbols.Values.SelectMany(type => type.Methods.Select(method => (method, type.Declaration.IdentifierToken.Location))))
            .Concat(_interfaceSymbols.SelectMany(entry => entry.Value.AllMethods.Select(method => (method, entry.Key.IdentifierToken.Location))));
        foreach (var (symbol, location) in signatures.DistinctBy(entry => entry.Item1))
            if (symbol.ReturnType is StructTypeSymbol { IsAbstract: true } ||
                symbol.Parameters.Any(parameter => parameter.Type is StructTypeSymbol { IsAbstract: true }))
                _diagnostics.Report(location, "abstract structs cannot be passed or returned by value");
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

        if (TypeIdentity.AreSame(structType, target))
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
            FileSymbolScope scope = _structScopes[declaration];
            var methods = ImmutableArray.CreateBuilder<FunctionSymbol>();
            foreach (PropertySymbol property in type.Properties)
            {
                if (property.Getter is not null)
                    methods.Add(property.Getter);
                if (property.Setter is not null)
                    methods.Add(property.Setter);
            }
            foreach (IndexerSymbol indexer in type.Indexers)
            {
                if (indexer.Getter is not null)
                    methods.Add(indexer.Getter);
                if (indexer.Setter is not null)
                    methods.Add(indexer.Setter);
            }

            foreach (MethodDeclarationSyntax methodSyntax in declaration.Methods)
            {
                if (type.FindField(methodSyntax.IdentifierToken.Text) is not null)
                {
                    _diagnostics.Report(
                        methodSyntax.IdentifierToken.Location,
                        $"struct '{type.Name}' already contains field '{methodSyntax.IdentifierToken.Text}'");
                }

                if (type.FindProperty(methodSyntax.IdentifierToken.Text) is not null)
                {
                    _diagnostics.Report(
                        methodSyntax.IdentifierToken.Location,
                        $"struct '{type.Name}' already contains property '{methodSyntax.IdentifierToken.Text}'");
                }

                TypeSymbol returnType = TypeResolver.ResolveReturnType(
                    methodSyntax.ReturnType,
                    scope,
                    _diagnostics);
                ImmutableArray<ParameterSymbol> parameters = BindParameters(
                    methodSyntax.Parameters,
                    scope);

                var method = new FunctionSymbol(
                    methodSyntax.IdentifierToken.Text,
                    type,
                    returnType,
                    parameters,
                    methodSyntax);

                FunctionSymbol? sameName = methods.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, method.Name, StringComparison.Ordinal));
                if (sameName is not null && !CanFormReadonlyOverloadPair(sameName, method))
                {
                    _diagnostics.Report(
                        methodSyntax.IdentifierToken.Location,
                        $"method overloading is not supported yet; struct '{type.Name}' may declare only one method named '{method.Name}'");
                    continue;
                }

                methods.Add(method);
                if (methodSyntax.Body is not null)
                {
                    _functionBodies.Add((method, methodSyntax.Body, scope));
                }
            }

            type.SetMethods(methods.ToImmutable());
        }
    }

    private void DeclareStructProperties()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var properties = ImmutableArray.CreateBuilder<PropertySymbol>();
            foreach (PropertyDeclarationSyntax syntax in declaration.Properties)
            {
                if (properties.Any(property => string.Equals(property.Name, syntax.IdentifierToken.Text, StringComparison.Ordinal)))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"property '{syntax.IdentifierToken.Text}' is already declared in struct '{type.Name}'");
                    continue;
                }
                if (type.FindField(syntax.IdentifierToken.Text) is not null)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"struct '{type.Name}' already contains field '{syntax.IdentifierToken.Text}'");
                }
                if (syntax.IsStatic)
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, "static properties are not supported in this iteration");
                }

                TypeSymbol propertyType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var property = new PropertySymbol(
                    syntax.IdentifierToken.Text,
                    type,
                    propertyType,
                    syntax.IsPublic ? Accessibility.Public : Accessibility.Private,
                    syntax);

                PropertyAccessorDeclarationSyntax? getterSyntax = syntax.Getter;
                PropertyAccessorDeclarationSyntax? setterSyntax = syntax.Setter;
                if (syntax.Accessors.Count(accessor => accessor.IsGetter) > 1)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"property '{property.Name}' declares more than one getter");
                if (syntax.Accessors.Count(accessor => accessor.IsSetter) > 1)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"property '{property.Name}' declares more than one setter");
                if (getterSyntax is null && setterSyntax is null)
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"property '{property.Name}' must declare a getter or setter");

                FunctionSymbol? getter = getterSyntax is null
                    ? null
                    : new FunctionSymbol($"get_{property.Name}", property, propertyType, [], getterSyntax);
                FunctionSymbol? setter = setterSyntax is null
                    ? null
                    : new FunctionSymbol(
                        $"set_{property.Name}",
                        property,
                        BuiltinTypes.Void,
                        [new ParameterSymbol("value", propertyType, 0)],
                        setterSyntax);
                property.SetAccessors(getter, setter);
                properties.Add(property);

                AddPropertyAccessorBody(getter, getterSyntax, syntax, scope);
                AddPropertyAccessorBody(setter, setterSyntax, syntax, scope);
            }

            type.SetProperties(properties.ToImmutable());
        }
    }

    private void AddPropertyAccessorBody(
        FunctionSymbol? accessor,
        PropertyAccessorDeclarationSyntax? accessorSyntax,
        PropertyDeclarationSyntax propertySyntax,
        FileSymbolScope scope)
    {
        if (accessor is null || accessorSyntax is null)
            return;

        if (accessorSyntax.Body is not null)
        {
            if (propertySyntax.IsAbstract)
                _diagnostics.Report(accessorSyntax.KeywordToken.Location, "abstract property accessors cannot have a body");
            _functionBodies.Add((accessor, accessorSyntax.Body, scope));
        }
        else if (!propertySyntax.IsAbstract)
        {
            _diagnostics.Report(accessorSyntax.KeywordToken.Location, "property accessor without a body must be abstract");
        }
    }

    private void DeclareStructIndexers()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var indexers = ImmutableArray.CreateBuilder<IndexerSymbol>();
            foreach (IndexerDeclarationSyntax syntax in declaration.Indexers)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(syntax.Parameters, scope);
                if (parameters.IsEmpty)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "an indexer must declare at least one parameter");
                if (indexers.Any(candidate => HaveSameParameterTypes(candidate.Parameters, parameters)))
                {
                    _diagnostics.Report(syntax.ThisKeyword.Location, $"struct '{type.Name}' already declares an indexer with the same parameter types");
                    continue;
                }
                if (syntax.IsStatic)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "static indexers are not supported");

                TypeSymbol indexerType = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);
                var indexer = new IndexerSymbol(
                    type,
                    indexerType,
                    parameters,
                    syntax.IsPublic ? Accessibility.Public : Accessibility.Private,
                    syntax);
                FunctionSymbol? getter = syntax.Getter is null
                    ? null
                    : new FunctionSymbol(indexer.GetAccessorName(getter: true), indexer, indexerType, parameters, syntax.Getter);
                FunctionSymbol? setter = syntax.Setter is null
                    ? null
                    : new FunctionSymbol(
                        indexer.GetAccessorName(getter: false),
                        indexer,
                        BuiltinTypes.Void,
                        [.. parameters, new ParameterSymbol("value", indexerType, parameters.Length)],
                        syntax.Setter);
                if (getter is null && setter is null)
                    _diagnostics.Report(syntax.ThisKeyword.Location, "an indexer must declare a getter or setter");
                indexer.SetAccessors(getter, setter);
                indexers.Add(indexer);
                AddIndexerAccessorBody(getter, syntax.Getter, syntax, scope);
                AddIndexerAccessorBody(setter, syntax.Setter, syntax, scope);
            }
            type.SetIndexers(indexers.ToImmutable());
        }
    }

    private void AddIndexerAccessorBody(
        FunctionSymbol? accessor,
        PropertyAccessorDeclarationSyntax? accessorSyntax,
        IndexerDeclarationSyntax indexerSyntax,
        FileSymbolScope scope)
    {
        if (accessor is null || accessorSyntax is null)
            return;
        if (accessorSyntax.Body is not null)
        {
            if (indexerSyntax.IsAbstract)
                _diagnostics.Report(accessorSyntax.KeywordToken.Location, "abstract indexer accessors cannot have a body");
            _functionBodies.Add((accessor, accessorSyntax.Body, scope));
        }
        else if (!indexerSyntax.IsAbstract)
        {
            _diagnostics.Report(accessorSyntax.KeywordToken.Location, "indexer accessor without a body must be abstract");
        }
    }

    private static bool HaveSameParameterTypes(
        ImmutableArray<ParameterSymbol> first,
        ImmutableArray<ParameterSymbol> second) =>
        first.Length == second.Length &&
        first.Zip(second).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type));

    private static bool CanFormReadonlyOverloadPair(FunctionSymbol first, FunctionSymbol second) =>
        !first.IsStatic &&
        !second.IsStatic &&
        first.IsReadonly != second.IsReadonly &&
        first.Parameters.Length == second.Parameters.Length &&
        first.Parameters.Zip(second.Parameters).All(pair => TypeIdentity.AreSame(pair.First.Type, pair.Second.Type));

    private void ValidateInterfaceImplementations()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        {
            foreach (InterfaceTypeSymbol @interface in type.ImplementedInterfaces)
            {
                foreach (FunctionSymbol required in @interface.AllMethods)
                {
                    FunctionSymbol? implementation = type.FindInterfaceImplementation(required);
                    if (implementation is null || implementation.IsStatic || !implementation.IsPublic)
                        _diagnostics.Report(type.Declaration.IdentifierToken.Location, $"struct '{type.Name}' does not implement interface method '{@interface.Name}.{required.Name}'");
                }
            }
        }
    }

    private void DeclareStructLifecycleFunctions()
    {
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            FileSymbolScope scope = _structScopes[declaration];
            var constructors = ImmutableArray.CreateBuilder<FunctionSymbol>();
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            foreach (ConstructorDeclarationSyntax constructorSyntax in declaration.Constructors)
            {
                ImmutableArray<ParameterSymbol> parameters = BindParameters(constructorSyntax.Parameters, scope);
                string signature = TypeSignature.Parameters(parameters);
                var constructor = new FunctionSymbol(FunctionKind.Constructor, type, parameters, constructorSyntax,
                    constructorSyntax.IsPublic ? Accessibility.Public : Accessibility.Private);
                if (!signatures.Add(signature))
                {
                    _diagnostics.Report(constructor.Locations[0], $"constructor '{constructor.ToDisplayString(SymbolDisplayFormat.Diagnostic)}' is already declared");
                    continue;
                }
                constructors.Add(constructor);
                _functionBodies.Add((constructor, constructorSyntax.Body, scope));
            }
            if (constructors.Count == 0 && type.BaseType is not null)
            {
                // Use the same body binder as an explicit empty constructor, including
                // overload/access checks and the base-before-fields initialization order.
                var constructor = new FunctionSymbol(FunctionKind.Constructor, type, [], declaration, Accessibility.Public);
                constructors.Add(constructor);
                _functionBodies.Add((constructor,
                    new BlockStatementSyntax(declaration.OpenBraceToken, [], declaration.CloseBraceToken), scope));
            }
            type.SetConstructors(constructors.ToImmutable());

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
                _functionBodies.Add((destructor, destructorSyntax.Body, scope));
            }
        }
    }

    private void DeclareFunctions()
    {
        foreach (SyntaxTree tree in _syntaxTrees)
        {
            NamespaceSymbol @namespace = _treeNamespaces[tree];
            FileSymbolScope scope = _treeScopes[tree];
            foreach (FunctionDeclarationSyntax declaration in tree.Root.Members.OfType<FunctionDeclarationSyntax>())
            {
                if (declaration.IsExtern && declaration.IdentifierToken.Text is "malloc" or "free")
                {
                    _diagnostics.Report(
                        declaration.IdentifierToken.Location,
                        $"native symbol '{declaration.IdentifierToken.Text}' is reserved for Xenon memory operations");
                }

                TypeSymbol returnType = TypeResolver.ResolveReturnType(declaration.ReturnType, scope, _diagnostics);
                ImmutableArray<ParameterSymbol> parameters = BindParameters(declaration.Parameters, scope);
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
                    _functionBodies.Add((function, declaration.Body, scope));
                }
            }
        }
    }

    private void ValidateNativeSymbols()
    {
        var symbols = new Dictionary<string, FunctionSymbol>(StringComparer.Ordinal);
        IEnumerable<FunctionSymbol> functions = _functionSymbols.Values.Concat(_structSymbols.Values.SelectMany(type =>
            type.Methods.Concat(type.Constructors).Concat(new[] { type.Destructor, type.InstanceInitializer }.OfType<FunctionSymbol>())));
        foreach (FunctionSymbol function in functions)
        {
            string name = NativeSymbolNames.Get(function);
            if (symbols.TryAdd(name, function)) continue;
            FunctionSymbol previous = symbols[name];
            string? signature = NativeSymbolNames.GetAbiSignature(function, _constants.TargetLayout);
            string? previousSignature = NativeSymbolNames.GetAbiSignature(previous, _constants.TargetLayout);
            if (function.IsExtern && previous.IsExtern)
            {
                if (signature is null || previousSignature is null)
                {
                    _constants.RequireTargetLayout();
                    continue;
                }
                if (signature == previousSignature) continue;
            }
            TextLocation location = function.Declaration is FunctionDeclarationSyntax declaration
                ? declaration.IdentifierToken.Location : function.ContainingType!.Declaration.IdentifierToken.Location;
            _diagnostics.Report(location,
                $"native symbol '{name}' collides between '{previous.FullName}' and '{function.FullName}' with incompatible ABI or multiple definitions");
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
        FileSymbolScope scope)
    {
        var parameters = ImmutableArray.CreateBuilder<ParameterSymbol>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < parameterSyntax.Length; index++)
        {
            ParameterSyntax syntax = parameterSyntax[index];
            TypeSymbol type = TypeResolver.Resolve(syntax.Type, scope, _diagnostics);

            if (TypeIdentity.AreSame(type, BuiltinTypes.Void))
            {
                _diagnostics.Report(syntax.Type.NameToken.Location, "parameter type cannot be 'void'");
            }

            if (!names.Add(syntax.IdentifierToken.Text))
            {
                _diagnostics.Report(
                    syntax.IdentifierToken.Location,
                    $"parameter '{syntax.IdentifierToken.Text}' is already declared");
            }

            parameters.Add(new ParameterSymbol(syntax.IdentifierToken.Text, type, index, syntax.Type.IsBindingReadonly(), declaration: syntax));
        }

        return parameters.ToImmutable();
    }
}
