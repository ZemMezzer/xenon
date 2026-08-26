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

                public readonly int Read()
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
                int Value;

                public int& Get()
                {
                    return Value;
                }

                public readonly readonly int& Get()
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

                public readonly int ReadResult()
                {
                    return Result;
                }

                public readonly int ReadMarker()
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
                int health;

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
                int stored;

                public virtual int Value
                {
                    get { return stored; }
                    set { stored = value; }
                }
            }

            struct Derived : Base
            {
                int adjusted;

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
                int stored;

                public virtual int Value
                {
                    get { return stored; }
                    set { stored = value; }
                }
            }

            struct Derived : Base
            {
                int adjusted;

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
                int stored;

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
                int stored;

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
                int stored;

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
