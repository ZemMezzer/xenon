using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Syntax;

public sealed class ParserTests
{
    [Fact]
    public void Parser_BuildsMinimalCompilationUnit()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            int Main()
            {
                return 42;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal("Example", tree.Root.Namespace.Name);

        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        Assert.Equal("Main", function.IdentifierToken.Text);
        Assert.Equal(SyntaxKind.IntKeyword, function.ReturnType.NameToken.Kind);
        Assert.Empty(function.Parameters);

        var returnStatement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(function.Body!.Statements));
        var literal = Assert.IsType<LiteralExpressionSyntax>(returnStatement.Expression);
        Assert.Equal(42UL, literal.LiteralToken.Value);
    }

    [Fact]
    public void Parser_ParsesDottedNamespaceAndExternalAbiModifiers()
    {
        SyntaxTree tree = Parse("""
            namespace Example.Math;

            extern int puts(readonly byte* text);

            export int Add(int a, int b)
            {
                return a + b;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal("Example.Math", tree.Root.Namespace.Name);
        Assert.Equal(2, tree.Root.Members.Length);

        var external = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[0]);
        Assert.True(external.IsExtern);
        Assert.Null(external.Body);
        Assert.NotNull(external.SemicolonToken);
        ParameterSyntax parameter = Assert.Single(external.Parameters);
        Assert.True(parameter.Type.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null);
        Assert.Single(parameter.Type.ConstructionChain().OfType<PointerTypeSyntax>());

        var exported = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        Assert.True(exported.IsExport);
        Assert.NotNull(exported.Body);
        Assert.Equal(2, exported.Parameters.Length);
    }

    [Fact]
    public void Parser_ParsesMutableAndReadonlyReferenceTypes()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            void Read(Entity& value, readonly Entity& readOnly)
            {
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        Assert.True(function.Parameters[0].Type.Contains<ReferenceTypeSyntax>());
        Assert.False(function.Parameters[0].Type.GetQualifier(SyntaxKind.ConstKeyword) is not null);
        Assert.True(function.Parameters[1].Type.Contains<ReferenceTypeSyntax>());
        Assert.True(function.Parameters[1].Type.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null);
    }

    [Fact]
    public void Parser_ParsesPropertyAccessors()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            struct Player
            {
                int Health
                {
                    get { return 1; }
                    set { }
                }
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var type = Assert.IsType<StructDeclarationSyntax>(Assert.Single(tree.Root.Members));
        PropertyDeclarationSyntax property = Assert.Single(type.Properties);
        Assert.Equal("Health", property.IdentifierToken.Text);
        Assert.NotNull(property.Getter?.Body);
        Assert.NotNull(property.Setter?.Body);
    }

    [Fact]
    public void Parser_ParsesInterfaceProperty()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            interface IValue
            {
                int Value { get; set; }
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var type = Assert.IsType<InterfaceDeclarationSyntax>(Assert.Single(tree.Root.Members));
        InterfacePropertyDeclarationSyntax property = Assert.Single(type.Properties);
        Assert.Equal("Value", property.IdentifierToken.Text);
        Assert.NotNull(property.Getter);
        Assert.NotNull(property.Setter);
    }

    [Fact]
    public void Parser_ParsesMultiParameterStructAndInterfaceIndexers()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            interface IGrid
            {
                int this[int x, int y] { get; set; }
            }

            struct Grid
            {
                int this[int x, int y]
                {
                    get { return x + y; }
                    set { }
                }
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var contract = Assert.IsType<InterfaceDeclarationSyntax>(tree.Root.Members[0]);
        Assert.Equal(2, Assert.Single(contract.Indexers).Parameters.Length);
        var implementation = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[1]);
        Assert.Equal(2, Assert.Single(implementation.Indexers).Parameters.Length);
    }

    [Fact]
    public void Parser_ParsesModuleAndStructConstants()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            const int Global = 4;
            struct Values
            {
                const int Local = 8;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        Assert.IsType<ModuleConstantDeclarationSyntax>(tree.Root.Members[0]);
        var type = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[1]);
        Assert.Single(type.Constants);
    }

    [Fact]
    public void Parser_ParsesPrimitiveCastExpression()
    {
        SyntaxTree tree = Parse("""
            namespace Example;
            const int Value = cast<int>(cast<long>(42));
            """);

        Assert.Empty(tree.Diagnostics);
        var declaration = Assert.IsType<ModuleConstantDeclarationSyntax>(Assert.Single(tree.Root.Members));
        Assert.IsType<CastExpressionSyntax>(declaration.Initializer);
    }

    [Fact]
    public void Parser_RejectsBaseConstructorCallWithoutParentheses()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            struct Base { }
            struct Derived : Base
            {
                public Derived() : base { }
            }
            """);

        Assert.NotEmpty(tree.Diagnostics);
    }

    [Fact]
    public void Parser_ParsesUsingDirectivesBeforeNamespace()
    {
        SyntaxTree tree = Parse("""
            using Xenon.Math;
            using Vec = Xenon.Math.Vector2;
            using Gfx = Graphics;

            namespace Example;

            int Main()
            {
                return 0;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(3, tree.Root.Usings.Length);

        UsingDirectiveSyntax math = tree.Root.Usings[0];
        Assert.False(math.HasAlias);
        Assert.Equal("Xenon.Math", math.Name);

        UsingDirectiveSyntax vec = tree.Root.Usings[1];
        Assert.True(vec.HasAlias);
        Assert.Equal("Vec", vec.AliasToken!.Text);
        Assert.Equal("Xenon.Math.Vector2", vec.Name);

        UsingDirectiveSyntax gfx = tree.Root.Usings[2];
        Assert.Equal("Gfx", gfx.AliasToken!.Text);
        Assert.Equal("Graphics", gfx.Name);
        Assert.Equal("Example", tree.Root.Namespace.Name);
    }

    [Fact]
    public void Parser_ParsesQualifiedTypeNames()
    {
        SyntaxTree tree = Parse("""
            using Math = Xenon.Math;

            namespace Example;

            Math.Vector3 Build(Math.Vector3 value)
            {
                Math.Vector3 copy = Math.Vector3 { 1, 2, 3 };
                return copy;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        Assert.Equal("Math.Vector3", function.ReturnType.Name);
        Assert.Equal("Math.Vector3", Assert.Single(function.Parameters).Type.Name);
        var declaration = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body!.Statements[0]);
        Assert.Equal("Math.Vector3", declaration.Type.Name);
        Assert.IsType<StructPositionalConstructionExpressionSyntax>(declaration.Initializer);
    }

    [Fact]
    public void Parser_RejectsUsingDirectiveAfterNamespace()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            using Xenon.Math;

            int Main()
            {
                return 0;
            }
            """);

        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Message == "using directives must appear before the namespace declaration");
    }

    [Fact]
    public void Parser_ParsesTopLevelVisibilityModifiers()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            private int Hidden()
            {
                return 1;
            }

            public int Visible()
            {
                return 2;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(2, tree.Root.Members.Length);

        var hidden = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[0]);
        Assert.True(hidden.IsPrivate);
        Assert.False(hidden.IsPublic);

        var visible = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        Assert.True(visible.IsPublic);
        Assert.False(visible.IsPrivate);
    }

    [Fact]
    public void Parser_RespectsBinaryOperatorPrecedence()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            int Calculate(int a, int b)
            {
                return a + b * 2;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var statement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(function.Body!.Statements));
        var addition = Assert.IsType<BinaryExpressionSyntax>(statement.Expression);
        Assert.Equal(SyntaxKind.PlusToken, addition.OperatorToken.Kind);

        var multiplication = Assert.IsType<BinaryExpressionSyntax>(addition.Right);
        Assert.Equal(SyntaxKind.StarToken, multiplication.OperatorToken.Kind);
    }

    [Fact]
    public void Parser_ParsesVariablesCallsAndExpressionStatements()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            int Main()
            {
                int result = Add(20, 22);
                puts("Hello from Xenon");
                return result;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        Assert.Equal(3, function.Body!.Statements.Length);

        var variable = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body.Statements[0]);
        var initializer = Assert.IsType<CallExpressionSyntax>(variable.Initializer);
        Assert.Equal(2, initializer.Arguments.Length);

        var expressionStatement = Assert.IsType<ExpressionStatementSyntax>(function.Body.Statements[1]);
        Assert.IsType<CallExpressionSyntax>(expressionStatement.Expression);
    }

    [Fact]
    public void Parser_InsertsMissingTokensAndReportsDiagnostics()
    {
        SyntaxTree tree = Parse("""
            namespace Example

            int Main()
            {
                return 42
            }
            """);

        Assert.Equal(2, tree.Diagnostics.Length);
        Assert.True(tree.Root.Namespace.SemicolonToken.IsMissing);

        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var returnStatement = Assert.IsType<ReturnStatementSyntax>(Assert.Single(function.Body!.Statements));
        Assert.True(returnStatement.SemicolonToken.IsMissing);
    }

    [Fact]
    public void Parser_ParsesControlFlowAndPostfixIncrement()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            int Sum(int count)
            {
                int total = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i == 2)
                        continue;
                    else
                        total += i;
                }

                while (total < 100)
                {
                    total++;
                    if (total == 50)
                        break;
                }

                return total;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var @for = Assert.IsType<ForStatementSyntax>(function.Body!.Statements[1]);
        Assert.IsType<VariableDeclarationStatementSyntax>(@for.Initializer);
        Assert.IsType<BinaryExpressionSyntax>(@for.Condition);
        var increment = Assert.IsType<PostfixUnaryExpressionSyntax>(@for.Increment);
        Assert.Equal(SyntaxKind.PlusPlusToken, increment.OperatorToken.Kind);

        var forBody = Assert.IsType<BlockStatementSyntax>(@for.Body);
        var @if = Assert.IsType<IfStatementSyntax>(Assert.Single(forBody.Statements));
        Assert.IsType<ContinueStatementSyntax>(@if.ThenStatement);
        Assert.IsType<ExpressionStatementSyntax>(@if.ElseStatement);

        var @while = Assert.IsType<WhileStatementSyntax>(function.Body.Statements[2]);
        var whileBody = Assert.IsType<BlockStatementSyntax>(@while.Body);
        Assert.IsType<BreakStatementSyntax>(
            Assert.IsType<IfStatementSyntax>(whileBody.Statements[1]).ThenStatement);
    }

    [Fact]
    public void Parser_ParsesStructFieldsAndPointerMemberAccess()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            struct Vector2
            {
                float X;
                float Y;
            }

            export float Sum(Vector2* value)
            {
                return value->X + value->Y;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        Assert.Equal(2, tree.Root.Members.Length);
        var type = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]);
        Assert.Equal("Vector2", type.IdentifierToken.Text);
        Assert.Equal(["X", "Y"], type.Fields.Select(field => field.IdentifierToken.Text).ToArray());

        var function = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        Assert.Equal("Vector2", Assert.Single(function.Parameters).Type.NameToken.Text);
        var @return = Assert.IsType<ReturnStatementSyntax>(Assert.Single(function.Body!.Statements));
        var addition = Assert.IsType<BinaryExpressionSyntax>(@return.Expression);
        Assert.Equal(SyntaxKind.ArrowToken, Assert.IsType<MemberAccessExpressionSyntax>(addition.Left).OperatorToken.Kind);
        Assert.Equal(SyntaxKind.ArrowToken, Assert.IsType<MemberAccessExpressionSyntax>(addition.Right).OperatorToken.Kind);
    }

    [Fact]
    public void Parser_ParsesConstructorsDestructorsVisibilityAndPositionalConstruction()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            struct Vector3
            {
                private int X;
                public int Y;
                int Z;

                public Vector3(int x, int y, int z)
                {
                    X = x;
                    Y = y;
                    Z = z;
                }

                ~Vector3()
                {
                    X = 0;
                }
            }

            void Build(int x, int y, int z)
            {
                Vector3 positional = Vector3 { x, y, z };
                Vector3 value = Vector3(x, y, z);
                Vector3* heap = new Vector3(x, y, z);
                free(heap);
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var type = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]);
        Assert.Equal(3, type.Fields.Length);
        Assert.False(type.Fields[0].IsPublic);
        Assert.True(type.Fields[1].IsPublic);
        Assert.False(type.Fields[2].IsPublic);
        Assert.Single(type.Constructors);
        Assert.NotNull(type.Destructor);

        var function = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        var positional = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body!.Statements[0]);
        Assert.IsType<StructPositionalConstructionExpressionSyntax>(positional.Initializer);
        var constructor = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body.Statements[1]);
        Assert.IsType<CallExpressionSyntax>(constructor.Initializer);
        var heap = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body.Statements[2]);
        Assert.Equal(3, Assert.IsType<NewExpressionSyntax>(heap.Initializer).Arguments.Length);
    }

    [Fact]
    public void Parser_ParsesStructMethodsAndMethodCalls()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            struct Counter
            {
                int Value;

                public void Add(int amount)
                {
                    Value += amount;
                }

                int Read()
                {
                    return Value;
                }
            }

            int Main()
            {
                Counter value = Counter { 10 };
                value.Add(5);
                Counter* pointer = &value;
                pointer->Add(7);
                return value.Read();
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var type = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]);
        Assert.Equal(2, type.Methods.Length);

        MethodDeclarationSyntax add = type.Methods[0];
        Assert.True(add.IsPublic);
        Assert.Equal("Add", add.IdentifierToken.Text);
        Assert.Equal(SyntaxKind.VoidKeyword, add.ReturnType.NameToken.Kind);
        Assert.Single(add.Parameters);

        MethodDeclarationSyntax read = type.Methods[1];
        Assert.True(read.IsPrivate);
        Assert.Equal("Read", read.IdentifierToken.Text);

        var main = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        var valueCall = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(main.Body!.Statements[1]).Expression);
        var valueTarget = Assert.IsType<MemberAccessExpressionSyntax>(valueCall.Target);
        Assert.Equal(SyntaxKind.DotToken, valueTarget.OperatorToken.Kind);
        Assert.Equal("Add", valueTarget.MemberToken.Text);

        var pointerCall = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(main.Body.Statements[3]).Expression);
        var pointerTarget = Assert.IsType<MemberAccessExpressionSyntax>(pointerCall.Target);
        Assert.Equal(SyntaxKind.ArrowToken, pointerTarget.OperatorToken.Kind);
    }

    [Fact]
    public void Parser_ParsesHeapAndStackArrayCreation()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            void Build()
            {
                int[] heap = new int[10];
                int[] stack = int[10];
                heap[0] = stack[1];
                free(heap);
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var heap = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body!.Statements[0]);
        Assert.True(heap.Type.Contains<ArrayTypeSyntax>());
        Assert.True(Assert.IsType<NewExpressionSyntax>(heap.Initializer).IsArrayAllocation);
        var stack = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body.Statements[1]);
        Assert.IsType<StackArrayCreationExpressionSyntax>(stack.Initializer);
        Assert.IsType<AssignmentExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(function.Body.Statements[2]).Expression);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(40)]
    public void Parser_ParsesStackArrayDimensionsWithoutRankLimit(int rank)
    {
        string suffix = "[" + new string(',', rank - 1) + "]";
        string dimensions = string.Join(",", Enumerable.Repeat("2", rank));
        SyntaxTree tree = Parse($"namespace Example; void M() {{ int{suffix} values = int[{dimensions}]; }}");
        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var declaration = Assert.IsType<VariableDeclarationStatementSyntax>(Assert.Single(function.Body!.Statements));
        var allocation = Assert.IsType<StackArrayCreationExpressionSyntax>(declaration.Initializer);
        Assert.Equal(rank, allocation.Dimensions.Length);
        Assert.Equal(rank - 1, allocation.CommaTokens.Length);
    }

    [Fact]
    public void Parser_RejectsFixedSizeArrayTypeDeclarations()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            void Build()
            {
                int[16] values;
            }
            """);

        Assert.Contains(
            tree.Diagnostics,
            diagnostic => diagnostic.Message ==
                "fixed-size array type syntax is not supported; use 'T[]' and initialize it with 'T[n]' or 'new T[n]'");
    }

    [Fact]
    public void Parser_ParsesIterationFourDeclarationsAndRecursiveArrayShapes()
    {
        SyntaxTree tree = Parse("""
            namespace Example;
            enum State : byte { Idle, Running = 10, Stopped, }
            void Test(readonly int* readonly pointer)
            {
                int[][,,][] arrays = new int[2][,,][];
                switch (State.Idle) { case State.Idle: break; default: return; }
            }
            """);
        Assert.Empty(tree.Diagnostics);
        var enumeration = Assert.IsType<EnumDeclarationSyntax>(tree.Root.Members[0]);
        Assert.Equal(3, enumeration.Members.Length);
        Assert.Equal("byte", enumeration.UnderlyingType!.Name);
        var function = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        Assert.True(function.Parameters[0].Type.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null);
        Assert.True(function.Parameters[0].Type.IsBindingReadonly());
        var arrays = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body!.Statements[0]);
        Assert.Equal([1, 3, 1], arrays.Type.ConstructionChain().OfType<ArrayTypeSyntax>().Select(array => array.Rank).ToArray());
        Assert.Equal([3, 1], Assert.IsType<NewExpressionSyntax>(arrays.Initializer).Type.ConstructionChain().OfType<ArrayTypeSyntax>().Select(array => array.Rank).ToArray());
        Assert.Equal(2, Assert.IsType<SwitchStatementSyntax>(function.Body.Statements[1]).Sections.Length);
    }

    [Theory]
    [InlineData("enum E { A = }")]
    [InlineData("enum E { A A }")]
    [InlineData("void M() { switch (1) { case : break; }")]
    [InlineData("void M() { int[,, x values; }")]
    public void Parser_RecoversFromMalformedIterationFourSyntax(string declaration)
    {
        Assert.NotEmpty(Parse("namespace Example; " + declaration).Diagnostics);
    }

    [Theory]
    [InlineData("int", false, false)]
    [InlineData("readonly int", true, false)]
    [InlineData("int readonly", false, true)]
    [InlineData("void readonly", false, true)]
    [InlineData("int*", false, false)]
    [InlineData("readonly int*", true, false)]
    [InlineData("int* readonly", false, true)]
    [InlineData("readonly int* readonly", true, true)]
    [InlineData("readonly int& readonly", true, true)]
    [InlineData("int[,] readonly", false, true)]
    public void Parser_SeparatesReturnTypeAndMethodReadonly(
        string signature, bool returnReadonly, bool methodReadonly)
    {
        // Preserve the written qualifiers in syntax; semantic analysis rejects
        // readonly by-value returns such as 'readonly int'.
        SyntaxTree tree = Parse($$"""
            namespace Example;
            struct Value { public {{signature}} Get() { } }
            interface IValue { {{signature}} Get(); }
            """);

        Assert.Empty(tree.Diagnostics);
        var method = Assert.Single(Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]).Methods);
        var contract = Assert.Single(Assert.IsType<InterfaceDeclarationSyntax>(tree.Root.Members[1]).Methods);
        Assert.Equal(methodReadonly, method.IsReadonly);
        Assert.Equal(methodReadonly, contract.IsReadonly);
        Assert.Equal(returnReadonly, method.ReturnType.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null);
        Assert.Equal(returnReadonly, contract.ReturnType.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null);
        Assert.Null(method.ReturnType.GetQualifier(SyntaxKind.ReadonlyKeyword, TypeQualifierPosition.Postfix));
        Assert.Null(contract.ReturnType.GetQualifier(SyntaxKind.ReadonlyKeyword, TypeQualifierPosition.Postfix));
    }

    [Theory]
    [InlineData("int*", false, false)]
    [InlineData("readonly int*", true, false)]
    [InlineData("int* readonly", false, true)]
    [InlineData("readonly int* readonly", true, true)]
    public void Parser_KeepsPointerBindingReadonlyOnVariablesOnly(
        string signature, bool pointeeReadonly, bool bindingReadonly)
    {
        SyntaxTree tree = Parse($$"""
            namespace Example;
            struct Value { public {{signature}} Pointer; }
            void Use({{signature}} parameter) { {{signature}} local = parameter; }
            {{signature}} Get() { return null; }
            """);
        Assert.Empty(tree.Diagnostics);
        var field = Assert.Single(Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]).Fields);
        var use = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        var local = Assert.IsType<VariableDeclarationStatementSyntax>(Assert.Single(use.Body!.Statements));
        foreach (TypeSyntax type in new[] { field.Type, use.Parameters[0].Type, local.Type })
        {
            Assert.Equal(pointeeReadonly, type.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null);
            Assert.Equal(bindingReadonly, type.IsBindingReadonly());
        }
        var get = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[2]);
        Assert.Equal(bindingReadonly, get.IsReadonly);
        Assert.Equal(pointeeReadonly, get.ReturnType.GetQualifier(SyntaxKind.ReadonlyKeyword) is not null);
        Assert.Null(get.ReturnType.GetQualifier(SyntaxKind.ReadonlyKeyword, TypeQualifierPosition.Postfix));
    }

    [Theory]
    [InlineData("readonly readonly int&", "duplicate readonly return type qualifier")]
    [InlineData("int* readonly readonly", "duplicate readonly method qualifier")]
    [InlineData("int* readonly []", "return types cannot have a readonly pointer binding")]
    public void Parser_RejectsInvalidReadonlyReturnQualifiers(string signature, string diagnostic)
    {
        SyntaxTree tree = Parse($"namespace Example; struct Value {{ public {signature} Get() {{ }} }}");
        Assert.Contains(tree.Diagnostics, item => item.Message == diagnostic);
    }

    [Theory]
    [InlineData("static A() {}")]
    [InlineData("virtual A() {}")]
    [InlineData("readonly A() {}")]
    [InlineData("abstract A() {}")]
    [InlineData("override A() {}")]
    [InlineData("virtual int field;")]
    [InlineData("abstract int field;")]
    [InlineData("override int field;")]
    [InlineData("static ~A() {}")]
    [InlineData("readonly ~A() {}")]
    [InlineData("abstract ~A() {}")]
    [InlineData("public private int field;")]
    [InlineData("public public int field;")]
    [InlineData("static static int field;")]
    [InlineData("readonly readonly int field;")]
    [InlineData("virtual virtual void M() {}")]
    [InlineData("virtual override abstract void M();")]
    [InlineData("static virtual int Value { get { return 0; } }")]
    [InlineData("static override void M() {}")]
    [InlineData("virtual override int this[int i] { get { return i; } }")]
    [InlineData("int* readonly Value { get { return null; } }")]
    [InlineData("readonly int Value { set {} }")]
    [InlineData("readonly int this[int i] { set {} }")]
    [InlineData("int this[int i] { public get { return i; } }")]
    public void Parser_RejectsDiscardedDuplicateAndConflictingMemberModifiers(string member)
    {
        SyntaxTree tree = Parse("namespace Example; struct A { " + member + " }");
        Assert.NotEmpty(tree.Diagnostics);
        Assert.Contains(tree.Diagnostics, d => d.Message.Contains("modifier", StringComparison.Ordinal) ||
            d.Message.Contains("static members", StringComparison.Ordinal) || d.Message.Contains("readonly pointer binding", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_PreservesDestructorOverrideAndRejectsUnsupportedInterfaceModifiers()
    {
        SyntaxTree tree = Parse("namespace Example; struct A { public override ~A() {} }");
        Assert.Empty(tree.Diagnostics);
        var destructor = Assert.Single(Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]).Members.OfType<DestructorDeclarationSyntax>());
        Assert.True(destructor.IsOverride);
        Assert.Equal(SyntaxKind.OverrideKeyword, destructor.OverrideKeyword!.Kind);
        SyntaxTree invalid = Parse("namespace Example; interface I { static void M(); }");
        Assert.Contains(invalid.Diagnostics, d => d.Message.Contains("not allowed on a interface member", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("readonly int Value { set; }")]
    [InlineData("readonly int this[int i] { set; }")]
    [InlineData("int Value { public get; }")]
    [InlineData("int this[int i] { virtual get; }")]
    public void Parser_RejectsIgnoredInterfaceAccessorModifiers(string member)
    {
        SyntaxTree tree = Parse("namespace Example; interface I { " + member + " }");
        Assert.Contains(tree.Diagnostics, d => d.Message.Contains("modifier", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_PreservesExplicitAbstractStructModifier()
    {
        SyntaxTree tree = Parse("namespace Example; abstract struct A {} struct B : A {}");
        Assert.Empty(tree.Diagnostics);
        var abstractType = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]);
        Assert.True(abstractType.IsAbstract);
        Assert.Equal(SyntaxKind.AbstractKeyword, abstractType.AbstractKeyword!.Kind);
        Assert.False(Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[1]).IsAbstract);
        Assert.Contains(Parse("namespace Example; abstract abstract struct A {}").Diagnostics,
            d => d.Message == "duplicate abstract struct modifier");
    }

    [Theory]
    [InlineData("abstract override void M();", "mutually exclusive")]
    [InlineData("abstract override int Value { get; }", "mutually exclusive")]
    [InlineData("abstract override int this[int x] { get; }", "mutually exclusive")]
    [InlineData("abstract ~A();", "not allowed on a destructor")]
    public void Parser_ExplicitlyRejectsUnsupportedAbstractMemberForms(string member, string diagnostic)
    {
        SyntaxTree tree = Parse("namespace Example; abstract struct A { " + member + " }");
        Assert.Contains(tree.Diagnostics, d => d.Message.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_RepresentsEachTypeConstructorAndQualifierAsANode()
    {
        var tree = Parse("namespace Example; extern void F(readonly Foo** readonly&[][,] value);");
        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var prefix = Assert.IsType<QualifiedTypeSyntax>(Assert.Single(function.Parameters).Type);
        Assert.Equal(TypeQualifierPosition.Prefix, prefix.Position);
        var outer = Assert.IsType<ArrayTypeSyntax>(prefix.ElementType);
        Assert.Equal(1, outer.Rank);
        var inner = Assert.IsType<ArrayTypeSyntax>(outer.ElementType);
        Assert.Equal(2, inner.Rank);
        var reference = Assert.IsType<ReferenceTypeSyntax>(inner.ElementType);
        var binding = Assert.IsType<QualifiedTypeSyntax>(reference.ElementType);
        Assert.Equal(TypeQualifierPosition.Postfix, binding.Position);
        var pointer = Assert.IsType<PointerTypeSyntax>(binding.ElementType);
        var pointee = Assert.IsType<PointerTypeSyntax>(pointer.ElementType);
        Assert.Equal("Foo", Assert.IsType<NamedTypeSyntax>(pointee.ElementType).Name);
    }

    [Fact]
    public void Parser_ParsesNestedTypeArgumentsWithoutChangingShiftExpressions()
    {
        var tree = Parse("namespace Example; void F(Foo<Bar<int>, readonly Baz<byte*>[] > value) { Foo<Bar<int>> local; int shifted = 8 >> 1; }");
        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var named = Assert.IsType<NamedTypeSyntax>(Assert.Single(function.Parameters).Type);
        Assert.Equal(2, named.TypeArguments!.Arguments.Length);
        var nested = Assert.IsType<NamedTypeSyntax>(named.TypeArguments.Arguments[0]);
        Assert.Equal("int", Assert.Single(nested.TypeArguments!.Arguments).Name);
        Assert.IsType<QualifiedTypeSyntax>(named.TypeArguments.Arguments[1]);
        var local = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body!.Statements[0]);
        var outer = Assert.IsType<NamedTypeSyntax>(local.Type).TypeArguments!;
        var inner = Assert.IsType<NamedTypeSyntax>(Assert.Single(outer.Arguments)).TypeArguments!;
        Assert.Equal(inner.GreaterToken.Location.Span.Start + 1, outer.GreaterToken.Location.Span.Start);
        var shifted = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body.Statements[1]);
        Assert.Equal(SyntaxKind.GreaterGreaterToken, Assert.IsType<BinaryExpressionSyntax>(shifted.Initializer).OperatorToken.Kind);
    }

    [Theory]
    [InlineData("Foo<>")]
    [InlineData("Foo<int,>")]
    [InlineData("Foo<,int>")]
    [InlineData("Foo<Bar<int>")]
    public void Parser_RecoversMalformedTypeArgumentsAndPreservesFollowingDeclarations(string type)
    {
        var tree = Parse($"namespace Example; extern void F({type} value); int Next() {{ return 1; }}");
        Assert.NotEmpty(tree.Diagnostics);
        Assert.Contains(tree.Root.Members.OfType<FunctionDeclarationSyntax>(), function => function.IdentifierToken.Text == "Next");
    }

    [Fact]
    public void Parser_RecoversTypeArgumentListAtEndOfFile()
    {
        var tree = Parse("namespace Example; extern void F(Foo<Bar<int");
        Assert.NotEmpty(tree.Diagnostics);
        Assert.Equal(SyntaxKind.EndOfFileToken, tree.Root.EndOfFileToken.Kind);
    }

    [Fact]
    public void Parser_UsesGeneralTypeMembersForStructsAndInterfaces()
    {
        var tree = Parse("namespace Example; struct S { int x; const int N = 1; void F() {} } interface I { void F(); int P { get; } }");
        Assert.Empty(tree.Diagnostics);
        var structure = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]);
        Assert.All(structure.Members, member => Assert.IsAssignableFrom<TypeMemberDeclarationSyntax>(member));
        Assert.IsType<TypeConstantDeclarationSyntax>(structure.Members[1]);
        var contract = Assert.IsType<InterfaceDeclarationSyntax>(tree.Root.Members[1]);
        Assert.IsAssignableFrom<TypeMemberDeclarationSyntax>(Assert.Single(contract.Methods));
        Assert.IsAssignableFrom<TypeMemberDeclarationSyntax>(Assert.Single(contract.Properties));
    }

    [Theory]
    [InlineData("a < b && c > d;")]
    [InlineData("a < b || c > d;")]
    [InlineData("a < b == c > d;")]
    public void Parser_DoesNotConfuseRelationalExpressionsWithTypeArguments(string expression)
    {
        var tree = Parse($"namespace Example; void F() {{ {expression} }}");
        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        Assert.IsType<ExpressionStatementSyntax>(Assert.Single(function.Body!.Statements));
    }

    [Fact]
    public void Parser_ParsesGenericFunctionsStructsAndIndependentWhereClauses()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            struct Pair<TKey, TValue>
                where TKey : Hashable, Equalable
                where TValue : IEntity
            {
                TKey key;
                TValue value;
            }

            TResult Convert<TSource, TResult>(TSource value)
                where TSource : BaseEntity, IEntity
                where TResult : Constructible
            {
                TResult result;
                return result;
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var pair = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]);
        Assert.Equal(["TKey", "TValue"], pair.TypeParameters!.Parameters.Select(parameter => parameter.IdentifierToken.Text));
        Assert.Equal(2, pair.WhereClauses.Length);
        Assert.Equal(2, pair.WhereClauses[0].Constraints.Length);
        Assert.Equal("Hashable", pair.WhereClauses[0].Constraints[0].Type.Name);

        var convert = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        Assert.Equal(["TSource", "TResult"], convert.TypeParameters!.Parameters.Select(parameter => parameter.IdentifierToken.Text));
        Assert.Equal(2, convert.WhereClauses.Length);
        Assert.Equal("TResult", convert.WhereClauses[1].TypeParameterToken.Text);
    }

    [Fact]
    public void Parser_ParsesStructuralTemplateRequirements()
    {
        SyntaxTree tree = Parse("""
            namespace Example;

            template VectorLike
            {
                VectorLike();
                VectorLike(float x, float y, float z);
                float readonly Length();
                float X { get; }
                float this[int index] { get; set; }
            }
            """);

        Assert.Empty(tree.Diagnostics);
        var template = Assert.IsType<TemplateDeclarationSyntax>(Assert.Single(tree.Root.Members));
        Assert.Equal("VectorLike", template.IdentifierToken.Text);
        Assert.Equal(2, template.Constructors.Length);
        Assert.Equal(3, template.Constructors[1].Parameters.Length);
        Assert.True(Assert.Single(template.Methods).IsReadonly);
        Assert.Single(template.Properties);
        Assert.Single(template.Indexers);
    }

    [Fact]
    public void Parser_RejectsTemplateImplementationsAndIncorrectConstructorNames()
    {
        SyntaxTree tree = Parse("""
            namespace Example;
            template ExampleTemplate
            {
                WrongName(int value);
                void Execute() { }
                int Value { get { return 1; } }
            }
            """);

        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidTemplateConstructorName);
        Assert.Equal(2, tree.Diagnostics.Count(diagnostic => diagnostic.Id == DiagnosticIds.TemplateMemberBodyNotAllowed));
    }

    [Fact]
    public void Parser_ReportsDuplicateGenericParameters()
    {
        SyntaxTree tree = Parse("namespace Example; void Apply<T, T>(T value) { }");

        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.DuplicateGenericParameter);
    }

    [Fact]
    public void Parser_ParsesExplicitGenericFunctionCallsWithoutChangingNestedTypeArguments()
    {
        SyntaxTree tree = Parse("namespace Example; void Test() { Identity<int>(1); Convert<Box<int>, Pair<int, float>>(value); }");

        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var first = Assert.IsType<CallExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(function.Body!.Statements[0]).Expression);
        Assert.Equal("int", Assert.Single(first.TypeArguments!.Arguments).Name);
        var second = Assert.IsType<CallExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(function.Body.Statements[1]).Expression);
        Assert.Equal(2, second.TypeArguments!.Arguments.Length);
        Assert.NotNull(Assert.IsType<NamedTypeSyntax>(second.TypeArguments.Arguments[1]).TypeArguments);
    }

    private static SyntaxTree Parse(string source) => SyntaxTree.Parse(SourceText.From(source, "test.xe"));
}
