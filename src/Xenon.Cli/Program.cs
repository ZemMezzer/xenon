using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xenon.Driver;
using Xenon.LanguageServer;
using Xenon.ProjectSystem;

namespace Xenon.Cli;

internal static class Program
{
    private const int Success = 0;
    private const int CompilationError = 1;
    private const int UsageError = 2;

    internal static string ProductVersion => XenonBuildInfo.Version;

    public static int Main(string[] args)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            PrintUsage();
            return Success;
        }

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine($"xenon {ProductVersion}");
            return Success;
        }

        if (args.Length > 0 && string.Equals(args[0], "lsp", StringComparison.Ordinal))
        {
            if (args.Length != 1)
                return WriteUsageError("'xenon lsp' does not accept command-line arguments");
            return RunLanguageServer();
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

        // Project commands use the reusable graph-aware build pipeline. The CLI only
        // selects a project/profile/target and presents the result.
        if (projectCommand)
        {
            return RunProjectCommand(inputs[0], profileName, targetTriple, runCommand, dumpTokens);
        }

        // A project file or directory always goes through the graph-aware driver,
        // including the legacy command shape (`xenon path --emit-llvm`).
        if (IsProjectShapedInput(inputs))
        {
            bool compileOnly = !emitObject && !emitLlvm;
            return RunProjectCommand(inputs[0], profileName, targetTriple, run: false, dumpTokens,
                compileOnly, skipLink: true);
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

        Compilation compilation = Compilation.Create(
            new CompilationOptions(CompilationOutputKind.Executable, input.Profile.EnableChecks),
            references: null,
            [.. sources]);

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
                selectedTarget = new LlvmTargetOptions(effectiveTriple, input.Profile.OptimizationLevel,
                    PositionIndependentCode: !IsWindowsTarget(effectiveTriple));
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
                    input.Name);
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

        if (emitLlvm)
        {
            try
            {
                string llvmIr = selectedTarget is null
                    ? new LlvmIrGenerator().Generate(compilation, input.Name)
                    : new LlvmIrGenerator().GenerateForTarget(
                        compilation,
                        selectedTarget,
                        input.Name);
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

        return Success;
    }

    private static CompilationInput ResolveInput(IReadOnlyList<string> inputs, string profileName)
    {
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
            defaultProfile);
    }

    private static int WriteUsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return UsageError;
    }

    private static int RunLanguageServer()
    {
        string? logPath = Environment.GetEnvironmentVariable("XENON_LSP_LOG_FILE");
        if (string.IsNullOrWhiteSpace(logPath))
            return LanguageServerEntryPoint.RunAsync(
                Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error)
                .GetAwaiter().GetResult();

        try
        {
            using var log = new StreamWriter(new FileStream(Path.GetFullPath(logPath),
                FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            return LanguageServerEntryPoint.RunAsync(
                Console.OpenStandardInput(), Console.OpenStandardOutput(), log)
                .GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"fatal: cannot open LSP log: {exception.Message}");
            return CompilationError;
        }
    }

    private static int RunProjectCommand(string inputPath, string profileName, string? targetTriple,
        bool run, bool dumpTokens, bool compileOnly = false, bool skipLink = false)
    {
        XenonBuildResult result = new XenonBuildDriver().Build(CreateProjectBuildRequest(
            inputPath, profileName, targetTriple, compileOnly, skipLink));
        foreach (Diagnostic diagnostic in result.Diagnostics)
            DiagnosticWriter.Write(Console.Error, diagnostic);
        if (!result.Success)
        {
            Console.Error.WriteLine($"error: {result.Failure}");
            return result.FailureKind == BuildFailureKind.Compiler ? CompilationError : UsageError;
        }
        if (run && result.Project!.Type != XenonProjectType.Executable)
            return WriteUsageError("'xenon run' requires an executable project");
        if (run && !result.IsRunnable)
            return WriteUsageError(
                $"cannot run target '{result.TargetTriple}' on host '{LlvmTargetPlatform.HostTriple}'");
        if (dumpTokens && result.Compilation is not null)
            DumpTokens(result.Compilation);
        if (result.ObjectPath is not null)
            Console.WriteLine($"Wrote {result.TargetTriple} object file to '{result.ObjectPath}'.");
        if (result.ArtifactPath is not null)
            Console.WriteLine($"Wrote artifact to '{result.ArtifactPath}'.");
        if (result.ImportLibraryPath is not null)
            Console.WriteLine($"Wrote import library to '{result.ImportLibraryPath}'.");
        if (result.NativeLinkSkipped && !compileOnly)
            Console.WriteLine($"Skipped native linking for target '{result.TargetTriple}'; emitted LLVM IR and object files.");
        if (!run) return Success;
        return RunExecutable(result.ArtifactPath!, result.Project!.RootDirectory);
    }

    internal static XenonBuildRequest CreateProjectBuildRequest(string inputPath, string profileName,
        string? targetTriple, bool compileOnly, bool skipLink) =>
        new(inputPath, profileName, TargetTriple: targetTriple, CompileOnly: compileOnly,
            SkipLink: skipLink);

    internal static bool IsProjectShapedInput(IReadOnlyList<string> inputs) =>
        inputs.Count == 1 &&
        (Directory.Exists(inputs[0]) ||
         string.Equals(Path.GetExtension(inputs[0]), ".xeproj", StringComparison.OrdinalIgnoreCase));

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
        Console.WriteLine("  xenon lsp");
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
        XenonBuildProfile Profile);
}
