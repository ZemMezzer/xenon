using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class AccessibilityAndStructModifierTests
{
    [Fact]
    public void InternalDeclarationsAreVisibleOnlyInsideTheirProjectBoundary()
    {
        Compilation library = Create("""
            namespace Library;
            internal struct Hidden { public static int Value = 40; }
            internal int Helper() { return 2; }
            public int PublicApi() { return Hidden.Value + Helper(); }
            """);
        Assert.Empty(library.Diagnostics);

        Compilation consumer = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From("""
                using Library;
                namespace App;
                int Run() { Hidden value; return Helper(); }
                """, "consumer.xe"));
        Assert.True(consumer.HasErrors);
        Assert.DoesNotContain(consumer.SemanticModel.GlobalNamespace.Namespaces.Single(n => n.Name == "Library").Types,
            type => type.Name == "Hidden");
    }

    [Fact]
    public void ProtectedAndProtectedInternalUseInheritanceAndOrSemantics()
    {
        Compilation sameProject = Create("""
            namespace Example;
            struct Base
            {
                protected int Value;
                protected internal int Shared;
            }
            struct Derived : Base
            {
                public int Read(Derived other) { return Value + other.Value + Shared; }
            }
            struct Unrelated { public int Read(Base value) { return value.Shared; } }
            """);
        Assert.Empty(sameProject.Diagnostics);

        Compilation library = Create("""
            namespace Library;
            public struct Base
            {
                protected int Value;
                protected internal int Shared;
                public Base() {}
                public Base(int value) { Value = value; Shared = value; }
            }
            """);
        Compilation derived = Compilation.Create(new CompilationOptions(), [new SourceCompilationReference(library)],
            SourceText.From("""
                using Library;
                namespace App;
                struct Derived : Base
                {
                    public int Read(Derived other) { return Value + other.Shared; }
                }
                """, "derived.xe"));
        Assert.Empty(derived.Diagnostics);

        Compilation unrelated = Compilation.Create(new CompilationOptions(), [new SourceCompilationReference(library)],
            SourceText.From("""
                using Library;
                namespace App;
                int Read(Base value) { return value.Shared; }
                """, "unrelated.xe"));
        Assert.True(unrelated.HasErrors);
    }

    [Fact]
    public void ProtectedReceiverMustBeDerivedTyped()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Base { protected int Value; }
            struct Derived : Base
            {
                public int Invalid(Base other) { return other.Value; }
            }
            """);
        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Message.Contains("private", StringComparison.Ordinal) ||
                          diagnostic.Message.Contains("accessible", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticStructRejectsEveryInstancePathAndAcceptsStaticApi()
    {
        Compilation valid = Create("""
            namespace Example;
            public static struct Console
            {
                public static int Count;
                public static int Read() { return Console.Count; }
                const int Offset = 1;
            }
            int Run() { Console.Count = 42; return Console.Read(); }
            """);
        Assert.Empty(valid.Diagnostics);

        string[] invalidMembers =
        [
            "int Value;", "int Read() { return 0; }", "int Value { get { return 0; } }",
            "int this[int index] { get { return index; } }", "Utility() {}", "~Utility() {}",
        ];
        foreach (string member in invalidMembers)
            Assert.True(Create($"namespace Example; static struct Utility {{ {member} }}").HasErrors, member);

        foreach (string use in new[]
                 {
                     "Utility value;", "Utility* value;", "Utility& value;", "Utility();",
                     "new Utility();", "Utility[1];", "new Utility[1];", "unique<Utility> value;",
                     "atomic<Utility> value;", "sizeof(Utility);", "alignof(Utility);",
                 })
            Assert.True(Create($"namespace Example; static struct Utility {{ }} void Run() {{ {use} }}").HasErrors,
                use);
    }

    [Fact]
    public void StaticStructRejectsInheritanceInterfacesAndGenericDefinitions()
    {
        Assert.True(Create("namespace Example; struct Base {} static struct Utility : Base {}").HasErrors);
        Assert.True(Create("namespace Example; interface I {} static struct Utility : I {}").HasErrors);
        Assert.True(Create("namespace Example; static struct Utility<T> {}").HasErrors);
        Assert.True(Create("namespace Example; static struct Utility {} struct Derived : Utility {}").HasErrors);
        Assert.True(Create("namespace Example; static struct Utility {} const nuint Size = sizeof(Utility);").HasErrors);
    }

    [Fact]
    public void SealedStructCanBeConstructedButCannotBeABase()
    {
        Compilation valid = Create("""
            namespace Example;
            struct Base {}
            sealed struct Value : Base { public Value() {} }
            Value Make() { return Value(); }
            """);
        Assert.Empty(valid.Diagnostics);
        Assert.True(Create("namespace Example; sealed struct Base {} struct Derived : Base {}").HasErrors);
        Assert.True(Create("namespace Example; sealed struct Value { public virtual int Read() { return 0; } }").HasErrors);
    }

    [Fact]
    public void ReadonlyStructInitializesInConstructorButRejectsMutationAndCapabilityEscape()
    {
        Compilation valid = Create("""
            namespace Example;
            readonly struct Point
            {
                int X;
                public Point(int x) { X = x; }
                public int Read() { return X; }
            }
            int Run() { Point point = Point(42); return point.Read(); }
            """);
        Assert.Empty(valid.Diagnostics);
        StructTypeSymbol point = Namespace(valid).Structs.Single(type => type.Name == "Point");
        Assert.True(point.IsReadonly);
        Assert.True(point.Fields.Single().IsReadonly);
        Assert.True(point.Methods.Single().IsReadonly);

        Assert.True(Create("namespace Example; readonly struct Value { int X; public void Mutate() { X = 1; } }").HasErrors);
        Assert.True(Create("namespace Example; readonly struct Value { int X; public int* Pointer() { return &X; } }").HasErrors);
        Assert.True(Create("namespace Example; struct Base {} readonly struct Value : Base {}").HasErrors);
    }

    [Fact]
    public void EnumStaticFieldsAreDistinctAccessibleFields()
    {
        Compilation compilation = Create("""
            namespace Example;
            enum Mode
            {
                Off,
                On,
                public static Mode Default;
                internal static int Count = 2;
                private static int Hidden;
            }
            int Run() { Mode.Default = Mode.On; Mode.Count++; return Mode.Count; }
            """);
        Assert.Empty(compilation.Diagnostics);
        EnumTypeSymbol mode = Namespace(compilation).Enums.Single();
        Assert.Equal(2, mode.Members.Length);
        Assert.Equal(3, mode.StaticFields.Length);
        Assert.Equal(Accessibility.Internal, mode.StaticFields.Single(field => field.Name == "Count").Accessibility);
        Assert.True(Create("namespace Example; enum Mode { protected static int Count; }").HasErrors);
        Assert.True(Create("namespace Example; enum Mode { protected internal static int Count; }").HasErrors);
    }

    [Fact]
    public void XelibPreservesAccessibilityAndTypeFlagsAndHidesInternalApi()
    {
        Compilation library = Create("""
            namespace Library;
            internal int Hidden() { return 40; }
            public T AddHidden<T>(T value) { Hidden(); return move value; }
            public struct Base { protected int Value; protected internal int Shared; }
            public sealed struct Final {}
            public readonly struct ReadonlyValue { public int Value; }
            public static struct Utility { public static int Count = 2; }
            public enum Mode { Off, public static int Count = 1; }
            """);
        Assert.Empty(library.Diagnostics);
        byte[] image = XelibWriter.Write(library, new XelibWriteOptions("Library"));
        LibraryCompilationReference reference = XelibReader.Read(image);
        NamespaceSymbol scope = reference.GlobalNamespace.Namespaces.Single();
        Assert.True(scope.Structs.Single(type => type.Name == "Final").IsSealed);
        Assert.True(scope.Structs.Single(type => type.Name == "ReadonlyValue").IsReadonly);
        Assert.True(scope.Structs.Single(type => type.Name == "Utility").IsStatic);
        Assert.Single(scope.Enums.Single().StaticFields);
        Assert.Equal(Accessibility.Protected,
            scope.Structs.Single(type => type.Name == "Base").Fields.Single(field => field.Name == "Value").Accessibility);

        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Library;
            namespace App;
            struct Derived : Base { public int Read() { return Value + Shared; } }
            int Run() { Utility.Count = 2; Mode.Count = 1; return AddHidden<int>(39) + Utility.Count + Mode.Count; }
            """, "app.xe"));
        Assert.Empty(consumer.Diagnostics);
        Assert.True(Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Library; namespace App; int Run() { return Hidden(); }", "hidden.xe")).HasErrors);
        Assert.True(Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Library; namespace App; struct Bad : Final {}", "sealed.xe")).HasErrors);
        Assert.True(Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Library; namespace App; void Bad() { Utility value; }", "static.xe")).HasErrors);
        Assert.True(Compilation.Create(new CompilationOptions(), [reference], SourceText.From(
            "using Library; namespace App; void Bad() { ReadonlyValue value = ReadonlyValue(); value.Value = 1; }",
            "readonly.xe")).HasErrors);
    }

    [Fact]
    public void SymbolDisplayUsesCanonicalSemanticModifiers()
    {
        Compilation compilation = Create("""
            namespace Example;
            internal readonly sealed struct Value
            {
                protected internal int Data;
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol value = Namespace(compilation).Structs.Single();
        Assert.Equal("internal readonly sealed struct Value",
            value.ToDisplayString(SymbolDisplayFormat.Declaration));
        Assert.Equal("protected internal readonly int Data",
            value.Fields.Single().ToDisplayString(SymbolDisplayFormat.Declaration));
    }

    private static Compilation Create(params string[] sources) => Compilation.Create(
        sources.Select((source, index) => SourceText.From(source, $"test{index}.xe")).ToArray());

    private static NamespaceSymbol Namespace(Compilation compilation) =>
        compilation.SemanticModel.GlobalNamespace.Namespaces.Single();
}
