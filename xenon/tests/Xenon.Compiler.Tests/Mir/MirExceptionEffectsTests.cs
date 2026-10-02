using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirExceptionEffectsTests
{
    private static IReadOnlyDictionary<FunctionSymbol, HashSet<TypeSymbol>> Analyze(string source)
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace Example; " + source));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        return new MirExceptionEffects(compilation.SemanticModel.Functions.Select(function =>
            MirLowerer.Lower(function, compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations))).Infer();
    }

    [Theory]
    [InlineData("void Fail(bool choice) { if (choice) throw 1; else throw true; } void Use(bool choice) { try { Fail(choice); } catch (readonly int& error) {} }", "bool")]
    [InlineData("void Use() { try { throw 1; } catch (readonly int& error) { throw; } }", "int")]
    [InlineData("void Use() { try { throw 1; } catch (readonly int& error) { try { throw true; } catch (readonly bool& nested) {} throw; } }", "int")]
    [InlineData("void Use() { try { throw 1; } finally { try { throw true; } catch (readonly bool& nested) {} } }", "int")]
    [InlineData("struct Bomb { public ~Bomb() { throw true; } } void Use() { Bomb value = Bomb(); }", "bool")]
    public void OnlyExceptionsReachingUnwindExitEscape(string source, string expected)
    {
        var effects = Analyze(source);
        Assert.Equal(expected, Assert.Single(effects.Single(pair => pair.Key.Name == "Use").Value).ToDisplayString());
    }

    [Fact]
    public void CaughtAndUnreachableThrowsDoNotEscape()
    {
        var effects = Analyze("void Use() { if (false) throw true; try { throw 1; } catch (readonly int& error) {} }");
        Assert.Empty(effects.Single(pair => pair.Key.Name == "Use").Value);
    }

    [Fact]
    public void RecursiveCallsPropagateTypedEffectsToFixedPoint()
    {
        var effects = Analyze("void A(bool branch) { if (branch) throw 1; B(branch); } void B(bool branch) { A(branch); }");
        foreach (var pair in effects.Where(pair => pair.Key.Name is "A" or "B"))
            Assert.Same(BuiltinTypes.Int, Assert.Single(pair.Value));
    }
}