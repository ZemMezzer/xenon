using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class FunctionCleanupMetadataTests
{
    [Fact]
    public void SourceGenericStructSpecializationsDeriveCleanupFromConcreteParametersAndLocals()
    {
        Compilation compilation = Compile("""
            namespace Cleanup;
            struct Resource { public ~Resource() { } }
            struct Wrapper<T>
            {
                public void Consume(T first, T second)
                {
                    { T nested = move first; }
                    T local = move second;
                }
            }
            void Use()
            {
                Wrapper<int> trivial = Wrapper<int>();
                trivial.Consume(1, 2);
                Wrapper<Resource> managed = Wrapper<Resource>();
                managed.Consume(Resource(), Resource());
            }
            """);

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        StructTypeSymbol[] wrappers = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Structs
            .Where(type => type.GenericDefinition?.Name == "Wrapper").ToArray();
        FunctionSymbol trivial = wrappers.Single(type => TypeIdentity.AreSame(
            type.TypeArguments.Single(), BuiltinTypes.Int)).Methods.Single(method => method.Name == "Consume");
        FunctionSymbol managed = wrappers.Single(type =>
            type.TypeArguments.Single() is StructTypeSymbol { Name: "Resource" }).Methods
            .Single(method => method.Name == "Consume");

        Assert.False(trivial.HasScalarCleanup);
        Assert.False(trivial.HasScopeCleanup);
        Assert.True(managed.HasScalarCleanup);
        Assert.True(managed.HasScopeCleanup);
        BoundFunction managedBody = compilation.SemanticModel.Functions.Single(function =>
            ReferenceEquals(function.Symbol, managed));
        Assert.Equal(2, DescendantLocals(managedBody.Body).Count(local => local.Destructor is not null));

        _ = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "generic-cleanup-source");
    }

    [Fact]
    public void ActionOfDestructibleValueGeneratesConsistentCleanupScopes()
    {
        Compilation compilation = Compile("""
            namespace Cleanup;
            struct String { private shared<byte[]> _buffer; }
            struct Action<T>
            {
                private function void(T) _callback;
                private Action(function void(T) callback) { _callback = callback; }
                public static Action<T> operator implicit(function void(T) callback)
                {
                    return Action<T>(callback);
                }
                public void Invoke(T value) { _callback(value); }
            }
            void Log(String value) { }
            int Main()
            {
                String value = String();
                Action<String> action = Log;
                action.Invoke(value);
                return 0;
            }
            """);

        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        FunctionSymbol invoke = compilation.SemanticModel.GlobalNamespace.Namespaces.Single().Structs
            .Single(type => type.GenericDefinition?.Name == "Action").Methods
            .Single(method => method.Name == "Invoke");
        Assert.True(invoke.HasScalarCleanup);
        Assert.True(invoke.HasScopeCleanup);

        _ = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost(), "action-destructible-cleanup");
    }

    [Fact]
    public void XelibGenericBodiesRecomputeCleanupWithoutSourceSyntax()
    {
        Compilation library = Compile("""
            namespace PortableCleanup;
            public struct Resource { public ~Resource() { } }
            public struct Holder<T> { public T Value; public Holder(T value) { Value = move value; } }
            public struct Wrapper<T>
            {
                public void Consume(T first, T second)
                {
                    { T nested = move first; }
                    T local = move second;
                }
                public void Allocate() { T[] values = T[2]; }
            }
            public void ConsumeFree<T>(T value) { T local = move value; }
            """);
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("PortableCleanup")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using PortableCleanup;
            namespace App;
            int Main()
            {
                Wrapper<int> trivial = Wrapper<int>();
                trivial.Consume(1, 2);
                trivial.Allocate();
                Wrapper<Resource> managed = Wrapper<Resource>();
                managed.Consume(Resource(), Resource());
                managed.Allocate();
                ConsumeFree<int>(3);
                ConsumeFree<Resource>(Resource());
                Holder<int> trivialHolder = Holder<int>(4);
                ConsumeFree<Holder<int>>(move trivialHolder);
                Holder<Resource> managedHolder = Holder<Resource>(Resource());
                ConsumeFree<Holder<Resource>>(move managedHolder);
                shared<Resource> owner = new Resource();
                ConsumeFree<shared<Resource>>(owner);
                shared<Resource[]> arrayOwner = new Resource[1];
                ConsumeFree<shared<Resource[]>>(arrayOwner);
                return 0;
            }
            """, "app.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        StructTypeSymbol[] wrappers = app.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "PortableCleanup").Structs
            .Where(type => type.GenericDefinition?.Name == "Wrapper").ToArray();
        FunctionSymbol trivial = wrappers.Single(type => TypeIdentity.AreSame(
            type.TypeArguments.Single(), BuiltinTypes.Int)).Methods.Single(method => method.Name == "Consume");
        FunctionSymbol managed = wrappers.Single(type =>
            type.TypeArguments.Single() is StructTypeSymbol { Name: "Resource" }).Methods
            .Single(method => method.Name == "Consume");
        FunctionSymbol[] freeSpecializations = app.SemanticModel.Functions.Select(function => function.Symbol)
            .Where(function => function.GenericDefinition?.Name == "ConsumeFree").ToArray();

        Assert.False(trivial.HasScalarCleanup);
        Assert.True(managed.HasScalarCleanup);
        Assert.False(freeSpecializations.Single(function => TypeIdentity.AreSame(
            function.TypeArguments.Single(), BuiltinTypes.Int)).HasScalarCleanup);
        Assert.True(freeSpecializations.Single(function =>
            function.TypeArguments.Single() is StructTypeSymbol { Name: "Resource" }).HasScalarCleanup);
        Assert.False(freeSpecializations.Single(function =>
            function.TypeArguments.Single() is StructTypeSymbol
            {
                GenericDefinition.Name: "Holder",
                TypeArguments: [PrimitiveTypeSymbol { Name: "int" }],
            }).HasScalarCleanup);
        Assert.True(freeSpecializations.Single(function =>
            function.TypeArguments.Single() is StructTypeSymbol
            {
                GenericDefinition.Name: "Holder",
                TypeArguments: [StructTypeSymbol { Name: "Resource" }],
            }).HasScalarCleanup);
        Assert.All(freeSpecializations.Where(function =>
            function.TypeArguments.Single() is SharedTypeSymbol),
            function => Assert.True(function.HasScalarCleanup));
        Assert.All(wrappers.Select(type => type.Methods.Single(method => method.Name == "Allocate")),
            function => Assert.True(function.HasStackArrays));
        BoundFunction managedBody = app.SemanticModel.Functions.Single(function =>
            ReferenceEquals(function.Symbol, managed));
        Assert.Equal(2, DescendantLocals(managedBody.Body).Count(local => local.Destructor is not null));

        _ = new LlvmIrGenerator().GenerateForTarget(
            app, LlvmTargetOptions.CreateHost(), "generic-cleanup-xelib");
    }

    private static IEnumerable<LocalVariableSymbol> DescendantLocals(BoundStatement statement)
    {
        switch (statement)
        {
            case BoundVariableDeclarationStatement declaration:
                yield return declaration.Variable;
                break;
            case BoundBlockStatement block:
                foreach (BoundStatement child in block.Statements)
                    foreach (LocalVariableSymbol local in DescendantLocals(child))
                        yield return local;
                break;
            case BoundIfStatement conditional:
                foreach (LocalVariableSymbol local in DescendantLocals(conditional.ThenStatement))
                    yield return local;
                if (conditional.ElseStatement is not null)
                    foreach (LocalVariableSymbol local in DescendantLocals(conditional.ElseStatement))
                        yield return local;
                break;
            case BoundWhileStatement loop:
                foreach (LocalVariableSymbol local in DescendantLocals(loop.Body))
                    yield return local;
                break;
            case BoundForStatement loop:
                if (loop.Initializer is not null)
                    foreach (LocalVariableSymbol local in DescendantLocals(loop.Initializer))
                        yield return local;
                foreach (LocalVariableSymbol local in DescendantLocals(loop.Body))
                    yield return local;
                break;
        }
    }

    private static Compilation Compile(string source) =>
        Compilation.Create(SourceText.From(source, "test.xe"));
}
