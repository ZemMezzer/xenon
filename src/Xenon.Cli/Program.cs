using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Driver;
using Xenon.ProjectSystem;

namespace Xenon.Cli;

internal static class Program
{
    private const int Success = 0;
    private const int CompilationError = 1;
    private const int UsageError = 2;

    public static int Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            PrintUsage();
            return Success;
        }

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine("xenon 0.1.0-dev");
            return Success;
        }

        bool buildCommand = args.Length > 0 && string.Equals(args[0], "build", StringComparison.Ordinal);
        bool runCommand = args.Length > 0 && string.Equals(args[0], "run", StringComparison.Ordinal);
        bool projectCommand = buildCommand || runCommand;
        int argumentIndex = projectCommand ? 1 : 0;
        bool dumpTokens = false;
        bool emitLlvm = false;
        bool emitObject = projectCommand;
        string profileName = "debug";
        string? targetTriple = null;
        var inputs = new List<string>();

        while (argumentIndex < args.Length)
        {
            string argument = args[argumentIndex++];
            switch (argument)
            {
                case "--dump-tokens":
                    dumpTokens = true;
                    break;
                case "--emit-llvm":
                    emitLlvm = true;
                    break;
                case "--emit-object":
                    emitObject = true;
                    break;
                case "--release":
                    profileName = "release";
                    break;
                case "--profile":
                    if (argumentIndex == args.Length)
                    {
                        return WriteUsageError("option '--profile' requires a value");
                    }

                    profileName = args[argumentIndex++];
                    break;
                case "--target":
                    if (argumentIndex == args.Length)
                    {
                        return WriteUsageError("option '--target' requires a value");
                    }

                    targetTriple = args[argumentIndex++];
                    break;
                default:
                    if (argument.StartsWith("--profile=", StringComparison.Ordinal))
                    {
                        profileName = argument["--profile=".Length..];
                    }
                    else if (argument.StartsWith("--target=", StringComparison.Ordinal))
                    {
                        targetTriple = argument["--target=".Length..];
                    }
                    else if (argument.StartsWith("-", StringComparison.Ordinal))
                    {
                        return WriteUsageError($"unknown option '{argument}'");
                    }
                    else
                    {
                        inputs.Add(argument);
                    }

                    break;
            }
        }

        if (profileName is not "debug" and not "release")
        {
            return WriteUsageError($"unknown build profile '{profileName}'");
        }

        if (targetTriple is not null && string.IsNullOrWhiteSpace(targetTriple))
        {
            return WriteUsageError("target triple cannot be empty");
        }

        if (targetTriple is not null && !emitObject && !emitLlvm)
        {
            return WriteUsageError("option '--target' requires 'build', '--emit-object', or '--emit-llvm'");
        }

        if (projectCommand)
        {
            if (inputs.Count > 1)
            {
                return WriteUsageError(
                    $"'xenon {(runCommand ? "run" : "build")}' accepts at most one file, project, or directory");
            }

            if (inputs.Count == 0)
            {
                inputs.Add(Directory.GetCurrentDirectory());
            }
        }
        else if (inputs.Count == 0)
        {
            PrintUsage();
            return UsageError;
        }

        CompilationInput input;
        try
        {
            input = ResolveInput(inputs, profileName);
        }
        catch (ProjectSystemException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return UsageError;
        }

        if (runCommand && !input.GenerateExecutableEntryPoint)
        {
            return WriteUsageError("'xenon run' requires an executable project");
        }

        var sources = new List<SourceText>(input.SourceFiles.Length);
        try
        {
            foreach (string sourcePath in input.SourceFiles)
            {
                sources.Add(SourceText.From(File.ReadAllText(sourcePath, Encoding.UTF8), sourcePath));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: cannot read source file: {exception.Message}");
            return CompilationError;
        }

        Compilation compilation = Compilation.Create([.. sources]);

        if (dumpTokens)
        {
            DumpTokens(compilation);
        }

        LlvmTargetOptions? selectedTarget = null;
        if (!compilation.HasErrors && (emitObject || emitLlvm || compilation.RequiresTargetLayout))
        {
            try
            {
                string effectiveTriple = targetTriple ?? LlvmTargetPlatform.HostTriple;
                bool positionIndependentCode = input.PositionIndependentCode ||
                    (input.GenerateExecutableEntryPoint && !IsWindowsTarget(effectiveTriple));
                selectedTarget = new LlvmTargetOptions(effectiveTriple, input.Profile.OptimizationLevel,
                    PositionIndependentCode: positionIndependentCode);
                compilation = LlvmIrGenerator.BindForTarget(compilation, selectedTarget);
            }
            catch (LlvmCodeGenerationException exception)
            {
                Console.Error.WriteLine($"error: {exception.Message}");
                return CompilationError;
            }
        }

        foreach (var diagnostic in compilation.Diagnostics)
        {
            DiagnosticWriter.Write(Console.Error, diagnostic);
        }

        if (compilation.HasErrors)
        {
            return CompilationError;
        }

        LlvmObjectFile? objectFile = null;
        if (emitObject)
        {
            try
            {
                string objectExtension = LlvmTargetPlatform.GetObjectFileExtension(selectedTarget!.Triple);
                string objectPath = XenonBuildPaths.GetObjectFilePath(
                    input.RootDirectory,
                    input.Name,
                    profileName,
                    selectedTarget.Triple,
                    objectExtension);
                objectFile = new LlvmObjectEmitter().Emit(
                    compilation,
                    objectPath,
                    selectedTarget,
                    input.Name,
                    input.GenerateExecutableEntryPoint);
                Console.WriteLine(
                    $"Wrote {objectFile.TargetTriple} object file to '{objectFile.Path}'.");
            }
            catch (LlvmCodeGenerationException exception)
            {
                Console.Error.WriteLine($"error: {exception.Message}");
                return CompilationError;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"error: cannot write object file: {exception.Message}");
                return CompilationError;
            }
        }

        LinkedExecutable? executable = null;
        if (projectCommand)
        {
            if (selectedTarget is null || objectFile is null)
            {
                Console.Error.WriteLine("error: executable linking requires an emitted object file");
                return CompilationError;
            }

            string hostTriple = LlvmTargetPlatform.HostTriple;
            if (!string.Equals(selectedTarget.Triple, hostTriple, StringComparison.OrdinalIgnoreCase))
            {
                if (runCommand)
                {
                    Console.Error.WriteLine(
                        $"error: cannot run target '{selectedTarget.Triple}' on host '{hostTriple}'");
                    return UsageError;
                }

                Console.WriteLine(
                    $"Skipped linking for cross target '{selectedTarget.Triple}'; the object file is ready for a configured target SDK/linker.");
            }
            else
            {
                try
                {
                    var nativeLinker = new NativeLinker();
                    var nativeOptions = new NativeLinkOptions(
                        input.NativeLibraries,
                        input.NativeLibraryPaths,
                        compilation.SemanticModel.Functions
                            .Select(function => function.Symbol)
                            .Where(symbol => symbol.IsExport)
                            .Select(NativeSymbolNames.Get)
                            .ToImmutableArray());
                    switch (input.ProjectType)
                    {
                        case XenonProjectType.Executable:
                            string executablePath = XenonBuildPaths.GetExecutablePath(
                                input.RootDirectory, input.Name, profileName, selectedTarget.Triple);
                            executable = nativeLinker.LinkExecutable(
                                objectFile.Path, executablePath, selectedTarget.Triple, nativeOptions);
                            Console.WriteLine($"Wrote executable to '{executable.Path}'.");
                            break;
                        case XenonProjectType.StaticLibrary:
                            string staticLibraryPath = XenonBuildPaths.GetStaticLibraryPath(
                                input.RootDirectory, input.Name, profileName, selectedTarget.Triple);
                            LinkedNativeArtifact staticLibrary = nativeLinker.CreateStaticLibrary(
                                objectFile.Path, staticLibraryPath, selectedTarget.Triple);
                            Console.WriteLine($"Wrote static library to '{staticLibrary.Path}'.");
                            break;
                        case XenonProjectType.SharedLibrary:
                            string sharedLibraryPath = XenonBuildPaths.GetSharedLibraryPath(
                                input.RootDirectory, input.Name, profileName, selectedTarget.Triple);
                            string? importLibraryPath = XenonBuildPaths.GetImportLibraryPath(
                                input.RootDirectory, input.Name, profileName, selectedTarget.Triple);
                            LinkedNativeArtifact sharedLibrary = nativeLinker.LinkSharedLibrary(
                                objectFile.Path,
                                sharedLibraryPath,
                                selectedTarget.Triple,
                                nativeOptions,
                                importLibraryPath);
                            Console.WriteLine($"Wrote shared library to '{sharedLibrary.Path}'.");
                            if (sharedLibrary.ImportLibraryPath is not null)
                            {
                                Console.WriteLine($"Wrote import library to '{sharedLibrary.ImportLibraryPath}'.");
                            }

                            break;
                        default:
                            throw new InvalidOperationException($"unsupported project type '{input.ProjectType}'");
                    }
                }
                catch (LinkerException exception)
                {
                    Console.Error.WriteLine($"error: {exception.Message}");
                    return CompilationError;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"error: cannot write executable: {exception.Message}");
                    return CompilationError;
                }
            }
        }

        if (emitLlvm)
        {
            try
            {
                string llvmIr = selectedTarget is null
                    ? new LlvmIrGenerator().Generate(compilation, input.Name)
                    : new LlvmIrGenerator().GenerateForTarget(
                        compilation,
                        selectedTarget,
                        input.Name,
                        input.GenerateExecutableEntryPoint);
                File.WriteAllText(
                    input.LlvmOutputPath,
                    llvmIr,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Console.WriteLine($"Wrote LLVM IR to '{input.LlvmOutputPath}'.");
            }
            catch (LlvmCodeGenerationException exception)
            {
                Console.Error.WriteLine($"error: {exception.Message}");
                return CompilationError;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"error: cannot write LLVM IR: {exception.Message}");
                return CompilationError;
            }
        }

        int tokenCount = compilation.SyntaxTrees.Sum(tree => tree.Tokens.Length - 1);
        int memberCount = compilation.SyntaxTrees.Sum(tree => tree.Root.Members.Length);
        string projectKind = input.IsImplicit ? "implicit" : "explicit";
        Console.WriteLine(
            $"Analyzed {projectKind} project '{input.Name}' ({profileName}): " +
            $"{compilation.SyntaxTrees.Length} file(s), {memberCount} declaration(s), {tokenCount} token(s).");

        if (runCommand)
        {
            if (executable is null)
            {
                Console.Error.WriteLine("error: no executable was produced");
                return CompilationError;
            }

            return RunExecutable(executable.Path, input.RootDirectory);
        }

        return Success;
    }

    private static CompilationInput ResolveInput(IReadOnlyList<string> inputs, string profileName)
    {
        if (inputs.Count == 1)
        {
            XenonProject project = XenonProjectLoader.Resolve(inputs[0]);
            XenonBuildProfile projectProfile = project.GetProfile(profileName);
            string llvmOutputPath = project.IsImplicit && project.SourceFiles.Length == 1
                ? Path.ChangeExtension(project.SourceFiles[0], ".ll")
                : Path.Combine(project.RootDirectory, $"{project.Name}.ll");
            return new CompilationInput(
                project.Name,
                project.IsImplicit,
                project.RootDirectory,
                project.SourceFiles,
                llvmOutputPath,
                projectProfile,
                project.Type,
                project.NativeLibraries,
                project.NativeLibraryPaths,
                project.Type is XenonProjectType.SharedLibrary,
                project.Type is XenonProjectType.Executable);
        }

        var sourceFiles = ImmutableArray.CreateBuilder<string>(inputs.Count);
        foreach (string input in inputs)
        {
            if (!string.Equals(Path.GetExtension(input), ".xe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectSystemException(
                    "multiple inputs must all be explicit .xe source files");
            }

            string fullPath = Path.GetFullPath(input);
            if (!File.Exists(fullPath))
            {
                throw new ProjectSystemException($"input file '{input}' does not exist");
            }

            sourceFiles.Add(fullPath);
        }

        string firstSource = sourceFiles[0];
        string rootDirectory = Path.GetDirectoryName(firstSource)!;
        XenonBuildProfile defaultProfile = profileName == "release"
            ? XenonBuildProfile.Release
            : XenonBuildProfile.Debug;
        return new CompilationInput(
            Path.GetFileNameWithoutExtension(firstSource),
            IsImplicit: true,
            rootDirectory,
            sourceFiles.ToImmutable(),
            Path.ChangeExtension(firstSource, ".ll"),
            defaultProfile,
            XenonProjectType.Executable,
            NativeLibraries: [],
            NativeLibraryPaths: [],
            PositionIndependentCode: false,
            GenerateExecutableEntryPoint: true);
    }

    private static int WriteUsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return UsageError;
    }

    private static int RunExecutable(string executablePath, string workingDirectory)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
            };
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"failed to start executable '{executablePath}'");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"error: cannot run executable '{executablePath}': {exception.Message}");
            return CompilationError;
        }
    }

    private static bool IsWindowsTarget(string triple) =>
        triple.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
        triple.Contains("win32", StringComparison.OrdinalIgnoreCase);

    private static void DumpTokens(Compilation compilation)
    {
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            Console.WriteLine($"{tree.Source.Path}:");
            foreach (SyntaxToken token in tree.Tokens)
            {
                string value = token.Value is null ? string.Empty : $" value={token.Value}";
                Console.WriteLine($"  {token.Location.Start.Line + 1}:{token.Location.Start.Character + 1} {token.Kind} '{Escape(token.Text)}'{value}");
            }
        }
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static void PrintUsage()
    {
        Console.WriteLine("Xenon compiler");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  xenon build [path] [--profile debug|release] [--target triple] [--dump-tokens] [--emit-llvm]");
        Console.WriteLine("  xenon run [path] [--profile debug|release]");
        Console.WriteLine("  xenon [--dump-tokens] [--emit-llvm] [--emit-object] <source.xe> [additional.xe ...]");
        Console.WriteLine("  xenon --version");
        Console.WriteLine("  xenon --help");
        Console.WriteLine();
        Console.WriteLine("If 'path' is a directory without a .xeproj, all .xe files below it form an implicit executable project.");
    }

    private sealed record CompilationInput(
        string Name,
        bool IsImplicit,
        string RootDirectory,
        ImmutableArray<string> SourceFiles,
        string LlvmOutputPath,
        XenonBuildProfile Profile,
        XenonProjectType ProjectType,
        ImmutableArray<string> NativeLibraries,
        ImmutableArray<string> NativeLibraryPaths,
        bool PositionIndependentCode,
        bool GenerateExecutableEntryPoint);
}
