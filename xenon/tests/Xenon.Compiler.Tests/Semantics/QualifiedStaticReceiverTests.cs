using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class QualifiedStaticReceiverTests
{
    [Theory]
    [InlineData("private static Writer Out;", "Read")]
    [InlineData("public static readonly Writer Out;", "Write")]
    public void QualifiedStaticReceiverPreservesAccessAndReadonlyChecks(string field, string method)
    {
        Compilation app = Compilation.Create(SourceText.From("""
            namespace Example.IO;
            public struct Writer {
                public void readonly Read() {}
                public void Write() {}
            }
            """ + "public struct Console { " + field + " }", "library.xe"),
            SourceText.From("namespace App; void Main() { Example.IO.Console.Out." + method + "(); }", "app.xe"));
        Assert.True(app.HasErrors);
        Assert.DoesNotContain(app.Diagnostics, diagnostic => diagnostic.Id == "XE2061");
    }

    [Theory]
    [InlineData(false, "Example.IO.Console.Out")]
    [InlineData(true, "Example.IO.Console.Out")]
    [InlineData(false, "Example.IO.Console.Nested.Writer")]
    [InlineData(true, "Example.IO.Console.Nested.Writer")]
    public void QualifiedStaticFieldCanStartInstanceCallChain(bool imported, string receiver)
    {
        SourceText declarations = SourceText.From("""
            namespace Example.IO;
            public struct Writer { public int readonly Read() { return 42; } }
            public struct Holder { public Writer Writer; }
            public struct Console {
                public static readonly Writer Out;
                public static readonly Holder Nested;
            }
            """, "library.xe");
        SourceText consumer = SourceText.From(
            "namespace App; int Main() { return " + receiver + ".Read(); }", "app.xe");
        Compilation app;
        if (imported)
        {
            Compilation library = Compilation.Create(declarations);
            Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
            LibraryCompilationReference reference = XelibReader.Read(
                XelibWriter.Write(library, new XelibWriteOptions("QualifiedReceivers")));
            app = Compilation.Create(new CompilationOptions(), [reference], consumer);
        }
        else
            app = Compilation.Create(declarations, consumer);
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
    }
}