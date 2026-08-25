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

            extern int puts(const byte* text);

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
        Assert.True(parameter.Type.IsConst);
        Assert.Equal(1, parameter.Type.PointerDepth);

        var exported = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        Assert.True(exported.IsExport);
        Assert.NotNull(exported.Body);
        Assert.Equal(2, exported.Parameters.Length);
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
        Assert.True(heap.Type.IsUnsizedArray);
        Assert.True(Assert.IsType<NewExpressionSyntax>(heap.Initializer).IsArrayAllocation);
        var stack = Assert.IsType<VariableDeclarationStatementSyntax>(function.Body.Statements[1]);
        Assert.IsType<StackArrayCreationExpressionSyntax>(stack.Initializer);
        Assert.IsType<AssignmentExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(function.Body.Statements[2]).Expression);
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

    private static SyntaxTree Parse(string source) => SyntaxTree.Parse(SourceText.From(source, "test.xe"));
}
