using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.CodeGen;

public sealed class LlvmIrGeneratorTests
{
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

        Assert.Contains("define internal i32 @Example.Main()", llvmIr, StringComparison.Ordinal);
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
        Assert.Contains("define internal i32 @Example.Add(i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @Example_Multiply(i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("add i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("mul i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @Example.Add", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @puts", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("define internal i32 @Example.Hidden()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @Example.Visible()", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("define internal i32 @Example.Visible()", llvmIr, StringComparison.Ordinal);
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

                ~Box()
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

        Assert.Contains("@Example.Box.__ctor", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@Example.Box.__dtor", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @Example.Box.__dtor", llvmIr, StringComparison.Ordinal);
        Assert.Contains("stack.array = alloca i32", llvmIr, StringComparison.Ordinal);
        Assert.Contains("getelementptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call ptr @malloc", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("define void @Example.Counter.Add(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @Example.Counter.Read(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @Example.Counter.Add(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @Example.Counter.Read(ptr", llvmIr, StringComparison.Ordinal);
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
        Assert.Contains("call void @Example.Consume(ptr null)", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("@Example.Entity.Count = global i32 1024", llvmIr, StringComparison.Ordinal);
        Assert.Contains("load i32, ptr @Example.Entity.Count", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("store i32 41, ptr @Example.State.Value", llvmIr, StringComparison.Ordinal);
        Assert.Contains("load i32, ptr @Example.State.Value", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("%Example.Enemy = type { i32, i32 }", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @Example.Entity.__ctor", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("@Example.Enemy.__vtable", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@Example.Enemy.Score", llvmIr, StringComparison.Ordinal);
        Assert.Contains("virtual.slot", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_DefinesAbstractVTableSlotsWithUnreachableStubs()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity
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

        Assert.Contains("define internal i32 @Example.Entity.Score(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("unreachable", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@Example.Enemy.__vtable", llvmIr, StringComparison.Ordinal);

        Compilation privateAbstract = CreateCompilation("""
            namespace Example;

            struct Entity
            {
                abstract void Update();
            }
            """);
        Assert.Empty(privateAbstract.Diagnostics);
        string privateIr = new LlvmIrGenerator().Generate(privateAbstract, "private-abstract-vtable");
        Assert.Contains("define internal void @Example.Entity.Update(ptr", privateIr, StringComparison.Ordinal);
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

        Assert.Contains("define internal i32 @Example.Read(ptr", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("define internal i32 @Example.Read(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@Example.Enemy.IScore.__itable", llvmIr, StringComparison.Ordinal);
        Assert.Contains("interface.slot", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("@Example.Test.Less = global i1 true", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@Example.Test.Divide = global i32 -1", llvmIr, StringComparison.Ordinal);
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
        Assert.Contains("@Example.Enemy.IScore.__itable", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("@Example.Value.IB.__itable = internal global [1 x ptr]", llvmIr, StringComparison.Ordinal);
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

        Assert.Contains("@Example.Derived.IValue.__itable = internal global [1 x ptr] [ptr @Example.Base.Get]", llvmIr, StringComparison.Ordinal);
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
                public ~Enemy() { }
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

        Assert.Contains("@Example.Enemy.__vtable", llvmIr, StringComparison.Ordinal);
        Assert.Contains("@Example.Enemy.__dtor", llvmIr, StringComparison.Ordinal);
        Assert.Contains("destructor.slot", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_CallsInheritedDestructorWhenDerivedDeclaresNone()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Entity { ~Entity() { } }
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

        Assert.Contains("call void @Example.Entity.__dtor", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_ChainsVirtualDestructorAcrossIntermediateTypeWithoutDestructor()
    {
        Compilation compilation = CreateCompilation("""
            namespace Example;

            struct Base { public virtual ~Base() { } }
            struct Middle : Base { }
            struct Derived : Middle { public ~Derived() { } }

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

        Assert.Contains("@Example.Derived.__vtable", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @Example.Base.__dtor", llvmIr, StringComparison.Ordinal);
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
            compilation,
            target,
            "targeted",
            generateExecutableEntryPoint: true);

        Assert.Contains($"target triple = \"{target.Triple}\"", llvmIr, StringComparison.Ordinal);
        Assert.Contains("target datalayout =", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @main()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @Example.Main()", llvmIr, StringComparison.Ordinal);
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
            compilation,
            target,
            "method-main",
            generateExecutableEntryPoint: true);

        Assert.Contains("define i32 @Example.Worker.Main(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("define i32 @main()", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call i32 @Example.Main()", llvmIr, StringComparison.Ordinal);
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
                compilation,
                outputPath,
                options,
                "object-test",
                generateExecutableEntryPoint: true);

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
                    compilation,
                    outputPath,
                    options,
                    "missing-main",
                    generateExecutableEntryPoint: true));

            Assert.Contains("int Main()", exception.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Compilation CreateCompilation(string source) =>
        Compilation.Create(SourceText.From(source, "test.xe"));

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "xenon-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
