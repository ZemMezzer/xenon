using Xenon.Compiler;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xenon.CodeGen.LLVM;
using Xunit;

namespace Xenon.Compiler.Tests;

public sealed class ExceptionFrontendTests
{
    [Fact]
    public void HeapArrayReplacementKeepsRhsGuardedUntilOldCleanupCommits()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { public ~Item() { throw 7; } }
            void Test()
            {
                try { Item[] values = Item[3]; values = new Item[3]; }
                catch (readonly int& error) { }
            }
            """);

        string ir = new LlvmIrGenerator().GenerateForTarget(
            compilation, LlvmTargetOptions.CreateHost());

        Assert.Contains("assignment.array.length", ir, StringComparison.Ordinal);
        Assert.Contains("allocation.guard.active", ir, StringComparison.Ordinal);
        int guardActivation = ir.IndexOf(
            "store i1 true, ptr %allocation.guard.active", StringComparison.Ordinal);
        int destinationDestruction = ir.IndexOf(
            "array.destination.destroy", StringComparison.Ordinal);
        Assert.True(guardActivation >= 0 && destinationDestruction > guardActivation);
    }

    [Fact]
    public void StackArrayReplacementDoesNotEndRhsStorageAtFullExpressionBoundary()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { public ~Item() { } }
            void Test()
            {
                Item[] values = Item[1];
                values = Item[1];
                values[0] = Item();
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        BoundFunction function = Assert.Single(compilation.SemanticModel.Functions,
            candidate => candidate.Symbol.Name == "Test");
        BoundExpressionStatement replacement = Assert.IsType<BoundExpressionStatement>(
            function.Body.Statements[1]);
        Assert.IsType<BoundAssignmentExpression>(replacement.Expression);
    }

    [Fact]
    public void CatchCannotUseDestinationThatMayHaveDiedDuringReplacement()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { public ~Item() { } }
            struct Payload
            {
                public shared<Item> Owner;
            }
            void Replace(Payload value)
            {
                try { value.Owner = new Item(); }
                catch (readonly int& error) { shared<Item> copy = value.Owner; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void DestructorKeepsNestedReplacementLiveOnExceptionalExit()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Leaf { public ~Leaf() { } }
            struct Branch { public Leaf Value; }
            struct Owner
            {
                public Branch Branch;
                public ~Owner() { this.Branch.Value = Leaf(); }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void DestructorKeepsInheritedReplacementLiveOnExceptionalExit()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Field { public ~Field() { } }
            struct Base { public Field Value; }
            struct Derived : Base
            {
                public ~Derived() { this.Value = Field(); }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void MethodKeepsReplacedReceiverFieldLiveWhenOldDestructorThrows()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Field { public ~Field() { throw 7; } }
            struct Container
            {
                public Field Value;
                public void Replace() { this.Value = Field(); }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void ParserBuildsDedicatedExceptionSyntax()
    {
        SyntaxTree tree = SyntaxTree.Parse(SourceText.From("""
            namespace Example;
            void Run()
            {
                try { throw 42; }
                catch (readonly int& value) { throw; }
                catch (...) { }
                finally { }
            }
            """, "exceptions.xe"));

        Assert.Empty(tree.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var statement = Assert.IsType<TryStatementSyntax>(Assert.Single(function.Body!.Statements));
        Assert.Equal(2, statement.Catches.Length);
        Assert.False(statement.Catches[0].IsCatchAll);
        Assert.True(statement.Catches[1].IsCatchAll);
        Assert.IsType<ThrowStatementSyntax>(Assert.Single(statement.Body.Statements));
        Assert.True(Assert.IsType<ThrowStatementSyntax>(
            Assert.Single(statement.Catches[0].Body.Statements)).IsRethrow);
        Assert.NotNull(statement.FinallyBody);
    }

    [Fact]
    public void ParserRejectsBareTry()
    {
        SyntaxTree tree = SyntaxTree.Parse(SourceText.From(
            "namespace Example; void Run() { try { } }", "exceptions.xe"));

        Assert.Contains(tree.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.TryRequiresHandler);
    }

    [Fact]
    public void BinderValidatesRethrowCatchFormAndOrdering()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Error { }
            struct Derived : Error { }
            void Bad()
            {
                throw;
                try { throw Derived(); }
                catch (readonly Error& error) { }
                catch (readonly Derived& error) { }
                catch (Derived error) { }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.RethrowOutsideCatch);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnreachableCatch);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidCatchType);
    }

    [Fact]
    public void BinderPreservesThrowCopyAndMoveSemantics()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            void Copy(Payload value) { throw value; }
            void Move(Payload value) { throw move value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction copy = Assert.Single(compilation.GetStaticImplementationFunctions(), f => f.Symbol.Name == "Copy");
        BoundFunction move = Assert.Single(compilation.GetStaticImplementationFunctions(), f => f.Symbol.Name == "Move");
        Assert.IsType<BoundCopyExpression>(Unwrap(Assert.IsType<BoundThrowStatement>(Assert.Single(copy.Body.Statements)).Expression!));
        Assert.IsType<BoundMoveExpression>(Unwrap(Assert.IsType<BoundThrowStatement>(Assert.Single(move.Body.Statements)).Expression!));
    }

    [Fact]
    public void BinderCarriesExceptionalMoveStateIntoCatch()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            void Read(Payload value)
            {
                try { throw move value.Owner; }
                catch (...) { shared<Item> copy = value.Owner; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void BinderCarriesPotentialCallMoveStateIntoCatch()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            void Consume(shared<Item> value) { }
            void Fail() { throw 7; }
            void Read(Payload value)
            {
                try
                {
                    Consume(move value.Owner);
                    Fail();
                }
                catch (...) { shared<Item> copy = value.Owner; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void BinderCarriesReturnMoveStateIntoFinally()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            shared<Item> Read(Payload value)
            {
                try { return move value.Owner; }
                finally { shared<Item> copy = value.Owner; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void BinderCarriesImplicitInitializerThrowsIntoCatch()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            int Fail() { throw 7; }
            struct Element { public int Value = Fail(); }
            struct State { public static threadlocal int Value = Fail(); }
            void FromArray(Payload value)
            {
                try
                {
                    shared<Item> moved = move value.Owner;
                    Element[] elements = Element[1];
                }
                catch (...) { shared<Item> copy = value.Owner; }
            }
            void FromThreadLocal(Payload value)
            {
                try
                {
                    shared<Item> moved = move value.Owner;
                    int current = State.Value;
                }
                catch (...) { shared<Item> copy = value.Owner; }
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove));
    }

    [Fact]
    public void ThrowingLaterArgumentRollsBackEarlierUncommittedMoveInCatchFlow()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { }
            struct Payload { public shared<Resource> Owner; }
            extern void Consume(shared<Resource> owner, int value);
            int Throwing() { throw 7; }
            void Test(Payload value)
            {
                try { Consume(move value.Owner, Throwing()); }
                catch (...) { shared<Resource> stillAlive = value.Owner; }
            }
            """);

        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void ThrowingLaterArgumentDoesNotRollbackMoveNestedInsideCommittedArgumentExpression()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { }
            struct Payload { public shared<Resource> Owner; }
            void Consume(shared<Resource> owner, int value) { }
            shared<Resource> Forward(shared<Resource> owner) { return move owner; }
            int Throwing() { throw 7; }
            void Test(Payload value)
            {
                try { Consume(Forward(move value.Owner), Throwing()); }
                catch (...) { shared<Resource> invalid = value.Owner; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void InterfaceConvertedMoveStaysCommittedWhenLaterArgumentThrows()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface IValue { int readonly Read(); }
            struct Concrete : IValue
            {
                public int Id;
                public int readonly Read() { return Id; }
                public ~Concrete() { }
            }
            extern void Consume(IValue value, int second);
            int Throwing() { throw 7; }
            void Test()
            {
                Concrete value = Concrete();
                try { Consume(move value, Throwing()); }
                catch (...) { int invalid = value.Id; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void ConcreteTypedMoveStillRollsBackWhenLaterArgumentThrows()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface IValue { int readonly Read(); }
            struct Concrete : IValue
            {
                public int Id;
                public int readonly Read() { return Id; }
                public ~Concrete() { }
            }
            extern void Consume(Concrete value, int second);
            int Throwing() { throw 7; }
            void Test()
            {
                Concrete value = Concrete();
                try { Consume(move value, Throwing()); }
                catch (...) { int stillAlive = value.Id; }
            }
            """);

        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void ArgumentListRejectsReinitializationAfterOverlappingDeferredMove()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface IValue { int readonly Read(); }
            struct Concrete : IValue
            {
                public int Id;
                public int readonly Read() { return Id; }
                public ~Concrete() { }
            }
            extern void Consume(Concrete old, Concrete replacement, int second);
            int Throwing() { throw 7; }
            void Test()
            {
                Concrete value = Concrete();
                Consume(move value, value = Concrete(), Throwing());
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.ArgumentLifetimeConflict);
    }

    [Fact]
    public void MaterializedInterfaceMoveAllowsLaterReinitializationInDelayedConstructorPath()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface IValue { int readonly Read(); }
            struct Concrete : IValue
            {
                public int Id;
                public int readonly Read() { return Id; }
                public ~Concrete() { }
            }
            struct Consumer
            {
                public Consumer(IValue old, Concrete replacement, int second) { }
            }
            int Throwing() { throw 7; }
            void Test()
            {
                Concrete value = Concrete();
                Concrete replacement = Concrete();
                try
                {
                    Consumer* consumer = new Consumer(
                        move value, value = move replacement, Throwing());
                }
                catch (...) { int stillAlive = value.Id; }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void ConstructorInterfaceMoveStaysCommittedWhenLaterArgumentThrows()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface IValue { int readonly Read(); }
            struct Concrete : IValue
            {
                public int Id;
                public int readonly Read() { return Id; }
                public ~Concrete() { }
            }
            struct Consumer
            {
                public Consumer(IValue value, int second) { }
            }
            int Throwing() { throw 7; }
            void Test()
            {
                Concrete value = Concrete();
                try { Consumer* consumer = new Consumer(move value, Throwing()); }
                catch (...) { int invalid = value.Id; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void ExplicitGenericInterfaceMoveStaysCommittedWhenLaterArgumentThrows()
    {
        Compilation compilation = Compile("""
            namespace Example;
            interface IValue { int readonly Read(); }
            struct Concrete : IValue
            {
                public int Id;
                public int readonly Read() { return Id; }
                public ~Concrete() { }
            }
            void Consume<T>(T value, int second) { }
            int Throwing() { throw 7; }
            void Test()
            {
                Concrete value = Concrete();
                try { Consume<IValue>(move value, Throwing()); }
                catch (...) { int invalid = value.Id; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void NestedCatchAllDoesNotPolluteOuterExceptionalFlow()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            void Fail() { throw 7; }
            void Read(Payload value)
            {
                try
                {
                    try { throw move value.Owner; }
                    catch (...) { value.Owner = new Item(); }
                    Fail();
                }
                catch (...) { shared<Item> copy = value.Owner; }
            }
            """);

        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void NestedExactTypedCatchPreservesDefiniteAssignmentForOuterThrow()
    {
        Compilation compilation = Compile("""
            namespace Example;
            void Fail() { throw 7; }
            void Read()
            {
                int assigned;
                try
                {
                    try { throw 1; }
                    catch (readonly int& error) { assigned = error; }
                    Fail();
                }
                catch (...) { int copy = assigned; }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void NestedRethrowPropagatesExceptionalMoveState()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            void Read(Payload value)
            {
                try
                {
                    try { throw move value.Owner; }
                    catch (...) { throw; }
                }
                catch (...) { shared<Item> copy = value.Owner; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void TypedRethrowPreservesDynamicExceptionTypeFlow()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Base { }
            struct Derived : Base { }
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            void Read(Payload value)
            {
                try
                {
                    try { throw Derived(); }
                    catch (readonly Base& error)
                    {
                        shared<Item> consumed = move value.Owner;
                        throw;
                    }
                }
                catch (readonly Derived& error) { shared<Item> invalid = value.Owner; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void FullExpressionTemporaryDestructorUsesFinalFlowState()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Resource { public ~Resource() { throw 7; } }
            struct Payload { public shared<Resource> Owner; }
            shared<Resource> Make() { return new Resource(); }
            void Read(Payload source, shared<Resource> dead)
            {
                shared<Resource> consumed = move dead;
                try { bool equal = Make() == (dead = move source.Owner); }
                catch (...) { shared<Resource> invalid = source.Owner; }
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void NewCatchAndFinallyExceptionsPropagatePostHandlerState()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            void FromCatch(Payload value)
            {
                try
                {
                    try { throw move value.Owner; }
                    catch (...)
                    {
                        value.Owner = new Item();
                        throw 7;
                    }
                }
                catch (...) { shared<Item> copy = value.Owner; }
            }
            void FromFinally(Payload value)
            {
                try
                {
                    try { throw move value.Owner; }
                    catch (...) { value.Owner = new Item(); }
                    finally { throw 7; }
                }
                catch (...) { shared<Item> copy = value.Owner; }
            }
            """);

        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void FinallyExceptionalInputsDoNotPolluteNormalPostTryState()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct Item { }
            struct Payload { public shared<Item> Owner; }
            void Fail() { throw 7; }
            void Read(Payload value)
            {
                try
                {
                    Fail();
                    shared<Item> moved = move value.Owner;
                }
                finally { }
                shared<Item> invalid = value.Owner;
            }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UseAfterMove);
    }

    [Fact]
    public void BinderRejectsTransitivelyFrameBoundExceptionStorage()
    {
        Compilation compilation = Compile("""
            namespace Example;
            struct UnsafeError { public int* Borrowed; }
            void Fail(UnsafeError value) { throw value; }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.InvalidThrownType);
    }

    [Fact]
    public void ThrowOnlyTryFinallySatisfiesFunctionTermination()
    {
        Compilation compilation = Compile("""
            namespace Example;
            int Fail()
            {
                try { throw 7; }
                finally { }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void XelibRoundTripsExceptionStatementsWithoutSource()
    {
        Compilation library = Compile("""
            namespace Errors;
            public struct LibraryError { public int Code; }
            public int Fail()
            {
                try { throw LibraryError(); }
                catch (readonly LibraryError& error) { throw; }
                finally { }
            }
            """);
        Assert.Empty(library.Diagnostics);

        byte[] image = XelibWriter.Write(library, new XelibWriteOptions("Errors"));
        LibraryCompilationReference reference = XelibReader.Read(image);
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("using Errors; namespace App; int Main() { return Fail(); }", "app.xe"));

        Assert.Empty(consumer.Diagnostics);
        BoundFunction fail = Assert.Single(consumer.GetStaticImplementationFunctions(), f => f.Symbol.Name == "Fail");
        var statement = Assert.IsType<BoundTryStatement>(Assert.Single(fail.Body.Statements));
        Assert.IsType<BoundThrowStatement>(Assert.Single(statement.Body.Statements));
        Assert.NotNull(statement.FinallyBody);
    }

    [Fact]
    public void XelibGenericSpecializationRevalidatesThrowableStorage()
    {
        Compilation library = Compile("""
            namespace Errors;
            public struct LibraryError<T> { public T Value; }
            public void Fail<T>() { throw LibraryError<T>(); }
            """);
        Assert.Empty(library.Diagnostics);

        byte[] image = XelibWriter.Write(library, new XelibWriteOptions("Errors"));
        LibraryCompilationReference reference = XelibReader.Read(image);
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("using Errors; namespace App; void Main() { Fail<int*>(); }", "app.xe"));

        Assert.Contains(consumer.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.InvalidThrownType);
    }

    [Fact]
    public void XelibGenericSpecializationRevalidatesNestedDestructorAccessibility()
    {
        Compilation library = Compile("""
            namespace Errors;
            public struct LibraryError<T> { public T Value; }
            public void Fail<T>() { throw LibraryError<T>(); }
            """);
        Assert.Empty(library.Diagnostics);

        byte[] image = XelibWriter.Write(library, new XelibWriteOptions("Errors"));
        LibraryCompilationReference reference = XelibReader.Read(image);
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference],
            SourceText.From("""
                using Errors;
                namespace App;
                struct PrivateResource { private ~PrivateResource() { } }
                void Main() { Fail<PrivateResource>(); }
                """, "app.xe"));

        Assert.Contains(consumer.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.InaccessibleSymbol);
    }

    [Fact]
    public void LlvmLowersThrowAndTypedCatchToNativeEh()
    {
        Compilation compilation = Compile("""
            namespace Example;
            int Main()
            {
                try { throw 42; }
                catch (readonly int& value) { return value; }
            }
            """);
        Assert.Empty(compilation.Diagnostics);

        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();
        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, target);

        Assert.Contains("invoke void @__xenon_eh_throw", ir, StringComparison.Ordinal);
        Assert.Contains(OperatingSystem.IsWindows() ? "catchswitch" : "landingpad", ir,
            StringComparison.Ordinal);
        Assert.Contains("@__xenon_eh_matches", ir, StringComparison.Ordinal);
    }

    [Fact]
    public void LlvmTerminatesExceptionsAtSimpleCAbiExports()
    {
        Compilation compilation = Compile("""
            namespace Example;
            export int Boundary() { throw 42; }
            int Main() { return 0; }
            """);
        Assert.Empty(compilation.Diagnostics);

        string ir = new LlvmIrGenerator().GenerateForTarget(compilation, LlvmTargetOptions.CreateHost());

        Assert.Contains("@Example_Boundary()", ir, StringComparison.Ordinal);
        Assert.Contains("exception.terminate", ir, StringComparison.Ordinal);
        Assert.Contains("@__xenon_eh_terminate", ir, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LlvmNativeReferenceKind.Static)]
    [InlineData(LlvmNativeReferenceKind.Shared)]
    public void NativeDependencyExceptionCapabilityEnablesCallerCleanupAndExportGuard(
        LlvmNativeReferenceKind referenceKind)
    {
        Compilation library = Compile("""
            namespace Library;
            public int Fail() { throw 42; }
            """);
        Compilation application = Compilation.Create(
            new CompilationOptions(),
            [new SourceCompilationReference(library)],
            SourceText.From("""
                using Library;
                namespace Application;
                struct Guard { public ~Guard() { } }
                export int Boundary()
                {
                    Guard guard = Guard();
                    return Fail();
                }
                """, "application.xe"));
        Assert.Empty(application.Diagnostics);
        var options = new LlvmCodeGenerationOptions("Application",
            [new LlvmNativeReference(library, referenceKind, "Library")]);

        string ir = new LlvmIrGenerator().GenerateForTarget(
            application, LlvmTargetOptions.CreateHost(), codeGenerationOptions: options);

        Assert.Contains("invoke i32", ir, StringComparison.Ordinal);
        Assert.Contains("@Application_Boundary", ir, StringComparison.Ordinal);
        Assert.Contains("@__xenon_eh_terminate", ir, StringComparison.Ordinal);
    }

    private static BoundExpression Unwrap(BoundExpression expression) =>
        expression is BoundFullExpression full ? full.Expression : expression;

    private static Compilation Compile(string source) =>
        Compilation.Create(SourceText.From(source, "exceptions.xe"));
}
