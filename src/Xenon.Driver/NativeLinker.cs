using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Xenon.Driver;

public sealed record LinkedExecutable(
    string Path,
    string LinkerPath);

public sealed class NativeLinker
{
    public LinkedExecutable LinkExecutable(
        string objectFilePath,
        string outputPath,
        string targetTriple)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTriple);

        string objectPath = Path.GetFullPath(objectFilePath);
        if (!File.Exists(objectPath))
        {
            throw new LinkerException($"object file '{objectPath}' does not exist");
        }

        string executablePath = Path.GetFullPath(outputPath);
        string outputDirectory = Path.GetDirectoryName(executablePath)!;
        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileNameWithoutExtension(executablePath)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(executablePath)}");

        LinkerCommand command = CreateHostLinkerCommand(
            objectPath,
            temporaryPath,
            targetTriple);
        try
        {
            RunLinker(command);
            if (!File.Exists(temporaryPath) || new FileInfo(temporaryPath).Length == 0)
            {
                throw new LinkerException(
                    $"linker did not produce the expected executable '{executablePath}'");
            }

            File.Move(temporaryPath, executablePath, overwrite: true);
            return new LinkedExecutable(executablePath, command.ExecutablePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static LinkerCommand CreateHostLinkerCommand(
        string objectPath,
        string outputPath,
        string targetTriple)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!IsWindowsTriple(targetTriple))
            {
                throw new LinkerException(
                    $"cross-target linking for '{targetTriple}' requires a configured SDK and linker");
            }

            return CreateWindowsLinkerCommand(objectPath, outputPath, targetTriple);
        }

        if (OperatingSystem.IsLinux())
        {
            if (!targetTriple.Contains("linux", StringComparison.OrdinalIgnoreCase))
            {
                throw new LinkerException(
                    $"cross-target linking for '{targetTriple}' requires a configured sysroot and linker");
            }

            return CreateUnixLinkerCommand(objectPath, outputPath, addNoPie: false);
        }

        if (OperatingSystem.IsMacOS())
        {
            if (!targetTriple.Contains("darwin", StringComparison.OrdinalIgnoreCase) &&
                !targetTriple.Contains("macos", StringComparison.OrdinalIgnoreCase))
            {
                throw new LinkerException(
                    $"cross-target linking for '{targetTriple}' requires a configured SDK and linker");
            }

            return CreateUnixLinkerCommand(objectPath, outputPath, addNoPie: false);
        }

        throw new LinkerException("host executable linking is not supported on this operating system");
    }

    private static LinkerCommand CreateWindowsLinkerCommand(
        string objectPath,
        string outputPath,
        string targetTriple)
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

        string[] arguments =
        [
            "/NOLOGO",
            "/INCREMENTAL:NO",
            "/SUBSYSTEM:CONSOLE",
            $"/MACHINE:{machine}",
            $"/OUT:{outputPath}",
            $"/LIBPATH:{toolchain.VisualCppLibraryDirectory}",
            $"/LIBPATH:{toolchain.UniversalCrtLibraryDirectory}",
            $"/LIBPATH:{toolchain.WindowsSdkLibraryDirectory}",
            objectPath,
            "libcmt.lib",
            "libvcruntime.lib",
            "libucrt.lib",
            "oldnames.lib",
            "kernel32.lib",
        ];
        return new LinkerCommand(toolchain.LinkerPath, arguments);
    }

    private static LinkerCommand CreateUnixLinkerCommand(
        string objectPath,
        string outputPath,
        bool addNoPie)
    {
        string? linker = FindExecutableOnPath(
            Environment.GetEnvironmentVariable("CC"),
            "clang",
            "cc",
            "gcc");
        if (linker is null)
        {
            throw new LinkerException(
                "no host C linker driver was found; install clang or gcc, or set CC");
        }

        var arguments = new List<string> { objectPath, "-o", outputPath };
        if (addNoPie)
        {
            arguments.Add("-no-pie");
        }

        return new LinkerCommand(linker, arguments);
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

        string[] programFilesRoots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (string programFilesRoot in programFilesRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string version in new[] { "2022", "2019", "2017" })
            {
                string visualStudioRoot = Path.Combine(
                    programFilesRoot,
                    "Microsoft Visual Studio",
                    version);
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
            string[] hostCandidates = hostArchitecture == "Hostarm64"
                ? ["Hostarm64", "Hostx64"]
                : [hostArchitecture];
            foreach (string hostCandidate in hostCandidates)
            {
                string linkerPath = Path.Combine(
                    toolsRoot,
                    "bin",
                    hostCandidate,
                    architecture,
                    "link.exe");
                string libraryDirectory = Path.Combine(toolsRoot, "lib", architecture);
                if (!File.Exists(linkerPath) || !Directory.Exists(libraryDirectory))
                {
                    continue;
                }

                (string ucrt, string sdk) = DiscoverWindowsSdkLibraries(architecture);
                return new WindowsToolchain(linkerPath, libraryDirectory, ucrt, sdk);
            }
        }

        throw new LinkerException(
            "MSVC linker was not found; install the Visual Studio C++ build tools workload");
    }

    private static (string UniversalCrt, string WindowsSdk) DiscoverWindowsSdkLibraries(
        string architecture)
    {
        string windowsKitsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Windows Kits",
            "10",
            "Lib");
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

        throw new LinkerException(
            $"Windows SDK libraries for architecture '{architecture}' were not found");
    }

    private static void RunLinker(LinkerCommand command)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new LinkerException($"failed to start linker '{command.ExecutablePath}'");
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(standardOutputTask, standardErrorTask);
            string standardOutput = standardOutputTask.Result;
            string standardError = standardErrorTask.Result;
            if (process.ExitCode != 0)
            {
                string details = string.Join(
                    Environment.NewLine,
                    new[] { standardOutput, standardError }
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .Select(text => text.Trim()));
                throw new LinkerException(
                    $"linker '{command.ExecutablePath}' failed with exit code {process.ExitCode}" +
                    (details.Length == 0 ? string.Empty : $":{Environment.NewLine}{details}"));
            }
        }
        catch (LinkerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LinkerException(
                $"cannot execute linker '{command.ExecutablePath}': {exception.Message}",
                exception);
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

    private sealed record LinkerCommand(
        string ExecutablePath,
        IReadOnlyList<string> Arguments);

    private sealed record WindowsToolchain(
        string LinkerPath,
        string VisualCppLibraryDirectory,
        string UniversalCrtLibraryDirectory,
        string WindowsSdkLibraryDirectory);
}
