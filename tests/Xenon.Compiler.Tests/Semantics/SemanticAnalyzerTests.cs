using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class SemanticAnalyzerTests
{
    [Fact]
    public void Analyzer_BuildsStructuralNamespacesAcrossFiles()
    {
        Compilation compilation = CreateCompilation(
            """
            namespace Xenon.Math;

            int Add(int a, int b)
            {
                return a + b;
            }
            """,
            """
            namespace Xenon.Math;

            int Answer()
            {
                return Add(20, 22);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol xenon = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        NamespaceSymbol math = Assert.Single(xenon.Namespaces);
        Assert.Equal("Xenon.Math", math.FullName);
        Assert.Equal(2, math.Functions.Count);
        Assert.Equal(2, compilation.SemanticModel.Functions.Length);
    }

    [Fact]
    public void Analyzer_BindsCoreInteropProgram()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            extern int puts(const byte* text);

            int Main()
            {
                puts("Hello from Xenon");
                return 0;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction main = Assert.Single(compilation.SemanticModel.Functions);
        Assert.Equal("Example.Main", main.Symbol.FullName);

        var expressionStatement = Assert.IsType<BoundExpressionStatement>(main.Body.Statements[0]);
        var call = Assert.IsType<BoundCallExpression>(expressionStatement.Expression);
        Assert.True(call.Function.IsExtern);
        Assert.IsType<PointerTypeSymbol>(call.Arguments[0].Type);
    }

    [Fact]
    public void Analyzer_AssignsTypesToVariablesAndExpressions()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                int result = 20 + 22;
                return result;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction main = Assert.Single(compilation.SemanticModel.Functions);
        var declaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[0]);
        Assert.Same(BuiltinTypes.Int, declaration.Variable.Type);

        var initializer = Assert.IsType<BoundBinaryExpression>(declaration.Initializer);
        Assert.Same(BuiltinTypes.Int, initializer.Type);
    }

    [Fact]
    public void Analyzer_ReportsUnknownIdentifier()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return missing;
            }
            """);

        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("unknown identifier 'missing'", diagnostic.Message);
    }

    [Fact]
    public void Analyzer_ReportsInvalidReturnType()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                float value = 10.0f;
                return value;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "cannot implicitly convert 'float' to 'int'");
    }

    [Fact]
    public void Analyzer_ReportsWrongArgumentCountAndType()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Add(int a, int b)
            {
                return a + b;
            }

            int Main()
            {
                Add(10);
                Add(10, 2.0);
                return 0;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "function 'Add' expects 2 argument(s), but 1 were provided");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "cannot implicitly convert 'double' to 'int'");
    }

    [Fact]
    public void Analyzer_ReportsDuplicateFunctionsAndVariables()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                int value = 1;
                int value = 2;
                return value;
            }

            int Main()
            {
                return 0;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "function 'Example.Main' is already declared");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "variable 'value' is already declared in this scope");
    }

    [Fact]
    public void Analyzer_AllowsNullForPointerVariables()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                byte* pointer = null;
                return 0;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_ReportsMissingReturnValue()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                int value = 42;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "not all code paths in function 'Main' return a value");
    }

    [Fact]
    public void Analyzer_BindsControlFlowAndAcceptsReturningIfElse()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Choose(bool condition)
            {
                if (condition)
                    return 1;
                else
                    return 2;
            }

            int Sum(int count)
            {
                int total = 0;
                for (int i = 0; i < count; i++)
                {
                    if (i == 2)
                        continue;

                    total += i;
                }

                while (total > 100)
                    break;

                return total;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction choose = compilation.SemanticModel.Functions[0];
        var @if = Assert.IsType<BoundIfStatement>(Assert.Single(choose.Body.Statements));
        Assert.IsType<BoundReturnStatement>(@if.ThenStatement);
        Assert.IsType<BoundReturnStatement>(@if.ElseStatement);

        BoundFunction sum = compilation.SemanticModel.Functions[1];
        Assert.IsType<BoundForStatement>(sum.Body.Statements[1]);
        Assert.IsType<BoundWhileStatement>(sum.Body.Statements[2]);
    }

    [Fact]
    public void Analyzer_ReportsInvalidLoopUsageAndConditionTypes()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                if (1)
                    break;

                while (2)
                {
                }

                continue;

                for (int i = 0; 3; i++)
                {
                }

                return i;
            }
            """);

        Assert.Equal(
            3,
            compilation.Diagnostics.Count(diagnostic =>
                diagnostic.Message == "condition must have type 'bool', but has type 'int'"));
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "'break' can only be used inside a loop");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "'continue' can only be used inside a loop");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "unknown identifier 'i'");
    }

    [Fact]
    public void Analyzer_BindsStructValueAndPointerMemberAccess()
    {
        Compilation compilation = CreateCompilation("""
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

            Vector2 Copy(Vector2 value)
            {
                Vector2 copy = value;
                copy.X = value.Y;
                return copy;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol example = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol vector = Assert.Single(example.Types);
        Assert.Equal("Example.Vector2", vector.FullName);
        Assert.Equal(["X", "Y"], vector.Fields.Select(field => field.Name).ToArray());

        BoundFunction sum = compilation.SemanticModel.Functions[0];
        var @return = Assert.IsType<BoundReturnStatement>(Assert.Single(sum.Body.Statements));
        var addition = Assert.IsType<BoundBinaryExpression>(@return.Expression);
        Assert.True(Assert.IsType<BoundMemberAccessExpression>(addition.Left).IsPointerAccess);

        BoundFunction copy = compilation.SemanticModel.Functions[1];
        var assignment = Assert.IsType<BoundExpressionStatement>(copy.Body.Statements[1]);
        Assert.IsType<BoundMemberAccessExpression>(
            Assert.IsType<BoundAssignmentExpression>(assignment.Expression).Target);
    }

    [Fact]
    public void Analyzer_RejectsRecursiveByValueStruct()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Node
            {
                Node Next;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message ==
                "struct 'Node' has a recursive by-value field 'Next'; use a pointer instead");
    }

    [Fact]
    public void Analyzer_RejectsWritesThroughConstStructPointer()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Vector2
            {
                float X;
                float Y;
            }

            void Mutate(const Vector2* value)
            {
                value->X = 1.0f;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "left side of assignment must be writable");
    }

    [Fact]
    public void Analyzer_RequiresPointersForStructsInExternalAbi()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Vector2
            {
                float X;
                float Y;
            }

            export Vector2 Copy(Vector2 value)
            {
                return value;
            }
            """);

        Assert.Equal(
            2,
            compilation.Diagnostics.Count(diagnostic => diagnostic.Message ==
                "external ABI does not yet support struct 'Vector2' by value; use a pointer instead"));
    }

    [Fact]
    public void Analyzer_BindsStackConstructionNewAndFree()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Vector3
            {
                float X;
                float Y;
                float Z;
            }

            void Build(float x, float y, float z)
            {
                Vector3 stack = Vector3(x, y, z);
                Vector3* heap = new Vector3(x, y, z);
                free(heap);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction function = Assert.Single(compilation.SemanticModel.Functions);
        Assert.IsType<BoundStructConstructionExpression>(
            Assert.IsType<BoundVariableDeclarationStatement>(function.Body.Statements[0]).Initializer);
        var allocation = Assert.IsType<BoundNewExpression>(
            Assert.IsType<BoundVariableDeclarationStatement>(function.Body.Statements[1]).Initializer);
        Assert.IsType<PointerTypeSymbol>(allocation.Type);
        Assert.IsType<BoundFreeExpression>(
            Assert.IsType<BoundExpressionStatement>(function.Body.Statements[2]).Expression);
    }

    [Fact]
    public void Analyzer_ValidatesStructConstructorAndFreeArguments()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Pair
            {
                int X;
                int Y;
            }

            void Invalid()
            {
                Pair pair = Pair(1);
                Pair* heap = new Pair(1, 2.0);
                free(pair);
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message ==
                "struct 'Pair' expects 2 constructor argument(s), but 1 were provided");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "cannot implicitly convert 'double' to 'int'");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "'free' requires a pointer, but has type 'Pair'");
    }

    [Fact]
    public void Analyzer_ReservesNativeMallocForNew()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            extern void* malloc(nuint size);
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message ==
                "native symbol 'malloc' is reserved for the built-in 'new' operation");
    }

    private static Compilation CreateCompilation(params string[] sources) => Compilation.Create(
        sources.Select((source, index) => SourceText.From(source, $"test{index}.xe")).ToArray());
}
