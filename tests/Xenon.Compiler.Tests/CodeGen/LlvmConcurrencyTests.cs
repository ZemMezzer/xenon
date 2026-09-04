using System.Collections.Immutable;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.CodeGen;

public sealed class LlvmConcurrencyTests
{
    [Fact]
    public void FirstAssignmentInitializesLockBackedAtomicStorageWithoutReadingPreviousValue()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace AtomicInitialization;
            struct State { public int Value; }
            struct Resource {}
            void Composite(State replacement)
            {
                atomic<State> value;
                value = replacement;
            }
            void Ownership(shared<Resource> replacement)
            {
                atomic<shared<Resource>> value;
                value = replacement;
            }
            """, "atomic-initialization.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "atomic-initialization");
        Assert.DoesNotContain("atomic.replace.previous", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("atomic.store.lock.attempt", ir, StringComparison.Ordinal);
        Assert.Contains("store i8 0", ir, StringComparison.Ordinal);
        Assert.Contains("local.cleanup.register", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedConstructorAtomicFieldAssignmentInitializesLockOnceThenReplaces()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace ConstructorAtomicReplacement;
            struct Resource {}
            struct Holder
            {
                public atomic<shared<Resource>> Value;
                public Holder(shared<Resource> first, shared<Resource> second)
                {
                    Value = first;
                    Value = second;
                }
            }
            """, "constructor-atomic-replacement.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "constructor-atomic-replacement");
        string constructor = GetIrFunctionContaining(ir, "atomic.replace.previous");

        Assert.Equal(1, constructor.Split("store i8 0", StringSplitOptions.None).Length - 1);
        Assert.Contains("atomic.replace.previous = load", constructor, StringComparison.Ordinal);
        Assert.Contains("atomic.replace.lock.attempt", constructor, StringComparison.Ordinal);
        Assert.Contains("store atomic i8 0", constructor, StringComparison.Ordinal);
    }

    [Fact]
    public void MemoryModelLowering_DoesNotGiveOrdinaryOwnershipOrReadonlyAccessHiddenAtomicity()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace MemoryModel;

            struct Data { public int Value; }

            export int Ordinary(int* value)
            {
                int previous = *value;
                *value = previous + 1;
                return *value;
            }

            export int ReadonlyView(readonly int* value) { return *value; }

            export int AtomicValue()
            {
                atomic<int> value = 1;
                int previous = value;
                value = previous + 1;
                return value;
            }

            export int SharedPointee()
            {
                shared<Data> owner = new Data();
                owner->Value = 7;
                return owner->Value;
            }

            export int ArrayElements()
            {
                int[] values = new int[2];
                values[0] = 9;
                int result = values[0];
                free(values);
                return result;
            }
            """, "memory-model.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "memory-model");

        string ordinary = GetIrFunction(ir, "MemoryModel_Ordinary");
        Assert.Contains("load i32", ordinary, StringComparison.Ordinal);
        Assert.Contains("store i32", ordinary, StringComparison.Ordinal);
        Assert.DoesNotContain(" atomic ", ordinary, StringComparison.Ordinal);

        string readonlyView = GetIrFunction(ir, "MemoryModel_ReadonlyView");
        Assert.Contains("load i32", readonlyView, StringComparison.Ordinal);
        Assert.DoesNotContain(" atomic ", readonlyView, StringComparison.Ordinal);

        string atomic = GetIrFunction(ir, "MemoryModel_AtomicValue");
        Assert.Contains("load atomic i32", atomic, StringComparison.Ordinal);
        Assert.Contains("store atomic i32", atomic, StringComparison.Ordinal);

        string shared = GetIrFunction(ir, "MemoryModel_SharedPointee");
        Assert.Contains("load i32", shared, StringComparison.Ordinal);
        Assert.Contains("store i32", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("load atomic i32", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("store atomic i32", shared, StringComparison.Ordinal);

        string array = GetIrFunction(ir, "MemoryModel_ArrayElements");
        Assert.Contains("load i32", array, StringComparison.Ordinal);
        Assert.Contains("store i32", array, StringComparison.Ordinal);
        Assert.DoesNotContain(" atomic ", array, StringComparison.Ordinal);
    }

    [Fact]
    public void ThreadLocalLowering_UsesNativeTlsAndLazyPerThreadInitializer()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace ThreadLocals;

            int Initialize() { return 41; }
            struct State
            {
                public static threadlocal int Value = Initialize();
                public static threadlocal int[] Buffer = new int[4];
            }

            int Read() { return State.Value; }
            void Write(int value) { State.Value = value; }
            int[] ReadBuffer() { return State.Buffer; }
            """, "thread-local.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.False(LlvmIrGenerator.RequiresNativeThreadingRuntime(compilation));
        FieldSymbol field = Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Structs)
            .StaticFields.Single(field => field.Name == "Value");
        Assert.True(field.IsThreadLocal);
        Assert.Equal("public static threadlocal int Value",
            field.ToDisplayString(SymbolDisplayFormat.Declaration));
        Assert.Contains(compilation.SemanticModel.Functions,
            function => function.Symbol.FunctionKind == FunctionKind.ThreadLocalInitializer);

        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "thread-local");

        Assert.Contains("thread_local", ir, StringComparison.Ordinal);
        Assert.Contains("threadlocal_ensure", ir, StringComparison.Ordinal);
        Assert.Contains("threadlocal_guard", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("_tlregdtor", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("FlsAlloc", ir, StringComparison.Ordinal);
        ImmutableArray<LlvmNativeExport> exports = LlvmIrGenerator.GetProjectNativeExports(
            compilation, "thread-local");
        Assert.Contains(exports, export => export.IsData &&
            export.Name.Contains("static_field", StringComparison.Ordinal));
        Assert.Contains(exports, export => !export.IsData &&
            export.Name.Contains("threadlocal_ensure", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("x86_64-unknown-linux-gnu")]
    [InlineData("x86_64-apple-darwin")]
    public void UnixThreadLocalCleanup_UsesLazyPthreadKeyWithoutCxxRuntime(string triple)
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace ThreadLocalCleanup;

            struct Resource
            {
                public int Value;
                public Resource(int value) { Value = value; }
                public ~Resource() { Value = 0; }
            }

            struct State
            {
                private static threadlocal unique<Resource> Current = new Resource(42);
                public static int Read() { return State.Current->Value; }
            }
            """, "thread-local-cleanup.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        Assert.True(LlvmIrGenerator.RequiresNativeThreadingRuntime(compilation));

        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            new LlvmTargetOptions(triple, PositionIndependentCode: true),
            "thread-local-cleanup");

        Assert.Contains("pthread_key_create", ir, StringComparison.Ordinal);
        Assert.Contains("pthread_key_delete", ir, StringComparison.Ordinal);
        Assert.Contains("pthread_getspecific", ir, StringComparison.Ordinal);
        Assert.Contains("pthread_setspecific", ir, StringComparison.Ordinal);
        Assert.Contains("threadlocal_pthread_cleanup", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("__cxa_thread_atexit", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("__dso_handle", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void AtomicArrayLowering_UsesRealElementLayoutAndPointerAtomicHandleOperations()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace AtomicArrays;

            struct State
            {
                public int Left = 7;
                public int Right = 7;
            }

            atomic<State>[] CreateStates(int count) { return new atomic<State>[count]; }
            State ReadState(atomic<State>[] values, int index) { return values[index]; }
            void WriteState(atomic<State>[] values, int index, State replacement)
            {
                values[index] = replacement;
            }
            int[] ReadHandle(atomic<int[]>& value) { return value; }
            void WriteHandle(atomic<int[]>& value, int[] replacement) { value = replacement; }
            int[] ExchangeHandle(atomic<int[]>& value, int[] replacement)
            {
                value <-> replacement;
                return replacement;
            }
            bool ReplaceHandle(atomic<int[]>& value, int[] expected, int[] desired)
            {
                return value : expected --> desired;
            }
            """, "atomic-arrays.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "atomic-arrays");

        Assert.Contains("array.initialize.element", ir, StringComparison.Ordinal);
        Assert.Contains("atomic.value.address", ir, StringComparison.Ordinal);
        Assert.Contains("load atomic ptr", ir, StringComparison.Ordinal);
        Assert.Contains("store atomic ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw xchg ptr", ir, StringComparison.Ordinal);
        Assert.Contains("cmpxchg ptr", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void AtomicOwnershipLowering_RetainsUnderWrapperLockAndReleasesAfterUnlock()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace AtomicOwnership;

            struct Resource { public int Value; }
            struct State { public shared<Resource> Owner; public int Generation; }

            shared<Resource> ReadShared(atomic<shared<Resource>>& value) { return value; }
            weak<Resource> ReadWeak(atomic<weak<Resource>>& value) { return value; }
            State ReadState(atomic<State>& value) { return value; }
            void WriteShared(atomic<shared<Resource>>& value, shared<Resource> replacement)
            {
                value = replacement;
            }
            bool ReplaceWeak(
                atomic<weak<Resource>>& value,
                weak<Resource> expected,
                weak<Resource> desired)
            {
                return value : expected --> desired;
            }
            bool ReplaceState(atomic<State>& value, State expected, State desired)
            {
                return value : expected --> desired;
            }
            """, "atomic-ownership.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "atomic-ownership");

        Assert.Contains("%__xenon.atomic.", ir, StringComparison.Ordinal);
        Assert.Contains("shared.retain.count", ir, StringComparison.Ordinal);
        Assert.Contains("weak.retain.count", ir, StringComparison.Ordinal);
        Assert.Contains("atomic.replace.previous", ir, StringComparison.Ordinal);
        Assert.Contains("store atomic i8 0", ir, StringComparison.Ordinal);
        Assert.Contains(" release", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeAtomicLowering_UsesPerObjectLockAndRealWrapperLayout()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace CompositeAtomics;

            struct State
            {
                public long First;
                public long Second;
                public int Generation;
            }

            State Read(atomic<State>& value) { return value; }
            void Write(atomic<State>& value, State replacement) { value = replacement; }
            State Exchange(atomic<State>& value, State replacement)
            {
                value <-> replacement;
                return replacement;
            }
            bool Replace(atomic<State>& value, State expected, State desired)
            {
                return value : expected --> desired;
            }
            nuint AtomicSize() { return sizeof(atomic<State>); }
            nuint ValueSize() { return sizeof(State); }
            """, "composite-atomics.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "composite-atomics");

        Assert.Contains("%__xenon.atomic.", ir, StringComparison.Ordinal);
        Assert.Contains("cmpxchg ptr", ir, StringComparison.Ordinal);
        Assert.Contains("i8 0, i8 1 acquire monotonic", ir, StringComparison.Ordinal);
        Assert.Contains("store atomic i8 0", ir, StringComparison.Ordinal);
        Assert.Contains(" release", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("load atomic %CompositeAtomics.State", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void AtomicPointerLowering_UsesPointerSizedAtomicInstructions()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace AtomicPointers;
            struct Resource { public int Value; }

            Resource* Local()
            {
                atomic<Resource*> value = null;
                return value;
            }
            nuint AtomicPointerSize() { return sizeof(atomic<Resource*>); }
            nuint AtomicPointerAlignment() { return alignof(atomic<Resource*>); }
            Resource* Read(atomic<Resource*>& value) { return value; }
            void Write(atomic<Resource*>& value, Resource* replacement) { value = replacement; }
            Resource* Exchange(atomic<Resource*>& value, Resource* replacement)
            {
                value <-> replacement;
                return replacement;
            }
            bool Replace(atomic<Resource*>& value, Resource* expected, Resource* desired)
            {
                return value : expected --> desired;
            }
            bool Clear(atomic<Resource*>& value, Resource* expected)
            {
                return value : expected --> null;
            }
            """, "atomic-pointers.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "atomic-pointers");

        Assert.Contains("load atomic ptr", ir, StringComparison.Ordinal);
        Assert.Contains("store atomic ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw xchg ptr", ir, StringComparison.Ordinal);
        Assert.Equal(2, ir.Split("cmpxchg ptr", StringSplitOptions.None).Length - 1);
        Assert.Contains("seq_cst", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareExchangeLowering_UsesStrongSequentiallyConsistentCmpXchgAndFloatValueEquality()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace CompareExchange;
            enum Mode { Waiting, Running }
            bool Int(atomic<int>& value, int expected, int desired) { return value : expected --> desired; }
            bool Bool(atomic<bool>& value, bool expected, bool desired) { return value : expected --> desired; }
            bool Float(atomic<float>& value, float expected, float desired) { return value : expected --> desired; }
            bool Enum(atomic<Mode>& value) { return value : Mode.Waiting --> Mode.Running; }
            """, "compare-exchange.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "compare-exchange");

        Assert.Equal(4, ir.Split("cmpxchg ptr", StringSplitOptions.None).Length - 1);
        Assert.Contains("cmpxchg ptr", ir, StringComparison.Ordinal);
        Assert.Contains("seq_cst seq_cst", ir, StringComparison.Ordinal);
        Assert.Contains("extractvalue", ir, StringComparison.Ordinal);
        Assert.Contains("cmpxchg.expected.bool.storage", ir, StringComparison.Ordinal);
        Assert.Contains("value.equal.float = fcmp oeq", ir, StringComparison.Ordinal);
        Assert.Contains("cmpxchg.float.current.bits", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("cmpxchg weak", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void StructEqualityAndCompositeCompareExchangeShareRecursiveFieldLowering()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace StructEquality;
            struct Inner
            {
                public int Number;
                public float Floating;
                public int* Pointer;
                public int[] Array;
            }
            struct State { public Inner Inner; public bool Enabled; }

            bool Equal(State left, State right) { return left == right; }
            bool Different(State left, State right) { return left != right; }
            bool Replace(atomic<State>& value, State expected, State desired)
            {
                return value : expected --> desired;
            }
            """, "struct-equality.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "struct-equality");

        Assert.True(ir.Split("value.equal.left.Inner", StringSplitOptions.None).Length - 1 >= 3);
        Assert.True(ir.Split("value.equal.float = fcmp oeq", StringSplitOptions.None).Length - 1 >= 3);
        Assert.True(ir.Split("value.equal.scalar = icmp eq", StringSplitOptions.None).Length - 1 >= 3);
        Assert.Contains("value.not.equal", ir, StringComparison.Ordinal);
        Assert.DoesNotContain("memcmp", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void SwapLowering_UsesAtomicExchangeForExactlyOneAtomicOperand()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Swap;

            void Ordinary(int& left, int& right) { left <-> right; }
            void AtomicLeft(atomic<int>& current, int& replacement) { current <-> replacement; }
            void AtomicRight(int& replacement, atomic<int>& current) { replacement <-> current; }
            void AtomicBool(atomic<bool>& current, bool& replacement) { current <-> replacement; }
            """, "swap.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "swap");

        Assert.Equal(3, ir.Split("atomicrmw xchg ptr", StringSplitOptions.None).Length - 1);
        Assert.Contains("atomicrmw xchg ptr", ir, StringComparison.Ordinal);
        Assert.Contains("i8", ir, StringComparison.Ordinal);
        Assert.Contains("seq_cst", ir, StringComparison.Ordinal);
        Assert.Contains("swap.left", ir, StringComparison.Ordinal);
        Assert.Contains("swap.right", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void PrimitiveAtomicLowering_UsesSequentiallyConsistentLoadsStoresAndRmw()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Atomics;

            struct State { public static atomic<int> Value = 7; }
            enum Mode { Off, On }

            int Exercise(atomic<int>& value)
            {
                int initial = value;
                value = initial + 1;
                value++;
                --value;
                value += 5;
                value -= 2;
                value |= 8;
                value &= 15;
                value ^= 3;
                return value;
            }

            float ExerciseFloat(atomic<float>& value)
            {
                value += 1.0f;
                return value--;
            }

            bool ExerciseBool(atomic<bool>& value)
            {
                bool previous = value;
                value = !previous;
                return value;
            }

            Mode ExerciseEnum(atomic<Mode>& value)
            {
                Mode previous = value;
                value = Mode.On;
                return previous;
            }
            """, "primitive-atomics.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "primitive-atomics");

        Assert.Contains("load atomic i32", ir, StringComparison.Ordinal);
        Assert.Contains("store atomic i32", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw add ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw sub ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw or ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw and ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw xor ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw fadd ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw fsub ptr", ir, StringComparison.Ordinal);
        Assert.Contains("seq_cst", ir, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParallelTargetLayouts_DoNotUseGlobalContext()
    {
        (LlvmTargetOptions Options, int PointerBytes, int CLongBytes)[] targets =
        [
            (LlvmTargetOptions.CreateHost(), IntPtr.Size, OperatingSystem.IsWindows() ? 4 : IntPtr.Size),
            (new LlvmTargetOptions("i686-pc-windows-msvc"), 4, 4),
            (new LlvmTargetOptions("x86_64-pc-windows-msvc"), 8, 4),
            (new LlvmTargetOptions("x86_64-unknown-linux-gnu"), 8, 8),
            (new LlvmTargetOptions("aarch64-unknown-linux-gnu"), 8, 8),
        ];

        Task[] workers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            Compilation compilation = CreateLayoutCompilation();
            for (int iteration = 0; iteration < 32; iteration++)
            {
                var target = targets[(worker + iteration) % targets.Length];
                Compilation bound = LlvmIrGenerator.BindForTarget(
                    compilation,
                    target.Options);

                Assert.False(bound.HasErrors, string.Join(Environment.NewLine, bound.Diagnostics));
                var layout = Assert.Single(
                    Assert.Single(bound.SemanticModel.GlobalNamespace.Namespaces).Enums);
                Assert.Equal(
                    [target.PointerBytes, target.PointerBytes, target.CLongBytes,
                        1, 1, 2, 2, 4, 4, 8, 8, 4, 4, 8, 8],
                    layout.Members.Select(member => (int)member.Value!).ToArray());
            }
        })).ToArray();

        await Task.WhenAll(workers);
    }

    [Fact]
    public void GeneratorReuse_SequentialSecondInvocationIsRejected()
    {
        Compilation compilation = CreateGenerationCompilation(0);
        var generator = new LlvmIrGenerator();

        generator.Generate(compilation, "first");
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => generator.Generate(compilation, "second"));
        Assert.Throws<InvalidOperationException>(() => generator.GenerateForTarget(
            compilation,
            new LlvmTargetOptions(string.Empty),
            "third"));

        Assert.Contains("single-use", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratorReuse_ConcurrentInvocationHasOneWinner()
    {
        Compilation compilation = CreateGenerationCompilation(0);
        var generator = new LlvmIrGenerator();
        using var gate = new ManualResetEventSlim();
        Task<Exception?>[] attempts = Enumerable.Range(0, 2).Select(index => Task.Run<Exception?>(() =>
        {
            gate.Wait();
            Exception? error = Record.Exception(
                () => generator.Generate(compilation, $"attempt_{index}"));
            return error;
        })).ToArray();

        gate.Set();
        Exception?[] errors = await Task.WhenAll(attempts);

        Assert.Equal(1, errors.Count(error => error is null));
        InvalidOperationException rejection = Assert.IsType<InvalidOperationException>(
            Assert.Single(errors.Where(error => error is not null)));
        Assert.Contains("single-use", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnershipLowering_UsesAtomicCountersAndCasWeakUpgrade()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace OwnershipConcurrency;

            struct Resource { public int Value; }

            shared<Resource> CopyShared(shared<Resource> value) { return value; }
            weak<Resource> CopyWeak(weak<Resource> value) { return value; }
            shared<Resource> Upgrade(weak<Resource> value) { return lock value; }
            """, "ownership-concurrency.xe"));

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation,
            LlvmTargetOptions.CreateHost(),
            "ownership-concurrency");

        Assert.Contains("atomicrmw add ptr", ir, StringComparison.Ordinal);
        Assert.Contains("atomicrmw sub ptr", ir, StringComparison.Ordinal);
        Assert.Contains(" release", ir, StringComparison.Ordinal);
        Assert.Contains("cmpxchg ptr", ir, StringComparison.Ordinal);
        Assert.Contains(" acquire monotonic", ir, StringComparison.Ordinal);
        Assert.Contains("fence acquire", ir, StringComparison.Ordinal);
    }

    private static Compilation CreateLayoutCompilation() => Compilation.Create(SourceText.From("""
        namespace Example;
        enum Layout
        {
            Pointer = cast<int>(sizeof(nint)),
            PointerAlignment = cast<int>(alignof(nint)),
            CLong = cast<int>(sizeof(clong)),
            BoolSize = cast<int>(sizeof(bool)),
            BoolAlignment = cast<int>(alignof(bool)),
            Int16Size = cast<int>(sizeof(short)),
            Int16Alignment = cast<int>(alignof(short)),
            Int32Size = cast<int>(sizeof(int)),
            Int32Alignment = cast<int>(alignof(int)),
            Int64Size = cast<int>(sizeof(long)),
            Int64Alignment = cast<int>(alignof(long)),
            FloatSize = cast<int>(sizeof(float)),
            FloatAlignment = cast<int>(alignof(float)),
            DoubleSize = cast<int>(sizeof(double)),
            DoubleAlignment = cast<int>(alignof(double))
        }
        """, "layout-concurrency.xe"));

    private static string GetIrFunction(string ir, string nativeName)
    {
        int name = ir.IndexOf($"@{nativeName}(", StringComparison.Ordinal);
        Assert.True(name >= 0, $"LLVM function '{nativeName}' was not found.");
        int start = ir.LastIndexOf("define ", name, StringComparison.Ordinal);
        int end = ir.IndexOf("\n}", name, StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= 0, $"LLVM function '{nativeName}' has no complete body.");
        return ir[start..(end + 2)];
    }

    private static string GetIrFunctionContaining(string ir, string marker)
    {
        int markerPosition = ir.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(markerPosition >= 0, $"LLVM marker '{marker}' was not found.");
        int start = ir.LastIndexOf("define ", markerPosition, StringComparison.Ordinal);
        int end = ir.IndexOf("\n}", markerPosition, StringComparison.Ordinal);
        Assert.True(start >= 0 && end >= 0, $"LLVM function containing '{marker}' has no complete body.");
        return ir[start..(end + 2)];
    }

    private static Compilation CreateGenerationCompilation(int worker) => Compilation.Create(SourceText.From($$"""
        namespace Parallel{{worker}};
        int Value() { return {{worker}} + 42; }
        """, $"parallel-{worker}.xe"));
}
