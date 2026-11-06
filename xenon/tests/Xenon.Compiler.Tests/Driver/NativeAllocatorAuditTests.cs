using System.Runtime.InteropServices;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Text;
using Xenon.Driver;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.Driver;

public sealed partial class NativeAllocatorAuditTests
{
    static NativeAllocatorAuditTests()
    {
        if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002 | 0x8000);
    }
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    private const string Prelude = """
        struct Failure { public int code; }
        static struct Log {
            public static int next; public static int count; public static int mask;
            public static int order; public static int duplicate; public static int ordinary;
            public static int freed; public static int allocated; public static int bad;
            public static int mode; public static int fieldDrops; public static int wholeDrops;
            public static int releaseOrder;
        }
        allocator Heap {
            public void* block;
            public nuint requested;
            public nuint alignment;
            +Heap(nuint size, nuint align) {
                Log.allocated++;
                requested = size; alignment = align;
                block = malloc(size, align);
                return block;
            }
            -Heap(void* pointer) {
                if (pointer != block) Log.bad++;
                Log.freed++; Log.releaseOrder = Log.order;
                free(pointer);
                if (Log.mode == 4) throw Failure { 40 };
            }
        }
        """;

    public static IEnumerable<object[]> DropCases()
    {
        foreach (int optimization in new[] { 0, 1, 2, 3 })
        foreach (bool custom in new[] { false, true })
        foreach (int throwingElement in new[] { 0, 1, 2, 3 })
            yield return [optimization, custom, throwingElement];
    }

    [Theory, MemberData(nameof(DropCases))]
    public void ArrayDropResumesAfterFirstMiddleOrLastDestructor(int optimization, bool custom, int throwingElement)
    {
        string allocation = custom ? "new(heap)" : "new";
        AssertSuccess(Prelude + $$"""
            struct Element {
                public int id = ++Log.next;
                public Element() { Log.ordinary++; }
                public ~Element() {
                    int bit = 1 << id;
                    if ((Log.mask & bit) != 0) Log.duplicate++;
                    Log.mask |= bit; Log.count++; Log.order = Log.order * 10 + id;
                    if (id == Log.mode) throw Failure { id };
                }
            }
            int Main() {
                Heap* heap = new Heap();
                Log.mode = {{throwingElement}};
                Element[] values = {{allocation}} Element[3];
                int caught = 0;
                try { delete(values); } catch (readonly Failure& error) { caught = error.code; }
                if (caught != {{throwingElement}}) return 1;
                if (Log.next != 3 || Log.ordinary != 0 || Log.count != 3) return 2;
                if (Log.mask != 14 || Log.duplicate != 0 || Log.order != 321) return 3;
                if (Log.allocated != {{(custom ? 1 : 0)}} || Log.freed != {{(custom ? 1 : 0)}} || Log.bad != 0) return 4;
                if ({{(custom ? "true" : "false")}} && Log.releaseOrder != 321) return 5;
                delete(heap);
                return 0;
            }
            """, optimization);
    }

    public static IEnumerable<object[]> ShapeCases()
    {
        foreach (int optimization in new[] { 0, 1, 2, 3 })
        foreach (bool custom in new[] { false, true }) yield return [optimization, custom];
    }

    [Theory, MemberData(nameof(ShapeCases))]
    public void EmptyRectangularPointerAndInheritedArraysPreserveLifetimes(int optimization, bool custom)
    {
        string allocation = custom ? "new(heap)" : "new";
        AssertSuccess(Prelude + $$"""
            struct Base {
                public int id = ++Log.next;
                public virtual ~Base() { Log.order = Log.order * 10 + id; Log.count++; }
            }
            struct Child : Base {
                public override ~Child() { Log.order = Log.order * 10 + id + 5; Log.wholeDrops++; }
            }
            int Main() {
                Heap* heap = new Heap();
                Child[] empty = {{allocation}} Child[0]; delete(empty);
                if (Log.count != 0 || Log.next != 0) return 1;
                Child[,] matrix = {{allocation}} Child[1, 3]; delete(matrix);
                if (Log.order != 837261 || Log.count != 3 || Log.wholeDrops != 3) return 2;
                Child* pointed = new Child();
                Child*[] pointers = {{allocation}} Child*[2];
                pointers[0] = pointed; pointers[1] = pointed;
                delete(pointers);
                if (Log.count != 3 || Log.wholeDrops != 3) return 3;
                delete(pointed);
                if (Log.count != 4 || Log.wholeDrops != 4) return 4;
                if (Log.allocated != {{(custom ? 3 : 0)}} || Log.freed != {{(custom ? 3 : 0)}} || Log.bad != 0) return 5;
                delete(heap); return 0;
            }
            """, optimization);
    }

    [Theory, MemberData(nameof(ShapeCases))]
    public void ArrayFieldConstructorFailureRollsBackOnlyCompletedElements(int optimization, bool custom)
    {
        string allocation = custom ? "new(heap)" : "new";
        AssertSuccess(Prelude + $$"""
            struct Resource {
                public Resource() { Log.next++; if (Log.next == 3) throw Failure { 31 }; }
                public ~Resource() { Log.fieldDrops++; }
            }
            struct Element {
                public Resource resource = Resource();
                public ~Element() { Log.wholeDrops++; }
            }
            int Main() {
                Heap* heap = new Heap();
                try { Element[] values = {{allocation}} Element[4]; delete(values); return 1; }
                catch (readonly Failure& error) { if (error.code != 31) return 2; }
                if (Log.next != 3 || Log.fieldDrops != 2 || Log.wholeDrops != 2) return 3;
                if (Log.freed != {{(custom ? 1 : 0)}} || Log.bad != 0) return 4;
                delete(heap); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void AllocationConstructorAndSubsequentOrdinaryConstructorRollback(int optimization)
    {
        AssertSuccess(Prelude + """
            struct Resource {
                public ~Resource() { Log.fieldDrops++; }
            }
            struct Element {
                public Resource resource = Resource();
                public @Element(Heap* heap) { Log.next++; if (Log.next == 3) throw Failure { 32 }; }
                public Element() { Log.ordinary++; throw Failure { 33 }; }
                public ~Element() { Log.wholeDrops++; }
            }
            int Main() {
                Heap* heap = new Heap();
                try { Element[] values = new(heap) Element[4]; delete(values); return 1; }
                catch (readonly Failure& error) { if (error.code != 32) return 2; }
                if (Log.next != 3 || Log.ordinary != 0 || Log.fieldDrops != 2 || Log.wholeDrops != 2 || Log.freed != 1) return 3;
                try { Element* value = new(heap) Element(); delete(value); return 4; }
                catch (readonly Failure& error) { if (error.code != 33) return 5; }
                if (Log.next != 4 || Log.ordinary != 1 || Log.fieldDrops != 3 || Log.wholeDrops != 2 || Log.freed != 2 || Log.bad != 0) return 6;
                delete(heap); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DeallocationFailureIsPropagatedOnceAfterAllElements(int optimization)
    {
        AssertSuccess(Prelude + """
            struct Element { public ~Element() { Log.count++; } }
            int Main() {
                Heap* heap = new Heap(); Log.mode = 4;
                Element[] values = new(heap) Element[3];
                try { delete(values); return 1; }
                catch (readonly Failure& error) { if (error.code != 40) return 2; }
                if (Log.count != 3 || Log.allocated != 1 || Log.freed != 1 || Log.bad != 0) return 3;
                delete(heap); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void StackAndOwnedArrayCleanupResumeWithoutRepeatingElements(int optimization)
    {
        AssertSuccess(Prelude + """
            struct Element {
                public int id = ++Log.next;
                public ~Element() { Log.count++; Log.order = Log.order * 10 + id; if (id == 2) throw Failure { 2 }; }
            }
            int Main() {
                try { Element[] values = Element[3]; }
                catch (readonly Failure& error) { if (error.code != 2) return 1; }
                if (Log.count != 3 || Log.order != 321) return 2;
                Log.next = 0; Log.count = 0; Log.order = 0;
                Heap* heap = new Heap();
                try { unique<Element[]> values = new(heap) Element[3]; }
                catch (readonly Failure& error) { if (error.code != 2) return 3; }
                if (Log.count != 3 || Log.order != 321 || Log.freed != 1 || Log.bad != 0) return 4;
                delete(heap); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(3, false)]
    [InlineData(0, true)] [InlineData(3, true)]
    public void SecondExceptionDuringArrayCleanupTerminates(int optimization, bool releaseThrows)
    {
        string destructor = releaseThrows
            ? """Log.count++; if (id == 1) { puts("first"); fflush(null); throw Failure { 1 }; }"""
            : """Log.count++; if (id >= 2) { if (id == 3) puts("first"); else puts("second"); fflush(null); throw Failure { id }; }""";
        string release = releaseThrows
            ? """puts("second"); fflush(null); free(p); throw Failure { 9 };"""
            : """puts("unexpected-release"); fflush(null); free(p);""";
        NativeProcessResult result = Run(Prelude + $$"""
            extern int puts(readonly byte* text);
            extern int fflush(void* stream);
            allocator Failing {
                +Failing(nuint s, nuint a) { return malloc(s,a); }
                -Failing(void* p) { {{release}} }
            }
            struct Element { public int id = ++Log.next; public ~Element() { {{destructor}} } }
            int Main() {
                Failing* heap = new Failing();
                Element[] values = new(heap) Element[3];
                try { delete(values); } catch (readonly Failure& error) { puts("unexpected-catch"); fflush(null); }
                return 0;
            }
            """, optimization);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("first\nsecond\n", result.Stdout.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData(0, "null")] [InlineData(3, "null")]
    [InlineData(0, "overflow")] [InlineData(3, "overflow")]
    [InlineData(0, "large")] [InlineData(3, "large")]
    public void FailedAndOverflowingAllocationsTrapAtTheExpectedPhase(int optimization, string scenario)
    {
        string expression = scenario == "overflow" ? "int[Limits.length, 2]" : scenario == "large" ? "byte[2147483647]" : "int()";
        NativeProcessResult result = Run($$"""
            extern int puts(readonly byte* text); extern int fflush(void* stream);
            static struct Limits { public static int length = 2147483647; }
            allocator Fail {
                +Fail(nuint size, nuint alignment) { puts("allocate"); fflush(null); return null; }
                -Fail(void* p) { puts("unexpected-release"); fflush(null); }
            }
            int Main() {
                Fail* heap = new Fail(); puts("start"); fflush(null);
                var value = new(heap) {{expression}};
                puts("unexpected-success"); fflush(null); return 0;
            }
            """, optimization);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(scenario == "overflow" ? "start\n" : "start\nallocate\n", result.Stdout.Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RawAllocationDoesNotConstructOrDestroyAndEmptyObjectsRelease(int optimization)
    {
        AssertSuccess(Prelude + """
            struct Resource { public int x; public Resource() { Log.ordinary++; } public ~Resource() { Log.count++; } }
            struct Empty {}
            int Main() {
                void* raw = malloc(sizeof(Resource)); free(raw);
                void* zero = calloc(1, sizeof(Resource)); free(zero);
                void* emptyRaw = malloc(0); free(emptyRaw);
                if (Log.ordinary != 0 || Log.count != 0) return 1;
                Resource* value = new Resource(); delete(value);
                if (Log.ordinary != 1 || Log.count != 1) return 2;
                Heap* heap = new Heap(); Empty* empty = new(heap) Empty(); delete(empty);
                if (heap->requested < sizeof(void*) * cast<nuint>(3) || Log.freed != 1 || Log.bad != 0) return 3;
                delete(heap); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void ThreeLevelAllocatorAndObjectDispatchPreservePhaseOrderAndRawAddress(int optimization)
    {
        AssertSuccess("""
            static struct Log { public static int order; public static int bad; }
            allocator Root {
                public void* raw;
                +Root(nuint s, nuint a) { Log.bad++; return malloc(s,a); }
                -Root(void* p) { Log.bad++; free(p); }
            }
            allocator Middle : Root {}
            allocator Leaf : Middle {
                +Leaf(nuint s, nuint a) { Log.order = Log.order * 10 + 1; raw = malloc(s,a); return raw; }
                -Leaf(void* p) { if (p != raw) Log.bad++; Log.order = Log.order * 10 + 9; free(p); }
            }
            struct Base {
                public @Base(Root* h) { Log.order = Log.order * 10 + 2; }
                public Base() { Log.order = Log.order * 10 + 4; }
                public virtual ~Base() { Log.order = Log.order * 10 + 8; }
            }
            struct Derived : Base {
                public @Derived(Root* h) { Log.order = Log.order * 10 + 3; }
                public Derived() : base() { Log.order = Log.order * 10 + 5; }
                public override ~Derived() { Log.order = Log.order * 10 + 7; }
            }
            int Main() {
                Leaf* leaf = new Leaf(); Root* provider = leaf;
                Base* value = new(provider) Derived(); Log.order = Log.order * 10 + 6;
                delete(value);
                if (Log.order != 123456789 || Log.bad != 0) return 1;
                delete(leaf); return 0;
            }
            """, optimization);
    }

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void SourceFreeGenericAllocatorLibraryExecutes(int optimization)
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace AllocatorLibrary;
            public allocator Heap {
                public int freed;
                +Heap(nuint s, nuint a) { return malloc(s,a); }
                -Heap(void* p) { freed++; free(p); }
            }
            public template Constructible { Constructible(); }
            public struct Value { public allocator Heap* owner; public @Value(Heap* h) { owner = h; } public Value() {} }
            public T* Make<T>(Heap* h) where T : Constructible { return new(h) T(); }
            """));
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("AllocatorLibrary")));
        Compilation consumer = Compilation.Create(new CompilationOptions(CompilationOutputKind.Executable), [reference], SourceText.From("""
            using AllocatorLibrary;
            namespace Consumer;
            int Main() { Heap* h=new Heap(); Value* v=Make<Value>(h); if(v->owner!=h)return 1; delete(v); if(h->freed!=1)return 2; delete(h); return 0; }
            """));
        Assert.Equal(0, Execute(consumer, optimization).ExitCode);
    }

    [Fact]
    public void XelibVersionsRemainFrozenBeforeOnePointZero()
    {
        Assert.Equal((ushort)1, XelibVersions.Container);
        Assert.Equal((ushort)1, XelibVersions.LibraryIr);
        Assert.Equal((ushort)1, XelibVersions.Language);
    }

    [Theory]
    [InlineData("void Use(Heap* h) { h.+Heap(1,1); }", "XE1001")]
    [InlineData("void Use(Heap* h) { h.-Heap(null); }", "XE1001")]
    [InlineData("void Use(readonly Heap* h) { int* p=new(h) int(); }", "XE0801")]
    [InlineData("allocator Other { +Other(nuint s,nuint a){return malloc(s,a);} -Other(void*p){free(p);} } struct Value { @Value(Other* h){} } void Use(Heap*h){ Value*v=new(h)Value(); }", "XE0803")]
    public void InvalidProviderAndDirectOperatorCallsAreRejected(string source, string diagnostic)
        => Assert.Contains(Compile(Prelude + source).Diagnostics, error => error.Id == diagnostic);

    [Theory]
    [InlineData(0)] [InlineData(3)]
    public void CustomAllocatedPayloadCrossesRealCAbiWithoutHeaderInItsLayout(int optimization)
    {
        Assert.Equal(0, Execute(Compile(Prelude + """
            struct Payload { public int value; public double weight; }
            extern int Inspect(Payload* value, nuint size, nuint alignment);
            int Main() {
                Heap* h=new Heap(); Payload* p=new(h) Payload { 42, 2.5 };
                if(Inspect(p,sizeof(Payload),alignof(Payload))!=0)return 1;
                if(p->value!=43 || p->weight!=5.0)return 2;
                delete(p); if(Log.freed!=1 || Log.bad!=0)return 3; delete(h); return 0;
            }
            """), optimization, """
            #include <stdint.h>
            #include <stddef.h>
            typedef struct Payload { int value; double weight; } Payload;
            int Inspect(Payload* p, size_t size, size_t alignment) {
                if(size!=sizeof(Payload) || alignment!=_Alignof(Payload))return 1;
                if((uintptr_t)p % _Alignof(Payload) || p->value!=42 || p->weight!=2.5)return 2;
                if((char*)&p->weight-(char*)p!=offsetof(Payload,weight))return 3;
                p->value++; p->weight*=2; return 0;
            }
            """).ExitCode);
    }

    public static IEnumerable<object[]> PartialInheritedCases()
    {
        foreach (int optimization in new[] { 0, 1, 2, 3 })
        foreach (string shape in new[] { "array", "scalar", "stack-array", "stack-value" })
        foreach (bool custom in shape.StartsWith("stack") ? new[] { false } : new[] { false, true })
            yield return [optimization, shape, custom];
    }

    [Theory, MemberData(nameof(PartialInheritedCases))]
    public void InheritedInitializerFailureDestroysCompletedFields(int optimization, string shape, bool custom)
    {
        bool array = shape.EndsWith("array");
        string provider = custom ? "(heap)" : "";
        string construct = shape switch {
            "array" => $"Child[] values=new{provider} Child[4]; delete(values);",
            "scalar" => $"Child* value=new{provider} Child{{}}; delete(value);",
            "stack-array" => "Child[] values=Child[4];",
            _ => "Child value=Child{};",
        };
        AssertSuccess(Prelude + $$"""
            struct Resource {
                public int id;
                public Resource() { id=++Log.next; }
                public ~Resource() { Log.fieldDrops++; Log.order=Log.order*10+id; }
            }
            struct Base { public Resource first=Resource(); public ~Base(){Log.wholeDrops++;} }
            struct Middle : Base { public Resource second=Resource(); public ~Middle(){Log.wholeDrops++;} }
            int Fail() { if(++Log.ordinary=={{(array ? 3 : 1)}})throw Failure{31};return 0; }
            struct Child : Middle {
                public Resource third=Resource(); public int failed=Fail();
                public ~Child(){Log.wholeDrops++;}
            }
            int Main() {
                Heap* heap=new Heap();
                try { {{construct}} return 1; }
                catch(readonly Failure& error){if(error.code!=31)return 2;}
                if(Log.fieldDrops!={{(array ? 9 : 3)}} || Log.next!={{(array ? 9 : 3)}})return 3;
                if(Log.order!={{(array ? 987654321 : 321)}} || Log.wholeDrops!={{(array ? 6 : 0)}})return 4;
                if(Log.freed!={{(custom ? 1 : 0)}} || Log.bad!=0)return 5;
                delete(heap);return 0;
            }
            """, optimization);
    }

    [Theory, MemberData(nameof(ShapeCases))]
    public void AggregateArgumentFailureDestroysInitializedFieldsAndArguments(int optimization, bool custom)
    {
        AssertSuccess(Prelude + $$"""
            struct Resource {
                public Resource(){Log.next++;}
                public ~Resource(){Log.fieldDrops++;}
            }
            struct Value { public Resource resource=Resource(); public int number; public ~Value(){Log.wholeDrops++;} }
            int Fail(){throw Failure{32};}
            int Main(){
                Heap* heap=new Heap();
                try{Value* value=new{{(custom ? "(heap)" : "")}} Value{Resource(),Fail()};return 1;}
                catch(readonly Failure& error){if(error.code!=32)return 2;}
                if(Log.next!=2 || Log.fieldDrops!=2 || Log.wholeDrops!=0)return 3;
                if(Log.freed!={{(custom ? 1 : 0)}} || Log.bad!=0)return 4;
                delete(heap);return 0;
            }
            """, optimization);
    }

    public static IEnumerable<object[]> ScalarRollbackCases()
    {
        foreach(int optimization in new[] { 0, 1, 2, 3 })
        foreach(string phase in new[] { "allocation-constructor", "ordinary-constructor", "initializer" })
            yield return [optimization, phase];
    }

    [Theory, MemberData(nameof(ScalarRollbackCases))]
    public void ReleaseExceptionDuringScalarConstructionRollbackTerminates(int optimization, string phase)
    {
        string declaration = phase switch {
            "allocation-constructor" => "public @Value(Heap* h){Fail();}",
            "ordinary-constructor" => "public Value(){Fail();}",
            _ => "public int field=Fail();",
        };
        NativeProcessResult result=Run($$"""
            struct Failure{public int code;}
            extern int puts(readonly byte* text);extern int fflush(void* stream);
            allocator Heap{
                +Heap(nuint s,nuint a){return malloc(s,a);}
                -Heap(void* p){puts("second");fflush(null);free(p);throw Failure{22};}
            }
            int Fail(){puts("first");fflush(null);throw Failure{11};}
            struct Value{ {{declaration}} }
            int Main(){
                Heap* heap=new Heap();
                try{Value* value=new(heap) Value();}
                catch(readonly Failure& error){puts("unexpected-catch");fflush(null);return error.code;}
                return 0;
            }
            """, optimization);
        Assert.NotEqual(0,result.ExitCode);
        Assert.Equal("first\nsecond\n",result.Stdout.Replace("\r\n","\n"));
    }

    private static Compilation Compile(string source) => Compilation.Create(
        new CompilationOptions(CompilationOutputKind.Executable), references: null,
        SourceText.From("namespace AllocatorAudit; " + source, "allocator-audit.xe"));
    private static void AssertSuccess(string source, int optimization) => Assert.Equal(0, Run(source, optimization).ExitCode);
    private static NativeProcessResult Run(string source, int optimization) => Execute(Compile(source), optimization);

    private static NativeProcessResult Execute(Compilation compilation, int optimization, string? cSource = null)
    {
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        foreach (MirFunction function in compilation.GetMirFunctions()) Assert.Empty(MirVerifier.Verify(function));
        string directory = Path.Combine(Path.GetTempPath(), "xenon-allocator-audit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            LlvmTargetOptions target = LlvmTargetOptions.CreateHost(optimizationLevel: optimization, positionIndependentCode: !OperatingSystem.IsWindows());
            string objectPath = Path.Combine(directory, "audit" + LlvmTargetPlatform.GetObjectFileExtension(target.Triple));
            string? cObject = null;
            if (cSource is not null)
            {
                string cPath = Path.Combine(directory, "probe.c"); File.WriteAllText(cPath, cSource);
                cObject = Path.Combine(directory, "probe" + LlvmTargetPlatform.GetObjectFileExtension(target.Triple));
                NativeProcessResult compiled = Process(FindCCompiler(), ["-std=c11", "-c", cPath, "-o", cObject], directory);
                Assert.True(compiled.ExitCode == 0, compiled.Stdout + compiled.Stderr);
            }
            new LlvmObjectEmitter().Emit(compilation, objectPath, target, "allocator-audit");
            string executablePath = XenonBuildPaths.GetExecutablePath(directory, "audit", optimization == 0 ? "debug" : "release", target.Triple);
            var linker = new NativeLinker();
            bool requiresExceptions = LlvmIrGenerator.RequiresNativeExceptionRuntime(compilation);
            LinkedNativeArtifact? runtime = requiresExceptions
                ? linker.LinkExceptionRuntimeLibrary(Path.Combine(Path.GetDirectoryName(executablePath)!,
                    OperatingSystem.IsWindows() ? "xenon-eh.dll" : OperatingSystem.IsMacOS() ? "libxenon-eh.dylib" : "libxenon-eh.so"), target.Triple,
                    OperatingSystem.IsWindows() ? Path.Combine(Path.GetDirectoryName(executablePath)!, "xenon-eh.lib") : null)
                : null;
            var executable = linker.LinkExecutable(objectPath, executablePath, target.Triple,
                new NativeLinkOptions(Libraries: cObject is null ? null : [cObject],
                    RequiresExceptionRuntime: requiresExceptions,
                    ExceptionRuntimeLibrary: runtime?.ImportLibraryPath ?? runtime?.Path));
            return Process(executable.Path, [], directory);
        }
        finally
        {
            for (int attempt=0;;attempt++)
            {
                try { Directory.Delete(directory, recursive: true); break; }
                catch (IOException) when (OperatingSystem.IsWindows() && attempt<5) { Thread.Sleep(50*(attempt+1)); }
                catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows() && attempt<5) { Thread.Sleep(50*(attempt+1)); }
            }
        }
    }
    private static NativeProcessResult Process(string executable, string[] args, string directory)
    {
        var result = new NativeProcessRunner().RunAsync(new NativeProcessRequest(executable, args, directory, TimeSpan.FromSeconds(60))).GetAwaiter().GetResult();
        Assert.True(result.StartError is null && !result.TimedOut && result.TerminationError is null,
            $"{result.StartError}; timedOut={result.TimedOut}; {result.TerminationError}\n{result.Stdout}\n{result.Stderr}");
        return result;
    }
    private static string FindCCompiler()
    {
        if (Environment.GetEnvironmentVariable("CC") is { Length: > 0 } configured) return configured;
        if (!OperatingSystem.IsWindows()) return "cc";
        var candidates = new List<string>();
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            candidates.Add(Path.Combine(root,"LLVM","bin","clang.exe"));
            string studio=Path.Combine(root,"Microsoft Visual Studio");
            if (!Directory.Exists(studio)) continue;
            foreach(string version in Directory.GetDirectories(studio))
            foreach(string edition in Directory.GetDirectories(version))
                candidates.Add(Path.Combine(edition,"VC","Tools","Llvm","x64","bin","clang.exe"));
        }
        return candidates.FirstOrDefault(File.Exists) ?? "clang";
    }
}
