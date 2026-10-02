using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Analysis;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirProvenanceAuditTests
{
    public static IEnumerable<object[]> HandlesAndCarriers =>
        from handle in new[] { "shared<State>", "unique<State>", "State*" }
        from carrier in new[] { "Holder", "Holder&", "readonly Holder&", "this", "local" }
        select new object[] { handle, carrier };

    [Theory]
    [MemberData(nameof(HandlesAndCarriers))]
    public void HandleFieldStartsPointeeRoot(string handle, string carrier)
    {
        string operation = "output = move holder.State->Nested.Value;";
        string method = carrier == "this" ? "public void Take(storage<Resource>& output) { output = move State->Nested.Value; }" : "";
        string body = carrier == "this" ? "" :
            carrier == "local" ? $"void Take(storage<Resource>& output) {{ Holder holder = Holder(); {operation} }}" :
            $"void Take({carrier} holder, storage<Resource>& output) {{ {operation} }}";
        var compilation = Compile($$"""
            struct Resource { public int Value; public ~Resource() {} }
            struct Nested { public storage<Resource> Value; }
            struct State { public Nested Nested; }
            struct Holder { public {{handle}} State; public Holder() { State = new State(); } {{method}} }
            {{body}}
            """);
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        var bound = compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Take");
        var mir = MirLowerer.Lower(bound, compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations);
        var origins = new MirReferenceOrigins(mir);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(mir), origins);
        var roots = mir.Blocks.SelectMany(block => block.Statements.Select((statement, index) => (block, statement, index)))
            .Where(item => item.statement is MirAssign { IsSemanticRead: true, IsMoveRead: true })
            .SelectMany(item => MirOperands.Of(((MirAssign)item.statement).Value).SelectMany(MirOperands.Places)
                .SelectMany(place => origins.Address(place, flow.Before[new(item.block.Id, item.index)]))).ToArray();
        var root = Assert.Single(roots);
        Assert.Equal(handle.StartsWith("shared") ? MirReferenceOriginKind.SharedPointee :
            handle.StartsWith("unique") ? MirReferenceOriginKind.UniquePointee : MirReferenceOriginKind.RawPointee, root.Kind);
        Assert.Equal(MirLifetimeAuthority.Owner, root.Authority);
        Assert.Equal("0/0", root.Path);
    }

    [Theory]
    [InlineData("Pair")]
    [InlineData("Pair&")]
    [InlineData("readonly Pair&")]
    public void OpaqueSharedFieldsMayAlias(string parameter)
    {
        var compilation = Compile($$"""
            struct Pair { public shared<int> First; public shared<int> Second; }
            void Test({{parameter}} pair) {
                int& a = *pair.First;
                int& b = *pair.Second;
                a = 1; b = 2;
            }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowConflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KnownSharedFieldAllocationsRetainPrecision(bool same)
    {
        var compilation = Compile($$"""
            struct Pair { public shared<int> First; public shared<int> Second; }
            void Test() {
                shared<int> first = new int();
                shared<int> second = {{(same ? "first" : "new int()")}};
                Pair pair = Pair { first, second };
                int& a = *pair.First;
                int& b = *pair.Second;
                a = 1; b = 2;
            }
            """);
        Assert.Equal(same, compilation.Diagnostics.Any(diagnostic => diagnostic.Id == DiagnosticIds.BorrowConflict));
        if (!same) Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("move first")]
    [InlineData("Forward<shared<int>>(first)")]
    [InlineData("lock weak first")]
    public void SharedForwardingPreservesAllocation(string forwarding)
    {
        var compilation = Compile($$"""
            T Forward<T>(T value) { return move value; }
            void Test() {
                shared<int> first = new int();
                shared<int> second = {{forwarding}};
                int& a = *first;
                int& b = *second;
                a = 1; b = 2;
            }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id is DiagnosticIds.BorrowConflict or DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void GenericOrdinaryReferenceDiagnosticIsNotDuplicated()
    {
        var compilation = Compile("""
            struct State<T> { public storage<T> Value; }
            void Bad<T>(State<T>& state, storage<T>& output) { output = move state.Value; }
            void Use(State<int>& state, storage<int>& output) { Bad<int>(state, output); }
            """);
        var diagnostic = Assert.Single(compilation.Diagnostics.Where(item => item.Id == DiagnosticIds.ReferenceParameterLifetimeMutation));
        Assert.Equal("audit.xe", diagnostic.Location.Path);
        Assert.True(diagnostic.Location.Span.Length > 0);
    }

    [Theory]
    [InlineData("shared<Data>")]
    [InlineData("unique<Data>")]
    public void InlinePointeePartialMoveRetainsPreMirRestriction(string owner)
    {
        var compilation = Compile($$"""
            struct Resource { public int Value; }
            struct Data { public Resource A; public Resource B; }
            int Take({{owner}} data) {
                Resource value = move data->A;
                return data->B.Value;
            }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnresolvedLifetimeOwner);
    }

    [Theory]
    [InlineData("shared<Data>")]
    [InlineData("unique<Data>")]
    [InlineData("Data*")]
    public void SiblingPointeeBorrowsKeepTheirFieldPaths(string owner)
    {
        var compilation = Compile($$"""
            struct Data { public int A; public int B; }
            void Test({{owner}} data) {
                int& a = data->A; int& b = data->B;
                a = 1; b = 2;
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void OrdinaryInlineStorageReferenceStillLacksAuthority()
    {
        var compilation = Compile("""
            struct Resource { public ~Resource() {} }
            struct State { public storage<Resource> Value; }
            void Bad(State& state, storage<Resource>& output) { output = move state.Value; }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ReferenceParameterLifetimeMutation);
    }

    [Fact]
    public void ReadonlyHandleCannotBeReplacedButItsPointeeCanBeUpdated()
    {
        var compilation = Compile("""
            struct Holder { public shared<int> Value; }
            void Good(readonly Holder& holder) { *holder.Value = 42; }
            void Bad(readonly Holder& holder) { holder.Value = new int(); }
            """);
        Assert.Single(compilation.Diagnostics);
        Assert.Equal(DiagnosticIds.InvalidAssignmentTarget, compilation.Diagnostics[0].Id);
    }

    [Theory]
    [InlineData("shared<Inner>", "unique<State>")]
    [InlineData("Inner*", "shared<State>")]
    [InlineData("unique<Inner>", "State*")]
    public void MixedNestedHandlesCrossEachBoundary(string outer, string inner)
    {
        var compilation = Compile($$"""
            struct State { public storage<int> Value; }
            struct Inner { public {{inner}} Child; }
            struct Holder { public {{outer}} Root; }
            void Take(Holder& holder, storage<int>& output) { output = move holder.Root->Child->Value; }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void WeakFieldPromotionUsesTheSharedPointee()
    {
        var compilation = Compile("""
            struct State { public storage<int> Value; }
            struct Holder { public weak<State> State; }
            void Take(readonly Holder& holder, storage<int>& output) {
                shared<State> locked = lock holder.State;
                output = move locked->Value;
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("shared<State>", "state")]
    [InlineData("unique<State>", "move state")]
    [InlineData("shared<State>", "&state")]
    [InlineData("shared<State>", "readonly &state")]
    public void CapturedHandleRetainsPointeeAuthority(string handle, string capture)
    {
        var compilation = Compile($$"""
            struct State { public storage<int> Value; }
            void Test({{handle}} state) {
                function void(storage<int>&) callback = [{{capture}}](storage<int>& output) => {
                    output = move state->Value;
                };
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void CapturedAggregateRetainsHandleFields()
    {
        var compilation = Compile("""
            struct State { public storage<int> Value; }
            struct Holder { public shared<State> State; }
            void Test(Holder holder) {
                function void(storage<int>&) callback = [holder](storage<int>& output) => {
                    output = move holder.State->Value;
                };
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }
    [Theory]
    [InlineData("shared<int>", "first")]
    [InlineData("shared<int>", "move first")]
    [InlineData("unique<int>", "move first")]
    public void HandleTransferKeepsExactPointeeIdentity(string handle, string transfer)
    {
        var compilation = Compile($$"""
            void Test() {
                {{handle}} first = new int();
                int before = *first;
                {{handle}} second = {{transfer}};
                int after = *second;
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        var mir = MirLowerer.Lower(compilation.SemanticModel.Functions.Single(function => function.Symbol.Name == "Test"),
            compilation.TypeFactory, compilation.SemanticModel.ExpressionLocations);
        var origins = new MirReferenceOrigins(mir);
        var flow = MirDataflow.Solve(MirFlowFacts.Graph(mir), origins);
        var roots = mir.Blocks.SelectMany(block => block.Statements.Select((statement, index) => (block, statement, index)))
            .Where(item => item.statement is MirAssign { IsSemanticRead: true })
            .SelectMany(item => MirOperands.Of(((MirAssign)item.statement).Value).SelectMany(MirOperands.Places)
                .Where(place => place.Projections.Any(projection => projection is MirDerefProjection))
                .SelectMany(place => origins.Address(place, flow.Before[new(item.block.Id, item.index)]))).ToArray();
        Assert.Equal(2, roots.Length);
        Assert.Equal(roots[0], roots[1]);
        Assert.True(roots[0].IsFresh);
    }

    [Fact]
    public void PinnedCarrierDoesNotPinSeparatePointeeStorage()
    {
        var compilation = Compile("""
            struct State { public storage<int> Value; }
            struct Holder { public shared<State> State; }
            void Test(storage<int>& output) {
                pin<Holder> holder = Holder { new State() };
                output = move holder.State->Value;
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UniqueChildOfPotentiallyAliasingSharedOwnersMayAlias(bool nested)
    {
        var compilation = Compile($$"""
            struct Inner { public unique<int> Child; }
            struct Holder { public {{(nested ? "unique<Inner>" : "unique<int>")}} Child; }
            void Test(shared<Holder> first, shared<Holder> second) {
                int& a = *first->Child{{(nested ? "->Child" : "")}};
                int& b = *second->Child{{(nested ? "->Child" : "")}};
                a = 1; b = 2;
            }
            """);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowConflict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctUniqueChildrenKeepTheirOwnershipIndependence(bool separateAllocations)
    {
        var compilation = Compile($$"""
            struct Holder { public unique<int> First; public unique<int> Second; }
            void Test() {
                shared<Holder> first = new Holder { new int(), new int() };
                shared<Holder> second = {{(separateAllocations ? "new Holder { new int(), new int() }" : "first")}};
                int& a = *first->First;
                int& b = *second->{{(separateAllocations ? "First" : "Second")}};
                a = 1; b = 2;
            }
            """);
        Assert.Empty(compilation.Diagnostics);
    }
    private static Compilation Compile(string source) => Compilation.Create(SourceText.From("namespace Audit;\n" + source, "audit.xe"));
}