using System.Buffers.Binary;
using System.Collections.Immutable;
using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xenon.Compiler.Tests.ProjectSystem;
using Xenon.Driver;
using Xenon.ProjectSystem;
using Xunit;

namespace Xenon.Compiler.Tests.Libraries;

public sealed class TargetSpecificXelibTests
{
    private const string Windows = "x86_64-pc-windows-msvc";
    private const string Linux = "x86_64-unknown-linux-gnu";
    private const string ArmLinux = "aarch64-unknown-linux-gnu";
    private const string Mac = "aarch64-apple-darwin";
    private static byte[] Build(string source, string target = Windows, string[]? defines = null, string profile = "debug")
    {
        var options = new CompilationOptions(ConditionalCompilation: new(defines, target, profile));
        var compilation = Compilation.Create(options, [], SourceText.From(source, "removed-source.xe"));
        compilation = LlvmIrGenerator.BindForTarget(compilation, new(target));
        return XelibWriter.Write(compilation, new("TestLibrary"));
    }
    private static Compilation Consumer(LibraryCompilationReference library, string target = Windows,
        string body = "return Value();", string[]? defines = null, string profile = "debug") =>
        Compilation.Create(new CompilationOptions(ConditionalCompilation: new(defines, target, profile)), [library],
            SourceText.From("using Lib; namespace App; public int Main() { " + body + " }"));
    private static string Ir(LibraryCompilationReference library, string target = Windows, string body = "return Value();",
        string[]? defines = null, string profile = "debug", int optimization = 0)
    {
        var compilation = Consumer(library, target, body, defines, profile);
        Assert.False(compilation.HasErrors, string.Join("\n", compilation.Diagnostics));
        return new LlvmIrGenerator().GenerateForTarget(compilation, new(target, OptimizationLevel: optimization));
    }

    [Theory]
    [InlineData(Windows, 1, "WinApi")]
    [InlineData(Linux, 2, "PosixApi")]
    [InlineData(Mac, 3, "PosixApi")]
    public void ExplicitTargetSelectsOnlyItsBoundProgram(string target, int value, string native)
    {
        byte[] bytes = Build("""
            namespace Lib;
            #if XENON_WINDOWS
            private extern int WinApi();
            public int Value() { return 1; }
            #elif XENON_LINUX
            private extern int PosixApi();
            public int Value() { return 2; }
            #else
            private extern int PosixApi();
            public int Value() { return 3; }
            #endif
            """, target);
        var library = XelibReader.Read(bytes);
        Assert.Equal(CompilationTarget.Normalize(target), library.TargetTriple);
        Assert.Contains($"ret i32 {value}", Ir(library, target));
        Assert.Contains(library.GlobalNamespace.Namespaces.Single().Functions, f => f.Name == native);
        Assert.DoesNotContain(library.GlobalNamespace.Namespaces.Single().Functions, f => f.Name == (native == "WinApi" ? "PosixApi" : "WinApi"));
        var container = XelibContainer.Read(bytes);
        Assert.False(container.Sections.ContainsKey(10));
        Assert.False(container.Sections.ContainsKey(11));
        Assert.DoesNotContain("#if", System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("removed-source.xe", System.Text.Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void MismatchIsDiagnosedBeforeSourceBindingOrCodeGeneration()
    {
        var library = XelibReader.Read(Build("namespace Lib; public int Value() { return 42; }"));
        var compilation = Consumer(library, Linux);
        var diagnostic = Assert.Single(compilation.Diagnostics);
        Assert.Equal("XE1101", diagnostic.Id);
        Assert.Contains(Windows, diagnostic.Message);
        Assert.Contains(Linux, diagnostic.Message);
        Assert.Throws<LlvmCodeGenerationException>(() => new LlvmIrGenerator().GenerateForTarget(compilation, new(Linux)));
        Assert.Contains("ret i32 42", Ir(library));
    }

    [Theory]
    [InlineData("namespace Lib;\n#if XENON_WINDOWS\nint A() { return 1; }\n#endif\nint Broken() { return Missing(); }")]
    [InlineData("namespace Lib;\n#if XENON_WINDOWS\nint Broken() { return Missing(); }\n#endif")]
    public void ActiveErrorsAlwaysPreventEmission(string source) => Assert.Throws<XelibFormatException>(() => Build(source));

    [Fact]
    public void InactiveErrorsRemainExcluded()
    {
        const string source = "namespace Lib;\n#if XENON_LINUX\nint Broken() { return Missing(); }\n#endif\npublic int Value() { return 42; }";
        Assert.Contains("ret i32 42", Ir(XelibReader.Read(Build(source))));
        Assert.Throws<XelibFormatException>(() => Build(source, Linux));
    }

    [Fact]
    public void DefinesAffectContentButConsumerDefinesAndOptimizationDoNotReselectIt()
    {
        const string source = "namespace Lib; public int Value() {\n#if FEATURE\nreturn 42;\n#else\nreturn 7;\n#endif\n}";
        var bytes = Build(source, defines: ["FEATURE", "UNUSED"]);
        Assert.Equal(bytes, Build(source, defines: ["UNUSED", "FEATURE", "FEATURE"], profile: "release"));
        Assert.NotEqual(bytes, Build(source));
        Assert.Equal(new[] { "FEATURE", "UNUSED" }, XelibMetadataReader.Read(bytes).Configuration.Defines);
        var library = XelibReader.Read(bytes);
        Assert.Contains("ret i32 42", Ir(library, profile: "release", optimization: 3));
        Assert.Contains("ret i32 42", Ir(library, defines: ["DIFFERENT"]));
        Assert.Contains("ret i32 7", Ir(XelibReader.Read(Build(source)), defines: ["FEATURE"]));
    }

    [Theory]
    [InlineData("XENON_DEBUG")]
    [InlineData("XENON_RELEASE")]
    public void ProfileConditionsAreRejectedForPrecompiledLibraries(string define)
    {
        var error = Assert.Throws<XelibFormatException>(() => Build($"namespace Lib;\n#if {define}\npublic int Value() {{ return 1; }}\n#endif"));
        Assert.Contains("unavailable in XELIB", error.Message);
    }

    [Theory]
    [InlineData(Windows, "ulong", 8, 4)]
    [InlineData(ArmLinux, "uint", 4, 8)]
    [InlineData(Mac, "uint", 4, 8)]
    public void LayoutAndNativeIntegersUseLibraryAbi(string target, string fieldType, int size, int cLongSize)
    {
        var library = XelibReader.Read(Build("""
            namespace Lib;
            public struct Native {
            #if XENON_X64
                public ulong Value;
            #else
                public uint Value;
            #endif
            }
            public const nuint Size = sizeof(Native);
            public const nuint Alignment = alignof(Native);
            public const nuint NativeLong = sizeof(clong);
            public int Value() { return cast<int>(Size + Alignment + NativeLong); }
            """, target));
        Assert.Equal(fieldType, library.GlobalNamespace.Namespaces.Single().Structs.Single().Fields.Single().Type.Name);
        Assert.All(library.GlobalNamespace.Namespaces.Single().Constants,
            constant => Assert.Equal(ConstantEvaluationState.Evaluated, constant.EvaluationState));
        Assert.Contains($"ret i32 {size * 2 + cLongSize}", Ir(library, target, optimization: 3));
    }

    [Fact]
    public void SourceIndependentGenericStructAndWholeProgramOptimizationSurvive()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.Write("library.xe", """
            namespace Lib;
            public struct Box<T> {
                public T Item;
                public T& Get() { return Item; }
            }
            public int AddTwo(int value) { return value + 2; }
            """);
        string file = directory.PathOf("library.xe");
        byte[] bytes = Build(File.ReadAllText(file));
        File.Delete(file);
        var library = XelibReader.Read(bytes);
        Assert.Contains("ret i32 42", Ir(library, body: "Box<int> box = Box<int>(); box.Item = 40; return AddTwo(box.Get());", optimization: 3));
        var metadata = XelibReader.Read(bytes, metadataOnly: true);
        Assert.Empty(metadata.ImplementationFunctions);
        Assert.False(Consumer(metadata, body: "Box<int> box = Box<int>(); box.Item = 40; return AddTwo(box.Get());").HasErrors);
    }

    [Fact]
    public void WriterRequiresResolvedLayoutAndRejectsForeignLayoutProvider()
    {
        var compilation = Compilation.Create(new CompilationOptions(ConditionalCompilation: new(targetTriple: Windows)), [],
            SourceText.From("namespace Lib; public const nuint Size = sizeof(clong);"));
        Assert.Throws<XelibFormatException>(() => XelibWriter.Write(compilation, new("TestLibrary")));
        var foreign = LlvmIrGenerator.BindForTarget(compilation, new(Linux));
        Assert.Throws<ArgumentException>(() => compilation.WithTargetLayout(foreign.TargetLayout!));
        var bound = LlvmIrGenerator.BindForTarget(compilation, new(Windows));
        Assert.NotEmpty(XelibWriter.Write(bound, new("TestLibrary")));
    }

    [Fact]
    public void SourceReferenceWithDefaultOptionsRetainsBoundAbi()
    {
        string host = CompilationTarget.DefaultTriple;
        string other = CompilationTarget.GetOperatingSystem(host) == 1 ? Linux : Windows;
        var unbound = Compilation.Create(SourceText.From("namespace Lib; public const nuint Size = sizeof(clong);"));
        var layout = LlvmIrGenerator.BindForTarget(unbound, new(host)).TargetLayout!;
        var bound = unbound.WithTargetLayout(layout);
        Assert.Null(bound.Options.ConditionalOptions.TargetTriple);
        var reference = new SourceCompilationReference(bound);
        Assert.Equal(host, reference.TargetTriple);
        var consumer = Compilation.Create(new CompilationOptions(ConditionalCompilation: new(targetTriple: other)),
            [reference], SourceText.From("using Lib; namespace App; public nuint Value() { return Size; }"));
        Assert.Contains(consumer.Diagnostics, diagnostic => diagnostic.Id == "XE1101");
        Assert.Throws<XelibFormatException>(() => XelibWriter.Write(consumer, new("BadLibrary")));
    }

    [Fact]
    public void SourceReferenceCannotHideTargetOfImportedLibraryConstants()
    {
        string host = CompilationTarget.DefaultTriple;
        string other = CompilationTarget.GetOperatingSystem(host) == 1 ? Linux : Windows;
        var basis = XelibReader.Read(Build("namespace Lib; public const nuint Size = sizeof(clong);", host));
        var bridge = Compilation.Create(new CompilationOptions(), [basis], SourceText.From(
            "using Lib; namespace Bridge; public const nuint BoundSize = Size;"));
        Assert.False(bridge.HasErrors, string.Join("\n", bridge.Diagnostics));
        Assert.Null(bridge.TargetLayout);
        var reference = new SourceCompilationReference(bridge);
        Assert.Equal(host, reference.TargetTriple);
        var consumer = Compilation.Create(new CompilationOptions(ConditionalCompilation: new(targetTriple: other)),
            [reference], SourceText.From("using Bridge; namespace App; public nuint Value() { return BoundSize; }"));
        Assert.Contains(consumer.Diagnostics, diagnostic => diagnostic.Id == "XE1101");
    }

    [Fact]
    public void UnsupportedFormatIsRejectedWithRebuildDiagnostic()
    {
        byte[] bytes = Build("namespace Lib; public int Value() { return 1; }");
        // Header: magic[8], header size[2], container[2], library IR[2].
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12, 2), (ushort)(XelibVersions.LibraryIr + 1));
        var error = Assert.Throws<XelibFormatException>(() => XelibReader.Read(bytes));
        Assert.Equal(XelibErrorCode.UnsupportedLibraryIrVersion, error.Code);
        Assert.Contains("rebuild", error.Message);
    }

    [Theory]
    [InlineData("{\"targetTriple\":\"arm64-apple-darwin\",\"defines\":[]}")]
    [InlineData("{\"targetTriple\":\"x86_64-pc-windows-msvc\",\"defines\":[\"B\",\"A\"]}")]
    [InlineData("{\"targetTriple\":\"x86_64-pc-windows-msvc\",\"defines\":[\"XENON_WINDOWS\"]}")]
    [InlineData("{}")]
    public void InvalidTargetMetadataIsRejectedEvenWithValidDigest(string metadata)
    {
        byte[] bytes = Build("namespace Lib; public int Value() { return 1; }");
        byte[] rewritten = RewriteConfiguration(bytes, System.Text.Encoding.UTF8.GetBytes(metadata));
        Assert.Equal(XelibErrorCode.InvalidRecord,
            Assert.Throws<XelibFormatException>(() => XelibMetadataReader.Read(rewritten)).Code);
    }

    [Fact]
    public void MissingTargetMetadataIsRejectedAndDependencyAbiCannotBeRelabeled()
    {
        byte[] basis = Build("namespace Lib; public int Value() { return 1; }");
        Assert.Equal(XelibErrorCode.MissingRequiredSection,
            Assert.Throws<XelibFormatException>(() => XelibReader.Read(RewriteConfiguration(basis, null))).Code);
        var dependency = XelibReader.Read(basis);
        var compilation = Compilation.Create(new CompilationOptions(ConditionalCompilation: new(targetTriple: Windows)),
            [dependency], SourceText.From("using Lib; namespace Engine; public int Read() { return Value(); }"));
        byte[] engine = XelibWriter.Write(compilation,
            new("Engine", Dependencies: [new(dependency, dependency.LibraryIdentity)]));
        byte[] relabeled = RewriteConfiguration(engine, XelibJson.Serialize(new XelibBuildConfiguration(Linux, [])));
        Assert.Equal(XelibErrorCode.TargetMismatch,
            Assert.Throws<XelibFormatException>(() => XelibReader.Read(relabeled, [dependency])).Code);
    }

    private static byte[] RewriteConfiguration(byte[] bytes, byte[]? configuration)
    {
        var container = XelibContainer.Read(bytes);
        var manifest = XelibMetadataReader.Read(bytes).Manifest;
        var sections = container.Sections.Where(pair => pair.Key != (uint)XelibSectionKind.Manifest &&
            pair.Key != (uint)XelibSectionKind.BuildConfiguration)
            .ToDictionary(pair => (XelibSectionKind)pair.Key, pair => pair.Value.ToArray());
        if (configuration is not null) sections[XelibSectionKind.BuildConfiguration] = configuration;
        string identity = XelibWriter.ComputeContentIdentity(manifest.Name, manifest.Version,
            sections.Select(pair => (pair.Key, (ReadOnlyMemory<byte>)pair.Value)));
        sections[XelibSectionKind.Manifest] = XelibJson.Serialize(manifest with { ContentIdentity = identity });
        return XelibContainer.Write(sections.Select(pair => new XelibSection(pair.Key, XelibSectionFlags.Required, pair.Value)));
    }

    [Fact]
    public void TargetAliasesAreCanonicalAndAbiEnvironmentsStayDistinct()
    {
        const string source = "namespace Lib; public int Value() { return 1; }";
        Assert.Equal(Build(source, "arm64-apple-darwin"), Build(source, Mac));
        Assert.Equal(Build(source, "windows-x64"), Build(source, Windows));
        Assert.NotEqual(Build(source, "x86_64-unknown-linux-musl"), Build(source, Linux));
    }

    [Fact]
    public void ProjectGraphPropagatesTargetAndSeparatesOutputsAndDefines()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("Core", "xenon-library", sources: [("core.xe", "namespace Core; public int Value() {\n#if XENON_WINDOWS\nreturn 1;\n#else\nreturn 2;\n#endif\n}")]);
        directory.WriteProject("Engine", "xenon-library", references: ["../Core/Core.xeproj"], sources: [("engine.xe", "using Core; namespace Engine; public int Value() { return Core.Value(); }")]);
        directory.WriteProject("Game", references: ["../Engine/Engine.xeproj"],
            sources: [("main.xe", "using Engine; namespace Game; int Main() { return Engine.Value(); }")]);
        var game = new XenonBuildDriver().Build(new(directory.PathOf("Game/Game.xeproj"),
            TargetTriple: ArmLinux, CompileOnly: true));
        Assert.True(game.Success, game.Failure + string.Join("\n", game.Diagnostics));
        Assert.All(game.Compilation!.References, reference => Assert.Equal(ArmLinux, reference.TargetTriple));
        string project = directory.PathOf("Engine/Engine.xeproj");
        var windows = new XenonBuildDriver().Build(new(project, TargetTriple: Windows));
        var linux = new XenonBuildDriver().Build(new(project, TargetTriple: ArmLinux));
        Assert.True(windows.Success, windows.Failure + string.Join("\n", windows.Diagnostics));
        Assert.True(linux.Success, linux.Failure + string.Join("\n", linux.Diagnostics));
        Assert.NotEqual(windows.ArtifactPath, linux.ArtifactPath);
        var basis = XelibReader.ReadFile(XenonBuildPaths.GetXenonLibraryPath(directory.PathOf("Engine"), "Core", "debug", ArmLinux));
        var engine = XelibReader.ReadFile(linux.ArtifactPath!, [basis]);
        Assert.Equal(ArmLinux, engine.TargetTriple);
        Assert.Equal(ArmLinux, Assert.Single(engine.Dependencies).TargetTriple);
        Assert.True(File.Exists(windows.ArtifactPath));
        var release = new XenonBuildDriver().Build(new(project, Profile: "release", TargetTriple: ArmLinux));
        Assert.True(release.Success, release.Failure);
        Assert.Equal(linux.ArtifactPath, release.ArtifactPath);
        Assert.Equal(XenonBuildPaths.GetXenonLibraryPath(directory.PathOf("Engine"), "Engine", "debug", ArmLinux, ["A", "B"]),
            XenonBuildPaths.GetXenonLibraryPath(directory.PathOf("Engine"), "Engine", "release", ArmLinux, ["B", "A"]));
    }

    [Fact]
    public async Task WorkspaceTargetSurvivesEditsAndRejectsWrongPrebuiltAbi()
    {
        using var directory = new WorkspaceTestDirectory();
        directory.WriteProject("App", sources: [("main.xe", "using Lib; namespace App; public int Main() { return Value(); }")]);
        directory.Write("App/Library.xelib", "");
        File.WriteAllBytes(directory.PathOf("App/Library.xelib"), Build("namespace Lib; public int Value() { return 42; }", ArmLinux));
        File.AppendAllText(directory.PathOf("App/App.xeproj"), "\n[libraries]\nlibraries = [\"Library.xelib\"]\n");
        using var workspace = Workspace.Create(directory.PathOf("App/App.xeproj"), targetTriple: ArmLinux);
        Assert.False((await workspace.CurrentSnapshot.RootProject.GetCompilationAsync()).HasErrors);
        var document = workspace.CurrentSnapshot.RootProject.Documents.Single();
        workspace.OpenDocument(document.Id, document.EffectiveText.Text + "\n// edit", new DocumentVersion(1));
        Assert.Equal(ArmLinux, (await workspace.CurrentSnapshot.RootProject.GetCompilationAsync()).Options.ConditionalOptions.TargetTriple);
        using var other = Workspace.Create(directory.PathOf("App/App.xeproj"), targetTriple: Windows);
        Assert.Contains((await other.CurrentSnapshot.RootProject.GetCompilationAsync()).Diagnostics, diagnostic => diagnostic.Id == "XE1101");
    }
}
