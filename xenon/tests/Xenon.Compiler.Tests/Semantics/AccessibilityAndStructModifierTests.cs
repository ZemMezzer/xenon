using Xenon.Compiler.Libraries;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class AccessibilityAndStructModifierTests
{
    [Fact]
    public void GenericSpecializationsShareTheirDefinitionsPrivateAccessDomain()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T>
            {
                private int _value;
                private Box() { _value = 42; }
                private void Test() { Box<T> value = Box<T>(); }
                private static void Hidden() { }
                private int Value { get { return _value; } }
                private int this[int index] { get { return _value + index; } }

                public static int Run()
                {
                    Box<T> value = Box<T>();
                    value.Test();
                    Hidden();
                    return value._value + value.Value + value[0];
                }

                public static Box<T> operator implicit(int ignored)
                {
                    Box<T> value = Box<T>();
                    value.Test();
                    Hidden();
                    int result = value._value + value.Value + value[0];
                    return value;
                }
            }

            int Test()
            {
                Box<int> first = 1;
                Box<float> second = 2;
                return Box<int>.Run() + Box<float>.Run();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void GenericPrivateAccessRemainsUnavailableOutsideTheDeclaringFamily()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T>
            {
                private Box() { }
                private int Value;
                private void Hidden() { }
            }
            struct Other<T>
            {
                public void Run(Box<T> value)
                {
                    Box<T> created = Box<T>();
                    value.Hidden();
                    int result = value.Value;
                }
            }
            """);

        Assert.Equal(3, compilation.Diagnostics.Count(
            diagnostic => diagnostic.Id == DiagnosticIds.InaccessibleSymbol));
    }

    [Fact]
    public void XelibGenericSpecializationPreservesPrivateAccessDomainWithoutSource()
    {
        Compilation library = Create("""
            namespace Library;
            public struct Box<T>
            {
                private int _value;
                private Box() { _value = 42; }
                private int Value { get { return _value; } }
                private void Hidden() { }
                public static int Run()
                {
                    Box<T> value = Box<T>();
                    value.Hidden();
                    return value._value + value.Value;
                }
            }
            """);
        Assert.Empty(library.Diagnostics);
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Library")));

        Compilation consumer = CreateConsumer(reference, """
            using Library;
            namespace App;
            int Run() { return Box<int>.Run() + Box<float>.Run(); }
            """);

        Assert.Empty(consumer.Diagnostics);
    }

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
    public void InconsistentAccessibilityRejectsBroaderApiPositionsAndHonorsEffectiveOwner()
    {
        string[] invalid =
        [
            "internal struct Hidden {} public Hidden Get() { return Hidden(); }",
            "internal struct Hidden {} public void Set(Hidden value) {}",
            "internal struct Hidden {} public struct Api { public Hidden Value; }",
            "internal struct Hidden {} public struct Api { protected Hidden Value; }",
            "internal struct Hidden {} public struct Api { protected internal Hidden Read() { return Hidden(); } }",
            "internal struct Hidden {} public struct Api { public static Hidden Value; }",
            "internal struct Hidden {} public struct Api { public Hidden Value { get { return Hidden(); } } }",
            "internal struct Hidden {} public struct Api { public Hidden this[int index] { get { return Hidden(); } } }",
            "internal struct Hidden {} public struct Api { public Api(Hidden value) {} }",
            "internal struct Hidden {} public enum Mode { Off, public static Hidden Value; }",
        ];

        foreach (string declarations in invalid)
        {
            Compilation compilation = Create($"namespace Example; {declarations}");
            Assert.Contains(compilation.Diagnostics,
                diagnostic => diagnostic.Id == DiagnosticIds.InconsistentAccessibility);
        }

        Compilation valid = Create("""
            namespace Example;
            internal struct Hidden {}
            internal Hidden Get() { return Hidden(); }
            internal struct Container
            {
                public Hidden Value;
                public static Hidden Shared;
                public Hidden Read() { return Hidden(); }
                public Container(Hidden value) { Value = value; }
            }
            """);
        Assert.DoesNotContain(valid.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.InconsistentAccessibility);

        Compilation message = Create(
            "namespace Example; internal struct Hidden {} public Hidden Get() { return Hidden(); }");
        Assert.Contains(message.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.InconsistentAccessibility &&
            diagnostic.Message ==
                "inconsistent accessibility: return type 'Hidden' is less accessible than function 'Get'");
    }

    [Fact]
    public void InconsistentAccessibilityRecursesThroughEverySupportedSignatureWrapper()
    {
        string[] exposedTypes =
        [
            "Hidden*", "readonly Hidden*", "Hidden&", "readonly Hidden&", "Hidden[]",
            "shared<Hidden>", "unique<Hidden>", "weak<Hidden>", "atomic<Hidden>",
            "storage<Hidden>", "pin<Hidden>",
            "Wrapper<Hidden>", "function Hidden(int)*", "function int(Hidden)*",
        ];

        foreach (string exposedType in exposedTypes)
        {
            Compilation compilation = Create($$"""
                namespace Example;
                internal struct Hidden {}
                public struct Wrapper<T> {}
                public interface Api { {{exposedType}} Read(); }
                """);
            Assert.Contains(compilation.Diagnostics,
                diagnostic => diagnostic.Id == DiagnosticIds.InconsistentAccessibility);
        }
    }

    [Fact]
    public void InconsistentAccessibilityRejectsBasesInterfacesAndGenericConstraints()
    {
        string[] invalid =
        [
            "internal struct HiddenBase {} public struct Api : HiddenBase {}",
            "internal interface IHidden {} public struct Api : IHidden {}",
            "internal interface IHidden {} public interface IApi : IHidden {}",
            "internal interface IHidden {} public struct Box<T> where T : IHidden {}",
            "internal struct HiddenBase {} public void Use<T>() where T : HiddenBase {}",
            "internal template HiddenContract { void Run(); } public struct Box<T> where T : HiddenContract {}",
        ];

        foreach (string declarations in invalid)
        {
            Compilation compilation = Create($"namespace Example; {declarations}");
            Assert.Contains(compilation.Diagnostics,
                diagnostic => diagnostic.Id == DiagnosticIds.InconsistentAccessibility);
        }

        Compilation baseMessage = Create(
            "namespace Example; internal struct HiddenBase {} public struct Api : HiddenBase {}");
        Assert.Contains(baseMessage.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.InconsistentAccessibility &&
            diagnostic.Message ==
                "inconsistent accessibility: base type 'HiddenBase' is less accessible than struct 'Api'");
    }

    [Theory]
    [InlineData("protected", true)]
    [InlineData("protected internal", false)]
    [InlineData("public", false)]
    public void ProtectedInternalOverrideHonorsSameLibraryBoundary(string accessibility, bool rejected)
    {
        Compilation compilation = Create($$"""
            namespace Example;
            public struct Base { protected internal virtual int Read() { return 1; } }
            public struct Derived : Base { {{accessibility}} override int Read() { return 42; } }
            """);
        Assert.Equal(rejected, compilation.Diagnostics.Any(
            diagnostic => diagnostic.Id == DiagnosticIds.OverrideAccessibilityReduction));
    }

    [Theory]
    [InlineData("protected", false)]
    [InlineData("protected internal", false)]
    [InlineData("public", false)]
    [InlineData("internal", true)]
    [InlineData("private", true)]
    public void ProtectedInternalOverrideAcrossSourceLibraryUsesOnlyCrossingProtectedBranch(
        string accessibility,
        bool rejected)
    {
        Compilation library = Create("""
            namespace Library;
            public struct Base { protected internal virtual int Read() { return 1; } }
            """);
        Compilation consumer = Compilation.Create(new CompilationOptions(),
            [new SourceCompilationReference(library)], SourceText.From($$"""
                using Library;
                namespace App;
                public struct Derived : Base { {{accessibility}} override int Read() { return 42; } }
                """, "consumer.xe"));

        Assert.Equal(rejected, consumer.Diagnostics.Any(
            diagnostic => diagnostic.Id == DiagnosticIds.OverrideAccessibilityReduction));
        if (!rejected)
        {
            StructTypeSymbol derived = consumer.SemanticModel.GlobalNamespace.Namespaces
                .Single(scope => scope.Name == "App").Structs.Single();
            FunctionSymbol overriding = derived.Methods.Single(method => method.Name == "Read");
            FunctionSymbol inherited = derived.BaseType!.VirtualMethods.Single(method => method.Name == "Read");
            Assert.Equal(inherited.VTableSlot, overriding.VTableSlot);
        }
    }

    [Fact]
    public void XelibProtectedInternalOverrideReusesSlotWhileProtectedAndInternalControlsHold()
    {
        Compilation library = Create("""
            namespace Library;
            public struct Base
            {
                protected internal virtual int Read() { return 1; }
                protected virtual int Ordinary() { return 1; }
                internal virtual int InternalOnly() { return 1; }
            }
            """);
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Library")));

        Compilation valid = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Library;
            namespace App;
            public struct Derived : Base
            {
                protected override int Read() { return 42; }
                protected override int Ordinary() { return 42; }
            }
            """, "valid.xe"));
        Assert.Empty(valid.Diagnostics);
        StructTypeSymbol derived = valid.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "App").Structs.Single();
        Assert.All(derived.Methods, method => Assert.Equal(
            derived.BaseType!.VirtualMethods.Single(inherited => inherited.Name == method.Name).VTableSlot,
            method.VTableSlot));

        Compilation invalid = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Library;
            namespace App;
            public struct Derived : Base
            {
                internal override int InternalOnly() { return 42; }
            }
            """, "invalid.xe"));
        Assert.True(invalid.HasErrors);
    }

    [Fact]
    public void InternalVirtualOverrideLookupIsBoundaryAwareWithoutFilteringInheritedSlots()
    {
        Compilation sameLibrary = Create("""
            namespace Example;
            public struct Base { internal virtual int Read() { return 1; } }
            public struct Derived : Base { internal override int Read() { return 2; } }
            """);
        Assert.Empty(sameLibrary.Diagnostics);
        StructTypeSymbol sameDerived = Namespace(sameLibrary).Structs.Single(type => type.Name == "Derived");
        Assert.Equal(sameDerived.BaseType!.Methods.Single().VTableSlot,
            sameDerived.Methods.Single().VTableSlot);

        Compilation missingOverride = Create("""
            namespace Example;
            public struct Base { internal virtual int Read() { return 1; } }
            public struct Derived : Base { public int Read() { return 2; } }
            """);
        Assert.Contains(missingOverride.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.MissingOverrideModifier);

        Compilation library = Create("""
            namespace Library;
            public struct Base { internal virtual int Read() { return 1; } }
            """);
        Compilation externalNew = CreateConsumer(library, """
            using Library;
            namespace App;
            public struct Derived : Base { public virtual int Read() { return 42; } }
            """);
        Assert.Empty(externalNew.Diagnostics);
        StructTypeSymbol externalDerived = externalNew.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "App").Structs.Single();
        FunctionSymbol baseMethod = externalDerived.BaseType!.Methods.Single();
        FunctionSymbol newMethod = externalDerived.Methods.Single();
        Assert.NotEqual(baseMethod.VTableSlot, newMethod.VTableSlot);
        Assert.Same(baseMethod, externalDerived.VirtualMethods[baseMethod.VTableSlot!.Value]);
        Assert.Same(newMethod, externalDerived.VirtualMethods[newMethod.VTableSlot!.Value]);

        Compilation externalOverride = CreateConsumer(library, """
            using Library;
            namespace App;
            public struct Derived : Base { public override int Read() { return 42; } }
            """);
        Assert.Contains(externalOverride.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.NoCompatibleOverrideTarget);
    }

    [Fact]
    public void PrivateVirtualIsNotAnOverrideCandidateButProtectedVirtualStillIs()
    {
        Compilation privateMember = Create("""
            namespace Example;
            public struct Base { private virtual int Read() { return 1; } }
            public struct Derived : Base { public virtual int Read() { return 2; } }
            """);
        Assert.Empty(privateMember.Diagnostics);
        StructTypeSymbol derived = Namespace(privateMember).Structs.Single(type => type.Name == "Derived");
        Assert.NotEqual(derived.BaseType!.Methods.Single().VTableSlot, derived.Methods.Single().VTableSlot);

        Compilation protectedMissingOverride = Create("""
            namespace Example;
            public struct Base { protected virtual int Read() { return 1; } }
            public struct Derived : Base { public int Read() { return 2; } }
            """);
        Assert.Contains(protectedMissingOverride.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.MissingOverrideModifier);

        Compilation protectedOverride = Create("""
            namespace Example;
            public struct Base { protected virtual int Read() { return 1; } }
            public struct Derived : Base { protected override int Read() { return 2; } }
            """);
        Assert.Empty(protectedOverride.Diagnostics);
    }

    [Fact]
    public void InaccessibleVirtualPropertiesAndIndexersCreateIndependentAccessorSlots()
    {
        Compilation library = Create("""
            namespace Library;
            public struct Base
            {
                internal virtual int Value { get { return 1; } set {} }
                internal virtual int this[int index] { get { return index; } set {} }
            }
            """);
        CompilationReference[] references =
        [
            new SourceCompilationReference(library),
            XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("Library"))),
        ];
        foreach (CompilationReference reference in references)
        {
            Compilation consumer = CreateConsumer(reference, """
                using Library;
                namespace App;
                public struct Derived : Base
                {
                    public virtual int Value { get { return 42; } set {} }
                    public virtual int this[int index] { get { return index + 42; } set {} }
                }
                """);
            Assert.Empty(consumer.Diagnostics);
            StructTypeSymbol derived = consumer.SemanticModel.GlobalNamespace.Namespaces
                .Single(scope => scope.Name == "App").Structs.Single();
            PropertySymbol baseProperty = derived.BaseType!.Properties.Single();
            PropertySymbol newProperty = derived.Properties.Single();
            IndexerSymbol baseIndexer = derived.BaseType.Indexers.Single();
            IndexerSymbol newIndexer = derived.Indexers.Single();
            Assert.NotEqual(baseProperty.Getter!.VTableSlot, newProperty.Getter!.VTableSlot);
            Assert.NotEqual(baseProperty.Setter!.VTableSlot, newProperty.Setter!.VTableSlot);
            Assert.NotEqual(baseIndexer.Getter!.VTableSlot, newIndexer.Getter!.VTableSlot);
            Assert.NotEqual(baseIndexer.Setter!.VTableSlot, newIndexer.Setter!.VTableSlot);
            Assert.All(new[] { baseProperty.Getter, baseProperty.Setter, baseIndexer.Getter, baseIndexer.Setter },
                accessor => Assert.Same(accessor, derived.VirtualMethods[accessor!.VTableSlot!.Value]));

            Compilation invalid = CreateConsumer(reference, """
                using Library;
                namespace App;
                public struct Derived : Base
                {
                    public override int Value { get { return 42; } set {} }
                    public override int this[int index] { get { return index; } set {} }
                }
                """);
            Assert.Equal(2, invalid.Diagnostics.Count(
                diagnostic => diagnostic.Id == DiagnosticIds.NoCompatibleOverrideTarget));
        }
    }

    [Fact]
    public void OverrideLookupFiltersAccessibilityPerCompleteOverloadSignature()
    {
        Compilation library = Create("""
            namespace Library;
            public struct Base
            {
                internal virtual int Read(int value) { return value; }
                protected virtual int Read(long value) { return cast<int>(value); }
            }
            """);
        Compilation consumer = CreateConsumer(library, """
            using Library;
            namespace App;
            public struct Derived : Base
            {
                public virtual int Read(int value) { return value + 1; }
                protected override int Read(long value) { return cast<int>(value) + 1; }
            }
            """);
        Assert.Empty(consumer.Diagnostics);
        StructTypeSymbol derived = consumer.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "App").Structs.Single();
        FunctionSymbol baseInternal = derived.BaseType!.Methods.Single(method =>
            TypeIdentity.AreSame(method.Parameters.Single().Type, BuiltinTypes.Int));
        FunctionSymbol newInteger = derived.Methods.Single(method =>
            TypeIdentity.AreSame(method.Parameters.Single().Type, BuiltinTypes.Int));
        FunctionSymbol baseProtected = derived.BaseType.Methods.Single(method =>
            TypeIdentity.AreSame(method.Parameters.Single().Type, BuiltinTypes.Long));
        FunctionSymbol overridingLong = derived.Methods.Single(method =>
            TypeIdentity.AreSame(method.Parameters.Single().Type, BuiltinTypes.Long));
        Assert.NotEqual(baseInternal.VTableSlot, newInteger.VTableSlot);
        Assert.Equal(baseProtected.VTableSlot, overridingLong.VTableSlot);
    }

    [Fact]
    public void XelibVirtualLayoutRootsUseCurrentArtifactMembershipPrecisely()
    {
        Compilation library = Create("""
            namespace Library;
            public struct Base
            {
                internal virtual int Read() { return 1; }
                internal virtual int Value { get { return 2; } set {} }
                internal virtual int this[int index] { get { return index; } set {} }
                public virtual ~Base() {}
                internal int Helper() { return 0; }
            }
            """);
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Library")));

        Compilation inheritable = CreateConsumer(reference, """
            using Library;
            namespace App;
            public struct Derived : Base {}
            """);
        Assert.Empty(inheritable.Diagnostics);
        StructTypeSymbol derived = inheritable.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "App").Structs.Single();
        StructTypeSymbol baseType = derived.BaseType!;
        var roots = new HashSet<FunctionSymbol>(
            inheritable.GetImplementationNativeAbiRoots().OfType<FunctionSymbol>(),
            ReferenceEqualityComparer.Instance);
        Assert.All(baseType.VirtualMethods, function =>
        {
            Assert.False(inheritable.IsSymbolDefinedHere(function));
            Assert.True(inheritable.IsSymbolDefinedInCurrentArtifact(function));
            Assert.Contains(function, roots);
        });
        Assert.DoesNotContain(baseType.Methods.Single(method => method.Name == "Helper"), roots);

        foreach (string declaration in new[]
                 {
                     "public sealed struct Derived : Base {}",
                     "internal struct Derived : Base {}",
                 })
        {
            Compilation closedSurface = CreateConsumer(reference, $$"""
                using Library;
                namespace App;
                {{declaration}}
                """);
            Assert.Empty(closedSurface.Diagnostics);
            Assert.DoesNotContain(closedSurface.GetImplementationNativeAbiRoots().OfType<FunctionSymbol>(),
                function => function.Origin.LibraryContentIdentity is not null);
        }
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

    private static Compilation CreateConsumer(Compilation library, string source) => Compilation.Create(
        new CompilationOptions(), [new SourceCompilationReference(library)],
        SourceText.From(source, "consumer.xe"));

    private static Compilation CreateConsumer(CompilationReference reference, string source) => Compilation.Create(
        new CompilationOptions(), [reference], SourceText.From(source, "consumer.xe"));

    private static NamespaceSymbol Namespace(Compilation compilation) =>
        compilation.SemanticModel.GlobalNamespace.Namespaces.Single();
}
