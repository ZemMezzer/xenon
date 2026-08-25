using System.Text;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.Cli;

internal static class Program
{
    private const int Success = 0;
    private const int CompilationError = 1;
    private const int UsageError = 2;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal))
        {
            PrintUsage();
            return args.Length == 0 ? UsageError : Success;
        }

        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine("xenon 0.1.0-dev");
            return Success;
        }

        bool dumpTokens = args.Contains("--dump-tokens", StringComparer.Ordinal);
        bool emitLlvm = args.Contains("--emit-llvm", StringComparer.Ordinal);
        string[] unknownOptions = args
            .Where(argument => argument.StartsWith("-", StringComparison.Ordinal))
            .Where(argument => argument is not "--dump-tokens" and not "--emit-llvm")
            .ToArray();

        if (unknownOptions.Length > 0)
        {
            Console.Error.WriteLine($"error: unknown option '{unknownOptions[0]}'");
            return UsageError;
        }

        string[] sourcePaths = args.Where(argument => !argument.StartsWith("-", StringComparison.Ordinal)).ToArray();
        if (sourcePaths.Length == 0)
        {
            Console.Error.WriteLine("error: no input files");
            return UsageError;
        }

        var sources = new List<SourceText>(sourcePaths.Length);
        foreach (string sourcePath in sourcePaths)
        {
            if (!string.Equals(Path.GetExtension(sourcePath), ".xe", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"error: input file '{sourcePath}' must use the .xe extension");
                return UsageError;
            }

            if (!File.Exists(sourcePath))
            {
                Console.Error.WriteLine($"error: input file '{sourcePath}' does not exist");
                return UsageError;
            }

            string fullPath = Path.GetFullPath(sourcePath);
            sources.Add(SourceText.From(File.ReadAllText(fullPath, Encoding.UTF8), fullPath));
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
                string moduleName = Path.GetFileNameWithoutExtension(sources[0].Path);
                string llvmIr = new LlvmIrGenerator().Generate(compilation, moduleName);
                string outputPath = Path.ChangeExtension(sources[0].Path, ".ll");
                File.WriteAllText(outputPath, llvmIr, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Console.WriteLine($"Wrote LLVM IR to '{outputPath}'.");
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
        Console.WriteLine($"Analyzed {compilation.SyntaxTrees.Length} file(s), {memberCount} declaration(s), {tokenCount} token(s).");
        return Success;
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
        Console.WriteLine("  xenon [--dump-tokens] [--emit-llvm] <source.xe> [additional.xe ...]");
        Console.WriteLine("  xenon --version");
        Console.WriteLine("  xenon --help");
    }
}
