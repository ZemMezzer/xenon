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
    private delegate nint AddressDelegate(nint value);

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
                readonly Container& readOnly = mutable;
                int& writable = mutable.Get();
                writable = 42;
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
                Outer value = Outer();
                value.Left = Pair(&State.Value, &State.Value, input);
                value.Right = local;
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
                // Accessor + heap + two array elements + the scalar local.
                if (output != 43) return 2;
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
                readonly Value& receiver = value;
                int* pointer = receiver.GetPointer(&number);
                *pointer = 40;
                pointer = &number;
                readonly int* view = value.Touch();
                if (*view != 1) return 1;
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
                IValue& reference = leaf;
                pointer->Current = 20;
                if (leaf.Value != 22 || view.Current != 24 || ViaReference(leaf) != 24) return 3;
                view.Current += 1;
                if (leaf.Value != 27 || reference.Read() != 29) return 4;
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
            struct D : H { public readonly int& Other; public D(int& v) : base(v) { this.Other = v; } }
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
                int value = 20; D d = D(value); d.Value = 21;
                Outer outer = Outer(value); outer.Inner.Value = 42;
                H positional = H { value };
                if (value != 42 || d.Other != 42 || positional.Value != 42) return 1;
                C c = C(); A& view = c; A* pointer = &c;
                if (view.Read() != 42 || pointer->Read() != 42) return 2;
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
                Log.Trace = 0; { R a; a = R(1); a = R(2); } if (Log.Trace != 2) return 8;
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
                D* d = new D(); A* a = d; B& b = *d; C& c = *d;
                d->N = 10; if (a->Read() != 10 || b.Value != 10 || c[2] != 12) return 1;
                b.Value = 20; if (a->Read() != 20) return 2;
                c[2] = 40; if (a->Read() != 38) return 3;
                Both view = *a; if (view.Read() != 38 || view[2] != 40) return 4;
                view.Value = 40; if (c[2] != 42) return 5;
                view[1] = 43; if (b.Value != 42) return 6;
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
                D d = D(); A& a = d; B& b = d;
                if (a.M(2) != 42 || d.M(2) != 42 || b.M(1.0f) != 2) return 1;
                return 42;
            }
            """, optimization));
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
