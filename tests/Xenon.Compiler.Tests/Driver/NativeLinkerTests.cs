using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Text;
using Xenon.Driver;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.Driver;

public sealed class MacOsFactAttribute : FactAttribute
{
    public MacOsFactAttribute()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Skip = "This integration test requires macOS and Xcode Command Line Tools.";
        }
    }
}

public sealed class NativeLinkerTests
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AddDelegate(int left, int right);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float SumVectorDelegate(ref NativeVector2 value);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVector2
    {
        public float X;
        public float Y;
    }

    [Fact]
    public void Linker_CreatesAndRunsHostExecutable()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "integration",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "methods-integration",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(
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
                "using-integration",
                generateExecutableEntryPoint: true);
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

    [MacOsFact]
    public void Linker_CreatesAndRunsMacOsMachOExecutable()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace MacOsIntegration;

            extern int puts(readonly byte* text);

            int Main()
            {
                puts("Hello from Xenon on macOS");
                return 42;
            }
            """, "macos-integration.xe"));
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(positionIndependentCode: true);
        string objectPath = Path.Combine(
            directory,
            $"macos-integration{LlvmTargetPlatform.GetObjectFileExtension(target.Triple)}");
        string executablePath = XenonBuildPaths.GetExecutablePath(
            directory,
            "macos-integration",
            "debug",
            target.Triple);

        try
        {
            Assert.Contains("apple", target.Triple, StringComparison.OrdinalIgnoreCase);

            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(
                compilation,
                objectPath,
                target,
                "macos-integration",
                generateExecutableEntryPoint: true);
            LinkedExecutable executable = new NativeLinker().LinkExecutable(
                objectFile.Path,
                executablePath,
                target.Triple);

            AssertMacOsMachO(executable.Path, expectedFileType: 2);

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
            Assert.Contains("Hello from Xenon on macOS", output, StringComparison.Ordinal);
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
                compilation, objectPath, target, "math", generateExecutableEntryPoint: false);
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
                compilation, objectPath, target, "math", generateExecutableEntryPoint: false);
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
        Compilation executableCompilation = Compilation.Create(SourceText.From("""
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
                "math",
                generateExecutableEntryPoint: false);
            new NativeLinker().CreateStaticLibrary(libraryObject.Path, libraryPath, target.Triple);

            LlvmObjectFile executableObject = new LlvmObjectEmitter().Emit(
                executableCompilation,
                executableObjectPath,
                target,
                "app",
                generateExecutableEntryPoint: true);
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

    [Fact]
    public void Linker_ExportsStructPointerFunctionWithCLayout()
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
            """, "vector.xe"));
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
                compilation, objectPath, target, "vector", generateExecutableEntryPoint: false);
            LinkedNativeArtifact library = new NativeLinker().LinkSharedLibrary(
                objectFile.Path,
                libraryPath,
                target.Triple,
                new NativeLinkOptions(ExportedSymbols: ["Example_Sum"]),
                importLibraryPath);

            nint handle = NativeLibrary.Load(library.Path);
            try
            {
                SumVectorDelegate sum = Marshal.GetDelegateForFunctionPointer<SumVectorDelegate>(
                    NativeLibrary.GetExport(handle, "Example_Sum"));
                var vector = new NativeVector2 { X = 20.0f, Y = 22.0f };
                Assert.Equal(42.0f, sum(ref vector));
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "struct-copy",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "heap-struct",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "abstract-link",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "interface-reference-upcast",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "readonly-flow",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "readonly-overloads",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "field-initializers",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "default-field-initializers",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "properties",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "virtual-properties",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "compound-virtual-properties",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "interface-properties",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "interface-indexers",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
                "compound-interface-indexers",
                generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(compilation, objectPath, target, "constants", generateExecutableEntryPoint: true);
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
        Compilation compilation = Compilation.Create(SourceText.From("""
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
            LlvmObjectFile objectFile = new LlvmObjectEmitter().Emit(compilation, objectPath, target, "layout-constants", generateExecutableEntryPoint: true);
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
                return output;
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
            struct Base { public abstract int readonly Score(); }
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
                public ~Derived()
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

    private static int RunIterationFourProgram(string source, int optimization)
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace IterationFour; " + source, "iteration4.xe"));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        string directory = CreateTemporaryDirectory();
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost(optimizationLevel: optimization, positionIndependentCode: !OperatingSystem.IsWindows());
        try
        {
            string objectPath = Path.Combine(directory, "iteration4" + LlvmTargetPlatform.GetObjectFileExtension(target.Triple));
            var objectFile = new LlvmObjectEmitter().Emit(compilation, objectPath, target, "iteration4", generateExecutableEntryPoint: true);
            string executablePath = XenonBuildPaths.GetExecutablePath(directory, "iteration4", "debug", target.Triple);
            var executable = new NativeLinker().LinkExecutable(objectFile.Path, executablePath, target.Triple);
            using Process process = Process.Start(new ProcessStartInfo(executable.Path) { UseShellExecute = false, CreateNoWindow = true })!;
            if (!process.WaitForExit(30000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new Xunit.Sdk.XunitException("Iteration 4 program did not terminate within 30 seconds.");
            }
            return process.ExitCode;
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Compilation CreateLibraryCompilation() => Compilation.Create(SourceText.From("""
        namespace Integration;

        export int Add(int left, int right)
        {
            return left + right;
        }
        """, "math.xe"));

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
