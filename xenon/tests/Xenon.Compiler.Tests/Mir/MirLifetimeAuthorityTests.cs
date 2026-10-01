using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirLifetimeAuthorityTests
{
    [Theory]
    [InlineData("int&", false, MirLifetimeAuthority.ReferenceParameter)]
    [InlineData("storage<int>&", true, MirLifetimeAuthority.StorageValue)]
    [InlineData("storage<int>&", false, null)]
    [InlineData("int*", false, null)]
    public void DestructionRequiresAuthorityOverTheStorage(string parameterType, bool borrowValue,
        MirLifetimeAuthority? expected)
    {
        var compilation = Compilation.Create(SourceText.From($"namespace Test; void Use({parameterType} value) {{ }}"));
        Assert.Empty(compilation.Diagnostics);
        var symbol = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Use").Symbol;
        var source = MirSourceInfo.Generated;
        var parameter = symbol.Parameters[0];
        var locals = new List<MirLocal> { new(new(0), "value", parameter.Type, MirLocalKind.Parameter, source) { Variable = parameter } };
        var statements = new List<MirStatement>();
        var place = new MirPlace(new(0)).Project(new MirDerefProjection());
        if (borrowValue)
        {
            var reference = compilation.TypeFactory.ReferenceTo(BuiltinTypes.Int);
            locals.Add(new(new(1), "borrow", reference, MirLocalKind.Temporary, source));
            statements.Add(new MirAssign(new(new(1)), new MirBorrow(place.Project(new MirLifetimeProjection()), MirBorrowKind.Exclusive, reference), source));
            place = new MirPlace(new(1)).Project(new MirDerefProjection());
        }
        var function = new MirFunction(symbol, [.. locals], [
            new(new(0), [.. statements], new MirDrop(place, null, new(1), new(2), source) { IsExplicit = true }),
            new(new(1), [], new MirReturn(null, source)),
            new(new(2), [], new MirResumeUnwind(source)),
        ], new(0), source);
        Assert.Empty(MirVerifier.Verify(function));
        var violations = MirLifetimeAuthorityAnalysis.Check(function);
        if (expected is null) Assert.Empty(violations);
        else Assert.Equal(expected, Assert.Single(violations).Authority);
    }
}