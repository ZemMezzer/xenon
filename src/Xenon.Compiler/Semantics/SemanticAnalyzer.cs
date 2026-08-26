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
    private readonly Dictionary<SyntaxTree, FileSymbolScope> _treeScopes = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionDeclarationSyntax, FunctionSymbol> _functionSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructDeclarationSyntax, StructTypeSymbol> _structSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<InterfaceDeclarationSyntax, InterfaceTypeSymbol> _interfaceSymbols = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<StructDeclarationSyntax, FileSymbolScope> _structScopes = new(ReferenceEqualityComparer.Instance);
    private readonly List<(FunctionSymbol Symbol, BlockStatementSyntax Body, FileSymbolScope Scope)> _functionBodies = [];

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
        DeclareInterfaces();
        BindUsingDirectives();
        BindTypeInheritance();
        ValidateInheritanceCycles();
        MarkVirtualDispatchRequirements();
        DeclareInterfaceMethods();
        AssignInterfaceMethodSlots();
        BindStructFields();
        ValidateStructLayouts();
        DeclareStructMethods();
        DeclareStructLifecycleFunctions();
        BuildVirtualMethodTables();
        ValidateMethodOverridesAndInterfaces();
        DeclareFunctions();

        var functions = ImmutableArray.CreateBuilder<BoundFunction>();
        foreach ((FunctionSymbol symbol, BlockStatementSyntax body, FileSymbolScope scope) in _functionBodies)
        {
            var binder = new FunctionBodyBinder(symbol, scope, _diagnostics);
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
            var scope = new FileSymbolScope(_globalNamespace, _treeNamespaces[tree]);
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

                object? constantValue = null;
                if (fieldSyntax.IsStatic && fieldSyntax.Initializer is not null && !TryEvaluateConstant(fieldSyntax.Initializer, out constantValue))
                {
                    _diagnostics.Report(fieldSyntax.IdentifierToken.Location, "static field initializers must be compile-time constants");
                }
                else if (fieldSyntax.IsStatic && fieldSyntax.Initializer is not null)
                {
                    TypeSymbol constantType = GetConstantExpressionType(fieldSyntax.Initializer);
                    if (ReferenceEquals(constantType, BuiltinTypes.Error) || !TypeFacts.CanAssign(fieldType, constantType))
                    {
                        _diagnostics.Report(fieldSyntax.IdentifierToken.Location, $"cannot implicitly convert '{constantType.Name}' to '{fieldType.Name}'");
                    }
                    else if (!IsSupportedStaticInitializer(fieldType, constantValue))
                    {
                        _diagnostics.Report(fieldSyntax.IdentifierToken.Location, $"static field type '{fieldType.Name}' does not support this constant initializer");
                    }
                }

                var field = new FieldSymbol(
                    fieldSyntax.IdentifierToken.Text,
                    type,
                    fieldType,
                    fieldSyntax.IsStatic ? staticFields.Count : (type.BaseType?.AllInstanceFields.Length ?? 0) + (type.HasVirtualDispatch ? 1 : 0) + fields.Count,
                    fieldSyntax.IsPublic ? Accessibility.Public : Accessibility.Private,
                    fieldSyntax.IsStatic,
                    constantValue,
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

    private static bool IsSupportedStaticInitializer(TypeSymbol type, object? value) =>
        value is null ||
        (ReferenceEquals(type, BuiltinTypes.Bool) && value is bool) ||
        (type is PrimitiveTypeSymbol { IsInteger: true } && value is not bool) ||
        type is PrimitiveTypeSymbol { IsFloatingPoint: true };

    private static bool TryEvaluateConstant(ExpressionSyntax syntax, out object? value)
    {
        try
        {
            switch (syntax)
            {
                case LiteralExpressionSyntax literal:
                    value = GetConstantLiteralValue(literal);
                    return true;
                case ParenthesizedExpressionSyntax parenthesized:
                    return TryEvaluateConstant(parenthesized.Expression, out value);
                case UnaryExpressionSyntax unary when TryEvaluateConstant(unary.Operand, out object? operand):
                    return TryEvaluateUnaryConstant(unary.OperatorToken.Kind, operand, out value);
                case BinaryExpressionSyntax binary when
                    TryEvaluateConstant(binary.Left, out object? left) &&
                    TryEvaluateConstant(binary.Right, out object? right):
                    return TryEvaluateBinaryConstant(left, binary.OperatorToken.Kind, right, out value);
                default:
                    value = null;
                    return false;
            }
        }
        catch (Exception exception) when (exception is OverflowException or DivideByZeroException or InvalidCastException or FormatException)
        {
            value = null;
            return false;
        }
    }

    private static object? GetConstantLiteralValue(LiteralExpressionSyntax literal) => literal.LiteralToken switch
    {
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

    private static TypeSymbol GetConstantExpressionType(ExpressionSyntax syntax) => syntax switch
    {
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken, LiteralToken.Value: ulong value } when value <= int.MaxValue => BuiltinTypes.Int,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken, LiteralToken.Value: ulong value } when value <= long.MaxValue => BuiltinTypes.Long,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.IntegerLiteralToken } => BuiltinTypes.ULong,
        LiteralExpressionSyntax { LiteralToken.Value: float } => BuiltinTypes.Float,
        LiteralExpressionSyntax { LiteralToken.Value: double } => BuiltinTypes.Double,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.TrueKeyword or SyntaxKind.FalseKeyword } => BuiltinTypes.Bool,
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.StringLiteralToken } => BuiltinTypes.PointerTo(BuiltinTypes.Byte, isConst: true),
        LiteralExpressionSyntax { LiteralToken.Kind: SyntaxKind.NullKeyword } => BuiltinTypes.Null,
        ParenthesizedExpressionSyntax parenthesized => GetConstantExpressionType(parenthesized.Expression),
        UnaryExpressionSyntax { OperatorToken.Kind: SyntaxKind.BangToken } => BuiltinTypes.Bool,
        UnaryExpressionSyntax unary => GetConstantExpressionType(unary.Operand),
        BinaryExpressionSyntax binary when binary.OperatorToken.Kind is
            SyntaxKind.EqualsEqualsToken or SyntaxKind.BangEqualsToken or
            SyntaxKind.LessToken or SyntaxKind.LessOrEqualsToken or
            SyntaxKind.GreaterToken or SyntaxKind.GreaterOrEqualsToken or
            SyntaxKind.AmpersandAmpersandToken or SyntaxKind.PipePipeToken => BuiltinTypes.Bool,
        BinaryExpressionSyntax binary when ReferenceEquals(GetConstantExpressionType(binary.Left), GetConstantExpressionType(binary.Right)) => GetConstantExpressionType(binary.Left),
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
                    else if (ReferenceEquals(baseStruct, type))
                        _diagnostics.Report(baseSyntax.NameToken.Location, $"struct '{type.Name}' cannot inherit from itself");
                    else
                        type.SetBaseType(baseStruct);
                }
                else if (resolved is InterfaceTypeSymbol @interface)
                    interfaces.Add(@interface);
                else if (!ReferenceEquals(resolved, BuiltinTypes.Error))
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
                if (resolved is InterfaceTypeSymbol @interface && !ReferenceEquals(@interface, type))
                    bases.Add(@interface);
                else if (!ReferenceEquals(resolved, BuiltinTypes.Error))
                    _diagnostics.Report(baseSyntax.NameToken.Location, $"interface '{type.Name}' may inherit only from interfaces");
            }
            type.SetBaseInterfaces(bases.ToImmutable());
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
                if (methods.Any(m => m.Name == syntax.IdentifierToken.Text))
                {
                    _diagnostics.Report(syntax.IdentifierToken.Location, $"interface '{type.Name}' already declares method '{syntax.IdentifierToken.Text}'");
                    continue;
                }
                methods.Add(new FunctionSymbol(syntax.IdentifierToken.Text, type, TypeResolver.Resolve(syntax.ReturnType, scope, _diagnostics), BindParameters(syntax.Parameters, scope), syntax));
            }
            type.SetMethods(methods.ToImmutable());
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
        if (ReferenceEquals(current, target))
            return true;
        return visited.Add(current) && current.BaseType is not null && ReachesStruct(current.BaseType, target, visited);
    }

    private static bool ReachesInterface(InterfaceTypeSymbol current, InterfaceTypeSymbol target, HashSet<InterfaceTypeSymbol> visited)
    {
        if (ReferenceEquals(current, target))
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
        foreach ((StructDeclarationSyntax declaration, StructTypeSymbol type) in _structSymbols)
        {
            if (declaration.Methods.Any(method => method.IsVirtual || method.IsOverride || method.IsAbstract) ||
                declaration.Destructor?.IsVirtual == true)
            {
                MarkBaseChainForVirtualDispatch(type);
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

    private static void MarkBaseChainForVirtualDispatch(StructTypeSymbol? type)
    {
        for (StructTypeSymbol? current = type; current is not null; current = current.BaseType)
            current.SetHasVirtualDispatch();
    }

    private void BuildVirtualMethodTables()
    {
        var built = new HashSet<StructTypeSymbol>();
        foreach (StructTypeSymbol type in _structSymbols.Values)
            BuildVirtualMethodTable(type, built);
    }

    private static void BuildVirtualMethodTable(StructTypeSymbol type, HashSet<StructTypeSymbol> built)
    {
        if (!built.Add(type))
            return;
        if (type.BaseType is not null)
            BuildVirtualMethodTable(type.BaseType, built);

        var slots = type.BaseType?.VirtualMethods.ToBuilder() ?? ImmutableArray.CreateBuilder<FunctionSymbol>();
        foreach (FunctionSymbol method in type.Methods)
        {
            FunctionSymbol? inherited = type.BaseType?.FindMethod(method.Name);
            if (method.IsOverride && inherited?.VTableSlot is int slot)
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
            int? inheritedSlot = type.BaseType?.FindDestructor()?.VTableSlot;
            if (inheritedSlot is int slot)
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
            FileSymbolScope scope = _structScopes[declaration];
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

                methods.Add(method);
                if (methodSyntax.Body is not null)
                {
                    _functionBodies.Add((method, methodSyntax.Body, scope));
                }
            }

            type.SetMethods(methods.ToImmutable());
        }
    }

    private void ValidateMethodOverridesAndInterfaces()
    {
        foreach (StructTypeSymbol type in _structSymbols.Values)
        {
            foreach (FunctionSymbol method in type.Methods)
            {
                FunctionSymbol? inherited = type.BaseType?.FindMethod(method.Name);
                if (method.IsOverride)
                {
                    if (inherited is null || (!inherited.IsVirtual && !inherited.IsAbstract) || !method.Overrides(inherited))
                        _diagnostics.Report(method.Declaration is MethodDeclarationSyntax syntax ? syntax.IdentifierToken.Location : type.Declaration.IdentifierToken.Location, $"method '{method.Name}' does not override a compatible virtual or abstract base method");
                }
                else if (inherited is not null && (inherited.IsVirtual || inherited.IsAbstract))
                    _diagnostics.Report(method.Declaration is MethodDeclarationSyntax syntax ? syntax.IdentifierToken.Location : type.Declaration.IdentifierToken.Location, $"method '{method.Name}' hides an inherited virtual method; use 'override'");

                if (method.IsStatic && (method.IsVirtual || method.IsOverride || method.IsAbstract))
                    _diagnostics.Report(method.Declaration is MethodDeclarationSyntax syntax ? syntax.IdentifierToken.Location : type.Declaration.IdentifierToken.Location, "static methods cannot be virtual, override, or abstract");
                if (method.IsAbstract && method.Declaration is MethodDeclarationSyntax { Body: not null } abstractSyntax)
                    _diagnostics.Report(abstractSyntax.IdentifierToken.Location, "abstract methods cannot have a body");
            }

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
                string signature = string.Join(",", parameters.Select(parameter => parameter.Type.Name));
                if (!signatures.Add(signature))
                {
                    _diagnostics.Report(constructorSyntax.IdentifierToken.Location, $"constructor '{type.Name}({signature})' is already declared");
                    continue;
                }
                var constructor = new FunctionSymbol(FunctionKind.Constructor, type, parameters, constructorSyntax,
                    constructorSyntax.IsPublic ? Accessibility.Public : Accessibility.Private);
                constructors.Add(constructor);
                _functionBodies.Add((constructor, constructorSyntax.Body, scope));
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

                TypeSymbol returnType = TypeResolver.Resolve(declaration.ReturnType, scope, _diagnostics);
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
