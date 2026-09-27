using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Syntax;

public sealed class ConditionalCompilationTests
{
    private static Compilation Compile(string source, string[]? defines = null, string? target = null, string profile = "debug") =>
        Compilation.Create(new CompilationOptions(ConditionalCompilation: new(defines, target, profile)), [], SourceText.From(source, "conditions.xe"));
    private static string[] Names(Compilation compilation) => compilation.SemanticModel.GlobalNamespace.Namespaces
        .SelectMany(ns => ns.Functions).Select(f => f.Name).Order().ToArray();

    [Theory]
    [InlineData("A", "A", true)] [InlineData("A", "", false)]
    [InlineData("!A", "", true)] [InlineData("!A", "A", false)]
    [InlineData("A && B", "A,B", true)] [InlineData("A && B", "A", false)]
    [InlineData("A || B", "B", true)] [InlineData("A || B", "", false)]
    [InlineData("!A && B", "B", true)] [InlineData("!A && B", "A,B", false)]
    [InlineData("A && (B || C)", "A,C", true)] [InlineData("A && (B || C)", "B,C", false)]
    [InlineData("(A || B) && !C", "B", true)] [InlineData("(A || B) && !C", "A,C", false)]
    [InlineData("A || B && C", "A", true)] [InlineData("A || B && C", "B", false)]
    [InlineData("USE", "use", false)] [InlineData("UNKNOWN", "", false)]
    public void BooleanExpressionsSelectOnlyOneDeclaration(string expression, string symbols, bool expected)
    {
        Compilation result = Compile($"namespace Probe;\n#if {expression}\nint Yes() {{ return 1; }}\n#else\nint No() {{ return 2; }}\n#endif",
            symbols.Split(',', StringSplitOptions.RemoveEmptyEntries));
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(new[] { expected ? "Yes" : "No" }, Names(result));
    }

    [Fact]
    public void NestedInactiveSyntaxAndExclusiveDuplicatesAreExcluded()
    {
        Compilation result = Compile("""
            namespace Probe;
            #if NEVER
            @ garbage UnknownType broken { !!!!!
              #if A
              #elif B
              #else
              Missing unknown;
              #endif
            #elif A
            int Value() { return 1; }
            #elif B
            int Value() { return 2; }
            #else
            int Value() { return 3; }
            #endif
            struct Item {
            #if A
                int field;
            #else
                NoType field;
            #endif
            }
            int Use() {
            #if !A
                missing();
            #endif
                return Value();
            }
            """, ["A", "B"]);
        Assert.False(result.HasErrors, string.Join("\n", result.Diagnostics));
        Assert.Equal(new[] { "Use", "Value" }, Names(result));
    }

    [Fact]
    public void StringsCommentsAndOriginalDiagnosticLocationsArePreserved()
    {
        const string source = "namespace Probe;\r\n/*\r\n#if NEVER\r\n#endif\r\n*/\r\n// #if NEVER\r\nreadonly byte* Text() { return \"#if NEVER\"; }\r\n#if NEVER\r\ninvalid\r\n#endif\r\nint Broken() { return missing; }";
        Compilation result = Compile(source);
        var error = Assert.Single(result.Diagnostics.Where(d => d.Message.Contains("unknown identifier")));
        Assert.Equal(10, error.Location.Start.Line);
        Assert.Equal(source.IndexOf("missing", StringComparison.Ordinal), error.Location.Span.Start);
        Assert.Equal(source, error.Location.Source.Text);
    }

    [Theory]
    [InlineData("#else", "without #if")] [InlineData("#elif A", "without #if")]
    [InlineData("#endif", "without #if")] [InlineData("#if A", "unterminated")]
    [InlineData("#if\n#endif", "missing conditional expression")]
    [InlineData("#if (A\n#endif", "expected ')'")]
    [InlineData("#if A + B\n#endif", "invalid token")]
    [InlineData("#if A &&\n#endif", "expected an identifier")]
    [InlineData("#if A\n#else\n#else\n#endif", "multiple #else")]
    [InlineData("#if A\n#else\n#elif B\n#endif", "#elif after #else")]
    [InlineData("#define A", "unsupported conditional directive")]
    [InlineData("#if_A\n#endif", "unsupported conditional directive")]
    [InlineData("#if 123\n#endif", "expected an identifier")]
    [InlineData("#if A\n#endif junk", "unexpected text")]
    public void MalformedDirectivesHaveSourceDiagnostics(string directives, string message)
    {
        Compilation result = Compile("namespace Probe;\n" + directives);
        Assert.Contains(result.Diagnostics, d => d.Id == "XE1100" && d.Message.Contains(message));
        Assert.All(result.Diagnostics.Where(d => d.Id == "XE1100"), d => Assert.Equal("conditions.xe", d.Location.Path));
    }

    [Theory]
    [InlineData("x86_64-pc-windows-msvc", "XENON_WINDOWS,XENON_X64")]
    [InlineData("x86_64-unknown-linux-gnu", "XENON_LINUX,XENON_UNIX,XENON_X64")]
    [InlineData("aarch64-apple-darwin", "XENON_ARM64,XENON_MACOS,XENON_UNIX")]
    public void BuiltinsUseExplicitTarget(string target, string expected)
    {
        var options = new ConditionalCompilationOptions(targetTriple: target);
        Assert.Equal((expected + ",XENON_DEBUG").Split(',').Order(), options.Defines);
    }

    [Fact]
    public void DefinesAreFrozenSortedAndReserved()
    {
        var source = new List<string> { "B", "A", "A" };
        var options = new ConditionalCompilationOptions(source);
        source.Add("C");
        Assert.DoesNotContain("C", options.Defines);
        Assert.Equal(options, new ConditionalCompilationOptions(["A", "B"]));
        foreach (string invalid in new[] { "", "123ABC", "A-B", "A B", "A=1", "XENON_WINDOWS", "XENON_FAKE" })
            Assert.Throws<ArgumentException>(() => new ConditionalCompilationOptions([invalid]));
    }

    [Theory]
    [InlineData("debug", "Yes")] [InlineData("release", "No")]
    public void ProfilesAndSnapshotChangesReparseSources(string profile, string name)
    {
        Compilation original = Compile("namespace Probe;\n#if XENON_DEBUG\nint Yes() { return 1; }\n#else\nint No() { return 2; }\n#endif");
        Compilation updated = original.WithOptions(new CompilationOptions(ConditionalCompilation: new(profile: profile)));
        Assert.Equal(new[] { name }, Names(updated));
        Assert.Equal(new[] { "Yes" }, Names(original));
    }

    [Fact]
    public void DirectNativeEmissionReselectsTargetBeforeErrorGate()
    {
        const string source = "namespace Probe;\n#if XENON_MACOS && XENON_ARM64\nint Mac() { return 42; }\n#else\nint Windows() { return 1; }\n#endif";
        Compilation windows = Compile(source, target: "x86_64-pc-windows-msvc");
        var target = new LlvmTargetOptions("aarch64-apple-darwin");
        string ir = new LlvmIrGenerator().GenerateForTarget(windows, target);
        Assert.Contains("ret i32 42", ir);
        Assert.DoesNotContain("ret i32 1", ir);
        Assert.Contains("ret i32 42", new LlvmIrGenerator().GenerateForTarget(Compile(source), target));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".o");
        try { new LlvmObjectEmitter().Emit(windows, path, target); Assert.True(new FileInfo(path).Length > 0); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ChangingConditionalTargetDiscardsPreviouslyBoundTypeLayout()
    {
        const string source = """
            namespace Probe;
            #if XENON_WINDOWS
            int Value(int input) { return 1; }
            #else
            int Value(int input)
            {
                switch (input)
                {
                    case 4: return 42;
                    case cast<int>(sizeof(clong)): return 84;
                    default: return 0;
                }
            }
            #endif
            """;
        Compilation windows = LlvmIrGenerator.BindForTarget(
            Compile(source, target: "x86_64-pc-windows-msvc"), new("x86_64-pc-windows-msvc"));
        Assert.False(windows.HasErrors, string.Join("\n", windows.Diagnostics));
        Assert.NotNull(windows.TargetLayout);
        Compilation withoutTarget = windows.WithOptions(new CompilationOptions());
        Assert.Null(withoutTarget.TargetLayout);
        Assert.Contains(withoutTarget.Diagnostics, diagnostic => diagnostic.Id == "XE1100");
        Compilation release = windows.WithOptions(windows.Options with
        {
            ConditionalCompilation = new ConditionalCompilationOptions(
                targetTriple: "x86_64-pc-windows-msvc", profile: "release"),
        });
        Assert.Same(windows.TargetLayout, release.TargetLayout);
        Assert.NotSame(windows.SyntaxTrees[0], release.SyntaxTrees[0]);
        Assert.False(release.HasErrors, string.Join("\n", release.Diagnostics));

        Compilation changed = windows.WithOptions(windows.Options with
        {
            ConditionalCompilation = windows.Options.ConditionalOptions.WithTarget("aarch64-apple-darwin"),
        });
        Assert.Null(changed.TargetLayout);
        Assert.False(changed.HasErrors, string.Join("\n", changed.Diagnostics));
        Compilation mac = LlvmIrGenerator.BindForTarget(windows, new("aarch64-apple-darwin"));
        Assert.False(mac.HasErrors, string.Join("\n", mac.Diagnostics));
        Assert.NotNull(mac.TargetLayout);
        string ir = new LlvmIrGenerator().GenerateForTarget(windows, new("aarch64-apple-darwin"));
        Assert.Contains("ret i32 42", ir);
        Assert.Contains("ret i32 84", ir);
        Assert.NotNull(windows.TargetLayout);
        Assert.Equal("x86_64-pc-windows-msvc", windows.Options.ConditionalOptions.TargetTriple);
    }

    [Fact]
    public void XelibBindsTargetSelectionAndRejectsForeignConsumers()
    {
        const string source = """
            namespace Lib;
            #if XENON_WINDOWS
            public T Echo<T>(T value) { return move value; }
            #else
            public int UnixOnly() { return 2; }
            #endif
            """;
        Compilation library = Compile(source, ["B", "A"], "x86_64-pc-windows-msvc");
        Assert.False(library.HasErrors, string.Join("\n", library.Diagnostics));
        byte[] bytes = XelibWriter.Write(library, new("Conditional"));
        Assert.Equal(bytes, XelibWriter.Write(Compile(source, ["A", "B"], "x86_64-pc-windows-msvc"), new("Conditional")));
        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Assert.Equal("x86_64-pc-windows-msvc", reference.TargetTriple);
        Compilation app = Compilation.Create(new CompilationOptions(ConditionalCompilation: new(targetTriple: "x86_64-pc-windows-msvc")),
            [reference], SourceText.From("using Lib; namespace App; int Main() { return Echo(42); }"));
        Assert.False(app.HasErrors, string.Join("\n", app.Diagnostics));
        Assert.DoesNotContain(app.References[0].GlobalNamespace.Namespaces.SelectMany(ns => ns.Functions), f => f.Name == "UnixOnly");
        Assert.Contains("(i32 42)", new LlvmIrGenerator().GenerateForTarget(app, new("x86_64-pc-windows-msvc")));
        Compilation foreign = app.WithOptions(new CompilationOptions(ConditionalCompilation: new(targetTriple: "aarch64-apple-darwin")));
        Assert.Contains(foreign.Diagnostics, d => d.Id == "XE1101");
        Assert.DoesNotContain(foreign.References[0].GlobalNamespace.Namespaces.SelectMany(ns => ns.Functions), f => f.Name == "UnixOnly");
        Assert.Throws<LlvmCodeGenerationException>(() => new LlvmIrGenerator().GenerateForTarget(app, new("aarch64-apple-darwin")));
    }

    [Fact]
    public void ProjectLocalConditionalLibrariesCaptureTheirOwnDefines()
    {
        const string source = "namespace Lib;\n#if FEATURE\npublic int Value() { return 42; }\n#else\npublic int Other() { return 0; }\n#endif";
        byte[] bytes = XelibWriter.Write(Compilation.Create(new CompilationOptions(ConditionalCompilation:
            new(targetTriple: "x86_64-pc-windows-msvc", projectDefines: ["FEATURE"])), [], SourceText.From(source)), new("Portable"));
        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Assert.Equal("x86_64-pc-windows-msvc", reference.TargetTriple);
        Compilation app = Compilation.Create(new CompilationOptions(ConditionalCompilation: new(targetTriple: "x86_64-pc-windows-msvc")),
            [reference], SourceText.From("using Lib; namespace App; int Main() { return Value(); }"));
        Assert.False(app.HasErrors);
        Assert.Contains("ret i32 42", new LlvmIrGenerator().GenerateForTarget(app, new("x86_64-pc-windows-msvc")));
    }
    [Fact]
    public void SupplementaryIdentifierWorksInConditionalDefines()
    {
        const string name = "\U00010400FEATURE";
        Compilation result = Compile("namespace Probe;\n#if " + name +
            "\nint Selected() { return 1; }\n#else\nint Other() { return 0; }\n#endif", [name]);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(["Selected"], Names(result));
        Assert.True(ConditionalCompilationOptions.IsIdentifier("A\U00010400B"));
        Assert.False(ConditionalCompilationOptions.IsIdentifier("A\uD800B"));
    }

}
