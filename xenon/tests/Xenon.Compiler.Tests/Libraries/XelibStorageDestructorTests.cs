using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Libraries;

public sealed class XelibStorageDestructorTests
{
    [Theory]
    [InlineData("storage<byte>")]
    [InlineData("storage<T>")]
    public void GenericAndNongenericStorageFieldsReuseImportedDestructor(string genericStorage)
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Owners;
            public struct GenericOwner<T> {
                private GENERIC_STORAGE _marker;
                public GenericOwner() {}
                public ~GenericOwner() {}
            }
            public struct Owner {
                private storage<byte> _marker;
                public Owner() {}
                public ~Owner() {}
            }
            """.Replace("GENERIC_STORAGE", genericStorage), "owners.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        LibraryCompilationReference reference = XelibReader.Read(
            XelibWriter.Write(library, new XelibWriteOptions("Owners")));
        Compilation app = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Owners;
            namespace App;
            int Main() {
                GenericOwner<byte> generic = GenericOwner<byte>();
                Owner ordinary = Owner();
                return 0;
            }
            """, "app.xe"));
        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();
        Compilation targeted = LlvmIrGenerator.BindForTarget(app, target);
        Assert.False(targeted.HasErrors, string.Join(Environment.NewLine, targeted.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(targeted, target);
    }
}