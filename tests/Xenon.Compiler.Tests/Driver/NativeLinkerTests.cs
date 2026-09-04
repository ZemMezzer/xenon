using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Text;
using Xenon.Driver;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.Driver;

public sealed class NativeLinkerTests
{
    static NativeLinkerTests()
    {
        // Trap tests must terminate unattended instead of waiting on Windows' crash UI.
        // Child processes inherit the parent's error mode.
        if (OperatingSystem.IsWindows()) SetErrorMode(0x0001 | 0x0002 | 0x8000);
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AddDelegate(int left, int right);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float SumVectorDelegate(ref NativeVector2 value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FloatVoidDelegate(float value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FloatPairIntDelegate(float expected, float desired);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DoubleVoidDelegate(double value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DoublePairIntDelegate(double expected, double desired);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AddressDelegate(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ParameterlessAddressDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint Int32AddressDelegate(int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AddressVoidDelegate(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AddressIntDelegate(nint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TwoAddressIntDelegate(nint first, nint second);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ParameterlessIntDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ParameterlessVoidDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Int32VoidDelegate(int value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Int32IntDelegate(int value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVector2
    {
        public float X;
        public float Y;
    }

    [Fact]
    public void Linker_CreatesAndRunsHostExecutable()
    {
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Integration;

            extern int puts(readonly byte* text);

            int Main()
            {
                puts("Hello from linked Xenon");
                return 17;
            }
            """, "integration.xe"));
        string directory = Path.Combine(
            Path.GetTempPath(),
            "xenon-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        string objectPath = Path.Combine(
            directory,
            $"integration{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory,
            "integration",
            "debug",
            target.Triple);

        try
        {
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "integration"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path,
                executablePath,
                target.Triple);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using Process process = Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.Equal(string.Empty, error);
            Assert.Contains("Hello from linked Xenon", output, StringComparison.Ordinal);
            Assert.Equal(17, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_CreatesAndRunsHostExecutableUsingStructMethods()
    {
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Integration;

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
                Counter value = Counter(20);
                value.Add(20);

                Counter* pointer = &value;
                pointer->Add(2);

                return value.Read();
            }
            """, "methods-integration.xe"));
        string directory = Path.Combine(
            Path.GetTempPath(),
            "xenon-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        string objectPath = Path.Combine(
            directory,
            $"methods-integration{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory,
            "methods-integration",
            "debug",
            target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);

            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "methods-integration"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path,
                executablePath,
                target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();

            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_CreatesAndRunsHostExecutableAcrossFilesUsingNamespaceImport()
    {
        Compilation compilation = CreateExecutableCompilation(
            SourceText.From("""
                namespace Library.Math;

                public int Add(int left, int right)
                {
                    return left + right;
                }
                """, "math.xe"),
            SourceText.From("""
                using Library.Math;

                namespace Integration;

                int Main()
                {
                    return Add(20, 22);
                }
                """, "main.xe"));
        string directory = Path.Combine(
            Path.GetTempPath(),
            "xenon-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        string objectPath = Path.Combine(
            directory,
            $"using-integration{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory,
            "using-integration",
            "debug",
            target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);

            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "using-integration"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path,
                executablePath,
                target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();

            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_CreatesHostStaticLibrary()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(positionIndependentCode: true);
        Compilation compilation = CreateLibraryCompilation();
        string objectPath = Path.Combine(
            directory,
            $"math{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetStaticLibraryPath(
            directory, "math", "debug", target.Triple);

        try
        {
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "math");
            LinkedNativeArtifact library = new NativeLinker().CreateStaticLibrary(
                objectFile.Path, libraryPath, target.Triple);

            Assert.True(File.Exists(library.Path));
            Assert.True(new FileInfo(library.Path).Length > 8);
            Assert.Equal("!<arch>\n", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(library.Path), 0, 8));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_CreatesLoadableHostSharedLibraryWithExport()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(positionIndependentCode: true);
        Compilation compilation = CreateLibraryCompilation();
        string objectPath = Path.Combine(
            directory,
            $"math{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "math", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "math", "debug", target.Triple);

        try
        {
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "math");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: ["Integration_Add"]),
                importLibraryPath);

            Assert.True(File.Exists(library.Path));
            if (OperatingSystem.IsWindows())
            {
                Assert.True(File.Exists(library.ImportLibraryPath));
            }
            else if (OperatingSystem.IsMacOS())
            {
                AssertMacOsMachO(library.Path, expectedFileType: 6);
            }

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                nint address = NativeLibrary.GetExport(handle, "Integration_Add");
                AddDelegate add = Marshal.GetDelegateForFunctionPointer<AddDelegate>(address);
                Assert.Equal(42, add(20, 22));
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_UninitializedLockBackedAtomicsAndAtomicFieldsPreserveLifetimes(int optimization)
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 1_000;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace AtomicInitializationRuntime;

            struct Counters
            {
                public static atomic<int> Destroyed;
                public static atomic<int> Failures;
            }

            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource() { Counters.Destroyed++; }
            }

            struct Snapshot
            {
                public int First;
                public int Second;
                public int Third;
                public int Fourth;
            }

            struct AtomicHolder
            {
                public atomic<shared<Resource>> Owner;
                public AtomicHolder(shared<Resource> owner) { Owner = owner; }
            }

            struct Nested
            {
                public AtomicHolder Inner;
                public Nested(shared<Resource> owner) { Inner = AtomicHolder(owner); }
            }

            export void Reset()
            {
                Counters.Destroyed = 0;
                Counters.Failures = 0;
            }

            export void Exercise(int worker, int iterations)
            {
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    int token = worker * iterations + iteration + 1;
                    {
                        atomic<Snapshot> value;
                        value = Snapshot { token, token, token, token };
                        Snapshot observed = value;
                        if (observed.First != token || observed.Second != token ||
                            observed.Third != token || observed.Fourth != token)
                            Counters.Failures++;
                    }
                    {
                        shared<Resource> owner = new Resource(token);

                        atomic<shared<Resource>> strong;
                        strong = owner;
                        shared<Resource> strongSnapshot = strong;
                        if (strongSnapshot == null || strongSnapshot->Id != token)
                            Counters.Failures++;

                        weak<Resource> observer = owner;
                        atomic<weak<Resource>> weakSlot;
                        weakSlot = observer;
                        weak<Resource> weakSnapshot = weakSlot;
                        shared<Resource> upgraded = lock weakSnapshot;
                        if (upgraded == null || upgraded->Id != token)
                            Counters.Failures++;

                        Nested nested = Nested(owner);
                        shared<Resource> nestedSnapshot = nested.Inner.Owner;
                        if (nestedSnapshot == null || nestedSnapshot->Id != token)
                            Counters.Failures++;
                    }
                }
            }

            export int Destroyed() { return Counters.Destroyed; }
            export int Failures() { return Counters.Failures; }
            """, "atomic-initialization-runtime.xe"));
        string objectPath = Path.Combine(directory,
            "atomic-initialization-runtime" + LlvmTargetPlatform.GetObjectFileExtension(target.Triple));
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "atomic-initialization-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "atomic-initialization-runtime", "debug", target.Triple);
        string[] exports =
        [
            "AtomicInitializationRuntime_Reset",
            "AtomicInitializationRuntime_Exercise",
            "AtomicInitializationRuntime_Destroyed",
            "AtomicInitializationRuntime_Failures",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "atomic-initialization-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path, libraryPath, target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports), importLibraryPath);
            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                ParameterlessVoidDelegate reset = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[0]);
                AddDelegate exercise = LoadDelegate<AddDelegate>(handle, exports[1]);
                ParameterlessIntDelegate destroyed = LoadDelegate<ParameterlessIntDelegate>(handle, exports[2]);
                ParameterlessIntDelegate failures = LoadDelegate<ParameterlessIntDelegate>(handle, exports[3]);

                reset();
                RunParallel(workerCount, worker => exercise(worker, iterationsPerWorker));
                Assert.Equal(0, failures());
                Assert.Equal(workerCount * iterationsPerWorker, destroyed());
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ThreadLocalStateIsIsolatedAndOwnedValuesDieAtNativeThreadExit(int optimization)
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace ThreadLocalRuntime;

            struct Counters
            {
                public static atomic<int> Initializations;
                public static atomic<int> Destructions;
                public static int Trace;
            }

            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource()
                {
                    Counters.Trace = Counters.Trace * 10 + Id;
                    Counters.Destructions++;
                }
            }

            int InitializeValue()
            {
                Counters.Initializations++;
                return 41;
            }

            struct State
            {
                public static threadlocal int Value = InitializeValue();
                private static threadlocal unique<Resource> First = new Resource(1);
                private static threadlocal unique<Resource> Second = new Resource(2);

                public static int TouchOwners()
                {
                    int first = State.First->Id;
                    int second = State.Second->Id;
                    return first * 10 + second;
                }
            }

            export int Read() { return State.Value; }
            export void Write(int value) { State.Value = value; }
            export int TouchOwners() { return State.TouchOwners(); }
            export int Initializations() { return Counters.Initializations; }
            export int Destructions() { return Counters.Destructions; }
            export int Trace() { return Counters.Trace; }
            """, "thread-local-runtime.xe"));
        string objectPath = Path.Combine(directory,
            "thread-local-runtime" + LlvmTargetPlatform.GetObjectFileExtension(target.Triple));
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "thread-local-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "thread-local-runtime", "debug", target.Triple);
        string[] exports =
        [
            "ThreadLocalRuntime_Read",
            "ThreadLocalRuntime_Write",
            "ThreadLocalRuntime_TouchOwners",
            "ThreadLocalRuntime_Initializations",
            "ThreadLocalRuntime_Destructions",
            "ThreadLocalRuntime_Trace",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "thread-local-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path, libraryPath, target.Triple,
                new NativeLinkOptions(
                    ExportedSymbols: exports,
                    RequiresThreadingRuntime:
                        LlvmIrGenerator.RequiresNativeThreadingRuntime(compilation)),
                importLibraryPath);
            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                ParameterlessIntDelegate read = LoadDelegate<ParameterlessIntDelegate>(handle, exports[0]);
                Int32VoidDelegate write = LoadDelegate<Int32VoidDelegate>(handle, exports[1]);
                ParameterlessIntDelegate touchOwners = LoadDelegate<ParameterlessIntDelegate>(handle, exports[2]);
                ParameterlessIntDelegate initializations = LoadDelegate<ParameterlessIntDelegate>(handle, exports[3]);
                ParameterlessIntDelegate destructions = LoadDelegate<ParameterlessIntDelegate>(handle, exports[4]);
                ParameterlessIntDelegate trace = LoadDelegate<ParameterlessIntDelegate>(handle, exports[5]);

                Assert.Equal(41, read());
                write(99);
                int[] before = new int[2];
                int[] after = new int[2];
                Thread[] workers = Enumerable.Range(0, 2).Select(index => new Thread(() =>
                {
                    before[index] = read();
                    write(100 + index);
                    after[index] = read();
                })).ToArray();
                foreach (Thread worker in workers) worker.Start();
                foreach (Thread worker in workers) worker.Join();

                Assert.Equal([41, 41], before);
                Assert.Equal([100, 101], after);
                Assert.Equal(99, read());
                Assert.Equal(3, initializations());

                var untouched = new Thread(() => { });
                untouched.Start();
                untouched.Join();
                Assert.Equal(3, initializations());

                int owners = 0;
                var ownerThread = new Thread(() => owners = touchOwners());
                ownerThread.Start();
                ownerThread.Join();
                Assert.Equal(12, owners);
                Assert.Equal(2, destructions());
                Assert.Equal(21, trace());
            }
            finally
            {
                // Every native worker that initialized destructible TLS is joined above. Keep this
                // ordering: the module owns the callbacks that run when those threads terminate.
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_BuiltinAllocationIsSafeAcrossNativeThreads(int optimization)
    {
        const int workerCount = 8;
        const int iterations = 2_000;
        const int crossThreadAllocationsPerWorker = 256;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace ConcurrentAllocationRuntime;

            struct Counters
            {
                public static atomic<int> Created;
                public static atomic<int> Destroyed;
                public static atomic<int> Failures;
            }

            int MarkCreated()
            {
                Counters.Created++;
                return 1;
            }

            struct Item
            {
                public int Marker = MarkCreated();
                public int Worker;
                public int Sequence;

                public Item(int worker, int sequence)
                {
                    Worker = worker;
                    Sequence = sequence;
                }

                public ~Item() { Counters.Destroyed++; }
            }

            void Check(bool condition)
            {
                if (!condition) Counters.Failures++;
            }

            export void Reset()
            {
                Counters.Created = 0;
                Counters.Destroyed = 0;
                Counters.Failures = 0;
            }

            export int Exercise(int worker, int iterations)
            {
                for (int iteration = 0; iteration < iterations; iteration++)
                {
                    Item* raw = new Item(worker, iteration);
                    Check(raw->Marker == 1 && raw->Worker == worker && raw->Sequence == iteration);
                    free(raw);

                    int length = (iteration & 15) + 1;
                    Item[] items = new Item[length];
                    for (int index = 0; index < length; index++)
                    {
                        Check(items[index].Marker == 1);
                        items[index].Worker = worker;
                        items[index].Sequence = iteration + index;
                        Check(items[index].Worker == worker && items[index].Sequence == iteration + index);
                    }
                    free(items);

                    int[] values = new int[length];
                    Check(values[0] == 0 && values[length - 1] == 0);
                    for (int index = 0; index < length; index++) values[index] = worker + iteration + index;
                    Check(values[length - 1] == worker + iteration + length - 1);
                    free(values);

                    {
                        unique<Item> owned = new Item(worker, iteration);
                        Check(owned->Marker == 1 && owned->Worker == worker);
                    }

                    {
                        unique<int[]> owned = new int[length];
                        owned[length - 1] = worker;
                        Check(owned[length - 1] == worker);
                    }

                    {
                        shared<Item> owner = new Item(worker, iteration);
                        weak<Item> observer = owner;
                        shared<Item> snapshot = lock observer;
                        Check(snapshot != null && snapshot->Worker == worker && snapshot->Sequence == iteration);
                    }
                }
                return Counters.Failures;
            }

            export Item* Allocate(int token) { return new Item(token, token); }
            export void Release(Item* value) { free(value); }
            export int Created() { return Counters.Created; }
            export int Destroyed() { return Counters.Destroyed; }
            export int Failures() { return Counters.Failures; }
            """, "concurrent-allocation-runtime.xe"));
        string objectPath = Path.Combine(directory,
            "concurrent-allocation-runtime" + LlvmTargetPlatform.GetObjectFileExtension(target.Triple));
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "concurrent-allocation-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "concurrent-allocation-runtime", "debug", target.Triple);
        string[] exports =
        [
            "ConcurrentAllocationRuntime_Reset",
            "ConcurrentAllocationRuntime_Exercise",
            "ConcurrentAllocationRuntime_Allocate",
            "ConcurrentAllocationRuntime_Release",
            "ConcurrentAllocationRuntime_Created",
            "ConcurrentAllocationRuntime_Destroyed",
            "ConcurrentAllocationRuntime_Failures",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "concurrent-allocation-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path, libraryPath, target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports), importLibraryPath);
            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                ParameterlessVoidDelegate reset = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[0]);
                AddDelegate exercise = LoadDelegate<AddDelegate>(handle, exports[1]);
                Int32AddressDelegate allocate = LoadDelegate<Int32AddressDelegate>(handle, exports[2]);
                AddressVoidDelegate release = LoadDelegate<AddressVoidDelegate>(handle, exports[3]);
                ParameterlessIntDelegate created = LoadDelegate<ParameterlessIntDelegate>(handle, exports[4]);
                ParameterlessIntDelegate destroyed = LoadDelegate<ParameterlessIntDelegate>(handle, exports[5]);
                ParameterlessIntDelegate failures = LoadDelegate<ParameterlessIntDelegate>(handle, exports[6]);

                reset();
                RunParallel(workerCount, worker => Assert.Equal(0, exercise(worker, iterations)));
                Assert.Equal(0, failures());

                var allocations = new nint[workerCount, crossThreadAllocationsPerWorker];
                RunParallel(workerCount, worker =>
                {
                    for (int index = 0; index < crossThreadAllocationsPerWorker; index++)
                        allocations[worker, index] = allocate(worker * crossThreadAllocationsPerWorker + index);
                });
                RunParallel(workerCount, worker =>
                {
                    int source = (worker + 1) % workerCount;
                    for (int index = 0; index < crossThreadAllocationsPerWorker; index++)
                        release(allocations[source, index]);
                });

                int completeCycles = iterations / 16;
                int remainder = iterations % 16;
                int arrayElementsPerWorker = completeCycles * 136 + remainder * (remainder + 1) / 2;
                int expected = workerCount * (arrayElementsPerWorker + iterations * 3) +
                    workerCount * crossThreadAllocationsPerWorker;
                Assert.Equal(expected, created());
                Assert.Equal(expected, destroyed());
                Assert.Equal(0, failures());
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ConcurrentOwnershipRetainReleaseAndWeakUpgradeAreSafe(int optimization)
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 25_000;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Concurrency;

            struct State
            {
                public static shared<Resource> Strong;
                public static weak<Resource> Observer;
                public static int Destroyed;
            }

            struct Resource
            {
                public int Marker;
                public Resource(int marker) { Marker = marker; }
                public ~Resource() { State.Destroyed++; }
            }

            export void Initialize()
            {
                shared<Resource> value = new Resource(42);
                State.Strong = value;
                State.Observer = value;
            }

            export int CopyShared()
            {
                shared<Resource> local = State.Strong;
                return local->Marker;
            }

            export int CopyWeakAndLock()
            {
                weak<Resource> local = State.Observer;
                shared<Resource> strong = lock local;
                if (strong == null) return 0;
                return strong->Marker;
            }

            export void ReleaseStrong() { State.Strong = null; }
            export int ReadDestroyed() { return State.Destroyed; }

            export void ReleaseWeak()
            {
                shared<Resource> empty = null;
                State.Observer = empty;
            }
            """, "ownership-concurrency.xe"));
        string objectPath = Path.Combine(
            directory,
            $"ownership-concurrency{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "ownership-concurrency", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "ownership-concurrency", "debug", target.Triple);
        string[] exports =
        [
            "Concurrency_Initialize",
            "Concurrency_CopyShared",
            "Concurrency_CopyWeakAndLock",
            "Concurrency_ReleaseStrong",
            "Concurrency_ReadDestroyed",
            "Concurrency_ReleaseWeak",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "ownership-concurrency");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                ParameterlessVoidDelegate initialize = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[0]);
                ParameterlessIntDelegate copyShared = LoadDelegate<ParameterlessIntDelegate>(handle, exports[1]);
                ParameterlessIntDelegate copyWeakAndLock = LoadDelegate<ParameterlessIntDelegate>(handle, exports[2]);
                ParameterlessVoidDelegate releaseStrong = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[3]);
                ParameterlessIntDelegate readDestroyed = LoadDelegate<ParameterlessIntDelegate>(handle, exports[4]);
                ParameterlessVoidDelegate releaseWeak = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[5]);

                initialize();

                Task[] sharedWorkers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
                {
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                        Assert.Equal(42, copyShared());
                })).ToArray();
                Task.WaitAll(sharedWorkers);

                using var ready = new CountdownEvent(workerCount);
                using var gate = new ManualResetEventSlim();
                int completedAttempts = 0;
                Task[] weakWorkers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(() =>
                {
                    ready.Signal();
                    gate.Wait();
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                    {
                        int result = copyWeakAndLock();
                        Assert.True(result is 0 or 42, $"Unexpected weak-lock result: {result}");
                        Interlocked.Increment(ref completedAttempts);
                    }
                })).ToArray();

                ready.Wait();
                gate.Set();
                Assert.True(SpinWait.SpinUntil(
                    () => Volatile.Read(ref completedAttempts) >= 1_000,
                    TimeSpan.FromSeconds(10)));
                releaseStrong();
                Task.WaitAll(weakWorkers);

                Assert.Equal(1, readDestroyed());
                for (int iteration = 0; iteration < 1_000; iteration++)
                    Assert.Equal(0, copyWeakAndLock());
                releaseWeak();
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_PrimitiveAtomicRmwIsIndivisibleAcrossNativeThreads(int optimization)
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 10_000;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace PrimitiveAtomics;

            struct State
            {
                public static atomic<int> Counter;
                public static atomic<int> Bits;
            }

            export void ResetCounter(int value) { State.Counter = value; }
            export int ReadCounter() { return State.Counter; }
            export int PostIncrement() { return State.Counter++; }
            export int PreIncrement() { return ++State.Counter; }
            export void AddTwo() { State.Counter += 2; }
            export void SubtractOne() { State.Counter -= 1; }
            export void ResetBits(int value) { State.Bits = value; }
            export int ReadBits() { return State.Bits; }
            export void OrBits(int value) { State.Bits |= value; }
            export void AndBits(int value) { State.Bits &= value; }
            export void XorBits(int value) { State.Bits ^= value; }
            """, "primitive-atomics.xe"));
        string objectPath = Path.Combine(
            directory,
            $"primitive-atomics{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "primitive-atomics", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "primitive-atomics", "debug", target.Triple);
        string[] exports =
        [
            "PrimitiveAtomics_ResetCounter",
            "PrimitiveAtomics_ReadCounter",
            "PrimitiveAtomics_PostIncrement",
            "PrimitiveAtomics_PreIncrement",
            "PrimitiveAtomics_AddTwo",
            "PrimitiveAtomics_SubtractOne",
            "PrimitiveAtomics_ResetBits",
            "PrimitiveAtomics_ReadBits",
            "PrimitiveAtomics_OrBits",
            "PrimitiveAtomics_AndBits",
            "PrimitiveAtomics_XorBits",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "primitive-atomics");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                Int32VoidDelegate resetCounter = LoadDelegate<Int32VoidDelegate>(handle, exports[0]);
                ParameterlessIntDelegate readCounter = LoadDelegate<ParameterlessIntDelegate>(handle, exports[1]);
                ParameterlessIntDelegate postIncrement = LoadDelegate<ParameterlessIntDelegate>(handle, exports[2]);
                ParameterlessIntDelegate preIncrement = LoadDelegate<ParameterlessIntDelegate>(handle, exports[3]);
                ParameterlessVoidDelegate addTwo = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[4]);
                ParameterlessVoidDelegate subtractOne = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[5]);
                Int32VoidDelegate resetBits = LoadDelegate<Int32VoidDelegate>(handle, exports[6]);
                ParameterlessIntDelegate readBits = LoadDelegate<ParameterlessIntDelegate>(handle, exports[7]);
                Int32VoidDelegate orBits = LoadDelegate<Int32VoidDelegate>(handle, exports[8]);
                Int32VoidDelegate andBits = LoadDelegate<Int32VoidDelegate>(handle, exports[9]);
                Int32VoidDelegate xorBits = LoadDelegate<Int32VoidDelegate>(handle, exports[10]);

                resetCounter(40);
                Assert.Equal(40, postIncrement());
                Assert.Equal(42, preIncrement());

                resetCounter(0);
                RunParallel(workerCount, _ =>
                {
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                    {
                        postIncrement();
                        addTwo();
                        subtractOne();
                    }
                });
                Assert.Equal(workerCount * iterationsPerWorker * 2, readCounter());

                const int firstSentinel = 0x13579bdf;
                const int secondSentinel = 0x2468ace0;
                resetCounter(firstSentinel);
                RunParallel(workerCount, worker =>
                {
                    if ((worker & 1) == 0)
                    {
                        for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                            resetCounter((iteration & 1) == 0 ? firstSentinel : secondSentinel);
                        return;
                    }
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                    {
                        int observed = readCounter();
                        Assert.True(observed is firstSentinel or secondSentinel,
                            $"Observed torn atomic write: 0x{observed:x8}");
                    }
                });

                resetBits(0);
                RunParallel(workerCount, worker =>
                {
                    int bit = 1 << worker;
                    for (int iteration = 0; iteration < 1_001; iteration++) orBits(bit);
                });
                Assert.Equal(0xff, readBits());

                resetBits(-1);
                RunParallel(workerCount, worker => andBits(~(1 << worker)));
                Assert.Equal(~0xff, readBits());

                resetBits(0);
                RunParallel(workerCount, worker =>
                {
                    int bit = 1 << worker;
                    for (int iteration = 0; iteration < 1_001; iteration++) xorBits(bit);
                });
                Assert.Equal(0xff, readBits());
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_SwapPreservesMoveOnlyValuesAndUsesAtomicExchange(int optimization)
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 10_000;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace SwapRuntime;

            struct State
            {
                public static int Trace;
                public static atomic<int> Current;
            }

            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource() { State.Trace = State.Trace * 10 + Id; }
            }

            export int OrdinarySwap()
            {
                int first = 10;
                int second = 20;
                first <-> second;
                return first * 100 + second;
            }

            export int UniqueSwap()
            {
                State.Trace = 0;
                {
                    unique<Resource> first = new Resource(1);
                    unique<Resource> second = new Resource(2);
                    first <-> second;
                }
                return State.Trace;
            }

            export void ResetAtomic(int value) { State.Current = value; }
            export int ReadAtomic() { return State.Current; }
            export int Exchange(int replacement)
            {
                State.Current <-> replacement;
                return replacement;
            }
            export int ExchangeSymmetric(int replacement)
            {
                replacement <-> State.Current;
                return replacement;
            }
            """, "swap-runtime.xe"));
        string objectPath = Path.Combine(
            directory,
            $"swap-runtime{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "swap-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "swap-runtime", "debug", target.Triple);
        string[] exports =
        [
            "SwapRuntime_OrdinarySwap",
            "SwapRuntime_UniqueSwap",
            "SwapRuntime_ResetAtomic",
            "SwapRuntime_ReadAtomic",
            "SwapRuntime_Exchange",
            "SwapRuntime_ExchangeSymmetric",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "swap-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                ParameterlessIntDelegate ordinarySwap = LoadDelegate<ParameterlessIntDelegate>(handle, exports[0]);
                ParameterlessIntDelegate uniqueSwap = LoadDelegate<ParameterlessIntDelegate>(handle, exports[1]);
                Int32VoidDelegate resetAtomic = LoadDelegate<Int32VoidDelegate>(handle, exports[2]);
                ParameterlessIntDelegate readAtomic = LoadDelegate<ParameterlessIntDelegate>(handle, exports[3]);
                Int32IntDelegate exchange = LoadDelegate<Int32IntDelegate>(handle, exports[4]);
                Int32IntDelegate exchangeSymmetric = LoadDelegate<Int32IntDelegate>(handle, exports[5]);

                Assert.Equal(2010, ordinarySwap());
                Assert.Equal(12, uniqueSwap());

                resetAtomic(10);
                Assert.Equal(10, exchange(20));
                Assert.Equal(20, readAtomic());
                Assert.Equal(20, exchangeSymmetric(30));
                Assert.Equal(30, readAtomic());

                resetAtomic(0);
                RunParallel(workerCount, worker =>
                {
                    int replacement = worker + 1;
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                    {
                        int previous = exchange(replacement);
                        Assert.InRange(previous, 0, workerCount);
                    }
                });
                Assert.InRange(readAtomic(), 1, workerCount);
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_CompareExchangeHasValueSemanticsAndOneContentionWinner(int optimization)
    {
        const int workerCount = 8;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace CompareExchangeRuntime;

            struct State
            {
                public static atomic<int> Current;
                public static int Calls;
            }

            int Expected() { State.Calls = State.Calls * 10 + 1; return 10; }
            int Desired() { State.Calls = State.Calls * 10 + 2; return 20; }

            export void Reset(int value) { State.Current = value; }
            export int Read() { return State.Current; }

            export int EvaluateOnce()
            {
                State.Current = 10;
                State.Calls = 0;
                bool succeeded = State.Current : Expected() --> Desired();
                if (!succeeded) return -1;
                return State.Calls * 100 + State.Current;
            }

            export int SuccessPreservesOperands()
            {
                State.Current = 10;
                int expected = 10;
                int desired = 20;
                bool succeeded = State.Current : expected --> desired;
                if (!succeeded) return -1;
                return State.Current * 10000 + expected * 100 + desired;
            }

            export int FailurePreservesOperands()
            {
                State.Current = 11;
                int expected = 10;
                int desired = 20;
                bool succeeded = State.Current : expected --> desired;
                if (succeeded) return -1;
                return State.Current * 10000 + expected * 100 + desired;
            }

            export int TryWin(int desired)
            {
                if (State.Current : 0 --> desired) return 1;
                return 0;
            }
            """, "compare-exchange-runtime.xe"));
        string objectPath = Path.Combine(
            directory,
            $"compare-exchange-runtime{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "compare-exchange-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "compare-exchange-runtime", "debug", target.Triple);
        string[] exports =
        [
            "CompareExchangeRuntime_Reset",
            "CompareExchangeRuntime_Read",
            "CompareExchangeRuntime_EvaluateOnce",
            "CompareExchangeRuntime_SuccessPreservesOperands",
            "CompareExchangeRuntime_FailurePreservesOperands",
            "CompareExchangeRuntime_TryWin",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "compare-exchange-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                Int32VoidDelegate reset = LoadDelegate<Int32VoidDelegate>(handle, exports[0]);
                ParameterlessIntDelegate read = LoadDelegate<ParameterlessIntDelegate>(handle, exports[1]);
                ParameterlessIntDelegate evaluateOnce = LoadDelegate<ParameterlessIntDelegate>(handle, exports[2]);
                ParameterlessIntDelegate success = LoadDelegate<ParameterlessIntDelegate>(handle, exports[3]);
                ParameterlessIntDelegate failure = LoadDelegate<ParameterlessIntDelegate>(handle, exports[4]);
                Int32IntDelegate tryWin = LoadDelegate<Int32IntDelegate>(handle, exports[5]);

                Assert.Equal(1220, evaluateOnce());
                Assert.Equal(201020, success());
                Assert.Equal(111020, failure());

                reset(0);
                int winnerCount = 0;
                RunParallel(workerCount, worker =>
                {
                    if (tryWin(worker + 1) == 1) Interlocked.Increment(ref winnerCount);
                });
                Assert.Equal(1, winnerCount);
                Assert.InRange(read(), 1, workerCount);
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_FloatingCompareExchangeUsesValueEqualityForNativeAndFallbackStorage(int optimization)
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace FloatingCompareExchangeRuntime;

            struct FloatBox { public float Value; }
            struct DoubleBox { public double Value; }
            struct Globals
            {
                public static atomic<float> FloatValue;
                public static atomic<FloatBox> FloatFallback;
                public static atomic<double> DoubleValue;
                public static atomic<DoubleBox> DoubleFallback;
            }

            export void SetFloat(float value)
            {
                Globals.FloatValue = value;
                Globals.FloatFallback = FloatBox { value };
            }
            export int CompareFloat(float expected, float desired)
            {
                int result = 0;
                if (Globals.FloatValue : expected --> desired) result += 1;
                FloatBox oldValue = FloatBox { expected };
                FloatBox newValue = FloatBox { desired };
                if (Globals.FloatFallback : oldValue --> newValue) result += 2;
                return result;
            }
            export int FloatIsNan()
            {
                float scalar = Globals.FloatValue;
                FloatBox fallback = Globals.FloatFallback;
                if (scalar != scalar && fallback.Value != fallback.Value) return 1;
                return 0;
            }

            export void SetDouble(double value)
            {
                Globals.DoubleValue = value;
                Globals.DoubleFallback = DoubleBox { value };
            }
            export int CompareDouble(double expected, double desired)
            {
                int result = 0;
                if (Globals.DoubleValue : expected --> desired) result += 1;
                DoubleBox oldValue = DoubleBox { expected };
                DoubleBox newValue = DoubleBox { desired };
                if (Globals.DoubleFallback : oldValue --> newValue) result += 2;
                return result;
            }
            export int DoubleIsNan()
            {
                double scalar = Globals.DoubleValue;
                DoubleBox fallback = Globals.DoubleFallback;
                if (scalar != scalar && fallback.Value != fallback.Value) return 1;
                return 0;
            }
            """, "floating-compare-exchange-runtime.xe"));
        string objectPath = Path.Combine(directory,
            "floating-compare-exchange-runtime" + LlvmTargetPlatform.GetObjectFileExtension(target.Triple));
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "floating-compare-exchange-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "floating-compare-exchange-runtime", "debug", target.Triple);
        string[] exports =
        [
            "FloatingCompareExchangeRuntime_SetFloat",
            "FloatingCompareExchangeRuntime_CompareFloat",
            "FloatingCompareExchangeRuntime_FloatIsNan",
            "FloatingCompareExchangeRuntime_SetDouble",
            "FloatingCompareExchangeRuntime_CompareDouble",
            "FloatingCompareExchangeRuntime_DoubleIsNan",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "floating-compare-exchange-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path, libraryPath, target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports), importLibraryPath);
            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                FloatVoidDelegate setFloat = LoadDelegate<FloatVoidDelegate>(handle, exports[0]);
                FloatPairIntDelegate compareFloat = LoadDelegate<FloatPairIntDelegate>(handle, exports[1]);
                ParameterlessIntDelegate floatIsNan = LoadDelegate<ParameterlessIntDelegate>(handle, exports[2]);
                DoubleVoidDelegate setDouble = LoadDelegate<DoubleVoidDelegate>(handle, exports[3]);
                DoublePairIntDelegate compareDouble = LoadDelegate<DoublePairIntDelegate>(handle, exports[4]);
                ParameterlessIntDelegate doubleIsNan = LoadDelegate<ParameterlessIntDelegate>(handle, exports[5]);

                setFloat(+0.0f);
                Assert.Equal(3, compareFloat(-0.0f, 1.0f));
                setFloat(-0.0f);
                Assert.Equal(3, compareFloat(+0.0f, 2.0f));
                setFloat(5.0f);
                Assert.Equal(3, compareFloat(5.0f, 6.0f));
                Assert.Equal(0, compareFloat(5.0f, 7.0f));
                setFloat(float.NaN);
                Assert.Equal(0, compareFloat(float.NaN, 8.0f));
                Assert.Equal(1, floatIsNan());

                setDouble(+0.0);
                Assert.Equal(3, compareDouble(-0.0, 1.0));
                setDouble(-0.0);
                Assert.Equal(3, compareDouble(+0.0, 2.0));
                setDouble(5.0);
                Assert.Equal(3, compareDouble(5.0, 6.0));
                Assert.Equal(0, compareDouble(5.0, 7.0));
                setDouble(double.NaN);
                Assert.Equal(0, compareDouble(double.NaN, 8.0));
                Assert.Equal(1, doubleIsNan());
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_AtomicPointersRemainRawAndAreIndivisibleUnderContention(int optimization)
    {
        const int workerCount = 8;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace AtomicPointerRuntime;

            struct Node { public int Value; }
            struct State { public static atomic<Node*> Current; }

            export void Reset(Node* value) { State.Current = value; }
            export Node* Read() { return State.Current; }
            export Node* Exchange(Node* replacement)
            {
                State.Current <-> replacement;
                return replacement;
            }
            export int Replace(Node* expected, Node* desired)
            {
                if (State.Current : expected --> desired) return 1;
                return 0;
            }
            export int Clear(Node* expected)
            {
                if (State.Current : expected --> null) return 1;
                return 0;
            }
            """, "atomic-pointer-runtime.xe"));
        string objectPath = Path.Combine(
            directory,
            $"atomic-pointer-runtime{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "atomic-pointer-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "atomic-pointer-runtime", "debug", target.Triple);
        string[] exports =
        [
            "AtomicPointerRuntime_Reset",
            "AtomicPointerRuntime_Read",
            "AtomicPointerRuntime_Exchange",
            "AtomicPointerRuntime_Replace",
            "AtomicPointerRuntime_Clear",
        ];
        nint initial = Marshal.AllocHGlobal(sizeof(int));
        nint replacement = Marshal.AllocHGlobal(sizeof(int));
        nint[] contenders = Enumerable.Range(0, workerCount)
            .Select(_ => Marshal.AllocHGlobal(sizeof(int)))
            .ToArray();

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "atomic-pointer-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                AddressVoidDelegate reset = LoadDelegate<AddressVoidDelegate>(handle, exports[0]);
                ParameterlessAddressDelegate read = LoadDelegate<ParameterlessAddressDelegate>(handle, exports[1]);
                AddressDelegate exchange = LoadDelegate<AddressDelegate>(handle, exports[2]);
                TwoAddressIntDelegate replace = LoadDelegate<TwoAddressIntDelegate>(handle, exports[3]);
                AddressIntDelegate clear = LoadDelegate<AddressIntDelegate>(handle, exports[4]);

                reset(initial);
                Assert.Equal(initial, read());
                Assert.Equal(initial, exchange(replacement));
                Assert.Equal(replacement, read());
                Assert.Equal(0, replace(initial, contenders[0]));
                Assert.Equal(replacement, read());
                Assert.Equal(1, replace(replacement, contenders[0]));
                Assert.Equal(contenders[0], read());
                Assert.Equal(1, clear(contenders[0]));
                Assert.Equal(nint.Zero, read());

                reset(initial);
                int winnerCount = 0;
                RunParallel(workerCount, worker =>
                {
                    if (replace(initial, contenders[worker]) == 1)
                        Interlocked.Increment(ref winnerCount);
                });
                Assert.Equal(1, winnerCount);
                Assert.Contains(read(), contenders);
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(initial);
            Marshal.FreeHGlobal(replacement);
            foreach (nint contender in contenders) Marshal.FreeHGlobal(contender);
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_CompositeAtomicsDoNotTearAndPreserveDestruction(int optimization)
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 10_000;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace CompositeAtomicRuntime;

            struct Snapshot
            {
                public int First;
                public int Second;
                public int Third;
                public int Fourth;
            }

            struct Globals
            {
                public static atomic<Snapshot> Current;
                public static int Trace;
            }

            struct Tracked
            {
                public int Id;
                public ~Tracked() { Globals.Trace = Globals.Trace * 10 + Id; }
            }

            export void Write(int value)
            {
                Globals.Current = Snapshot { value, value, value, value };
            }

            export int IsConsistent()
            {
                Snapshot value = Globals.Current;
                if (value.First != value.Second) return 0;
                if (value.First != value.Third) return 0;
                if (value.First != value.Fourth) return 0;
                return 1;
            }

            export int ReadGeneration()
            {
                Snapshot value = Globals.Current;
                return value.First;
            }

            export int TryTransition(int expected, int desired)
            {
                Snapshot oldValue = Snapshot { expected, expected, expected, expected };
                Snapshot newValue = Snapshot { desired, desired, desired, desired };
                if (Globals.Current : oldValue --> newValue) return 1;
                return 0;
            }

            export int Exchange(int desired)
            {
                Snapshot replacement = Snapshot { desired, desired, desired, desired };
                Globals.Current <-> replacement;
                return replacement.First;
            }

            export int AtomicSize() { return cast<int>(sizeof(atomic<Snapshot>)); }
            export int ValueSize() { return cast<int>(sizeof(Snapshot)); }

            export int LocalCleanup()
            {
                Globals.Trace = 0;
                {
                    atomic<Tracked> current = Tracked { 1 };
                    current = Tracked { 2 };
                }
                return Globals.Trace;
            }
            """, "composite-atomic-runtime.xe"));
        string objectPath = Path.Combine(
            directory,
            $"composite-atomic-runtime{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "composite-atomic-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "composite-atomic-runtime", "debug", target.Triple);
        string[] exports =
        [
            "CompositeAtomicRuntime_Write",
            "CompositeAtomicRuntime_IsConsistent",
            "CompositeAtomicRuntime_ReadGeneration",
            "CompositeAtomicRuntime_TryTransition",
            "CompositeAtomicRuntime_Exchange",
            "CompositeAtomicRuntime_AtomicSize",
            "CompositeAtomicRuntime_ValueSize",
            "CompositeAtomicRuntime_LocalCleanup",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "composite-atomic-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                Int32VoidDelegate write = LoadDelegate<Int32VoidDelegate>(handle, exports[0]);
                ParameterlessIntDelegate isConsistent = LoadDelegate<ParameterlessIntDelegate>(handle, exports[1]);
                ParameterlessIntDelegate read = LoadDelegate<ParameterlessIntDelegate>(handle, exports[2]);
                AddDelegate transition = LoadDelegate<AddDelegate>(handle, exports[3]);
                Int32IntDelegate exchange = LoadDelegate<Int32IntDelegate>(handle, exports[4]);
                ParameterlessIntDelegate atomicSize = LoadDelegate<ParameterlessIntDelegate>(handle, exports[5]);
                ParameterlessIntDelegate valueSize = LoadDelegate<ParameterlessIntDelegate>(handle, exports[6]);
                ParameterlessIntDelegate localCleanup = LoadDelegate<ParameterlessIntDelegate>(handle, exports[7]);

                Assert.True(atomicSize() > valueSize());
                Assert.Equal(12, localCleanup());

                const int firstSentinel = 0x13579bdf;
                const int secondSentinel = 0x2468ace0;
                write(firstSentinel);
                RunParallel(workerCount, worker =>
                {
                    if ((worker & 1) == 0)
                    {
                        for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                            write((iteration & 1) == 0 ? firstSentinel : secondSentinel);
                        return;
                    }
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                        Assert.Equal(1, isConsistent());
                });

                write(0);
                int winnerCount = 0;
                RunParallel(workerCount, worker =>
                {
                    if (transition(0, worker + 1) == 1) Interlocked.Increment(ref winnerCount);
                });
                Assert.Equal(1, winnerCount);
                Assert.InRange(read(), 1, workerCount);

                int beforeExchange = read();
                Assert.Equal(beforeExchange, exchange(42));
                Assert.Equal(42, read());
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_AtomicOwnershipPublishesSafelyAndBalancesLifetimes(int optimization)
    {
        const int workerCount = 8;
        const int writerCount = workerCount / 2;
        const int iterationsPerWriter = 1_500;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace AtomicOwnershipRuntime;

            struct Counters
            {
                public static atomic<int> Destroyed;
            }

            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource() { Counters.Destroyed++; }
            }

            struct Snapshot
            {
                public shared<Resource> Object;
                public int Generation;
            }

            struct Globals
            {
                public static atomic<shared<Resource>> Current;
                public static atomic<weak<Resource>> Observer;
                public static atomic<Snapshot> Published;
                public static weak<Resource> EmptyWeak;
            }

            export void ResetDestroyed() { Counters.Destroyed = 0; }
            export int ReadDestroyed() { return Counters.Destroyed; }

            export void PublishShared(int id)
            {
                shared<Resource> replacement = new Resource(id);
                Globals.Current = replacement;
            }

            export int ReadShared()
            {
                shared<Resource> snapshot = Globals.Current;
                if (snapshot == null) return 0;
                return snapshot->Id;
            }

            export void ClearShared() { Globals.Current = null; }

            export int ExchangeShared(int id)
            {
                shared<Resource> replacement = new Resource(id);
                Globals.Current <-> replacement;
                if (replacement == null) return 0;
                return replacement->Id;
            }

            export int SharedCasSuccess(int id)
            {
                shared<Resource> expected = Globals.Current;
                shared<Resource> desired = new Resource(id);
                if (Globals.Current : expected --> desired) return 1;
                return 0;
            }

            export int SharedCasFailure(int id)
            {
                shared<Resource> expected = new Resource(-1);
                shared<Resource> desired = new Resource(id);
                if (Globals.Current : expected --> desired) return 1;
                return 0;
            }

            export void PublishWeak(int id)
            {
                shared<Resource> replacement = new Resource(id);
                Globals.Observer = replacement;
                Globals.Current = replacement;
            }

            export int ReadWeak()
            {
                weak<Resource> snapshot = Globals.Observer;
                shared<Resource> strong = lock snapshot;
                if (strong == null) return 0;
                return strong->Id;
            }

            export void ClearWeak()
            {
                weak<Resource> empty = Globals.EmptyWeak;
                Globals.Observer <-> empty;
            }

            export void PublishState(int generation)
            {
                shared<Resource> value = new Resource(generation);
                Snapshot replacement = Snapshot { value, generation };
                Globals.Published = replacement;
            }

            export int IsStateConsistent()
            {
                Snapshot snapshot = Globals.Published;
                if (snapshot.Object == null)
                {
                    if (snapshot.Generation == 0) return 1;
                    return 0;
                }
                if (snapshot.Object->Id == snapshot.Generation) return 1;
                return 0;
            }

            export void ClearState()
            {
                Snapshot empty = Snapshot { null, 0 };
                Globals.Published = empty;
            }

            export int AtomicSharedSize() { return cast<int>(sizeof(atomic<shared<Resource>>)); }
            export int SharedSize() { return cast<int>(sizeof(shared<Resource>)); }

            export int LocalAtomicCleanup()
            {
                Counters.Destroyed = 0;
                {
                    atomic<shared<Resource>> strong = new Resource(1);
                    shared<Resource> strongSnapshot = strong;
                }
                {
                    shared<Resource> owner = new Resource(2);
                    weak<Resource> observer = owner;
                    atomic<weak<Resource>> weakValue = observer;
                    weak<Resource> weakSnapshot = weakValue;
                }
                return Counters.Destroyed;
            }
            """, "atomic-ownership-runtime.xe"));
        string objectPath = Path.Combine(
            directory,
            $"atomic-ownership-runtime{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "atomic-ownership-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "atomic-ownership-runtime", "debug", target.Triple);
        string[] exports =
        [
            "AtomicOwnershipRuntime_ResetDestroyed",
            "AtomicOwnershipRuntime_ReadDestroyed",
            "AtomicOwnershipRuntime_PublishShared",
            "AtomicOwnershipRuntime_ReadShared",
            "AtomicOwnershipRuntime_ClearShared",
            "AtomicOwnershipRuntime_ExchangeShared",
            "AtomicOwnershipRuntime_SharedCasSuccess",
            "AtomicOwnershipRuntime_SharedCasFailure",
            "AtomicOwnershipRuntime_PublishWeak",
            "AtomicOwnershipRuntime_ReadWeak",
            "AtomicOwnershipRuntime_ClearWeak",
            "AtomicOwnershipRuntime_PublishState",
            "AtomicOwnershipRuntime_IsStateConsistent",
            "AtomicOwnershipRuntime_ClearState",
            "AtomicOwnershipRuntime_AtomicSharedSize",
            "AtomicOwnershipRuntime_SharedSize",
            "AtomicOwnershipRuntime_LocalAtomicCleanup",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "atomic-ownership-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                ParameterlessVoidDelegate resetDestroyed = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[0]);
                ParameterlessIntDelegate readDestroyed = LoadDelegate<ParameterlessIntDelegate>(handle, exports[1]);
                Int32VoidDelegate publishShared = LoadDelegate<Int32VoidDelegate>(handle, exports[2]);
                ParameterlessIntDelegate readShared = LoadDelegate<ParameterlessIntDelegate>(handle, exports[3]);
                ParameterlessVoidDelegate clearShared = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[4]);
                Int32IntDelegate exchangeShared = LoadDelegate<Int32IntDelegate>(handle, exports[5]);
                Int32IntDelegate casSuccess = LoadDelegate<Int32IntDelegate>(handle, exports[6]);
                Int32IntDelegate casFailure = LoadDelegate<Int32IntDelegate>(handle, exports[7]);
                Int32VoidDelegate publishWeak = LoadDelegate<Int32VoidDelegate>(handle, exports[8]);
                ParameterlessIntDelegate readWeak = LoadDelegate<ParameterlessIntDelegate>(handle, exports[9]);
                ParameterlessVoidDelegate clearWeak = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[10]);
                Int32VoidDelegate publishState = LoadDelegate<Int32VoidDelegate>(handle, exports[11]);
                ParameterlessIntDelegate isStateConsistent = LoadDelegate<ParameterlessIntDelegate>(handle, exports[12]);
                ParameterlessVoidDelegate clearState = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[13]);
                ParameterlessIntDelegate atomicSharedSize = LoadDelegate<ParameterlessIntDelegate>(handle, exports[14]);
                ParameterlessIntDelegate sharedSize = LoadDelegate<ParameterlessIntDelegate>(handle, exports[15]);
                ParameterlessIntDelegate localAtomicCleanup = LoadDelegate<ParameterlessIntDelegate>(handle, exports[16]);

                Assert.True(atomicSharedSize() > sharedSize());
                Assert.Equal(2, localAtomicCleanup());

                resetDestroyed();
                RunParallel(workerCount, worker =>
                {
                    if (worker < writerCount)
                    {
                        for (int iteration = 0; iteration < iterationsPerWriter; iteration++)
                            publishShared(worker * iterationsPerWriter + iteration + 1);
                        return;
                    }
                    for (int iteration = 0; iteration < iterationsPerWriter; iteration++)
                        Assert.InRange(readShared(), 0, writerCount * iterationsPerWriter);
                });
                clearShared();
                Assert.Equal(writerCount * iterationsPerWriter, readDestroyed());

                resetDestroyed();
                RunParallel(workerCount, worker =>
                {
                    if (worker < writerCount)
                    {
                        for (int iteration = 0; iteration < iterationsPerWriter; iteration++)
                            publishWeak(worker * iterationsPerWriter + iteration + 1);
                        return;
                    }
                    for (int iteration = 0; iteration < iterationsPerWriter; iteration++)
                        Assert.InRange(readWeak(), 0, writerCount * iterationsPerWriter);
                });
                clearShared();
                Assert.Equal(0, readWeak());
                clearWeak();
                Assert.Equal(writerCount * iterationsPerWriter, readDestroyed());

                resetDestroyed();
                clearState();
                RunParallel(workerCount, worker =>
                {
                    if (worker < writerCount)
                    {
                        for (int iteration = 0; iteration < iterationsPerWriter; iteration++)
                            publishState(worker * iterationsPerWriter + iteration + 1);
                        return;
                    }
                    for (int iteration = 0; iteration < iterationsPerWriter; iteration++)
                        Assert.Equal(1, isStateConsistent());
                });
                clearState();
                Assert.Equal(writerCount * iterationsPerWriter, readDestroyed());

                resetDestroyed();
                publishShared(11);
                Assert.Equal(11, exchangeShared(22));
                Assert.Equal(22, readShared());
                Assert.Equal(1, readDestroyed());
                Assert.Equal(0, casFailure(33));
                Assert.Equal(3, readDestroyed());
                Assert.Equal(1, casSuccess(44));
                Assert.Equal(44, readShared());
                Assert.Equal(4, readDestroyed());
                clearShared();
                Assert.Equal(5, readDestroyed());
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_AtomicArraysPreserveElementLayoutAndHandleSemantics(int optimization)
    {
        const int workerCount = 8;
        const int iterationsPerWorker = 5_000;
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            optimizationLevel: optimization,
            positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace AtomicArrayRuntime;

            struct State
            {
                public int First = 7;
                public int Second = 7;
                public int Third = 7;
                public int Fourth = 7;
            }

            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource() { Globals.Destroyed++; }
            }

            struct Globals
            {
                public static atomic<int>[] Counters;
                public static atomic<State>[] States;
                public static atomic<shared<Resource>>[] Owned;
                public static atomic<int[]> Current;
                public static int[] FirstHandle;
                public static int[] SecondHandle;
                public static atomic<int> Destroyed;
            }

            export void SetupCounters(int count) { Globals.Counters = new atomic<int>[count]; }
            export void IncrementCounters(int index)
            {
                Globals.Counters[0]++;
                Globals.Counters[index + 1]++;
            }
            export int ReadCounter(int index) { return Globals.Counters[index]; }
            export void FreeCounters() { free(Globals.Counters); }

            export void SetupStates(int count) { Globals.States = new atomic<State>[count]; }
            export int IsDefaultState(int index)
            {
                State value = Globals.States[index];
                if (value.First != 7) return 0;
                if (value.Second != 7) return 0;
                if (value.Third != 7) return 0;
                if (value.Fourth != 7) return 0;
                return 1;
            }
            export void WriteState(int value)
            {
                Globals.States[0] = State { value, value, value, value };
            }
            export int IsStateConsistent()
            {
                State value = Globals.States[0];
                if (value.First != value.Second) return 0;
                if (value.First != value.Third) return 0;
                if (value.First != value.Fourth) return 0;
                return 1;
            }
            export void FreeStates() { free(Globals.States); }

            export void SetupOwned(int count)
            {
                Globals.Destroyed = 0;
                Globals.Owned = new atomic<shared<Resource>>[count];
                for (int index = 0; index < count; index++)
                    Globals.Owned[index] = new Resource(index);
            }
            export int FreeOwned()
            {
                free(Globals.Owned);
                return Globals.Destroyed;
            }
            export int StackOwned(int count)
            {
                Globals.Destroyed = 0;
                {
                    atomic<shared<Resource>>[] values = atomic<shared<Resource>>[count];
                    for (int index = 0; index < count; index++)
                        values[index] = new Resource(index);
                }
                return Globals.Destroyed;
            }

            export void SetupHandles()
            {
                Globals.FirstHandle = new int[4];
                Globals.SecondHandle = new int[4];
                for (int index = 0; index < 4; index++)
                {
                    Globals.FirstHandle[index] = 11;
                    Globals.SecondHandle[index] = 22;
                }
                Globals.Current = Globals.FirstHandle;
            }
            export void PublishHandle(int which)
            {
                if (which == 1) Globals.Current = Globals.FirstHandle;
                else Globals.Current = Globals.SecondHandle;
            }
            export int IsHandleConsistent()
            {
                int[] snapshot = Globals.Current;
                int first = snapshot[0];
                if (snapshot[1] != first) return 0;
                if (snapshot[2] != first) return 0;
                if (snapshot[3] != first) return 0;
                return 1;
            }
            export int TryFirstToSecond()
            {
                if (Globals.Current : Globals.FirstHandle --> Globals.SecondHandle) return 1;
                return 0;
            }
            export int ExchangeToFirst()
            {
                int[] replacement = Globals.FirstHandle;
                Globals.Current <-> replacement;
                return replacement[0];
            }
            export void FreeHandles()
            {
                free(Globals.FirstHandle);
                free(Globals.SecondHandle);
            }

            export int AtomicStateSize() { return cast<int>(sizeof(atomic<State>)); }
            export int StateSize() { return cast<int>(sizeof(State)); }
            export int AtomicHandleSize() { return cast<int>(sizeof(atomic<int[]>)); }
            export int HandleSize() { return cast<int>(sizeof(int[])); }
            """, "atomic-array-runtime.xe"));
        string objectPath = Path.Combine(
            directory,
            $"atomic-array-runtime{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "atomic-array-runtime", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "atomic-array-runtime", "debug", target.Triple);
        string[] exports =
        [
            "AtomicArrayRuntime_SetupCounters",
            "AtomicArrayRuntime_IncrementCounters",
            "AtomicArrayRuntime_ReadCounter",
            "AtomicArrayRuntime_FreeCounters",
            "AtomicArrayRuntime_SetupStates",
            "AtomicArrayRuntime_IsDefaultState",
            "AtomicArrayRuntime_WriteState",
            "AtomicArrayRuntime_IsStateConsistent",
            "AtomicArrayRuntime_FreeStates",
            "AtomicArrayRuntime_SetupOwned",
            "AtomicArrayRuntime_FreeOwned",
            "AtomicArrayRuntime_StackOwned",
            "AtomicArrayRuntime_SetupHandles",
            "AtomicArrayRuntime_PublishHandle",
            "AtomicArrayRuntime_IsHandleConsistent",
            "AtomicArrayRuntime_TryFirstToSecond",
            "AtomicArrayRuntime_ExchangeToFirst",
            "AtomicArrayRuntime_FreeHandles",
            "AtomicArrayRuntime_AtomicStateSize",
            "AtomicArrayRuntime_StateSize",
            "AtomicArrayRuntime_AtomicHandleSize",
            "AtomicArrayRuntime_HandleSize",
        ];

        try
        {
            Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "atomic-array-runtime");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: exports),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                Int32VoidDelegate setupCounters = LoadDelegate<Int32VoidDelegate>(handle, exports[0]);
                Int32VoidDelegate incrementCounters = LoadDelegate<Int32VoidDelegate>(handle, exports[1]);
                Int32IntDelegate readCounter = LoadDelegate<Int32IntDelegate>(handle, exports[2]);
                ParameterlessVoidDelegate freeCounters = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[3]);
                Int32VoidDelegate setupStates = LoadDelegate<Int32VoidDelegate>(handle, exports[4]);
                Int32IntDelegate isDefaultState = LoadDelegate<Int32IntDelegate>(handle, exports[5]);
                Int32VoidDelegate writeState = LoadDelegate<Int32VoidDelegate>(handle, exports[6]);
                ParameterlessIntDelegate isStateConsistent = LoadDelegate<ParameterlessIntDelegate>(handle, exports[7]);
                ParameterlessVoidDelegate freeStates = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[8]);
                Int32VoidDelegate setupOwned = LoadDelegate<Int32VoidDelegate>(handle, exports[9]);
                ParameterlessIntDelegate freeOwned = LoadDelegate<ParameterlessIntDelegate>(handle, exports[10]);
                Int32IntDelegate stackOwned = LoadDelegate<Int32IntDelegate>(handle, exports[11]);
                ParameterlessVoidDelegate setupHandles = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[12]);
                Int32VoidDelegate publishHandle = LoadDelegate<Int32VoidDelegate>(handle, exports[13]);
                ParameterlessIntDelegate isHandleConsistent = LoadDelegate<ParameterlessIntDelegate>(handle, exports[14]);
                ParameterlessIntDelegate tryFirstToSecond = LoadDelegate<ParameterlessIntDelegate>(handle, exports[15]);
                ParameterlessIntDelegate exchangeToFirst = LoadDelegate<ParameterlessIntDelegate>(handle, exports[16]);
                ParameterlessVoidDelegate freeHandles = LoadDelegate<ParameterlessVoidDelegate>(handle, exports[17]);
                ParameterlessIntDelegate atomicStateSize = LoadDelegate<ParameterlessIntDelegate>(handle, exports[18]);
                ParameterlessIntDelegate stateSize = LoadDelegate<ParameterlessIntDelegate>(handle, exports[19]);
                ParameterlessIntDelegate atomicHandleSize = LoadDelegate<ParameterlessIntDelegate>(handle, exports[20]);
                ParameterlessIntDelegate handleSize = LoadDelegate<ParameterlessIntDelegate>(handle, exports[21]);

                Assert.True(atomicStateSize() > stateSize());
                Assert.Equal(handleSize(), atomicHandleSize());

                setupCounters(workerCount + 1);
                RunParallel(workerCount, worker =>
                {
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                        incrementCounters(worker);
                });
                Assert.Equal(workerCount * iterationsPerWorker, readCounter(0));
                for (int worker = 0; worker < workerCount; worker++)
                    Assert.Equal(iterationsPerWorker, readCounter(worker + 1));
                freeCounters();

                setupStates(4);
                for (int index = 0; index < 4; index++) Assert.Equal(1, isDefaultState(index));
                RunParallel(workerCount, worker =>
                {
                    if ((worker & 1) == 0)
                    {
                        for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                            writeState((worker + 1) * iterationsPerWorker + iteration);
                        return;
                    }
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                        Assert.Equal(1, isStateConsistent());
                });
                freeStates();

                setupOwned(128);
                Assert.Equal(128, freeOwned());
                Assert.Equal(64, stackOwned(64));

                setupHandles();
                RunParallel(workerCount, worker =>
                {
                    if ((worker & 1) == 0)
                    {
                        for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                            publishHandle((iteration & 1) + 1);
                        return;
                    }
                    for (int iteration = 0; iteration < iterationsPerWorker; iteration++)
                        Assert.Equal(1, isHandleConsistent());
                });
                publishHandle(1);
                int winners = 0;
                RunParallel(workerCount, _ =>
                {
                    if (tryFirstToSecond() == 1) Interlocked.Increment(ref winners);
                });
                Assert.Equal(1, winners);
                Assert.Equal(22, exchangeToFirst());
                Assert.Equal(1, isHandleConsistent());
                freeHandles();
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    [Fact]
    public void Linker_ResolvesNamedLibraryFromCustomSearchPath()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        string objectExtension = LlvmTargetPlatform.GetObjectFileExtension(target.Triple);
        string libraryObjectPath = Path.Combine(directory, $"math{objectExtension}");
        string libraryPath = XenonBuildPaths.GetStaticLibraryPath(
            directory, "math", "debug", target.Triple);
        string executableObjectPath = Path.Combine(directory, $"app{objectExtension}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "app", "debug", target.Triple);
        Compilation executableCompilation = CreateExecutableCompilation(SourceText.From("""
            namespace Integration;

            extern int Integration_Add(int left, int right);

            int Main()
            {
                return Integration_Add(20, 22);
            }
            """, "app.xe"));

        try
        {
            LlvmObjectFile libraryObject = new LlvmObjectEmitter().Emit(
                CreateLibraryCompilation(),
                libraryObjectPath,
                target,
                "math"
                );
            new NativeLinker().CreateStaticLibrary(libraryObject.Path, libraryPath, target.Triple);

            LlvmObjectFile executableObject = new LlvmObjectEmitter().Emit(
                executableCompilation,
                executableObjectPath,
                target,
                "app"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                executableObject.Path,
                executablePath,
                target.Triple,
                new NativeLinkOptions(
                    Libraries: ["math"],
                    LibraryPaths: [Path.GetDirectoryName(libraryPath)!]));

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Linker_ExportsStructPointerFunctionWithCLayout(bool hasPolymorphicDescendant)
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(positionIndependentCode: true);
        Compilation compilation = Compilation.Create(SourceText.From("""
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
            export Vector2* Second(Vector2* value) { return &value[1]; }
            """ + (hasPolymorphicDescendant ? """

            interface IValue { float Read(); }
            struct DerivedVector : Vector2, IValue { public float Read() { return X + Y; } }
            export Vector2* Upcast(DerivedVector* value) { return value; }
            export Vector2* Reference(DerivedVector* value) { Vector2& reference = *value; return &reference; }
            """ : ""), "vector.xe"));
        string objectPath = Path.Combine(
            directory,
            $"vector{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string libraryPath = XenonBuildPaths.GetSharedLibraryPath(
            directory, "vector", "debug", target.Triple);
        string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
            directory, "vector", "debug", target.Triple);

        try
        {
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation, objectPath, target, "vector");
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: hasPolymorphicDescendant
                    ? ["Example_Sum", "Example_Second", "Example_Upcast", "Example_Reference"]
                    : ["Example_Sum", "Example_Second"]),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                SumVectorDelegate sum = Marshal.GetDelegateForFunctionPointer<SumVectorDelegate>(
                    NativeLibrary.GetExport(handle, "Example_Sum"));
                var vector = new NativeVector2 { X = 20.0f, Y = 22.0f };
                Assert.Equal(42.0f, sum(ref vector));
                nint storage = Marshal.AllocHGlobal(64);
                try
                {
                    AddressDelegate second = Marshal.GetDelegateForFunctionPointer<AddressDelegate>(NativeLibrary.GetExport(handle, "Example_Second"));
                    Assert.Equal(storage + Marshal.SizeOf<NativeVector2>(), second(storage));
                    if (hasPolymorphicDescendant)
                    {
                        AddressDelegate upcast = Marshal.GetDelegateForFunctionPointer<AddressDelegate>(NativeLibrary.GetExport(handle, "Example_Upcast"));
                        AddressDelegate reference = Marshal.GetDelegateForFunctionPointer<AddressDelegate>(NativeLibrary.GetExport(handle, "Example_Reference"));
                        Assert.Equal(storage, upcast(storage));
                        Assert.Equal(storage, reference(storage));
                        Assert.Equal(nint.Zero, upcast(nint.Zero));
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(storage);
                }
            }
            finally
            {
                NativeLibrary.Free(handle);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_PreservesStructValueCopiesOnStackAndAcrossCalls()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            struct Pair
            {
                public int X;
                public int Y;
            }

            Pair ChangeCopy(Pair value)
            {
                value.X = 40;
                return value;
            }

            int ReadX(Pair* value)
            {
                return value->X;
            }

            int Main()
            {
                Pair original = Pair { 20, 2 };

                Pair copy = original;
                copy.Y = 22;

                Pair returned = ChangeCopy(copy);
                return original.Y + ReadX(&returned);
            }
            """, "struct-copy.xe"));
        string objectPath = Path.Combine(
            directory,
            $"struct-copy{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "struct-copy", "debug", target.Triple);

        try
        {
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "struct-copy"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ConstructsStructsOnStackAndHeapAndFreesAllocation()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            struct Vector3
            {
                public int X;
                public int Y;
                public int Z;
            }

            int Main()
            {
                Vector3 stack = Vector3 { 10, 12, 20 };
                Vector3* heap = new Vector3 { stack.X, stack.Y, stack.Z };
                int result = heap->X + heap->Y + heap->Z;
                free(heap);
                return result;
            }
            """, "heap-struct.xe"));
        string objectPath = Path.Combine(
            directory,
            $"heap-struct{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "heap-struct", "debug", target.Triple);

        try
        {
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "heap-struct"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ResolvesAbstractVTableSlotsThroughConcreteOverride()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
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
                Entity& entity = enemy;
                return entity.Score();
            }
            """, "abstract-link.xe"));
        string objectPath = Path.Combine(
            directory,
            $"abstract-link{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "abstract-link", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "abstract-link"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ProjectsDerivedInterfaceReferenceToCorrectBaseTable()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
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
                IC& ic = value;
                IB& ib = ic;
                return ib.B();
            }
            """, "interface-reference-upcast.xe"));
        string objectPath = Path.Combine(
            directory,
            $"interface-reference-upcast{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "interface-reference-upcast", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "interface-reference-upcast"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(20, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ExecutesReadonlyFieldReferenceAndMethodFlow()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            struct Counter
            {
                readonly int Value;

                public Counter(int value)
                {
                    Value = value;
                }

                public int readonly Read()
                {
                    return Value;
                }
            }

            int Main()
            {
                readonly Counter counter = Counter(42);
                readonly Counter& reference = counter;
                return reference.Read();
            }
            """, "readonly-flow.xe"));
        string objectPath = Path.Combine(
            directory,
            $"readonly-flow{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "readonly-flow", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "readonly-flow"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_SelectsReadonlyAwareMethodOverloads()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
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
                int& writable = mutable.Get();
                writable = 42;
                readonly Container& readOnly = mutable;
                readonly int& readable = readOnly.Get();
                return readable;
            }
            """, "readonly-overloads.xe"));
        string objectPath = Path.Combine(
            directory,
            $"readonly-overloads{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "readonly-overloads", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "readonly-overloads"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ExecutesInstanceFieldInitializersAfterBaseConstruction()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            int Offset()
            {
                return 2;
            }

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
                readonly int Result = Stage + Offset();
                int Marker = 7;

                public Derived()
                {
                    Marker = Marker + 1;
                }

                public int readonly ReadResult()
                {
                    return Result;
                }

                public int readonly ReadMarker()
                {
                    return Marker;
                }
            }

            int Main()
            {
                Derived value = Derived();
                if (value.ReadMarker() != 8)
                    return 1;
                return value.ReadResult();
            }
            """, "field-initializers.xe"));
        string objectPath = Path.Combine(
            directory,
            $"field-initializers{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "field-initializers", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "field-initializers"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ExecutesFieldInitializersForPositionalAndDefaultBaseConstruction()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            struct Value
            {
                int Seed = 20;
                public readonly int Number = Seed + 2;
            }

            struct Base
            {
                public readonly int Number = 20;
            }

            struct Derived : Base
            {
                public Derived()
                    : base()
                {
                }
            }

            int Main()
            {
                Value first = Value { };
                Derived second = Derived();
                return first.Number + second.Number;
            }
            """, "default-field-initializers.xe"));
        string objectPath = Path.Combine(
            directory,
            $"default-field-initializers{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "default-field-initializers", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "default-field-initializers"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ExecutesPropertyGetterAndSetter()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
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
                player.Health = 42;
                return player.Health;
            }
            """, "properties.xe"));
        string objectPath = Path.Combine(
            directory,
            $"properties{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "properties", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "properties"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_DispatchesVirtualPropertyAccessors()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            struct Base
            {
                public int stored;

                public virtual int Value
                {
                    get { return stored; }
                    set { stored = value; }
                }
            }

            struct Derived : Base
            {
                public int adjusted;

                public override int Value
                {
                    get { return adjusted + 1; }
                    set { adjusted = value + 1; }
                }
            }

            int Main()
            {
                Derived derived = Derived { 0, 0 };
                Base* value = &derived;
                value->Value = 40;
                return value->Value;
            }
            """, "virtual-properties.xe"));
        string objectPath = Path.Combine(
            directory,
            $"virtual-properties{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "virtual-properties", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "virtual-properties"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ExecutesCompoundVirtualPropertyAssignmentsAndEvaluatesReceiverOnce()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            struct Base
            {
                public int stored;

                public virtual int Value
                {
                    get { return stored; }
                    set { stored = value; }
                }
            }

            struct Derived : Base
            {
                public int adjusted;

                public override int Value
                {
                    get { return adjusted; }
                    set { adjusted = value; }
                }
            }

            struct Probe
            {
                public static int Calls;

                public static Base* Get(Base* value)
                {
                    Probe.Calls += 1;
                    return value;
                }
            }

            int Main()
            {
                Derived derived = Derived { 0, 1 };
                Base* view = &derived;
                Probe.Get(view)->Value += 50;
                view->Value -= 10;
                return view->Value + Probe.Calls;
            }
            """, "compound-virtual-properties.xe"));
        string objectPath = Path.Combine(
            directory,
            $"compound-virtual-properties{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "compound-virtual-properties", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "compound-virtual-properties"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_DispatchesInterfacePropertyAccessors()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            interface IValue
            {
                int Value { get; set; }
            }

            struct Box : IValue
            {
                public int stored;

                public int Value
                {
                    get { return stored; }
                    set { stored = value; }
                }
            }

            int Main()
            {
                Box box = Box { 0 };
                IValue value = box;
                value.Value = 42;
                return value.Value;
            }
            """, "interface-properties.xe"));
        string objectPath = Path.Combine(
            directory,
            $"interface-properties{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "interface-properties", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "interface-properties"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_DispatchesMultiParameterInterfaceIndexer()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            interface IGrid
            {
                int this[int x, int y] { get; set; }
            }

            struct Grid : IGrid
            {
                public int stored;

                public int this[int x, int y]
                {
                    get { return stored + x + y; }
                    set { stored = value - x - y; }
                }
            }

            int Main()
            {
                Grid concrete = Grid { 0 };
                IGrid grid = concrete;
                grid[4, 7] = 42;
                return grid[4, 7];
            }
            """, "interface-indexers.xe"));
        string objectPath = Path.Combine(
            directory,
            $"interface-indexers{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "interface-indexers", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "interface-indexers"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_ExecutesCompoundInterfaceIndexerAssignmentAndEvaluatesArgumentsOnce()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            interface IGrid
            {
                int Value { get; set; }
                int this[int x, int y] { get; set; }
            }

            struct Grid : IGrid
            {
                public int stored;

                public int Value
                {
                    get { return stored; }
                    set { stored = value; }
                }

                public int this[int x, int y]
                {
                    get { return stored + x + y; }
                    set { stored = value - x - y; }
                }
            }

            struct Probe
            {
                public static int Calls;

                public static int Next()
                {
                    Probe.Calls += 1;
                    return Probe.Calls;
                }
            }

            int Main()
            {
                Grid concrete = Grid { 0 };
                IGrid grid = concrete;
                grid.Value += 10;
                grid.Value -= 5;
                grid[Probe.Next(), Probe.Next()] += 35;
                return grid.Value + Probe.Calls;
            }
            """, "compound-interface-indexers.xe"));
        string objectPath = Path.Combine(
            directory,
            $"compound-interface-indexers{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory, "compound-interface-indexers", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "compound-interface-indexers"
                );
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path, executablePath, target.Triple);

            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = executable.Path,
                UseShellExecute = false,
            })!;
            process.WaitForExit();
            Assert.Equal(42, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_InlinesModuleAndStructConstants()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            const int A = 4;
            const int B = A * 2;
            const int C = B + A;

            struct Values
            {
                const int Factor = 3;
            }

            int Main() { return C * Values.Factor; }
            """, "constants.xe"));
        string objectPath = Path.Combine(directory, $"constants{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(directory, "constants", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(compilation, objectPath, target, "constants");
            LinkedExecutable executable = new NativeLinker().LinkExecutable(objectFile.Path, executablePath, target.Triple);
            using Process process = Process.Start(new ProcessStartInfo { FileName = executable.Path, UseShellExecute = false })!;
            process.WaitForExit();
            Assert.Equal(36, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Linker_EvaluatesLayoutAndCastConstantExpressions()
    {
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(
            positionIndependentCode: !OperatingSystem.IsWindows());
        Compilation compilation = CreateExecutableCompilation(SourceText.From("""
            namespace Example;

            struct Pair
            {
                int First;
                long Second;
            }

            const int PairSize = sizeof(Pair);
            const int PairAlignment = alignof(Pair);
            const int SecondOffset = offsetof(Pair, Second);
            const int Narrowed = cast<int>(cast<long>(6));

            int Main()
            {
                return PairSize + PairAlignment + SecondOffset + Narrowed;
            }
            """, "layout-constants.xe"));
        string objectPath = Path.Combine(directory, $"layout-constants{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(directory, "layout-constants", "debug", target.Triple);

        try
        {
            Assert.Empty(compilation.Diagnostics);
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(compilation, objectPath, target, "layout-constants");
            LinkedExecutable executable = new NativeLinker().LinkExecutable(objectFile.Path, executablePath, target.Triple);
            using Process process = Process.Start(new ProcessStartInfo { FileName = executable.Path, UseShellExecute = false })!;
            process.WaitForExit();
            Assert.Equal(38, process.ExitCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyFreeFunctionsWithExplicitMutableCapabilities(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            extern int readonly abs(int value);
            int readonly Magnitude(int value) { return abs(value); }
            int readonly Square(int value) { return value * value; }
            export void readonly Fill(int* output, readonly int* input)
            {
                *output = *input + Square(1);
            }
            void readonly Increment(int& value) { value++; }
            void readonly Store(int** destination, int* source) { *destination = source; }
            int* readonly Identity(int* value) { return value; }
            struct Reader
            {
                public int Offset;
                public void readonly Copy(int* output, readonly int* input)
                {
                    Fill(output, input);
                    *output += Offset;
                }
                public static int readonly Read(readonly int* value) { return *value; }
            }
            int Main()
            {
                int input = Magnitude(-40);
                int output = 0;
                int* pointer = &input;
                Store(&pointer, &output);
                Reader reader = Reader { 0 };
                reader.Copy(Identity(pointer), &input);
                Increment(output);
                readonly int* readonly view = pointer;
                return Reader.Read(view);
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyContextualInstanceMethods(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Value = 7; }
            struct Counter
            {
                public int Value;
                public void Increment() { Add(1); }
                void Add(int amount) { Value += amount; }
            }
            struct Writer
            {
                public int* Hidden;
                public int* Output;
                public void Rewrite(int* output)
                {
                    Output = &State.Value;
                    Output = output;
                    Write();
                }
                void Write() { *Output = 40; }
            }
            void readonly Run(Counter& counter) { counter.Increment(); }
            void readonly RunPointer(Counter* counter) { counter->Increment(); }
            int readonly Local()
            {
                Counter counter = Counter();
                Writer writer = Writer { &State.Value, &State.Value };
                writer.Rewrite(&counter.Value);
                counter.Increment();
                return counter.Value;
            }
            int Main()
            {
                Counter counter = Counter();
                Run(counter);
                if (counter.Value != 1) return 1;
                RunPointer(&counter);
                if (counter.Value != 2) return 2;
                int result = Local();
                if (State.Value != 7) return 3;
                return result + 1;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyContextualDisposeAndDispatch(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            interface IDisposable { void Dispose(); }
            struct Payload
            {
                public int* Trace;
                public ~Payload() { *Trace += 1; }
            }
            struct Resource : IDisposable
            {
                public Payload* Memory;
                public void Dispose() { Release(); }
                void Release() { free(Memory); Memory = null; }
            }
            struct Base
            {
                public int* Trace;
                public virtual void Increment() { *Trace += 1; }
            }
            struct Derived : Base
            {
                public override void Increment() { *Trace += 40; }
            }
            void readonly Destroy(Resource& resource) { resource.Dispose(); }
            void readonly DestroyInterface(IDisposable& resource) { resource.Dispose(); }
            void readonly Increment(Base& value) { value.Increment(); }
            int readonly Run()
            {
                int trace = 0;
                Resource local = Resource { new Payload { &trace } };
                Destroy(local);
                if (local.Memory != null || trace != 1) return 1;
                local.Memory = new Payload { &trace };
                local.Dispose();
                if (local.Memory != null || trace != 2) return 2;
                local.Memory = new Payload { &trace };
                DestroyInterface(local);
                if (local.Memory != null || trace != 3) return 3;
                Derived derived = Derived();
                derived.Trace = &trace;
                Increment(derived);
                return trace - 1;
            }
            int Main() { return Run(); }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyConcreteVirtualAndInterfaceDispatch(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Value = 7; }
            interface IReset { void Reset(); }
            struct Base : IReset
            {
                public int Value;
                public virtual void Reset() { Value = 10; }
            }
            struct Good : Base { public override void Reset() { Value = 20; } }
            struct Evil : Base { public override void Reset() { State.Value++; } }
            int readonly Run()
            {
                Base value = Base();
                Base& reference = value;
                reference.Reset();
                if (value.Value != 10) return 1;
                Base copy = value;
                copy.Reset();
                if (copy.Value != 10) return 2;
                Good good = Good();
                Base* pointer = &good;
                pointer->Reset();
                if (good.Value != 20) return 3;
                IReset directView = good;
                IReset& viewAlias = directView;
                viewAlias.Reset();
                if (good.Value != 20) return 4;
                Good derived = Good();
                Base* basePointer = &derived;
                IReset baseView = *basePointer;
                baseView.Reset();
                // Conversion through Base preserves the runtime implementation.
                if (derived.Value != 20 || State.Value != 7) return 5;
                Base* heap = new Base();
                heap->Reset();
                int result = heap->Value;
                free(heap);
                if (result != 10) return 6;
                return 42;
            }
            int Main()
            {
                int result = Run();
                Evil evil = Evil();
                Base* pointer = &evil;
                IReset view = *pointer;
                view.Reset();
                if (State.Value != 8) return 7;
                return result;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsRuntimeInterfaceMapsAndAccessors(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            interface IValue
            {
                int Read();
                int Current { get; set; }
                int this[int index] { get; set; }
            }
            struct Base : IValue
            {
                public int Value;
                public virtual int Read() { return Value; }
                public virtual int Current { get { return Value; } set { Value = value; } }
                public virtual int this[int index] { get { return Value; } set { Value = value; } }
            }
            struct Derived : Base
            {
                public override int Read() { return Value + 1; }
                public override int Current { get { return Value + 2; } set { Value = value + 3; } }
                public override int this[int index] { get { return Value + index; } set { Value = value + index; } }
            }
            interface IPlain { int Read(); }
            struct Plain : IPlain { public int Read() { return 1; } }
            struct PlainDerived : Plain { public int Read() { return 42; } }
            struct Nested { public Plain Value; }
            struct Globals { public static Nested Value; }
            int ViaReference(Base& value) { IValue view = value; return view.Read(); }
            int Main()
            {
                Derived derived = Derived();
                Base* pointer = &derived;
                IValue view = *pointer;
                view.Current = 7;
                if (derived.Value != 10 || view.Current != 12 || ViaReference(derived) != 11) return 1;
                view[2] = 20;
                if (derived.Value != 22 || view[3] != 25) return 2;
                PlainDerived plain = PlainDerived();
                Plain* plainPointer = &plain;
                IPlain plainView = *plainPointer;
                if (plainView.Read() != 42) return 3;
                Nested nested = Nested();
                IPlain nestedView = nested.Value;
                if (nestedView.Read() != 1) return 4;
                IPlain staticView = Globals.Value.Value;
                if (staticView.Read() != 1) return 5;
                Nested[] values = Nested[1];
                IPlain arrayView = values[0].Value;
                return plainView.Read() + arrayView.Read() - 1;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyLoopArrayAndRecursivePrecision(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Value = 7; }
            struct Base { public int Value; public virtual void Reset() { Value = 1; } }
            struct Evil : Base { public override void Reset() { State.Value++; } }
            struct Data { public int* Pointer; }
            struct Recursive
            {
                public int* Hidden;
                public int* Output;
                public void A(int n) { if (n > 0) B(n - 1); *Output += 1; }
                void B(int n) { C(n); }
                void C(int n) { A(n); }
            }
            int readonly Run()
            {
                for (int i = 0; i < 2; i++)
                {
                    Base value = Base();
                    value.Reset();
                    if (value.Value != 1) return 1;
                }
                int output = 0;
                Data[] heap = new Data[2];
                heap[0].Pointer = &State.Value;
                heap[1].Pointer = &output;
                *heap[1].Pointer = 10;
                free(heap);
                if (output != 10 || State.Value != 7) return 2;
                Data[,] stack = Data[2, 2];
                stack[0, 1].Pointer = &State.Value;
                stack[1, 0].Pointer = &output;
                *stack[1, 0].Pointer = 39;
                Recursive recursive = Recursive { &State.Value, &output };
                recursive.A(2);
                if (State.Value != 7) return 3;
                return output;
            }
            int Main() { return Run(); }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyFlowStrongUpdates(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Value = 7; }
            struct Data { public int* Output; }
            struct Outer { public Data Inner; }
            void readonly Fill(bool choose, int key, int* output, int* other)
            {
                int* ptr = &State.Value;
                int** alias = &ptr;
                ptr = output;
                **alias = 10;
                Data source = Data();
                source.Output = output;
                Data destination = Data();
                destination.Output = &State.Value;
                destination = source;
                *destination.Output += 1;
                Outer nested = Outer();
                nested.Inner.Output = &State.Value;
                nested.Inner.Output = other;
                *nested.Inner.Output = 20;
                if (choose) ptr = output;
                else ptr = other;
                *ptr += 1;
                for (int i = 0; i < 3; i++)
                {
                    ptr = &State.Value;
                    ptr = output;
                    *ptr += 1;
                }
                while (true)
                {
                    ptr = &State.Value;
                    ptr = other;
                    break;
                }
                *ptr += 1;
                switch (key)
                {
                    case 0:
                    case 1: ptr = output; break;
                    default: ptr = other; break;
                }
                *ptr += 1;
            }
            int Main()
            {
                int output = 0;
                int other = 0;
                Fill(true, 0, &output, &other);
                if (output != 16 || other != 21) return 1;
                Fill(false, 2, &output, &other);
                if (output != 14 || other != 23 || State.Value != 7) return 2;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyCleanupWithCurrentFlowState(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Value = 7; }
            struct Item
            {
                public int** Slot;
                public ~Item() { **Slot += 1; }
            }
            void readonly Clean(int* output, bool stop)
            {
                int* ptr = &State.Value;
                {
                    Item[] items = Item[1];
                    items[0].Slot = &ptr;
                    ptr = output;
                    if (stop) return;
                }
                while (true)
                {
                    ptr = &State.Value;
                    Item[] items = Item[1];
                    items[0].Slot = &ptr;
                    ptr = output;
                    break;
                }
                for (int i = 0; i < 2; i++)
                {
                    ptr = &State.Value;
                    Item[] items = Item[1];
                    items[0].Slot = &ptr;
                    ptr = output;
                    continue;
                }
            }
            int Main()
            {
                int value = 38;
                Clean(&value, false);
                if (value != 42) return 1;
                value = 41;
                Clean(&value, true);
                if (value != 42 || State.Value != 7) return 2;
                return value;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyIndependentFieldProvenance(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Value = 7; }
            struct Pair
            {
                public int* Hidden;
                public int* Output;
                public readonly int* Input;
                public Pair(int* hidden, int* output, readonly int* input)
                {
                    Hidden = hidden;
                    Output = output;
                    Input = input;
                }
                public int* Destination
                {
                    get { return Output; }
                    set { Output = value; }
                }
                public ~Pair() { *Output += 1; }
            }
            struct Outer { public Pair Left; public Pair Right; }
            void readonly Process(int* output, readonly int* input)
            {
                Pair local = Pair(&State.Value, output, input);
                *local.Output = *local.Input;
                Outer value = Outer { Pair(&State.Value, output, input), local };
                Outer copy = value;
                Pair& alias = copy.Right;
                alias.Destination = output;
                *alias.Destination += 1;
                Pair* heap = new Pair(&State.Value, output, input);
                free(heap);
                {
                    Pair[] items = Pair[2];
                    items[0].Hidden = &State.Value;
                    items[0].Output = output;
                    items[1].Hidden = &State.Value;
                    items[1].Output = output;
                }
            }
            int Main()
            {
                int input = 38;
                int output = 0;
                Process(&output, &input);
                if (State.Value != 7 || input != 38) return 1;
                // Accessor + heap + two array elements + scalar local + four
                // compiler-destroyed Pair fields in value/copy.
                if (output != 47) return 2;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyLocalStructsAndAccessors(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Inner { public int Value; public int* Pointer; }
            struct Data
            {
                public Inner Inner;
                public int Current
                {
                    get { return Inner.Value; }
                    set { Inner.Value = value; }
                }
                public int this[int x, int y]
                {
                    get { return Inner.Value + x + y; }
                    set { Inner.Value = value - x - y; }
                }
            }
            Data readonly Build(int* output)
            {
                Data result = Data();
                result.Inner.Pointer = output;
                result.Current = 30;
                result.Current += 2;
                result[2, 3] += 5;
                *result.Inner.Pointer = result[2, 3];
                return result;
            }
            int readonly Run()
            {
                Data initial = Data();
                if (initial.Inner.Value != 0 || initial.Inner.Pointer != null) return 1;
                Data* heap = new Data();
                if (heap->Inner.Value != 0 || heap->Inner.Pointer != null) return 2;
                free(heap);
                int output = 0;
                Data result = Build(&output);
                Data& alias = result;
                if (alias.Current != 37) return 3;
                return output;
            }
            int Main() { return Run(); }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyLifecycleWithExplicitResources(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Resource
            {
                public int* Trace;
                public int Id = 1;
                public Resource(int* trace, int id) { Trace = trace; Id = id; }
                public ~Resource() { *Trace = *Trace * 10 + Id; }
            }
            void readonly Destroy(Resource* resource) { free(resource); }
            int readonly Run()
            {
                int trace = 0;
                Resource* resource = new Resource(&trace, 4);
                Destroy(resource);
                resource = new Resource(&trace, 2);
                free(resource);
                if (trace != 42) return 1;
                trace = 0;
                {
                    Resource[,] values = Resource[1, 2];
                    values[0, 0].Trace = &trace;
                    values[0, 0].Id = 2;
                    values[0, 1].Trace = &trace;
                    values[0, 1].Id = 4;
                }
                if (trace != 42) return 2;
                trace = 0;
                Resource[] values = new Resource[2];
                values[0].Trace = &trace;
                values[0].Id = 2;
                values[1].Trace = &trace;
                values[1].Id = 4;
                free(values);
                return trace;
            }
            int Main() { return Run(); }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyVirtualLifecycleAndAccessors(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            interface IValue { int Current { get; set; } }
            struct Base
            {
                public int* Trace;
                public Base(int* trace) { Trace = trace; }
                public virtual ~Base() { *Trace = *Trace * 10 + 2; }
            }
            struct Data : Base, IValue
            {
                public int Value;
                public Data(int* trace) : base(trace) { Value = 0; }
                public override ~Data() { *Trace = *Trace * 10 + 4; }
                public int Current
                {
                    get { return Value; }
                    set { Value = value; }
                }
            }
            void readonly Set(IValue& value) { value.Current = 42; }
            void readonly Destroy(Base* value) { free(value); }
            int readonly Run()
            {
                int trace = 0;
                Data* data = new Data(&trace);
                Set(*data);
                if (data->Current != 42) return 1;
                Destroy(data);
                return trace;
            }
            int Main() { return Run(); }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsReadonlyMethodQualifiersAndPointerReturns(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            interface IValue
            {
                readonly int* readonly GetView();
                int readonly Read();
                void readonly Update();
            }
            struct Value : IValue
            {
                public int* Pointer;
                public int Count;
                public readonly int* Touch() { Count++; return &Count; }
                public int* readonly GetPointer(int* pointer) { return pointer; }
                public readonly int* readonly GetView() { return Pointer; }
                public int readonly Read() { return Count; }
                public void readonly Update() { int snapshot = Count; }
            }
            abstract struct Base { public abstract int readonly Score(); }
            struct Derived : Base { public override int readonly Score() { return 42; } }
            int Main()
            {
                int number = 10;
                Value value = Value { &number, 0 };
                readonly int* view = value.Touch();
                if (*view != 1) return 1;
                readonly Value& receiver = value;
                int* pointer = receiver.GetPointer(&number);
                *pointer = 40;
                pointer = &number;
                view = receiver.GetView();
                if (*view != 40) return 2;
                readonly IValue& contract = value;
                contract.Update();
                Derived derived = Derived { };
                readonly Base& baseView = derived;
                if (baseView.Score() != 42) return 3;
                return *contract.GetView() + contract.Read() + 1;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ExecutesSharedSwitchBodyOnceForEveryGroupedLabel(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Probe
            {
                public static int Calls = 0;
                public static int Destroyed = 0;
                public static void Handle() { Probe.Calls++; }
                public ~Probe() { Probe.Destroyed++; }
            }
            int Group(int value)
            {
                int result;
                switch (value)
                {
                    case 1:
                    case 2:
                    case 3:
                        Probe[] temporary = Probe[1];
                        Probe.Handle();
                        result = 10;
                        break;
                    default: return 0;
                }
                return result;
            }
            int SharedReturn(int value)
            {
                switch (value)
                {
                    case 1:
                    case 2: return 3;
                    default: return 0;
                }
            }
            int Main()
            {
                int total = Group(1) + Group(2) + Group(3) + Group(9);
                if (Probe.Calls != 3 || Probe.Destroyed != 3) return 1;
                return total + SharedReturn(1) + SharedReturn(2) + SharedReturn(9) + Probe.Calls + Probe.Destroyed;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsIterationFourEnumsSwitchAndReadonlyPointers(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            const int Start = 10;
            enum State : byte { Idle, Running = Start, Stopped }
            enum Signed : sbyte { Negative = -1 }
            int Select(State value)
            {
                switch (value)
                {
                    case State.Idle: return 0;
                    case State.Running: return 20;
                    default: return 30;
                }
            }
            int Main()
            {
                int a = 1;
                int b = 2;
                readonly int* p = &a;
                p = &b;
                int* readonly fixed = &a;
                *fixed = 3;
                readonly int* readonly both = &b;
                int result = 0;
                for (int i = 0; i < 4; i++)
                {
                    switch (i)
                    {
                        case 0: continue;
                        case 1:
                        case 2: result += i; break;
                        default:
                            switch (i) { case 3: result += 4; break; default: break; }
                            break;
                    }
                    result += 1;
                }
                return result + Select(cast<State>(10)) + cast<int>(State.Stopped) + cast<int>(Signed.Negative) + a - *p + *both - 1;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsRectangularJaggedAndStackArrayMetadata(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            int[,] Create(int rows, int columns) { return new int[rows, columns]; }
            int Inspect(int[,] values) { return values.Length + values.GetLength(0) + values.GetLength(1) + values.Rank; }
            int Main()
            {
                int[,] matrix = Create(2, 3);
                int n = 0;
                for (int i = 0; i < 2; i++)
                    for (int j = 0; j < 3; j++) { matrix[i,j] = n; n++; }
                int* first = &matrix[0,0];
                if (first[4] != matrix[1,1]) return 1;
                int[][,] matrices = new int[2][,];
                matrices[0] = matrix;
                int[,] alias = matrices[0];
                free(matrices);
                if (alias[1,2] != 5) return 2;
                int[][] rows = new int[2][];
                rows[0] = new int[3]; rows[1] = new int[5]; rows[1][4] = 7;
                int[,] stack = int[2,2];
                if (stack.Rank != 2 || stack.GetLength(1) != 2) return 3;
                int[] empty = new int[0];
                int result = Inspect(alias) + rows.Length + rows[0].Length + rows[1].Length + rows[1][4] + stack.Length + empty.Length + alias[1,2];
                free(empty); free(alias); free(rows[0]); free(rows[1]); free(rows);
                return result + 3;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_DestroysArrayElementsOnceInReverseOrder(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Counter
            {
                public static int Trace = 0;
                public static int Count = 0;
                public int Id = 7;
                public ~Counter() { Counter.Trace = Counter.Trace * 10 + Id; Counter.Count += 1; }
            }
            int Main()
            {
                Counter[,] values = new Counter[2, 2];
                if (values[0,0].Id != 7) return 1;
                values[0,0].Id = 1; values[0,1].Id = 2; values[1,0].Id = 3; values[1,1].Id = 4;
                free(values);
                Counter[] empty = new Counter[0]; free(empty);
                if (Counter.Trace != 4321 || Counter.Count != 4) return 2;
                Counter[][] nested = new Counter[1][];
                Counter[] child = new Counter[1]; child[0].Id = 5;
                nested[0] = child;
                free(nested);
                if (Counter.Count != 4) return 3;
                free(child);
                if (Counter.Trace != 43215 || Counter.Count != 5) return 4;
                return 42;
            }
            """, optimization));
    }

    [Fact]
    public void Linker_RunsHighRankDeepNestingAndLargeIndexers()
    {
        string suffix = "[" + new string(',', 39) + "]";
        string dimensions = string.Join(",", Enumerable.Repeat("1", 40));
        string indices = string.Join(",", Enumerable.Repeat("0", 40));
        string parameters = string.Join(",", Enumerable.Range(0, 16).Select(i => $"int p{i}"));
        string arguments = string.Join(",", Enumerable.Range(0, 16));
        string nested = string.Concat(Enumerable.Repeat("[]", 24));
        Assert.Equal(42, RunIterationFourProgram($$"""
            struct Grid { public int this[{{parameters}}] { get { return p15; } set { } } }
            int Main()
            {
                int{{suffix}} values = new int[{{dimensions}}];
                int{{suffix}} stack = int[{{dimensions}}];
                stack[{{indices}}] = 42;
                if (stack.Rank != 40 || stack.GetLength(39) != 1 || stack.Length != 1 || stack[{{indices}}] != 42) return 4;
                values[{{indices}}] = 42;
                if (values.Rank != 40 || values.GetLength(39) != 1 || values.Length != 1) return 1;
                int result = values[{{indices}}];
                int{{nested}} deep = new int[1]{{nested[2..]}};
                if (deep.Length != 1 || deep.Rank != 1) return 2;
                Grid grid = Grid {};
                if (grid[{{arguments}}] != 15) return 3;
                grid[{{arguments}}] = 12;
                free(deep); free(values); return result;
            }
            """, 2));
    }

    [Fact]
    public void Linker_PreservesZeroSizedDimensionsAndEvaluatesIndicesOnce()
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Counter
            {
                public static int Value = 0;
                public static int Next() { Counter.Value += 1; return Counter.Value; }
            }
            int Main()
            {
                int[,,] empty = new int[2147483647,2147483647,0];
                if (empty.Length != 0 || empty.GetLength(1) != 2147483647) return 1;
                free(empty);
                int[,] values = new int[Counter.Next(), Counter.Next()];
                if (values.Length != 2 || Counter.Value != 2) return 2;
                Counter.Value = -1;
                values[Counter.Next(), Counter.Next()] = 42;
                if (Counter.Value != 1) return 3;
                int result = values[0,1];
                free(values);
                int[][] nested = new int[1][];
                free(nested[0]);
                free(nested);
                return result;
            }
            """, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsTargetDependentEnumAndCaseConstants(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Packet { public byte Tag; public nint Payload; }
            const int PayloadOffset = cast<int>(offsetof(Packet, Payload));
            enum Layout { Size = cast<int>(sizeof(Packet)), After, Offset = PayloadOffset, Alignment = cast<int>(alignof(Packet)) }
            enum Native : nint { Size = cast<nint>(sizeof(nint)), Next }
            int Main()
            {
                if (cast<int>(Layout.Size) != cast<int>(sizeof(Packet))) return 1;
                if (cast<int>(Layout.After) != cast<int>(sizeof(Packet)) + 1) return 2;
                if (cast<int>(Layout.Offset) != cast<int>(offsetof(Packet, Payload))) return 3;
                if (cast<int>(Layout.Alignment) != cast<int>(alignof(Packet))) return 4;
                if (cast<int>(Native.Next) != cast<int>(sizeof(nint)) + 1) return 5;
                const int Size = cast<int>(sizeof(Packet));
                const int Next = Size + 1;
                switch (cast<int>(Layout.After)) { case Next: break; default: return 6; }
                switch (Layout.Size) { case Layout.Size: break; default: return 7; }
                switch (sizeof(nint)) { case alignof(nint): break; default: return 8; }
                const nuint High = cast<nuint>(4294967296);
                switch (High) { case cast<nuint>(4294967296): break; default: return 9; }
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_DestroysStackArraysByAllocationInReverseOrder(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Item
            {
                public static int Trace = 0;
                public static int Count = 0;
                public int Id = 7;
                public Item() { Item.Count += 100; }
                public ~Item() { Item.Trace = Item.Trace * 10 + Id; Item.Count += 1; }
            }
            void Nested()
            {
                Item[,] outer = Item[1,2];
                if (Item.Count != 0 || outer[0,0].Id != 7) { Item.Count = -100; return; }
                outer[0,0].Id = 1; outer[0,1].Id = 2;
                {
                    Item[] inner = Item[1]; inner[0].Id = 3;
                    Item[] alias = inner;
                    inner = Item[1]; inner[0].Id = 4;
                    alias[0].Id = 3;
                    Item[,] empty = Item[0,3];
                    if (empty.Length != 0 || empty.Rank != 2) Item.Count = -100;
                }
                if (Item.Trace != 43 || Item.Count != 2) Item.Count = -100;
            }
            int Early()
            {
                Item[] values = Item[1]; values[0].Id = 5;
                return Item.Trace;
            }
            int NestedEarly()
            {
                Item[] outer = Item[1]; outer[0].Id = 6;
                {
                    Item[] inner = Item[1]; inner[0].Id = 7;
                    return inner[0].Id;
                }
            }
            void HeapReplacement()
            {
                Item[] values = Item[1]; values[0].Id = 8;
                values = new Item[1]; values[0].Id = 9;
                free(values);
            }
            int Main()
            {
                Nested();
                if (Item.Trace != 4321 || Item.Count != 4) return 1;
                int beforeCleanup = Early();
                if (beforeCleanup != 4321 || Item.Trace != 43215 || Item.Count != 5) return 2;
                if (NestedEarly() != 7 || Item.Trace != 4321576 || Item.Count != 7) return 3;
                Item.Trace = 0;
                HeapReplacement();
                if (Item.Trace != 98 || Item.Count != 9) return 4;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_CleansStackArraysAcrossLoopAndSwitchExits(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Item
            {
                public static int Trace = 0;
                public int Id = 1;
                public ~Item() { Item.Trace = Item.Trace * 10 + Id; }
            }
            int Main()
            {
                int i = 0;
                while (i < 2)
                {
                    Item[] outer = Item[1]; outer[0].Id = 1;
                    {
                        Item[,] inner = Item[1,1]; inner[0,0].Id = 2;
                        i++;
                        if (i == 1) continue;
                        break;
                    }
                }
                if (Item.Trace != 2121) return 1;
                Item.Trace = 0; i = 0;
                for (Item[] keep = Item[1]; i < 2; i++)
                {
                    keep[0].Id = 9;
                    Item[] body = Item[1]; body[0].Id = 2;
                    switch (i)
                    {
                        case 0:
                            Item[] first = Item[1]; first[0].Id = 3;
                            continue;
                        default:
                            Item[] last = Item[1]; last[0].Id = 4;
                            break;
                    }
                    if (Item.Trace != 324) return 2;
                    break;
                }
                if (Item.Trace != 32429) return 3;
                Item.Trace = 0;
                if (false) Item[1];
                if (true) Item[1];
                else Item[2];
                for (int j = 0; j < 2; j++) Item[1];
                if (Item.Trace != 111) return 4;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ReclaimsStackStorageAndHandlesRepeatedTemporaryAllocations(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Count = 0; public static int Dimensions = 0; public static int Next() { State.Dimensions++; return State.Dimensions; } }
            struct Base { public int Id = 2; public virtual ~Base() { State.Count += Id; } }
            struct Derived : Base { public long Padding = cast<long>(10); }
            struct Other { public ~Other() { State.Count += 5; } }
            void Repeated()
            {
                int i = 0;
                if (false && Derived[1].Length == 1) State.Count = -100;
                // The condition belongs to this function's scope, so all four
                // allocations stay registered until it exits, including the last check.
                while (Derived[1].Length == 1 && i < 3) i++;
                Other[2];
            }
            int Main()
            {
                Repeated();
                if (State.Count != 18) return 1;
                for (int i = 0; i < 2048; i++)
                {
                    int[,] scratch = int[32,64];
                    scratch[31,63] = i;
                    if (scratch[31,63] != i) return 2;
                }
                int[,] dimensions = int[State.Next(), State.Next()];
                if (State.Dimensions != 2 || dimensions.Length != 2 || dimensions.GetLength(1) != 2) return 3;
                int[,,] empty = int[2147483647,2147483647,0];
                if (empty.Length != 0 || empty.Rank != 3) return 4;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_CleansDestructorBodyArraysBeforeCallingBaseDestructor(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Trace = 0; }
            struct Temporary { public ~Temporary() { State.Trace = State.Trace * 10 + 3; } }
            struct Base
            {
                public virtual int Read() { return 1; }
                public virtual ~Base() { State.Trace = State.Trace * 10 + 1; }
            }
            struct Derived : Base
            {
                public override int Read() { return 42; }
                public override ~Derived()
                {
                    Temporary[1];
                    State.Trace = State.Trace * 10 + 2;
                }
            }
            int Main()
            {
                {
                    Derived[,] array = Derived[1,1];
                    if (array[0,0].Read() != 42) return 1;
                }
                if (State.Trace != 231) return 2;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_StableBaseLayoutPreservesAddressesArraysAndValueAbi(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Base { public int Value; }
            struct Middle : Base { public int More; }
            interface IFoo { int Foo(); }
            interface IBar { int Bar(); }
            interface IBaz { int Baz(); }
            struct Derived : Middle, IFoo, IBar, IBaz
            {
                public int Last;
                public int Foo() { return Value; }
                public int Bar() { return More; }
                public int Baz() { return Last; }
            }
            struct Padded { public long Wide; public byte Small; }
            struct PaddedChild : Padded { public byte Tail; }
            struct Empty { }
            struct EmptyChild : Empty, IFoo { public int Foo() { return 42; } }
            Base RoundTrip(Base value) { value.Value += 1; return value; }
            Derived Make() { return Derived { 10, 20, 12 }; }
            Base* Upcast(Derived* value) { return value; }
            int ViaReference(Base& value) { value.Value += 1; return value.Value; }
            int Main()
            {
                if (cast<int>(sizeof(Base)) != 4 || cast<int>(alignof(Base)) != 4 || cast<int>(offsetof(Base, Value)) != 0) return 1;
                if (cast<int>(sizeof(Middle)) != 8 || cast<int>(alignof(Middle)) != 4 || cast<int>(offsetof(Middle, More)) != 4) return 2;
                if (cast<int>(offsetof(Derived, Value)) != 0 || cast<int>(offsetof(Derived, More)) != 4) return 3;
                Derived value = Make();
                Base* basePointer = Upcast(&value);
                Middle* middlePointer = &value;
                if (&basePointer->Value != &value.Value || &basePointer->Value != &middlePointer->Value) return 4;
                basePointer->Value = 41;
                Base& reference = value;
                if (&reference != basePointer || ViaReference(reference) != 42 || value.Value != 42) return 5;
                IFoo foo = value;
                IBar bar = value;
                IBaz baz = value;
                if (foo.Foo() != 42 || bar.Bar() != 20 || baz.Baz() != 12) return 6;
                if (Make().Value != 10 || Make().More != 20 || Make().Last != 12) return 7;
                Base original = Base { 41 };
                Base copy = RoundTrip(original);
                if (original.Value != 41 || copy.Value != 42) return 8;
                Base[] heap = new Base[2];
                Base[] stack = Base[2];
                heap[0].Value = 10;
                heap[1].Value = 20;
                stack[0].Value = 11;
                stack[1].Value = 21;
                Base* first = &heap[0];
                Base* stackFirst = &stack[0];
                if (&first[1] != &heap[1] || first[1].Value != 20 || &stackFirst[1] != &stack[1]) return 9;
                if (heap[0].Value != 10 || stack[0].Value != 11 || stack[1].Value != 21) return 10;
                free(heap);
                PaddedChild padded = PaddedChild { cast<long>(9), cast<byte>(7), cast<byte>(5) };
                Padded* paddedBase = &padded;
                if (offsetof(PaddedChild, Tail) != sizeof(Padded)) return 11;
                *paddedBase = Padded { cast<long>(1), cast<byte>(2) };
                if (cast<int>(padded.Tail) != 5 || cast<int>(padded.Wide) != 1 || cast<int>(padded.Small) != 2) return 12;
                EmptyChild empty = EmptyChild();
                Empty* emptyBase = &empty;
                IFoo emptyView = empty;
                Empty& emptyReference = empty;
                if (emptyBase != &emptyReference || emptyView.Foo() != 42) return 13;
                return copy.Value;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_DispatchesAtNonzeroOffsetAcrossAllStorageAndLifecyclePaths(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Trace; }
            struct Prefix { public long Guard = cast<long>(123); public byte Tag = cast<byte>(7); }
            interface IValue
            {
                int Read();
                int Current { get; set; }
                int this[int index] { get; set; }
            }
            struct Base : Prefix, IValue
            {
                public int Value = 10;
                public virtual int Read() { return Value; }
                public virtual int Current { get { return Value; } set { Value = value; } }
                public virtual int this[int index] { get { return Value; } set { Value = value; } }
                public virtual ~Base() { State.Trace = State.Trace * 10 + 1; }
            }
            struct Derived : Base
            {
                public int Extra = 2;
                public override int Read() { return Value + Extra; }
                public override int Current { get { return Value + Extra; } set { Value = value + Extra; } }
                public override int this[int index] { get { return Value + index; } set { Value = value + index; } }
                public override ~Derived() { State.Trace = State.Trace * 10 + 2; }
            }
            struct Leaf : Derived { }
            struct Nested { public Leaf Value; }
            struct Globals { public static Nested Value; }
            struct Constructed : Base
            {
                public Constructed(int value) { Value = value; }
                public override int Read() { return Value + 1; }
            }
            struct ConstructedLeaf : Constructed { public ConstructedLeaf(int value) : base(value) { } }
            interface IPlain { int Read(); }
            struct Plain : Prefix, IPlain { public int Read() { return 1; } }
            struct PlainDerived : Plain { public int Read() { return 42; } }
            int ViaReference(Base& value) { IValue view = value; return view.Read(); }
            int Main()
            {
                if (offsetof(Base, Value) < sizeof(Prefix) + sizeof(nint)) return 1;
                Leaf leaf = Leaf();
                Base* pointer = &leaf;
                Prefix* prefix = pointer;
                if (&prefix->Guard != &leaf.Guard || cast<int>(prefix->Guard) != 123 || cast<int>(prefix->Tag) != 7) return 2;
                IValue view = *pointer;
                pointer->Current = 20;
                if (leaf.Value != 22 || view.Current != 24 || ViaReference(leaf) != 24) return 3;
                view.Current += 1;
                if (leaf.Value != 27) return 4;
                IValue& reference = leaf;
                if (reference.Read() != 29) return 4;
                leaf[2] = 30;
                view[3] += 1;
                if (leaf.Value != 39 || pointer->Read() != 41 || view[1] != 40) return 5;
                if (cast<int>(Leaf().Guard) != 123 || Leaf().Read() != 12) return 6;
                IValue global = Globals.Value.Value;
                if (global.Read() != 0) return 7;
                Nested nested = Nested();
                IValue nestedView = nested.Value;
                if (nestedView.Read() != 0) return 8;
                // Nested fields are zero-defaulted without running their initializers.
                // Dispatch metadata nevertheless belongs to each nested runtime object.
                Leaf[] heap = new Leaf[2];
                Base* second = &heap[1];
                IValue arrayView = *second;
                if (arrayView.Read() != 12 || cast<int>(heap[1].Guard) != 123) return 9;
                State.Trace = 0;
                free(heap);
                if (State.Trace != 2121) return 10;
                State.Trace = 0;
                { Leaf[] stack = Leaf[1]; IValue stackView = stack[0]; if (stackView.Read() != 12) return 11; }
                if (State.Trace != 21) return 12;
                ConstructedLeaf* constructed = new ConstructedLeaf(41);
                Base* constructedBase = constructed;
                IValue constructedView = *constructedBase;
                if (constructedBase->Read() != 42 || constructedView.Read() != 42 || cast<int>(constructedBase->Guard) != 123) return 13;
                free(constructed);
                Base* allocated = new Leaf();
                IValue allocatedView = *allocated;
                if (allocatedView.Read() != 12) return 14;
                State.Trace = 0;
                free(allocated);
                if (State.Trace != 21) return 15;
                PlainDerived plain = PlainDerived();
                Plain* plainBase = &plain;
                IPlain plainView = *plainBase;
                if (plainView.Read() != 42) return 16;
                return 42;
            }
            """, optimization));
    }


    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_RunsElementPointerArithmetic(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Pair { public byte Tag; public long Value; }
            struct Cursor
            {
                public static int Calls;
                public static int* Data;
                public static int** Address() { Cursor.Calls += 1; return &Cursor.Data; }
                public int* Current { get { return Cursor.Data; } set { Cursor.Data = value; } }
                public long Storage;
                public long Bits { get { return Storage; } set { Storage = value; } }
            }
            int Main()
            {
                int[] values = int[4];
                values[0] = 10; values[1] = 20; values[2] = 42; values[3] = 30;
                int* start = &values[0]; int* ptr = start;
                int* old = ptr++;
                if (old != start || *ptr != 20) return 1;
                ptr += 2; ptr -= 1;
                if (*ptr != 42 || ptr - start != cast<nint>(2)) return 2;
                if (start - ptr != cast<nint>(-2)) return 3;
                if (2 + start != ptr || ptr - 2 != start) return 4;
                sbyte negative = cast<sbyte>(-1);
                if (*(ptr + negative) != 20) return 5;
                byte positive = cast<byte>(1);
                if (*(positive + ptr) != 30) return 6;
                if (--ptr != start + 1 || ptr-- != start + 1 || ptr != start) return 7;
                Pair[] pairs = Pair[3];
                Pair* first = &pairs[0]; Pair* last = first + 2;
                if (last - first != cast<nint>(2) || last != &pairs[2]) return 8;
                Cursor.Data = start;
                int* prior = (*Cursor.Address())++;
                if (Cursor.Calls != 1 || prior != start || Cursor.Data != start + 1) return 9;
                Cursor cursor = Cursor(); cursor.Current += cast<sbyte>(1); cursor.Current -= 1;
                if (Cursor.Data != start + 1) return 10;
                cursor.Bits = cast<long>(1); cursor.Bits <<= 33;
                if (cursor.Bits != (cast<long>(1) << 33)) return 11;
                byte large = cast<byte>(255);
                if ((start + large) - start != cast<nint>(255)) return 12;
                if ((start - cast<sbyte>(-128)) - start != cast<nint>(128)) return 13;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_RunsImplicitDerivedConstructorsAndNullFree(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct State { public static int Calls; public static int[] Empty; }
            struct Base
            {
                public int Value;
                public Base() { Value = 42; }
                public virtual ~Base() { State.Calls += 1; }
            }
            struct Implicit : Base { public int Extra; }
            struct Explicit : Base { public int Extra; public Explicit() {} }
            struct Third : Implicit {}
            struct Plain { public int Value; }
            int Main()
            {
                Implicit a = Implicit(); Explicit b = Explicit(); Third c = Third();
                if (a.Value != 42 || b.Value != 42 || c.Value != 42) return 1;
                if (a.Extra != 0 || b.Extra != 0 || c.Extra != 0) return 5;
                Base* nil = null; free(nil);
                free(null);
                Plain* plain = null; free(plain);
                free(State.Empty);
                if (State.Calls != 0) return 2;
                Third* live = new Third();
                if (live->Value != 42) return 3;
                Base* up = live; free(up);
                if (State.Calls != 1) return 4;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData("float", "f", 0)]
    [InlineData("float", "f", 3)]
    [InlineData("double", "", 0)]
    [InlineData("double", "", 3)]
    public void Linker_MatchesIeeeConstantAndRuntimeComparisons(string type, string suffix, int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram($$"""
            const {{type}} Nan = 0.0{{suffix}} / 0.0{{suffix}};
            const {{type}} Inf = 1.0{{suffix}} / 0.0{{suffix}};
            const bool NanEq = Nan == Nan;
            const bool NanNe = Nan != Nan;
            const bool FiniteEq = Nan == 10.0{{suffix}};
            const bool FiniteNe = Nan != 10.0{{suffix}};
            const bool Zeros = 0.0{{suffix}} == -0.0{{suffix}};
            const bool Infinities = Inf > 10.0{{suffix}} && Inf == Inf && -Inf < Inf;
            int Check({{type}} nan, {{type}} inf, {{type}} zero, {{type}} negativeZero)
            {
                if (NanEq || !NanNe || FiniteEq || !FiniteNe || !Zeros || !Infinities) return 1;
                if ((nan == nan) != NanEq || (nan != nan) != NanNe) return 2;
                if ((nan == 10.0{{suffix}}) != FiniteEq || (nan != 10.0{{suffix}}) != FiniteNe) return 3;
                if ((zero == negativeZero) != Zeros) return 4;
                if ((inf > 10.0{{suffix}} && inf == inf && -inf < inf) != Infinities) return 5;
                return 42;
            }
            int Main() { return Check(Nan, Inf, 0.0{{suffix}}, -0.0{{suffix}}); }
            """, optimization));
    }

    public static IEnumerable<object[]> IntegerTypes()
    {
        foreach (string type in new[] { "sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "nint", "nuint", "clong", "culong" })
            foreach (int optimization in new[] { 0, 3 })
                yield return new object[] { type, optimization };
    }

    [Theory]
    [MemberData(nameof(IntegerTypes))]
    public void Linker_MatchesIntegerConstantAndRuntimeBoundaries(string type, int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram($$"""
            const int Width = cast<int>(sizeof({{type}})) * 8;
            const {{type}} One = cast<{{type}}>(1);
            const {{type}} High = One << (Width - 1);
            const {{type}} Shifted = High >> (Width - 1);
            const {{type}} Quotient = High / One;
            const {{type}} Remainder = High % One;
            const {{type}} Half = High / cast<{{type}}>(2);
            const {{type}} BelowHigh = High - One;
            {{type}} Left({{type}} value, long count) { return value << count; }
            {{type}} Right({{type}} value, byte count) { return value >> count; }
            {{type}} Divide({{type}} value, {{type}} divisor) { return value / divisor; }
            {{type}} Mod({{type}} value, {{type}} divisor) { return value % divisor; }
            int Main()
            {
                if (Left(One, cast<long>(0)) != One || Right(High, cast<byte>(0)) != High) return 1;
                if (Left(One, cast<long>(Width - 1)) != High) return 2;
                if (Right(High, cast<byte>(Width - 1)) != Shifted) return 3;
                if (Divide(High, One) != Quotient || Mod(High, One) != Remainder) return 4;
                if (Divide(High, cast<{{type}}>(2)) != Half || BelowHigh + One != High) return 6;
                {{type}} copy = High; copy >>= cast<long>(Width - 1);
                if (copy != Shifted) return 5;
                return 42;
            }
            """, optimization));
    }

    public static IEnumerable<object[]> InvalidIntegerOperations()
    {
        foreach (object[] row in IntegerTypes())
        {
            string type = (string)row[0];
            int optimization = (int)row[1];
            foreach (string operation in new[] { "<<", ">>" })
                foreach (string count in new[] { "-1", "Width", "Width + 1", "4294967296" })
                    yield return new object[] { type, operation, "cast<" + type + ">(1)", "cast<long>(" + count + ")", optimization };
            foreach (string operation in new[] { "/", "%" })
            {
                yield return new object[] { type, operation, "cast<" + type + ">(1)", "cast<" + type + ">(0)", optimization };
                if (type is "sbyte" or "short" or "int" or "long" or "nint" or "clong")
                    yield return new object[] { type, operation, "(cast<" + type + ">(1) << (Width - 1))", "cast<" + type + ">(-1)", optimization };
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidIntegerOperations))]
    public void Linker_TrapsInvalidIntegerOperations(string type, string operation, string left, string right, int optimization)
    {
        string countType = operation is "<<" or ">>" ? "long" : type;
        int exitCode = RunIterationFourProgram($$"""
            const int Width = cast<int>(sizeof({{type}})) * 8;
            {{type}} Apply({{type}} value, {{countType}} count) { return value {{operation}} count; }
            int Main() { {{type}} result = Apply({{left}}, {{right}}); return 42; }
            """, optimization);
        // A trap must terminate the process, including when the result is unused at -O3.
        Assert.NotEqual(42, exitCode);
        Assert.NotEqual(0, exitCode);
    }

    [Theory]
    [InlineData("IA, IB", 0)]
    [InlineData("IB, IA", 3)]
    public void Linker_ResolvesInheritedInterfaceOverloads(string bases, int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram($$"""
            interface Root { int Read(); }
            interface IA : Root { int Get(int value); }
            interface IB : Root { float Get(float value); }
            interface IC : {{bases}} {}
            struct Parent { public int Read() { return 10; } public int Get(int value) { return value + 10; } }
            struct Child : Parent, IC { public float Get(float value) { return value + 20.0f; } }
            int Call(IC value) { return value.Read() + value.Get(1) + cast<int>(value.Get(1.0f)); }
            int Main() { Child value = Child(); return Call(value); }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_DistinguishesIndexerArrayRanksAndNestedArrays(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Parent
            {
                public int this[int[] value] { get { return 1; } }
                public int this[int[][] value] { get { return 4; } }
            }
            struct Child : Parent
            {
                public int this[int[,] value] { get { return 2; } }
                public int this[int[,,] value] { get { return 3; } }
                public int this[int[][,] value] { get { return 5; } }
                public int this[int[,][] value] { get { return 6; } }
            }
            int Main()
            {
                Child c = Child();
                int[] a = new int[0]; int[,] b = new int[0,0]; int[,,] d = new int[0,0,0];
                int[][] e = new int[0][]; int[][,] f = new int[0][,]; int[,][] g = new int[0,0][];
                int sum = c[a] + c[b] + c[d] + c[e] + c[f] + c[g];
                free(a); free(b); free(d); free(e); free(f); free(g);
                return sum * 2;
            }
            """, optimization));
    }


    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_MatchesStaticConstantsAndShortCircuitRuntime(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Layout
            {
                public static nuint Bytes = sizeof(Layout);
                public byte Tag; public long Value;
                public static byte Wrapped = cast<byte>(255) + cast<byte>(1);
                public static bool NanDifferent = (0.0 / 0.0) != (0.0 / 0.0);
                public static bool Skipped = false && (1 / 0 == 0);
            }
            bool Check() { return true || (1 << -1 == 0); }
            int Main()
            {
                if (Layout.Bytes != sizeof(Layout) || Layout.Bytes < cast<nuint>(9)) return 1;
                if (Layout.Wrapped != cast<byte>(0) || !Layout.NanDifferent || Layout.Skipped) return 2;
                if (!Check()) return 3;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_PreservesMostDerivedConstructionAndDestructionDispatch(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Log { public static int Calls; public static int Properties; public static int Interfaces; }
            interface I { int Read(); }
            struct A : I
            {
                public A() { Log.Calls = Log.Calls * 10 + Read(); Log.Properties = Value + (*this)[1]; I view = *this; Log.Interfaces = view.Read(); }
                public virtual int Read() { return 1; }
                public virtual int Value { get { return 10; } }
                public virtual int this[int x] { get { return x + 10; } }
                public virtual ~A() { Log.Calls = Log.Calls * 10 + Read(); I view = *this; Log.Interfaces = view.Read(); }
            }
            struct B : A
            {
                public B() { Log.Calls = Log.Calls * 10 + Read(); }
                public override int Read() { return 2; }
                public override ~B() { Log.Calls = Log.Calls * 10 + Read(); }
            }
            struct C : B
            {
                public int Stage;
                public C() { Stage = 4; Log.Calls = Log.Calls * 10 + Read(); }
                public override int Read() { return Stage + 3; }
                public override int Value { get { return Stage + 30; } }
                public override int this[int x] { get { return Stage + x + 30; } }
                public override ~C() { Log.Calls = Log.Calls * 10 + Read(); Stage = 5; }
            }
            int Main()
            {
                C stack = C();
                if (Log.Calls != 337 || Log.Properties != 61 || Log.Interfaces != 3 || stack.Stage != 4) return 1;
                B& reference = stack; if (reference.Read() != 7) return 2;
                Log.Calls = 0;
                C* heap = new C(); A* pointer = heap;
                if (Log.Calls != 337 || pointer->Read() != 7) return 3;
                free(pointer);
                if (Log.Calls != 337788 || Log.Interfaces != 8) return 4;
                Log.Calls = 0;
                A* direct = new A(); free(direct);
                if (Log.Calls != 11 || Log.Properties != 21 || Log.Interfaces != 1) return 5;
                Log.Calls = 0;
                C[] array = new C[2]; free(array);
                if (Log.Calls != 388388) return 6;
                Log.Calls = 0;
                { C[] local = C[2]; }
                if (Log.Calls != 388388) return 7;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("return;", 0)]
    [InlineData("if (Mode == 1) return; return;", 3)]
    [InlineData("if (Mode == 1) { if (Mode > 0) return; }", 3)]
    [InlineData("while (Mode > 0) { return; }", 0)]
    [InlineData("for (int i = 0; i < 2; i++) { if (Mode == 1) return; }", 3)]
    public void Linker_FinalizesEveryDestructorExitAfterLocalCleanup(string exit, int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram($$"""
            struct Log { public static int Value; }
            struct Local { public ~Local() { Log.Value = Log.Value * 10 + 4; } }
            struct A { public virtual ~A() { Log.Value = Log.Value * 10 + 1; } }
            struct B : A { public override ~B() { Log.Value = Log.Value * 10 + 2; return; } }
            struct C : B
            {
                public int Mode;
                public C(int mode) { Mode = mode; }
                public override ~C() { Local[] local = Local[1]; Log.Value = Log.Value * 10 + 3; {{exit}} }
            }
            int Main()
            {
                for (int mode = 0; mode < 2; mode++)
                {
                    Log.Value = 0; C* c = new C(mode); A* a = c; free(a);
                    if (Log.Value != 3421) return 1;
                }
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_BindsReferencesAndAllowsAbstractViewsAndPrivateSelfDestruction(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct H { public int& Value; public H(int& value) { Value = value; } }
            struct D : H { public readonly int& Other; public D(int& v, readonly int& other) : base(v) { this.Other = other; } }
            struct Outer { public H Inner; public Outer(int& v) { Inner = H(v); } }
            abstract struct A { public abstract int Read(); }
            struct C : A { public override int Read() { return 42; } }
            struct Resource
            {
                public static int Count;
                private ~Resource() { Resource.Count += 1; }
                public static void Destroy(Resource* p) { free(p); }
            }
            int Main()
            {
                int value = 20; int other = 30;
                { D d = D(value, other); d.Value = 21; if (d.Value != 21 || d.Other != 30) return 1; }
                { Outer outer = Outer(value); outer.Inner.Value = 42; if (value != 42) return 1; }
                { H positional = H { value }; if (positional.Value != 42) return 1; }
                C c = C(); A& view = c;
                if (view.Read() != 42) return 2;
                A* pointer = &c;
                if (pointer->Read() != 42) return 2;
                Resource* r = new Resource(); Resource.Destroy(r);
                if (Resource.Count != 1) return 3;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_ShortCircuitKeepsConditionalAllocationAndCleanup(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Item { public static int Count; public ~Item() { Item.Count += 1; } }
            bool Cleanup(Item* p) { free(p); return true; }
            void Check(bool c)
            {
                bool a = c && (Item[1].Length == 1);
                bool b = c || (Item[1].Length == 1);
                Item* p = new Item();
                bool freed = c && Cleanup(p);
                if (!c) free(p);
            }
            int Main()
            {
                Check(false); Check(true);
                if (Item.Count != 4) return 1;
                int value; bool result = ((value = 1) == 1) && ((value = 2) == 2);
                if (value != 2 || !result) return 2;
                bool skipped = true || ((value = 10) == 10);
                if (value != 2 || !skipped) return 3;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [MemberData(nameof(IntegerTypes))]
    public void Linker_EqualitySupportsScalarAndReadonlyPointerViews(string type, int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram($$"""
            enum E { A, B }
            bool Compare({{type}} a, {{type}} b) { return a == b && !(a != b); }
            int Main()
            {
                {{type}} value = cast<{{type}}>(42); {{type}}* p = &value;
                readonly {{type}}* view = p; {{type}}* readonly binding = p;
                if (!Compare(value, value) || Compare(value, cast<{{type}}>(0))) return 1;
                if (p != view || view != p || binding != view || !(p == view) || p == null || null == p) return 2;
                if (E.A == E.B || !(E.A != E.B) || !(true == true) || false != false) return 3;
                return 42;
            }
            """, optimization));
    }

    public static IEnumerable<object[]> FloatingCastTypes()
    {
        foreach (object[] row in IntegerTypes())
        foreach (string sourceType in new[] { "float", "double" })
            yield return new object[] { row[0], row[1], sourceType };
    }

    [Theory]
    [MemberData(nameof(FloatingCastTypes))]
    public void Linker_MatchesFloatingCastConstantAndRuntimeBoundaries(string type, int optimization, string sourceType)
    {
        bool signed = type is "sbyte" or "short" or "int" or "long" or "nint" or "clong";
        int width = type switch { "sbyte" or "byte" => 8, "short" or "ushort" => 16, "int" or "uint" => 32,
            "clong" or "culong" => OperatingSystem.IsWindows() ? 32 : IntPtr.Size * 8,
            "nint" or "nuint" => IntPtr.Size * 8, _ => 64 };
        double upper = Math.ScaleB(1, signed ? width - 1 : width);
        double last = sourceType == "float" ? MathF.BitDecrement((float)upper) : Math.BitDecrement(upper);
        double lower = signed ? -upper : 0;
        double fractionalMinimum = sourceType == "float" ? (float)(lower - 0.75) : lower - 0.75;
        string suffix = sourceType == "float" ? "f" : "";
        string Literal(double value) => value.ToString("E17", System.Globalization.CultureInfo.InvariantCulture) + suffix;
        Assert.Equal(42, RunIterationFourProgram($$"""
            const {{type}} Minimum = cast<{{type}}>({{Literal(lower)}});
            const {{type}} Last = cast<{{type}}>({{Literal(last)}});
            const {{type}} Fraction = cast<{{type}}>(12.75{{suffix}});
            const {{type}} Negative = cast<{{type}}>({{(signed ? "-12.75" : "-0.75")}}{{suffix}});
            {{type}} Convert({{sourceType}} value) { return cast<{{type}}>(value); }
            int Main()
            {
                if (Convert(0.0{{suffix}}) != cast<{{type}}>(0)) return 1;
                if (Convert(12.75{{suffix}}) != Fraction || Fraction != cast<{{type}}>(12)) return 2;
                if (Convert({{(signed ? "-12.75" : "-0.75")}}{{suffix}}) != Negative || Negative != cast<{{type}}>({{(signed ? "-12" : "0")}})) return 3;
                if (Convert({{Literal(lower)}}) != Minimum || Convert({{Literal(last)}}) != Last) return 4;
                if (Minimum != cast<{{type}}>({{new System.Numerics.BigInteger(lower)}}) || Last != cast<{{type}}>({{new System.Numerics.BigInteger(last)}})) return 5;
                if (Convert({{Literal(fractionalMinimum)}}) != Minimum) return 6;
                return 42;
            }
            """, optimization));
    }

    public static IEnumerable<object[]> InvalidFloatingCasts()
    {
        foreach (object[] row in FloatingCastTypes())
        {
            string type = (string)row[0], source = (string)row[2];
            bool signed = type is "sbyte" or "short" or "int" or "long" or "nint" or "clong";
            foreach (string value in new[] { "0.0 / 0.0", "1.0 / 0.0", "-1.0 / 0.0",
                $"cast<double>(cast<ulong>(1) << (cast<int>(sizeof({type})) * 8 - {(signed ? 1 : 0)} - 1)) * 2.0",
                signed ? "-1.0e30" : "-1.0" })
                yield return new object[] { type, row[1], source, value };
            int width = type switch { "sbyte" or "byte" => 8, "short" or "ushort" => 16, "int" or "uint" => 32,
                "clong" or "culong" => OperatingSystem.IsWindows() ? 32 : IntPtr.Size * 8,
                "nint" or "nuint" => IntPtr.Size * 8, _ => 64 };
            double lower = signed ? -Math.ScaleB(1, width - 1) : 0;
            double below = source == "float" ? MathF.BitDecrement((float)(lower - 1)) : Math.BitDecrement(lower - 1);
            yield return new object[] { type, row[1], source, below.ToString("E17", System.Globalization.CultureInfo.InvariantCulture) };
        }
    }

    [Theory]
    [MemberData(nameof(InvalidFloatingCasts))]
    public void Linker_TrapsInvalidFloatingCastsEvenWhenUnused(string type, int optimization, string sourceType, string value)
    {
        int exit = RunIterationFourProgram($$"""
            {{type}} Convert({{sourceType}} value) { return cast<{{type}}>(value); }
            int Main() { {{type}} unused = Convert(cast<{{sourceType}}>({{value}})); return 42; }
            """, optimization);
        Assert.NotEqual(42, exit);
        Assert.NotEqual(0, exit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_TrapsPrematureVirtualAccessToAnUnboundReference(int optimization)
    {
        int exit = RunIterationFourProgram("""
            struct Base { public Base() { Read(); } public virtual int Read() { return 0; } }
            struct Derived : Base
            {
                public int& Value;
                public Derived(int& value) { Value = value; }
                public override int Read() { return Value; }
            }
            int Main() { int value = 42; Derived d = Derived(value); return 42; }
            """, optimization);
        Assert.NotEqual(42, exit);
        Assert.NotEqual(0, exit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_AssignmentEvaluatesTargetsThenRhsExactlyOnce(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Log { public static int Trace; }
            interface I { int Value { get; set; } int this[int index] { get; set; } }
            struct Cell : I
            {
                public int Field;
                public int Value { get { Log.Trace = Log.Trace * 10 + 4; return Field; } set { Log.Trace = Log.Trace * 10 + 3; Field = value; } }
                public int this[int index] { get { Log.Trace = Log.Trace * 10 + 4; return Field; } set { Log.Trace = Log.Trace * 10 + 3; Field = value; } }
            }
            Cell* Target(Cell* p) { Log.Trace = Log.Trace * 10 + 1; return p; }
            I& View(I& value) { Log.Trace = Log.Trace * 10 + 1; return value; }
            int Index() { Log.Trace = Log.Trace * 10 + 1; return 0; }
            int Rhs() { Log.Trace = Log.Trace * 10 + 2; return 7; }
            int Main()
            {
                int[] a = new int[1]; int[,] grid = new int[1,1];
                int i; a[(i = 0)] = i; if (a[0] != 0) return 1;
                Log.Trace = 0; a[Index()] = Rhs(); if (Log.Trace != 12 || a[0] != 7) return 2;
                Log.Trace = 0; grid[Index(),Index()] = Rhs(); if (Log.Trace != 112 || grid[0,0] != 7) return 3;
                Cell c = Cell();
                Log.Trace = 0; Target(&c)->Field = Rhs(); if (Log.Trace != 12 || c.Field != 7) return 4;
                Log.Trace = 0; (*Target(&c)).Field = Rhs(); if (Log.Trace != 12) return 5;
                Log.Trace = 0; Target(&c)->Value = Rhs(); if (Log.Trace != 123) return 6;
                Log.Trace = 0; (*Target(&c))[Index()] = Rhs(); if (Log.Trace != 1123) return 7;
                Log.Trace = 0; Target(&c)->Value += Rhs(); if (Log.Trace != 1423 || c.Field != 14) return 8;
                I view = c;
                Log.Trace = 0; View(view)[Index()] = Rhs(); if (Log.Trace != 1123) return 9;
                Log.Trace = 0; View(view)[Index()] += Rhs(); if (Log.Trace != 11423 || c.Field != 14) return 10;
                int x = 1; x += (x = 10); if (x != 11) return 11;
                Log.Trace = 0; a[Index()] = grid[Index(),Index()] = Rhs(); if (Log.Trace != 1112) return 12;
                Log.Trace = 0; int old = a[Index()]++; if (Log.Trace != 1 || old != 7 || a[0] != 8) return 13;
                free(a); free(grid); return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_ScalarAndArrayCleanupUseOneReverseConstructionOrder(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Log { public static int Trace; public static int Constructed; }
            struct R { public int Id; public R(int id) { Id = id; Log.Constructed += 1; } public ~R() { Log.Trace = Log.Trace * 10 + Id; } }
            struct Plain { public int Value; }
            struct A { public virtual ~A() { Log.Trace = Log.Trace * 10 + 1; } }
            struct B : A { public override ~B() { Log.Trace = Log.Trace * 10 + 2; } }
            struct C : B { public override ~C() { Log.Trace = Log.Trace * 10 + 3; } }
            int Capture() { R value = R(1); return Log.Trace; }
            int Main()
            {
                if (Capture() != 0 || Log.Trace != 1) return 1;
                Log.Trace = 0; { R a = R(1); R b = R(2); } if (Log.Trace != 21) return 2;
                Log.Trace = 0;
                { R a = R(1); { R b = R(2); } if (Log.Trace != 2) return 3; }
                if (Log.Trace != 21) return 4;
                Log.Trace = 0;
                { R a = R(1); R[] b = R[2]; b[0].Id = 2; b[1].Id = 3; R c = R(4); Plain p = Plain(); }
                if (Log.Trace != 4321) return 5;
                Log.Trace = 0; { C value = C(); } if (Log.Trace != 321) return 6;
                Log.Trace = 0; { R neverConstructed; } if (Log.Trace != 0) return 7;
                if (Log.Constructed != 7) return 8;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_ScalarCleanupCoversReturnBreakContinueAndDeferredConstruction(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Log { public static int Trace; }
            struct R { public int Id; public R(int id) { Id = id; } public ~R() { Log.Trace = Log.Trace * 10 + Id; } }
            void Early(bool condition) { R a = R(1); { R b = R(2); if (condition) return; } }
            void Conditional(bool condition) { R a; if (condition) a = R(1); }
            void Ordered(bool condition) { R a; R b; if (condition) { a = R(1); b = R(2); } else { b = R(2); a = R(1); } }
            int Main()
            {
                Early(true); if (Log.Trace != 21) return 1;
                Log.Trace = 0; Early(false); if (Log.Trace != 21) return 2;
                Log.Trace = 0;
                for (int i = 0; i < 3; i++) { R value = R(i + 1); if (i == 0) continue; break; }
                if (Log.Trace != 12) return 3;
                Log.Trace = 0;
                { R a; { a = R(1); R b = R(2); } if (Log.Trace != 2) return 4; R c = R(3); }
                if (Log.Trace != 231) return 5;
                Log.Trace = 0; Conditional(false); if (Log.Trace != 0) return 6;
                Conditional(true); if (Log.Trace != 1) return 7;
                Log.Trace = 0; { R a; a = R(1); a = R(2); } if (Log.Trace != 12) return 8;
                Log.Trace = 0; Ordered(true); if (Log.Trace != 21) return 9;
                Log.Trace = 0; Ordered(false); if (Log.Trace != 12) return 10;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_BindingReadonlyAllowsFreeAndPrivateScalarCleanupInsideOwnType(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct R
            {
                public static int Count;
                private ~R() { R.Count += 1; }
                public static void Run() { R local = R(); }
            }
            struct Item { public int Value; }
            void readonly Destroy(Item* readonly pointer) { free(pointer); }
            int Main() { R.Run(); if (R.Count != 1) return 1; Item* readonly p = new Item(); Destroy(p); return 42; }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_CompletesAbstractMethodPropertyAndIndexerSlotsAcrossLevels(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            interface Root { int Read(); int Value { get; set; } int this[int x] { get; set; } }
            interface Left : Root {}
            interface Right : Root {}
            interface Both : Left, Right {}
            abstract struct A : Both
            {
                public abstract int Read();
                public abstract int Value { get; set; }
                public abstract int this[int x] { get; set; }
            }
            abstract struct B : A {}
            abstract struct C : B {}
            struct D : C
            {
                public int N;
                public override int Read() { return N; }
                public override int Value { get { return N; } set { N = value; } }
                public override int this[int x] { get { return N + x; } set { N = value - x; } }
            }
            int Main()
            {
                D* d = new D(); A* a = d;
                d->N = 10; if (a->Read() != 10) return 1;
                B& b = *d; if (b.Value != 10) return 2; b.Value = 20;
                if (a->Read() != 20) return 3;
                C& c = *d; if (c[2] != 22) return 4; c[2] = 40;
                if (a->Read() != 38) return 5;
                Both view = *a; if (view.Read() != 38 || view[2] != 40) return 4;
                view.Value = 40;
                C& finalC = *d; if (finalC[2] != 42) return 6; finalC[1] = 43;
                B& finalB = *d; if (finalB.Value != 42) return 7;
                free(a); return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_ExplicitDestructorOverridesAndInheritedCleanupKeepTheSameDispatch(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Log { public static int Trace; }
            struct A
            {
                public virtual int Read() { return 1; }
                public virtual ~A() { Log.Trace = Log.Trace * 10 + Read(); }
            }
            struct B : A { public override ~B() { Log.Trace = Log.Trace * 10 + 2; } }
            struct C : B {}
            struct D : C
            {
                public override int Read() { return 7; }
                public override ~D() { Log.Trace = Log.Trace * 10 + 4; }
            }
            struct E : D {}
            struct F : A {}
            int Main()
            {
                A* p = new E(); free(p); if (Log.Trace != 427) return 1;
                Log.Trace = 0; { E value = E(); } if (Log.Trace != 427) return 2;
                Log.Trace = 0; { E[] values = E[2]; } if (Log.Trace != 427427) return 3;
                Log.Trace = 0; p = new F(); if (p->Read() != 1) return 4;
                free(p); if (Log.Trace != 1) return 5;
                Log.Trace = 0; { F value = F(); } if (Log.Trace != 1) return 6;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Linker_OverrideLookupSkipsDifferentOverloadsInIntermediateTypes(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct A { public virtual int M(int x) { return x; } }
            struct B : A { public int M(float x) { return cast<int>(x) + 1; } }
            struct C : B {}
            struct D : C { public override int M(int x) { return x + 40; } }
            int Main()
            {
                D d = D(); A& a = d;
                if (a.M(2) != 42) return 1;
                if (d.M(2) != 42) return 1;
                B& b = d;
                if (b.M(1.0f) != 2) return 1;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RunsAdvancedLifetimeCoreTypes(int optimization)
    {
        Assert.Equal(42, RunIterationFourProgram("""
            struct Resource
            {
                public int* Destroyed;
                public Resource(int* destroyed) { Destroyed = destroyed; }
                public void Touch() { *Destroyed += 0; }
                public ~Resource() { (*Destroyed)++; }
            }
            int Main()
            {
                int destroyed = 0;
                if (sizeof(storage<Resource>) <= sizeof(Resource) || alignof(storage<Resource>) < alignof(Resource)) return 4;
                if (sizeof(pin<Resource>) != sizeof(Resource) || alignof(pin<Resource>) != alignof(Resource)) return 5;
                {
                    storage<Resource> slot;
                    slot = Resource(&destroyed);
                    slot.Touch();
                    destruct(slot);
                    slot = Resource(&destroyed);
                    Resource live = move slot;

                    pin<Resource> fixedValue = Resource(&destroyed);
                    Resource* before = &fixedValue;
                    fixedValue.Touch();
                    Resource* after = &fixedValue;
                    if (before != after) return 1;

                    storage<Resource>[] slots = new storage<Resource>[4];
                    slots[0] = Resource(&destroyed);
                    slots[1] = Resource(&destroyed);
                    destruct(slots[0]);
                    destruct(slots[1]);
                    free(slots);

                    unique<Resource> owned = new Resource(&destroyed);
                    storage<unique<Resource>> uniqueSlot;
                    uniqueSlot = move owned;
                    destruct(uniqueSlot);

                    shared<Resource> strong = new Resource(&destroyed);
                    storage<shared<Resource>> sharedSlot;
                    sharedSlot = strong;
                    destruct(sharedSlot);

                    pin<storage<Resource>> pinnedSlot;
                    pinnedSlot = Resource(&destroyed);
                    pinnedSlot.Touch();
                    destruct(pinnedSlot);
                }
                if (destroyed != 8) return 2;
                return 42;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_DestroysDiscardedNonTrivialCallResultsAtFullExpressionEnd(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State { public static int Destroyed; }
            struct Resource
            {
                public int Value;
                public Resource(int value) { Value = value; }
                public ~Resource() { State.Destroyed += Value; }
            }
            struct Bundle
            {
                public Resource Value;
                public Bundle(int value) { Value = Resource(value); }
            }
            struct Factory
            {
                public Resource Make(int value) { return Resource(value); }
            }
            Resource MakeResource(int value) { return Resource(value); }
            Bundle MakeBundle(int value) { return Bundle(value); }
            unique<Resource> MakeUnique(int value) { return new Resource(value); }
            shared<Resource> MakeShared(int value) { return new Resource(value); }
            int GetInt() { return 42; }
            int Main()
            {
                State.Destroyed = 0;

                GetInt();
                if (State.Destroyed != 0) return 1;
                MakeResource(1);
                if (State.Destroyed != 1) return 2;
                MakeUnique(2);
                if (State.Destroyed != 3) return 3;
                MakeShared(4);
                if (State.Destroyed != 7) return 4;
                MakeBundle(8);
                if (State.Destroyed != 15) return 5;

                {
                    Resource consumed = MakeResource(16);
                    if (State.Destroyed != 15) return 6;
                }
                if (State.Destroyed != 31) return 7;

                {
                    unique<Resource> consumed = MakeUnique(32);
                    if (State.Destroyed != 31) return 8;
                }
                if (State.Destroyed != 63) return 9;

                Factory factory = Factory();
                factory.Make(64);
                if (State.Destroyed != 127) return 10;

                {
                    shared<Resource> consumed = MakeShared(128);
                    if (State.Destroyed != 127) return 11;
                }
                if (State.Destroyed != 255) return 12;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_DestroysNestedFullExpressionTemporariesInReverseConstructionOrder(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State
            {
                public static int Destroyed;
                public static int Used;
                public static int Order;
            }
            interface IReadable { int readonly Read(); }
            struct Resource : IReadable
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public void Use() { State.Used += Id; }
                public int readonly Read() { return Id; }
                public ~Resource()
                {
                    State.Destroyed += 1;
                    State.Order = State.Order * 10 + Id;
                }
            }
            Resource CreateResource(int id) { return Resource(id); }
            shared<Resource> GetShared(int id) { return new Resource(id); }
            int Add(int left, int right) { return left + right; }
            int ReadBorrowed(readonly Resource& value) { return value.Read(); }
            int ReadInterface(IReadable value) { return value.Read(); }
            struct FieldHolder
            {
                public int Value = CreateResource(8).Read();
            }
            struct BaseHolder
            {
                public int Value;
                public BaseHolder(readonly Resource& value) { Value = value.Read(); }
            }
            struct DerivedHolder : BaseHolder
            {
                public DerivedHolder() : base(CreateResource(9)) {}
            }
            int Main()
            {
                State.Destroyed = 0;
                State.Used = 0;
                State.Order = 0;

                GetShared(1)->Use();
                if (State.Used != 1 || State.Destroyed != 1) return 1;

                CreateResource(2).Use();
                if (State.Used != 3 || State.Destroyed != 2) return 2;

                {
                    shared<Resource> owner = new Resource(3);
                    weak<Resource> observer = owner;
                    bool alive = (lock observer) != null;
                    if (!alive) return 3;
                    destruct(owner);
                    if (State.Destroyed != 3) return 4;
                }

                State.Order = 0;
                int total = Add(CreateResource(4).Read(), CreateResource(5).Read());
                if (total != 9) return 5;
                if (State.Destroyed != 5 || State.Order != 54) return 6;

                if (ReadBorrowed(CreateResource(6)) != 6) return 7;
                if (State.Destroyed != 6 || State.Order != 546) return 8;
                if (ReadInterface(CreateResource(7)) != 7) return 9;
                if (State.Destroyed != 7 || State.Order != 5467) return 10;

                FieldHolder fieldHolder = FieldHolder();
                if (fieldHolder.Value != 8) return 11;
                if (State.Destroyed != 8 || State.Order != 54678) return 12;

                DerivedHolder derivedHolder = DerivedHolder();
                if (derivedHolder.Value != 9) return 13;
                if (State.Destroyed != 9 || State.Order != 546789) return 14;

                State.Order = 0;
                {
                    storage<Resource> direct = Resource(ReadBorrowed(CreateResource(10)));
                    if (direct.Id != 10) return 15;
                    if (State.Destroyed != 10 || State.Order != 10) return 16;
                }
                if (State.Destroyed != 11 || State.Order != 110) return 17;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_PreservesStorageRuntimeStateAcrossMutableStorageReferenceCalls(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State { public static int Destroyed; }
            struct Resource
            {
                public int Value;
                public Resource(int value) { Value = value; }
                public void SetValue(int value) { Value = value; }
                public ~Resource() { State.Destroyed += 1; }
            }
            void Reset(storage<Resource>& value, int next)
            {
                destruct(value);
                value = Resource(next);
            }
            int Main()
            {
                State.Destroyed = 0;
                {
                    storage<Resource> value = Resource(1);
                    Reset(value, 2);
                    value.SetValue(3);
                    if (value.Value != 3) return 1;
                }
                if (State.Destroyed != 2) return 2;

                {
                    storage<Resource> value = Resource(4);
                    value.SetValue(5);
                    if (value.Value != 5) return 3;
                }
                if (State.Destroyed != 3) return 4;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_UsesResolvedReferenceOwnersForExactlyOnceLifetimeOperations(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State { public static int Destroyed; }
            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource() { State.Destroyed += 1; }
            }
            struct Owner
            {
                public Resource Value;
                public Resource& Get() { return Value; }
            }
            Resource& Forward(Resource& value) { return value; }
            storage<Resource>& ForwardStorage(storage<Resource>& value) { return value; }
            int Main()
            {
                State.Destroyed = 0;
                {
                    Resource value = Resource(1);
                    destruct(Forward(value));
                    if (State.Destroyed != 1) return 1;
                }
                if (State.Destroyed != 1) return 2;

                {
                    Resource value = Resource(2);
                    Resource result = move Forward(value);
                    if (result.Id != 2 || State.Destroyed != 1) return 3;
                }
                if (State.Destroyed != 2) return 4;

                {
                    storage<Resource> value = Resource(3);
                    destruct(ForwardStorage(value));
                    if (State.Destroyed != 3) return 5;
                    value = Resource(4);
                }
                if (State.Destroyed != 4) return 6;

                {
                    Owner owner = Owner { Resource(5) };
                    destruct(owner.Get());
                    if (State.Destroyed != 5) return 7;
                }
                if (State.Destroyed != 5) return 8;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_PreservesWholeObjectDestructionAtUserDestructorBoundaries(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State
            {
                public static int ParentDestroyed;
                public static int ChildDestroyed;
                public static int Order;
            }
            struct Child
            {
                public ~Child()
                {
                    State.ChildDestroyed += 1;
                    State.Order = State.Order * 10 + 2;
                }
            }
            struct Parent
            {
                public Child Child;
                public ~Parent()
                {
                    State.ParentDestroyed += 1;
                    State.Order = State.Order * 10 + 1;
                }
            }
            int Main()
            {
                State.ParentDestroyed = 0;
                State.ChildDestroyed = 0;
                State.Order = 0;
                {
                    Parent parent = Parent();
                    destruct(parent);
                    if (State.ParentDestroyed != 1 || State.ChildDestroyed != 1) return 1;
                    if (State.Order != 12) return 2;
                }
                if (State.ParentDestroyed != 1 || State.ChildDestroyed != 1) return 3;
                {
                    Parent parent = Parent();
                }
                if (State.ParentDestroyed != 2 || State.ChildDestroyed != 2) return 4;
                if (State.Order != 1212) return 5;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ConstructsDirectlyInsideStorageAndPinnedStorage(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State { public static int Constructors; public static int Destructors; }
            struct SelfReference
            {
                public SelfReference* Self;
                public SelfReference() { Self = this; }
            }
            struct Container
            {
                public pin<SelfReference> Value;
                public Container() { Value = SelfReference(); }
            }
            struct Resource
            {
                public int Value;
                public Resource(int value) { Value = value; State.Constructors += 1; }
                public ~Resource() { State.Destructors += 1; }
            }
            int Main()
            {
                storage<SelfReference> ordinary = SelfReference();
                bool ordinaryValid = ordinary.Self == &ordinary;
                destruct(ordinary);
                if (!ordinaryValid) return 1;

                pin<SelfReference> direct = SelfReference();
                if (direct.Self != &direct) return 2;

                Container container = Container();
                if (container.Value.Self != &container.Value) return 3;

                pin<storage<SelfReference>> pinned;
                pinned = SelfReference();
                bool pinnedValid = pinned.Self == &pinned;
                destruct(pinned);
                if (!pinnedValid) return 4;

                {
                    storage<Resource> reusable = Resource(10);
                    bool firstValid = reusable.Value == 10;
                    destruct(reusable);
                    if (!firstValid) return 5;
                    reusable = Resource(20);
                    bool secondValid = reusable.Value == 20;
                    if (!secondValid) return 6;
                }
                if (State.Constructors != 2 || State.Destructors != 2) return 5;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_TracksConditionalStorageCleanupAndMoveResponsibility(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State { public static int Constructors; public static int Destructors; }
            struct Resource
            {
                public Resource() { State.Constructors++; }
                public ~Resource() { State.Destructors++; }
            }
            void ConditionalConstruction(bool condition)
            {
                storage<Resource> value;
                if (condition) value = Resource();
            }
            void ConditionalDestruction(bool condition)
            {
                storage<Resource> value = Resource();
                if (condition) destruct(value);
            }
            void BalancedReconstruction(bool condition)
            {
                storage<Resource> value = Resource();
                if (condition) destruct(value);
                else destruct(value);
                value = Resource();
            }
            int Main()
            {
                ConditionalConstruction(false);
                if (State.Destructors != 0) return 1;
                ConditionalConstruction(true);
                if (State.Destructors != 1) return 2;
                ConditionalDestruction(false);
                ConditionalDestruction(true);
                if (State.Destructors != 3) return 3;
                BalancedReconstruction(true);
                BalancedReconstruction(false);
                if (State.Destructors != 7) return 4;
                {
                    storage<Resource> source = Resource();
                    Resource destination = move source;
                }
                if (State.Destructors != 8) return 5;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_PersistsStorageStateInFieldsHeapAggregatesAndArrays(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State { public static int Constructors; public static int Destructors; }
            struct Resource
            {
                public Resource() { State.Constructors++; }
                public Resource* Address() { return this; }
                public void Touch() {}
                public ~Resource() { State.Destructors++; }
            }
            struct Holder
            {
                public storage<Resource> Value;
                public void Create() { Value = Resource(); }
                public void Destroy() { destruct(Value); }
            }
            struct DestructorHolder
            {
                public storage<Resource> Value;
                public bool DestroyExplicitly;
                public DestructorHolder(bool destroyExplicitly)
                {
                    DestroyExplicitly = destroyExplicitly;
                    Value = Resource();
                }
                public ~DestructorHolder()
                {
                    if (DestroyExplicitly) destruct(Value);
                }
            }
            void StoreAndDestroy<T>(T value)
            {
                storage<T> slot;
                slot = move value;
                destruct(slot);
            }
            void Reset() { State.Constructors = 0; State.Destructors = 0; }
            int Main()
            {
                Reset();
                { Holder holder = Holder(); }
                if (State.Destructors != 0) return 1;

                Reset();
                { Holder holder = Holder(); holder.Value = Resource(); }
                if (State.Constructors != 1 || State.Destructors != 1) return 2;

                Reset();
                { Holder holder = Holder(); holder.Value = Resource(); destruct(holder.Value); }
                if (State.Constructors != 1 || State.Destructors != 1) return 3;

                Reset();
                {
                    Holder holder = Holder();
                    holder.Value = Resource();
                    destruct(holder.Value);
                    holder.Value = Resource();
                }
                if (State.Constructors != 2 || State.Destructors != 2) return 4;

                Reset();
                { Holder holder = Holder(); holder.Create(); holder.Destroy(); }
                if (State.Constructors != 1 || State.Destructors != 1) return 5;
                Reset();
                { Holder holder = Holder(); holder.Create(); }
                if (State.Constructors != 1 || State.Destructors != 1) return 6;

                Reset();
                {
                    DestructorHolder explicitHolder = DestructorHolder(true);
                    DestructorHolder automaticHolder = DestructorHolder(false);
                }
                if (State.Constructors != 2 || State.Destructors != 2) return 16;

                Reset();
                storage<Resource>* empty = new storage<Resource>();
                free(empty);
                if (State.Destructors != 0) return 7;

                Reset();
                storage<Resource>* initialized = new storage<Resource>();
                *initialized = Resource();
                free(initialized);
                if (State.Constructors != 1 || State.Destructors != 1) return 8;

                Reset();
                storage<Resource>* ended = new storage<Resource>();
                *ended = Resource();
                destruct(*ended);
                free(ended);
                if (State.Constructors != 1 || State.Destructors != 1) return 9;

                Reset();
                storage<Resource>* reused = new storage<Resource>();
                *reused = Resource();
                Resource* firstAddress = (*reused).Address();
                destruct(*reused);
                *reused = Resource();
                Resource* secondAddress = (*reused).Address();
                if (firstAddress != secondAddress) return 10;
                free(reused);
                if (State.Constructors != 2 || State.Destructors != 2) return 11;

                Reset();
                {
                    storage<Resource>* moved = new storage<Resource>();
                    *moved = Resource();
                    Resource value = move *moved;
                    free(moved);
                    if (State.Destructors != 0) return 12;
                }
                if (State.Destructors != 1) return 13;

                Reset();
                {
                    Holder first = Holder();
                    first.Value = Resource();
                    Holder second = move first;
                }
                if (State.Constructors != 1 || State.Destructors != 1) return 14;

                Reset();
                storage<Resource>[] values = new storage<Resource>[4];
                values[0] = Resource();
                values[3] = Resource();
                free(values);
                if (State.Constructors != 2 || State.Destructors != 2) return 15;

                Reset();
                StoreAndDestroy(Resource());
                if (State.Constructors != 1 || State.Destructors != 1) return 17;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ReusesStorageThroughMutableReferenceWithExactlyOnceDestruction(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State { public static int Constructors; public static int Destructors; }
            struct Resource
            {
                public Resource() { State.Constructors++; }
                public ~Resource() { State.Destructors++; }
            }
            int Main()
            {
                {
                    storage<Resource> slot;
                    storage<Resource>& reference = slot;
                    reference = Resource();
                    destruct(reference);
                    reference = Resource();
                }
                if (State.Constructors != 2 || State.Destructors != 2) return 1;

                State.Constructors = 0;
                State.Destructors = 0;
                storage<Resource>* value = new storage<Resource>();
                storage<Resource>& reference = *value;
                reference = Resource();
                destruct(reference);
                reference = Resource();
                free(value);
                if (State.Constructors != 2 || State.Destructors != 2) return 2;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ReusesIndexedStoragePointerWithExactlyOnceDestruction(int optimization)
    {
        Assert.Equal(0, RunIterationFourProgram("""
            struct State { public static int Constructors; public static int Destructors; }
            struct Resource
            {
                public Resource() { State.Constructors++; }
                public ~Resource() { State.Destructors++; }
            }
            int Main()
            {
                {
                    storage<Resource>* pointer = new storage<Resource>();
                    int index = 0;
                    pointer[index] = Resource();
                    destruct(pointer[index]);
                    pointer[index] = Resource();
                    Resource value = move pointer[index];
                    free(pointer);
                    if (State.Constructors != 2 || State.Destructors != 1) return 1;
                }
                if (State.Constructors != 2 || State.Destructors != 2) return 2;
                return 0;
            }
            """, optimization));
    }

    [Theory]
    [InlineData("construct", 0)]
    [InlineData("construct", 2)]
    [InlineData("destruct", 0)]
    [InlineData("destruct", 2)]
    [InlineData("access", 0)]
    [InlineData("access", 2)]
    public void Linker_TrapsInvalidIndirectStorageLifetimeOperations(string operation, int optimization)
    {
        string action = operation switch
        {
            "construct" => "Initialize(value); Initialize(value);",
            "destruct" => "Destroy(value);",
            "access" => "Read(value);",
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        int exit = RunIterationFourProgram($$"""
            struct Resource { public void Touch() {} }
            void Initialize(storage<Resource>* value) { *value = Resource(); }
            void Destroy(storage<Resource>* value) { destruct(*value); }
            void Read(storage<Resource>* value) { (*value).Touch(); }
            int Main()
            {
                storage<Resource>* value = new storage<Resource>();
                {{action}}
                return 42;
            }
            """, optimization);
        Assert.NotEqual(0, exit);
        Assert.NotEqual(42, exit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_StructValueEqualityAndCompositeCasUseTheSameFieldSemantics(int optimization)
    {
        int exit = RunIterationFourProgram("""
            struct Resource { public int Value; }
            struct Position { public int X; public int Y; }
            struct Inner
            {
                public Position Position;
                public Resource* Pointer;
                public int[] Array;
            }
            struct Outer { public int Id; public Inner Inner; }
            struct Floating { public float Value; }
            struct Ownership
            {
                public shared<Resource> Strong;
                public weak<Resource> Weak;
            }
            struct Pair<T>
            {
                public T First;
                public T Second;
                public Pair(T first, T second) { First = move first; Second = move second; }
            }

            int Main()
            {
                Position firstPosition = Position { 1, 2 };
                Position samePosition = Position { 1, 2 };
                Position otherPosition = Position { 1, 3 };
                if (!(firstPosition == samePosition) || firstPosition != samePosition) return 1;
                if (firstPosition == otherPosition || !(firstPosition != otherPosition)) return 2;

                Resource* firstPointer = new Resource { 7 };
                Resource* sameAddress = firstPointer;
                Resource* otherPointer = new Resource { 7 };
                int[] firstArray = new int[2];
                int[] sameArrayHandle = firstArray;
                int[] otherArray = new int[2];
                firstArray[0] = 10;
                otherArray[0] = 10;

                Outer first = Outer { 5, Inner { firstPosition, firstPointer, firstArray } };
                Outer same = Outer { 5, Inner { samePosition, sameAddress, sameArrayHandle } };
                Outer differentPointer = Outer { 5, Inner { samePosition, otherPointer, sameArrayHandle } };
                Outer differentArray = Outer { 5, Inner { samePosition, sameAddress, otherArray } };
                if (!(first == same)) return 3;
                if (first == differentPointer || first == differentArray) return 4;

                Inner nullLeft = Inner { firstPosition, null, firstArray };
                Inner nullRight = Inner { samePosition, null, sameArrayHandle };
                if (!(nullLeft == nullRight)) return 5;

                Floating positiveZero = Floating { +0.0f };
                Floating negativeZero = Floating { -0.0f };
                if (!(positiveZero == negativeZero)) return 6;
                float nan = 0.0f / 0.0f;
                Floating nanLeft = Floating { nan };
                Floating nanRight = Floating { nan };
                if (nanLeft == nanRight || !(nanLeft != nanRight)) return 7;

                Pair<int> pair = Pair<int>(8, 9);
                Pair<int> samePair = Pair<int>(8, 9);
                if (!(pair == samePair)) return 8;

                shared<Resource> owner = new Resource { 11 };
                shared<Resource> sameOwner = owner;
                weak<Resource> observer = owner;
                weak<Resource> sameObserver = observer;
                Ownership owned = Ownership { owner, observer };
                Ownership sameOwned = Ownership { sameOwner, sameObserver };
                if (!(owned == sameOwned)) return 9;

                atomic<Outer> state = first;
                Outer desired = Outer { 6, Inner { otherPosition, otherPointer, otherArray } };
                if (!(state : same --> desired)) return 10;
                state = first;
                if (state : differentPointer --> desired) return 11;

                atomic<Floating> floating = positiveZero;
                if (!(floating : negativeZero --> Floating { 1.0f })) return 12;
                floating = nanLeft;
                if (floating : nanRight --> Floating { 2.0f }) return 13;

                free(firstArray);
                free(otherArray);
                free(firstPointer);
                free(otherPointer);
                return 42;
            }
            """, optimization);
        Assert.Equal(42, exit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_RepeatedConstructorFieldAssignmentReleasesPreviousSharedValues(int optimization)
    {
        int exit = RunIterationFourProgram("""
            struct Counters
            {
                public static int First;
                public static int Second;
                public static int Third;
                public static int Fourth;
            }
            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource()
                {
                    if (Id == 1) Counters.First++;
                    else if (Id == 2) Counters.Second++;
                    else if (Id == 3) Counters.Third++;
                    else if (Id == 4) Counters.Fourth++;
                }
            }
            struct NormalHolder
            {
                public shared<Resource> Value;
                public NormalHolder(shared<Resource> first, shared<Resource> second)
                {
                    Value = first;
                    Value = second;
                }
            }
            struct AtomicHolder
            {
                public atomic<shared<Resource>> Value;
                public AtomicHolder(shared<Resource> first, shared<Resource> second)
                {
                    Value = first;
                    Value = second;
                }
            }
            void ExerciseNormal()
            {
                shared<Resource> first = new Resource(1);
                shared<Resource> second = new Resource(2);
                NormalHolder holder = NormalHolder(first, second);
            }
            void ExerciseAtomic()
            {
                shared<Resource> first = new Resource(3);
                shared<Resource> second = new Resource(4);
                AtomicHolder holder = AtomicHolder(first, second);
            }
            int Main()
            {
                ExerciseNormal();
                if (Counters.First != 1 || Counters.Second != 1) return 1;
                ExerciseAtomic();
                if (Counters.Third != 1 || Counters.Fourth != 1) return 2;
                return 42;
            }
            """, optimization);
        Assert.Equal(42, exit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ConstructorFieldInitializationRemainsCorrectAcrossPartialBranchesAndLoops(int optimization)
    {
        int exit = RunIterationFourProgram("""
            struct Counters
            {
                public static int Count;
                public static int Sum;
                public static int SumSquares;
            }
            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource()
                {
                    Counters.Count++;
                    Counters.Sum += Id;
                    Counters.SumSquares += Id * Id;
                }
            }
            struct PartialNormal
            {
                public shared<Resource> Value;
                public PartialNormal(bool choose, shared<Resource> first, shared<Resource> second)
                {
                    if (choose) Value = first;
                    Value = second;
                }
            }
            struct PartialAtomic
            {
                public atomic<shared<Resource>> Value;
                public PartialAtomic(bool choose, shared<Resource> first, shared<Resource> second)
                {
                    if (choose) Value = first;
                    Value = second;
                }
            }
            struct LoopNormal
            {
                public shared<Resource> Value;
                public LoopNormal(int count, shared<Resource> first, shared<Resource> second)
                {
                    int index = 0;
                    while (index < count)
                    {
                        Value = first;
                        index++;
                    }
                    Value = second;
                }
            }
            struct LoopAtomic
            {
                public atomic<shared<Resource>> Value;
                public LoopAtomic(int count, shared<Resource> first, shared<Resource> second)
                {
                    int index = 0;
                    while (index < count)
                    {
                        Value = first;
                        index++;
                    }
                    Value = second;
                }
            }
            void PartialNormalTrue()
            {
                shared<Resource> first = new Resource(1);
                shared<Resource> second = new Resource(2);
                PartialNormal holder = PartialNormal(true, first, second);
            }
            void PartialNormalFalse()
            {
                shared<Resource> first = new Resource(3);
                shared<Resource> second = new Resource(4);
                PartialNormal holder = PartialNormal(false, first, second);
            }
            void PartialAtomicTrue()
            {
                shared<Resource> first = new Resource(5);
                shared<Resource> second = new Resource(6);
                PartialAtomic holder = PartialAtomic(true, first, second);
            }
            void PartialAtomicFalse()
            {
                shared<Resource> first = new Resource(7);
                shared<Resource> second = new Resource(8);
                PartialAtomic holder = PartialAtomic(false, first, second);
            }
            void LoopNormalTwice()
            {
                shared<Resource> first = new Resource(9);
                shared<Resource> second = new Resource(10);
                LoopNormal holder = LoopNormal(2, first, second);
            }
            void LoopAtomicTwice()
            {
                shared<Resource> first = new Resource(11);
                shared<Resource> second = new Resource(12);
                LoopAtomic holder = LoopAtomic(2, first, second);
            }
            void LoopNormalZero()
            {
                shared<Resource> first = new Resource(13);
                shared<Resource> second = new Resource(14);
                LoopNormal holder = LoopNormal(0, first, second);
            }
            void LoopAtomicZero()
            {
                shared<Resource> first = new Resource(15);
                shared<Resource> second = new Resource(16);
                LoopAtomic holder = LoopAtomic(0, first, second);
            }
            int Main()
            {
                PartialNormalTrue();
                PartialNormalFalse();
                PartialAtomicTrue();
                PartialAtomicFalse();
                LoopNormalTwice();
                LoopAtomicTwice();
                LoopNormalZero();
                LoopAtomicZero();
                if (Counters.Count != 16) return 1;
                if (Counters.Sum != 136) return 2;
                if (Counters.SumSquares != 1496) return 3;
                return 42;
            }
            """, optimization);
        Assert.Equal(42, exit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void Linker_ConditionalMoveReassignmentUsesRuntimeLifetimeState(int optimization)
    {
        int exit = RunIterationFourProgram("""
            struct Counters
            {
                public static int ResourceCount;
                public static int ResourceSum;
                public static int ResourceSquares;
                public static int LastResource;
                public static int BoxCount;
                public static int BoxSum;
                public static int LastBox;
            }
            struct Resource
            {
                public int Id;
                public Resource(int id) { Id = id; }
                public ~Resource()
                {
                    Counters.ResourceCount++;
                    Counters.ResourceSum += Id;
                    Counters.ResourceSquares += Id * Id;
                    Counters.LastResource = Id;
                }
            }
            struct Box
            {
                public int Id;
                public Box(int id) { Id = id; }
                public ~Box()
                {
                    Counters.BoxCount++;
                    Counters.BoxSum += Id;
                    Counters.LastBox = Id;
                }
            }
            struct State { public shared<Resource> Value; }
            struct Inner { public shared<Resource> Value; }
            struct Outer { public Inner Inner; }

            int SharedCase(bool take, int firstId, int secondId)
            {
                Counters.LastResource = 0;
                shared<Resource> value = new Resource(firstId);
                shared<Resource> moved;
                if (take) moved = move value;
                value = new Resource(secondId);
                if (!take && Counters.LastResource != firstId) return 1;
                if (take && Counters.LastResource == firstId) return 2;
                return 0;
            }

            int UniqueCase(bool take, int firstId, int secondId)
            {
                Counters.LastResource = 0;
                unique<Resource> value = new Resource(firstId);
                unique<Resource> moved;
                if (take) moved = move value;
                value = new Resource(secondId);
                if (!take && Counters.LastResource != firstId) return 1;
                if (take && Counters.LastResource == firstId) return 2;
                return 0;
            }

            int WeakCase(bool take, int firstId, int secondId)
            {
                shared<Resource> first = new Resource(firstId);
                shared<Resource> second = new Resource(secondId);
                weak<Resource> value = first;
                weak<Resource> moved;
                if (take) moved = move value;
                value = second;
                shared<Resource> promoted = lock value;
                if (promoted != second) return 1;
                return 0;
            }

            int FieldCase(bool take, int firstId, int secondId)
            {
                Counters.LastResource = 0;
                State state = State { new Resource(firstId) };
                shared<Resource> moved;
                if (take) moved = move state.Value;
                state.Value = new Resource(secondId);
                if (!take && Counters.LastResource != firstId) return 1;
                if (take && Counters.LastResource == firstId) return 2;
                return 0;
            }

            int BoxCase(bool take, int firstId, int secondId)
            {
                Counters.LastBox = 0;
                Box value = Box(firstId);
                Box moved;
                if (take) moved = move value;
                value = Box(secondId);
                if (!take && Counters.LastBox != firstId) return 1;
                if (take && Counters.LastBox == firstId) return 2;
                return 0;
            }

            int LoopSharedCase(bool take, int firstId, int secondId)
            {
                Counters.LastResource = 0;
                shared<Resource> value = new Resource(firstId);
                shared<Resource> moved;
                int index = 0;
                while (index < 1)
                {
                    if (take)
                    {
                        moved = move value;
                        break;
                    }
                    index++;
                }
                value = new Resource(secondId);
                if (!take && Counters.LastResource != firstId) return 1;
                if (take && Counters.LastResource == firstId) return 2;
                return 0;
            }

            int NestedCase(bool take, int firstId, int secondId)
            {
                Counters.LastResource = 0;
                Outer value = Outer { Inner { new Resource(firstId) } };
                Outer moved;
                if (take) moved = move value;
                value = Outer { Inner { new Resource(secondId) } };
                if (!take && Counters.LastResource != firstId) return 1;
                if (take && Counters.LastResource == firstId) return 2;
                return 0;
            }

            int Main()
            {
                if (SharedCase(false, 1, 2) != 0) return 1;
                if (SharedCase(true, 3, 4) != 0) return 2;
                if (UniqueCase(false, 5, 6) != 0) return 3;
                if (UniqueCase(true, 7, 8) != 0) return 4;
                if (WeakCase(false, 9, 10) != 0) return 5;
                if (WeakCase(true, 11, 12) != 0) return 6;
                if (FieldCase(false, 13, 14) != 0) return 7;
                if (FieldCase(true, 15, 16) != 0) return 8;
                if (BoxCase(false, 101, 102) != 0) return 9;
                if (BoxCase(true, 103, 104) != 0) return 10;
                if (LoopSharedCase(false, 17, 18) != 0) return 11;
                if (LoopSharedCase(true, 19, 20) != 0) return 12;
                if (NestedCase(false, 21, 22) != 0) return 13;
                if (NestedCase(true, 23, 24) != 0) return 14;
                if (Counters.ResourceCount != 24) return 15;
                if (Counters.ResourceSum != 300) return 16;
                if (Counters.ResourceSquares != 4900) return 17;
                if (Counters.BoxCount != 4) return 18;
                if (Counters.BoxSum != 410) return 19;
                return 42;
            }
            """, optimization);
        Assert.Equal(42, exit);
    }

    private static int RunIterationFourProgram(string source, int optimization)
    {
        Compilation compilation = CreateExecutableCompilation(
            SourceText.From("namespace IterationFour; " + source, "iteration4.xe"));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(optimizationLevel: optimization, positionIndependentCode: !OperatingSystem.IsWindows());
        try
        {
            string objectPath = Path.Combine(directory, "iteration4" + LlvmTargetPlatform.GetObjectFileExtension(target.Triple));
            var objectFile = new LlvmObjectEmitter().Emit(compilation, objectPath, target, "iteration4");
            string executablePath = XenonBuildPaths.GetExecutablePath(directory, "iteration4", "debug", target.Triple);
            var executable = new NativeLinker().LinkExecutable(objectFile.Path, executablePath, target.Triple);
            NativeProcessResult process = new NativeProcessRunner().RunAsync(new NativeProcessRequest(
                executable.Path, [], directory, TimeSpan.FromSeconds(30))).GetAwaiter().GetResult();
            Assert.True(process.StartError is null && !process.TimedOut && process.TerminationError is null,
                $"Iteration 4 execution failed: {process.StartError}; timeout={process.TimedOut}; {process.TerminationError}\n" +
                $"stdout: {process.Stdout}\nstderr: {process.Stderr}\nExecutable: {executable.Path}");
            return process.ExitCode!.Value;
        }
        finally
        {
            DeleteIterationDirectory(directory);
        }
    }

    private static void DeleteIterationDirectory(string directory)
    {
        // Windows can briefly retain the executable after an intentional trap.
        // Retry only transient cleanup failures; a persistent error still fails.
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Delete(directory, recursive: true); return; }
            catch (Exception error) when (OperatingSystem.IsWindows() && attempt < 5 &&
                error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private static Compilation CreateLibraryCompilation() => Compilation.Create(SourceText.From("""
        namespace Integration;

        export int Add(int left, int right)
        {
            return left + right;
        }
        """, "math.xe"));

    private static TDelegate LoadDelegate<TDelegate>(nint library, string export)
        where TDelegate : Delegate =>
        Marshal.GetDelegateForFunctionPointer<TDelegate>(NativeLibrary.GetExport(library, export));

    private static void RunParallel(int workerCount, Action<int> action)
    {
        using var ready = new CountdownEvent(workerCount);
        using var gate = new ManualResetEventSlim();
        Task[] workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(() =>
        {
            ready.Signal();
            gate.Wait();
            action(worker);
        })).ToArray();
        ready.Wait();
        gate.Set();
        Task.WaitAll(workers);
    }

    private static Compilation CreateExecutableCompilation(params SourceText[] sources) =>
        Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Executable),
            references: null,
            sources);

    private static string CreateTemporaryDirectory()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "xenon-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void AssertMacOsMachO(string path, uint expectedFileType)
    {
        const uint MachO64Magic = 0xfeedfacf;
        const uint X86_64CpuType = 0x01000007;
        const uint Arm64CpuType = 0x0100000c;

        byte[] header = new byte[16];
        using (FileStream stream = File.OpenRead(path))
        {
            stream.ReadExactly(header);
        }

        Assert.Equal(MachO64Magic, BinaryPrimitives.ReadUInt32LittleEndian(header));

        uint expectedCpuType = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => X86_64CpuType,
            Architecture.Arm64 => Arm64CpuType,
            Architecture architecture => throw new Xunit.Sdk.XunitException(
                $"Unsupported macOS test architecture '{architecture}'."),
        };

        Assert.Equal(expectedCpuType, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)));
        Assert.Equal(expectedFileType, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)));
    }
}
