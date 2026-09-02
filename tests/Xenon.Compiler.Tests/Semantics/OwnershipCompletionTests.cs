using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class OwnershipCompletionTests
{
    [Fact]
    public void Analyzer_AllowsSharedReadonlyBorrowsAndNonLexicalMutableBorrow()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Pair
            {
                public int First;
                public int Second;
                public int readonly Read() { return First + Second; }
            }
            int Main()
            {
                Pair value = Pair { 1, 2 };
                readonly Pair& a = value;
                readonly Pair& b = value;
                int sum = a.Read() + b.Read() + value.Read();
                Pair& mutable = value;
                mutable.First = sum;
                value.Second = 4;
                Pair& first = value;
                int& left = first.First;
                int& right = first.Second;
                left = right;
                return value.Read();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("Pair& first = value; Pair& second = value; first.First = second.First;", DiagnosticIds.BorrowConflict)]
    [InlineData("readonly Pair& view = value; value.First = 2; return view.First;", DiagnosticIds.BorrowedPlaceMutation)]
    [InlineData("Pair& view = value; int x = value.First; return view.First + x;", DiagnosticIds.BorrowedPlaceAccess)]
    [InlineData("Pair& view = value; Pair moved = move value; return view.First + moved.First;", DiagnosticIds.MoveWhileBorrowed)]
    public void Analyzer_RejectsConflictingBorrows(string body, string diagnosticId)
    {
        Compilation compilation = Create($$"""
            namespace Example;
            struct Pair { public int First; public int Second; }
            int Main() { Pair value = Pair(); {{body}} return 0; }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void Analyzer_ModelsStorageConstructionDestructionReuseAndMoveOut()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource
            {
                public int Value;
                public void Set(int value) { Value = value; }
                public ~Resource() { Value = 0; }
            }
            int Main()
            {
                storage<Resource> slot;
                slot = Resource();
                slot.Set(7);
                destruct(slot);
                slot = Resource { 9 };
                Resource value = move slot;
                return value.Value;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_AllowsStorageStateToCrossFieldsMethodsPointersAndHeapAllocation()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource { public void Touch() {} public ~Resource() {} }
            struct Holder
            {
                public storage<Resource> Value;
                public void Create() { Value = Resource(); }
                public void Destroy() { destruct(Value); }
                public void Read() { Value.Touch(); }
            }
            void Main()
            {
                Holder holder = Holder();
                holder.Create();
                holder.Read();
                holder.Destroy();
                holder.Create();
                storage<Resource>* value = new storage<Resource>();
                *value = Resource();
                destruct(*value);
                *value = Resource();
                Resource result = move *value;
                free(value);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Theory]
    [InlineData("storage<Resource> slot; int x = slot.Value;", DiagnosticIds.StorageNotInitialized)]
    [InlineData("storage<Resource> slot; destruct(slot);", DiagnosticIds.ExplicitDestructionRequiresLiveValue)]
    [InlineData("storage<Resource> slot; slot = Resource(); slot = Resource();", DiagnosticIds.StorageAlreadyInitialized)]
    public void Analyzer_RejectsInvalidManualLifetimeOperations(string body, string diagnosticId)
    {
        Compilation compilation = Create($$"""
            namespace Example;
            struct Resource { public int Value; public ~Resource() {} }
            void Main() { {{body}} }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void Analyzer_ModelsStorageAndPinTypeProperties()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource { public int Value; public ~Resource() {} }
            void Main()
            {
                pin<Resource> fixedValue = Resource { 7 };
                int result = fixedValue.Value;
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        TypeFactory types = compilation.TypeFactory;
        StructTypeSymbol resource = Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Structs);
        Assert.Equal("storage<Example.Resource>", types.StorageOf(resource).ToDisplayString(TypeDisplayFormat.FullyQualified));
        Assert.False(TypeFacts.CanCopy(types.StorageOf(resource)));
        Assert.True(TypeFacts.HasAutomaticDestructor(types.PinOf(resource)));
        Assert.False(TypeFacts.CanMove(types.PinOf(resource)));
    }

    [Fact]
    public void Analyzer_RejectsMovingPinnedValuesAndContainingAggregates()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct Container { public pin<Resource> Value; }
            void InvalidPin() { pin<Resource> value = Resource(); pin<Resource> moved = move value; }
            void InvalidContainer() { Container value; Container moved = move value; }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(diagnostic => diagnostic.Id == DiagnosticIds.PinnedRelocation));
    }

    [Fact]
    public void Analyzer_RejectsPinnedByValueAbi()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct Container { public pin<Resource> Value; }
            pin<Resource> Create() { return Resource(); }
            void Process(pin<Resource> value) {}
            Container CreateContainer() { return Container(); }
            """);

        Assert.True(compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.PinnedRelocation) >= 3);
    }

    [Fact]
    public void Analyzer_SubstitutesLifetimeModifiersInGenericStructs()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource {}
            struct Slot<T>
            {
                public storage<T> Storage;
            }
            void Main() { Slot<Resource> slot; }
            """);

        Assert.Empty(compilation.Diagnostics);
        StructTypeSymbol slot = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces)
            .Structs.Single(type => type.GenericDefinition is not null);
        Assert.IsType<StorageTypeSymbol>(slot.Fields[0].Type);
        Assert.Equal("storage<Example.Resource>", slot.Fields[0].Type.ToDisplayString(TypeDisplayFormat.FullyQualified));
        Assert.False(TypeFacts.CanCopy(slot));
    }

    [Fact]
    public void Analyzer_RejectsAggregateReferenceEscapesTransitively()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; public void Flush() {} }
            struct Inner { public Data& Data; public ~Inner() { Data.Flush(); } }
            struct Outer { public Inner Inner; }
            Inner Bad()
            {
                Data local = Data();
                return Inner { local };
            }
            Outer NestedBad()
            {
                Data local = Data();
                return Outer { Inner { local } };
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.AggregateReferenceEscape));
    }

    [Fact]
    public void Analyzer_ComposesBorrowPlacesThroughReferenceReturningCalls()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            Data& Forward(Data& value) { return value; }
            void Invalid()
            {
                Data value = Data();
                Data& reference = Forward(value);
                Data moved = move value;
                reference.Value = moved.Value;
            }
            void Valid()
            {
                Data value = Data();
                Data& reference = Forward(value);
                reference.Value = 1;
                Data moved = move value;
            }
            """);

        Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MoveWhileBorrowed);
    }

    [Fact]
    public void Analyzer_SuspendsParentBorrowWhileChildReborrowIsLive()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Pair { public int First; public int Second; }
            int Main()
            {
                Pair value = Pair();
                Pair& parent = value;
                int& child = parent.First;
                parent.First = 1;
                return child;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Fact]
    public void Analyzer_MergesStorageStateAcrossControlFlow()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource { public ~Resource() {} }
            void Balanced(bool condition)
            {
                storage<Resource> slot;
                if (condition) slot = Resource();
                else slot = Resource();
                destruct(slot);
            }
            void MaybeEmpty(bool condition)
            {
                storage<Resource> slot;
                if (condition) slot = Resource();
                destruct(slot);
            }
            void MaybeInitialized(bool condition)
            {
                storage<Resource> slot;
                if (condition) slot = Resource();
                slot = Resource();
                destruct(slot);
            }
            """);

        Assert.DoesNotContain(compilation.Diagnostics, diagnostic =>
            diagnostic.Id is DiagnosticIds.ExplicitDestructionRequiresLiveValue or
                DiagnosticIds.StorageAlreadyInitialized);
    }

    [Fact]
    public void Analyzer_TracksMutableAndReadonlyReferenceFieldsAsBorrows()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; public void Mutate() { Value++; } }
            struct View { public Data& Data; }
            struct ReadView { public readonly Data& Data; }
            int MutableMove()
            {
                Data data = Data();
                View view = View { data };
                Data moved = move data;
                return view.Data.Value + moved.Value;
            }
            int ReadonlyMutation()
            {
                Data data = Data();
                ReadView view = ReadView { data };
                data.Mutate();
                return view.Data.Value;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MoveWhileBorrowed);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.BorrowedPlaceMutation);
    }

    [Fact]
    public void Analyzer_EndsAggregateBorrowAfterLastUseAndTransfersItOnMove()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct View { public Data& Data; }
            void EndsAfterUse()
            {
                Data data = Data();
                View view = View { data };
                int observed = view.Data.Value;
                Data moved = move data;
            }
            int TransferOnMove()
            {
                Data data = Data();
                View first = View { data };
                View second = move first;
                Data moved = move data;
                return second.Data.Value + moved.Value;
            }
            """);

        Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MoveWhileBorrowed);
    }

    [Fact]
    public void Analyzer_UpdatesAggregateAssignmentProvenanceAndRejectsMutableReferenceCopies()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct View { public Data& Data; }
            struct ReadView { public readonly Data& Data; }
            void AssignmentEscape(Data& external)
            {
                View view = View { external };
                {
                    Data local = Data();
                    view = View { local };
                }
                int value = view.Data.Value;
            }
            void InvalidCopy(Data& data)
            {
                View first = View { data };
                View second = first;
            }
            void ReadonlyCopy(Data& data)
            {
                ReadView first = ReadView { data };
                ReadView second = first;
                int value = first.Data.Value + second.Data.Value;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ReferenceDestructionOrder);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ValueNotCopyable);
    }

    [Fact]
    public void Analyzer_PreservesAndEndsReferenceMetadataInsideStorage()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct View { public Data& Data; }
            void InvalidEscape()
            {
                storage<View> slot;
                {
                    Data local = Data();
                    slot = View { local };
                }
                int value = slot.Data.Value;
                destruct(slot);
            }
            void DestructionEndsBorrow()
            {
                storage<View> slot;
                Data data = Data();
                slot = View { data };
                int observed = slot.Data.Value;
                destruct(slot);
                Data moved = move data;
            }
            int MovePreservesBorrow()
            {
                Data data = Data();
                storage<View> slot;
                slot = View { data };
                View view = move slot;
                Data moved = move data;
                return view.Data.Value + moved.Value;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.ReferenceDestructionOrder);
        Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MoveWhileBorrowed);
    }

    [Fact]
    public void Analyzer_PreservesPinThroughNestedLifetimeModifiers()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource { public ~Resource() {} }
            void InvalidMoves()
            {
                pin<Resource> first = Resource();
                pin<Resource> movedFirst = move first;
            }
            void StableStorage()
            {
                pin<storage<Resource>> slot;
                slot = Resource();
                destruct(slot);
            }
            """);

        Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.PinnedRelocation);
        TypeFactory types = compilation.TypeFactory;
        StructTypeSymbol resource = Assert.Single(
            Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces).Structs);
        Assert.True(TypeFacts.IsPinned(types.PinOf(types.StorageOf(resource))));
        Assert.False(TypeFacts.CanMove(types.PinOf(types.StorageOf(resource))));
        Assert.True(TypeFacts.HasAutomaticDestructor(types.PinOf(types.StorageOf(resource))));
    }

    [Fact]
    public void Analyzer_RejectsReferenceFieldMutationFromOrdinaryMethods()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data {}
            struct View
            {
                public Data& Data;
                public View(Data& data) { Data = data; }
                public void Set(Data& data) { Data = data; }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.ReferenceProvenanceMutation);
    }

    [Fact]
    public void Analyzer_TracksConstructorAndChildPlaceReferenceProvenance()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct Object { public Data First; public Data Second; }
            struct View
            {
                public Data& Data;
                public View(Data& data) { Data = data; }
            }
            int ConstructorResult()
            {
                Data data = Data();
                View view = View(data);
                Data moved = move data;
                return view.Data.Value + moved.Value;
            }
            int ParentMove()
            {
                Object value = Object();
                View view = View { value.First };
                Object moved = move value;
                return view.Data.Value + moved.First.Value;
            }
            int DisjointField()
            {
                Object value = Object();
                View view = View { value.First };
                value.Second.Value = 42;
                return view.Data.Value + value.Second.Value;
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(diagnostic => diagnostic.Id == DiagnosticIds.MoveWhileBorrowed));
    }

    [Fact]
    public void Analyzer_UsesActualConstructorReferenceFieldOrigins()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct View
            {
                public Data& Data;
                public View(Data& ignored, Data& actual) { Data = actual; }
            }
            struct Pair
            {
                public Data& Left;
                public Data& Right;
                public Pair(Data& first, Data& second)
                {
                    Left = second;
                    Right = first;
                }
            }
            void StorageEscape()
            {
                Data external = Data();
                storage<View> value;
                {
                    Data local = Data();
                    value = View(external, local);
                }
                int observed = value.Data.Value;
            }
            void ReversedMapping()
            {
                Data first = Data();
                storage<Pair> value;
                {
                    Data second = Data();
                    value = Pair(first, second);
                }
                int observed = value.Left.Value;
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.ReferenceDestructionOrder));
    }

    [Fact]
    public void Analyzer_UnionsConditionalConstructorOriginsAndRejectsConstructorLocals()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct View
            {
                public Data& Data;
                public View(Data& first, Data& second, bool chooseFirst)
                {
                    if (chooseFirst) Data = first;
                    else Data = second;
                }
            }
            struct Invalid
            {
                public Data& Data;
                public Invalid()
                {
                    Data local = Data();
                    Data = local;
                }
            }
            void Escape(bool condition)
            {
                Data external = Data();
                storage<View> value;
                {
                    Data local = Data();
                    value = View(external, local, condition);
                }
                int observed = value.Data.Value;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.AggregateReferenceEscape);
        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.ReferenceDestructionOrder);
    }

    [Fact]
    public void Analyzer_PreservesConstructorReferenceOriginsThroughGenericSpecialization()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct View<T>
            {
                public T& Value;
                public View(T& ignored, T& actual) { Value = actual; }
            }
            void Escape()
            {
                Data external = Data();
                storage<View<Data>> value;
                {
                    Data local = Data();
                    value = View<Data>(external, local);
                }
                int observed = value.Value.Value;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.ReferenceDestructionOrder);
    }

    [Fact]
    public void Analyzer_PreservesReferenceOriginsThroughConstructorChains()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct View
            {
                public Data& Data;
                public View(Data& ignored, Data& actual) { Data = actual; }
                public View(Data& actual) : this(actual, actual) {}
            }
            struct BaseView
            {
                public Data& Data;
                public BaseView(Data& ignored, Data& actual) { Data = actual; }
            }
            struct DerivedView : BaseView
            {
                public DerivedView(Data& ignored, Data& actual) : base(ignored, actual) {}
            }
            void EscapeThisChain()
            {
                storage<View> value;
                {
                    Data local = Data();
                    value = View(local);
                }
                int observed = value.Data.Value;
            }
            void EscapeBaseChain()
            {
                Data external = Data();
                storage<DerivedView> value;
                {
                    Data local = Data();
                    value = DerivedView(external, local);
                }
                int observed = value.Data.Value;
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.ReferenceDestructionOrder));
    }

    [Fact]
    public void Analyzer_TransfersProvenanceOnMoveAssignmentAndReplacesStorageProvenance()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; }
            struct View { public Data& Data; }
            int MoveAssignment()
            {
                Data first = Data();
                Data second = Data();
                View destination = View { first };
                View source = View { second };
                destination = move source;
                Data oldValue = move first;
                Data invalid = move second;
                return destination.Data.Value + invalid.Value + oldValue.Value;
            }
            void ReconstructStorage()
            {
                storage<View> slot;
                Data first = Data();
                Data second = Data();
                slot = View { first };
                int firstValue = slot.Data.Value;
                destruct(slot);
                Data movedFirst = move first;
                slot = View { second };
                int secondValue = slot.Data.Value;
                destruct(slot);
                Data movedSecond = move second;
            }
            """);

        Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MoveWhileBorrowed);
    }

    [Theory]
    [InlineData("storage<pin<Resource>> value;", DiagnosticIds.InvalidLifetimeModifier)]
    [InlineData("storage<storage<Resource>> value;", DiagnosticIds.InvalidLifetimeModifier)]
    [InlineData("unique<pin<Resource>> value;", DiagnosticIds.InvalidUniqueTypeArgument)]
    [InlineData("shared<pin<Resource>> value;", DiagnosticIds.InvalidUniqueTypeArgument)]
    [InlineData("weak<pin<Resource>> value;", DiagnosticIds.InvalidUniqueTypeArgument)]
    public void Analyzer_EnforcesLifetimeModifierCompatibilityMatrix(string declaration, string diagnosticId)
    {
        Compilation compilation = Create($$"""
            namespace Example;
            struct Resource {}
            void Main() { {{declaration}} }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == diagnosticId);
    }

    [Fact]
    public void Analyzer_DoesNotInitializeStorageWhenConstructorBindingFails()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource { public Resource(int value) {} }
            void Main()
            {
                storage<Resource> slot;
                slot = true;
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch);
    }

    [Fact]
    public void Analyzer_KeepsReferenceBorrowAliveForDestructorValue()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data { public int Value; public void Flush() {} }
            struct View { public Data& Data; public ~View() { Data.Flush(); } }
            void DestructorUse()
            {
                Data data = Data();
                View view = View { data };
                int observed = view.Data.Value;
                Data moved = move data;
            }
            void ExplicitEnd()
            {
                Data data = Data();
                View view = View { data };
                destruct(view);
                Data moved = move data;
            }
            """);

        Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.MoveWhileBorrowed);
    }

    [Fact]
    public void Analyzer_RejectsLifetimeEndingOperationsWithActiveOverlappingBorrows()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource
            {
                public int Value;
                public void Use() { Value++; }
                public int readonly Read() { return Value; }
                public ~Resource() {}
            }
            struct View { public Resource& Resource; }
            Resource& Forward(Resource& value) { return value; }
            void UseView(readonly View& view) { view.Resource.Read(); }
            void MutableWhole()
            {
                Resource value = Resource();
                Resource& reference = value;
                destruct(value);
                reference.Use();
            }
            void ReadonlyWhole()
            {
                Resource value = Resource();
                readonly Resource& reference = value;
                destruct(value);
                int observed = reference.Read();
            }
            void MutableChild()
            {
                Resource value = Resource();
                int& reference = value.Value;
                destruct(value);
                reference = 42;
            }
            void ReadonlyChild()
            {
                Resource value = Resource();
                readonly int& reference = value.Value;
                destruct(value);
                int observed = reference;
            }
            void StorageValue()
            {
                storage<Resource> slot = Resource();
                Resource& reference = slot;
                destruct(slot);
                reference.Use();
            }
            void StorageChild()
            {
                storage<Resource> slot = Resource();
                int& reference = slot.Value;
                destruct(slot);
                reference = 10;
            }
            void MutableStorage()
            {
                storage<Resource> slot = Resource();
                storage<Resource>& reference = slot;
                destruct(slot);
                reference.Use();
            }
            void ReadonlyStorage()
            {
                storage<Resource> slot = Resource();
                readonly storage<Resource>& reference = slot;
                destruct(slot);
                int observed = reference.Read();
            }
            void Aggregate()
            {
                Resource value = Resource();
                View view = View { value };
                destruct(value);
                UseView(view);
            }
            void ReturnedReference()
            {
                Resource value = Resource();
                Resource& reference = Forward(value);
                destruct(value);
                reference.Use();
            }
            void DirectHeapPointer()
            {
                Resource* value = new Resource();
                Resource& reference = *value;
                free(value);
                reference.Use();
            }
            void DirectHeapStoragePointer()
            {
                storage<Resource>* value = new storage<Resource>();
                *value = Resource();
                Resource& reference = *value;
                free(value);
                reference.Use();
            }
            """);

        Assert.Equal(10, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.DestructWhileBorrowed));
        Assert.Equal(2, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.FreeWhileBorrowed));
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic =>
            diagnostic.Id == DiagnosticIds.BorrowedPlaceAccess);
    }

    [Fact]
    public void Analyzer_AllowsLifetimeEndAfterFinalUseAndThroughMutableStorageReference()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource
            {
                public int Value;
                public void Use() { Value++; }
                public int readonly Read() { return Value; }
                public ~Resource() {}
            }
            void Read(readonly Resource& value) { value.Read(); }
            void MutableFinalUse()
            {
                Resource value = Resource();
                Resource& reference = value;
                reference.Use();
                destruct(value);
            }
            void ReadonlyFinalUse()
            {
                Resource value = Resource();
                readonly Resource& reference = value;
                int observed = reference.Read();
                destruct(value);
            }
            void ChildFinalUse()
            {
                Resource value = Resource();
                int& reference = value.Value;
                reference = 10;
                destruct(value);
            }
            void StorageReferenceReuse()
            {
                storage<Resource> slot;
                storage<Resource>& reference = slot;
                reference = Resource();
                destruct(reference);
                reference = Resource();
                reference.Use();
            }
            void ContainedFinalUse()
            {
                storage<Resource> slot = Resource();
                Resource& reference = slot;
                reference.Use();
                destruct(slot);
            }
            void Disjoint()
            {
                Resource first = Resource();
                Resource second = Resource();
                Resource& reference = first;
                destruct(second);
                reference.Use();
            }
            void CallBorrowEnds()
            {
                Resource value = Resource();
                Read(value);
                destruct(value);
            }
            void DirectHeapBorrowEnds()
            {
                Resource* value = new Resource();
                Resource& reference = *value;
                reference.Use();
                free(value);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_RejectsLifetimeMutationThroughReadonlyStorageReference()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Resource { public ~Resource() {} }
            void Destruct()
            {
                storage<Resource> slot = Resource();
                readonly storage<Resource>& reference = slot;
                destruct(reference);
            }
            void Reconstruct()
            {
                storage<Resource> slot;
                readonly storage<Resource>& reference = slot;
                reference = Resource();
            }
            void Move()
            {
                storage<Resource> slot = Resource();
                readonly storage<Resource>& reference = slot;
                Resource value = move reference;
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(diagnostic =>
            diagnostic.Id == DiagnosticIds.InvalidAssignmentTarget));
        Assert.Single(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidMoveSource);
    }

    private static Compilation Create(string source) =>
        Compilation.Create(SourceText.From(source, "ownership-completion.xe"));
}
