using System.Collections.Immutable;
using System.Text;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
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
        int argumentIndex = buildCommand ? 1 : 0;
        bool dumpTokens = false;
        bool emitLlvm = false;
        string profileName = "debug";
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
                default:
                    if (argument.StartsWith("--profile=", StringComparison.Ordinal))
                    {
                        profileName = argument["--profile=".Length..];
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

        if (buildCommand)
        {
            if (inputs.Count > 1)
            {
                return WriteUsageError("'xenon build' accepts at most one file, project, or directory");
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

        foreach (var diagnostic in compilation.Diagnostics)
        {
            DiagnosticWriter.Write(Console.Error, diagnostic);
        }

        if (compilation.HasErrors)
        {
            return CompilationError;
        }

        if (emitLlvm)
        {
            try
            {
                string llvmIr = new LlvmIrGenerator().Generate(compilation, input.Name);
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
        if (inputs.Count == 1)
        {
            XenonProject project = XenonProjectLoader.Resolve(inputs[0]);
            _ = project.GetProfile(profileName);
            string llvmOutputPath = project.IsImplicit && project.SourceFiles.Length == 1
                ? Path.ChangeExtension(project.SourceFiles[0], ".ll")
                : Path.Combine(project.RootDirectory, $"{project.Name}.ll");
            return new CompilationInput(
                project.Name,
                project.IsImplicit,
                project.SourceFiles,
                llvmOutputPath);
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
        return new CompilationInput(
            Path.GetFileNameWithoutExtension(firstSource),
            IsImplicit: true,
            sourceFiles.ToImmutable(),
            Path.ChangeExtension(firstSource, ".ll"));
    }

    private static int WriteUsageError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return UsageError;
    }

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
        Console.WriteLine("  xenon build [path] [--profile debug|release] [--dump-tokens] [--emit-llvm]");
        Console.WriteLine("  xenon [--dump-tokens] [--emit-llvm] <source.xe> [additional.xe ...]");
        Console.WriteLine("  xenon --version");
        Console.WriteLine("  xenon --help");
        Console.WriteLine();
        Console.WriteLine("If 'path' is a directory without a .xeproj, all .xe files below it form an implicit executable project.");
    }

    private sealed record CompilationInput(
        string Name,
        bool IsImplicit,
        ImmutableArray<string> SourceFiles,
        string LlvmOutputPath);
}
