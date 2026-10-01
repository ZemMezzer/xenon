using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirDiagnosticSourceTests
{
    [Fact]
    public void ImportedSpecializationReportsEachCallSiteAndEachSemanticViolation()
    {
        var library = Compilation.Create(SourceText.From("namespace Lib; public void Forward<T>(T value) {}", "library.xe"));
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("SourceLess")));
        var compilation = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Lib;
            namespace Client;
            void Use() {
                Forward<int>(1);
                Forward<int>(2);
            }
            """, "consumer.xe"));
        Assert.Empty(compilation.Diagnostics);
        var bound = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name.StartsWith("Forward<"));
        var mir = MirLowerer.Lower(bound, compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations);
        var bag = new DiagnosticBag();
        var first = new MirSourceInfo(TextLocation.None, OriginId: 1) { IsFallback = true };
        var second = first with { OriginId = 2 };
        MirDiagnosticReporter.Report(bag, mir, first, "first violation", DiagnosticIds.UnresolvedLifetimeOwner, "place1");
        MirDiagnosticReporter.Report(bag, mir, first, "different wording, same violation", DiagnosticIds.UnresolvedLifetimeOwner, "place1");
        MirDiagnosticReporter.Report(bag, mir, second, "second violation", DiagnosticIds.UnresolvedLifetimeOwner, "place2");
        Assert.True(bag.Count == 4, string.Join("; ", bound.Symbol.SpecializationLocations.Select(site => $"{site.Span.Start}+{site.Span.Length} line {site.Start.Line}:{site.Start.Character}")));
        Assert.Equal(2, bag.Select(diagnostic => diagnostic.Location.Span.Start).Distinct().Count());
        Assert.All(bag, diagnostic =>
        {
            Assert.Equal("consumer.xe", diagnostic.Location.Path);
            Assert.Contains("SourceLess.xelib", Assert.Single(diagnostic.RelatedLocations).Message);
            Assert.Contains("while specializing", Assert.Single(diagnostic.RelatedLocations).Message);
        });
    }

    [Fact]
    public void LibraryFallbackWithoutCallSiteHasStableIdentityAndContext()
    {
        var library = Compilation.Create(SourceText.From("namespace Lib; public void Forward(int value) {}", "library.xe"));
        Assert.Empty(library.Diagnostics);
        var reference = XelibReader.Read(XelibWriter.Write(library, new XelibWriteOptions("NoSource")));
        var function = reference.ImplementationFunctions.Single(body => body.Symbol.Name == "Forward").Symbol;
        var first = MirDiagnosticSource.Resolve(function, TextLocation.None);
        Assert.Equal(first, MirDiagnosticSource.Resolve(function, TextLocation.None));
        Assert.Contains("NoSource.xelib", first.Path);
        Assert.Contains("Lib.Forward", first.Path);
        var bag = new DiagnosticBag();
        MirDiagnosticReporter.Report(bag, function, TextLocation.None, "first wording", DiagnosticIds.UnresolvedLifetimeOwner, "operation");
        MirDiagnosticReporter.Report(bag, function, TextLocation.None, "second wording", DiagnosticIds.UnresolvedLifetimeOwner, "operation");
        Assert.Single(bag);
    }
    [Fact]
    public void GenericMIRPreservesOriginalExpressionSpans()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Test;
            T Identity<T>(T value) { return move value; }
            int Use() { return Identity<int>(42); }
            """, "generic.xe"));
        Assert.Empty(compilation.Diagnostics);
        var bound = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name.StartsWith("Identity<"));
        var mir = MirLowerer.Lower(bound, compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations);
        var move = Assert.Single(mir.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>()
            .Where(assign => assign.IsSemanticRead && assign.IsMoveRead));
        Assert.Equal("generic.xe", move.Source.Location.Path);
        Assert.False(move.Source.IsFallback);
        Assert.Equal(1, move.Source.Location.Start.Line);
    }

    [Fact]
    public void ProvenanceTraceShowsPointeeAndNestedPath()
    {
        var compilation = Compilation.Create(SourceText.From("""
            namespace Test;
            struct Inner { public storage<int> Value; }
            struct State { public Inner Nested; }
            struct Holder { public shared<State> State; }
            void Take(readonly Holder& holder, storage<int>& output) { output = move holder.State->Nested.Value; }
            """));
        Assert.Empty(compilation.Diagnostics);
        string trace = compilation.DumpMir(includeProvenance: true);
        Assert.Contains("// provenance Test.Take", trace);
        Assert.Contains("SharedPointee(", trace);
        Assert.Contains("authority=Owner", trace);
        Assert.Contains(")/0/0", trace);
        Assert.DoesNotContain("// provenance", compilation.DumpMir());
        Assert.Equal(trace, compilation.DumpMir(includeProvenance: true));
    }
}