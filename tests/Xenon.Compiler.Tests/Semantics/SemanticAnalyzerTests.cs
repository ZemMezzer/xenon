using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class SemanticAnalyzerTests
{

    [Theory]
    [InlineData("int x = 0; x /= 1.5f;")]
    [InlineData("int x = 0; x <<= true;")]
    [InlineData("int* x = null; x -= x;")]
    public void Analyzer_ReportsInvalidCompoundOperandsWithoutCrashing(string statement)
    {
        Assert.True(CreateCompilation("namespace Example; void F() { " + statement + " }").HasErrors);
    }

    [Fact]
    public void Analyzer_ShortCircuitsInvalidConstantOperationsConsistently()
    {
        Assert.Empty(CreateCompilation("""
            namespace Example;
            const bool A = false && (1 / 0 == 0);
            struct S { public static bool B = true || (1 << -1 == 0); }
            bool F() { const bool C = false && (1 % 0 == 0); return true || (1 << -1 == 0); }
            """).Diagnostics);
    }


    [Theory]
    [InlineData("a + 1", "int* a")]
    [InlineData("1 + a", "int* a")]
    [InlineData("a - 1", "int* a")]
    [InlineData("a - b", "int* a, readonly int* b")]
    [InlineData("a++", "int* a")]
    [InlineData("++a", "int* a")]
    [InlineData("a += 2", "int* a")]
    [InlineData("a -= 2", "int* a")]
    public void Analyzer_AcceptsElementPointerArithmetic(string expression, string parameters)
    {
        Assert.Empty(CreateCompilation($"namespace Example; void F({parameters}) {{ {expression}; }}").Diagnostics);
    }

    [Theory]
    [InlineData("int* a, float* b", "a - b")]
    [InlineData("int* a, int* b", "a + b")]
    [InlineData("void* a", "a + 1")]
    [InlineData("void* a", "a++")]
    public void Analyzer_RejectsInvalidPointerArithmetic(string parameters, string expression)
    {
        Assert.True(CreateCompilation($"namespace Example; void F({parameters}) {{ {expression}; }}").HasErrors);
    }

    [Theory]
    [InlineData("Base() {}", "private")]
    [InlineData("public Base(int value) {}", "no constructor")]
    public void Analyzer_ValidatesImplicitAndExplicitBaseConstructor(string constructor, string diagnostic)
    {
        foreach (string derived in new[] { "", "public Derived() {}" })
        {
            Compilation compilation = CreateCompilation($$"""
                namespace Example;
                struct Base { {{constructor}} }
                struct Derived : Base { {{derived}} }
                """);
            Assert.Contains(compilation.Diagnostics, item => item.Message.Contains(diagnostic, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("IA, IB")]
    [InlineData("IB, IA")]
    public void Analyzer_RejectsConflictingInheritedInterfaceMembers(string bases)
    {
        foreach (string member in new[] { "Get();", "Value { get; }", "this[int index] { get; }" })
        {
            Compilation compilation = CreateCompilation($$"""
                namespace Example;
                interface IA { int {{member}} }
                interface IB { float {{member}} }
                interface IC : {{bases}} {}
                """);
            Assert.Contains(compilation.Diagnostics, item => item.Message.Contains("incompatible", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("IA, IB")]
    [InlineData("IB, IA")]
    public void Analyzer_ResolvesInheritedOverloadsAndDiamond(string bases)
    {
        Compilation compilation = CreateCompilation($$"""
            namespace Example;
            interface Root { int Read(); int this[int index] { get; } }
            interface IA : Root { int Get(int value); }
            interface IB : Root { float Get(float value); }
            interface IC : {{bases}} {}
            float F(IC value) { int a = value.Get(1); int b = value.Read(); int c = value[0]; return value.Get(1.0f); }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_DiagnosesAmbiguousInheritedOverloads()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            interface IA { int Get(int* value); }
            interface IB { int Get(float* value); }
            interface IC : IA, IB {}
            int F(IC value) { return value.Get(null); }
            """);
        Assert.Contains(compilation.Diagnostics, item => item.Message.Contains("ambiguous", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("extern int Foo(int x);", "extern double Foo(double x);")]
    [InlineData("extern int Foo(int x);", "extern int Foo(int x, int y);")]
    [InlineData("export int F() { return 1; }", "export int F() { return 2; }")]
    public void Analyzer_DiagnosesGlobalNativeCollisions(string first, string second)
    {
        Compilation compilation = Compilation.Create(
            SourceText.From("namespace A_B; " + first, "first.xe"),
            SourceText.From("namespace A.B; " + second, "second.xe"));
        Assert.Contains(compilation.Diagnostics, item => item.Message.Contains("native symbol", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_AcceptsIdenticalExternAbiDespiteReadonlyQualifiers()
    {
        Compilation compilation = Compilation.Create(
            SourceText.From("namespace A; extern int Foo(int* value);", "first.xe"),
            SourceText.From("namespace B; extern int readonly Foo(readonly int* value);", "second.xe"));
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("sbyte", 8, true)]
    [InlineData("byte", 8, false)]
    [InlineData("short", 16, true)]
    [InlineData("ushort", 16, false)]
    [InlineData("int", 32, true)]
    [InlineData("uint", 32, false)]
    [InlineData("long", 64, true)]
    [InlineData("ulong", 64, false)]
    public void Analyzer_ValidatesConstantIntegerEdges(string type, int width, bool signed)
    {
        foreach (string operation in new[] { "<<", ">>" })
        {
            foreach (int count in new[] { 0, width - 1, width, width + 1, -1 })
            {
                string expression = $"cast<{type}>(1) {operation} {count}";
                Compilation compilation = CreateCompilation($"namespace Example; const {type} Result = {expression};");
                Assert.Equal(count < 0 || count >= width, compilation.HasErrors);
                Compilation field = CreateCompilation($"namespace Example; struct S {{ public static {type} Result = {expression}; }}");
                Assert.Equal(compilation.HasErrors, field.HasErrors);
            }
        }
        foreach (string operation in new[] { "/", "%" })
        {
            string expression = $"cast<{type}>(1) {operation} cast<{type}>(0)";
            Assert.True(CreateCompilation($"namespace Example; const {type} Result = {expression};").HasErrors);
            Assert.True(CreateCompilation($"namespace Example; {type} F({type} x) {{ return x {operation} cast<{type}>(0); }}").HasErrors);
            if (signed)
            {
                string minimum = $"(cast<{type}>(1) << {width - 1})";
                Assert.True(CreateCompilation($"namespace Example; const {type} Result = {minimum} {operation} cast<{type}>(-1);").HasErrors);
            }
        }
    }

    [Theory]
    [InlineData("Base copy = derived;")]
    [InlineData("Derived* downcast = basePointer;")]
    [InlineData("Derived* downcast = cast<Derived*>(basePointer);")]
    [InlineData("IFoo view = *basePointer;")]
    public void Analyzer_StableLayoutDoesNotIntroduceSlicingDowncastsOrDescendantOnlyContracts(string statement)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Base { public int Value; }
            interface IFoo { int Foo(); }
            struct Derived : Base, IFoo { public int Foo() { return 42; } }
            void Test() { Derived derived = Derived(); Base* basePointer = &derived;
            """ + statement + " }");
        Assert.True(compilation.HasErrors);
    }

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

            extern int puts(readonly byte* text);

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
            diagnostic => diagnostic.Message == "'break' can only be used inside a loop or switch");
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
    public void Analyzer_RejectsWritesThroughReadonlyStructPointer()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Vector2
            {
                float X;
                float Y;
            }

            void Mutate(readonly Vector2* value)
            {
                value->X = 1.0f;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "left side of assignment must be writable");
    }

    [Fact]
    public void Analyzer_UsesReadonlyInsteadOfConstForPointerConstCorrectness()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Value
            {
                int Data;
            }

            void Use(Value* mutable, readonly Value* readOnly)
            {
                readonly Value* upgraded = mutable;
                Value* invalid = readOnly;
                readOnly->Data = 1;
            }

            void Legacy(const Value* value)
            {
            }
            """);

        NamespaceSymbol @namespace = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        FunctionSymbol use = @namespace.Functions.Single(function => function.Name == "Use");
        FunctionSymbol legacy = @namespace.Functions.Single(function => function.Name == "Legacy");
        Assert.True(Assert.IsType<PointerTypeSymbol>(use.Parameters[1].Type).IsReadonly);
        Assert.False(Assert.IsType<PointerTypeSymbol>(legacy.Parameters[0].Type).IsReadonly);
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Message == "cannot implicitly convert 'readonly Value*' to 'Value*'");
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Message == "left side of assignment must be writable");
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Message == "'const T*' is no longer supported; use 'readonly T*'");
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
                public int X;
                public int Y;
                public int Z;

                public Vector3(int x, int y, int z)
                {
                    X = x;
                    Y = y;
                    Z = z;
                }

                public ~Vector3()
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
    public void Analyzer_RejectsPrivateFieldInitializationThroughExternalPositionalConstruction()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Counter
            {
                private int Value;
            }

            int Main()
            {
                Counter local = Counter { 10 };
                Counter* heap = new Counter { 20 };
                return 0;
            }
            """);

        Assert.Equal(
            2,
            compilation.Diagnostics.Count(
                diagnostic => diagnostic.Message == "field 'Value' is private in struct 'Counter'"));
    }

    [Fact]
    public void Analyzer_AllowsPrivateFieldInitializationInsideDeclaringStruct()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Counter
            {
                private int Value;

                public static Counter Create(int value)
                {
                    return Counter { value };
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
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
    public void Analyzer_ResolvesUsingNamespacesAcrossFiles()
    {
        Compilation compilation = CreateCompilation(
            """
            namespace Library.Math;

            struct Counter
            {
                int Value;

                public Counter(int value)
                {
                    Value = value;
                }

                public int Read()
                {
                    return Value;
                }
            }

            public int Add(int left, int right)
            {
                return left + right;
            }
            """,
            """
            using Library.Math;

            namespace Game;

            int Main()
            {
                Counter counter = Counter(Add(20, 22));
                return counter.Read();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction main = Assert.Single(
            compilation.SemanticModel.Functions.Where(function => function.Symbol.FullName == "Game.Main"));
        var declaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[0]);
        var constructor = Assert.IsType<BoundConstructorCallExpression>(declaration.Initializer);
        var add = Assert.IsType<BoundCallExpression>(constructor.Arguments[0]);
        Assert.Equal("Library.Math.Add", add.Function.FullName);
    }

    [Fact]
    public void Analyzer_UsingIsFileLocalAndNotTransitive()
    {
        Compilation compilation = CreateCompilation(
            """
            namespace Library;

            struct Value
            {
                int Data;
            }
            """,
            """
            using Library;

            namespace Game;

            public int UsesImport(Value value)
            {
                return 0;
            }
            """,
            """
            namespace Game;

            int MissingOwnUsing(Value value)
            {
                return 0;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "unknown type 'Value'");
    }

    [Fact]
    public void Analyzer_DuplicateNamespaceUsingInOneFileIsHarmless()
    {
        Compilation compilation = CreateCompilation(
            """
            namespace Library;

            struct Value
            {
                int Data;
            }
            """,
            """
            using Library;
            using Library;

            namespace Game;

            int Read(Value value)
            {
                return 0;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_ReportsAmbiguousImportedTypeAndSupportsTypeAlias()
    {
        Compilation ambiguous = CreateCompilation(
            """
            namespace First;
            struct Value { int Data; }
            """,
            """
            namespace Second;
            struct Value { int Data; }
            """,
            """
            using First;
            using Second;

            namespace Game;

            int Read(Value value)
            {
                return 0;
            }
            """);

        Assert.Contains(
            ambiguous.Diagnostics,
            diagnostic => diagnostic.Message.Contains("type name 'Value' is ambiguous", StringComparison.Ordinal));

        Compilation aliased = CreateCompilation(
            """
            namespace First;
            struct Value { int Data; }
            """,
            """
            namespace Second;
            struct Value { int Data; }
            """,
            """
            using FirstValue = First.Value;
            using SecondValue = Second.Value;

            namespace Game;

            int Read(FirstValue first, SecondValue second)
            {
                return 42;
            }
            """);

        Assert.Empty(aliased.Diagnostics);
    }

    [Fact]
    public void Analyzer_ResolvesFullyQualifiedAndNamespaceAliasedTypes()
    {
        Compilation compilation = CreateCompilation(
            """
            namespace Library.Math;

            struct Vector
            {
                public int X;
                public int Y;
            }
            """,
            """
            using Math = Library.Math;

            namespace Game;

            int Read(Math.Vector aliased, Library.Math.Vector qualified)
            {
                Math.Vector local = Math.Vector { 20, 22 };
                return local.X + qualified.Y;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_ResolvesNamespaceAliasForQualifiedFunctionCall()
    {
        Compilation compilation = CreateCompilation(
            """
            namespace Library.Math;

            public int Add(int left, int right)
            {
                return left + right;
            }
            """,
            """
            using Math = Library.Math;

            namespace Game;

            int Main()
            {
                return Math.Add(20, 22);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction main = Assert.Single(
            compilation.SemanticModel.Functions.Where(function => function.Symbol.FullName == "Game.Main"));
        var @return = Assert.IsType<BoundReturnStatement>(Assert.Single(main.Body.Statements));
        var call = Assert.IsType<BoundCallExpression>(@return.Expression);
        Assert.Equal("Library.Math.Add", call.Function.FullName);
    }

    [Fact]
    public void Analyzer_ImportedPrivateFunctionRemainsInaccessible()
    {
        Compilation compilation = CreateCompilation(
            """
            namespace Library;

            int Hidden()
            {
                return 42;
            }
            """,
            """
            using Library;

            namespace Game;

            int Main()
            {
                return Hidden();
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "function 'Hidden' is private in namespace 'Library'");
        Assert.DoesNotContain(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "unknown function 'Hidden'");
    }

    [Fact]
    public void Analyzer_BindsStructMethodsWithImplicitThisAndValueOrPointerReceivers()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Counter
            {
                int Value;

                public Counter(int value)
                {
                    Value = value;
                }

                private void AddCore(int amount)
                {
                    Value += amount;
                }

                public void Add(int amount)
                {
                    AddCore(amount);
                }

                public int Read()
                {
                    return Value;
                }
            }

            int Main()
            {
                Counter value = Counter(10);
                value.Add(5);

                Counter* pointer = &value;
                pointer->Add(7);

                return value.Read();
            }
            """);

        Assert.Empty(compilation.Diagnostics);

        StructTypeSymbol counter = Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types);
        Assert.Equal(3, counter.Methods.Length);
        Assert.True(counter.FindMethod("Add")!.IsPublic);
        Assert.False(counter.FindMethod("AddCore")!.IsPublic);

        BoundFunction add = Assert.Single(
            compilation.SemanticModel.Functions.Where(function =>
                function.Symbol.FunctionKind == FunctionKind.Method &&
                function.Symbol.Name == "Add"));
        var addCoreStatement = Assert.IsType<BoundExpressionStatement>(Assert.Single(add.Body.Statements));
        var implicitCall = Assert.IsType<BoundMethodCallExpression>(addCoreStatement.Expression);
        Assert.IsType<BoundThisExpression>(implicitCall.Receiver);
        Assert.True(implicitCall.IsPointerAccess);

        BoundFunction main = Assert.Single(
            compilation.SemanticModel.Functions.Where(function => function.Symbol.Name == "Main"));
        var valueCallStatement = Assert.IsType<BoundExpressionStatement>(main.Body.Statements[1]);
        var valueCall = Assert.IsType<BoundMethodCallExpression>(valueCallStatement.Expression);
        Assert.False(valueCall.IsPointerAccess);
        Assert.Equal("Add", valueCall.Method.Name);

        var pointerCallStatement = Assert.IsType<BoundExpressionStatement>(main.Body.Statements[3]);
        var pointerCall = Assert.IsType<BoundMethodCallExpression>(pointerCallStatement.Expression);
        Assert.True(pointerCall.IsPointerAccess);

        var returnStatement = Assert.IsType<BoundReturnStatement>(main.Body.Statements[4]);
        Assert.IsType<BoundMethodCallExpression>(returnStatement.Expression);
    }

    [Fact]
    public void Analyzer_EnforcesStructMethodVisibilityAndReadonlyReceiverCalls()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Counter
            {
                int Value;

                void Hidden()
                {
                    Value++;
                }

                public void Add(int amount)
                {
                    Value += amount;
                }
            }

            void CallReadonly(readonly Counter* pointer)
            {
                pointer->Add(1);
            }

            int Main()
            {
                Counter value = Counter { 0 };
                value.Hidden();
                return 0;
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "method 'Hidden' is private in struct 'Counter'");
        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message ==
                "mutable method 'Add' cannot be called on a readonly 'Counter' receiver");
    }

    [Fact]
    public void Analyzer_RejectsStructMethodOverloading()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Counter
            {
                public void Add(int value)
                {
                }

                public void Add(float value)
                {
                }
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message ==
                "method overloading is not supported yet; struct 'Counter' may declare only one method named 'Add'");
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

    [Fact]
    public void Analyzer_BindsInheritanceInterfacesAndStaticMembers()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            interface IRenderable
            {
                void Render();
            }

            struct Entity
            {
                public int Id;
                public static int Count = 3;
                public virtual void Update() { }
            }

            struct Enemy : Entity, IRenderable
            {
                public int Health;
                public override void Update() { }
                public void Render() { }
                public static int GetCount() { return Entity.Count; }
            }

            int Main()
            {
                Enemy value = Enemy { 1, 100 };
                Entity* entity = &value;
                return Enemy.GetCount() + entity->Id;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol enemy = compilation.SemanticModel.GlobalNamespace
            .Namespaces.Single().Types.Single(type => type.Name == "Enemy");
        Assert.Equal("Entity", enemy.BaseType!.Name);
        Assert.Equal(2, enemy.AllInstanceFields.Length);
        Assert.Single(enemy.Interfaces);
        Assert.Single(enemy.BaseType.StaticFields);
    }

    [Fact]
    public void Analyzer_SelectsOverloadedConstructorsIncludingBaseConstructors()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity
            {
                public Entity() { }
                public Entity(int id) { }
            }

            struct Enemy : Entity
            {
                public Enemy() : base() { }
                public Enemy(int id) : base(id) { }
            }

            void Build()
            {
                Enemy first = Enemy();
                Enemy second = Enemy(42);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol enemy = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Types.Single(type => type.Name == "Enemy");
        Assert.Equal(2, enemy.Constructors.Length);
    }

    [Fact]
    public void Analyzer_RejectsPrivateBaseMemberAccessFromDerivedStruct()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity { private int Secret; }
            struct Enemy : Entity
            {
                int Reveal() { return Secret; }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "field 'Secret' is private in struct 'Entity'");
    }

    [Fact]
    public void Analyzer_RejectsDerivedObjectUseInBaseConstructorArguments()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base { public Base(int value) { } }
            struct Derived : Base
            {
                int Value;
                public Derived() : base(Value) { Value = 100; }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "the derived object cannot be used in base constructor arguments");
    }

    [Fact]
    public void Analyzer_RejectsImplicitPrivateBaseMethodCall()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base { private int Secret() { return 42; } }
            struct Derived : Base
            {
                public int Leak() { return Secret(); }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "method 'Secret' is private in struct 'Base'");
    }

    [Fact]
    public void Analyzer_AllowsAssignmentToMutableStaticField()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct State
            {
                public static int Value;
            }

            int Main()
            {
                State.Value = 41;
                State.Value += 1;
                return State.Value;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_EvaluatesStaticConstantExpressions()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Limits { public static int Maximum = 512 * 2; }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol limits = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Types.Single();
        Assert.Equal(1024, Assert.Single(limits.StaticFields).ConstantValue);
    }

    [Fact]
    public void Analyzer_ReportsStructAndInterfaceInheritanceCycles()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct A : B { }
            struct B : A { }
            interface IA : IB { }
            interface IB : IA { }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("struct inheritance cycle", StringComparison.Ordinal));
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("interface inheritance cycle", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_RejectsPrivateBaseConstructor()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base { private Base(int value) { } }
            struct Derived : Base { public Derived() : base(123) { } }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "constructor 'Base' is private");
    }

    [Fact]
    public void Analyzer_RejectsPrivateStaticMemberAccess()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Secret
            {
                private static int Value = 42;
                private static int Get() { return 42; }
            }

            int Main() { return Secret.Value + Secret.Get(); }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "static field 'Value' is private in struct 'Secret'");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "static method 'Get' is private in struct 'Secret'");
    }

    [Fact]
    public void Analyzer_RejectsStaticInitializerTypeMismatch()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Test
            {
                public static int A = true;
                public static bool B = 123;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "cannot implicitly convert 'bool' to 'int'");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "cannot implicitly convert 'int' to 'bool'");
    }

    [Fact]
    public void Analyzer_SupportsThisAndRejectsItInBaseArguments()
    {
        Compilation valid = CreateCompilation("""
            namespace Example;

            struct Base { public Base(int id) { } }
            struct Derived : Base
            {
                int Health;
                public Derived(int id, int health) : base(id) { this.Health = health; }
            }
            """);
        Assert.Empty(valid.Diagnostics);

        Compilation invalid = CreateCompilation("""
            namespace Example;

            struct Base { public Base(Derived* value) { } }
            struct Derived : Base { public Derived() : base(this) { } }
            """);
        Assert.Contains(invalid.Diagnostics, diagnostic => diagnostic.Message == "the derived object cannot be used in base constructor arguments");
    }

    [Fact]
    public void Analyzer_AssignsInterfaceMethodSlotsPerInterface()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            interface IA { int A(); }
            interface IB { int B(); }
            interface IC : IA, IB { int C(); }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol example = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        InterfaceTypeSymbol ib = example.Interfaces.Single(type => type.Name == "IB");
        InterfaceTypeSymbol ic = example.Interfaces.Single(type => type.Name == "IC");
        FunctionSymbol b = ib.FindMethod("B")!;

        Assert.Equal(0, ib.GetMethodSlot(b));
        Assert.Equal(1, ic.GetMethodSlot(b));
    }

    [Fact]
    public void Analyzer_RejectsStaticStringInitializerBeforeCodeGeneration()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Messages
            {
                public static readonly readonly byte* Text = "Hello";
            }
            """);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Message == "static field type 'readonly byte*' does not support this constant initializer");
    }

    [Fact]
    public void Analyzer_PrefersExactBaseConstructorOverload()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity { }
            struct Enemy : Entity { }

            struct Base
            {
                public Base(Entity* value) { }
                public Base(Enemy* value) { }
            }

            struct Derived : Base
            {
                public Derived(Enemy* value) : base(value) { }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction constructor = compilation.SemanticModel.Functions.Single(function =>
            function.Symbol.FunctionKind == FunctionKind.Constructor &&
            function.Symbol.ContainingType?.Name == "Derived");
        var statement = Assert.IsType<BoundExpressionStatement>(constructor.Body.Statements[0]);
        var baseCall = Assert.IsType<BoundBaseLifecycleCallExpression>(statement.Expression);
        var parameterType = Assert.IsType<PointerTypeSymbol>(Assert.Single(baseCall.Function.Parameters).Type);

        Assert.Equal("Enemy", parameterType.ElementType.Name);
    }

    [Fact]
    public void Analyzer_RejectsMutationThroughReadonlyReference()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity { public int Value; }

            void Mutate(readonly Entity& entity)
            {
                entity.Value = 42;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "left side of assignment must be writable");
    }

    [Fact]
    public void Analyzer_RequiresReferenceLocalInitializer()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity { }

            void Invalid()
            {
                Entity& entity;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "reference variables must be initialized");
    }

    [Fact]
    public void Analyzer_EvaluatesSignedIntegerConstantsWithSignedSemantics()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Test
            {
                public static bool Less = -1 < 1;
                public static int Divide = -3 / 2;
                public static int Remainder = -3 % 2;
                public static int Shift = -1 >> 1;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol type = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Types.Single();
        Assert.Equal(true, type.StaticFields.Single(field => field.Name == "Less").ConstantValue);
        Assert.Equal(-1, type.StaticFields.Single(field => field.Name == "Divide").ConstantValue);
        Assert.Equal(-1, type.StaticFields.Single(field => field.Name == "Remainder").ConstantValue);
        Assert.Equal(-1, type.StaticFields.Single(field => field.Name == "Shift").ConstantValue);
    }

    [Fact]
    public void Analyzer_AllowsImplicitDefaultBaseConstructionWhenBaseDeclaresNoConstructor()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base
            {
                int Value;
            }

            struct Derived : Base
            {
                public Derived()
                {
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_AllowsExplicitBaseCallForImplicitDefaultConstruction()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base
            {
            }

            struct Derived : Base
            {
                public Derived() : base()
                {
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_BindsImplicitBaseCallWhenParameterlessConstructorExists()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base
            {
                public Base()
                {
                }
            }

            struct Derived : Base
            {
                public Derived()
                {
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction constructor = compilation.SemanticModel.Functions.Single(function =>
            function.Symbol.FunctionKind == FunctionKind.Constructor &&
            function.Symbol.ContainingType?.Name == "Derived");
        var statement = Assert.IsType<BoundExpressionStatement>(constructor.Body.Statements[0]);
        var baseCall = Assert.IsType<BoundBaseLifecycleCallExpression>(statement.Expression);
        Assert.Equal("Base", baseCall.Function.ContainingType!.Name);
        Assert.Empty(baseCall.Arguments);
    }

    [Fact]
    public void Analyzer_EnforcesReadonlyLocalsAndFields()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int ReadValue() { return 10; }

            struct Entity
            {
                public readonly int Id;

                public Entity(int id)
                {
                    Id = id;
                }
            }

            void Invalid()
            {
                readonly int result = ReadValue();
                result = 20;

                Entity entity = Entity(1);
                entity.Id = 2;
            }
            """);

        Assert.Equal(
            2,
            compilation.Diagnostics.Count(diagnostic => diagnostic.Message == "left side of assignment must be writable"));
    }

    [Fact]
    public void Analyzer_BindsInstanceFieldInitializersBetweenBaseCallAndConstructorBody()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base
            {
                public int Stage;

                public Base()
                {
                    Stage = 40;
                }
            }

            struct Derived : Base
            {
                readonly int Result = Stage + 2;
                int Marker = 7;

                public Derived()
                {
                    Marker = Marker + 1;
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction constructor = compilation.SemanticModel.Functions.Single(function =>
            function.Symbol.FunctionKind == FunctionKind.Constructor &&
            function.Symbol.ContainingType?.Name == "Derived");

        Assert.IsType<BoundBaseLifecycleCallExpression>(
            Assert.IsType<BoundExpressionStatement>(constructor.Body.Statements[0]).Expression);
        var initializerCall = Assert.IsType<BoundBaseLifecycleCallExpression>(
            Assert.IsType<BoundExpressionStatement>(constructor.Body.Statements[1]).Expression);
        Assert.Equal(FunctionKind.InstanceInitializer, initializerCall.Function.FunctionKind);
        Assert.IsType<BoundAssignmentExpression>(
            Assert.IsType<BoundExpressionStatement>(constructor.Body.Statements[2]).Expression);

        BoundFunction initializer = compilation.SemanticModel.Functions.Single(function =>
            function.Symbol.FunctionKind == FunctionKind.InstanceInitializer &&
            function.Symbol.ContainingType?.Name == "Derived");
        Assert.Equal(
            "Result",
            Assert.IsType<BoundMemberAccessExpression>(
                Assert.IsType<BoundAssignmentExpression>(
                    Assert.IsType<BoundExpressionStatement>(initializer.Body.Statements[0]).Expression).Target).Field.Name);
        Assert.Equal(
            "Marker",
            Assert.IsType<BoundMemberAccessExpression>(
                Assert.IsType<BoundAssignmentExpression>(
                    Assert.IsType<BoundExpressionStatement>(initializer.Body.Statements[1]).Expression).Target).Field.Name);
    }

    [Fact]
    public void Analyzer_RejectsInstanceFieldInitializerTypeMismatch()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Invalid
            {
                readonly int Value = true;

                public Invalid()
                {
                }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Message == "cannot implicitly convert 'bool' to 'int'");
    }

    [Fact]
    public void Analyzer_BindsThisAsReadonlyReferenceInsideReadonlyMethod()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            void readonly Read(readonly Value& value)
            {
            }

            struct Value
            {
                public void readonly Test()
                {
                    Read(this);
                }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_EnforcesReadonlyInterfaceMemberCalls()
    {
        Compilation valid = CreateCompilation("""
            namespace Example;

            interface IValue
            {
                int readonly Read();
                readonly int Current { get; }
                readonly int this[int index] { get; }
            }

            struct Value : IValue
            {
                int value;
                public int readonly Read() { return value; }
                public readonly int Current { get { return value; } }
                public readonly int this[int index] { get { return value + index; } }
            }

            int Use(readonly IValue& value)
            {
                return value.Read() + value.Current + value[1];
            }
            """);

        Assert.Empty(valid.Diagnostics);

        Compilation invalid = CreateCompilation("""
            namespace Example;

            interface IMutable
            {
                void Mutate();
                int Current { get; }
                int this[int index] { get; }
            }

            void Use(readonly IMutable& value)
            {
                value.Mutate();
                int current = value.Current;
                int indexed = value[0];
            }
            """);

        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Message == "mutable interface method 'Mutate' cannot be called on a readonly 'IMutable' receiver");
        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Message == "property 'Current' cannot be read through a readonly interface receiver because its getter is mutable");
        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Message == "no indexer of type 'IMutable' matches the provided arguments");
    }

    [Fact]
    public void Analyzer_FoldsTargetIndependentConstantsAndRejectsDivisionByZero()
    {
        Compilation valid = CreateCompilation("""
            namespace Example;

            const int A = 4 * 8;
            const int B = A + 10;
            const byte Wrapped = cast<byte>(255) + cast<byte>(1);

            int Main() { return B; }
            """);

        Assert.Empty(valid.Diagnostics);
        NamespaceSymbol @namespace = Assert.Single(valid.SemanticModel.GlobalNamespace.Namespaces);
        ConstantSymbol a = @namespace.Constants.Single(constant => constant.Name == "A");
        ConstantSymbol b = @namespace.Constants.Single(constant => constant.Name == "B");
        ConstantSymbol wrapped = @namespace.Constants.Single(constant => constant.Name == "Wrapped");
        Assert.Equal(32, a.Value);
        Assert.Equal(42, b.Value);
        Assert.Equal(32, Assert.IsType<BoundLiteralExpression>(a.BoundValue).Value);
        Assert.Equal(42, Assert.IsType<BoundLiteralExpression>(b.BoundValue).Value);
        Assert.Equal(0, wrapped.Value);

        Compilation invalid = CreateCompilation("""
            namespace Example;

            const int Bad = 1 / 0;
            int Main() { return 0; }
            """);

        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Message == "initializer of constant 'Bad' contains an invalid compile-time operation");
    }

    [Fact]
    public void Analyzer_EnforcesReadonlyInstanceMethods()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Counter
            {
                int Value;

                public int readonly Read()
                {
                    return Value;
                }

                public void Reset()
                {
                    Value = 0;
                }

                public int readonly Invalid()
                {
                    Reset();
                    Value = 1;
                    return Read();
                }
            }

            int Use(readonly Counter& counter)
            {
                int value = counter.Read();
                counter.Reset();
                return value;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "readonly method 'Invalid' cannot call mutable method 'Reset' through 'this'");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "left side of assignment must be writable");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "mutable method 'Reset' cannot be called on a readonly 'Counter' receiver");
    }

    [Fact]
    public void Analyzer_SelectsMethodOverloadForReceiverMutability()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Container
            {
                public int Value;

                public int& Get()
                {
                    return Value;
                }

                public readonly int& readonly Get()
                {
                    return Value;
                }
            }

            int Main()
            {
                Container value = Container { 7 };
                Container& mutable = value;
                readonly Container& readOnly = mutable;
                int& writable = mutable.Get();
                readonly int& readable = readOnly.Get();
                return readable;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol container = Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types);
        Assert.Equal(2, container.Methods.Length);
        Assert.Contains(container.Methods, method => !method.IsReadonly && method.FullName == "Example.Container.Get");
        Assert.Contains(container.Methods, method => method.IsReadonly && method.FullName == "Example.Container.Get.__readonly");

        BoundFunction main = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Main");
        var mutableDeclaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[3]);
        var mutableConversion = Assert.IsType<BoundReferenceConversionExpression>(mutableDeclaration.Initializer);
        var mutableDereference = Assert.IsType<BoundReferenceDereferenceExpression>(mutableConversion.Source);
        var mutableCall = Assert.IsType<BoundMethodCallExpression>(mutableDereference.Reference);
        Assert.False(mutableCall.Method.IsReadonly);
        var readonlyDeclaration = Assert.IsType<BoundVariableDeclarationStatement>(main.Body.Statements[4]);
        var readonlyConversion = Assert.IsType<BoundReferenceConversionExpression>(readonlyDeclaration.Initializer);
        var readonlyDereference = Assert.IsType<BoundReferenceDereferenceExpression>(readonlyConversion.Source);
        var readonlyCall = Assert.IsType<BoundMethodCallExpression>(readonlyDereference.Reference);
        Assert.True(readonlyCall.Method.IsReadonly);
    }

    [Theory]
    [InlineData("int*", false, false)]
    [InlineData("readonly int*", true, false)]
    [InlineData("int* readonly", false, true)]
    [InlineData("readonly int* readonly", true, true)]
    public void Analyzer_SeparatesPointerReturnAccessFromMethodReadonly(
        string signature, bool pointeeReadonly, bool methodReadonly)
    {
        string declaration = $$"""
            namespace Example;
            struct Value
            {
                public int Count;
                public {{signature}} Get(int* pointer) { return pointer; }
            }
            """;
        Compilation compilation = CreateCompilation(declaration);
        Assert.Empty(compilation.Diagnostics);
        var method = Assert.Single(Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types).Methods);
        Assert.Equal(methodReadonly, method.IsReadonly);
        Assert.Equal(pointeeReadonly, Assert.IsType<PointerTypeSymbol>(method.ReturnType).IsReadonly);

        Compilation write = CreateCompilation(declaration + """
            void Use(Value& value, int* pointer) { *value.Get(pointer) = 10; }
            """);
        Assert.Equal(pointeeReadonly, write.HasErrors);
        if (pointeeReadonly)
            Assert.Contains(write.Diagnostics, diagnostic => diagnostic.Message == "left side of assignment must be writable");

        Compilation call = CreateCompilation(declaration + """
            void Use(readonly Value& value, int* pointer) { value.Get(pointer); }
            """);
        Assert.Equal(!methodReadonly, call.HasErrors);

        Compilation mutation = CreateCompilation(declaration.Replace("return pointer;", "Count++; return pointer;"));
        Assert.Equal(methodReadonly, mutation.HasErrors);
    }

    [Theory]
    [InlineData("readonly int&", "return Count;")]
    [InlineData("readonly int*", "return &Count;")]
    public void Analyzer_ReadonlyReturnAccessDoesNotMakeMethodReadonly(string returnType, string body)
    {
        string declaration = $$"""
            namespace Example;
            struct Value
            {
                public int Count;
                public {{returnType}} Get() { Count++; {{body}} }
            }
            """;
        Assert.Empty(CreateCompilation(declaration).Diagnostics);
        Compilation invalid = CreateCompilation(declaration + """
            void Use(readonly Value& value) { value.Get(); }
            """);
        Assert.Contains(invalid.Diagnostics, diagnostic =>
            diagnostic.Message == "mutable method 'Get' cannot be called on a readonly 'Value' receiver");
    }

    [Theory]
    [InlineData("int", "0")]
    [InlineData("bool", "false")]
    [InlineData("void", "")]
    [InlineData("Value", "Value { }")]
    [InlineData("State", "State.Idle")]
    [InlineData("int[]", "new int[1]")]
    [InlineData("int[,]", "new int[1, 2]")]
    [InlineData("Value[]", "new Value[1]")]
    [InlineData("int[][]", "new int[1][]")]
    public void Analyzer_RejectsReadonlyOnByValueReturnTypes(string returnType, string value)
    {
        Compilation compilation = CreateCompilation($$"""
            namespace Example;
            struct Value { }
            enum State { Idle }
            struct Source
            {
                public readonly {{returnType}} Get() { return {{value}}; }
                public readonly {{returnType}} readonly Read() { return {{value}}; }
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(d => d.Message ==
            "'readonly' cannot qualify a by-value return type; place 'readonly' before the method name to declare a readonly method"));
    }

    [Theory]
    [InlineData("readonly int Get() { return 0; }")]
    [InlineData("extern readonly int Get();")]
    [InlineData("export readonly int Get() { return 0; }")]
    [InlineData("struct Value { public static readonly int Get() { return 0; } }")]
    [InlineData("struct Value { public virtual readonly int Get() { return 0; } }")]
    [InlineData("abstract struct Value { public abstract readonly int Get(); }")]
    [InlineData("interface IValue { readonly int Get(); }")]
    [InlineData("interface IValue { readonly int readonly Get(); }")]
    public void Analyzer_RejectsReadonlyByValueReturnsAcrossCallableKinds(string declaration)
    {
        string source = "namespace Example; " + declaration;
        Compilation compilation = CreateCompilation(source);
        var diagnostic = Assert.Single(compilation.Diagnostics.Where(d => d.Message ==
            "'readonly' cannot qualify a by-value return type; place 'readonly' before the method name to declare a readonly method"));
        Assert.Equal(source.IndexOf("readonly", StringComparison.Ordinal), diagnostic.Location.Span.Start);
        Assert.Equal("readonly".Length, diagnostic.Location.Span.Length);
    }

    [Fact]
    public void Analyzer_AllowsReadonlyMethodsReturningValuesWithoutReturnQualifier()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Value { }
            struct Source
            {
                public int readonly GetNumber() { return 0; }
                public Value readonly GetValue() { return Value { }; }
                public int[] readonly GetArray() { return new int[1]; }
                public void readonly Update() { }
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_PreservesMeaningfulReadonlyReturnAccess()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            readonly int& GetReference(int& value) { return value; }
            readonly int*[] GetPointers(readonly int*[] values) { return values; }
            interface IValue
            {
                readonly int& GetMutable();
                readonly int& readonly GetReadonly();
            }
            struct Value : IValue
            {
                public int Count;
                public readonly int& GetMutable() { Count++; return Count; }
                public readonly int& readonly GetReadonly() { return Count; }
                public static readonly int& GetStatic(int& value) { return value; }
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        var getPointers = compilation.SemanticModel.Functions.Single(f => f.Symbol.Name == "GetPointers");
        var array = Assert.IsType<ArrayTypeSymbol>(getPointers.Symbol.ReturnType);
        Assert.True(Assert.IsType<PointerTypeSymbol>(array.ElementType).IsReadonly);
    }

    [Fact]
    public void Analyzer_PreservesReadonlyPointeeInFreeFunctionReturns()
    {
        const string declarations = """
            namespace Example;
            readonly int* GetPointer(int* pointer) { return pointer; }
            struct Value { public static readonly int* GetPointer(int* pointer) { return pointer; } }
            """;
        Compilation valid = CreateCompilation(declarations + """
            void Use(int* a, int* b)
            {
                readonly int* pointer = GetPointer(a);
                pointer = Value.GetPointer(b);
            }
            """);
        Assert.Empty(valid.Diagnostics);
        Compilation invalid = CreateCompilation(declarations + """
            void Use(int* pointer) { *GetPointer(pointer) = 10; *Value.GetPointer(pointer) = 20; }
            """);
        Assert.Equal(2, invalid.Diagnostics.Count(d => d.Message == "left side of assignment must be writable"));
    }

    [Theory]
    [InlineData("int* readonly Get() { return null; }")]
    [InlineData("readonly int* readonly Get() { return null; }")]
    [InlineData("void readonly Update() { }")]
    [InlineData("struct Value { public static int* readonly Get() { return null; } }")]
    [InlineData("struct Value { public static void readonly Update() { } }")]
    public void Analyzer_AllowsReadonlyFunctionsWithoutReceiver(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.Empty(compilation.Diagnostics);
        Assert.True(Assert.Single(compilation.SemanticModel.Functions).Symbol.IsReadonly);
    }

    [Fact]
    public void Analyzer_VoidReadonlyMethodCannotMutateReceiver()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Value
            {
                public int Count;
                public void readonly Update() { Count = 10; }
            }
            """);
        Assert.Contains(compilation.Diagnostics, d => d.Message == "left side of assignment must be writable");
    }

    [Theory]
    [InlineData("int readonly Square(int value) { return value * value; }")]
    [InlineData("void readonly Set(int* value) { *value = 10; }")]
    [InlineData("void readonly Set(int& value) { value = 10; }")]
    [InlineData("void readonly Set(int* readonly value) { *value = 10; }")]
    [InlineData("int readonly Read(readonly int* value) { return *value; }")]
    [InlineData("int readonly Read(readonly int& value) { return value; }")]
    [InlineData("void readonly Alias(int* value) { int* p = value; int* readonly fixed = p; *fixed = 10; readonly int* readonly view = p; }")]
    [InlineData("void readonly Alias(int& value) { int& reference = value; int* pointer = &reference; *pointer = 10; }")]
    [InlineData("int readonly Local() { int value = 1; int* pointer = &value; *pointer += 9; return value; }")]
    [InlineData("int readonly Sum(readonly int* values, int count) { int result = 0; for (int i = 0; i < count; i++) result += values[i]; return result; }")]
    [InlineData("int readonly A() { return B(); } int readonly B() { return 10; }")]
    [InlineData("int readonly Recurse(int n) { if (n == 0) return 0; return Recurse(n - 1); }")]
    [InlineData("void readonly Wrapper(int* value) { Write(value); }")]
    [InlineData("extern int readonly External(int value); int readonly Use() { return External(1); }")]
    [InlineData("readonly int* readonly View() { return State.Pointer; }")]
    [InlineData("int* readonly Identity(int* value) { return value; }")]
    [InlineData("readonly int& readonly View() { return State.Value; }")]
    [InlineData("int readonly ReadGlobal() { return State.Value; }")]
    [InlineData("struct Holder { public int Value; } void readonly Set(Holder* value) { value->Value = 10; }")]
    [InlineData("struct Holder { public int* Pointer; } void readonly Set(Holder& value) { *value.Pointer = 10; }")]
    [InlineData("struct Holder { public int* Pointer; } void readonly Local(int* pointer) { Holder value = Holder { pointer }; *value.Pointer = 10; }")]
    [InlineData("int readonly Local() { int[] a = int[2]; a[0] = 10; a[1] = 20; return a[0] + a[1]; }")]
    [InlineData("int[] readonly Create() { int[] a = new int[2]; a[0] = 10; return a; }")]
    [InlineData("void readonly Heap() { int[] a = new int[2]; a[0] = 10; free(a); }")]
    [InlineData("int readonly Initial() { return 3; } struct Item { public int Value = Initial(); } int readonly Local() { Item item = Item {}; return item.Value; }")]
    [InlineData("struct Value { public int readonly Read() { return 10; } public int Read() { State.Value++; return 20; } } int readonly Use(Value& value) { return value.Read(); }")]
    [InlineData("struct Value { public static int readonly Read() { return 10; } } int readonly Use() { return Value.Read(); }")]
    [InlineData("struct Data { public int Value; } Data readonly Build() { Data data = Data(); data.Value = 10; return data; }")]
    [InlineData("struct Inner { public int Value; } struct Outer { public Inner Inner; } void readonly Test() { Outer value = Outer(); value.Inner.Value = 42; }")]
    [InlineData("struct Data { public int* Pointer; } void readonly Test() { int value = 0; Data data = Data(); data.Pointer = &value; *data.Pointer = 10; }")]
    [InlineData("struct Inner { public int* Pointer; } struct Outer { public Inner Inner; } void readonly Test(int* output) { Outer value = Outer(); value.Inner.Pointer = output; Outer& alias = value; int* copy = alias.Inner.Pointer; *copy = 42; }")]
    [InlineData("struct Data { public int* Pointer; } void readonly Test(int& output) { Data value = Data(); value.Pointer = &output; *value.Pointer = 10; }")]
    [InlineData("struct Data { public int* Pointer; } void readonly Test(Data* input) { Data local = Data(); local.Pointer = input->Pointer; *local.Pointer = 10; }")]
    [InlineData("struct Data { public int* Pointer; } void readonly Test(int* input) { Data[] local = Data[2]; local[0].Pointer = input; *local[0].Pointer = 10; }")]
    [InlineData("void readonly Destroy(int* input) { free(input); }")]
    [InlineData("struct Data { public int Value; public Data() { Value = 10; } } void readonly Test() { Data data = Data(); }")]
    [InlineData("struct Data { public int* Pointer; public Data(int* input) { Pointer = input; } } void readonly Test(int* output) { Data data = Data(output); *data.Pointer = 10; }")]
    [InlineData("struct Data { public int* Pointer; public Data(int* input) { Pointer = input; } } int readonly Test() { Data data = Data(State.Pointer); return *data.Pointer; }")]
    [InlineData("struct Data { public int Value = 10; public Data() { Value += 2; } } int readonly Test() { Data data = Data(); return data.Value; }")]
    [InlineData("struct Base { public int Value; public Base() { Value = 10; } } struct Data : Base { public Data() : base() { Value++; } } void readonly Test() { Data data = Data(); }")]
    [InlineData("struct Data { public int* Pointer; public ~Data() { *Pointer += 1; } } void readonly Destroy(Data* input) { free(input); }")]
    [InlineData("struct Data { public int* Pointer; public Data(int* output) { Pointer = output; } public ~Data() { *Pointer += 1; } } void readonly Test(int* output) { Data* value = new Data(output); free(value); }")]
    [InlineData("struct Data { public int* Pointer; public ~Data() { *Pointer += 1; } } void readonly Test(int* output) { Data[] values = Data[2]; values[0].Pointer = output; values[1].Pointer = output; }")]
    [InlineData("struct Data { public int Value; public ~Data() { Value = 0; } } void readonly Test() { Data[] values = new Data[2]; free(values); }")]
    [InlineData("struct Data { public int Value; public int Current { get { return Value; } set { Value = value; } } } int readonly Test() { Data data = Data(); data.Current = 10; data.Current += 2; return data.Current; }")]
    [InlineData("struct Data { public int Value; public int this[int x, int y] { get { return Value; } set { Value = value + x + y; } } } void readonly Test(Data& data) { data[2, 3] = 10; data[2, 3] += 1; }")]
    [InlineData("struct Data { public int Value; public int Current { get { return Value; } set { Value = value; } } } void readonly Test(Data* data) { data->Current = 10; }")]
    [InlineData("struct Data { public int* Pointer; public int* Current { get { return Pointer; } set { Pointer = value; } } } void readonly Test(int* output) { Data data = Data(); data.Current = output; *data.Current = 10; }")]
    [InlineData("struct Value { public int Current { get { return 1; } } } int readonly Test(Value& value) { return value.Current; }")]
    [InlineData("struct Value { public int this[int index] { get { return 1; } } } int readonly Test(Value& value) { return value[0]; }")]
    [InlineData("struct Value { public Value() {} } void readonly Test() { Value value = Value(); }")]
    [InlineData("interface IValue { int Current { get; set; } } struct Data : IValue { public int Value; public int Current { get { return Value; } set { Value = value; } } } void readonly Test(IValue& data) { data.Current = 10; }")]
    [InlineData("struct Base { public int Value; public virtual int Current { get { return Value; } set { Value = value; } } } struct Data : Base { public override int Current { get { return Value; } set { Value = value + 1; } } } void readonly Test(Base& data) { data.Current = 10; }")]
    [InlineData("interface IValue { int Current { set; } } struct Base { public int Value; public int Current { set { Value = value; } } } struct Data : Base, IValue {} void readonly Test(IValue& data) { data.Current = 10; }")]
    [InlineData("struct Other { public int Current { set { State.Value = value; } } } interface IValue { int Current { set; } } struct Data : IValue { public int Value; public int Current { set { Value = value; } } } void readonly Test(IValue& data) { data.Current = 10; }")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; public Pair(int* hidden, int* output) { Hidden = hidden; Output = output; } } void readonly Test(int* output) { Pair value = Pair(State.Pointer, output); *value.Output = 10; }")]
    [InlineData("struct Pair { public int* Hidden = State.Pointer; public int* Output; } void readonly Test(int* output) { Pair value = Pair { State.Pointer, output }; *value.Output = 10; }")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; public Pair(int* hidden, int* output) { Hidden = hidden; Output = output; } } void readonly Test(int* output) { Pair* value = new Pair(State.Pointer, output); *value->Output = 10; free(value); }")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; public ~Pair() { *Output += 1; } } void readonly Test(int* output) { Pair* value = new Pair { State.Pointer, output }; free(value); }")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; public ~Pair() { *Output += 1; } } void readonly Test(int* output) { Pair[] values = Pair[1]; values[0].Hidden = State.Pointer; values[0].Output = output; }")]
    [InlineData("struct Base { public int* Hidden; } struct Pair : Base { public int* Output; } void readonly Test(int* output) { Pair value = Pair(); value.Hidden = State.Pointer; value.Output = output; Pair copy = value; *copy.Output = 10; }")]
    [InlineData("struct Leaf { public int* Pointer; } struct Node { public Leaf A; public Leaf B; } struct Tree { public Node A; public Node B; } void readonly Test(int* output) { Tree tree = Tree(); tree.A.A.Pointer = State.Pointer; tree.A.B.Pointer = output; Tree copy = tree; *copy.A.B.Pointer = 10; }")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; public void readonly Test(int* output) { Pair local = Pair { Hidden, output }; *local.Output = 10; } }")]
    [InlineData("struct Pair { public int* Output; } void readonly Test(Pair& value, int* output) { value.Output = output; *value.Output = 10; }")]
    [InlineData("struct Pair { public readonly int* Input; public int* Output; } Pair readonly Test(int* output) { Pair value = Pair { State.Pointer, output }; return value; }")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; } struct Holder { public Pair Storage; public Pair Value { get { return Storage; } set { Storage = value; } } } void readonly Test(int* output) { Holder value = Holder(); value.Value = Pair { State.Pointer, output }; Pair copy = value.Value; *copy.Output = 10; }")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; public int*& Value { get { return Output; } } } void readonly Test(int* output) { Pair value = Pair { State.Pointer, output }; int*& alias = value.Value; *alias = 10; }")]
    [InlineData("struct Node { public Node* Next; public int* Value; } void readonly Use(Node& node) { *node.Value = 10; } void readonly Test(int* output) { Node node = Node(); node.Next = &node; node.Value = output; Use(node); }")]
    [InlineData("struct Item { public int** Slot; public ~Item() { **Slot += 1; } } void readonly Test(int* output) { int* ptr = State.Pointer; { Item[] items = Item[1]; items[0].Slot = &ptr; ptr = output; } }")]
    [InlineData("struct Item { public int** Slot; public int* Value; public ~Item() { *Slot = Value; } } void readonly Test(int* output) { int* ptr = output; { Item[] items = Item[1]; items[0].Slot = &ptr; items[0].Value = State.Pointer; } ptr = output; *ptr = 10; }")]
    [InlineData("struct Value { public int* Pointer; public int* Current { get { int* result = Pointer; Pointer = State.Pointer; return result; } } } void readonly Test(int* output) { Value value = Value { output }; *value.Current = 10; }")]
    [InlineData("struct Value { public int* Pointer; public Value(int* pointer) { Pointer = State.Pointer; Pointer = pointer; } } void readonly Test(int* output) { Value value = Value(output); *value.Pointer = 10; }")]
    [InlineData("struct Data { public int* Pointer; public int* Current { set { if (value != null) Pointer = value; else return; Pointer = value; } get { return Pointer; } } } void readonly Test(int* output) { Data data = Data { output }; data.Current = output; *data.Current = 10; }")]
    public void Analyzer_AllowsExplicitEffectsInReadonlyFunctions(string source)
    {
        Compilation compilation = CreateReadonlyEffectCompilation(source);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("void readonly Test() { State.Value = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test() { State.Value++; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test() { State.Value += 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test() { int* p = &State.Value; *p = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test() { int& p = State.Value; p = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test() { int* p = State.Pointer; *p = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test() { int* p = State.Pointer; int** alias = &p; int* q = *alias; *q = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test(int* input) { int* p = input; int** alias = &p; *alias = State.Pointer; *p = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test(bool branch, int* input) { int* p = input; if (branch) p = State.Pointer; *p = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test(bool loop, int* input) { int* p = input; while (loop) { *p = 10; p = State.Pointer; } }", "cannot mutate hidden state")]
    [InlineData("void readonly Test(int key, int* input) { int* p = input; switch(key) { case 1: p = State.Pointer; break; default: break; } *p = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test() { int[] a = State.Values; a[0] = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test(int[] a) { a[0] = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Holder { public int* Pointer; } void readonly Test(Holder value) { *value.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Holder { public int* Pointer; } void readonly Test(readonly Holder& value) { Holder copy = value; *copy.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Holder { public int* Pointer; public void readonly Test() { *Pointer = 10; } }", "cannot mutate hidden state")]
    [InlineData("struct Holder { public int* Pointer; public void readonly Test() { int* copy = Pointer; *copy = 10; } }", "cannot mutate hidden state")]
    [InlineData("struct Holder { public int* Pointer; public void readonly Test() { Write(Pointer); } }", "cannot pass a mutable capability")]
    [InlineData("struct Holder { public int Value; public void readonly Test() { Value = 10; } }", "must be writable")]
    [InlineData("void readonly Test(readonly int* value) { *value = 10; }", "must be writable")]
    [InlineData("void readonly Test(readonly int& value) { value = 10; }", "must be writable")]
    [InlineData("void readonly Test() { Write(State.Pointer); }", "cannot pass a mutable capability")]
    [InlineData("void readonly Test() { Write(&State.Value); }", "cannot pass a mutable capability")]
    [InlineData("int* readonly Test() { return State.Pointer; }", "cannot return a mutable capability")]
    [InlineData("void readonly Test(int** output) { *output = State.Pointer; }", "cannot store a mutable capability")]
    [InlineData("void readonly WriteNested(int** value) { **value = 10; } void readonly Test() { int* local = State.Pointer; WriteNested(&local); }", "cannot pass a mutable capability")]
    [InlineData("void readonly WriteNested(int*& value) { *value = 10; } void readonly Test() { int* local = State.Pointer; WriteNested(local); }", "cannot pass a mutable capability")]
    [InlineData("struct Holder { public int* Pointer; } void readonly WriteNested(Holder& value) { *value.Pointer = 10; } void readonly Test() { Holder local = Holder { State.Pointer }; WriteNested(local); }", "cannot pass a mutable capability")]
    [InlineData("int** readonly Test() { int* local = State.Pointer; return &local; }", "cannot return a mutable capability")]
    [InlineData("int*[] readonly Test() { int*[] local = new int*[1]; local[0] = State.Pointer; return local; }", "cannot return a mutable capability")]
    [InlineData("void readonly Test(int*** output) { int* local = State.Pointer; *output = &local; }", "cannot store a mutable capability")]
    [InlineData("int*& readonly Identity(int*& value) { return value; } void readonly Test(int* input) { int* local = input; Identity(local) = State.Pointer; *local = 10; }", "cannot mutate hidden state")]
    [InlineData("int** readonly Identity(int** value) { return value; } void readonly Test(int* input) { int* local = input; *Identity(&local) = State.Pointer; *local = 10; }", "cannot mutate hidden state")]
    [InlineData("void readonly Test() { free(State.Pointer); }", "cannot mutate hidden state")]
    [InlineData("void readonly Test(readonly int* input) { free(input); }", "cannot free memory through a readonly pointer")]
    [InlineData("void Effectful() {} void readonly Test() { Effectful(); }", "cannot call non-readonly")]
    [InlineData("extern void External(); void readonly Test() { External(); }", "cannot call non-readonly")]
    [InlineData("struct Value { public static void Effectful() {} } void readonly Test() { Value.Effectful(); }", "cannot call non-readonly")]
    [InlineData("interface IValue { void Effectful(); } void readonly Test(IValue& value) { value.Effectful(); }", "cannot verify effects of member 'Effectful' without an implementation")]
    [InlineData("struct Value { public int Current { set { State.Value = value; } } } void readonly Test(Value& value) { value.Current = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Value { public ~Value() { State.Value++; } } void readonly Test(Value* value) { free(value); }", "cannot mutate hidden state")]
    [InlineData("struct Value { public ~Value() { State.Value++; } } void readonly Test() { Value[] values = Value[1]; }", "cannot mutate hidden state")]
    [InlineData("int Effectful() { return 1; } struct Value { public int Field = Effectful(); } void readonly Test() { Value[] values = Value[1]; }", "cannot call non-readonly")]
    [InlineData("struct Value { public int Field = (State.Value = 1); } void readonly Test() { Value value = Value {}; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; } void readonly Test() { Data data = Data(); data.Pointer = State.Pointer; *data.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public void readonly Test() { Data data = Data(); data.Pointer = Pointer; *data.Pointer = 10; } }", "cannot mutate hidden state")]
    [InlineData("struct Inner { public int* Pointer; } struct Outer { public Inner Inner; } void readonly Test() { Outer local = Outer(); local.Inner.Pointer = &State.Value; Outer& alias = local; *alias.Inner.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; } void readonly Test() { Data data = Data(); data.Pointer = State.Pointer; free(data.Pointer); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int Value; public Data() { State.Value++; } } void readonly Test() { Data data = Data(); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int Value = (State.Value = 10); public Data() {} } void readonly Test() { Data data = Data(); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public Data(int* input) { Pointer = input; } } void readonly Test() { Data data = Data(State.Pointer); *data.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public Data() { Pointer = State.Pointer; } } void readonly Test() { Data data = Data(); *data.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public Data(int* input) { *input = 10; } } void readonly Test() { Data data = Data(State.Pointer); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public ~Data() { *Pointer = 10; } } void readonly Test() { Data* data = new Data(); data->Pointer = State.Pointer; free(data); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public ~Data() { *Pointer = 10; } } void readonly Test() { Data[] data = Data[1]; data[0].Pointer = State.Pointer; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public int* Current { get { return Pointer; } set { Pointer = value; } } } void readonly Test() { Data data = Data(); data.Current = State.Pointer; *data.Current = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int Value; public int Current { get { return Value; } set { Value = value; } } } struct Globals { public static Data Item; } void readonly Test() { Globals.Item.Current = 10; }", "must be writable")]
    [InlineData("struct Data { public int Value; public int Current { get { State.Value++; return Value; } } } int readonly Test() { Data data = Data(); return data.Current; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int Value; public int this[int index] { set { State.Value = value; } } } void readonly Test() { Data data = Data(); data[0] = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public int*& Current { get { return Pointer; } } } void readonly Test(int* output) { Data data = Data(); data.Pointer = output; int*& alias = data.Current; alias = State.Pointer; *data.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Base { public int Value; public virtual int Current { set { Value = value; } } } struct Data : Base { public override int Current { set { State.Value = value; } } } void readonly Test(Base& data) { data.Current = 10; }", "cannot mutate hidden state")]
    [InlineData("interface IValue { int Current { set; } } struct Data : IValue { public int Current { set { State.Value = value; } } } void readonly Test(IValue& data) { data.Current = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public void readonly Test() { Data copy = Data { Pointer }; *copy.Pointer = 10; } }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public void readonly Test() { Data* copy = new Data { Pointer }; *copy->Pointer = 10; } }", "cannot mutate hidden state")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; public Pair(int* hidden, int* output) { Hidden = hidden; Output = output; } } void readonly Test(int* output) { Pair value = Pair(State.Pointer, output); *value.Hidden = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Pair { public int* Hidden = State.Pointer; public int* Output; } void readonly Test(int* output) { Pair value = Pair { State.Pointer, output }; *value.Hidden = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; } Pair readonly Test(int* output) { Pair value = Pair { State.Pointer, output }; return value; }", "cannot return a mutable capability")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; } Pair* readonly Test(int* output) { Pair* value = new Pair { State.Pointer, output }; return value; }", "cannot return a mutable capability")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; public ~Pair() { *Hidden += 1; } } void readonly Test(int* output) { Pair* value = new Pair { State.Pointer, output }; free(value); }", "cannot mutate hidden state")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; } struct Holder { public Pair Storage; public Pair Value { get { return Storage; } set { Storage = value; } } } void readonly Test(int* output) { Holder value = Holder(); value.Value = Pair { State.Pointer, output }; Pair copy = value.Value; *copy.Hidden = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; } int*& readonly GetOutput(Pair& value) { return value.Output; } void readonly Test(int* output) { Pair value = Pair { output, output }; GetOutput(value) = State.Pointer; *value.Output = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Pair { public int* Hidden; public int* Output; } struct Holder { public Pair Storage; } void readonly Test(int* output) { Holder value = Holder { Pair { State.Pointer, output } }; Holder* alias = &value; Write(alias->Storage.Hidden); }", "cannot pass a mutable capability")]
    [InlineData("struct Item { public int** Slot; public ~Item() { **Slot += 1; } } void readonly Test(int* output) { int* ptr = output; { Item[] items = Item[1]; items[0].Slot = &ptr; ptr = State.Pointer; } }", "cannot mutate hidden state")]
    [InlineData("struct Item { public int** Slot; public int* Value; public ~Item() { *Slot = Value; } } void readonly Test(int* output) { int* ptr = output; { Item[] items = Item[1]; items[0].Slot = &ptr; items[0].Value = State.Pointer; } *ptr = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Item { public int** Slot; public ~Item() { **Slot += 1; } } void readonly Test(bool stop, int* output) { int* ptr = State.Pointer; { Item[] items = Item[1]; items[0].Slot = &ptr; if (stop) return; ptr = output; } }", "cannot mutate hidden state")]
    [InlineData("struct Value { public int* Pointer; public int* Current { get { int* result = Pointer; Pointer = State.Pointer; return result; } } } void readonly Test(int* output) { Value value = Value { output }; *value.Current = 10; *value.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Base { public int* Pointer; public virtual int* Current { set { Pointer = value; } } } struct Derived : Base { public override int* Current { set { } } } void readonly Test(bool choose, Base& other, int* output) { Base value = Base { State.Pointer }; Base* alias = &value; if (choose) alias = &other; alias->Current = output; *value.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Item { public int** Slot; public ~Item() { **Slot += 1; } } void readonly Test(int* output) { int* ptr = output; while (true) { Item[] items = Item[1]; items[0].Slot = &ptr; ptr = State.Pointer; break; } }", "cannot mutate hidden state")]
    public void Analyzer_RejectsHiddenEffectsInReadonlyFunctions(string source, string expected)
    {
        Compilation compilation = CreateReadonlyEffectCompilation(source);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("struct Data { public void Reset() {} } void readonly Test(Data& data) { data.Reset(); }")]
    [InlineData("struct Data { public int Value; public void Reset() { Value = 0; } } void readonly Test() { Data data = Data(); data.Reset(); }")]
    [InlineData("struct Data { public int Value; public void Reset() { Value = 0; } } void readonly Test(Data& data) { data.Reset(); }")]
    [InlineData("struct Data { public int Value; public void Reset() { Value = 0; } } void readonly Test(Data* data) { data->Reset(); }")]
    [InlineData("struct Data { public int Value; public void Reset() { Value = 0; } public void readonly Test(Data& other) { other.Reset(); Data local = Data(); local.Reset(); } }")]
    [InlineData("struct Data { public int Value; public void Reset() { Clear(); } void Clear() { Value = 0; } } void readonly Test(Data& data) { data.Reset(); }")]
    [InlineData("struct Data { public int* Hidden; public int* Output; public void Write() { *Output = 10; } } void readonly Test(int* output) { Data data = Data { State.Pointer, output }; data.Write(); }")]
    [InlineData("struct Data { public int* Hidden; public int* Output; public void Write() { *Output = 10; } } void readonly Test(int* output) { Data data = Data { State.Pointer, output }; Data copy = data; Data* alias = &copy; alias->Write(); }")]
    [InlineData("struct Data { public int* Pointer; public void Write(int* output) { Pointer = State.Pointer; Pointer = output; *Pointer = 10; } } void readonly Test(int* output) { Data data = Data(); data.Write(output); *data.Pointer = 20; }")]
    [InlineData("struct Data { public int* Pointer; public void Set(int* output) { Pointer = output; } public void Write() { *Pointer = 10; } } void readonly Test(int* output) { Data data = Data { State.Pointer }; data.Set(output); data.Write(); }")]
    [InlineData("struct Data { public void Write(int* output) { *output = 10; } } void readonly Test(int* output) { Data data = Data(); data.Write(output); }")]
    [InlineData("struct Data { public void Read(readonly int* input) { int value = *input; } } void readonly Test() { Data data = Data(); data.Read(State.Pointer); }")]
    [InlineData("struct Data { public int* Pointer; public int* Get() { return Pointer; } } void readonly Test(int* output) { Data data = Data { output }; *data.Get() = 10; }")]
    [InlineData("struct Resource { public int* Memory; public void Dispose() { free(Memory); Memory = null; } } void readonly Test(Resource& resource) { resource.Dispose(); }")]
    [InlineData("struct Resource { public int* Memory; public void Dispose() { free(Memory); Memory = null; } } void readonly Test(int* memory) { Resource resource = Resource { memory }; resource.Dispose(); }")]
    [InlineData("struct Data { public int Value; public void Reset() { Value = 0; } public Data() { Reset(); } public ~Data() { Reset(); } public int Current { set { Reset(); Value = value; } } } void readonly Test() { Data* data = new Data(); data->Current = 10; free(data); }")]
    [InlineData("struct Data { public int Value; public void Recurse(int count) { if (count > 0) Recurse(count - 1); Value++; } } void readonly Test(Data& data) { data.Recurse(2); }")]
    [InlineData("struct Data { public int Value; public void First(int count) { if (count > 0) Second(count - 1); Value++; } void Second(int count) { First(count); } } void readonly Test(Data& data) { data.First(2); }")]
    [InlineData("struct Base { public int Value; public virtual void Reset() { Value = 0; } } struct Data : Base { public override void Reset() { Value = 1; } } void readonly Test(Base& data) { data.Reset(); }")]
    [InlineData("interface IData { void Reset(); } struct Data : IData { public int Value; public void Reset() { Value = 0; } } void readonly Test(IData& data) { data.Reset(); }")]
    public void Analyzer_ContextuallyAllowsMutableInstanceMethods(string source)
    {
        Compilation compilation = CreateReadonlyEffectCompilation(source);
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("struct Data { public void Reset() {} } struct Globals { public static Data* Item; } void readonly Test() { Globals.Item->Reset(); }", "cannot call mutable instance method 'Reset' on hidden state")]
    [InlineData("struct Data { public void Reset() {} } struct Globals { public static Data Item; } void readonly Test() { Globals.Item.Reset(); }", "readonly")]
    [InlineData("struct Data { public void Reset() {} } struct Owner { public Data Item; public void readonly Test() { Item.Reset(); } }", "readonly")]
    [InlineData("struct Data { public void Reset() {} } void readonly Test(readonly Data& data) { data.Reset(); }", "readonly")]
    [InlineData("struct Data { public void Reset() {} } void readonly Test(readonly Data* data) { data->Reset(); }", "readonly")]
    [InlineData("struct Data { public void Reset() {} public void readonly Test() { Reset(); } }", "cannot call mutable method")]
    [InlineData("struct Data { public int Value; public void Reset() { Value = 0; State.Value++; } } void readonly Test(Data& data) { data.Reset(); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public void Reset() { Clear(); } void Clear() { State.Value++; } } void readonly Test(Data& data) { data.Reset(); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Hidden; public int* Output; public void Write() { *Hidden = 10; } } void readonly Test(int* output) { Data data = Data { State.Pointer, output }; data.Write(); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public void Write() { *Pointer = 10; } } void readonly Test() { Data data = Data { &State.Value }; data.Write(); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public readonly int* Pointer; public void Write() { *Pointer = 10; } } void readonly Test(Data& data) { data.Write(); }", "must be writable")]
    [InlineData("struct Data { public void Write(int* output) { *output = 10; } } void readonly Test() { Data data = Data(); data.Write(State.Pointer); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public void Write(readonly int* output) { *output = 10; } } void readonly Test(int* output) { Data data = Data(); data.Write(output); }", "must be writable")]
    [InlineData("struct Data { public int* Pointer; public void Write(bool condition, int* output) { Pointer = State.Pointer; if (condition) Pointer = output; *Pointer = 10; } } void readonly Test(bool condition, int* output) { Data data = Data(); data.Write(condition, output); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public void Set(int* output) { Pointer = output; } } void readonly Test(int* output) { Data data = Data { output }; data.Set(State.Pointer); *data.Pointer = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public int* Get() { return Pointer; } } void readonly Test() { Data data = Data { State.Pointer }; *data.Get() = 10; }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public void Set() { Pointer = State.Pointer; } } void readonly Test(Data& data) { data.Set(); }", "cannot store a mutable capability")]
    [InlineData("struct Resource { public int* Memory; public void Dispose() { free(Memory); Memory = null; } } void readonly Test() { Resource resource = Resource { State.Pointer }; resource.Dispose(); }", "cannot mutate hidden state")]
    [InlineData("struct Resource { public void Dispose() {} } struct Globals { public static Resource* Item; } void readonly Test() { Globals.Item->Dispose(); }", "cannot call mutable instance method 'Dispose' on hidden state")]
    [InlineData("void Helper() {} struct Data { public void Reset() { Helper(); } } void readonly Test(Data& data) { data.Reset(); }", "cannot call non-readonly")]
    [InlineData("struct Data { static void Helper() {} public void Reset() { Data.Helper(); } } void readonly Test(Data& data) { data.Reset(); }", "cannot call non-readonly")]
    [InlineData("struct Data { public void Recurse(int count) { if (count > 0) Recurse(count - 1); State.Value++; } } void readonly Test(Data& data) { data.Recurse(2); }", "cannot mutate hidden state")]
    [InlineData("struct Data { public int* Pointer; public void Recurse(int count) { if (count > 0) Recurse(count - 1); *Pointer = 10; } } void readonly Test() { Data data = Data { State.Pointer }; data.Recurse(2); }", "hidden")]
    [InlineData("struct Base { public virtual void Reset() {} } struct Data : Base { public override void Reset() { State.Value++; } } void readonly Test(Base& data) { data.Reset(); }", "cannot mutate hidden state")]
    [InlineData("interface IData { void Reset(); } struct Safe : IData { public void Reset() {} } struct Unsafe : IData { public void Reset() { State.Value++; } } void readonly Test(IData& data) { data.Reset(); }", "cannot mutate hidden state")]
    [InlineData("struct Arg { public int* Pointer; } struct Data { public void Recurse(Arg argument, int count) { if (count > 0) Recurse(Arg { State.Pointer }, count - 1); *argument.Pointer = 10; } } void readonly Test(int* output) { Data data = Data(); data.Recurse(Arg { output }, 2); }", "hidden")]
    [InlineData("struct Data { public void Recurse(int[] argument, int count) { if (count > 0) Recurse(State.Values, count - 1); argument[0] = 10; } } void readonly Test() { Data data = Data(); int[] values = new int[1]; data.Recurse(values, 2); free(values); }", "hidden")]
    public void Analyzer_ContextuallyRejectsMutableInstanceMethodHiddenEffects(string source, string expected)
    {
        Compilation compilation = CreateReadonlyEffectCompilation(source);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Holder value = Holder { State.Pointer }; Consume(value);")]
    [InlineData("Holder value = Holder { State.Pointer }; Holder& alias = value; Consume(alias);")]
    [InlineData("Holder value = Holder { State.Pointer }; Holder* alias = &value; Consume(*alias);")]
    [InlineData("Holder value = Holder { State.Pointer }; Holder copy = value; Consume(copy);")]
    [InlineData("Outer outer = Outer { Holder { State.Pointer } }; Consume(outer.Inner);")]
    [InlineData("Holder value = Holder { State.Pointer }; ConsumePointer(&value);")]
    [InlineData("int* pointer = State.Pointer; ConsumeSlot(pointer);")]
    public void Analyzer_ReferenceArgumentsPreserveHiddenReferentFields(string body)
    {
        Compilation compilation = CreateReadonlyEffectCompilation("""
            struct Holder { public int* Pointer; }
            struct Outer { public Holder Inner; }
            void readonly Consume(Holder& value) { *value.Pointer = 10; }
            void readonly ConsumePointer(Holder* value) { *value->Pointer = 10; }
            void readonly ConsumeSlot(int*& value) { *value = 10; }
            void readonly Test() {
            """ + body + "}");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot pass a mutable capability", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Data[] values = new Data[2]; values[0].Pointer = State.Pointer; values[1].Pointer = output; *values[1].Pointer = 10; free(values);")]
    [InlineData("Data[] values = Data[2]; values[0].Pointer = State.Pointer; values[1].Pointer = output; Data[] alias = values; *alias[1].Pointer = 10;")]
    [InlineData("Data[,] values = Data[2, 2]; values[0, 1].Pointer = State.Pointer; values[1, 0].Pointer = output; *values[1, 0].Pointer = 10;")]
    [InlineData("int*[] values = new int*[2]; values[0] = State.Pointer; values[1] = output; *values[1] = 10; free(values);")]
    [InlineData("Data[] values = Data[2]; values[key].Pointer = State.Pointer; values[1].Pointer = output; *values[1].Pointer = 10;")]
    [InlineData("Data[] values = Data[2]; values[0].Pointer = State.Pointer; values[0].Pointer = output; *values[0].Pointer = 10;")]
    public void Analyzer_ReadonlyTracksSeparateArrayElements(string body)
    {
        Compilation compilation = CreateReadonlyEffectCompilation("struct Data { public int* Pointer; } void readonly Test(int key, int* output) {" + body + "}");
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("Data[] values = Data[2]; values[0].Pointer = State.Pointer; values[1].Pointer = output; *values[0].Pointer = 10;")]
    [InlineData("Data[] values = Data[2]; values[0].Pointer = State.Pointer; values[1].Pointer = output; *values[key].Pointer = 10;")]
    [InlineData("Data[] values = Data[2]; values[1].Pointer = output; values[key].Pointer = State.Pointer; *values[1].Pointer = 10;")]
    [InlineData("Data[] values = Data[2]; values[0].Pointer = State.Pointer; values[1].Pointer = output; Data* pointer = &values[1]; *pointer[-1].Pointer = 10;")]
    [InlineData("Data[] values = Data[2]; values[0].Pointer = State.Pointer; values[1].Pointer = output; int** pointer = &values[1].Pointer; *pointer[-1] = 10;")]
    [InlineData("Data[] values = Data[2]; values[0].Pointer = State.Pointer; values[1].Pointer = output; if (condition) values[0].Pointer = output; *values[0].Pointer = 10;")]
    public void Analyzer_ReadonlyArrayAliasesRetainPossibleHiddenElements(string body)
    {
        Compilation compilation = CreateReadonlyEffectCompilation("struct Data { public int* Pointer; } void readonly Test(bool condition, int key, int* output) {" + body + "}");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot mutate hidden state", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Data* pointer", "pointer[-1].Pointer", "&values[1]")]
    [InlineData("Data& value", "(&value)[-1].Pointer", "values[1]")]
    [InlineData("int** pointer", "pointer[-1]", "&values[1].Pointer")]
    public void Analyzer_ReadonlyArrayElementArgumentsCannotHideReachableSiblings(string parameter, string target, string argument)
    {
        Compilation compilation = CreateReadonlyEffectCompilation("struct Data { public int* Pointer; } void readonly WriteSibling(" + parameter + ") { *" + target + " = 10; } void readonly Test(int* output) { Data[] values = Data[2]; values[0].Pointer = State.Pointer; values[1].Pointer = output; WriteSibling(" + argument + "); }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot pass a mutable capability", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("struct Data { public int* Hidden; public int* Output; public void A(int n) { if (n > 0) B(n - 1); *Output = 10; } void B(int n) { C(n); } void C(int n) { A(n); } } void readonly Test(int* output) { Data data = Data { State.Pointer, output }; data.A(3); }")]
    [InlineData("struct Arg { public int* Hidden; public int* Output; } struct Data { public void A(Arg arg, int n) { if (n > 0) B(arg, n - 1); *arg.Output = 10; } void B(Arg arg, int n) { A(arg, n); } } void readonly Test(int* output) { Data data = Data(); data.A(Arg { State.Pointer, output }, 3); }")]
    [InlineData("struct Data { public void A(int[] values, int n) { if (n > 0) B(values, n - 1); values[0] = 10; } void B(int[] values, int n) { A(values, n); } } void readonly Test() { Data data = Data(); int[] values = new int[2]; data.A(values, 3); free(values); }")]
    [InlineData("interface IReset { void Reset(); } struct Base : IReset { public void Reset() {} } struct Good : Base {} struct Unrelated : IReset { public void Reset() { State.Value++; } } void readonly Test(Base& value) { IReset view = value; view.Reset(); }")]
    public void Analyzer_ReadonlyVerifiesRecursiveEffectsAndBoundedDispatch(string source)
    {
        Compilation compilation = CreateReadonlyEffectCompilation(source);
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("struct Arg { public int* Pointer; } struct Data { public void A(Arg arg, int n) { if (n > 0) B(Arg { State.Pointer }, n - 1); *arg.Pointer = 10; } void B(Arg arg, int n) { A(arg, n); } } void readonly Test(int* output) { Data data = Data(); data.A(Arg { output }, 3); }")]
    [InlineData("struct Data { public int* Pointer; public void A(int n) { if (n > 0) B(n - 1); *Pointer = 10; } void B(int n) { Pointer = State.Pointer; A(n); } } void readonly Test(int* output) { Data data = Data { output }; data.A(3); }")]
    [InlineData("struct Data { public int* Pointer; public int* A(int n) { if (n > 0) return B(n - 1); return State.Pointer; } int* B(int n) { return A(n); } } void readonly Test() { Data data = Data(); *data.A(3) = 10; }")]
    public void Analyzer_ReadonlyRejectsHiddenEffectsAcrossRecursiveCycles(string source)
    {
        Compilation compilation = CreateReadonlyEffectCompilation(source);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("hidden state", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("void readonly Test() { Base value = Base(); value.Reset(); }")]
    [InlineData("void readonly Test() { Base* value = new Base(); value->Reset(); free(value); }")]
    [InlineData("void readonly Test() { Base value = Base(); Base& alias = value; alias.Reset(); }")]
    [InlineData("void readonly Test() { Base value = Base(); Base* alias = &value; alias->Reset(); }")]
    [InlineData("void readonly Test() { Base value = Base(); Base copy = value; copy.Reset(); }")]
    [InlineData("void readonly Test() { Good value = Good(); Base* alias = &value; alias->Reset(); }")]
    [InlineData("void readonly Test() { Base value = Base(); IReset view = value; view.Reset(); }")]
    [InlineData("void readonly Test() { Base value = Base(); IReset view = value; IReset& alias = view; alias.Reset(); }")]
    [InlineData("void readonly Test() { Good value = Good(); Base* alias = &value; IReset view = *alias; view.Reset(); }")]
    [InlineData("interface IEffect { void Reset(); } struct Root : IEffect { public virtual void Reset() { State.Value++; } } struct Safe : Root { public override void Reset() {} } void readonly Test() { Safe value = Safe(); Root* pointer = &value; IEffect view = *pointer; view.Reset(); }")]
    [InlineData("void readonly Test(bool condition) { while (condition) { Base value = Base(); value.Reset(); break; } }")]
    [InlineData("void readonly Test(bool condition) { while (condition) { Base* value = new Base(); value->Reset(); free(value); } }")]
    [InlineData("void readonly Test(bool condition) { while (condition) { Base value = Base(); IReset view = value; view.Reset(); } }")]
    [InlineData("void readonly Test() { Good value = Good(); IReset view = value; Good replacement = Good(); value = replacement; view.Reset(); }")]
    [InlineData("void readonly Test(bool condition) { Base first = Base(); Good second = Good(); IReset view = first; if (condition) view = second; view.Reset(); }")]
    [InlineData("void readonly Test(bool condition) { Base first = Base(); Good second = Good(); Base* value = &first; if (condition) value = &second; value->Reset(); }")]
    [InlineData("struct Wrapper { public Base Value; public Wrapper() { Value = Base(); } public void Reset() { Value.Reset(); } } void readonly Test() { Wrapper value = Wrapper(); value.Reset(); }")]
    [InlineData("struct Wrapper { public void Reset(Base& value) { value.Reset(); } } void readonly Test() { Wrapper wrapper = Wrapper(); Base value = Base(); wrapper.Reset(value); }")]
    [InlineData("void readonly Test(Base value) { value = Base(); value.Reset(); }")]
    public void Analyzer_ReadonlyDispatchUsesKnownConcreteReceiver(string source)
    {
        Compilation compilation = CreateReadonlyDispatchCompilation(source);
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("void readonly Test(Base& value) { value.Reset(); }")]
    [InlineData("void readonly Test(IReset& value) { value.Reset(); }")]
    [InlineData("void readonly Test(IReset value) { value.Reset(); }")]
    [InlineData("void readonly Test() { Evil value = Evil(); Base* alias = &value; alias->Reset(); }")]
    [InlineData("void readonly Test() { Evil value = Evil(); IReset view = value; view.Reset(); }")]
    [InlineData("void readonly Test(bool condition) { Base safe = Base(); Evil evil = Evil(); IReset view = safe; if (condition) view = evil; view.Reset(); }")]
    [InlineData("void readonly Test(IReset& other) { Base safe = Base(); IReset view = safe; IReset& alias = view; alias = other; view.Reset(); }")]
    [InlineData("interface IWrite { void Write(); } struct Writer : IWrite { public int* Pointer; public void Write() { *Pointer = 10; } } void readonly Test() { Writer writer = Writer { State.Pointer }; IWrite view = writer; view.Write(); }")]
    [InlineData("void readonly Test() { Evil value = Evil(); Base* alias = &value; IReset view = *alias; view.Reset(); }")]
    [InlineData("void readonly Test(bool condition) { Base safe = Base(); Evil evil = Evil(); Base* value = &safe; if (condition) value = &evil; value->Reset(); }")]
    [InlineData("void readonly Test(bool condition, Base& other) { Base value = Base(); if (condition) value = other; value.Reset(); }")]
    [InlineData("void readonly Test(bool condition, Base& other) { Base value = Base(); while (condition) { value.Reset(); value = other; } }")]
    [InlineData("void readonly Test(Base& other) { Base value = Base(); Base& alias = value; alias = other; value.Reset(); }")]
    [InlineData("void readonly Touch(Base& value) { value.Value = 1; } void readonly Test() { Base value = Base(); Touch(value); value.Reset(); }")]
    public void Analyzer_ReadonlyDispatchPreservesUnknownAndAlternativeTargets(string source)
    {
        Compilation compilation = CreateReadonlyDispatchCompilation(source);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot mutate hidden state", StringComparison.Ordinal));
    }

    private static Compilation CreateReadonlyDispatchCompilation(string source) => CreateReadonlyEffectCompilation("""
        interface IReset { void Reset(); }
        struct Base : IReset
        {
            public int Value;
            public virtual void Reset() { Value = 0; }
        }
        struct Good : Base { public override void Reset() { Value = 1; } }
        struct Evil : Base { public override void Reset() { State.Value++; } }
        """ + source);

    [Fact]
    public void Analyzer_RecordsReadonlyExternContractOnFunctionSymbol()
    {
        Compilation compilation = CreateReadonlyEffectCompilation("""
            extern int readonly Trusted(int value);
            extern int Effectful(int value);
            int readonly Use(int value) { return Trusted(value); }
            """);
        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol space = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        Assert.True(space.Functions.Single(f => f.Name == "Trusted").IsReadonly);
        Assert.False(space.Functions.Single(f => f.Name == "Effectful").IsReadonly);
        Assert.True(space.Functions.Single(f => f.Name == "Use").IsReadonly);
    }


    [Theory]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; *data.Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Value = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; int* alias = data.Output; *alias = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Data copy = data; *copy.Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Data copy = Data(); copy = data; *copy.Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; int local = 0; data.Output = &local; *data.Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Data& alias = data; *alias.Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Data* alias = &data; *alias->Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; int*& alias = data.Output; Write(alias);")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; int** alias = &data.Output; **alias = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Write(data.Output);")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Write(&data.Value);")]
    [InlineData("Outer data = Outer(); data.Left.Hidden = State.Pointer; data.Left.Output = output; *data.Left.Output = 10;")]
    [InlineData("Outer data = Outer(); data.Left.Output = State.Pointer; data.Right.Output = output; *data.Right.Output = 10;")]
    [InlineData("Outer data = Outer(); data.Left.Output = State.Pointer; data.Right.Output = output; Outer copy = data; *copy.Right.Output = 10;")]
    [InlineData("Outer data = Outer(); data.Left.Output = State.Pointer; data.Right.Output = output; Data copy = data.Right; *copy.Output = 10;")]
    [InlineData("Outer data = Outer(); data.Left.Output = State.Pointer; Data local = Data(); local.Hidden = State.Pointer; local.Output = output; data.Right = local; *data.Right.Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Input = input; data.Output = output; *data.Output = *data.Input;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Property = output; *data.Property = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data[0] = output; *data[0] = 10;")]
    [InlineData("Data[] data = Data[2]; data[0].Hidden = State.Pointer; data[1].Output = output; *data[0].Output = 10;")]
    [InlineData("Data data = Data { State.Pointer, output, input, 0 }; *data.Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = output; data.Output = output; Data copy = data; copy.Hidden = State.Pointer; *data.Hidden = 10; *copy.Output = 10;")]
    public void Analyzer_PreservesIndependentFieldProvenance(string body)
    {
        Compilation compilation = CreateFieldProvenanceCompilation(body);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; *data.Hidden = 10;", "cannot mutate hidden state")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; int* alias = data.Hidden; *alias = 10;", "cannot mutate hidden state")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Data copy = data; *copy.Hidden = 10;", "cannot mutate hidden state")]
    [InlineData("Outer data = Outer(); data.Left.Output = State.Pointer; data.Right.Output = output; *data.Left.Output = 10;", "cannot mutate hidden state")]
    [InlineData("Outer data = Outer(); data.Left.Output = State.Pointer; data.Right.Output = output; Outer copy = data; *copy.Left.Output = 10;", "cannot mutate hidden state")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Data* alias = &data; *alias->Hidden = 10;", "cannot mutate hidden state")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; int*& alias = data.Output; alias = data.Hidden; *data.Output = 10;", "cannot mutate hidden state")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; int** alias = &data.Output; *alias = data.Hidden; *data.Output = 10;", "cannot mutate hidden state")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; Write(data.Hidden);", "cannot pass a mutable capability")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; WriteNested(data);", "cannot pass a mutable capability")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; *destination = data;", "cannot store a mutable capability")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; free(data.Hidden);", "cannot mutate hidden state")]
    [InlineData("Data data = Data(); data.Input = input; data.Output = output; *data.Input = 10;", "must be writable")]
    [InlineData("Data data = Data(); data.Output = input;", "cannot implicitly convert")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Property = data.Hidden; *data.Property = 10;", "cannot mutate hidden state")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data[0] = data.Hidden; *data[0] = 10;", "cannot mutate hidden state")]
    [InlineData("Data[] data = Data[2]; data[0].Hidden = State.Pointer; data[1].Output = output; *data[0].Hidden = 10;", "cannot mutate hidden state")]
    public void Analyzer_FieldProvenanceDoesNotLaunderHiddenAccess(string body, string expected)
    {
        Compilation compilation = CreateFieldProvenanceCompilation(body);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains(expected, StringComparison.Ordinal));
    }

    private static Compilation CreateFieldProvenanceCompilation(string body) =>
        CreateReadonlyEffectCompilation("""
            struct Data
            {
                public int* Hidden;
                public int* Output;
                public readonly int* Input;
                public int Value;
                public int* Property { get { return Output; } set { Output = value; } }
                public int* this[int index] { get { return Output; } set { Output = value; } }
            }
            struct Outer { public Data Left; public Data Right; }
            void readonly WriteNested(Data& data) { *data.Hidden = 10; }
            void readonly Test(int* output, readonly int* input, Data* destination)
            {
            """ + body + "}");



    [Theory]
    [InlineData("int* ptr = State.Pointer; ptr = output; *ptr = 10;")]
    [InlineData("Data data = Data(); data.Output = State.Pointer; data.Output = output; *data.Output = 10;")]
    [InlineData("Outer data = Outer(); data.Inner.Output = State.Pointer; data.Inner.Output = output; *data.Inner.Output = 10;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = output; data.Output = other; *data.Output = 10;")]
    [InlineData("int* ptr = State.Pointer; int** alias = &ptr; ptr = output; **alias = 10;")]
    [InlineData("int* ptr = State.Pointer; int** alias = &ptr; *alias = output; *ptr = 10;")]
    [InlineData("Data data = Data(); data.Output = State.Pointer; int*& alias = data.Output; alias = output; *data.Output = 10;")]
    [InlineData("Data a = Data(); a.Output = output; Data b = Data(); b.Output = State.Pointer; b = a; *b.Output = 10;")]
    [InlineData("int* ptr = State.Pointer; if (condition) ptr = output; else ptr = other; *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; if (condition) ptr = output; else return; *ptr = 10;")]
    [InlineData("int* ptr = output; if (condition) { ptr = State.Pointer; return; } *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; while (condition) { ptr = State.Pointer; ptr = output; *ptr = 10; }")]
    [InlineData("int* ptr = output; while (condition) { ptr = State.Pointer; ptr = output; } *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; while (condition) { ptr = output; break; } ptr = other; *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; while (true) { ptr = output; break; } *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; for (; condition; ptr = State.Pointer) { ptr = output; *ptr = 10; }")]
    [InlineData("int* ptr = State.Pointer; switch (key) { case 0: case 1: ptr = output; break; default: ptr = other; break; } *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; switch (key) { case 0: ptr = output; break; default: return; } *ptr = 10;")]
    [InlineData("Data original = Data(); original.Output = output; Data copy = original; original.Output = State.Pointer; *copy.Output = 10;")]
    [InlineData("int* ptr = State.Pointer; { ptr = output; } *ptr = 10;")]
    [InlineData("Data data = Data(); data.Output = State.Pointer; data.Value = output; *data.Value = 10;")]
    [InlineData("int* ptr = State.Pointer; bool ignored = condition && ((ptr = output) == output); ptr = output; *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; bool ignored = true && ((ptr = output) == output); *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; while ((ptr = output) == null) { } *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; int** alias = &ptr; alias[key] = output; ptr = output; *ptr = 10;")]
    [InlineData("Data data = Data(); data.Output = State.Pointer; Data* alias = &data; alias->Output = output; *data.Output = 10;")]
    [InlineData("int* ptr = State.Pointer; if (condition) { ptr = output; *ptr = 10; } else { ptr = other; *ptr = 10; }")]
    [InlineData("int* ptr = output; while (condition) { ptr = State.Pointer; if (key == 0) { ptr = output; continue; } ptr = other; } *ptr = 10;")]
    public void Analyzer_ReadonlyFlowStrongUpdatesExactLocations(string body)
    {
        Compilation compilation = CreateReadonlyFlowCompilation(body);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("int* ptr = output; ptr = State.Pointer; *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; *ptr = 10; ptr = output;")]
    [InlineData("Data data = Data(); data.Hidden = State.Pointer; data.Output = State.Pointer; data.Output = output; *data.Hidden = 10;")]
    [InlineData("int* ptr = output; int** alias = &ptr; *alias = State.Pointer; *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; if (condition) ptr = output; *ptr = 10;")]
    [InlineData("int* ptr; if (condition) ptr = output; else ptr = State.Pointer; *ptr = 10;")]
    [InlineData("int* ptr = output; while (condition) { ptr = State.Pointer; } *ptr = 10;")]
    [InlineData("int* ptr = output; while (condition) { *ptr = 10; ptr = State.Pointer; }")]
    [InlineData("int* ptr = State.Pointer; while (condition) { ptr = output; } *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; for (; condition; ptr = output) { *ptr = 10; }")]
    [InlineData("int* ptr = output; while (condition) { if (key == 0) { ptr = State.Pointer; continue; } *ptr = 10; }")]
    [InlineData("int* ptr = output; while (true) { ptr = State.Pointer; break; } *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; switch (key) { case 0: ptr = output; break; } *ptr = 10;")]
    [InlineData("int* ptr = output; switch (key) { case 0: ptr = State.Pointer; break; default: ptr = output; break; } *ptr = 10;")]
    [InlineData("int* left = State.Pointer; int* right = State.Pointer; int** alias; if (condition) alias = &left; else alias = &right; *alias = output; *left = 10;")]
    [InlineData("Data[] items = Data[2]; items[0].Output = State.Pointer; items[key].Output = output; *items[0].Output = 10;")]
    [InlineData("int* ptr = State.Pointer; bool ignored = condition && ((ptr = output) == output); *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; bool ignored = condition || ((ptr = output) == output); *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; int** alias = &ptr; alias[key] = output; *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; int** alias = &ptr; alias = alias + key; *alias = output; *ptr = 10;")]
    [InlineData("int* ptr = output; for (; condition; ptr = State.Pointer) { *ptr = 10; }")]
    [InlineData("int* ptr = output; while (condition) { switch (key) { case 0: ptr = State.Pointer; continue; default: break; } *ptr = 10; }")]
    [InlineData("int* ptr = State.Pointer; switch (key) { default: } *ptr = 10;")]
    [InlineData("int* ptr = State.Pointer; switch (key) { case 0: ptr = output; break; default: } *ptr = 10;")]
    public void Analyzer_ReadonlyFlowPreservesPossibleHiddenOrigins(string body)
    {
        Compilation compilation = CreateReadonlyFlowCompilation(body);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("cannot mutate hidden state", StringComparison.Ordinal));
    }

    private static Compilation CreateReadonlyFlowCompilation(string body) =>
        CreateReadonlyEffectCompilation("""
            struct Data
            {
                public int* Hidden;
                public int* Output;
                public int* Value { get { return Output; } set { Output = value; } }
            }
            struct Outer { public Data Inner; }
            void readonly Test(bool condition, int key, int* output, int* other)
            {
            """ + body + "}");


    private static Compilation CreateReadonlyEffectCompilation(string source) => CreateCompilation("""
        namespace Example;
        struct State
        {
            public static int Value;
            public static int* Pointer;
            public static int[] Values;
        }
        void readonly Write(int* value) { *value = 10; }
        """ + source);

    [Fact]
    public void Analyzer_BindsPropertyGetAndSetAsAccessorsWithoutStorage()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Player
            {
                public int health;

                public int Health
                {
                    get { return health; }
                    set { health = value; }
                }
            }

            int Main()
            {
                Player player = Player { 0 };
                player.Health = 100;
                return player.Health;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol player = Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types);
        PropertySymbol property = Assert.Single(player.Properties);
        Assert.Single(player.AllInstanceFields);
        Assert.NotNull(property.Getter);
        Assert.NotNull(property.Setter);

        BoundFunction main = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Main");
        var setStatement = Assert.IsType<BoundExpressionStatement>(main.Body.Statements[1]);
        Assert.IsType<BoundPropertySetExpression>(setStatement.Expression);
        var returnStatement = Assert.IsType<BoundReturnStatement>(main.Body.Statements[2]);
        var getCall = Assert.IsType<BoundMethodCallExpression>(returnStatement.Expression);
        Assert.Same(property.Getter, getCall.Method);
    }

    [Fact]
    public void Analyzer_BindsCompoundPropertyAndIndexerAssignmentsAsSingleAccessorOperations()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Values
            {
                public int stored;

                public int Value
                {
                    get { return stored; }
                    set { stored = value; }
                }

                public int this[int index]
                {
                    get { return stored + index; }
                    set { stored = value - index; }
                }
            }

            int Main()
            {
                Values values = Values { 10 };
                values.Value += 5;
                values[2] -= 3;
                return values.Value;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction main = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Main");
        var propertyStatement = Assert.IsType<BoundExpressionStatement>(main.Body.Statements[1]);
        var propertyAssignment = Assert.IsType<BoundCompoundAccessorAssignmentExpression>(propertyStatement.Expression);
        Assert.Equal(SyntaxKind.PlusToken, propertyAssignment.OperatorKind);
        Assert.Empty(propertyAssignment.Arguments);
        Assert.Null(propertyAssignment.InterfaceType);

        var indexerStatement = Assert.IsType<BoundExpressionStatement>(main.Body.Statements[2]);
        var indexerAssignment = Assert.IsType<BoundCompoundAccessorAssignmentExpression>(indexerStatement.Expression);
        Assert.Equal(SyntaxKind.MinusToken, indexerAssignment.OperatorKind);
        Assert.Single(indexerAssignment.Arguments);
        Assert.Null(indexerAssignment.InterfaceType);
    }

    [Fact]
    public void Analyzer_RejectsMissingPropertyAccessorAndReadonlyMutation()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Player
            {
                int health;

                public readonly int Health
                {
                    get { return health; }
                }
            }

            int Main()
            {
                Player player = Player { 0 };
                readonly Player& readOnly = player;
                int value = readOnly.Health;
                player.Health = 10;
                return value;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "property 'Health' does not declare a setter");
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("cannot be read through a readonly receiver", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_ResolvesIndexerOverloadsByParameterTypes()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Values
            {
                public int this[int index]
                {
                    get { return index; }
                }

                public int this[bool enabled]
                {
                    get { if (enabled) return 42; return 0; }
                }
            }

            int Main()
            {
                Values values = Values { };
                return values[1] + values[true];
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol values = Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types);
        Assert.Equal(2, values.Indexers.Length);
    }

    [Fact]
    public void Analyzer_ResolvesConstantDependenciesAndReportsCycles()
    {
        Compilation valid = CreateCompilation("""
            namespace Example;

            const int C = B + A;
            const int A = 4;
            const int B = A * 2;

            struct Values
            {
                const int Factor = C;
            }

            int Main() { return Values.Factor; }
            """);
        Assert.Empty(valid.Diagnostics);

        Compilation cyclic = CreateCompilation("""
            namespace Example;
            const int A = B + 1;
            const int B = A + 1;
            """);
        Assert.Contains(cyclic.Diagnostics, diagnostic => diagnostic.Message.Contains("circular constant dependency", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_ConvertsMutableReferenceToReadonlyButNotBack()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Value { public int Data; }

            void Test()
            {
                Value value = Value { 1 };
                Value& mutableValue = value;
                readonly Value& readOnlyValue = mutableValue;
                Value& invalid = readOnlyValue;
            }
            """);

        Assert.Single(compilation.Diagnostics);
        Assert.Contains("cannot implicitly convert", compilation.Diagnostics[0].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Analyzer_RejectsConstReferenceSyntaxInIterationThree()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Value { }
            void Read(const Value& value) { }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message == "'const T&' is no longer supported; use 'readonly T&'");
    }

    [Theory]
    [InlineData("int*", false, false)]
    [InlineData("readonly int*", false, true)]
    [InlineData("int* readonly", true, false)]
    [InlineData("readonly int* readonly", true, true)]
    public void Analyzer_DistinguishesPointerBindingAndPointeeReadonly(string type, bool bindingReadonly, bool pointeeReadonly)
    {
        Compilation binding = CreateCompilation($"namespace Example; void M(int* a, int* b) {{ {type} pointer = a; pointer = b; }}");
        Compilation pointee = CreateCompilation($"namespace Example; void M(int* a) {{ {type} pointer = a; *pointer = 2; }}");
        Compilation index = CreateCompilation($"namespace Example; void M(int* a) {{ {type} pointer = a; pointer[0] = 2; }}");
        Assert.Equal(bindingReadonly, binding.HasErrors);
        Assert.Equal(pointeeReadonly, pointee.HasErrors);
        Assert.Equal(pointeeReadonly, index.HasErrors);
    }

    [Fact]
    public void Analyzer_BindsEnumsSwitchAndRecursiveArrayTypes()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            const int Start = 10;
            enum State : byte { Idle, Running = Start + 1, Stopped }
            int Test(State state)
            {
                const int Max = 12;
                int result;
                switch (state)
                {
                    case State.Idle: result = 0; break;
                    case State.Running: result = 11; break;
                    default: result = Max; break;
                }
                int[][,] matrices = new int[2][,];
                matrices[0] = new int[3, 4];
                int[,][][] rows = new int[1, 2][][];
                free(rows);
                free(matrices[0]);
                free(matrices);
                return result;
            }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var enumeration = Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Enums);
        Assert.Same(BuiltinTypes.Byte, enumeration.UnderlyingType);
        Assert.Equal([0, 11, 12], enumeration.Members.Select(member => (int)member.Value!));
        var body = Assert.Single(compilation.SemanticModel.Functions).Body;
        var matrices = Assert.IsType<BoundVariableDeclarationStatement>(body.Statements[3]);
        var outer = Assert.IsType<ArrayTypeSymbol>(matrices.Variable.Type);
        Assert.Equal(1, outer.Rank);
        Assert.Equal(2, Assert.IsType<ArrayTypeSymbol>(outer.ElementType).Rank);
        Assert.Equal("int[][,]", outer.Name);
    }

    [Theory]
    [InlineData("case 1: case 2: case 3: Handle(); break; default: break;")]
    [InlineData("case 1: case 2: return; default: break;")]
    [InlineData("case 1: default: Handle(); break;")]
    [InlineData("default: case 1: case 2: Handle(); return;")]
    [InlineData("case 1: case 2: if (value > 0) return; else break; default: break;")]
    public void Analyzer_AllowsConsecutiveLabelsToShareAnExplicitlyTerminatedBody(string sections)
    {
        Compilation compilation = CreateCompilation($$"""
            namespace Example;
            void Handle() { }
            void Use(int value) { switch (value) { {{sections}} } }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("case 1: Handle(); case 2: Handle(); break;")]
    [InlineData("case 1: Handle(); default: Handle(); break;")]
    [InlineData("case 1: case 2: Handle(); case 3: break;")]
    [InlineData("case 1: case 2: if (value > 0) return; default: break;")]
    public void Analyzer_RejectsReachableCaseBodyFallthrough(string sections)
    {
        Compilation compilation = CreateCompilation($$"""
            namespace Example;
            void Handle() { }
            void Use(int value) { switch (value) { {{sections}} } }
            """);
        Assert.Contains(compilation.Diagnostics, d => d.Message.StartsWith("implicit fallthrough", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("enum E : float { A }", "underlying type")]
    [InlineData("enum E : bool { A }", "underlying type")]
    [InlineData("enum E { A, A }", "duplicate enum member")]
    [InlineData("enum E : byte { A = 256 }", "out of range")]
    [InlineData("enum E : byte { A = 255, B }", "out of range")]
    [InlineData("enum E : byte { A = -1 }", "out of range")]
    [InlineData("enum E { A = 1.5 }", "compile-time constant")]
    [InlineData("int Read() { return 1; } enum E { A = Read() }", "compile-time constant")]
    [InlineData("enum E { A = B, B = A }", "circular constant dependency")]
    [InlineData("const int N = cast<int>(E.A); enum E { A = N }", "circular constant dependency")]
    [InlineData("enum E { A } int M() { return E.A; }", "cannot implicitly convert")]
    [InlineData("enum E { A } E M() { return 0; }", "cannot implicitly convert")]
    [InlineData("enum E { A } enum F { A } E M() { return F.A; }", "cannot implicitly convert")]
    [InlineData("enum E { A } float M() { return cast<float>(E.A); }", "not a valid primitive cast")]
    [InlineData("void M(float value) { switch(value) { default: break; } }", "switch operand")]
    [InlineData("void M(int* value) { switch(value) { default: break; } }", "switch operand")]
    [InlineData("void M(int value) { switch(value) { case value: break; } }", "compile-time constant")]
    [InlineData("void M(int value) { switch(value) { case 2: break; case 1+1: break; } }", "duplicate case")]
    [InlineData("void M(int value) { switch(value) { default: break; default: break; } }", "duplicate default")]
    [InlineData("void M(int value) { switch(value) { case 1: value = 2; case 2: break; } }", "fallthrough")]
    [InlineData("void M(int value) { switch(value) { case 1: if(value == 1) break; default: break; } }", "fallthrough")]
    [InlineData("void M(int value) { switch(value) { default: continue; } }", "continue")]
    [InlineData("enum E { A } void M(E value) { switch(value) { case 0: break; } }", "not compatible")]
    [InlineData("enum E { A = 1, B = 1 } void M(E value) { switch(value) { case E.A: break; case E.B: break; } }", "duplicate case")]
    [InlineData("void M(byte value) { switch(value) { case 256: break; } }", "not compatible")]
    [InlineData("void M() { int[,] a = new int[2,3]; a[0] = 1; }", "requires 2 index")]
    [InlineData("void M() { int[,] a = new int[2,3]; a[0,1,2] = 1; }", "requires 2 index")]
    [InlineData("void M() { int[,] a = new int[2,3]; a[0,1.0] = 1; }", "index must be an integer")]
    [InlineData("void M() { int[,] a = new int[2]; }", "cannot implicitly convert")]
    [InlineData("void M() { int[] a = new int[-1]; }", "array length")]
    [InlineData("void M() { int[,] a = new int[50000,50000]; }", "total array length")]
    [InlineData("void M(int value) { int result; switch(value) { case 1: result = 1; break; } int other = result; }", "before it is initialized")]
    [InlineData("void M(int value) { int result; switch(value) { case 1: break; default: result = 1; break; } int other = result; }", "before it is initialized")]
    [InlineData("void M() { int[,] a = new int[2,3]; a.GetLength(1+1); }", "dimension must be")]
    [InlineData("void M() { int[,] a = new int[2,3]; a.GetLength(-1); }", "dimension must be")]
    [InlineData("void M() { int[,] a = new int[2,3]; a.GetLength(0.0); }", "one int dimension")]
    [InlineData("void M(int* readonly value, int* other) { value = other; }", "must be writable")]
    [InlineData("void M(readonly int* value) { value[0]++; }", "unary operator")]
    [InlineData("void M(readonly int* value) { int& x = value[0]; }", "cannot implicitly convert")]
    [InlineData("void M(readonly int* value) { int* x = &value[0]; }", "cannot implicitly convert")]
    [InlineData("void M() { int* readonly value; }", "must be initialized")]
    [InlineData("void M(int value) { const int N = value; }", "compile-time constant")]
    public void Analyzer_RejectsInvalidIterationFourPrograms(string source, string diagnostic)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, item => item.Message.Contains(diagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_DoesNotImposeRankNestingOrIndexerParameterLimits()
    {
        string rank = "[" + new string(',', 39) + "]";
        string nesting = string.Concat(Enumerable.Repeat("[]", 24));
        string dimensions = string.Join(",", Enumerable.Repeat("1", 40));
        string indices = string.Join(",", Enumerable.Repeat("0", 40));
        string parameters = string.Join(",", Enumerable.Range(0, 16).Select(i => $"int p{i}"));
        string arguments = string.Join(",", Enumerable.Range(0, 16));
        Compilation compilation = CreateCompilation($$"""
            namespace Example;
            struct Grid { public int this[{{parameters}}] { get { return p15; } set { } } }
            void Test()
            {
                int{{rank}} values = new int[{{dimensions}}];
                values[{{indices}}] = 42;
                int{{nesting}} nested = new int[1]{{nesting[2..]}};
                Grid grid = Grid {};
                grid[{{arguments}}] = grid[{{arguments}}];
                free(values);
                free(nested);
            }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Fact]
    public void Analyzer_ResolvesEnumConstantsAcrossFilesAndAliases()
    {
        Compilation compilation = CreateCompilation(
            "namespace Types; const int Start = 40; enum State { Ready = Start + 2 }",
            "using S = Types.State; namespace Example; const S Value = S.Ready; int Test(S state) { switch(state) { case Value: return cast<int>(Types.State.Ready); default: return 0; } }");
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Fact]
    public void Analyzer_PreservesPointerFieldQualifiersAndMutableReadonlyPointeeBindings()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Holder
            {
                public readonly int* Data;
                public int* readonly Fixed;
                public Holder(int* pointer) { Data = pointer; Fixed = pointer; }
            }
            void Test(int* pointer)
            {
                readonly int* data;
                data = pointer;
                Holder holder = Holder(pointer);
                holder.Data = pointer;
                *holder.Fixed = 42;
            }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var holder = Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types);
        Assert.False(holder.Fields[0].IsReadonly);
        Assert.True(Assert.IsType<PointerTypeSymbol>(holder.Fields[0].Type).IsReadonly);
        Assert.True(holder.Fields[1].IsReadonly);
        Assert.False(Assert.IsType<PointerTypeSymbol>(holder.Fields[1].Type).IsReadonly);
    }

    [Theory]
    [InlineData("enum E { A = cast<int>(sizeof(int)) / 0 }", "compile-time constant")]
    [InlineData("enum E { A = B, B = A + cast<int>(sizeof(int)) }", "circular constant")]
    [InlineData("enum E { A = cast<int>(sizeof(void)) }", "compile-time constant")]
    [InlineData("void M(int x) { switch(x) { case cast<int>(sizeof(void)): break; } }", "non-void")]
    [InlineData("struct Bad { public Bad Value; } enum E { A = cast<int>(sizeof(Bad)) }", "recursive")]
    public void Analyzer_StillRejectsInvalidTargetDependentExpressionsWithoutASelectedTarget(string source, string message)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.True(compilation.HasErrors);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains(message, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("int[,] a = int[2,3]; int length = a.Length; int rank = a.Rank; int width = a.GetLength(1); a[1,2] = 42;")]
    [InlineData("Item[,,] a = Item[2,3,4]; a[1,2,3].Id = 42;")]
    [InlineData("Example.Item[,] a = Example.Item[1,2];")]
    [InlineData("int[] a = int[2]; { int[] alias = a; alias[0] = 1; }")]
    [InlineData("int[] a = int[2]; a = new int[3]; free(a);")]
    [InlineData("int[] a = int[2]; if (flag) a = new int[3]; else a = new int[4]; free(a);")]
    [InlineData("int[] a = int[2]; switch (1) { case 0: a = new int[3]; break; default: a = new int[4]; break; } free(a);")]
    public void Analyzer_AcceptsRectangularStackArraysAndInnerAliases(string body)
    {
        Compilation compilation = CreateCompilation("namespace Example; struct Item { public int Id; } void M(bool flag) { " + body + " }");
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("int[][] a = int[2][];", "stack arrays cannot contain array elements")]
    [InlineData("int[,][] a = int[2,3][];", "stack arrays cannot contain array elements")]
    [InlineData("int[,] a = int[2];", "cannot implicitly convert")]
    [InlineData("int[,] a = int[2,-1];", "array length")]
    [InlineData("int[,] a = int[2147483647,2];", "total array length")]
    [InlineData("int[,] a = int[2,true];", "array length must be an integer")]
    [InlineData("int[,] a = int[2,3]; int d = a.GetLength(2);", "GetLength dimension")]
    [InlineData("int[,] a = int[2,3]; free(a);", "stack array cannot be freed")]
    [InlineData("int[,] a; { a = int[2,3]; }", "allocation scope")]
    [InlineData("int[,] a; { int[,] b = int[2,3]; a = b; }", "allocation scope")]
    [InlineData("int[,] a = int[2,3]; if (flag) a = new int[2,3]; free(a);", "stack array cannot be freed")]
    [InlineData("int[,] a; free(a = int[2,3]);", "stack array cannot be freed")]
    [InlineData("int[,] a = int[2,3]; while (flag) { a = new int[2,3]; } free(a);", "stack array cannot be freed")]
    [InlineData("int[,] a = int[2,3]; switch (1) { case 0: a = new int[2,3]; break; } free(a);", "stack array cannot be freed")]
    public void Analyzer_RejectsInvalidStackArraysAndScopeEscape(string body, string message)
    {
        Compilation compilation = CreateCompilation("namespace Example; void M(bool flag) { " + body + " }");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains(message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bool r = c && ((value = 10) == 10); return value;")]
    [InlineData("bool r = c || ((value = 10) == 10); return value;")]
    [InlineData("bool r = c && (c || ((value = 10) == 10)); return value;")]
    [InlineData("if (c && ((value = 10) == 10)) {} return value;")]
    [InlineData("if (c || ((value = 10) == 10)) return value; return 0;")]
    [InlineData("bool r = false && ((value = 10) == 10); return value;")]
    [InlineData("bool r = true || ((value = 10) == 10); return value;")]
    public void Analyzer_ShortCircuitDoesNotGuaranteeRhsAssignment(string body)
    {
        Compilation compilation = CreateCompilation("namespace Example; int M(bool c) { int value; " + body + " }");
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("used before it is initialized", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bool r = ((value = 1) == 1) && ((value = 2) == 2); return value;")]
    [InlineData("bool r = true && ((value = 2) == 2); return value;")]
    [InlineData("bool r = false || ((value = 2) == 2); return value;")]
    [InlineData("if (c && ((value = 10) == 10)) return value; return 0;")]
    [InlineData("if (c || ((value = 10) == 10)) return 0; else return value;")]
    [InlineData("if ((c && ((value = 10) == 10)) && value == 10) return value; return 0;")]
    public void Analyzer_ShortCircuitPreservesGuaranteedAndBranchAssignments(string body)
    {
        Compilation compilation = CreateCompilation("namespace Example; int M(bool c) { int value; " + body + " }");
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("&&")]
    [InlineData("||")]
    public void Analyzer_ShortCircuitRetainsStackArrayAndHiddenPointerOrigins(string op)
    {
        Compilation array = CreateCompilation($$"""
            namespace Example;
            void M(bool c) { int[] a = int[1]; bool r = c {{op}} ((a = new int[1]).Length == 1); free(a); }
            """);
        Assert.Contains(array.Diagnostics, d => d.Message.Contains("stack array cannot be freed", StringComparison.Ordinal));
        Compilation pointer = CreateCompilation($$"""
            namespace Example;
            struct State { public static int* Hidden; }
            void readonly M(bool c) { int value = 0; int* p = State.Hidden; bool r = c {{op}} ((p = &value) != null); *p = 42; }
            """);
        Assert.True(pointer.HasErrors);
        Assert.Contains(pointer.Diagnostics, d => d.Message.Contains("hidden", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("struct Data { public int X; } bool M(Data a, Data b) { return a == b; }")]
    [InlineData("struct Data { public int X; } bool M(Data a, Data b) { return a != b; }")]
    [InlineData("interface I {} bool M(I a, I b) { return a == b; }")]
    [InlineData("void F() {} bool M() { return F() != F(); }")]
    [InlineData("bool M(int* a, float* b) { return a == b; }")]
    [InlineData("bool M(int[] a, int[] b) { return a == b; }")]
    public void Analyzer_RejectsUnsupportedEquality(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("binary operator", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("struct R { private ~R() {} } void M(R* p) { free(p); }")]
    [InlineData("struct R { private ~R() {} } void M() { R[] p = new R[1]; free(p); }")]
    [InlineData("struct R { private ~R() {} } void M() { R[] p = R[1]; }")]
    [InlineData("struct R { private ~R() {} } struct D : R { public ~D() {} }")]
    [InlineData("struct R { private ~R() {} } struct D : R {}")]
    public void Analyzer_EnforcesDestructorAccessibility(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("destructor 'R' is private", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("struct H { public int& Value; } void M() { H h = H(); }")]
    [InlineData("struct H { public readonly int& Value; } void M() { H* h = new H(); }")]
    [InlineData("struct H { public int& Value; } struct Outer { public H Inner; } void M() { Outer h = Outer(); }")]
    [InlineData("struct H { public int& Value; } struct D : H {}")]
    [InlineData("void M() { int&[] values = new int&[10]; }")]
    [InlineData("void M() { readonly int&[] values = new readonly int&[10]; }")]
    [InlineData("struct H { public int& Value; public H(int& v, bool c) { if (c) Value = v; } }")]
    [InlineData("struct H { public int& Value; public H(int& v, bool c) { if (c) return; Value = v; } }")]
    [InlineData("struct H { public int& Value; public H(int& v) {} } void M() { H[] h = new H[1]; }")]
    [InlineData("struct H { public int& Value; public H(int& v) { int x = Value; Value = v; } }")]
    [InlineData("struct H { public int& Value; public H(int& v) { Read(); Value = v; } public int Read() { return Value; } }")]
    [InlineData("struct H { public int& Value; public H(int& v) { H* escaped = this; Value = v; } }")]
    [InlineData("struct H { public int& Value = Value; }")]
    [InlineData("struct H { public int& Value; } struct State { public static H Empty; }")]
    public void Analyzer_RejectsUnboundReferenceStorage(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.True(compilation.HasErrors, source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("reference", StringComparison.Ordinal) || d.Message.Contains("before it is initialized", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("struct H { public int& Value; public H(int& v) { Value = v; } }")]
    [InlineData("struct H { public readonly int& Value; public H(int& v) { this.Value = v; } }")]
    [InlineData("struct H { public int& Value; public H(int& v, bool c) { if (c) Value = v; else Value = v; } }")]
    [InlineData("struct H { public int& Value; public H(int& v) { Value = v; } } struct D : H { public D(int& v) : base(v) {} }")]
    [InlineData("struct H { public int& Value; } void M(int& v) { H h = H { v }; }")]
    [InlineData("struct H { public int Value; public int& Ref = Value; } void M() { H h = H(); }")]
    public void Analyzer_AcceptsExplicitReferenceInitialization(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("void M() { A a; }")]
    [InlineData("void M() { A a = A(); }")]
    [InlineData("struct H { public A Value; } void M() { H h = H(); }")]
    [InlineData("struct H { public A Value; } struct Outer { public H Inner; }")]
    [InlineData("struct H { public A Value; } struct D : H {}")]
    [InlineData("void M() { A[] a = new A[1]; }")]
    [InlineData("extern A Get();")]
    [InlineData("interface I { void Set(A value); }")]
    public void Analyzer_RejectsAbstractValueStorage(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; abstract struct A { public abstract int Read(); } " + source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("abstract", StringComparison.Ordinal));
    }

    public static IEnumerable<object[]> InvalidFloatingConstants()
    {
        foreach (string type in new[] { "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint", "clong", "culong" })
        foreach (string suffix in new[] { "", "f" })
        foreach (string expression in new[] { $"0.0{suffix} / 0.0{suffix}", $"1.0{suffix} / 0.0{suffix}", $"-1.0{suffix} / 0.0{suffix}", $"1.0e30{suffix}", $"-1.0e30{suffix}" })
            yield return new object[] { type, expression };
    }

    [Theory]
    [MemberData(nameof(InvalidFloatingConstants))]
    public void Analyzer_RejectsInvalidFloatingConstants(string type, string expression)
    {
        Compilation compilation = CreateCompilation($"namespace Example; const {type} Value = cast<{type}>({expression});");
        if (!compilation.HasErrors)
            compilation = Xenon.CodeGen.LLVM.LlvmIrGenerator.BindForTarget(compilation, Xenon.CodeGen.LLVM.LlvmTargetOptions.CreateHost());
        Assert.True(compilation.HasErrors, type + ": " + expression);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("compile-time", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_AccountsForBaseDestructorEffectsOnEarlyReturn()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct State { public static int Value; }
            struct Base { public ~Base() { State.Value += 1; } }
            struct Derived : Base { public ~Derived() { return; } }
            void readonly M() { Derived* p = new Derived(); free(p); }
            """);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("hidden", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("int*", false)]
    [InlineData("readonly int*", true)]
    [InlineData("int* readonly", false)]
    [InlineData("readonly int* readonly", true)]
    public void Analyzer_FreeUsesPointeeReadonlyIndependentlyOfFunctionReadonly(string pointerType, bool rejected)
    {
        foreach (string effect in new[] { "", "readonly " })
        {
            Compilation compilation = CreateCompilation($"namespace Example; void {effect}Destroy({pointerType} p) {{ free(p); }}");
            Assert.Equal(rejected, compilation.HasErrors);
            if (rejected) Assert.Contains(compilation.Diagnostics, d => d.Message == "cannot free memory through a readonly pointer");
        }
    }

    [Theory]
    [InlineData("struct A { public ~A() {} } struct B : A { public override ~B() {} }")]
    [InlineData("struct A { public override ~A() {} }")]
    public void Analyzer_RejectsDestructorOverrideWithoutVirtualBase(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("does not override a virtual base destructor", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("void M() { R value = R(); }")]
    [InlineData("void M() { R value; value = R(); }")]
    public void Analyzer_ChecksScalarCleanupDestructorAccessibility(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; struct R { private ~R() {} } " + source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("destructor 'R' is private", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("R value = R { &State.Value };")]
    [InlineData("{ R value = R { &State.Value }; return; }")]
    [InlineData("while (flag) { R value = R { &State.Value }; break; }")]
    [InlineData("while (flag) { R value = R { &State.Value }; continue; }")]
    [InlineData("R value; if (flag) value = R { &State.Value };")]
    public void Analyzer_TracksImplicitScalarDestructorEffects(string body)
    {
        Compilation compilation = CreateCompilation("namespace Example; struct State { public static int Value; } struct R { public int* P; public ~R() { *P = 42; } } void readonly M(bool flag) { " + body + " }");
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("hidden", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("void readonly M() { int local = 0; int* p = State.Hidden; *p = (p = &local)[0]; }")]
    [InlineData("int* readonly M() { int local = 0; int* p = State.Hidden; p += (p = &local)[0]; return p; }")]
    public void Analyzer_AssignmentCapturesTargetAndOldValueBeforeRhs(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; struct State { public static int* Hidden; } " + source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("hidden", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_AssignmentDoesNotReevaluateTargetAfterRhs()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct State { public static int* Hidden; }
            void readonly M() { int* value = null; int** slot = &value; *slot = *(slot = &State.Hidden); }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        Compilation invalid = CreateCompilation("namespace Example; void M(int[] a) { int i; a[i] = (i = 0); }");
        Assert.Contains(invalid.Diagnostics, d => d.Message.Contains("used before it is initialized", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("if (flag) { w = Write { &p }; c = Clear { &p }; } else { c = Clear { &p }; w = Write { &p }; }", true)]
    [InlineData("w = Write { &p }; c = Clear { &p };", false)]
    [InlineData("c = Clear { &p }; w = Write { &p };", true)]
    [InlineData("w = Write { &p }; if (flag) c = Clear { &p };", true)]
    [InlineData("if (flag) { w = Write { &p }; c = Clear { &p }; } else { w = Write { &p }; c = Clear { &p }; }", false)]
    [InlineData("", false)]
    public void Analyzer_TracksActualScalarConstructionOrder(string initialization, bool rejected)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct State { public static int* Hidden; }
            struct Clear { public int** Slot; public ~Clear() { *Slot = null; } }
            struct Write { public int** Slot; public ~Write() { **Slot = 42; } }
            void readonly M(bool flag) {
                int* p = State.Hidden;
                Write w; Clear c;
            """ + initialization + " }");
        Assert.Equal(rejected, compilation.HasErrors);
        if (rejected) Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("hidden", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("*P += 1;")]
    [InlineData("while (true) {}")]
    public void Analyzer_ConvergesWithScalarCleanupInRecursiveCalls(string destructorBody)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct R { public int* P; public ~R() {
            """ + destructorBody + """
            } }
            struct Runner { public void Run(int* p, int n) { R local = R { p }; if (n > 0) Run(p, n - 1); } }
            void readonly M(int* p) { Runner runner = Runner(); runner.Run(p, 2); }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("public virtual void M(int x) {}", "public void M(int x) {}")]
    [InlineData("public virtual void M(int x) {}", "public virtual void M(int x) {}")]
    [InlineData("public abstract void M(int x);", "public void M(int x) {}")]
    [InlineData("public virtual int readonly M() { return 1; }", "public int readonly M() { return 2; }")]
    [InlineData("public virtual int Value { get { return 1; } set {} }", "public int Value { get { return 2; } set {} }")]
    [InlineData("public abstract int Value { get; }", "public int Value { get { return 2; } }")]
    [InlineData("public virtual int this[int x] { get { return x; } }", "public int this[int x] { get { return x; } }")]
    [InlineData("public abstract int this[int x] { get; set; }", "public int this[int x] { get { return x; } set {} }")]
    [InlineData("public virtual ~Base() {}", "public ~Derived() {}")]
    [InlineData("public virtual ~Base() {}", "public virtual ~Derived() {}")]
    public void Analyzer_RequiresExplicitOverrideAndDoesNotInstallInvalidSlots(string baseMember, string derivedMember)
    {
        Compilation compilation = CreateCompilation("namespace Example; abstract struct Base { " + baseMember +
            " } struct Derived : Base { " + derivedMember + " }");
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("must be declared 'override'", StringComparison.Ordinal));
        StructTypeSymbol derived = Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.Where(type => type.Name == "Derived"));
        Assert.All(derived.Methods, method => Assert.Null(method.VTableSlot));
        if (derived.Destructor is { } destructor) Assert.Null(destructor.VTableSlot);
        Assert.Equal<FunctionSymbol>(derived.BaseType!.VirtualMethods, derived.VirtualMethods);
    }

    [Theory]
    [InlineData("public void M(int x) {}", "public override void M(int x) {}")]
    [InlineData("", "public override void M() {}")]
    [InlineData("public virtual int M() { return 0; }", "public override float M() { return 0.0f; }")]
    [InlineData("public virtual void M(int x) {}", "public override void M(float x) {}")]
    [InlineData("public virtual void M(int x) {}", "public override void M() {}")]
    [InlineData("public virtual void M(int* x) {}", "public override void M(readonly int* x) {}")]
    [InlineData("public virtual void M(int& x) {}", "public override void M(readonly int& x) {}")]
    [InlineData("public virtual void M(int[] x) {}", "public override void M(int[,] x) {}")]
    [InlineData("public virtual void M() {}", "public override void readonly M() {}")]
    [InlineData("public virtual int Value { get { return 0; } }", "public override int get_Value() { return 1; }")]
    [InlineData("public virtual int get_Value() { return 0; }", "public override int Value { get { return 1; } }")]
    [InlineData("public int Value { get { return 0; } }", "public override int Value { get { return 1; } }")]
    [InlineData("public virtual int Value { get { return 0; } set {} }", "public override int Value { get { return 1; } }")]
    [InlineData("public virtual int Value { get { return 0; } }", "public override int Value { get { return 1; } set {} }")]
    [InlineData("public virtual readonly int Value { get { return 0; } }", "public override int Value { get { return 1; } }")]
    [InlineData("public virtual int Value { get { return 0; } }", "public override float Value { get { return 1.0f; } }")]
    [InlineData("public int this[int x] { get { return x; } }", "public override int this[int x] { get { return x; } }")]
    [InlineData("public virtual int this[int x] { get { return x; } }", "public override int this[float x] { get { return 1; } }")]
    [InlineData("public virtual int this[int x] { get { return x; } set {} }", "public override int this[int x] { get { return x; } }")]
    [InlineData("public ~Base() {}", "public override ~Derived() {}")]
    [InlineData("", "public override ~Derived() {}")]
    public void Analyzer_RejectsIncompatibleOverrideWithoutReplacingBaseSlot(string baseMember, string derivedMember)
    {
        Compilation compilation = CreateCompilation("namespace Example; struct Base { " + baseMember +
            " } struct Derived : Base { " + derivedMember + " }");
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("does not override", StringComparison.Ordinal));
        StructTypeSymbol derived = Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.Where(type => type.Name == "Derived"));
        Assert.All(derived.Methods, method => Assert.Null(method.VTableSlot));
        if (derived.Destructor is { } destructor) Assert.Null(destructor.VTableSlot);
        Assert.Equal<FunctionSymbol>(derived.BaseType!.VirtualMethods, derived.VirtualMethods);
    }

    [Theory]
    [InlineData("public abstract void M();", "public override void M() {}")]
    [InlineData("public abstract int Value { get; set; }", "public override int Value { get { return 42; } set {} }")]
    [InlineData("public abstract int this[int x] { get; set; }", "public override int this[int x] { get { return x; } set {} }")]
    public void Analyzer_RequiresConcreteCompletionAcrossTheWholeInheritanceChain(string declaration, string implementation)
    {
        string[] sources = ["namespace Example; abstract struct A { " + declaration + " }",
            "namespace Example; abstract struct B : A {} abstract struct C : B {}",
            "namespace Example; struct D : C { " + implementation + " }"];
        foreach (string[] order in new[] { sources, sources.Reverse().ToArray() })
        {
            Compilation valid = CreateCompilation(order);
            Assert.False(valid.HasErrors, string.Join(Environment.NewLine, valid.Diagnostics));
            StructTypeSymbol derived = Assert.Single(Assert.Single(valid.SemanticModel.GlobalNamespace.Namespaces).Types.Where(type => type.Name == "D"));
            Assert.False(derived.IsAbstract);
            Assert.All(derived.VirtualMethods, method => { Assert.False(method.IsAbstract); Assert.Same(derived, method.ContainingType); });
        }
        Compilation invalid = CreateCompilation(sources[0], sources[1], "namespace Example; struct D : C {}");
        var diagnostic = Assert.Single(invalid.Diagnostics.Where(d => d.Message.Contains("does not implement abstract member", StringComparison.Ordinal)));
        Assert.Contains("Example.D", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Example.A", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("struct A { public abstract void M(); }")]
    [InlineData("abstract struct A {} void M() { A value; }")]
    [InlineData("abstract struct A {} void M() { A value = A(); }")]
    [InlineData("abstract struct A {} void M() { A* value = new A(); }")]
    [InlineData("abstract struct A {} struct B { public A Value; }")]
    [InlineData("abstract struct A {} void M(A value) {}")]
    [InlineData("abstract struct A {} void M() { A[] values = A[1]; }")]
    public void Analyzer_UsesDeclaredAbstractnessInsteadOfInferringItFromSlots(string source)
    {
        Compilation compilation = CreateCompilation("namespace Example; " + source);
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("abstract", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_InheritedOverloadsAndStaticMembersDoNotReplaceVirtualSlots()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct A { public virtual int M(int x) { return x; } }
            struct B : A { public int M(float x) { return 1; } }
            struct C : B { public static int M(int x) { return 2; } }
            struct D : C { public override int M(int x) { return 3; } }
            struct E : D {}
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var types = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.ToDictionary(type => type.Name);
        Assert.Null(types["B"].Methods[0].VTableSlot);
        Assert.Null(types["C"].Methods[0].VTableSlot);
        Assert.Same(types["A"].Methods[0], Assert.Single(types["C"].VirtualMethods));
        Assert.Same(types["D"].Methods[0], Assert.Single(types["E"].VirtualMethods));
    }

    [Theory]
    [InlineData("", "", false)]
    [InlineData("public virtual void M() {}", "", true)]
    [InlineData("public virtual void M() {}", "override ", false)]
    [InlineData("", "override ", true)]
    public void Analyzer_SeparatesInterfaceImplementationFromStructOverride(string baseMember, string modifier, bool rejected)
    {
        Compilation compilation = CreateCompilation("namespace Example; interface I { void M(); } struct Base { " + baseMember +
            " } struct Derived : Base, I { public " + modifier + "void M() {} }");
        Assert.Equal(rejected, compilation.HasErrors);
        if (rejected) Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("override", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_PreservesInheritedDestructorSlotWhenNoDestructorIsDeclared()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct A { public virtual ~A() {} }
            struct B : A {}
            struct C : B { public override ~C() {} }
            struct D : C {}
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var types = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.ToDictionary(type => type.Name);
        Assert.Null(types["B"].Destructor);
        Assert.Null(types["D"].Destructor);
        Assert.Same(types["A"].Destructor, Assert.Single(types["B"].VirtualMethods));
        Assert.Same(types["C"].Destructor, Assert.Single(types["D"].VirtualMethods));
        Assert.True(types["C"].Destructor!.IsOverride);
    }

    [Fact]
    public void Analyzer_RejectsReducedOverrideAccessibilityBeforeAssigningSlots()
    {
        Compilation compilation = CreateCompilation("namespace Example; struct A { public virtual void M() {} } struct B : A { private override void M() {} }");
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("accessibility", StringComparison.Ordinal));
        StructTypeSymbol derived = Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.Where(type => type.Name == "B"));
        Assert.Null(Assert.Single(derived.Methods).VTableSlot);
        Assert.Same(derived.BaseType!.Methods[0], Assert.Single(derived.VirtualMethods));
    }

    [Theory]
    [InlineData("public override void M() {}")]
    [InlineData("public override int Value { get { return 1; } }")]
    [InlineData("public override int this[int i] { get { return i; } }")]
    [InlineData("public override ~A() {}")]
    public void Analyzer_RejectsOverrideOnRootStructs(string member)
    {
        Compilation compilation = CreateCompilation("namespace Example; struct A { " + member + " }");
        Assert.Contains(compilation.Diagnostics, d => d.Message.Contains("does not override", StringComparison.Ordinal));
        Assert.Empty(Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types).VirtualMethods);
    }

    [Fact]
    public void Analyzer_DistinguishesInheritedVirtualOverloadsByTheirFullSignature()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct A { public virtual int M(int x) { return x; } }
            struct B : A { public virtual int M(float x) { return 1; } }
            struct C : B { public override int M(int x) { return 2; } }
            abstract struct D : C {}
            struct E : D {}
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var types = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.ToDictionary(type => type.Name);
        Assert.Equal(2, types["E"].VirtualMethods.Length);
        Assert.Same(types["C"].Methods[0], types["E"].VirtualMethods[0]);
        Assert.Same(types["B"].Methods[0], types["E"].VirtualMethods[1]);
        Assert.True(types["D"].IsAbstract);
        Assert.False(types["E"].IsAbstract);
    }

    [Theory]
    [InlineData("public", false)]
    [InlineData("private", true)]
    [InlineData("", true)]
    public void Analyzer_ValidatesDestructorOverrideAccessibilityBeforeReplacingItsSlot(string access, bool rejected)
    {
        Compilation compilation = CreateCompilation("namespace Example; struct Base { public virtual ~Base() {} } " +
            "struct Derived : Base { " + access + " override ~Derived() {} }");
        Assert.Equal(rejected, compilation.HasErrors);
        var types = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.ToDictionary(type => type.Name);
        FunctionSymbol destructor = types["Derived"].Destructor!;
        if (rejected)
        {
            var diagnostic = Assert.Single(compilation.Diagnostics);
            Assert.Equal("an override cannot reduce the accessibility of its inherited member", diagnostic.Message);
            Assert.Equal("override", diagnostic.Location.Source.GetText(diagnostic.Location.Span));
            Assert.Null(destructor.VTableSlot);
            Assert.Same(types["Base"].Destructor, Assert.Single(types["Derived"].VirtualMethods));
        }
        else
        {
            Assert.Equal(types["Base"].Destructor!.VTableSlot, destructor.VTableSlot);
            Assert.Same(destructor, Assert.Single(types["Derived"].VirtualMethods));
        }
    }

    [Theory]
    [InlineData("public override ~Leaf() {}", false)]
    [InlineData("private override ~Leaf() {}", true)]
    [InlineData("", false)]
    public void Analyzer_ChecksDestructorOverrideAccessThroughIntermediateTypes(string leaf, bool rejected)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Base { public virtual ~Base() {} }
            struct Derived : Base { public override ~Derived() {} }
            struct Middle : Derived {}
            struct Leaf : Middle {
            """ + leaf + " }");
        Assert.Equal(rejected, compilation.HasErrors);
        var types = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.ToDictionary(type => type.Name);
        Assert.Null(types["Middle"].Destructor);
        FunctionSymbol expected = !rejected && types["Leaf"].Destructor is { } own ? own : types["Derived"].Destructor!;
        Assert.Same(expected, Assert.Single(types["Leaf"].VirtualMethods));
        if (rejected) Assert.Contains(compilation.Diagnostics, d => d.Message == "an override cannot reduce the accessibility of its inherited member");
    }

    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    public void Analyzer_KeepsPrivateBaseDestructorAccessSeparateFromOverrideVisibility(string access)
    {
        Compilation compilation = CreateCompilation("namespace Example; struct Base { private virtual ~Base() {} } " +
            "struct Derived : Base { " + access + " override ~Derived() {} }");
        // Private -> private/public is not narrowing, but the generated base
        // destructor call is still inaccessible outside Base in Xenon.
        Assert.Equal("destructor 'Base' is private", Assert.Single(compilation.Diagnostics).Message);
        Assert.DoesNotContain(compilation.Diagnostics, d => d.Message.Contains("reduce the accessibility", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("void M() {}")]
    [InlineData("int Value { get { return 0; } set {} }")]
    [InlineData("int this[int i] { get { return i; } set {} }")]
    public void Analyzer_KeepsOtherOverrideAccessibilityRulesUnchanged(string member)
    {
        foreach (string access in new[] { "public", "private" })
        {
            Compilation compilation = CreateCompilation("namespace Example; struct Base { public virtual " + member +
                " } struct Derived : Base { " + access + " override " + member + " }");
            Assert.Equal(access == "private", compilation.HasErrors);
            var types = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Types.ToDictionary(type => type.Name);
            Assert.All(types["Derived"].VirtualMethods, method => Assert.Same(types[access == "private" ? "Base" : "Derived"], method.ContainingType));
        }
    }

    [Theory]
    [InlineData("Resource* resource = Resource.Create(); Resource.Destroy(resource);", false)]
    [InlineData("Resource* resource = Resource.Create(); free(resource);", true)]
    [InlineData("Resource resource = Resource();", true)]
    [InlineData("Resource[] resources = Resource[1];", true)]
    public void Analyzer_PrivateDestructorAllowsTypeOwnedReleaseButNotExternalCleanup(string body, bool rejected)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Resource
            {
                private virtual ~Resource() {}
                public static Resource* Create() { return new Resource(); }
                public static void Destroy(Resource* resource) { free(resource); }
                public static void UseLocal() { Resource resource = Resource(); }
            }
            void Main() {
            """ + body + " }");
        Assert.Equal(rejected, compilation.HasErrors);
        if (rejected) Assert.Contains(compilation.Diagnostics, d => d.Message == "destructor 'Resource' is private");
    }

    private static Compilation CreateCompilation(params string[] sources) => Compilation.Create(
        sources.Select((source, index) => SourceText.From(source, $"test{index}.xe")).ToArray());
}
