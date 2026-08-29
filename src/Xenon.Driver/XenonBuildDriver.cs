using Xenon.CodeGen.LLVM;
using Xenon.Compiler;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xenon.ProjectSystem;

namespace Xenon.Driver;

public enum BuildStage { ProjectLoading, Compilation, LlvmGeneration, Emit, Link, ArtifactValidation, Complete }
public enum BuildFailureKind { Compiler, NativeTool, Environment }

public sealed record XenonBuildRequest(
    string InputPath, string Profile = "debug", string? OutputRoot = null,
    string? TargetTriple = null, bool CompileOnly = false, TimeSpan? ToolTimeout = null);

public sealed class XenonBuildResult
{
    public bool Success { get; internal set; }
    public BuildStage Stage { get; internal set; } = BuildStage.ProjectLoading;
    public BuildFailureKind? FailureKind { get; internal set; }
    public string? Failure { get; internal set; }
    public XenonProject? Project { get; internal set; }
    public string? TargetTriple { get; internal set; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal set; } = [];
    public string? LlvmIrPath { get; internal set; }
    public string? ObjectPath { get; internal set; }
    public string? ArtifactPath { get; internal set; }
    public string? ImportLibraryPath { get; internal set; }
    public NativeProcessResult? LinkProcess { get; internal set; }
}

/// <summary>Reusable project-to-native pipeline. No console, CLI parser, or process-wide working-directory changes.</summary>
public sealed class XenonBuildDriver(INativeProcessRunner? processRunner = null)
{
    public XenonBuildResult Build(XenonBuildRequest request)
    {
        var result = new XenonBuildResult();
        try
        {
            XenonProject project = XenonProjectLoader.Resolve(request.InputPath);
            result.Project = project;
            XenonBuildProfile profile = project.GetProfile(request.Profile);
            SourceText[] sources = project.SourceFiles
                .Select(path => SourceText.From(File.ReadAllText(path), path)).ToArray();
            result.Stage = BuildStage.Compilation;
            Compilation compilation = Compilation.Create(sources);
            result.Diagnostics = compilation.Diagnostics;
            if (compilation.HasErrors) return Fail(result, BuildFailureKind.Compiler, "Compilation failed.");

            LlvmTargetOptions? target = null;
            if (!request.CompileOnly || compilation.RequiresTargetLayout)
            {
                string triple = request.TargetTriple ?? LlvmTargetPlatform.HostTriple;
                result.TargetTriple = triple;
                target = new LlvmTargetOptions(triple, profile.OptimizationLevel,
                    PositionIndependentCode: project.Type == XenonProjectType.SharedLibrary ||
                        (project.Type == XenonProjectType.Executable && LlvmTargetPlatform.GetObjectFileExtension(triple) != ".obj"));
                compilation = LlvmIrGenerator.BindForTarget(compilation, target);
                result.Diagnostics = compilation.Diagnostics;
                if (compilation.HasErrors) return Fail(result, BuildFailureKind.Compiler, "Target semantic analysis failed.");
            }
            if (request.CompileOnly)
            {
                result.Success = true;
                result.Stage = BuildStage.Complete;
                return result;
            }

            string root = Path.GetFullPath(request.OutputRoot ?? project.RootDirectory);
            string hostTriple = target!.Triple;
            result.ObjectPath = XenonBuildPaths.GetObjectFilePath(root, project.Name, request.Profile,
                hostTriple, LlvmTargetPlatform.GetObjectFileExtension(hostTriple));
            result.LlvmIrPath = Path.ChangeExtension(result.ObjectPath, ".ll");
            result.ArtifactPath = XenonBuildPaths.GetArtifactPath(root, project.Name, project.Type, request.Profile, hostTriple);
            bool executable = project.Type == XenonProjectType.Executable;
            result.Stage = BuildStage.LlvmGeneration;
            string ir = new LlvmIrGenerator().GenerateForTarget(compilation, target, project.Name, executable);
            Directory.CreateDirectory(Path.GetDirectoryName(result.LlvmIrPath)!);
            File.WriteAllText(result.LlvmIrPath, ir);
            result.Stage = BuildStage.Emit;
            new LlvmObjectEmitter().Emit(compilation, result.ObjectPath, target, project.Name, executable);

            result.Stage = BuildStage.Link;
            var linker = new NativeLinker(processRunner, request.ToolTimeout, project.RootDirectory);
            var options = new NativeLinkOptions(project.NativeLibraries, project.NativeLibraryPaths,
                compilation.SemanticModel.Functions.Select(function => function.Symbol)
                    .Where(symbol => symbol.IsExport).Select(NativeSymbolNames.Get).ToArray());
            LinkedNativeArtifact artifact;
            if (executable)
            {
                LinkedExecutable linked = linker.LinkExecutable(result.ObjectPath, result.ArtifactPath, hostTriple, options);
                artifact = new(linked.Path, linked.LinkerPath) { ProcessResult = linked.ProcessResult };
            }
            else if (project.Type == XenonProjectType.StaticLibrary)
                artifact = linker.CreateStaticLibrary(result.ObjectPath, result.ArtifactPath, hostTriple);
            else
                artifact = linker.LinkSharedLibrary(result.ObjectPath, result.ArtifactPath, hostTriple, options,
                    XenonBuildPaths.GetImportLibraryPath(root, project.Name, request.Profile, hostTriple));
            result.LinkProcess = artifact.ProcessResult;
            result.ImportLibraryPath = artifact.ImportLibraryPath;
            result.Stage = BuildStage.ArtifactValidation;
            if (!File.Exists(artifact.Path) || new FileInfo(artifact.Path).Length == 0)
                return Fail(result, BuildFailureKind.Environment, $"Expected artifact is missing or empty: {artifact.Path}");
            result.Success = true;
            result.Stage = BuildStage.Complete;
            return result;
        }
        catch (LinkerException exception)
        {
            result.LinkProcess = exception.ProcessResult;
            return Fail(result, exception.IsEnvironmentFailure ? BuildFailureKind.Environment : BuildFailureKind.NativeTool, exception.Message);
        }
        catch (LlvmCodeGenerationException exception)
        {
            return Fail(result, IsNativeEnvironmentFailure(exception) ? BuildFailureKind.Environment : BuildFailureKind.Compiler, exception.ToString());
        }
        catch (Exception exception) when (exception is ProjectSystemException or IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return Fail(result, BuildFailureKind.Environment, exception.ToString());
        }
        catch (Exception exception)
        {
            // A compiler implementation exception must retain its stage, not look like a harness preparation error.
            bool compilerStage = result.Stage is BuildStage.Compilation or BuildStage.LlvmGeneration or BuildStage.Emit;
            return Fail(result, compilerStage ? BuildFailureKind.Compiler : BuildFailureKind.Environment, exception.ToString());
        }
    }

    private static bool IsNativeEnvironmentFailure(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException ||
        (exception.InnerException is not null && IsNativeEnvironmentFailure(exception.InnerException));

    private static XenonBuildResult Fail(XenonBuildResult result, BuildFailureKind kind, string message)
    {
        result.FailureKind = kind;
        result.Failure = message;
        return result;
    }
}
