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
    public void Analyzer_ContextuallyTypesNullAsPointer()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Holder
            {
                public int* Value;
            }

            struct Box
            {
                public int* Value;

                public Box(int* value)
                {
                    Value = value;
                }
            }

            int* ReturnNull()
            {
                return null;
            }

            void Consume(int* value)
            {
            }

            int Main()
            {
                int* pointer = null;
                pointer = null;
                bool equals = pointer == null;
                bool notEquals = null != pointer;
                Consume(null);
                Holder holder = Holder { null };
                Box box = Box(null);
                Box* heap = new Box(null);
                free(heap);
                if (equals && !notEquals)
                    return 0;

                return 1;
            }
            """);

        Assert.Empty(compilation.Diagnostics);

        BoundFunction returnNull = Assert.Single(
            compilation.SemanticModel.Functions.Where(function => function.Symbol.Name == "ReturnNull"));
        var returnStatement = Assert.IsType<BoundReturnStatement>(Assert.Single(returnNull.Body.Statements));
        Assert.IsType<PointerTypeSymbol>(returnStatement.Expression!.Type);

        BoundFunction main = Assert.Single(
            compilation.SemanticModel.Functions.Where(function => function.Symbol.Name == "Main"));
        var declaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[0]);
        Assert.IsType<PointerTypeSymbol>(declaration.Initializer!.Type);

        var equalsDeclaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[2]);
        var equals = Assert.IsType<BoundBinaryExpression>(equalsDeclaration.Initializer);
        Assert.IsType<PointerTypeSymbol>(equals.Right.Type);

        var notEqualsDeclaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[3]);
        var notEquals = Assert.IsType<BoundBinaryExpression>(notEqualsDeclaration.Initializer);
        Assert.IsType<PointerTypeSymbol>(notEquals.Left.Type);

        var callStatement = Assert.IsType<BoundExpressionStatement>(main.Body.Statements[4]);
        var call = Assert.IsType<BoundCallExpression>(callStatement.Expression);
        Assert.IsType<PointerTypeSymbol>(Assert.Single(call.Arguments).Type);

        var holderDeclaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[5]);
        var holder = Assert.IsType<BoundStructConstructionExpression>(holderDeclaration.Initializer);
        Assert.IsType<PointerTypeSymbol>(Assert.Single(holder.Arguments).Type);

        var boxDeclaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[6]);
        var box = Assert.IsType<BoundConstructorCallExpression>(boxDeclaration.Initializer);
        Assert.IsType<PointerTypeSymbol>(Assert.Single(box.Arguments).Type);

        var heapDeclaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[7]);
        var heap = Assert.IsType<BoundNewExpression>(heapDeclaration.Initializer);
        Assert.IsType<PointerTypeSymbol>(Assert.Single(heap.Arguments).Type);
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
                public float X;
                public float Y;
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
                "struct 'Node' has a recursive by-value field 'Next'; use a pointer or array handle instead");
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
    public void Analyzer_BindsConstructorsPositionalConstructionDestructorAndFree()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Vector3
            {
                int X;
                int Y;
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

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol example = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol vector = Assert.Single(example.Types);
        Assert.NotNull(vector.Constructor);
        Assert.NotNull(vector.Destructor);

        BoundFunction function = Assert.Single(compilation.SemanticModel.Functions.Where(f => f.Symbol.Name == "Build"));
        Assert.IsType<BoundStructConstructionExpression>(
            Assert.IsType<BoundVariableDeclarationStatement>(function.Body.Statements[0]).Initializer);
        Assert.IsType<BoundConstructorCallExpression>(
            Assert.IsType<BoundVariableDeclarationStatement>(function.Body.Statements[1]).Initializer);
        var allocation = Assert.IsType<BoundNewExpression>(
            Assert.IsType<BoundVariableDeclarationStatement>(function.Body.Statements[2]).Initializer);
        Assert.NotNull(allocation.Constructor);
        var free = Assert.IsType<BoundFreeExpression>(
            Assert.IsType<BoundExpressionStatement>(function.Body.Statements[3]).Expression);
        Assert.Same(vector.Destructor, free.Destructor);
    }

    [Fact]
    public void Analyzer_FieldsAndFunctionsDefaultToPrivate()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Pair
            {
                int X;
                public int Y;
            }

            int Hidden() { return 1; }
            public int Visible() { return 2; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol example = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol pair = Assert.Single(example.Types);
        Assert.False(pair.Fields[0].IsPublic);
        Assert.True(pair.Fields[1].IsPublic);
        FunctionSymbol hidden = Assert.Single(example.Functions.Where(function => function.Name == "Hidden"));
        FunctionSymbol visible = Assert.Single(example.Functions.Where(function => function.Name == "Visible"));
        Assert.False(hidden.IsPublic);
        Assert.True(visible.IsPublic);
    }

    [Fact]
    public void Analyzer_RejectsPrivateFieldAccessOutsideStruct()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Pair
            {
                int X;
                public int Y;
            }

            int Read(Pair* pair)
            {
                return pair->X;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "field 'X' is private in struct 'Pair'");
    }

    [Fact]
    public void Analyzer_RejectsUseOfUninitializedLocals()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Vector3
            {
                public int X;
                public int Y;
                public int Z;
            }

            int ReadScalar()
            {
                int value;
                return value;
            }

            void WriteField()
            {
                Vector3 vec;
                vec.X = 10;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "local variable 'value' is used before it is initialized");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "local variable 'vec' is used before it is initialized");
    }

    [Fact]
    public void Analyzer_AllowsExplicitAssignmentAfterDeclaration()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Vector3
            {
                public int X;
                public int Y;
                public int Z;

                public Vector3(int x, int y, int z)
                {
                    X = x;
                    Y = y;
                    Z = z;
                }
            }

            int Build(int x, int y, int z)
            {
                Vector3 vec;
                vec = Vector3(x, y, z);
                return vec.X;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_TracksDefiniteAssignmentAcrossIfBranches()
    {
        Compilation constantTrue = CreateCompilation("""
            namespace Example;

            struct Vector3
            {
                public int X;
                public int Y;
                public int Z;

                public Vector3(int x, int y, int z)
                {
                    X = x;
                    Y = y;
                    Z = z;
                }
            }

            int Build(int x, int y, int z)
            {
                Vector3 vec;
                if (true)
                {
                    vec = Vector3(x, y, z);
                }

                return vec.X;
            }
            """);

        Compilation bothBranches = CreateCompilation("""
            namespace Example;

            struct Vector3
            {
                public int X;
                public int Y;
                public int Z;
            }

            int Build(bool condition)
            {
                Vector3 vec;
                if (condition)
                    vec = Vector3 { 1, 2, 3 };
                else
                    vec = Vector3 { 4, 5, 6 };

                return vec.X;
            }
            """);

        Compilation conditionalOnly = CreateCompilation("""
            namespace Example;

            struct Vector3
            {
                public int X;
                public int Y;
                public int Z;
            }

            int Build(bool condition)
            {
                Vector3 vec;
                if (condition)
                {
                    vec = Vector3 { 1, 2, 3 };
                }

                return vec.X;
            }
            """);

        Assert.Empty(constantTrue.Diagnostics);
        Assert.Empty(bothBranches.Diagnostics);
        Assert.Contains(
            conditionalOnly.Diagnostics,
            diagnostic => diagnostic.Message == "local variable 'vec' is used before it is initialized");
    }

    [Fact]
    public void Analyzer_BindsHeapAndStackArraysAndRejectsStackEscape()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            void Consume(int[] values)
            {
            }

            struct Holder
            {
                int[] Values;
            }

            int[] Invalid()
            {
                int[] stack = int[10];
                stack[0] = 42;
                Consume(stack);
                Holder holder = Holder { stack };
                return stack;
            }

            void Heap()
            {
                int[] values = new int[10];
                values[0] = 1;
                Consume(values);
                free(values);
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "stack array cannot be passed to another function");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "stack array cannot be returned from a function");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "stack array cannot be stored inside a positional struct value");
        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message.Contains("heap array", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyzer_RequiresConstructorParenthesesButKeepsBracePositionalConstruction()
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
                Pair okay = Pair { 1, 2 };
                Pair bad = Pair(1, 2);
                free(okay);
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message.Contains("does not declare a constructor", StringComparison.Ordinal));
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "'free' requires a heap pointer or heap array, but has type 'Pair'");
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
                "native symbol 'malloc' is reserved for Xenon memory operations");
    }

    private static Compilation CreateCompilation(params string[] sources) => Compilation.Create(
        sources.Select((source, index) => SourceText.From(source, $"test{index}.xe")).ToArray());
}
