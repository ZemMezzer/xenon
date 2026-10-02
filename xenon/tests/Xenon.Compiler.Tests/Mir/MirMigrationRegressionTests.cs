using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirMigrationRegressionTests
{
    [Fact]
    public void OwningReceiverMustBeUnwrappedBeforeDispatch()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Regression;
            struct Resource { public int Read() { return 42; } }
            int F(shared<Resource> value) { return value->Read(); }
            """));
        Assert.Empty(compilation.Diagnostics);
        MirFunction function = compilation.GetMirFunctions().Single(body => body.Symbol.Name == "F");
        MirCall call = Assert.Single(function.Blocks.Select(block => block.Terminator).OfType<MirCall>(),
            call => call.Callee is MirFunctionOperand { Function.Name: "Read" });
        Assert.IsType<PointerTypeSymbol>(call.Receiver!.Type);
        MirLocal owner = Assert.Single(function.Locals, local => local.Kind == MirLocalKind.Parameter);
        MirFunction invalid = function with { Blocks = [.. function.Blocks.Select(block => ReferenceEquals(block.Terminator, call)
            ? block with { Terminator = call with { Receiver = new MirCopy(new(owner.Id), owner.Type) } } : block)] };
        Assert.Contains(MirVerifier.Verify(invalid), error => error.Message.Contains("project owner storage"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacementUnwindUsesDestructorExceptionEffects(bool throwingDestructor)
    {
        var compilation = Compilation.Create(SourceText.From($$"""
            namespace Regression;
            struct Fault {}
            struct Value {
                public int Id;
                public ~Value() { {{(throwingDestructor ? "throw Fault();" : "")}} }
                public static Value operator +(readonly Value& left, int right) {
                    if (right < 0) throw Fault();
                    return Value { left.Id + right };
                }
            }
            int F() {
                Value value = Value { 3 };
                try { value += -1; }
                catch (readonly Fault& fault) { return value.Id; }
                return 0;
            }
            """));
        Assert.Equal(throwingDestructor, compilation.Diagnostics.Any(diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove));
        if (!throwingDestructor) Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void InterfaceReturningIndependentOwnerDoesNotBorrowReceiverTemporary()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Regression;
            struct Resource {}
            interface Factory { unique<Resource> Create(); }
            struct Concrete : Factory { public unique<Resource> Create() { return new Resource(); } }
            unique<Resource> Make() {
                Concrete concrete = Concrete();
                Factory& view = concrete;
                return view.Create();
            }
            """));
        Assert.Empty(compilation.Diagnostics);
        Assert.Empty(compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Make").Symbol.ResultLifetimeDependencies);
    }
}