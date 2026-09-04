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
    string? TargetTriple = null, bool CompileOnly = false, TimeSpan? ToolTimeout = null,
    bool SkipLink = false);

public sealed class XenonBuildResult
{
    public bool Success { get; internal set; }
    public BuildStage Stage { get; internal set; } = BuildStage.ProjectLoading;
    public BuildFailureKind? FailureKind { get; internal set; }
    public string? Failure { get; internal set; }
    public XenonProject? Project { get; internal set; }
    public XenonProjectGraph? ProjectGraph { get; internal set; }
    public Compilation? Compilation { get; internal set; }
    public string? TargetTriple { get; internal set; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; internal set; } = [];
    public string? LlvmIrPath { get; internal set; }
    public string? ObjectPath { get; internal set; }
    public string? ArtifactPath { get; internal set; }
    public string? ImportLibraryPath { get; internal set; }
    public NativeProcessResult? LinkProcess { get; internal set; }
    /// <summary>True when native linking was intentionally omitted for an object-only or foreign-target build.</summary>
    public bool NativeLinkSkipped { get; internal set; }
    public bool IsRunnable => Success && Project?.Type == XenonProjectType.Executable &&
        !NativeLinkSkipped && ArtifactPath is not null;
}

/// <summary>Reusable project-to-native pipeline. No console, CLI parser, or process-wide working-directory changes.</summary>
public sealed class XenonBuildDriver(INativeProcessRunner? processRunner = null)
{
    public XenonBuildResult Build(XenonBuildRequest request)
    {
        var result = new XenonBuildResult();
        try
        {
            XenonProjectGraph graph = XenonProjectGraph.Load(request.InputPath);
            result.ProjectGraph = graph;
            result.Project = graph.Root;
            string outputRoot = Path.GetFullPath(request.OutputRoot ?? graph.Root.RootDirectory);
            string triple = request.TargetTriple ?? LlvmTargetPlatform.HostTriple;
            bool canLinkForHost = string.Equals(
                triple, LlvmTargetPlatform.HostTriple, StringComparison.OrdinalIgnoreCase);
            result.TargetTriple = triple;
            var compilations = new Dictionary<string, Compilation>(StringComparer.OrdinalIgnoreCase);
            var artifacts = new Dictionary<string, LinkedNativeArtifact>(StringComparer.OrdinalIgnoreCase);

            foreach (XenonProject project in graph.BuildOrder)
            {
                XenonBuildProfile profile = project.GetProfile(request.Profile);
                result.Stage = BuildStage.Compilation;
                Compilation compilation = XenonProjectCompilationFactory.Create(
                    project, request.Profile, compilations);
                if (compilation.HasErrors)
                {
                    result.Diagnostics = compilation.Diagnostics;
                    return Fail(result, BuildFailureKind.Compiler,
                        $"Compilation failed for project '{project.Name}'.");
                }

                LlvmTargetOptions? target = null;
                if (!request.CompileOnly || compilation.RequiresTargetLayout)
                {
                    target = new LlvmTargetOptions(triple, profile.OptimizationLevel,
                        PositionIndependentCode: RequiresPositionIndependentCode(project.Type, triple));
                    compilation = LlvmIrGenerator.BindForTarget(compilation, target);
                    if (compilation.HasErrors)
                    {
                        result.Diagnostics = compilation.Diagnostics;
                        return Fail(result, BuildFailureKind.Compiler,
                            $"Target semantic analysis failed for project '{project.Name}'.");
                    }
                }
                compilations.Add(project.Identity, compilation);
                if (ReferenceEquals(project, graph.Root) || project.Identity == graph.Root.Identity)
                {
                    result.Compilation = compilation;
                    result.Diagnostics = compilation.Diagnostics;
                }
                if (request.CompileOnly) continue;

                string objectPath = XenonBuildPaths.GetObjectFilePath(outputRoot, project.Name,
                    request.Profile, triple, LlvmTargetPlatform.GetObjectFileExtension(triple));
                string llvmIrPath = Path.ChangeExtension(objectPath, ".ll");
                string artifactPath = XenonBuildPaths.GetArtifactPath(outputRoot, project.Name,
                    project.Type, request.Profile, triple);
                if (project.Identity == graph.Root.Identity)
                {
                    result.ObjectPath = objectPath;
                    result.LlvmIrPath = llvmIrPath;
                }
                result.Stage = BuildStage.LlvmGeneration;
                LlvmCodeGenerationOptions codeGenerationOptions = CreateCodeGenerationOptions(
                    graph, project, compilations);
                string ir = new LlvmIrGenerator().GenerateForTarget(
                    compilation, target!, project.Name, codeGenerationOptions);
                Directory.CreateDirectory(Path.GetDirectoryName(llvmIrPath)!);
                File.WriteAllText(llvmIrPath, ir);
                result.Stage = BuildStage.Emit;
                new LlvmObjectEmitter().Emit(
                    compilation, objectPath, target!, project.Name, codeGenerationOptions);

                if (request.SkipLink || !canLinkForHost)
                {
                    if (project.Identity == graph.Root.Identity)
                        result.NativeLinkSkipped = true;
                    continue;
                }

                result.Stage = BuildStage.Link;
                var linker = new NativeLinker(processRunner, request.ToolTimeout, project.RootDirectory);
                string[] dependencyArtifacts = graph.GetNativeLinkOrder(project)
                    .Select(dependency => artifacts[dependency.Identity])
                    .Select(artifact => artifact.ImportLibraryPath ?? artifact.Path).ToArray();
                IEnumerable<string> exportedSymbols = project.Type == XenonProjectType.SharedLibrary
                    ? LlvmIrGenerator.GetProjectNativeExports(compilation, project.Name).Select(export =>
                        export.IsData && IsWindowsTarget(triple) ? $"{export.Name},DATA" : export.Name)
                    : compilation.SemanticModel.Functions.Select(function => function.Symbol)
                        .Where(symbol => symbol.IsExport).Select(NativeSymbolNames.Get);
                bool requiresThreadingRuntime =
                    LlvmIrGenerator.RequiresNativeThreadingRuntime(compilation) ||
                    graph.GetNativeLinkOrder(project)
                        .Where(dependency => dependency.Type == XenonProjectType.StaticLibrary)
                        .Any(dependency => LlvmIrGenerator.RequiresNativeThreadingRuntime(
                            compilations[dependency.Identity]));
                var options = new NativeLinkOptions(
                    project.NativeLibraries.AddRange(dependencyArtifacts),
                    project.NativeLibraryPaths,
                    exportedSymbols.Distinct(StringComparer.Ordinal).ToArray(),
                    RequiresThreadingRuntime: requiresThreadingRuntime);
                LinkedNativeArtifact artifact;
                if (project.Type == XenonProjectType.Executable)
                {
                    LinkedExecutable linked = linker.LinkExecutable(objectPath, artifactPath, triple, options);
                    artifact = new(linked.Path, linked.LinkerPath) { ProcessResult = linked.ProcessResult };
                }
                else if (project.Type == XenonProjectType.StaticLibrary)
                    artifact = linker.CreateStaticLibrary(objectPath, artifactPath, triple);
                else
                    artifact = linker.LinkSharedLibrary(objectPath, artifactPath, triple, options,
                        XenonBuildPaths.GetImportLibraryPath(outputRoot, project.Name, request.Profile, triple));
                result.Stage = BuildStage.ArtifactValidation;
                if (!File.Exists(artifact.Path) || new FileInfo(artifact.Path).Length == 0)
                    return Fail(result, BuildFailureKind.Environment,
                        $"Expected artifact is missing or empty: {artifact.Path}");
                artifacts.Add(project.Identity, artifact);
                if (project.Identity == graph.Root.Identity)
                {
                    result.ArtifactPath = artifact.Path;
                    result.ImportLibraryPath = artifact.ImportLibraryPath;
                    result.LinkProcess = artifact.ProcessResult;
                }
            }
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

    private static bool IsWindowsTarget(string triple) =>
        triple.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
        triple.Contains("win32", StringComparison.OrdinalIgnoreCase);

    internal static bool RequiresPositionIndependentCode(XenonProjectType projectType, string triple) =>
        projectType == XenonProjectType.SharedLibrary ||
        LlvmTargetPlatform.GetObjectFileExtension(triple) != ".obj";

    private static LlvmCodeGenerationOptions CreateCodeGenerationOptions(
        XenonProjectGraph graph,
        XenonProject project,
        IReadOnlyDictionary<string, Compilation> compilations) =>
        new(project.Name, graph.GetTransitiveDependencies(project).Select(dependency =>
            new LlvmNativeReference(
                compilations[dependency.Identity],
                dependency.Type == XenonProjectType.SharedLibrary
                    ? LlvmNativeReferenceKind.Shared
                    : LlvmNativeReferenceKind.Static,
                dependency.Name)));

    private static XenonBuildResult Fail(XenonBuildResult result, BuildFailureKind kind, string message)
    {
        result.FailureKind = kind;
        result.Failure = message;
        return result;
    }
}
