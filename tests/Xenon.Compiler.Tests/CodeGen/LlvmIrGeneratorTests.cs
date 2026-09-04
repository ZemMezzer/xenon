using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using System.Text;
using Xunit;

namespace Xenon.Compiler.Tests.CodeGen;

public sealed class LlvmIrGeneratorTests
{
    [Fact]
    public void Generator_EmitsConcreteGenericFunctionSpecializations()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct UnusedBox<T> { T value; }
            T Identity<T>(T value) { return move value; }
            T Forward<T>(T value) { return Identity(move value); }
            int Use() { return Identity<int>(42) + Identity(1) + Forward(2); }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal(2, compilation.SemanticModel.Functions.Count(
            function => function.Symbol.IsGenericSpecialization));
        string ir = new LlvmIrGenerator().Generate(compilation, "generic-functions");

        Assert.Contains("define internal i32", ir, StringComparison.Ordinal);
        Assert.Contains("call i32", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RebindsStructuralTemplateCallsForConcreteSpecializations()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            template ValueSource { int Get(); }
            int Evaluate<T>(T value) where T : ValueSource { return value.Get(); }
            struct Item { public int Get() { return 42; } }
            int Use() { Item item = Item { }; return Evaluate<Item>(item); }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Single(compilation.SemanticModel.Functions,
            function => function.Symbol.IsGenericSpecialization);
        string ir = new LlvmIrGenerator().Generate(compilation, "constrained-generics");

        Assert.Contains("call i32", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsConcreteGenericStructLayoutAndMembers()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Box<T>
            {
                T value;
                public Box(T initial) { value = move initial; }
                public T Get() { return move value; }
            }
            int Use()
            {
                Box<int>* box = new Box<int>(42);
                int value = box->Get();
                free(box);
                return value;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost(),
            "generic-structs");

        Assert.Contains("type { i32 }", ir, StringComparison.Ordinal);
        Assert.Contains("call i32", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsDistinctBodiesForMultipleGenericStructSpecializations()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Box<T>
            {
                T value;
                public Box(T initial) { value = move initial; }
                public T Get() { return move value; }
            }
            int ReadInt(int value) { return Box<int>(value).Get(); }
            float ReadFloat(float value) { return Box<float>(value).Get(); }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol[] boxes = ns.Structs.Where(type => type.IsGenericSpecialization).ToArray();
        Assert.Equal(2, boxes.Length);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost(),
            "generic-struct-specializations");

        Assert.Contains("type { i32 }", ir, StringComparison.Ordinal);
        Assert.Contains("type { float }", ir, StringComparison.Ordinal);
        foreach (StructTypeSymbol box in boxes)
        {
            FunctionSymbol get = box.Methods.Single(method => method.Name == "Get");
            string encodedIdentity = Convert.ToHexString(Encoding.UTF8.GetBytes(get.FullName));
            Assert.Contains(encodedIdentity, ir, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Generator_RespectsRuntimeCheckCompilationOption()
    {
        const string Source = "namespace Example; int Divide(int value, int divisor) { return value / divisor; }";
        Compilation checkedCompilation = CreateCompilation(Source);
        Compilation uncheckedCompilation = checkedCompilation.WithOptions(
            new CompilationOptions(CompilationOutputKind.Library, EnableRuntimeChecks: false));

        string checkedIr = new LlvmIrGenerator().Generate(checkedCompilation);
        string uncheckedIr = new LlvmIrGenerator().Generate(uncheckedCompilation);

        Assert.Contains("call void @llvm.trap()", checkedIr, StringComparison.Ordinal);
        Assert.DoesNotContain("call void @llvm.trap()", uncheckedIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RunsRequestedDefaultOptimizationPipeline()
    {
        Compilation compilation = CreateCompilation(
            "namespace Example; export int Identity(int value) { int copy = value; return copy; }");
        string unoptimized = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(optimizationLevel: 0));
        string optimized = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(optimizationLevel: 3));

        Assert.Contains("alloca", unoptimized, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca", optimized, StringComparison.Ordinal);
        Assert.Contains("@Example_Identity(", optimized, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_VerifiesLoopVectorizerOutput()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            int Main()
            {
                int count = 100000;
                int[] values = new int[count];
                for (int i = 0; i < count; i++)
                    values[i] = values[i] * 3 + 1;
                int result = values[count - 1];
                free(values);
                return result;
            }
            """).WithOptions(new CompilationOptions(
                CompilationOutputKind.Executable,
                EnableRuntimeChecks: false));

        string optimized = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(optimizationLevel: 3));

        Assert.Contains("<", optimized, StringComparison.Ordinal);
        Assert.Contains(" x i32>", optimized, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_VerifiesCheckedArithmeticWithoutMemoryRuntime()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            int Divide(int value, int divisor) { return value / divisor; }
            uint Remainder(uint value, uint divisor) { return value % divisor; }
            long Shift(long value, int count) { return value << count; }
            """);
        string ir = new LlvmIrGenerator().Generate(compilation);
        Assert.Contains("@llvm.trap", ir, StringComparison.Ordinal);
        Assert.Contains("division.valid", ir, StringComparison.Ordinal);
        Assert.Contains("shift.count.valid", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("@malloc", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("@calloc", ir, StringComparison.Ordinal);
    }


    [Theory]
    [InlineData("i686-pc-windows-msvc", "i32", "i32")]
    [InlineData("x86_64-pc-windows-msvc", "i64", "i32")]
    [InlineData("x86_64-unknown-linux-gnu", "i64", "i64")]
    public void Generator_VerifiesTargetSizedArithmetic(string triple, string nativeType, string cLongType)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            nint Distance(int* a, int* b) { return a - b; }
            int* Advance(int* value, sbyte count) { return value + count; }
            nint Shift(nint value, int count) { return value << count; }
            clong Divide(clong value, clong divisor) { return value / divisor; }
            """);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions(triple));
        Assert.Contains("getelementptr i32", ir, StringComparison.Ordinal);
        Assert.Contains("ptrtoint ptr", ir, StringComparison.Ordinal);
        Assert.Contains("sdiv " + nativeType, ir, StringComparison.Ordinal);
        Assert.Contains("shl " + nativeType, ir, StringComparison.Ordinal);
        Assert.Contains("sdiv " + cLongType, ir, StringComparison.Ordinal);
        Assert.Contains("shift.count.valid", ir, StringComparison.Ordinal);
        Assert.Contains("division.valid", ir, StringComparison.Ordinal);
        Assert.Contains("@llvm.trap", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("add ptr", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("sub ptr", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_ChecksAllHeapAllocationsBeforeInitialization()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct S { public int Value; public S() { Value = 42; } }
            S* Object() { return new S(); }
            S* Positional() { return new S { 12 }; }
            int[] Vector(int count) { return new int[count]; }
            int[,] Matrix(int x, int y) { return new int[x,y]; }
            """);
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());
        foreach (string function in new[] { "Object", "Positional", "Vector", "Matrix" })
        {
            int start = ir.IndexOf("@" + ManagedSymbol("xenon", $"Example.{function}", "function"), StringComparison.Ordinal);
            string body = ir[start..ir.IndexOf("\n}", start, StringComparison.Ordinal)];
            string allocator = function is "Vector" or "Matrix" ? "calloc" : "malloc";
            int allocation = body.IndexOf($"call ptr @{allocator}", StringComparison.Ordinal);
            int check = body.IndexOf("allocation.valid = icmp ne ptr", StringComparison.Ordinal);
            int branch = body.IndexOf("br i1 %allocation.valid", StringComparison.Ordinal);
            Assert.True(allocation >= 0 && check > allocation && branch > check, body);
            Assert.Contains("@llvm.trap", body, StringComparison.Ordinal);
            if (function == "Object")
                Assert.True(body.IndexOf("call void @" + ManagedSymbol("xenon", "Example.S.__ctor", "function"), StringComparison.Ordinal) > branch, body);
        }
    }

    [Fact]
    public void Generator_UsesZeroedAllocatorWithoutRedundantMemsetForHeapArrays()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            int[] Create(int count) { return new int[count]; }
            """);

        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());

        Assert.Contains($"call ptr @calloc(i{IntPtr.Size * 8} 1, i{IntPtr.Size * 8}", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("call void @llvm.memset", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_ReusesCompatibleExternSymbolsAcrossNamespaces()
    {
        Compilation compilation = Compilation.Create(
            SourceText.From("namespace A; extern int Foo(int* value); int Call(int* p) { return Foo(p); }", "a.xe"),
            SourceText.From("namespace B; extern int readonly Foo(readonly int* value); int Call(readonly int* p) { return Foo(p); }", "b.xe"));
        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().Generate(compilation);
        Assert.Equal(1, ir.Split("declare i32 @Foo(", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, ir.Split("call i32 @Foo(", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("@Foo.1", ir, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("i686-pc-windows-msvc", false)]
    [InlineData("x86_64-pc-windows-msvc", true)]
    public void Generator_ValidatesExternAbiAfterTargetSelection(string triple, bool errors)
    {
        Compilation compilation = Compilation.Create(
            SourceText.From("namespace A; extern nint Foo(nint x);", "a.xe"),
            SourceText.From("namespace B; extern int Foo(int x);", "b.xe"));
        Assert.False(compilation.HasErrors);
        Compilation bound = LlvmIrGenerator.BindForTarget(compilation, new LlvmTargetOptions(triple));
        Assert.Equal(errors, bound.HasErrors);
        if (errors) Assert.Contains(bound.Diagnostics, diagnostic => diagnostic.Message.Contains("native symbol", StringComparison.Ordinal));
        else new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions(triple));
    }

    [Fact]
    public void Generator_EmitsAndVerifiesMinimalMain()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return 42;
            }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "minimal");

        Assert.Contains("define internal i32 @" + ManagedSymbol("minimal", "Example.Main", "function") + "()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("ret i32 42", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsFunctionsArithmeticCallsAndExports()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            extern int puts(readonly byte* text);

            int Add(int a, int b)
            {
                return a + b;
            }

            export int Multiply(int a, int b)
            {
                return a * b;
            }

            int Main()
            {
                int result = Add(20, 22);
                puts("Hello from Xenon");
                return result;
            }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "core");

        Assert.Contains("declare i32 @puts(ptr)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define internal i32 @" + ManagedSymbol("core", "Example.Add", "function") + "(i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @Example_Multiply(i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("add i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("mul i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @" + ManagedSymbol("core", "Example.Add", "function"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @puts", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_ReadonlyExternContractDoesNotChangeNativeAbi()
    {
        const string source = """
            namespace Example;
            extern int abs(int value);
            extern void Process(int* output, readonly int* input);
            int Main()
            {
                int input = -42;
                int output = 0;
                Process(&output, &input);
                return abs(output);
            }
            """;
        Compilation ordinary = CreateCompilation(source);
        Compilation qualified = CreateCompilation(source
            .Replace("int abs", "int readonly abs", StringComparison.Ordinal)
            .Replace("void Process", "void readonly Process", StringComparison.Ordinal));
        Assert.Empty(ordinary.Diagnostics);
        Assert.Empty(qualified.Diagnostics);
        string ordinaryIr = new LlvmIrGenerator().Generate(ordinary, "abi");
        string qualifiedIr = new LlvmIrGenerator().Generate(qualified, "abi");
        Assert.Equal(ordinaryIr, qualifiedIr);
        Assert.Contains("declare i32 @abs(i32)", qualifiedIr, StringComparison.Ordinal);
        Assert.Contains("declare void @Process(ptr, ptr)", qualifiedIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RejectsCompilationWithSemanticErrors()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return missing;
            }
            """);

        var exception = Assert.Throws<LlvmCodeGenerationException>(
            () => new LlvmIrGenerator().Generate(compilation));
        Assert.Contains("contains errors", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsAndVerifiesControlFlow()
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
                {
                    total--;
                    if (total == 110)
                        break;
                }

                return total;
            }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "control-flow");

        Assert.Contains("for.condition:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("while.condition:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("if.then:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("if.else:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("br i1", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UsesPhiNodesForShortCircuitBooleanOperators()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            bool Both(bool left, bool right)
            {
                return left && right;
            }

            bool Either(bool left, bool right)
            {
                return left || right;
            }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "short-circuit");

        Assert.Contains("logic.rhs:", llvmIr, StringComparison.Ordinal);
        Assert.Contains("phi i1", llvmIr, StringComparison.Ordinal);
        Assert.Equal(2, llvmIr.Split("phi i1", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Generator_EmitsStructLayoutAndMemberAccess()
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
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "structs");

        Assert.Contains("%Example.Vector2 = type { float, float }", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define float @Example_Sum(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Equal(2, llvmIr.Split("%Example.Vector2, ptr", StringSplitOptions.None).Length - 1);
        Assert.Contains("fadd float", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsStructConstructionAllocationAndFree()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Pair
            {
                public int X;
                public int Y;
            }

            int Main()
            {
                Pair stack = Pair { 20, 22 };
                Pair* heap = new Pair { stack.X, stack.Y };
                int result = heap->X + heap->Y;
                free(heap);
                return result;
            }
            """);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(compilation, target, "heap-struct");

        Assert.Contains("insertvalue %Example.Pair", llvmIr, StringComparison.Ordinal);
        Assert.Contains($"call ptr @malloc(i{IntPtr.Size * 8} 8)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @free", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UsesExternalXenonLinkageForPublicFunctions()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Hidden() { return 1; }
            public int Visible() { return 2; }
            """);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "visibility");

        Assert.Contains("define internal i32 @" + ManagedSymbol("visibility", "Example.Hidden", "function") + "()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @" + ManagedSymbol("visibility", "Example.Visible", "function") + "()", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("define internal i32 @" + ManagedSymbol("visibility", "Example.Visible", "function") + "()", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsConstructorDestructorAndArrayStorage()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Box
            {
                int Value;

                public Box(int value)
                {
                    Value = value;
                }

                public ~Box()
                {
                    Value = 0;
                }
            }

            int Main()
            {
                Box value = Box(42);
                Box* heap = new Box(10);
                free(heap);

                int[] dynamic = new int[10];
                dynamic[0] = 7;
                free(dynamic);

                int[] temporary = int[4];
                temporary[1] = 3;
                return temporary[1];
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(compilation, target, "lifecycle-arrays");

        Assert.Contains("@" + ManagedSymbol("lifecycle-arrays", "Example.Box.__ctor", "function"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("@" + ManagedSymbol("lifecycle-arrays", "Example.Box.__dtor", "function"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @" + ManagedSymbol("lifecycle-arrays", "Example.Box.__dtor", "function"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("stack.array = alloca i8", llvmIr, StringComparison.Ordinal);
        Assert.Contains("array.metadata.address", llvmIr, StringComparison.Ordinal);
        Assert.Contains("getelementptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call ptr @calloc", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsStructMethodsWithImplicitThis()
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

                public void Add(int amount)
                {
                    Value += amount;
                }

                public int Read()
                {
                    return Value;
                }
            }

            int Main()
            {
                Counter value = Counter(20);
                value.Add(22);

                Counter* pointer = &value;
                pointer->Add(1);

                return value.Read();
            }
            """);

        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "struct-methods");

        Assert.Contains("define void @" + ManagedSymbol("struct-methods", "Example.Counter.Add", "function") + "(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @" + ManagedSymbol("struct-methods", "Example.Counter.Read", "function") + "(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @" + ManagedSymbol("struct-methods", "Example.Counter.Add", "function") + "(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @" + ManagedSymbol("struct-methods", "Example.Counter.Read", "function") + "(ptr", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsContextuallyTypedNullPointers()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

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
                if (pointer != null)
                    return 1;

                if (null != pointer)
                    return 2;

                Consume(null);
                Box value = Box(null);
                Box* heap = new Box(null);
                free(heap);
                if (ReturnNull() == null)
                    return 0;

                return 3;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(compilation, target, "null-pointers");

        Assert.Contains("ret ptr null", llvmIr, StringComparison.Ordinal);
        Assert.Contains("store ptr null", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @" + ManagedSymbol("null-pointers", "Example.Consume", "function") + "(ptr null)", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("<null>", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsDelayedLocalInitialization()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Pair
            {
                public int X;
                public int Y;
            }

            int Main()
            {
                Pair value;
                value = Pair { 20, 22 };
                return value.X + value.Y;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string llvmIr = new LlvmIrGenerator().Generate(compilation, "delayed-init");

        Assert.Contains("%value = alloca %Example.Pair", llvmIr, StringComparison.Ordinal);
        Assert.Contains("store %Example.Pair", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsStaticFieldsAndTargetLayoutIntrinsics()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity
            {
                public int Id;
                public static int Count = 512 * 2;
            }

            int ReadCount()
            {
                return Entity.Count;
            }

            nuint Size() { return sizeof(Entity); }
            nuint Alignment() { return alignof(Entity); }
            nuint Offset() { return offsetof(Entity, Id); }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "static-layout");

        string count = ManagedSymbol("static-layout", "Example.Entity.Count", "static_field");
        Assert.Contains($"@{count} = global i32 1024", llvmIr, StringComparison.Ordinal);
        Assert.Contains($"load i32, ptr @{count}", llvmIr, StringComparison.Ordinal);
        Assert.Contains("ret i64 4", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsStoresToMutableStaticFields()
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

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "static-write");

        string value = ManagedSymbol("static-write", "Example.State.Value", "static_field");
        Assert.Contains($"store i32 41, ptr @{value}", llvmIr, StringComparison.Ordinal);
        Assert.Contains($"load i32, ptr @{value}", llvmIr, StringComparison.Ordinal);
        Assert.Contains("store i32", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_InitializesBaseConstructorBeforeDerivedBody()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity
            {
                public int Id;
                public Entity(int id) { Id = id; }
            }

            struct Enemy : Entity
            {
                public int Health;
                public Enemy(int id, int health) : base(id) { Health = health; }
            }

            int Main()
            {
                Enemy value = Enemy(1, 100);
                return value.Id + value.Health;
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "base-ctor");

        Assert.Contains("%Example.Enemy = type { %Example.Entity, i32 }", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @" + ManagedSymbol("base-ctor", "Example.Entity.__ctor", "function"), llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsVTablesAndVirtualBaseDispatch()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity
            {
                public virtual int Score() { return 1; }
            }

            struct Enemy : Entity
            {
                public override int Score() { return 42; }
            }

            int Main()
            {
                Enemy enemy = Enemy { };
                Entity* entity = &enemy;
                return entity->Score();
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "virtual-dispatch");

        Assert.Contains("@" + ManagedSymbol("virtual-dispatch", "Example.Enemy.__vtable", "vtable"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("@" + ManagedSymbol("virtual-dispatch", "Example.Enemy.Score", "function"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("virtual.slot", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_DefinesAbstractVTableSlotsWithUnreachableStubs()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            abstract struct Entity
            {
                public abstract int Score();
            }

            struct Enemy : Entity
            {
                public override int Score() { return 42; }
            }

            int Main()
            {
                Enemy enemy = Enemy { };
                Entity* entity = &enemy;
                return entity->Score();
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "abstract-vtable");

        Assert.Contains("define internal i32 @" + ManagedSymbol("abstract-vtable", "Example.Entity.Score", "function") + "(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("unreachable", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@" + ManagedSymbol("abstract-vtable", "Example.Enemy.__vtable", "vtable"), llvmIr, StringComparison.Ordinal);

        Compilation privateAbstract = CreateCompilation("""
            namespace Example;

            abstract struct Entity
            {
                abstract void Update();
            }
            """);
        Assert.Empty(privateAbstract.Diagnostics);
        string privateIr = new LlvmIrGenerator().Generate(privateAbstract, "private-abstract-vtable");
        Assert.Contains("define internal void @" + ManagedSymbol("private-abstract-vtable", "Example.Entity.Update", "function") + "(ptr", privateIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_LowersDerivedToBaseReferencesAndVirtualDispatch()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity
            {
                public virtual int Score() { return 1; }
            }

            struct Enemy : Entity
            {
                public override int Score() { return 42; }
            }

            int Read(Entity& entity)
            {
                return entity.Score();
            }

            int Main()
            {
                Enemy enemy = Enemy { };
                Entity& entity = enemy;
                return Read(entity);
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "reference-dispatch");

        Assert.Contains("define internal i32 @" + ManagedSymbol("reference-dispatch", "Example.Read", "function") + "(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("virtual.slot", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_LowersDynamicInterfaceReferences()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            interface IScore { int Score(); }

            struct Enemy : IScore
            {
                public int Score() { return 42; }
            }

            int Read(IScore& score)
            {
                return score.Score();
            }

            int Main()
            {
                Enemy enemy = Enemy { };
                IScore& score = enemy;
                return Read(score);
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "interface-reference-dispatch");

        Assert.Contains("define internal i32 @" + ManagedSymbol("interface-reference-dispatch", "Example.Read", "function") + "(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@" + ManagedSymbol("interface-reference-dispatch", "Example.Enemy.Example.IScore.__itable", "interface_table"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("interface.slot", llvmIr, StringComparison.Ordinal);
        Assert.Equal(1, llvmIr.Split("call i32 @strcmp", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Generator_MaterializesDerivedTemporaryUsingItsSourceType()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base
            {
                public Base() { }
            }

            struct Derived : Base
            {
                int Value;
                public Derived() { Value = 42; }
            }

            int Main()
            {
                readonly Base& value = Derived();
                return 0;
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "derived-reference-temporary");

        Assert.Contains("alloca %Example.Derived", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("alloca %Example.Base,", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsSignedStaticConstantsWithoutOverflow()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Test
            {
                public static bool Less = -1 < 1;
                public static int Divide = -3 / 2;
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "signed-static-constants");

        Assert.Contains("@" + ManagedSymbol("signed-static-constants", "Example.Test.Less", "static_field") + " = global i1 true", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@" + ManagedSymbol("signed-static-constants", "Example.Test.Divide", "static_field") + " = global i32 -1", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsInterfaceTableAndDynamicInterfaceCall()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            interface IScore
            {
                int Score();
            }

            struct Enemy : IScore
            {
                public int Score() { return 42; }
            }

            int Main()
            {
                Enemy enemy = Enemy { };
                IScore score = enemy;
                IScore* pointer = &score;
                return pointer->Score();
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "interface-dispatch");

        Assert.Contains("%Example.IScore = type { ptr, ptr }", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@" + ManagedSymbol("interface-dispatch", "Example.Enemy.Example.IScore.__itable", "interface_table"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("interface.slot", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UsesSlotsFromTheStaticInterfaceType()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            interface IA { int A(); }
            interface IB { int B(); }
            interface IC : IA, IB { int C(); }

            struct Value : IC
            {
                public int A() { return 10; }
                public int B() { return 20; }
                public int C() { return 30; }
            }

            int Main()
            {
                Value value = Value { };
                IB ib = value;
                return ib.B();
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "multiple-interface-inheritance");

        Assert.Contains("@" + ManagedSymbol("multiple-interface-inheritance", "Example.Value.Example.IB.__itable", "interface_table") + " = global [2 x ptr]", llvmIr, StringComparison.Ordinal);
        Assert.Contains("i32 0, i32 0", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_PreservesCompatibleInheritedInterfaceImplementation()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            interface IValue { int Get(int value); }

            struct Base : IValue
            {
                public int Get(int value) { return value + 1; }
            }

            struct Derived : Base
            {
                public int Get() { return 100; }
            }

            int Main()
            {
                Derived derived = Derived { };
                IValue value = derived;
                return value.Get(10);
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().Generate(compilation, "inherited-interface-implementation");

        Assert.Contains("@" + ManagedSymbol("inherited-interface-implementation", "Example.Derived.Example.IValue.__itable", "interface_table") + " = global [2 x ptr] [ptr @" + ManagedSymbol("inherited-interface-implementation", "Example.Derived.__imap", "interface_map") + ", ptr @" + ManagedSymbol("inherited-interface-implementation", "Example.Base.Get", "function") + "]", llvmIr, StringComparison.Ordinal);
        Assert.Contains("interface.runtime.map = load ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@" + ManagedSymbol("inherited-interface-implementation", "Example.Derived.__vtable", "vtable") + " = global [1 x ptr] [ptr @" + ManagedSymbol("inherited-interface-implementation", "Example.Derived.__imap", "interface_map") + "]", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UsesVirtualDestructorWhenFreeingThroughBasePointer()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity
            {
                public virtual ~Entity() { }
            }

            struct Enemy : Entity
            {
                public override ~Enemy() { }
            }

            int Main()
            {
                Enemy* enemy = new Enemy { };
                Entity* entity = enemy;
                free(entity);
                return 0;
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "virtual-destructor");

        Assert.Contains("@" + ManagedSymbol("virtual-destructor", "Example.Enemy.__vtable", "vtable"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("@" + ManagedSymbol("virtual-destructor", "Example.Enemy.__dtor", "function"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("destructor.slot", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_CallsInheritedDestructorWhenDerivedDeclaresNone()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity { public ~Entity() { } }
            struct Enemy : Entity { }

            int Main()
            {
                Enemy* enemy = new Enemy { };
                free(enemy);
                return 0;
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost(), "inherited-destructor");

        Assert.Contains("call void @" + ManagedSymbol("inherited-destructor", "Example.Entity.__dtor", "function"), llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_ChainsVirtualDestructorAcrossIntermediateTypeWithoutDestructor()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base { public virtual ~Base() { } }
            struct Middle : Base { }
            struct Derived : Middle { public override ~Derived() { } }

            int Main()
            {
                Derived* derived = new Derived { };
                Base* value = derived;
                free(value);
                return 0;
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost(), "destructor-gap");

        Assert.Contains("@" + ManagedSymbol("destructor-gap", "Example.Derived.__vtable", "vtable"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @" + ManagedSymbol("destructor-gap", "Example.Base.__dtor", "function"), llvmIr, StringComparison.Ordinal);
        Assert.Contains("destructor.slot", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsTargetedIrWithNativeEntryPoint()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return 0;
            }
            """);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(
            AsExecutable(compilation),
            target,
            "targeted");

        Assert.Contains($"target triple = \"{target.Triple}\"", llvmIr, StringComparison.Ordinal);
        Assert.Contains("target datalayout =", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @main()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @" + ManagedSymbol("targeted", "Example.Main", "function") + "()", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_DoesNotTreatStructMainMethodAsExecutableEntryPoint()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Worker
            {
                public int Main()
                {
                    return 7;
                }
            }

            int Main()
            {
                Worker worker = Worker { };
                return worker.Main();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();

        string llvmIr = new LlvmIrGenerator().GenerateForTarget(
            AsExecutable(compilation),
            target,
            "method-main");

        Assert.Contains("define i32 @" + ManagedSymbol("method-main", "Example.Worker.Main", "function") + "(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @main()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @" + ManagedSymbol("method-main", "Example.Main", "function") + "()", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UsesCompilationOutputKindAsTheOnlyEntryPointAuthority()
    {
        Compilation library = CreateCompilation("namespace Example; int Main() { return 42; }");
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();

        string libraryIr = new LlvmIrGenerator().GenerateForTarget(library, target, "library-main");
        string executableIr = new LlvmIrGenerator().GenerateForTarget(
            AsExecutable(library), target, "executable-main");

        Assert.DoesNotContain("define i32 @main()", libraryIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @main()", executableIr, StringComparison.Ordinal);
        Assert.Equal(CompilationOutputKind.Library, library.Options.OutputKind);
    }

    [Theory]
    [InlineData(LlvmNativeReferenceKind.Static, false)]
    [InlineData(LlvmNativeReferenceKind.Shared, true)]
    public void Generator_UsesNativeReferenceKindForReferencedRuntimeData(
        LlvmNativeReferenceKind referenceKind,
        bool expectsDllImport)
    {
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(positionIndependentCode: true);
        Compilation library = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Library),
            references: null,
            SourceText.From("""
                namespace RuntimeOwnership.Core;
                interface IState { int Read(); }
                struct State : IState
                {
                    public static int Value = 1;
                    public int Initialized = 40;
                    public State() { }
                    public int Read() { return Initialized; }
                    public static int AddTwo(int value) { return value + 2; }
                }
                public int ReadStatic() { return State.Value; }
                """, "core.xe"));
        library = LlvmIrGenerator.BindForTarget(library, target);
        Compilation application = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Executable),
            [new SourceCompilationReference(library)],
            SourceText.From("""
                using RuntimeOwnership.Core;
                namespace RuntimeOwnership.App;
                int Main()
                {
                    State.Value = 40;
                    State state = State();
                    IState view = state;
                    int initialized = view.Read();
                    return State.AddTwo(ReadStatic());
                }
                """, "app.xe"));
        application = LlvmIrGenerator.BindForTarget(application, target);

        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        Assert.False(application.HasErrors, string.Join(Environment.NewLine, application.Diagnostics));
        string libraryIr = new LlvmIrGenerator().GenerateForTarget(library, target, "runtime-core");
        string applicationIr = new LlvmIrGenerator().GenerateForTarget(
            application,
            target,
            "runtime-app",
            new LlvmCodeGenerationOptions("runtime-app", [
                new LlvmNativeReference(library, referenceKind, "runtime-core")
            ]));

        string staticField = ManagedSymbol("runtime-core", "RuntimeOwnership.Core.State.Value", "static_field");
        string interfaceKey = ManagedSymbol("runtime-core", "RuntimeOwnership.Core.IState.__interface_key", "interface_key");
        string vtable = ManagedSymbol("runtime-core", "RuntimeOwnership.Core.State.__vtable", "vtable");
        string interfaceTable = ManagedSymbol("runtime-core", "RuntimeOwnership.Core.State.RuntimeOwnership.Core.IState.__itable", "interface_table");
        string interfaceMap = ManagedSymbol("runtime-core", "RuntimeOwnership.Core.State.__imap", "interface_map");
        string initializer = ManagedSymbol("runtime-core", "RuntimeOwnership.Core.State.__init_fields", "function");
        string constructor = ManagedSymbol("runtime-core", "RuntimeOwnership.Core.State.__ctor", "function");
        string addTwo = ManagedSymbol("runtime-core", "RuntimeOwnership.Core.State.AddTwo", "function");
        Assert.Contains($"@{staticField} = global i32 1", libraryIr,
            StringComparison.Ordinal);
        Assert.Contains($"@{interfaceKey} = internal constant", libraryIr,
            StringComparison.Ordinal);
        string storage = expectsDllImport && OperatingSystem.IsWindows()
            ? "external dllimport global" : "external global";
        Assert.Contains($"@{staticField} = {storage} i32",
            applicationIr, StringComparison.Ordinal);
        Assert.Contains($"@{interfaceKey} = internal constant",
            applicationIr, StringComparison.Ordinal);
        Assert.Contains($"@{vtable} = {storage}",
            applicationIr, StringComparison.Ordinal);
        Assert.Contains($"@{interfaceTable} = {storage}",
            applicationIr, StringComparison.Ordinal);
        Assert.Contains($"@{interfaceMap} = {storage}",
            applicationIr, StringComparison.Ordinal);
        if (!expectsDllImport)
            Assert.DoesNotContain("dllimport global", applicationIr, StringComparison.Ordinal);
        Assert.Contains($"declare void @{initializer}", applicationIr,
            StringComparison.Ordinal);
        Assert.Contains($"declare void @{constructor}", applicationIr,
            StringComparison.Ordinal);
        Assert.Contains($"declare i32 @{addTwo}", applicationIr,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"define void @{initializer}", applicationIr,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"define i32 @{addTwo}", applicationIr,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_InterfaceRuntimeIdentityIsStableAcrossModulesAndScopedByAbiIdentity()
    {
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(positionIndependentCode: true);
        const string contractSource = "namespace Contracts; interface IValue { int Read(); }";
        Compilation contract = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Library),
            references: null,
            SourceText.From(contractSource, "contract.xe"));
        Compilation application = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Library),
            [new SourceCompilationReference(contract)],
            SourceText.From("using Contracts; namespace App; int Read(IValue value) { return value.Read(); }", "app.xe"));

        string contractIr = new LlvmIrGenerator().GenerateForTarget(contract, target, "ContractsModule");
        string applicationIr = new LlvmIrGenerator().GenerateForTarget(
            application,
            target,
            "ApplicationModule",
            new LlvmCodeGenerationOptions("ApplicationModule", [
                new LlvmNativeReference(contract, LlvmNativeReferenceKind.Static, "ContractsModule")
            ]));
        Compilation independent = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Library),
            references: null,
            SourceText.From(contractSource, "independent.xe"));
        string otherModuleIr = new LlvmIrGenerator().GenerateForTarget(independent, target, "OtherContractsModule");

        static string KeyDefinition(string ir) => ir.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line =>
                line.Contains(" = internal constant", StringComparison.Ordinal) &&
                line.Contains(":Contracts.IValue\\00\"", StringComparison.Ordinal));

        Assert.Equal(KeyDefinition(contractIr), KeyDefinition(applicationIr));
        Assert.NotEqual(KeyDefinition(contractIr), KeyDefinition(otherModuleIr));
    }

    [Fact]
    public void Generator_ProjectScopesSameFunctionNamesAcrossReferencesAndLeavesAmbiguityToSemantics()
    {
        Compilation libraryA = Compilation.Create(SourceText.From(
            "namespace Shared; public int Value() { return 1; }", "a.xe"));
        Compilation libraryB = Compilation.Create(SourceText.From(
            "namespace Shared; public int Value() { return 2; }", "b.xe"));
        Compilation unused = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Executable),
            [new SourceCompilationReference(libraryA), new SourceCompilationReference(libraryB)],
            SourceText.From("namespace App; int Main() { return 0; }", "unused.xe"));
        var options = new LlvmCodeGenerationOptions("App", [
            new LlvmNativeReference(libraryA, LlvmNativeReferenceKind.Static, "LibraryA"),
            new LlvmNativeReference(libraryB, LlvmNativeReferenceKind.Static, "LibraryB"),
        ]);

        Assert.False(unused.HasErrors, string.Join(Environment.NewLine, unused.Diagnostics));
        string ir = new LlvmIrGenerator().Generate(unused, "App", options);
        string symbolA = ManagedSymbol("LibraryA", "Shared.Value", "function");
        string symbolB = ManagedSymbol("LibraryB", "Shared.Value", "function");
        Assert.NotEqual(symbolA, symbolB);
        Assert.Contains($"declare i32 @{symbolA}()", ir, StringComparison.Ordinal);
        Assert.Contains($"declare i32 @{symbolB}()", ir, StringComparison.Ordinal);

        Compilation reversed = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Executable),
            [new SourceCompilationReference(libraryB), new SourceCompilationReference(libraryA)],
            SourceText.From("namespace App; int Main() { return 0; }", "reversed.xe"));
        string reversedIr = new LlvmIrGenerator().Generate(
            reversed,
            "App",
            new LlvmCodeGenerationOptions("App", [
                new LlvmNativeReference(libraryB, LlvmNativeReferenceKind.Static, "LibraryB"),
                new LlvmNativeReference(libraryA, LlvmNativeReferenceKind.Static, "LibraryA"),
            ]));
        Assert.Contains($"declare i32 @{symbolA}()", reversedIr, StringComparison.Ordinal);
        Assert.Contains($"declare i32 @{symbolB}()", reversedIr, StringComparison.Ordinal);

        Compilation ambiguous = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Executable),
            [new SourceCompilationReference(libraryA), new SourceCompilationReference(libraryB)],
            SourceText.From("using Shared; namespace App; int Main() { return Value(); }", "ambiguous.xe"));
        Assert.Contains(ambiguous.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AmbiguousName);
        Assert.Throws<LlvmCodeGenerationException>(() =>
            new LlvmIrGenerator().Generate(ambiguous, "App", options));
    }

    [Fact]
    public void Generator_ProjectScopesReferencedTypeRuntimeSymbols()
    {
        const string source = """
            namespace Shared;
            interface IValue { int Read(); }
            struct State : IValue
            {
                public static int Value = 1;
                public int Seed = 1;
                public int Read() { return Seed; }
            }
            """;
        Compilation libraryA = Compilation.Create(SourceText.From(source, "a.xe"));
        Compilation libraryB = Compilation.Create(SourceText.From(source, "b.xe"));
        Compilation app = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Library),
            [new SourceCompilationReference(libraryA), new SourceCompilationReference(libraryB)],
            SourceText.From("namespace App; int Main() { return 0; }", "app.xe"));
        var options = new LlvmCodeGenerationOptions("App", [
            new LlvmNativeReference(libraryA, LlvmNativeReferenceKind.Static, "LibraryA"),
            new LlvmNativeReference(libraryB, LlvmNativeReferenceKind.Static, "LibraryB"),
        ]);

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        string ir = new LlvmIrGenerator().Generate(app, "App", options);
        foreach (string abiIdentity in new[] { "LibraryA", "LibraryB" })
        {
            Assert.Contains("@" + ManagedSymbol(abiIdentity, "Shared.State.Value", "static_field"), ir, StringComparison.Ordinal);
            Assert.Contains("@" + ManagedSymbol(abiIdentity, "Shared.State.__vtable", "vtable"), ir, StringComparison.Ordinal);
            Assert.Contains("@" + ManagedSymbol(abiIdentity, "Shared.State.__imap", "interface_map"), ir, StringComparison.Ordinal);
            Assert.Contains("@" + ManagedSymbol(abiIdentity, "Shared.State.Shared.IValue.__itable", "interface_table"), ir, StringComparison.Ordinal);
            Assert.Contains("@" + ManagedSymbol(abiIdentity, "Shared.State.__init_fields", "function"), ir, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Generator_PreservesExplicitNativeAbiNamesWhileManglingManagedSymbols()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            extern int puts(readonly byte* value);
            export int Api() { return 1; }
            public int Managed() { return 2; }
            """);

        string ir = new LlvmIrGenerator().Generate(compilation, "ProjectA");

        Assert.Contains("declare i32 @puts(ptr)", ir, StringComparison.Ordinal);
        Assert.Contains("define i32 @Example_Api()", ir, StringComparison.Ordinal);
        Assert.Contains("define i32 @" + ManagedSymbol("ProjectA", "Example.Managed", "function") + "()", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("puts.__xenon_", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("Example_Api.__xenon_", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RequiresCompleteExactNativeReferenceMetadata()
    {
        Compilation library1 = Compilation.Create(SourceText.From(
            "namespace Lib; public int Value() { return 1; }", "library.xe"));
        Compilation library2 = library1.ReplaceSyntaxTree(
            library1.SyntaxTrees[0],
            SyntaxTree.Parse(SourceText.From(
                "namespace Lib; public int Value() { return 2; }", "library.xe")));
        Compilation app = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Library),
            [new SourceCompilationReference(library1)],
            SourceText.From("namespace App; int Main() { return 0; }", "app.xe"));

        LlvmCodeGenerationException missing = Assert.Throws<LlvmCodeGenerationException>(() =>
            new LlvmIrGenerator().Generate(app, "App"));
        Assert.Contains("Missing native ABI metadata", missing.Message, StringComparison.Ordinal);

        LlvmCodeGenerationException wrongSnapshot = Assert.Throws<LlvmCodeGenerationException>(() =>
            new LlvmIrGenerator().Generate(app, "App", new LlvmCodeGenerationOptions("App", [
                new LlvmNativeReference(library2, LlvmNativeReferenceKind.Static, "Library")
            ])));
        Assert.Contains("does not match", wrongSnapshot.Message, StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            new LlvmNativeReference(library1, LlvmNativeReferenceKind.Static, " "));
        Assert.Throws<ArgumentNullException>(() =>
            new LlvmNativeReference(library1, LlvmNativeReferenceKind.Static, null!));
        Assert.Throws<ArgumentException>(() => new LlvmCodeGenerationOptions(""));
        Assert.Throws<ArgumentNullException>(() => new LlvmCodeGenerationOptions(null!));
        Assert.Throws<ArgumentException>(() => new LlvmCodeGenerationOptions("App", [
            new LlvmNativeReference(library1, LlvmNativeReferenceKind.Static, "Library"),
            new LlvmNativeReference(library1, LlvmNativeReferenceKind.Shared, "Library"),
        ]));

        string ir = new LlvmIrGenerator().Generate(app, "App", new LlvmCodeGenerationOptions("App", [
            new LlvmNativeReference(library1, LlvmNativeReferenceKind.Static, "Library")
        ]));
        Assert.Contains("@" + ManagedSymbol("Library", "Lib.Value", "function"), ir, StringComparison.Ordinal);

        Compilation middle = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Library),
            [new SourceCompilationReference(library1)],
            SourceText.From("namespace Middle; public int Ready() { return 1; }", "middle.xe"));
        Compilation transitiveApp = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Library),
            [new SourceCompilationReference(middle)],
            SourceText.From("namespace App; int Main() { return 0; }", "transitive-app.xe"));
        LlvmCodeGenerationException incompleteClosure = Assert.Throws<LlvmCodeGenerationException>(() =>
            new LlvmIrGenerator().Generate(
                transitiveApp,
                "App",
                new LlvmCodeGenerationOptions("App", [
                    new LlvmNativeReference(middle, LlvmNativeReferenceKind.Static, "Middle")
                ])));
        Assert.Contains("Missing native ABI metadata", incompleteClosure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectEmitter_EmitsNonEmptyObjectForHostTarget()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int Main()
            {
                return 42;
            }
            """);
        LlvmTargetOptions options = LlvmTargetOptions.CreateHost();
        string directory = CreateTemporaryDirectory();
        string outputPath = Path.Combine(
            directory,
            $"main{LlvmTargetPlatform.GetObjectFileExtension(options.Triple)}");

        try
        {
            LlvmObjectFile result = new LlvmObjectEmitter().Emit(
                AsExecutable(compilation),
                outputPath,
                options,
                "object-test");

            Assert.Equal(Path.GetFullPath(outputPath), result.Path);
            Assert.Equal(options.Triple, result.TargetTriple);
            Assert.False(string.IsNullOrWhiteSpace(result.DataLayout));
            Assert.True(new FileInfo(result.Path).Length > 0);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObjectEmitter_LowersTargetSizedIntegerTypes()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            nint NativeIdentity(nint value)
            {
                return value;
            }

            clong CIdentity(clong value)
            {
                return value;
            }
            """);
        LlvmTargetOptions options = LlvmTargetOptions.CreateHost(optimizationLevel: 2);
        string directory = CreateTemporaryDirectory();
        string outputPath = Path.Combine(
            directory,
            $"native-types{LlvmTargetPlatform.GetObjectFileExtension(options.Triple)}");

        try
        {
            LlvmObjectFile result = new LlvmObjectEmitter().Emit(
                compilation,
                outputPath,
                options,
                "native-types");

            Assert.True(File.Exists(result.Path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObjectEmitter_EmitsObjectForExplicitCrossTarget()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            export int Add(int left, int right)
            {
                return left + right;
            }
            """);
        var options = new LlvmTargetOptions("aarch64-unknown-linux-gnu", OptimizationLevel: 2);
        string directory = CreateTemporaryDirectory();
        string outputPath = Path.Combine(directory, "library.o");

        try
        {
            LlvmObjectFile result = new LlvmObjectEmitter().Emit(
                compilation,
                outputPath,
                options,
                "cross-target");
            byte[] header = File.ReadAllBytes(result.Path)[..4];

            Assert.Equal([0x7f, (byte)'E', (byte)'L', (byte)'F'], header);
            Assert.Equal("aarch64-unknown-linux-gnu", result.TargetTriple);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ObjectEmitter_RejectsExecutableWithoutValidMain()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            int NotMain()
            {
                return 0;
            }
            """);
        LlvmTargetOptions options = LlvmTargetOptions.CreateHost();
        string directory = CreateTemporaryDirectory();
        string outputPath = Path.Combine(
            directory,
            $"missing-main{LlvmTargetPlatform.GetObjectFileExtension(options.Triple)}");

        try
        {
            LlvmCodeGenerationException exception = Assert.Throws<LlvmCodeGenerationException>(
                () => new LlvmObjectEmitter().Emit(
                    AsExecutable(compilation),
                    outputPath,
                    options,
                    "missing-main"));

            Assert.Contains("int Main()", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("byte", 8)]
    [InlineData("sbyte", 8)]
    [InlineData("short", 16)]
    [InlineData("ushort", 16)]
    [InlineData("int", 32)]
    [InlineData("uint", 32)]
    [InlineData("long", 64)]
    [InlineData("ulong", 64)]
    public void Generator_LowersEnumStorageAndSwitchToUnderlyingInteger(string underlying, int bits)
    {
        Compilation compilation = CreateCompilation($$"""
            namespace Example;
            enum E : {{underlying}} { Zero, Value = 42 }
            int Test(E value)
            {
                switch (value) { case E.Zero: return 0; case E.Value: return cast<int>(value); default: return -1; }
            }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().Generate(compilation);
        Assert.Contains($"switch i{bits}", ir, StringComparison.Ordinal);
        Assert.Contains($"i{bits} 42, label %switch.case", ir, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("i686-pc-windows-msvc", 32)]
    [InlineData("x86_64-pc-windows-msvc", 64)]
    [InlineData("aarch64-unknown-linux-gnu", 64)]
    public void Generator_VerifiesArrayMetadataAndCheckedArithmeticAcrossTargets(string triple, int pointerBits)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            enum State : nint { Ready = 1 }
            int Main()
            {
                long[,] values = new long[2,3];
                values[1,2] = cast<long>(42);
                int dimension = 1;
                int length = values.GetLength(dimension);
                int result = cast<int>(values[1,2]) + cast<int>(State.Ready) - 1;
                free(values);
                return result;
            }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions(triple));
        Assert.Contains($"call ptr @calloc(i{pointerBits}", ir, StringComparison.Ordinal);
        Assert.Contains("array.dimension.inrange = icmp ult i32", ir, StringComparison.Ordinal);
        Assert.Contains("array.linear.index", ir, StringComparison.Ordinal);
        Assert.Contains("call void @llvm.trap()", ir, StringComparison.Ordinal);
        Assert.Contains("array.free.end", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_KeepsSwitchBreakMergeReachableBeforeUnreachableReturn()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            int Test(int value)
            {
                switch (value) { default: break; return 0; }
                return 42;
            }
            """);
        string ir = new LlvmIrGenerator().Generate(compilation);
        Assert.Contains("ret i32 42", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RejectsTargetDependentEnumOverflowBeforeEmittingValues()
    {
        Compilation compilation = CreateCompilation("namespace Example; enum E : nint { Large = 4294967296 }");
        Assert.False(compilation.HasErrors);
        Assert.Throws<LlvmCodeGenerationException>(() => new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions("i686-pc-windows-msvc")));
        new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions("x86_64-pc-windows-msvc"));
    }

    [Theory]
    [InlineData("i686-pc-windows-msvc", 4, 4)]
    [InlineData("x86_64-pc-windows-msvc", 8, 4)]
    [InlineData("x86_64-unknown-linux-gnu", 8, 8)]
    [InlineData("aarch64-unknown-linux-gnu", 8, 8)]
    public void Generator_BindsLayoutConstantsEnumsAndCasesForSelectedAbi(string triple, int pointerBytes, int cLongBytes)
    {
        Compilation original = CreateCompilation("""
            namespace Example;
            struct Packet { public byte Tag; public nint Payload; }
            enum Layout
            {
                Size = cast<int>(sizeof(Packet)),
                Next,
                TwiceNext = cast<int>(Next) * 2,
                Alignment = cast<int>(alignof(Packet)),
                Offset = cast<int>(offsetof(Packet, Payload)),
                CLong = cast<int>(sizeof(clong))
            }
            int Select(Layout layout)
            {
                const int NativeSize = cast<int>(sizeof(nint));
                const int Next = NativeSize + 1;
                switch (layout) { case Layout.Next: return Next; default: return 0; }
            }
            int SelectSize(nuint size)
            {
                switch (size) { case sizeof(nint): return 42; default: return 0; }
            }
            """);
        Assert.False(original.HasErrors, string.Join(Environment.NewLine, original.Diagnostics));
        Assert.True(original.RequiresTargetLayout);
        var options = new LlvmTargetOptions(triple);
        Compilation bound = LlvmIrGenerator.BindForTarget(original, options);
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        Assert.False(bound.RequiresTargetLayout);
        var enumeration = Assert.Single(Assert.Single(bound.SemanticModel.GlobalNamespace.Namespaces).Enums);
        Assert.Equal([pointerBytes * 2, pointerBytes * 2 + 1, (pointerBytes * 2 + 1) * 2, pointerBytes, pointerBytes, cLongBytes],
            enumeration.Members.Select(member => (int)member.Value!).ToArray());
        string ir = new LlvmIrGenerator().GenerateForTarget(original, options);
        Assert.Contains($"switch i{pointerBytes * 8}", ir, StringComparison.Ordinal);
        Assert.Contains($"i32 {pointerBytes * 2 + 1}, label %switch.case", ir, StringComparison.Ordinal);
        Assert.True(original.RequiresTargetLayout);
        Assert.All(Assert.Single(Assert.Single(original.SemanticModel.GlobalNamespace.Namespaces).Enums).Members,
            member => Assert.Null(member.Value));
    }

    [Fact]
    public void Generator_RebindsOneCompilationAcrossDifferentTargetsWithoutCachingValues()
    {
        Compilation original = CreateCompilation("""
            namespace Example;
            const nuint Native = cast<nuint>(4294967296);
            enum E : ulong { Value = cast<ulong>(Native), Next }
            int Select(nuint value)
            {
                switch(value) { case cast<nuint>(0): return 1; case Native: return 2; default: return 3; }
            }
            """);
        Assert.False(original.HasErrors);
        Compilation wide = LlvmIrGenerator.BindForTarget(original, new LlvmTargetOptions("x86_64-pc-windows-msvc"));
        Compilation narrow = LlvmIrGenerator.BindForTarget(original, new LlvmTargetOptions("i686-pc-windows-msvc"));
        Compilation wideAgain = LlvmIrGenerator.BindForTarget(original, new LlvmTargetOptions("x86_64-pc-windows-msvc"));
        Assert.False(wide.HasErrors, string.Join(Environment.NewLine, wide.Diagnostics));
        Assert.False(wideAgain.HasErrors);
        Assert.Contains(narrow.Diagnostics, diagnostic => diagnostic.Message == "duplicate case value");
        Assert.Equal(4294967296UL, Assert.Single(Assert.Single(wide.SemanticModel.GlobalNamespace.Namespaces).Enums).Members[0].Value);
        Assert.Equal(0UL, Assert.Single(Assert.Single(narrow.SemanticModel.GlobalNamespace.Namespaces).Enums).Members[0].Value);
        Assert.False(original.HasErrors);
    }

    [Fact]
    public void TargetBoundCompilationRemainsValidAcrossAllSnapshotDerivations()
    {
        SourceText source = SourceText.From("""
            namespace Example;
            struct Packet { public byte Tag; public nint Payload; }
            enum Layout
            {
                Size = cast<int>(sizeof(Packet)),
                Alignment = cast<int>(alignof(Packet)),
                Native = cast<int>(sizeof(nuint)),
                CLong = cast<int>(sizeof(culong))
            }
            """, "layout.xe");
        Compilation original = Compilation.Create(source);
        LlvmTargetOptions options = LlvmTargetOptions.CreateHost();
        Compilation target = LlvmIrGenerator.BindForTarget(original, options);
        Compilation reference = Compilation.Create(SourceText.From(
            "namespace Dependency; public int Value() { return 1; }", "dependency.xe"));

        SyntaxTree oldTree = target.SyntaxTrees[0];
        SyntaxTree replacementTree = SyntaxTree.Parse(oldTree.Source.WithText(oldTree.Source.Text +
            Environment.NewLine + "int Identity(int value) { return value; }"));
        Compilation replaced = target.ReplaceSyntaxTree(oldTree, replacementTree);
        Compilation withOptions = target.WithOptions(new CompilationOptions(CompilationOutputKind.Executable));
        Compilation withReferences = target.WithReferences([new SourceCompilationReference(reference)]);
        SyntaxTree addedTree = SyntaxTree.Parse(SourceText.From(
            "namespace Extra; struct Marker { public nint Value; }", "extra.xe"));
        Compilation added = target.AddSyntaxTrees(addedTree);
        Compilation removed = added.RemoveSyntaxTrees(addedTree);

        int pointerBytes = IntPtr.Size;
        int cLongBytes = OperatingSystem.IsWindows() ? 4 : pointerBytes;
        foreach (Compilation snapshot in new[] { target, replaced, withOptions, withReferences, added, removed })
        {
            Assert.False(snapshot.HasErrors, string.Join(Environment.NewLine, snapshot.Diagnostics));
            Assert.NotNull(snapshot.TargetLayout);
            var layout = Assert.Single(snapshot.SemanticModel.GlobalNamespace.Namespaces
                .Single(@namespace => @namespace.Name == "Example").Enums);
            Assert.Equal([pointerBytes * 2, pointerBytes, pointerBytes, cLongBytes],
                layout.Members.Select(member => (int)member.Value!).ToArray());
        }

        Assert.Null(original.TargetLayout);
        Assert.True(original.RequiresTargetLayout);
        Assert.Contains("target datalayout =", new LlvmIrGenerator().GenerateForTarget(target, options),
            StringComparison.Ordinal);
        Assert.Equal(CompilationOutputKind.Library, target.Options.OutputKind);
        Assert.Equal(CompilationOutputKind.Executable, withOptions.Options.OutputKind);
    }

    [Theory]
    [InlineData("enum E : byte { A = cast<int>(sizeof(nint)) * 32 - 1, B }", "x86_64-pc-windows-msvc", "i686-pc-windows-msvc", "out of range")]
    [InlineData("enum E { A = 1 / (cast<int>(sizeof(nint)) - 4) }", "i686-pc-windows-msvc", "x86_64-pc-windows-msvc", "valid operations")]
    [InlineData("void M(nuint x) { switch(x) { case sizeof(nint): break; case cast<nuint>(4): break; } }", "i686-pc-windows-msvc", "x86_64-pc-windows-msvc", "duplicate case")]
    [InlineData("void M(int x) { switch(x) { case 1 / (cast<int>(sizeof(nint)) - 4): break; } }", "i686-pc-windows-msvc", "x86_64-pc-windows-msvc", "compile-time constant")]
    [InlineData("void M() { int[] a = new int[1]; a.GetLength(cast<int>(sizeof(nint)) - 4); free(a); }", "x86_64-pc-windows-msvc", "i686-pc-windows-msvc", "dimension must be")]
    public void Generator_ReportsTargetDependentErrorsInSemanticPass(string source, string invalidTarget, string validTarget, string diagnostic)
    {
        Compilation original = CreateCompilation("namespace Example; " + source);
        Assert.False(original.HasErrors, string.Join(Environment.NewLine, original.Diagnostics));
        Compilation valid = LlvmIrGenerator.BindForTarget(original, new LlvmTargetOptions(validTarget));
        Assert.False(valid.HasErrors, string.Join(Environment.NewLine, valid.Diagnostics));
        Compilation invalid = LlvmIrGenerator.BindForTarget(original, new LlvmTargetOptions(invalidTarget));
        Assert.Contains(invalid.Diagnostics, item => item.Message.Contains(diagnostic, StringComparison.Ordinal));
        Assert.All(invalid.Diagnostics, item => Assert.Equal("test.xe", item.Location.Source.Path));
        LlvmCodeGenerationException error = Assert.Throws<LlvmCodeGenerationException>(() =>
            new LlvmIrGenerator().GenerateForTarget(original, new LlvmTargetOptions(invalidTarget)));
        Assert.Contains("Target-specific semantic validation failed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UsesInheritanceVirtualDispatchAndInterfaceAbiInConstants()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            interface IValue { int Read(); }
            struct Base { public int Id; public virtual int Read() { return Id; } }
            struct Derived : Base { public nint Tail; }
            enum Layout { DerivedSize = cast<int>(sizeof(Derived)), TailOffset = cast<int>(offsetof(Derived, Tail)), InterfaceSize = cast<int>(sizeof(IValue)) }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        foreach (var (triple, pointer) in new[] { ("i686-pc-windows-msvc", 4), ("x86_64-pc-windows-msvc", 8) })
        {
            Compilation bound = LlvmIrGenerator.BindForTarget(compilation, new LlvmTargetOptions(triple));
            Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
            var values = Assert.Single(Assert.Single(bound.SemanticModel.GlobalNamespace.Namespaces).Enums).Members.Select(member => (int)member.Value!).ToArray();
            Assert.Equal([pointer * 3, pointer * 2, pointer * 2], values);
            new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions(triple));
        }
    }

    [Fact]
    public void Generator_RequiresExplicitTargetForDeferredValuesAndPreservesExistingObjectOnError()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            enum E : byte { A = cast<int>(sizeof(nint)) * 32 - 1, B }
            """);
        Assert.Contains("target layout", Assert.Throws<LlvmCodeGenerationException>(() => new LlvmIrGenerator().Generate(compilation)).Message, StringComparison.Ordinal);
        string directory = CreateTemporaryDirectory();
        string path = Path.Combine(directory, "existing.obj");
        byte[] original = [1, 2, 3, 4];
        File.WriteAllBytes(path, original);
        try
        {
            Assert.Throws<LlvmCodeGenerationException>(() => new LlvmObjectEmitter().Emit(compilation, path, new LlvmTargetOptions("x86_64-pc-windows-msvc")));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("i686-pc-windows-msvc", 2147483648UL)]
    [InlineData("x86_64-pc-windows-msvc", 9223372036854775808UL)]
    public void Generator_FoldsNativeShiftsWithIntegerCountUsingTargetWidth(string triple, ulong expected)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            const nuint HighBit = cast<nuint>(1) << (cast<int>(sizeof(nuint)) * 8 - 1);
            enum E : ulong { High = cast<ulong>(HighBit) }
            int Test(nuint value) { switch(value) { case cast<nuint>(1) << (cast<int>(sizeof(nuint)) * 8 - 1): return 42; default: return 0; } }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        Compilation bound = LlvmIrGenerator.BindForTarget(compilation, new LlvmTargetOptions(triple));
        Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
        Assert.Equal(expected, Assert.Single(Assert.Single(bound.SemanticModel.GlobalNamespace.Namespaces).Enums).Members[0].Value);
        new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions(triple));
    }

    [Fact]
    public void Generator_RejectsNativeShiftOutsideTargetWidth()
    {
        Compilation compilation = CreateCompilation("namespace Example; enum E : ulong { A = cast<ulong>(cast<nuint>(1) << 32) }");
        Assert.False(compilation.HasErrors);
        Compilation narrow = LlvmIrGenerator.BindForTarget(compilation, new LlvmTargetOptions("i686-pc-windows-msvc"));
        Compilation wide = LlvmIrGenerator.BindForTarget(compilation, new LlvmTargetOptions("x86_64-pc-windows-msvc"));
        Assert.True(narrow.HasErrors);
        Assert.False(wide.HasErrors, string.Join(Environment.NewLine, wide.Diagnostics));
    }

    [Theory]
    [InlineData("i686-pc-windows-msvc")]
    [InlineData("x86_64-pc-windows-msvc")]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("aarch64-unknown-linux-gnu")]
    public void Generator_VerifiesScopedScalarAndArrayCleanupForTarget(string triple)
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Item { public nint Id; public ~Item() { Id = cast<nint>(0); } }
            int Test(int n)
            {
                Item first = Item();
                Item deferred;
                Item[,] outer = Item[n,2];
                for (int i = 0; i < n; i++)
                {
                    deferred = Item();
                    Item local = Item();
                    Item[,,] inner = Item[1,2,3];
                    switch (i) { case 0: continue; case 1: break; default: return inner.Length; }
                    if (n == 2) break;
                }
                return outer.GetLength(1);
            }
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, new LlvmTargetOptions(triple));
        Assert.Contains("call ptr @llvm.stacksave.p0", ir, StringComparison.Ordinal);
        Assert.Contains("call void @llvm.stackrestore.p0", ir, StringComparison.Ordinal);
        Assert.Contains("stack.destroy.element", ir, StringComparison.Ordinal);
        Assert.Contains("local.cleanup.node", ir, StringComparison.Ordinal);
        Assert.Contains("local.constructed", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("call void @free", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("call ptr @malloc", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("call ptr @calloc", ir, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("i686-pc-windows-msvc", 32, 32)]
    [InlineData("x86_64-pc-windows-msvc", 64, 32)]
    [InlineData("x86_64-unknown-linux-gnu", 64, 64)]
    public void Generator_UsesCheckedFloatingCastBoundariesForEveryTargetWidth(string triple, int nativeWidth, int cLongWidth)
    {
        var target = new LlvmTargetOptions(triple);
        foreach (string type in new[] { "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint", "clong", "culong" })
        foreach (string sourceType in new[] { "float", "double" })
        {
            bool signed = type is "sbyte" or "short" or "int" or "long" or "nint" or "clong";
            int width = type switch { "sbyte" or "byte" => 8, "short" or "ushort" => 16, "int" or "uint" => 32,
                "nint" or "nuint" => nativeWidth, "clong" or "culong" => cLongWidth, _ => 64 };
            double upper = Math.ScaleB(1, signed ? width - 1 : width), lower = signed ? -upper : 0;
            double last = sourceType == "float" ? MathF.BitDecrement((float)upper) : Math.BitDecrement(upper);
            double below = sourceType == "float" ? MathF.BitDecrement((float)(lower - 1)) : Math.BitDecrement(lower - 1);
            string Literal(double number) => number.ToString("E17", System.Globalization.CultureInfo.InvariantCulture) + (sourceType == "float" ? "f" : "");
            foreach (double invalid in new[] { upper, below })
            {
                Compilation bad = CreateCompilation($"namespace Example; const {type} Bad = cast<{type}>({Literal(invalid)});");
                if (!bad.HasErrors) bad = LlvmIrGenerator.BindForTarget(bad, target);
                Assert.True(bad.HasErrors, $"{triple}: {sourceType} -> {type}, {invalid}");
            }
            string code = $$"""
                namespace Example;
                enum Values : {{(signed ? "long" : "ulong")}}
                {
                    Min = cast<{{(signed ? "long" : "ulong")}}>(cast<{{type}}>({{Literal(lower)}})),
                    Last = cast<{{(signed ? "long" : "ulong")}}>(cast<{{type}}>({{Literal(last)}}))
                }
                {{type}} Convert({{sourceType}} value) { return cast<{{type}}>(value); }
                """;
            Compilation compilation = LlvmIrGenerator.BindForTarget(CreateCompilation(code), target);
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            var members = Assert.Single(Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Enums).Members;
            Assert.Equal(new System.Numerics.BigInteger(lower), System.Numerics.BigInteger.Parse(members[0].Value!.ToString()!));
            Assert.Equal(new System.Numerics.BigInteger(last), System.Numerics.BigInteger.Parse(members[1].Value!.ToString()!));
            string ir = new LlvmIrGenerator().GenerateForTarget(compilation, target);
            Assert.Contains("cast.range.valid", ir, StringComparison.Ordinal);
            Assert.Contains("call void @llvm.trap()", ir, StringComparison.Ordinal);
            Assert.Contains(signed ? "fptosi" : "fptoui", ir, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Generator_DoesNotAddCleanupRuntimeForTrivialMove()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Pair { public int X; public int Y; }
            int Use()
            {
                Pair source = Pair { 1, 2 };
                Pair destination = move source;
                return destination.X + destination.Y;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().Generate(compilation, "trivial-move");
        Assert.DoesNotContain("local.cleanup.node", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("local.constructed", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("stack.cleanup", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_EmitsRecursiveDestructorGlueInReverseFieldOrder()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Leaf { public int Id; public ~Leaf() {} }
            struct Container { public Leaf First; public Leaf Second; }
            void Use() { Container value = Container(); }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol container = Assert.Single(Assert.Single(
            compilation.SemanticModel.GlobalNamespace.Namespaces).Structs.Where(type => type.Name == "Container"));
        Assert.Equal(FunctionKind.DestructorGlue, container.CompleteDestructor!.FunctionKind);
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "destructor-glue");
        Assert.Contains(ManagedSymbol("destructor-glue", container.CompleteDestructor.FullName, "function"), ir, StringComparison.Ordinal);
        int second = ir.IndexOf("Second.destructor.address", StringComparison.Ordinal);
        int first = ir.IndexOf("First.destructor.address", StringComparison.Ordinal);
        Assert.True(second >= 0 && first > second, "generated destructor glue must destroy fields in reverse declaration order");
        Assert.Contains("local.cleanup.node", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_LowersCompositeCopiesFieldByField()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Pair { public int Left; public int Right; }
            struct Container { public Pair Pair; public int Tail; }
            int Use()
            {
                Container source = Container { Pair { 1, 2 }, 3 };
                Container destination = source;
                destination.Pair.Left = 10;
                return source.Pair.Left + destination.Pair.Left + destination.Tail;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().Generate(compilation, "copy-glue");
        Assert.Contains("copy.Pair", ir, StringComparison.Ordinal);
        Assert.Contains("copy.Left", ir, StringComparison.Ordinal);
        Assert.Contains("copy.Right", ir, StringComparison.Ordinal);
        Assert.Contains("copy.Tail", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_OwnershipCopiesRetainAndMovesOnlyTransferHandles()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Resource {}
            shared<Resource> CopyShared(shared<Resource> value)
            {
                shared<Resource> result = value;
                return move result;
            }
            shared<Resource> MoveShared(shared<Resource> value)
            {
                shared<Resource> result = move value;
                return move result;
            }
            weak<Resource> CopyWeak(weak<Resource> value)
            {
                weak<Resource> result = value;
                return move result;
            }
            weak<Resource> MoveWeak(weak<Resource> value)
            {
                weak<Resource> result = move value;
                return move result;
            }
            unique<Resource> MoveUnique(unique<Resource> value)
            {
                unique<Resource> result = move value;
                return move result;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "ownership-costs");
        string Body(string name)
        {
            string symbol = ManagedSymbol("ownership-costs", $"Example.{name}", "function");
            int start = ir.IndexOf($"@{symbol}(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"missing function {name}");
            int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
            Assert.True(end > start, $"unterminated function {name}");
            return ir[start..end];
        }

        Assert.Contains("shared.retain.body", Body("CopyShared"), StringComparison.Ordinal);
        Assert.DoesNotContain("shared.retain.body", Body("MoveShared"), StringComparison.Ordinal);
        Assert.Contains("weak.retain.body", Body("CopyWeak"), StringComparison.Ordinal);
        Assert.DoesNotContain("weak.retain.body", Body("MoveWeak"), StringComparison.Ordinal);
        Assert.DoesNotContain("copy.", Body("MoveUnique"), StringComparison.Ordinal);
        Assert.Contains("store i1 false", Body("MoveShared"), StringComparison.Ordinal);
        Assert.Contains("store i1 false", Body("MoveWeak"), StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_ConditionallyMovedReassignmentUsesExistingCleanupActivityFlag()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Resource {}
            void Consume(shared<Resource> value) {}
            void Maybe(bool take)
            {
                shared<Resource> value = new Resource();
                if (take) Consume(move value);
                value = new Resource();
            }
            void Definite(bool take)
            {
                shared<Resource> value = new Resource();
                if (take) Consume(move value);
                else Consume(move value);
                value = new Resource();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "conditional-move-reinitialization");
        string Body(string name)
        {
            string symbol = ManagedSymbol("conditional-move-reinitialization", $"Example.{name}", "function");
            int start = ir.IndexOf($"@{symbol}(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"missing function {name}");
            int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
            Assert.True(end > start, $"unterminated function {name}");
            return ir[start..end];
        }

        Assert.Contains("local.destructor.active", Body("Maybe"), StringComparison.Ordinal);
        Assert.Contains("local.destroy", Body("Maybe"), StringComparison.Ordinal);
        Assert.DoesNotContain("local.destructor.active", Body("Definite"), StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_UnifiedBorrowIdentityAddsNoRuntimeBorrowRepresentation()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Pair { public int First; public int Second; }
            int UsePointees(unique<Pair> uniqueValue, shared<Pair> sharedValue)
            {
                readonly int& uniqueReference = uniqueValue->First;
                int first = uniqueReference;
                readonly int& sharedReference = sharedValue->Second;
                return first + sharedReference;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "borrow-layout");

        Assert.DoesNotContain("borrow_count", ir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("borrow_registry", ir, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("borrow.table", ir, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Generator_UsesDirectCleanupForScalarAndTransparentAggregateOwnership()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Resource { public int Value; public Resource(int value) { Value = value; } }
            struct Wrapper { public shared<Resource> Value; public weak<Resource> Observer; }
            struct Outer { public Wrapper Nested; }
            shared<Resource> Forward(shared<Resource> value) { return move value; }
            int Use(shared<Resource> root, weak<Resource> observer)
            {
                shared<Resource> copy = Forward(root);
                shared<Resource> locked = lock observer;
                if (locked == null) return copy->Value;
                return copy->Value + locked->Value;
            }
            int UseAggregate(shared<Resource> root, weak<Resource> observer)
            {
                Wrapper source = Wrapper { root, observer };
                Wrapper destination = move source;
                shared<Resource> extracted = move destination.Value;
                destination.Value = root;
                return extracted->Value + destination.Value->Value;
            }
            int UseNestedAggregate(shared<Resource> root, weak<Resource> observer)
            {
                Outer value = Outer { Wrapper { root, observer } };
                return value.Nested.Value->Value;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        const string module = "direct-ownership-cleanup";
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), module);
        string Body(string fullName)
        {
            string symbol = ManagedSymbol(module, fullName, "function");
            int start = ir.IndexOf($"@{symbol}(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"missing function {fullName}");
            int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
            Assert.True(end > start, $"unterminated function {fullName}");
            return ir[start..end];
        }

        string use = Body("Example.Use");
        Assert.Contains("ownership.cleanup.active", use, StringComparison.Ordinal);
        Assert.Contains("ownership.cleanup.destroy", use, StringComparison.Ordinal);
        Assert.DoesNotContain("local.cleanup.node", use, StringComparison.Ordinal);
        Assert.DoesNotContain("stack.cleanup.field", use, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.stacksave", use, StringComparison.Ordinal);

        string forward = Body("Example.Forward");
        Assert.Contains("store i1 false", forward, StringComparison.Ordinal);
        Assert.DoesNotContain("local.cleanup.node", forward, StringComparison.Ordinal);
        Assert.DoesNotContain("stack.cleanup.field", forward, StringComparison.Ordinal);

        string aggregate = Body("Example.UseAggregate");
        Assert.Contains("ownership.cleanup.active", aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain("local.cleanup.node", aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain("stack.cleanup.field", aggregate, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.stacksave", aggregate, StringComparison.Ordinal);

        string nested = Body("Example.UseNestedAggregate");
        Assert.Contains("ownership.cleanup.active", nested, StringComparison.Ordinal);
        Assert.DoesNotContain("local.cleanup.node", nested, StringComparison.Ordinal);
        Assert.DoesNotContain("stack.cleanup.field", nested, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RetainsGenericCleanupFallbackForDeferredNonTransparentAndStackOwnership()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Resource {}
            struct Tracked { public ~Tracked() {} }
            struct CustomWrapper
            {
                public shared<Resource> Value;
                public ~CustomWrapper() {}
            }
            void Deferred(bool initialize)
            {
                shared<Resource> value;
                if (initialize) value = new Resource();
            }
            void CustomAggregate(shared<Resource> value)
            {
                CustomWrapper wrapper = CustomWrapper { value };
            }
            void MixedStackArray(int count)
            {
                unique<Resource> value = new Resource();
                int[] scratch = int[count];
                scratch[0] = 1;
            }
            void Pinned()
            {
                pin<Tracked> value = Tracked();
            }
            void Stored()
            {
                storage<Tracked> value;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        const string module = "generic-ownership-cleanup";
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), module);
        string Body(string fullName)
        {
            string symbol = ManagedSymbol(module, fullName, "function");
            int start = ir.IndexOf($"@{symbol}(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"missing function {fullName}");
            int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
            Assert.True(end > start, $"unterminated function {fullName}");
            return ir[start..end];
        }

        Assert.Contains("local.cleanup.node", Body("Example.Deferred"), StringComparison.Ordinal);
        Assert.Contains("stack.cleanup.field", Body("Example.Deferred"), StringComparison.Ordinal);
        Assert.Contains("local.cleanup.node", Body("Example.CustomAggregate"), StringComparison.Ordinal);
        Assert.Contains("stack.cleanup.field", Body("Example.CustomAggregate"), StringComparison.Ordinal);
        Assert.Contains("local.cleanup.node", Body("Example.MixedStackArray"), StringComparison.Ordinal);
        Assert.Contains("stack.cleanup.field", Body("Example.MixedStackArray"), StringComparison.Ordinal);
        Assert.Contains("llvm.stacksave", Body("Example.MixedStackArray"), StringComparison.Ordinal);
        Assert.Contains("local.cleanup.node", Body("Example.Pinned"), StringComparison.Ordinal);
        Assert.Contains("stack.cleanup.field", Body("Example.Pinned"), StringComparison.Ordinal);
        Assert.Contains("local.cleanup.node", Body("Example.Stored"), StringComparison.Ordinal);
        Assert.Contains("stack.cleanup.field", Body("Example.Stored"), StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_RetainsGenericCleanupFallbackForOversizedTransparentAggregate()
    {
        string fields = string.Join(Environment.NewLine,
            Enumerable.Range(0, 17).Select(index => $"public shared<Resource> Value{index};"));
        string values = string.Join(", ", Enumerable.Repeat("value", 17));
        Compilation compilation = CreateCompilation($$"""
            namespace Example;
            struct Resource {}
            struct LargeWrapper
            {
                {{fields}}
            }
            void Use(shared<Resource> value)
            {
                LargeWrapper wrapper = LargeWrapper { {{values}} };
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        const string module = "oversized-ownership-cleanup";
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), module);
        string symbol = ManagedSymbol(module, "Example.Use", "function");
        int start = ir.IndexOf($"@{symbol}(", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string body = ir[start..end];

        Assert.Contains("local.cleanup.node", body, StringComparison.Ordinal);
        Assert.Contains("stack.cleanup.field", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_LowersReceiverEffectsAndRawArrayMovesWithoutSourceZeroingOrHeapCopies()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;
            struct Resource {}
            struct Holder
            {
                public unique<Resource> Value;
                public unique<Resource> Take() { return move Value; }
                public unique<Resource> Replace()
                {
                    unique<Resource> old = move Value;
                    Value = new Resource();
                    return move old;
                }
            }
            int[] MoveArray(int[] value) { return move value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        const string module = "move-effects";
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), module);
        string Body(string fullName)
        {
            string symbol = ManagedSymbol(module, fullName, "function");
            int start = ir.IndexOf($"@{symbol}(", StringComparison.Ordinal);
            Assert.True(start >= 0, $"missing function {fullName}");
            int end = ir.IndexOf("\n}", start, StringComparison.Ordinal);
            Assert.True(end > start, $"unterminated function {fullName}");
            return ir[start..end];
        }

        string take = Body("Example.Holder.Take");
        Assert.DoesNotContain("store ptr null", take, StringComparison.Ordinal);
        Assert.DoesNotContain("zeroinitializer", take, StringComparison.Ordinal);

        StructTypeSymbol holder = Assert.Single(Assert.Single(
            compilation.SemanticModel.GlobalNamespace.Namespaces).Structs.Where(type => type.Name == "Holder"));
        var ownership = Assert.IsType<UniqueTypeSymbol>(Assert.Single(holder.Fields).Type);
        string ownershipDestructor = ManagedSymbol(module, ownership.CompleteDestructor!.FullName, "function");
        string replace = Body("Example.Holder.Replace");
        Assert.Contains("store i1 false", replace, StringComparison.Ordinal);
        Assert.Contains("ownership.cleanup.active", replace, StringComparison.Ordinal);
        Assert.Contains($"call void @{ownershipDestructor}", replace, StringComparison.Ordinal);

        string arrayMove = Body("Example.MoveArray");
        Assert.DoesNotContain("@malloc", arrayMove, StringComparison.Ordinal);
        Assert.DoesNotContain("@calloc", arrayMove, StringComparison.Ordinal);
        Assert.DoesNotContain("llvm.memcpy", arrayMove, StringComparison.Ordinal);
    }

    private static Compilation CreateCompilation(string source) =>
        Compilation.Create(SourceText.From(source, "test.xe"));

    private static Compilation AsExecutable(Compilation compilation) =>
        compilation.WithOptions(new CompilationOptions(CompilationOutputKind.Executable));

    private static string ManagedSymbol(string abiIdentity, string sourceIdentity, string category) =>
        $"__xenon_{category}_{Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(abiIdentity))}_" +
        Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(sourceIdentity));

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "xenon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
