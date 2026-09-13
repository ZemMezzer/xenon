using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class ReadonlyStorageEffectTests
{
    [Theory]
    [InlineData("output", false)]
    [InlineData("State.Pointer", true)]
    public void LocalStorageCleanupChecksElementPointerEffects(string pointer, bool hasErrors)
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Test;
            struct State { public static int* Pointer; }
            struct Resource {
                private int* _pointer;
                public Resource(int* pointer) { _pointer = pointer; }
                public ~Resource() { *_pointer = 1; }
            }
            """ + "void readonly Run(int* output) { storage<Resource> slot; slot = Resource(" + pointer + "); }"));
        Assert.Equal(hasErrors, compilation.HasErrors);
        if (hasErrors)
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("hidden state"));
    }
    [Theory]
    [InlineData("output", false)]
    [InlineData("State.Pointer", true)]
    [InlineData("output", false, "Owner original = Owner(POINTER); Owner moved = move original;")]
    [InlineData("State.Pointer", true, "Owner original = Owner(POINTER); Owner moved = move original;")]
    [InlineData("output", false, "storage<Owner> slot; slot = Owner(POINTER);")]
    [InlineData("State.Pointer", true, "storage<Owner> slot; slot = Owner(POINTER);")]
    public void AggregateStorageCleanupPreservesElementPointerOrigin(string pointer, bool hasErrors,
        string statements = "Owner owner = Owner(POINTER);")
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Test;
            struct State { public static int* Pointer; }
            struct Resource {
                private int* _pointer;
                public Resource(int* pointer) { _pointer = pointer; }
                public ~Resource() { *_pointer = 1; }
            }
            struct Owner {
                private storage<Resource> _slot;
                public Owner(int* pointer) { _slot = Resource(pointer); }
            }
            """ + "void readonly Run(int* output) { " + statements.Replace("POINTER", pointer) + " }"));
        Assert.Equal(hasErrors, compilation.HasErrors);
        if (hasErrors)
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("hidden state"));
    }
    [Fact]
    public void LocalStorageFieldCleanupIsAllowedInReadonlyFunction()
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Test;
            struct Owner { private storage<byte> _slot; public ~Owner() {} }
            void readonly Run() { Owner owner = Owner(); }
            """));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("State.Value = 1;", true)]
    public void StorageFieldCleanupChecksElementDestructorEffects(string destructorBody, bool hasErrors)
    {
        Compilation compilation = Compilation.Create(SourceText.From("""
            namespace Test;
            struct State { public static int Value; }
            struct Resource {
                public Resource() {}
            """ + "public ~Resource() { " + destructorBody + " } }" + """
            struct Owner {
                private storage<Resource> _slot;
                public Owner() { _slot = Resource(); }
            }
            void readonly Run() { Owner owner = Owner(); }
            """));
        Assert.Equal(hasErrors, compilation.HasErrors);
        if (hasErrors)
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Message.Contains("hidden state"));
    }
}