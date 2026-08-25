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

            extern int puts(const byte* text);

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

            extern int puts(const byte* text);

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
