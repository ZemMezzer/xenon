using Xenon.Cli;
using Xenon.CodeGen.LLVM;
using Xenon.Driver;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.ProjectSystem;

public sealed class ConditionalBuildTests
{
    [Theory]
    [InlineData("-DFEATURE")]
    [InlineData("--define=FEATURE")]
    [InlineData("--define")]
    public void DirectCliAcceptsBooleanAliases(string option)
    {
        using var directory = new WorkspaceTestDirectory();
        directory.Write("main.xe", "namespace App;\n#if FEATURE\nint Main() { return 0; }\n#else\nUnknown value;\n#endif");
        string[] args = option == "--define" ? [directory.PathOf("main.xe"), option, "FEATURE"] : [directory.PathOf("main.xe"), option];
        Assert.Equal(0, Program.Main(args));
    }

    [Theory]
    [InlineData("-DXENON_WINDOWS")] [InlineData("-DA=1")] [InlineData("-D123ABC")]
    [InlineData("-DA-B")] [InlineData("-D")]
    public void CliRejectsMalformedAndReservedDefines(string option)
    {
        using var directory = new WorkspaceTestDirectory();
        directory.Write("main.xe", "namespace App; int Main() { return 0; }");
        Assert.Equal(2, Program.Main([directory.PathOf("main.xe"), option]));
    }

    [Fact]
    public void ProjectDefinesStayLocalAndInvocationDefinesReachDependencies()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Lib", "static-library", sources: [("lib.xe", """
            namespace Lib;
            #if GAME_DEV
            Unknown leaked;
            #endif
            #if VALIDATE
            public int Value() { return 42; }
            #else
            Unknown missing;
            #endif
            """)]);
        directory.WriteProject("App", references: ["../Lib/Lib.xeproj"], sources: [("main.xe", """
            using Lib;
            namespace App;
            #if GAME_DEV && VALIDATE
            int Main() { return Value(); }
            #else
            Unknown missing;
            #endif
            """)]);
        string path = directory.PathOf("App/App.xeproj");
        File.AppendAllText(path, "\n[build]\ndefines = [\"GAME_DEV\", \"GAME_DEV\"]\n");
        Assert.Equal(new[] { "GAME_DEV" }, XenonProjectLoader.Resolve(path).Defines);
        XenonBuildResult result = new XenonBuildDriver().Build(new(path, CompileOnly: true, Defines: ["VALIDATE"]));
        Assert.True(result.Success, result.Failure + string.Join("\n", result.Diagnostics));
        Assert.False(new XenonBuildDriver().Build(new(path, CompileOnly: true)).Success);
        Assert.Equal(0, Program.Main([path, "-DVALIDATE"]));
    }

    [Fact]
    public void GlobalDefinesSelectDistinctArtifactsAndOutputPaths()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", "xenon-library", sources: [("lib.xe", "namespace App;\n#if A\npublic int First() { return 1; }\n#else\npublic int Second() { return 2; }\n#endif")]);
        string path = directory.PathOf("App/App.xeproj");
        XenonBuildResult first = new XenonBuildDriver().Build(new(path, Defines: ["A"]));
        Assert.True(first.Success, first.Failure);
        byte[] before = File.ReadAllBytes(first.ArtifactPath!);
        XenonBuildResult second = new XenonBuildDriver().Build(new(path, Defines: ["B"]));
        Assert.True(second.Success, second.Failure);
        Assert.NotEqual(before, File.ReadAllBytes(second.ArtifactPath!));
        Assert.NotEqual(first.ArtifactPath, second.ArtifactPath);
        Assert.Equal(before, File.ReadAllBytes(first.ArtifactPath!));
        Assert.Contains(second.Compilation!.SemanticModel.GlobalNamespace.Namespaces.SelectMany(n => n.Functions), f => f.Name == "Second");
    }

    [Fact]
    public async Task EditorRebuildsActiveTreesAndPrimedSymbolIndexesWhenDefinesChange()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "namespace App;\n#if A\nint First() { return 1; }\n#else\nint Second() { return 2; }\n#endif")]);
        string path = directory.PathOf("App/App.xeproj");
        using Workspace workspace = directory.CreateWorkspace();
        var oldProject = workspace.CurrentSnapshot.RootProject;
        var oldSymbols = await oldProject.GetSymbolIndexAsync();
        var oldCompilation = await oldProject.GetCompilationAsync();
        Assert.Contains(oldSymbols.Entries, entry => entry.Name == "Second");
        File.AppendAllText(path, "\n[build]\ndefines = [\"A\"]\n");
        var updated = workspace.UpdateProject(oldProject.Id, XenonProjectLoader.Resolve(path)).RootProject;
        var symbols = await updated.GetSymbolIndexAsync();
        Assert.Contains(symbols.Entries, entry => entry.Name == "First");
        Assert.DoesNotContain(symbols.Entries, entry => entry.Name == "Second");
        Assert.NotSame(oldCompilation, await updated.GetCompilationAsync());
        Assert.False((await updated.GetCompilationAsync()).HasErrors);
        var document = Assert.Single(updated.Documents);
        _ = await updated.GetSemanticModelAsync(document.Id);
        var edited = workspace.OpenDocument(document.Id, document.EffectiveText.Text + "\n// edit", new DocumentVersion(1));
        Assert.Contains((await edited.RootProject.GetSymbolIndexAsync()).Entries, entry => entry.Name == "First");
        Assert.Equal(LlvmTargetPlatform.HostTriple, (await edited.RootProject.GetCompilationAsync()).Options.ConditionalOptions.TargetTriple);
    }

    [Fact]
    public void ConsumerDefinesCannotChangePrebuiltLibraryAfterItsSourcesAreRemoved()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Lib", "xenon-library", sources: [("lib.xe", """
            namespace Lib;
            public int LocalChoice() {
            #if PRIVATE
                return 42;
            #else
                return 0;
            #endif
            }
            public int Value() {
            #if GLOBAL
                return 10;
            #else
                return 20;
            #endif
            }
            """)]);
        string libraryProject = directory.PathOf("Lib/Lib.xeproj");
        File.AppendAllText(libraryProject, "\n[build]\ndefines = [\"PRIVATE\"]\n");
        XenonBuildResult built = new XenonBuildDriver().Build(new(libraryProject));
        Assert.True(built.Success, built.Failure + string.Join("\n", built.Diagnostics));
        byte[] original = File.ReadAllBytes(built.ArtifactPath!);
        File.Delete(directory.PathOf("Lib/src/lib.xe"));
        directory.WriteProject("App", sources: [("main.xe", "using Lib; namespace App; int Main() { if (LocalChoice() != 42) return 99; return Value(); }")]);
        string app = directory.PathOf("App/App.xeproj");
        File.AppendAllText(app, $"\n[build]\ndefines = [\"GLOBAL\"]\n[libraries]\nlibraries = [\"{built.ArtifactPath!.Replace('\\', '/')}\"]\n");
        Assert.Equal(20, Program.Main(["run", app]));
        Assert.Equal(20, Program.Main(["run", app, "-DGLOBAL"]));
        Assert.Equal(original, File.ReadAllBytes(built.ArtifactPath));
    }

    [Theory]
    [InlineData("XENON_RELEASE")] [InlineData("VALUE=1")] [InlineData("bad-name")]
    public void ProjectLoaderRejectsInvalidDefineNames(string define)
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App");
        string path = directory.PathOf("App/App.xeproj");
        File.AppendAllText(path, $"\n[build]\ndefines = [\"{define}\"]\n");
        Assert.Throws<ProjectSystemException>(() => XenonProjectLoader.Resolve(path));
    }
}
