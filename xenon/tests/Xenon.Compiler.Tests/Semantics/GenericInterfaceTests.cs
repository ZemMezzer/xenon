using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class GenericInterfaceTests
{
    [Fact]
    public void Analyzer_ConstructsGenericInterfaceMembersAndInheritance()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            interface Hashable<T> : Equatable<T> { ulong GetHash(); }
            struct AssetId : Hashable<AssetId>
            {
                public bool Equals(readonly AssetId& other) { return true; }
                public ulong GetHash() { return cast<ulong>(0); }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol example = compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!;
        StructTypeSymbol assetId = example.Structs.Single(type => type.Name == "AssetId");
        InterfaceTypeSymbol hashable = Assert.Single(assetId.Interfaces);
        Assert.Equal("Hashable<Example.AssetId>", hashable.Name);
        InterfaceTypeSymbol equatable = Assert.Single(hashable.BaseInterfaces);
        Assert.Same(assetId, Assert.Single(equatable.TypeArguments));
        FunctionSymbol equals = equatable.FindMethod("Equals")!;
        var parameter = Assert.IsType<ReferenceTypeSymbol>(Assert.Single(equals.Parameters).Type);
        Assert.True(parameter.IsReadonly);
        Assert.Same(assetId, parameter.ElementType);
    }

    [Fact]
    public void Analyzer_SubstitutesFixedGenericBaseInterfaceArguments()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Base<T> { T Get(); }
            interface Derived<T> : Base<int> { }
            struct Value : Derived<Value> { public int Get() { return 0; } }
            """);

        Assert.Empty(compilation.Diagnostics);
        InterfaceTypeSymbol derived = compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!
            .Structs.Single(type => type.Name == "Value").Interfaces.Single();
        InterfaceTypeSymbol @base = Assert.Single(derived.BaseInterfaces);
        Assert.Same(BuiltinTypes.Int, Assert.Single(@base.TypeArguments));
        Assert.Same(BuiltinTypes.Int, @base.FindMethod("Get")!.ReturnType);
    }

    [Fact]
    public void Analyzer_RejectsImplementationAgainstUnsubstitutedSignature()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Foo : Equatable<Foo>
            {
                public bool Equals(readonly int& other) { return true; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UnimplementedInterfaceMember);
    }

    [Fact]
    public void Analyzer_SubstitutesGenericInterfacePropertiesAndIndexers()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Container<T>
            {
                T Current { get; }
                T this[T key] { get; }
            }
            struct UsesContainer { public Container<int> Value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        InterfaceTypeSymbol container = Assert.IsType<InterfaceTypeSymbol>(
            compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!
                .Structs.Single().Fields.Single().Type);
        Assert.Same(BuiltinTypes.Int, Assert.Single(container.Properties).Type);
        InterfaceIndexerSymbol indexer = Assert.Single(container.Indexers);
        Assert.Same(BuiltinTypes.Int, indexer.Type);
        Assert.Same(BuiltinTypes.Int, Assert.Single(indexer.Parameters).Type);
        Assert.NotNull(Assert.Single(container.Properties).GenericDefinition);
        Assert.NotNull(indexer.GenericDefinition);
    }

    [Fact]
    public void Analyzer_DistinguishesGenericInterfaceArities()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Marker { }
            interface Marker<T> { }
            interface Marker<TFirst, TSecond> { }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Equal([0, 1, 2], compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!
            .Interfaces.Select(type => type.GenericArity).Order());
    }

    [Fact]
    public void GenericStructSpecialization_SubstitutesImplementedInterface()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Wrapper<T> : Equatable<T>
            {
                public bool Equals(readonly T& other) { return true; }
            }
            struct UsesWrapper { public Wrapper<int> Value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol wrapper = compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!
            .Structs.Single(type => type.GenericDefinition?.Name == "Wrapper" &&
                TypeIdentity.AreSame(Assert.Single(type.TypeArguments), BuiltinTypes.Int));
        InterfaceTypeSymbol equatable = Assert.Single(wrapper.Interfaces);
        Assert.Same(BuiltinTypes.Int, Assert.Single(equatable.TypeArguments));
        Assert.Same(BuiltinTypes.Int, Assert.IsType<ReferenceTypeSymbol>(
            Assert.Single(equatable.FindMethod("Equals")!.Parameters).Type).ElementType);
    }

    [Fact]
    public void GenericFunctionInference_InfersThroughConstructedInterface()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Foo : Equatable<Foo>
            {
                public bool Equals(readonly Foo& other) { return true; }
            }
            void Accept<T>(Equatable<T> value) { }
            void Test()
            {
                Foo value = Foo();
                Equatable<Foo> view = value;
                Accept(view);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Contains(compilation.SemanticModel.Functions, function =>
            function.Symbol.GenericDefinition?.Name == "Accept" &&
            function.Symbol.TypeArguments is [StructTypeSymbol { Name: "Foo" }]);
    }

    [Fact]
    public void CodeGeneration_EmitsConstructedGenericInterfaceDispatch()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Foo : Equatable<Foo>
            {
                public bool Equals(readonly Foo& other) { return true; }
            }
            int Main()
            {
                Foo value = Foo();
                Equatable<Foo> view = value;
                if (view.Equals(value)) return 0;
                return 1;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        string ir = new LlvmIrGenerator().Generate(compilation);
        Assert.Contains("interface_key", ir, StringComparison.Ordinal);
        Assert.Contains("interface_table", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLessXelib_PreservesGenericInterfaceDefinitionsAndConstruction()
    {
        Compilation library = Create("""
            namespace Contracts;
            public interface Equatable<T> { bool Equals(readonly T& other); }
            public interface Hashable<T> : Equatable<T> { ulong GetHash(); }
            """);
        Assert.Empty(library.Diagnostics);
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Contracts")), metadataOnly: true);

        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Contracts;
            namespace App;
            struct AssetId : Hashable<AssetId>
            {
                public bool Equals(readonly AssetId& other) { return true; }
                public ulong GetHash() { return cast<ulong>(0); }
            }
            """, "app.xe"));

        Assert.Empty(app.Diagnostics);
        InterfaceTypeSymbol hashable = app.SemanticModel.GlobalNamespace.FindNamespace("App")!
            .Structs.Single().Interfaces.Single();
        Assert.Equal(1, hashable.GenericArity);
        Assert.Equal("Hashable", hashable.GenericDefinition!.Name);
        Assert.Single(hashable.BaseInterfaces);
    }

    [Fact]
    public void SourceLessXelib_SpecializesGenericFunctionInterfaceCalls()
    {
        Compilation library = Create("""
            namespace Contracts;
            public interface Equatable<T> { bool Equals(readonly T& other); }
            public bool AreEqual<T>(Equatable<T> value, readonly T& other)
            {
                return value.Equals(other);
            }
            """);
        Assert.Empty(library.Diagnostics);
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Contracts")));

        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Contracts;
            namespace App;
            struct Value : Equatable<Value>
            {
                public bool Equals(readonly Value& other) { return true; }
            }
            int Main()
            {
                Value value = Value();
                Equatable<Value> view = value;
                if (AreEqual(view, value)) return 0;
                return 1;
            }
            """, "app.xe"));

        Assert.Empty(app.Diagnostics);
        string ir = new LlvmIrGenerator().Generate(app);
        Assert.Contains("interface_key", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLessXelib_PreservesGenericInterfaceArities()
    {
        Compilation library = Create("""
            namespace Contracts;
            public interface Marker { }
            public interface Marker<T> { }
            public interface Marker<TFirst, TSecond> { }
            """);
        Assert.Empty(library.Diagnostics);

        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Contracts")), metadataOnly: true);

        Assert.Equal([0, 1, 2], reference.GlobalNamespace.FindNamespace("Contracts")!
            .Interfaces.Select(type => type.GenericArity).Order());
    }

    [Fact]
    public void GenericConstraint_PreservesSelfConstructedInterfaceTarget()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Container<T> where T : Equatable<T> { }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol container = compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!
            .Structs.Single(type => type.Name == "Container");
        GenericConstraintSymbol constraint = Assert.Single(Assert.Single(container.TypeParameters).Constraints);
        InterfaceTypeSymbol equatable = Assert.IsType<InterfaceTypeSymbol>(constraint.Target);
        Assert.Equal("Equatable", equatable.GenericDefinition!.Name);
        Assert.Same(Assert.Single(container.TypeParameters), Assert.Single(equatable.TypeArguments));
    }

    [Fact]
    public void GenericConstraint_AcceptsMatchingConstructedInterfaceAndRejectsMismatch()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Foo : Equatable<Foo>
            {
                public bool Equals(readonly Foo& other) { return true; }
            }
            struct Bar { }
            struct Container<T> where T : Equatable<T> { }
            struct Valid { public Container<Foo> Value; }
            struct Invalid { public Container<Bar> Value; }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics.Where(item =>
            item.Id == DiagnosticIds.GenericConstraintNotSatisfied));
        Assert.Contains("Equatable<Example.Bar>", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericConstraint_SupportsMultipleFixedAndNestedConstructedTargets()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { }
            interface Hashable { }
            interface ConvertibleTo<T> { }
            struct Wrapper<T> { }
            struct Container<TKey, TValue>
                where TKey : Equatable<Wrapper<TKey>>, Hashable
                where TValue : ConvertibleTo<int>
            { }
            struct Key : Equatable<Wrapper<Key>>, Hashable { }
            struct Value : ConvertibleTo<int> { }
            struct Uses { public Container<Key, Value> Item; }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol container = compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!
            .Structs.Single(type => type.Name == "Container");
        InterfaceTypeSymbol equatable = Assert.IsType<InterfaceTypeSymbol>(
            container.TypeParameters[0].Constraints[0].Target);
        StructTypeSymbol wrapper = Assert.IsType<StructTypeSymbol>(Assert.Single(equatable.TypeArguments));
        Assert.Same(container.TypeParameters[0], Assert.Single(wrapper.TypeArguments));
        InterfaceTypeSymbol convertible = Assert.IsType<InterfaceTypeSymbol>(
            Assert.Single(container.TypeParameters[1].Constraints).Target);
        Assert.Same(BuiltinTypes.Int, Assert.Single(convertible.TypeArguments));
    }

    [Fact]
    public void GenericFunctionConstraint_BindsConstructedInterfaceMembersAndValidatesCalls()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Foo : Equatable<Foo>
            {
                public bool Equals(readonly Foo& other) { return true; }
            }
            bool AreEqual<T>(T left, readonly T& right) where T : Equatable<T>
            {
                return left.Equals(right);
            }
            bool Test()
            {
                Foo left = Foo();
                Foo right = Foo();
                return AreEqual<Foo>(left, right);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.Contains(compilation.SemanticModel.Functions, function =>
            function.Symbol.GenericDefinition?.Name == "AreEqual" &&
            function.Symbol.TypeArguments is [StructTypeSymbol { Name: "Foo" }]);
    }

    [Fact]
    public void OpenGenericConstraint_PropagatesSelfConstructedGuarantee()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Required<T> where T : Equatable<T> { public T Value; }
            struct Outer<T> where T : Equatable<T> { public Required<T> Value; }
            void Accept<T>(readonly T& value) where T : Equatable<T> { }
            void Forward<T>(readonly T& value) where T : Equatable<T> { Accept<T>(value); }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void GenericConstraint_ReportsNormalConstructedTypeDiagnostics()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { }
            struct Wrong<T> where T : Equatable<T, int> { }
            struct Unknown<T> where T : Missing<T> { }
            """);

        Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.GenericArityMismatch);
        Assert.Contains(compilation.Diagnostics, item => item.Id == DiagnosticIds.UnknownType);
        Assert.DoesNotContain(compilation.Diagnostics, item =>
            item.Id == DiagnosticIds.InvalidGenericConstraint);
    }

    [Fact]
    public void GenericConstraint_UsesNormalConstructionForGenericBaseStructs()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Base<T> { }
            struct Derived : Base<int> { }
            struct Container<T> where T : Base<int> { }
            struct Uses { public Container<Derived> Value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol container = compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!
            .Structs.Single(type => type.Name == "Container");
        StructTypeSymbol required = Assert.IsType<StructTypeSymbol>(
            Assert.Single(Assert.Single(container.TypeParameters).Constraints).Target);
        Assert.Same(BuiltinTypes.Int, Assert.Single(required.TypeArguments));
    }

    [Fact]
    public void GenericStruct_ImplementsSelfConstructedGenericInterface()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface Equatable<T> { bool Equals(readonly T& other); }
            struct Wrapper<T> : Equatable<Wrapper<T>>
            {
                public bool Equals(readonly Wrapper<T>& other) { return true; }
            }
            struct Use { public Wrapper<int> Value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol wrapper = compilation.SemanticModel.GlobalNamespace.FindNamespace("Example")!
            .Structs.Single(type => type.GenericDefinition?.Name == "Wrapper" && type.IsConcreteType);
        InterfaceTypeSymbol equatable = Assert.Single(wrapper.Interfaces);
        Assert.True(TypeIdentity.AreSame(wrapper, Assert.Single(equatable.TypeArguments)));
    }

    [Fact]
    public void SourceLessXelib_PreservesAndValidatesConstructedInterfaceConstraint()
    {
        Compilation library = Create("""
            namespace Contracts;
            public interface Equatable<T> { bool Equals(readonly T& other); }
            public struct Container<T> where T : Equatable<T> { public T Value; }
            """);
        Assert.Empty(library.Diagnostics);
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Contracts")), metadataOnly: true);

        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Contracts;
            namespace App;
            struct Foo : Equatable<Foo>
            {
                public bool Equals(readonly Foo& other) { return true; }
            }
            struct Uses { public Container<Foo> Value; }
            """, "app.xe"));

        Assert.Empty(app.Diagnostics);

        Compilation invalidApp = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Contracts;
            namespace App;
            struct Bar { }
            struct Uses { public Container<Bar> Value; }
            """, "invalid-app.xe"));
        Diagnostic diagnostic = Assert.Single(invalidApp.Diagnostics.Where(item =>
            item.Id == DiagnosticIds.GenericConstraintNotSatisfied));
        Assert.Contains("Equatable<App.Bar>", diagnostic.Message, StringComparison.Ordinal);
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "generic-interfaces.xe"));
}
