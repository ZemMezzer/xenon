using System.Runtime.InteropServices;

namespace Xenon.Driver;

public sealed record NativeLinkOptions(
    IReadOnlyList<string>? Libraries = null,
    IReadOnlyList<string>? LibraryPaths = null,
    IReadOnlyList<string>? ExportedSymbols = null);

public sealed record LinkedExecutable(string Path, string LinkerPath)
{
    public NativeProcessResult? ProcessResult { get; init; }
}

public sealed record LinkedNativeArtifact(string Path, string ToolPath, string? ImportLibraryPath = null)
{
    public NativeProcessResult? ProcessResult { get; init; }
}

public sealed class NativeLinker
{
    private readonly INativeProcessRunner _processRunner;
    private readonly TimeSpan _timeout;
    private readonly string? _workingDirectory;

    public NativeLinker(INativeProcessRunner? processRunner = null, TimeSpan? timeout = null, string? workingDirectory = null)
    {
        _processRunner = processRunner ?? new NativeProcessRunner();
        _timeout = timeout ?? TimeSpan.FromMinutes(2);
        _workingDirectory = workingDirectory;
    }

    public LinkedExecutable LinkExecutable(
        string objectFilePath,
        string outputPath,
        string targetTriple,
        NativeLinkOptions? options = null)
    {
        LinkedNativeArtifact artifact = CreateArtifact(
            objectFilePath, outputPath, targetTriple, NativeArtifactKind.Executable,
            options ?? new NativeLinkOptions(), importLibraryPath: null);
        return new LinkedExecutable(artifact.Path, artifact.ToolPath) { ProcessResult = artifact.ProcessResult };
    }

    public LinkedNativeArtifact CreateStaticLibrary(
        string objectFilePath,
        string outputPath,
        string targetTriple) =>
        CreateArtifact(
            objectFilePath, outputPath, targetTriple, NativeArtifactKind.StaticLibrary,
            new NativeLinkOptions(), importLibraryPath: null);

    public LinkedNativeArtifact LinkSharedLibrary(
        string objectFilePath,
        string outputPath,
        string targetTriple,
        NativeLinkOptions? options = null,
        string? importLibraryPath = null) =>
        CreateArtifact(
            objectFilePath, outputPath, targetTriple, NativeArtifactKind.SharedLibrary,
            options ?? new NativeLinkOptions(), importLibraryPath);

    private LinkedNativeArtifact CreateArtifact(
        string objectFilePath,
        string outputPath,
        string targetTriple,
        NativeArtifactKind kind,
        NativeLinkOptions options,
        string? importLibraryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTriple);

        string objectPath = Path.GetFullPath(objectFilePath);
        if (!File.Exists(objectPath))
        {
            throw new LinkerException($"object file '{objectPath}' does not exist");
        }

        string artifactPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        bool hasExports = (options.ExportedSymbols?.Count ?? 0) > 0;
        string? finalImportLibraryPath = importLibraryPath is null || !hasExports
            ? null
            : Path.GetFullPath(importLibraryPath);
        // PE import tables embed the DLL basename passed to LINK. Keep the final
        // basename while isolating the unpublished output in a temporary directory.
        string? temporaryDirectory = OperatingSystem.IsWindows() && kind == NativeArtifactKind.SharedLibrary
            ? Path.Combine(Path.GetDirectoryName(artifactPath)!,
                $".x-{Guid.NewGuid():N}"[..11])
            : null;
        if (temporaryDirectory is not null) Directory.CreateDirectory(temporaryDirectory);
        string temporaryPath = temporaryDirectory is null
            ? CreateTemporaryPath(artifactPath)
            : Path.Combine(temporaryDirectory, Path.GetFileName(artifactPath));
        string? temporaryImportLibraryPath = finalImportLibraryPath is null
            ? null
            : temporaryDirectory is null
                ? CreateTemporaryPath(finalImportLibraryPath)
                : Path.Combine(temporaryDirectory, Path.GetFileName(finalImportLibraryPath));

        LinkerCommand command = CreateHostCommand(
            objectPath, temporaryPath, targetTriple, kind, options, temporaryImportLibraryPath);
        NativeProcessResult? processResult = null;
        try
        {
            // Preserve existing callers' relative native-library semantics. The build driver supplies its project root.
            processResult = RunTool(command, _workingDirectory ?? Directory.GetCurrentDirectory());
            EnsureProduced(temporaryPath, kind.ToString().ToLowerInvariant());
            if (temporaryImportLibraryPath is not null)
            {
                EnsureProduced(temporaryImportLibraryPath, "import library");
                Directory.CreateDirectory(Path.GetDirectoryName(finalImportLibraryPath!)!);
                File.Move(temporaryImportLibraryPath, finalImportLibraryPath!, overwrite: true);
            }

            File.Move(temporaryPath, artifactPath, overwrite: true);
            return new LinkedNativeArtifact(artifactPath, command.ExecutablePath, finalImportLibraryPath)
            {
                ProcessResult = processResult,
            };
        }
        catch (LinkerException exception) when (exception.ProcessResult is null)
        {
            throw new LinkerException(exception.Message, exception)
            {
                ProcessResult = processResult,
                IsEnvironmentFailure = exception.IsEnvironmentFailure,
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LinkerException($"Cannot publish native artifact '{artifactPath}': {exception.Message}", exception)
            {
                ProcessResult = processResult,
            };
        }
        finally
        {
            DeleteIfExists(temporaryPath);
            DeleteIfExists(temporaryImportLibraryPath);
            if (temporaryImportLibraryPath is not null)
            {
                DeleteIfExists(Path.ChangeExtension(temporaryImportLibraryPath, ".exp"));
            }
            if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: false);
        }
    }

    private static LinkerCommand CreateHostCommand(
        string objectPath,
        string outputPath,
        string targetTriple,
        NativeArtifactKind kind,
        NativeLinkOptions options,
        string? importLibraryPath)
    {
        EnsureHostTarget(targetTriple);
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsCommand(objectPath, outputPath, targetTriple, kind, options, importLibraryPath);
        }

        return kind is NativeArtifactKind.StaticLibrary
            ? CreateUnixArchiveCommand(objectPath, outputPath)
            : CreateUnixLinkCommand(objectPath, outputPath, kind, options);
    }

    private static LinkerCommand CreateWindowsCommand(
        string objectPath,
        string outputPath,
        string targetTriple,
        NativeArtifactKind kind,
        NativeLinkOptions options,
        string? importLibraryPath)
    {
        string architecture = GetMsvcArchitecture(targetTriple);
        WindowsToolchain toolchain = DiscoverWindowsToolchain(architecture);
        string machine = architecture switch
        {
            "x64" => "X64",
            "x86" => "X86",
            "arm64" => "ARM64",
            _ => throw new LinkerException($"unsupported MSVC target architecture '{architecture}'"),
        };

        if (kind is NativeArtifactKind.StaticLibrary)
        {
            return new LinkerCommand(
                toolchain.LibrarianPath,
                ["/NOLOGO", $"/MACHINE:{machine}", $"/OUT:{outputPath}", objectPath]);
        }

        var arguments = new List<string>
        {
            "/NOLOGO",
            "/INCREMENTAL:NO",
            $"/MACHINE:{machine}",
            $"/OUT:{outputPath}",
            $"/LIBPATH:{toolchain.VisualCppLibraryDirectory}",
            $"/LIBPATH:{toolchain.UniversalCrtLibraryDirectory}",
            $"/LIBPATH:{toolchain.WindowsSdkLibraryDirectory}",
        };
        if (kind is NativeArtifactKind.Executable)
        {
            arguments.Add("/SUBSYSTEM:CONSOLE");
        }
        else
        {
            arguments.Add("/DLL");
            if (importLibraryPath is not null)
            {
                arguments.Add($"/IMPLIB:{importLibraryPath}");
            }

            foreach (string symbol in options.ExportedSymbols ?? [])
            {
                arguments.Add($"/EXPORT:{symbol}");
            }
        }

        foreach (string path in options.LibraryPaths ?? [])
        {
            arguments.Add($"/LIBPATH:{Path.GetFullPath(path)}");
        }

        arguments.Add(objectPath);
        arguments.AddRange(["libcmt.lib", "libvcruntime.lib", "libucrt.lib", "oldnames.lib", "kernel32.lib"]);
        foreach (string library in options.Libraries ?? [])
        {
            arguments.Add(GetWindowsLibraryArgument(library));
        }

        return new LinkerCommand(toolchain.LinkerPath, arguments);
    }

    private static LinkerCommand CreateUnixArchiveCommand(string objectPath, string outputPath)
    {
        string? archiver = FindExecutableOnPath(Environment.GetEnvironmentVariable("AR"), "llvm-ar", "ar");
        if (archiver is null)
        {
            throw new LinkerException("no host archiver was found; install llvm-ar or ar, or set AR");
        }

        return new LinkerCommand(archiver, ["rcs", outputPath, objectPath]);
    }

    private static LinkerCommand CreateUnixLinkCommand(
        string objectPath,
        string outputPath,
        NativeArtifactKind kind,
        NativeLinkOptions options)
    {
        string? linker = FindExecutableOnPath(
            Environment.GetEnvironmentVariable("CC"), "clang", "cc", "gcc");
        if (linker is null)
        {
            throw new LinkerException("no host C linker driver was found; install clang or gcc, or set CC");
        }

        var arguments = new List<string>();
        if (kind is NativeArtifactKind.SharedLibrary)
        {
            arguments.Add(OperatingSystem.IsMacOS() ? "-dynamiclib" : "-shared");
        }

        arguments.Add(objectPath);
        foreach (string path in options.LibraryPaths ?? [])
        {
            arguments.Add($"-L{Path.GetFullPath(path)}");
        }

        foreach (string library in options.Libraries ?? [])
        {
            arguments.Add(GetUnixLibraryArgument(library));
        }

        arguments.Add("-o");
        arguments.Add(outputPath);
        return new LinkerCommand(linker, arguments);
    }

    private static string GetWindowsLibraryArgument(string library)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(library);
        return IsLibraryPath(library) || Path.HasExtension(library) ? library : $"{library}.lib";
    }

    private static string GetUnixLibraryArgument(string library)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(library);
        return IsLibraryPath(library) || Path.HasExtension(library) ? library : $"-l{library}";
    }

    private static bool IsLibraryPath(string value) =>
        Path.IsPathFullyQualified(value) ||
        value.Contains(Path.DirectorySeparatorChar) ||
        value.Contains(Path.AltDirectorySeparatorChar);

    private static void EnsureHostTarget(string targetTriple)
    {
        bool compatible = OperatingSystem.IsWindows()
            ? IsWindowsTriple(targetTriple)
            : OperatingSystem.IsLinux()
                ? targetTriple.Contains("linux", StringComparison.OrdinalIgnoreCase)
                : OperatingSystem.IsMacOS() &&
                  (targetTriple.Contains("darwin", StringComparison.OrdinalIgnoreCase) ||
                   targetTriple.Contains("macos", StringComparison.OrdinalIgnoreCase));
        if (!compatible)
        {
            throw new LinkerException(
                $"cross-target linking for '{targetTriple}' requires a configured target SDK and linker");
        }
    }

    private static WindowsToolchain DiscoverWindowsToolchain(string architecture)
    {
        string hostArchitecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "Hostx86",
            Architecture.Arm64 => "Hostarm64",
            _ => "Hostx64",
        };
        var visualCppRoots = new List<string>();
        string? configuredTools = Environment.GetEnvironmentVariable("VCToolsInstallDir");
        if (!string.IsNullOrWhiteSpace(configuredTools))
        {
            visualCppRoots.Add(configuredTools);
        }

        foreach (string programFilesRoot in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string version in new[] { "2022", "2019", "2017" })
            {
                string visualStudioRoot = Path.Combine(programFilesRoot, "Microsoft Visual Studio", version);
                if (!Directory.Exists(visualStudioRoot))
                {
                    continue;
                }

                foreach (string editionDirectory in Directory.EnumerateDirectories(visualStudioRoot))
                {
                    string toolsRoot = Path.Combine(editionDirectory, "VC", "Tools", "MSVC");
                    if (Directory.Exists(toolsRoot))
                    {
                        visualCppRoots.AddRange(Directory.EnumerateDirectories(toolsRoot));
                    }
                }
            }
        }

        foreach (string toolsRoot in visualCppRoots
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(path => ParseVersion(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))))
        {
            string[] hostCandidates = hostArchitecture == "Hostarm64" ? ["Hostarm64", "Hostx64"] : [hostArchitecture];
            foreach (string hostCandidate in hostCandidates)
            {
                string toolDirectory = Path.Combine(toolsRoot, "bin", hostCandidate, architecture);
                string linkerPath = Path.Combine(toolDirectory, "link.exe");
                string librarianPath = Path.Combine(toolDirectory, "lib.exe");
                string libraryDirectory = Path.Combine(toolsRoot, "lib", architecture);
                if (!File.Exists(linkerPath) || !File.Exists(librarianPath) || !Directory.Exists(libraryDirectory))
                {
                    continue;
                }

                (string ucrt, string sdk) = DiscoverWindowsSdkLibraries(architecture);
                return new WindowsToolchain(linkerPath, librarianPath, libraryDirectory, ucrt, sdk);
            }
        }

        throw new LinkerException("MSVC linker was not found; install the Visual Studio C++ build tools workload");
    }

    private static (string UniversalCrt, string WindowsSdk) DiscoverWindowsSdkLibraries(string architecture)
    {
        string windowsKitsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "Lib");
        if (!Directory.Exists(windowsKitsRoot))
        {
            throw new LinkerException("Windows SDK libraries were not found");
        }

        foreach (string versionDirectory in Directory.EnumerateDirectories(windowsKitsRoot)
                     .OrderByDescending(path => ParseVersion(Path.GetFileName(path))))
        {
            string universalCrt = Path.Combine(versionDirectory, "ucrt", architecture);
            string windowsSdk = Path.Combine(versionDirectory, "um", architecture);
            if (File.Exists(Path.Combine(universalCrt, "libucrt.lib")) &&
                File.Exists(Path.Combine(windowsSdk, "kernel32.lib")))
            {
                return (universalCrt, windowsSdk);
            }
        }

        throw new LinkerException($"Windows SDK libraries for architecture '{architecture}' were not found");
    }

    private NativeProcessResult RunTool(LinkerCommand command, string workingDirectory)
    {
        NativeProcessResult result = _processRunner.RunAsync(new NativeProcessRequest(
            command.ExecutablePath, command.Arguments, workingDirectory, _timeout)).GetAwaiter().GetResult();
        if (result.StartError is null && !result.TimedOut && result.ExitCode == 0) return result;
        string reason = result.StartError is not null ? $"could not start: {result.StartError}"
            : result.TimedOut ? $"exceeded timeout of {_timeout.TotalSeconds} seconds"
            : $"failed with exit code {result.ExitCode}";
        throw new LinkerException($"native tool '{command.ExecutablePath}' {reason}\n{result.GetStdoutForDiagnostics()}\n{result.GetStderrForDiagnostics()}")
        {
            ProcessResult = result,
            IsEnvironmentFailure = result.StartError is not null || result.TerminationError is not null,
        };
    }
    private static string CreateTemporaryPath(string finalPath)
    {
        string directory = Path.GetDirectoryName(finalPath)!;
        Directory.CreateDirectory(directory);
        return Path.Combine(
            directory,
            $".{Path.GetFileNameWithoutExtension(finalPath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(finalPath)}");
    }

    private static void EnsureProduced(string path, string artifactName)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new LinkerException($"native tool did not produce the expected {artifactName} '{path}'");
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (path is not null && File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string GetMsvcArchitecture(string targetTriple)
    {
        string architecture = targetTriple.Split('-', 2)[0];
        return architecture switch
        {
            "x86_64" or "amd64" => "x64",
            "i386" or "i486" or "i586" or "i686" => "x86",
            "aarch64" or "arm64" => "arm64",
            _ => throw new LinkerException(
                $"target architecture '{architecture}' is not supported by the MSVC linker driver"),
        };
    }

    private static bool IsWindowsTriple(string triple) =>
        triple.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
        triple.Contains("win32", StringComparison.OrdinalIgnoreCase);

    private static string? FindExecutableOnPath(params string?[] names)
    {
        string[] pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string? name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (Path.IsPathFullyQualified(name) && File.Exists(name))
            {
                return Path.GetFullPath(name);
            }

            foreach (string directory in pathDirectories)
            {
                string path = Path.Combine(directory, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static Version ParseVersion(string text) =>
        Version.TryParse(text, out Version? version) ? version : new Version();

    private enum NativeArtifactKind
    {
        Executable,
        StaticLibrary,
        SharedLibrary,
    }

    private sealed record LinkerCommand(string ExecutablePath, IReadOnlyList<string> Arguments);

    private sealed record WindowsToolchain(
        string LinkerPath,
        string LibrarianPath,
        string VisualCppLibraryDirectory,
        string UniversalCrtLibraryDirectory,
        string WindowsSdkLibraryDirectory);
}
