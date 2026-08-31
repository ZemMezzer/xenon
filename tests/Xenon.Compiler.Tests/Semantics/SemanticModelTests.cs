using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class SemanticModelTests
{
    [Fact]
    public void GeneralCompletionIncludesRootNamespacesAndPreservesNamespaceTypeAmbiguity()
    {
        Compilation compilation = Create(
            "namespace Library.Tools; struct Remote {}",
            "namespace App; struct Library {} void Test() { }");
        SyntaxTree tree = compilation.SyntaxTrees[1];
        SemanticModel model = compilation.GetSemanticModel(tree);
        int position = tree.Source.Text.IndexOf("{ }", StringComparison.Ordinal) + 1;
        Symbol[] candidates = model.GetCompletionSymbols(tree, position).Where(symbol =>
            symbol.Name == "Library").ToArray();

        Assert.Contains(candidates, symbol => symbol is NamespaceSymbol);
        Assert.Contains(candidates, symbol => symbol is StructTypeSymbol);
        Assert.Contains(model.GetCompletionSymbols(tree, position), symbol =>
            symbol is NamespaceSymbol { Name: "App" });
    }

    [Fact]
    public void EditorMetadataClassifiesEnumMembersAndHidesAccessorsAndIndexerIdentifiers()
    {
        Compilation compilation = Create("""
            namespace Example;
            enum State { Ready }
            interface IValue { int Value { get; set; } int this[int index] { get; } }
            struct Box {
                public int Value { get { return 1; } set {} }
                public int this[int index] { get { return index; } }
                public void Run() {}
            }
            """);
        Symbol[] declarations = compilation.SemanticModel.GetDeclaredSymbols().ToArray();

        ConstantSymbol member = declarations.OfType<ConstantSymbol>().Single(symbol => symbol.Name == "Ready");
        Assert.Equal(EditorSymbolKind.EnumMember, EditorSymbolClassifier.GetKind(member));
        Assert.All(declarations.OfType<FunctionSymbol>().Where(function => function.IsAccessor), accessor =>
        {
            Assert.False(accessor.IsUserVisible);
            Assert.False(accessor.HasUserEditableIdentifier);
            Assert.False(EditorSymbolClassifier.IsEditorVisible(accessor));
        });
        Assert.All(declarations.Where(symbol => symbol is IndexerSymbol or InterfaceIndexerSymbol),
            indexer => Assert.False(indexer.HasUserEditableIdentifier));
        Assert.True(declarations.OfType<PropertySymbol>().Single().HasUserEditableIdentifier);
        Assert.True(declarations.OfType<FunctionSymbol>().Single(function => function.Name == "Run")
            .HasUserEditableIdentifier);
    }

    [Fact]
    public void CompletionSymbolsRespectCallableContextInheritanceAndUserVisibility()
    {
        const string source = """
            namespace Example;
            struct Base {
                public int Inherited;
                int Hidden;
                public void readonly ReadOnly() {}
                public void Mutate() {}
            }
            struct Derived : Base {
                int OwnPrivate;
                public Derived() {}
                ~Derived() {}
                public static void Static() { }
                public void Instance(int parameter) { int local = 0; local; }
                public void readonly Read() { }
            }
            """;
        Compilation compilation = Create(source);
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);

        Symbol[] instanceSymbols = model.GetCompletionSymbols(source.IndexOf("local;", StringComparison.Ordinal)).ToArray();
        string[] instance = instanceSymbols.Select(symbol => symbol.Name).ToArray();
        Assert.Contains("local", instance);
        Assert.Contains("parameter", instance);
        Assert.Contains("OwnPrivate", instance);
        Assert.Contains("Inherited", instance);
        Assert.Contains("Mutate", instance);
        Assert.Contains("ReadOnly", instance);
        Assert.Contains("Static", instance);
        Assert.DoesNotContain("Hidden", instance);
        Assert.DoesNotContain(instanceSymbols, symbol => symbol is FunctionSymbol
            { FunctionKind: FunctionKind.Constructor or FunctionKind.Destructor or FunctionKind.InstanceInitializer });
        Assert.DoesNotContain(instanceSymbols, symbol => !symbol.IsUserVisible);

        int staticPosition = source.IndexOf("Static() {", StringComparison.Ordinal) + "Static() {".Length;
        string[] staticSymbols = model.GetCompletionSymbols(staticPosition).Select(symbol => symbol.Name).ToArray();
        Assert.Contains("Static", staticSymbols);
        Assert.DoesNotContain("OwnPrivate", staticSymbols);
        Assert.DoesNotContain("Inherited", staticSymbols);
        Assert.DoesNotContain("Mutate", staticSymbols);

        int readonlyPosition = source.IndexOf("Read() {", StringComparison.Ordinal) + "Read() {".Length;
        string[] readonlySymbols = model.GetCompletionSymbols(readonlyPosition).Select(symbol => symbol.Name).ToArray();
        Assert.Contains("ReadOnly", readonlySymbols);
        Assert.DoesNotContain("Mutate", readonlySymbols);
    }

    [Fact]
    public void NamespaceCompletionIsSemanticNestedAndDetectsTypeAmbiguity()
    {
        Compilation nested = Create(
            "namespace Game.Core; public void Run() {} struct Item {}",
            "namespace Game; struct Root {}",
            "namespace App; void Test() { Game.Core. }");
        SyntaxTree nestedTree = nested.SyntaxTrees[2];
        SemanticModel nestedModel = nested.GetSemanticModel(nestedTree);
        MemberAccessExpressionSyntax access = SyntaxNavigator.DescendantNodesAndSelf(nestedTree.Root)
            .OfType<MemberAccessExpressionSyntax>().Single(member => member.MemberToken.IsMissing);
        CompletionReceiverInfo receiver = nestedModel.GetCompletionReceiver(access.Receiver);
        Assert.Equal(CompletionReceiverKind.Namespace, receiver.Kind);
        Assert.Equal("Game.Core", receiver.Namespace!.FullName);
        Assert.Contains(nestedModel.GetCompletionSymbols(access, nestedTree.Source.Length), symbol => symbol.Name == "Run");
        Assert.Contains(nestedModel.GetCompletionSymbols(access, nestedTree.Source.Length), symbol => symbol.Name == "Item");

        Compilation root = Create(
            "namespace Game.Core; struct Item {}",
            "namespace App; void Test() { Game. }");
        SyntaxTree rootTree = root.SyntaxTrees[1];
        SemanticModel rootModel = root.GetSemanticModel(rootTree);
        MemberAccessExpressionSyntax rootAccess = SyntaxNavigator.DescendantNodesAndSelf(rootTree.Root)
            .OfType<MemberAccessExpressionSyntax>().Single(member => member.MemberToken.IsMissing);
        Assert.Contains(rootModel.GetCompletionSymbols(rootAccess, rootTree.Source.Length), symbol => symbol.Name == "Core");

        Compilation ambiguous = Create(
            "namespace Game.Core; struct Item {}",
            "namespace App; struct Game {} void Test() { Game. }");
        SyntaxTree ambiguousTree = ambiguous.SyntaxTrees[1];
        SemanticModel ambiguousModel = ambiguous.GetSemanticModel(ambiguousTree);
        MemberAccessExpressionSyntax ambiguousAccess = SyntaxNavigator.DescendantNodesAndSelf(ambiguousTree.Root)
            .OfType<MemberAccessExpressionSyntax>().Single(member => member.MemberToken.IsMissing);
        Assert.Equal(CompletionReceiverKind.Ambiguous,
            ambiguousModel.GetCompletionReceiver(ambiguousAccess.Receiver).Kind);
        Assert.Empty(ambiguousModel.GetCompletionSymbols(ambiguousAccess, ambiguousTree.Source.Length));
    }

    [Fact]
    public void RenameConflictsUseExactDeclarationScopeAndContainer()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct First { int A; int B; }
            struct Second {}
            void Test(int left, int right) {
                int a = 0;
                int b = 1;
                { int shadow = 2; }
            }
            """);
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        Symbol[] declarations = model.GetDeclaredSymbols().ToArray();
        Assert.True(model.HasRenameConflict(declarations.OfType<LocalVariableSymbol>().Single(symbol => symbol.Name == "a"), "b"));
        Assert.False(model.HasRenameConflict(declarations.OfType<LocalVariableSymbol>().Single(symbol => symbol.Name == "a"), "shadow"));
        Assert.True(model.HasRenameConflict(declarations.OfType<ParameterSymbol>().Single(symbol => symbol.Name == "left"), "right"));
        Assert.True(model.HasRenameConflict(declarations.OfType<FieldSymbol>().Single(symbol => symbol.Name == "A"), "B"));
        Assert.True(model.HasRenameConflict(declarations.OfType<StructTypeSymbol>().Single(symbol => symbol.Name == "First"), "Second"));
    }

    [Fact]
    public void AssociatedTypeAndDefinitionMetadataDistinguishDeclarations()
    {
        Compilation compilation = Create("""
            namespace Example;
            extern void Native();
            interface IValue { void Read(); }
            struct Value {
                public int Field;
                public Value() {}
                public void Method(int parameter) { int local = 0; }
            }
            """);
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        Symbol[] declarations = model.GetDeclaredSymbols().ToArray();
        FunctionSymbol constructor = declarations.OfType<FunctionSymbol>()
            .Single(symbol => symbol.FunctionKind == FunctionKind.Constructor);
        Assert.Equal("Value", SemanticModel.GetAssociatedDeclaredType(constructor)!.Name);
        Assert.False(declarations.OfType<FunctionSymbol>().Single(symbol => symbol.Name == "Native").IsDefinition);
        Assert.False(declarations.OfType<FunctionSymbol>().Single(symbol => symbol.Name == "Read").IsDefinition);
        Assert.True(declarations.OfType<FunctionSymbol>().Single(symbol => symbol.Name == "Method").IsDefinition);
        Assert.True(declarations.OfType<StructTypeSymbol>().Single().IsDefinition);
        Assert.True(declarations.OfType<FieldSymbol>().Single().IsDefinition);
        Assert.False(declarations.OfType<ParameterSymbol>().Single().IsDefinition);
        Assert.True(declarations.OfType<LocalVariableSymbol>().Single().IsDefinition);
    }

    [Fact]
    public void DeclaredTypesInSignaturesAreResolvedSymbolReferences()
    {
        Compilation compilation = Create(
            "namespace Example; struct Player {} Player Create(Player value) { return value; }");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax function = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();

        Assert.IsType<StructTypeSymbol>(model.GetSymbolInfo(function.ReturnType).Symbol);
        Assert.IsType<StructTypeSymbol>(model.GetSymbolInfo(function.Parameters[0].Type).Symbol);
        Assert.Equal(2, model.GetResolvedReferences().Count(reference =>
            reference.Kind == ResolvedReferenceKind.Type && reference.Symbol.Name == "Player"));
    }

    [Fact]
    public void SyntaxErrors_DoNotEraseValidDeclarationsInSameOrOtherFiles()
    {
        Compilation compilation = Create(
            "namespace Example; void Broken(int value) { value + } struct Survives { public int Value; }",
            "namespace Example; int Good() { return 42; }");

        Assert.True(compilation.HasErrors);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        Assert.Contains(ns.Types, type => type.Name == "Survives");
        Assert.Contains(ns.Functions, function => function.Name == "Good");
        Assert.Contains(compilation.SemanticModel.Functions, function => function.Symbol.Name == "Good");
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnexpectedToken);
    }

    [Fact]
    public void IncompleteMemberAccess_PreservesReceiverTypeMembersAndScope()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Player { public int Score; public int Get() { return Score; } }
            void Test(Player player) { player. }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax function = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var statement = Assert.IsType<ExpressionStatementSyntax>(Assert.Single(function.Body!.Statements));
        var access = Assert.IsType<MemberAccessExpressionSyntax>(statement.Expression);

        Assert.True(access.MemberToken.IsMissing);
        Assert.IsType<StructTypeSymbol>(model.GetTypeInfo(access.Receiver).Type);
        Assert.Equal(CandidateReason.Incomplete, model.GetSymbolInfo(access).CandidateReason);
        Assert.Contains(model.LookupSymbols(access.MemberToken.Location.Span.Start), symbol => symbol.Name == "player");
        TypeSymbol receiverType = model.GetTypeInfo(access.Receiver).Type;
        ImmutableArray<Symbol> members = model.LookupMembers(receiverType, access.MemberToken.Location.Span.Start);
        Assert.Contains(members, symbol => symbol.Name == "Score");
        Assert.Contains(members, symbol => symbol.Name == "Get");
    }

    [Fact]
    public void IncompleteCall_PreservesCallableAndCompletedArguments()
    {
        Compilation compilation = Create("""
            namespace Example;
            int Foo(int value) { return value; }
            void Test(int value) { Foo(value, }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax function = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Last();
        var statement = Assert.IsType<ExpressionStatementSyntax>(Assert.Single(function.Body!.Statements));
        var call = Assert.IsType<CallExpressionSyntax>(statement.Expression);

        Assert.True(call.CloseParenthesisToken.IsMissing);
        Assert.Equal("Foo", Assert.IsType<FunctionSymbol>(model.GetSymbolInfo(call).Symbol).Name);
        Assert.Equal("Foo", Assert.IsType<FunctionSymbol>(model.GetSymbolInfo(call.Target).Symbol).Name);
        Assert.Equal(CandidateReason.Incomplete, model.GetSymbolInfo(call).CandidateReason);
        Assert.Single(model.GetSymbolInfo(call).CandidateSymbols);
        Assert.DoesNotContain(model.GetDiagnostics(), diagnostic => diagnostic.Id == DiagnosticIds.WrongArity);
        Assert.Same(BuiltinTypes.Int, model.GetTypeInfo(call.Arguments[0]).Type);
        Assert.IsType<ErrorTypeSymbol>(model.GetTypeInfo(call.Arguments[^1]).Type);
    }

    [Theory]
    [InlineData("void Free(int value) {} void Test() { Free(")]
    [InlineData("struct Value { public void Method(int value) {} } void Test(Value value) { value.Method(")]
    [InlineData("struct Value { public static void Method(int value) {} } void Test() { Value.Method(")]
    [InlineData("interface IValue { void Method(int value); } void Test(IValue& value) { value.Method(")]
    [InlineData("struct Value { public void Method(int value) {} public void readonly Method(int value) {} } void Test(readonly Value value) { value.Method(")]
    public void IncompleteCallablePrefixPreservesCandidateWithoutPrematureArityDiagnostic(string members)
    {
        Compilation compilation = Create("namespace Example; " + members);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Last();
        var call = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body!.Statements[0]).Expression);

        SymbolInfo info = model.GetSymbolInfo(call);
        Assert.Equal(CandidateReason.Incomplete, info.CandidateReason);
        Assert.NotEmpty(info.CandidateSymbols);
        Assert.DoesNotContain(model.GetDiagnostics(), diagnostic => diagnostic.Id == DiagnosticIds.WrongArity);
        Assert.DoesNotContain(model.GetDiagnostics(), diagnostic => diagnostic.Id == DiagnosticIds.NoMatchingCandidate);
    }

    [Fact]
    public void IncompleteQualifiedFunctionPrefixPreservesCandidateWithoutPrematureArityDiagnostic()
    {
        Compilation compilation = Create(
            "namespace Library; public void Read(int value) {}",
            "namespace App; void Test() { Library.Read(");
        SyntaxTree tree = compilation.SyntaxTrees[1];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var call = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body!.Statements[0]).Expression);

        SymbolInfo info = model.GetSymbolInfo(call);
        Assert.Equal(CandidateReason.Incomplete, info.CandidateReason);
        Assert.Equal("Library.Read", Assert.IsType<FunctionSymbol>(Assert.Single(info.CandidateSymbols)).FullName);
        Assert.DoesNotContain(model.GetDiagnostics(), diagnostic => diagnostic.Id == DiagnosticIds.WrongArity);
        Assert.DoesNotContain(model.GetDiagnostics(), diagnostic => diagnostic.Id == DiagnosticIds.NoMatchingCandidate);
    }

    [Fact]
    public void IncompleteConstructorPrefixDoesNotEmitFinalConstructorErrors()
    {
        Compilation compilation = Create("namespace Example; struct Box { public Box(int value) {} } void Test() { Box(");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var call = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body!.Statements[0]).Expression);

        SymbolInfo info = model.GetSymbolInfo(call);
        Assert.Equal(CandidateReason.Incomplete, info.CandidateReason);
        Assert.Single(info.CandidateSymbols);
        Assert.DoesNotContain(model.GetDiagnostics(), diagnostic => diagnostic.Id is
            DiagnosticIds.WrongArity or DiagnosticIds.NoMatchingCandidate or DiagnosticIds.MissingConstructor);
    }

    [Fact]
    public void IncompleteCallStillReportsAProvenIncompatibleArgument()
    {
        Compilation compilation = Create("namespace Example; void Foo(int value) {} void Test() { Foo(null,");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Last();
        var call = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body!.Statements[0]).Expression);

        Assert.Equal(CandidateReason.NotInvocable, model.GetSymbolInfo(call).CandidateReason);
        Assert.Contains(model.GetDiagnostics(), diagnostic => diagnostic.Id == DiagnosticIds.TypeMismatch);
        Assert.DoesNotContain(model.GetDiagnostics(), diagnostic => diagnostic.Id == DiagnosticIds.WrongArity);
    }

    [Fact]
    public void IncompleteCallStillReportsAProvenExcessArgument()
    {
        Compilation compilation = Create("namespace Example; void Foo(int value) {} void Test() { Foo(1, 2,");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Last();
        var call = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body!.Statements[0]).Expression);

        Assert.Equal(CandidateReason.WrongArity, model.GetSymbolInfo(call).CandidateReason);
        Assert.Contains(model.GetDiagnostics(), diagnostic => diagnostic.Id == DiagnosticIds.WrongArity);
    }

    [Fact]
    public void FailedCalls_ExposeCandidatesAndPreciseReasons()
    {
        Compilation compilation = Create("""
            namespace Example;
            void Foo(int value) {}
            void Test() { Foo(); 1(); }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Last();
        var wrongArity = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body!.Statements[0]).Expression);
        var notInvocable = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[1]).Expression);

        SymbolInfo wrongArityInfo = model.GetSymbolInfo(wrongArity);
        Assert.Equal(CandidateReason.WrongArity, wrongArityInfo.CandidateReason);
        Assert.Single(wrongArityInfo.CandidateSymbols);
        Assert.Equal("Foo", wrongArityInfo.CandidateSymbols[0].Name);
        Assert.Equal(CandidateReason.NotInvocable, model.GetSymbolInfo(notInvocable).CandidateReason);
    }

    [Fact]
    public void AmbiguousConstructorsAndIndexersExposeAllCandidates()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box {
                public Box(int* value) {}
                public Box(float* value) {}
                public int this[int* value] { get { return 1; } }
                public int this[float* value] { get { return 2; } }
            }
            void Test(Box box) {
                Box created = Box(null);
                int item = box[null];
            }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var construction = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body!.Statements[0]).Initializer);
        var index = Assert.IsType<IndexExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body.Statements[1]).Initializer);

        SymbolInfo constructors = model.GetSymbolInfo(construction);
        Assert.Equal(CandidateReason.Ambiguous, constructors.CandidateReason);
        Assert.Equal(2, constructors.CandidateSymbols.Length);
        Assert.All(constructors.CandidateSymbols, symbol => Assert.Equal(SymbolKind.Function, symbol.Kind));

        SymbolInfo indexers = model.GetSymbolInfo(index);
        Assert.Equal(CandidateReason.Ambiguous, indexers.CandidateReason);
        Assert.Equal(2, indexers.CandidateSymbols.Length);
        Assert.All(indexers.CandidateSymbols, symbol => Assert.IsType<IndexerSymbol>(symbol));
    }

    [Fact]
    public void InterfaceOverloadsAndPrivateMembersExposeCandidateFailures()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface IReader {
                int Read(int* value);
                int Read(float* value);
            }
            struct Hidden {
                Hidden(int value) {}
                int Read() { return 0; }
            }
            void Test(IReader& reader, Hidden hidden) {
                int result = reader.Read(null);
                Hidden created = Hidden(1);
                hidden.Read();
            }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var interfaceCall = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body!.Statements[0]).Initializer);
        var privateConstructor = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body.Statements[1]).Initializer);
        var privateMethod = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[2]).Expression);

        SymbolInfo interfaceInfo = model.GetSymbolInfo(interfaceCall);
        Assert.Equal(CandidateReason.Ambiguous, interfaceInfo.CandidateReason);
        Assert.Equal(2, interfaceInfo.CandidateSymbols.Length);

        Assert.Equal(CandidateReason.Inaccessible, model.GetSymbolInfo(privateConstructor).CandidateReason);
        Assert.Single(model.GetSymbolInfo(privateConstructor).CandidateSymbols);
        Assert.Null(model.GetSymbolInfo(privateConstructor).Symbol);
        Assert.Equal(CandidateReason.Inaccessible, model.GetSymbolInfo(privateMethod).CandidateReason);
        Assert.Single(model.GetSymbolInfo(privateMethod).CandidateSymbols);
        Assert.Null(model.GetSymbolInfo(privateMethod).Symbol);
    }

    [Fact]
    public void IncompleteBinaryExpression_PreservesLeftSymbolAndType()
    {
        Compilation compilation = Create("namespace Example; void Test(int value) { value + }");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        var function = Assert.IsType<FunctionDeclarationSyntax>(Assert.Single(tree.Root.Members));
        var statement = Assert.IsType<ExpressionStatementSyntax>(Assert.Single(function.Body!.Statements));
        var binary = Assert.IsType<BinaryExpressionSyntax>(statement.Expression);

        Assert.Equal("value", Assert.IsType<ParameterSymbol>(model.GetSymbolInfo(binary.Left).Symbol).Name);
        Assert.Same(BuiltinTypes.Int, model.GetTypeInfo(binary.Left).Type);
        Assert.IsType<ErrorTypeSymbol>(model.GetTypeInfo(binary.Right).Type);
        Assert.IsType<ErrorTypeSymbol>(model.GetTypeInfo(binary).Type);
    }

    [Theory]
    [InlineData("if (")]
    [InlineData("while (")]
    [InlineData("return value +")]
    public void IncompleteStatements_RemainAnalyzable(string statement)
    {
        Compilation compilation = Create($"namespace Example; void Test(int value) {{ {statement}");
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        Assert.True(compilation.HasErrors);
        Assert.Contains(model.LookupSymbols(compilation.SyntaxTrees[0].Source.Length), symbol => symbol.Name == "value");
        Assert.NotEmpty(model.GetDiagnostics());
    }

    [Fact]
    public void IncompleteTypeSyntax_ProducesStableErrorType()
    {
        Compilation compilation = Create("namespace Example; struct Foo {} void Test(Foo< value)");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax function = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        TypeSyntax type = function.Parameters[0].Type;

        Assert.IsType<ErrorTypeSymbol>(model.GetTypeInfo(type).Type);
        Assert.Contains(model.GetDiagnostics(), diagnostic => diagnostic.Message.Contains("generic type arguments", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclaredSymbol_MapsTypeMemberParameterAndLocal()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box { public int Value; public int Read(int delta) { int local = Value + delta; return local; } }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        var type = Assert.IsType<StructDeclarationSyntax>(Assert.Single(tree.Root.Members));
        FieldDeclarationSyntax field = Assert.Single(type.Fields);
        MethodDeclarationSyntax method = Assert.Single(type.Methods);
        var local = Assert.IsType<VariableDeclarationStatementSyntax>(method.Body!.Statements[0]);

        Assert.IsType<StructTypeSymbol>(model.GetDeclaredSymbol(type));
        Assert.IsType<FieldSymbol>(model.GetDeclaredSymbol(field));
        Assert.IsType<FunctionSymbol>(model.GetDeclaredSymbol(method));
        Assert.IsType<ParameterSymbol>(model.GetDeclaredSymbol(method.Parameters[0]));
        Assert.IsType<LocalVariableSymbol>(model.GetDeclaredSymbol(local));
        Assert.All(new SyntaxNode[] { type, field, method, method.Parameters[0], local }, node =>
            Assert.NotEmpty(model.GetDeclaredSymbol(node)!.Locations));
    }

    [Fact]
    public void ReferencesMapToPropertiesIndexersAndConstantsInsteadOfAccessors()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box {
                const int Answer = 42;
                public int Value { get { return 1; } set {} }
                public int this[int index] { get { return index; } set {} }
            }
            void Test(Box box) {
                int constant = Box.Answer;
                int read = box.Value;
                box.Value = 1;
                box.Value += 1;
                int indexed = box[0];
                box[0] = 1;
                box[0] += 1;
            }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();

        var constant = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body!.Statements[0]).Initializer);
        var propertyRead = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body.Statements[1]).Initializer);
        var propertyWrite = Assert.IsType<AssignmentExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[2]).Expression);
        var propertyCompound = Assert.IsType<AssignmentExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[3]).Expression);
        var indexRead = Assert.IsType<IndexExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body.Statements[4]).Initializer);
        var indexWrite = Assert.IsType<AssignmentExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[5]).Expression);
        var indexCompound = Assert.IsType<AssignmentExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[6]).Expression);

        Assert.IsType<ConstantSymbol>(model.GetSymbolInfo(constant).Symbol);
        Assert.IsType<PropertySymbol>(model.GetSymbolInfo(propertyRead).Symbol);
        Assert.IsType<PropertySymbol>(model.GetSymbolInfo(propertyWrite.Target).Symbol);
        Assert.IsType<PropertySymbol>(model.GetSymbolInfo(propertyWrite).Symbol);
        Assert.IsType<PropertySymbol>(model.GetSymbolInfo(propertyCompound.Target).Symbol);
        Assert.IsType<PropertySymbol>(model.GetSymbolInfo(propertyCompound).Symbol);
        Assert.IsType<IndexerSymbol>(model.GetSymbolInfo(indexRead).Symbol);
        Assert.IsType<IndexerSymbol>(model.GetSymbolInfo(indexWrite.Target).Symbol);
        Assert.IsType<IndexerSymbol>(model.GetSymbolInfo(indexWrite).Symbol);
        Assert.IsType<IndexerSymbol>(model.GetSymbolInfo(indexCompound.Target).Symbol);
        Assert.IsType<IndexerSymbol>(model.GetSymbolInfo(indexCompound).Symbol);
    }

    [Fact]
    public void ContextualConversionsAreRecordedForInitializersAssignmentsReturnsAndArguments()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box {
                public int* Field = null;
                public int* Value { get { return null; } set {} }
                public int* this[int index] { get { return null; } set {} }
            }
            int* Identity(int* value) { return null; }
            void Test(Box box) {
                int* local = null;
                local = null;
                box.Value = null;
                box[0] = null;
                Identity(null);
            }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        var box = tree.Root.Members.OfType<StructDeclarationSyntax>().Single();
        FunctionDeclarationSyntax identity = tree.Root.Members.OfType<FunctionDeclarationSyntax>().First();
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Last();
        ExpressionSyntax[] nulls =
        [
            box.Fields.Single().Initializer!,
            box.Properties.Single().Getter!.Body!.Statements.OfType<ReturnStatementSyntax>().Single().Expression!,
            box.Indexers.Single().Getter!.Body!.Statements.OfType<ReturnStatementSyntax>().Single().Expression!,
            Assert.IsType<ReturnStatementSyntax>(identity.Body!.Statements[0]).Expression!,
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body!.Statements[0]).Initializer!,
            Assert.IsType<AssignmentExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[1]).Expression).Expression,
            Assert.IsType<AssignmentExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[2]).Expression).Expression,
            Assert.IsType<AssignmentExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[3]).Expression).Expression,
            Assert.IsType<CallExpressionSyntax>(Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[4]).Expression).Arguments[0],
        ];

        Assert.All(nulls, expression =>
        {
            TypeInfo info = model.GetTypeInfo(expression);
            Assert.Equal("<null>", info.Type.Name);
            Assert.IsType<PointerTypeSymbol>(info.ConvertedType);
        });
    }

    [Fact]
    public void ArrayPseudoMembersAreStableCompilerOwnedSymbolsAndExpressionLookupUsesReceiverState()
    {
        Compilation compilation = Create("namespace Example; void Test(readonly int[] values) { values. }");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var access = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body!.Statements[0]).Expression);
        int position = access.MemberToken.Location.Span.Start;

        ReceiverInfo receiver = Assert.IsType<ReceiverInfo>(model.GetReceiverInfo(access.Receiver));
        Assert.True(receiver.IsReadonly);
        Assert.False(receiver.IsWritable);
        ImmutableArray<Symbol> first = model.LookupMembers(access.Receiver, position);
        ImmutableArray<Symbol> second = model.LookupMembers(access.Receiver, position);
        Assert.Equal(new[] { "GetLength", "Length", "Rank" }, first.Select(symbol => symbol.Name));
        Assert.Empty(model.LookupMembers(receiver.Type, position,
            new MemberLookupOptions(MemberAccessKind.Static)));
        Assert.All(first, symbol =>
        {
            var synthetic = Assert.IsType<SyntheticMemberSymbol>(symbol);
            Assert.Empty(synthetic.Locations);
            Assert.Empty(synthetic.DeclaringSyntaxReferences);
            Assert.Same(synthetic, second.Single(candidate => candidate.Name == synthetic.Name));
            Assert.False(string.IsNullOrWhiteSpace(synthetic.ToDisplayString(SymbolDisplayFormat.Signature)));
        });
    }

    [Fact]
    public void CompletedArrayMemberReferencesUseTheSameSyntheticSymbolsAsCompletion()
    {
        Compilation compilation = Create("""
            namespace Example;
            void Test() {
                int[] values = new int[4];
                int length = values.Length;
                int rank = values.Rank;
                int dimension = values.GetLength(0);
            }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var length = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body!.Statements[1]).Initializer);
        var rank = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body.Statements[2]).Initializer);
        var getLengthCall = Assert.IsType<CallExpressionSyntax>(
            Assert.IsType<VariableDeclarationStatementSyntax>(test.Body.Statements[3]).Initializer);
        var getLength = Assert.IsType<MemberAccessExpressionSyntax>(getLengthCall.Target);

        AssertSameSyntheticMember(length, "Length");
        AssertSameSyntheticMember(rank, "Rank");
        AssertSameSyntheticMember(getLength, "GetLength");
        Assert.Same(model.GetSymbolInfo(getLength).Symbol, model.GetSymbolInfo(getLengthCall).Symbol);

        void AssertSameSyntheticMember(MemberAccessExpressionSyntax access, string name)
        {
            Symbol completion = model.LookupMembers(access.Receiver, access.MemberToken.Location.Span.Start)
                .Single(symbol => symbol.Name == name);
            SyntheticMemberSymbol reference = Assert.IsType<SyntheticMemberSymbol>(model.GetSymbolInfo(access).Symbol);
            Assert.Same(completion, reference);
            Assert.Empty(reference.Locations);
            Assert.Empty(reference.DeclaringSyntaxReferences);
            Assert.False(string.IsNullOrWhiteSpace(reference.ToDisplayString(SymbolDisplayFormat.Signature)));
        }
    }

    [Fact]
    public void ExpressionMemberLookupFiltersStaticAndReadonlyReceiverMembers()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box {
                const int Constant = 1;
                public static int StaticField;
                public int Mutable() { return 1; }
                public int readonly Read() { return 2; }
                public int MutableValue { get { return 1; } }
                public readonly int ReadValue { get { return 2; } }
                public int this[int value] { get { return value; } }
                public readonly int this[float value] { get { return 2; } }
            }
            void Test(readonly Box value) { value.; Box.; }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax test = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var readonlyAccess = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body!.Statements[0]).Expression);
        var staticAccess = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(test.Body.Statements[1]).Expression);

        ImmutableArray<Symbol> readonlyMembers = model.LookupMembers(
            readonlyAccess.Receiver, readonlyAccess.MemberToken.Location.Span.Start);
        Assert.Contains(readonlyMembers, symbol => symbol.Name == "Read");
        Assert.Contains(readonlyMembers, symbol => symbol.Name == "ReadValue");
        Assert.Single(readonlyMembers.OfType<IndexerSymbol>());
        Assert.DoesNotContain(readonlyMembers, symbol => symbol.Name is "Mutable" or "MutableValue" or "StaticField" or "Constant");

        ReceiverInfo staticReceiver = Assert.IsType<ReceiverInfo>(model.GetReceiverInfo(staticAccess.Receiver));
        Assert.True(staticReceiver.IsStatic);
        ImmutableArray<Symbol> staticMembers = model.LookupMembers(
            staticAccess.Receiver, staticAccess.MemberToken.Location.Span.Start);
        Assert.Contains(staticMembers, symbol => symbol.Name == "Constant");
        Assert.Contains(staticMembers, symbol => symbol.Name == "StaticField");
        Assert.DoesNotContain(staticMembers, symbol => symbol.Name is "Read" or "ReadValue" or "Mutable");
    }

    [Fact]
    public void LookupSymbols_RespectsOrderScopeAndShadowing()
    {
        Compilation compilation = Create("""
            namespace Example;
            int global() { return 0; }
            void Test(int parameter) {
                int before = 1;
                { int before = 2; before; }
                int after = 3;
            }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        int innerPosition = tree.Source.Text.IndexOf("before; }", StringComparison.Ordinal);
        ImmutableArray<Symbol> inner = model.LookupSymbols(innerPosition);
        Assert.Single(inner.Where(symbol => symbol.Name == "before"));
        Assert.Contains(inner, symbol => symbol.Name == "parameter");
        Assert.DoesNotContain(inner, symbol => symbol.Name == "after");
        Assert.Contains(inner, symbol => symbol.Name == "global");
    }

    [Fact]
    public void LookupSymbols_WorksInTypeBodiesAndIncludesAliases()
    {
        Compilation compilation = Create("""
            using Number = Example.Value;
            namespace Example;
            struct Value { public int Field; }
            struct Container { public int Member; }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        int typePosition = tree.Source.Text.IndexOf("public int Member", StringComparison.Ordinal);
        ImmutableArray<Symbol> symbols = model.LookupSymbols(typePosition);
        Assert.Contains(symbols, symbol => symbol.Name == "Member");
        AliasSymbol alias = Assert.IsType<AliasSymbol>(symbols.Single(symbol => symbol.Name == "Number"));
        Assert.Equal("Value", alias.Target.Name);
    }

    [Fact]
    public void LookupSymbols_RespectsForAndSwitchSectionScopesAndHalfOpenBoundaries()
    {
        Compilation compilation = Create("""
            namespace Example;
            void Test(int value) {
                for (int i = 0; i < 2; i++) { i; }
                i;
                switch (value) {
                    case 0: int first = 1; first; break;
                    case 1: int second = 2; second; break;
                }
            }
            """);
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        FunctionDeclarationSyntax function = tree.Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var forStatement = Assert.IsType<ForStatementSyntax>(function.Body!.Statements[0]);
        var afterForStatement = Assert.IsType<ExpressionStatementSyntax>(function.Body.Statements[1]);

        int insideFor = tree.Source.Text.IndexOf("i; }", StringComparison.Ordinal);
        int afterFor = SyntaxNavigator.GetSpan(afterForStatement.Expression).Start;
        Assert.Contains(model.LookupSymbols(insideFor), symbol => symbol.Name == "i");
        Assert.DoesNotContain(model.LookupSymbols(afterFor), symbol => symbol.Name == "i");
        Assert.DoesNotContain(model.LookupSymbols(((BlockStatementSyntax)forStatement.Body).CloseBraceToken.Location.Span.End),
            symbol => symbol.Name == "i");

        int firstUse = tree.Source.Text.IndexOf("first; break", StringComparison.Ordinal);
        int secondUse = tree.Source.Text.IndexOf("second; break", StringComparison.Ordinal);
        Assert.Contains(model.LookupSymbols(firstUse), symbol => symbol.Name == "first");
        Assert.DoesNotContain(model.LookupSymbols(secondUse), symbol => symbol.Name == "first");
        Assert.Contains(model.LookupSymbols(secondUse), symbol => symbol.Name == "second");
    }

    [Fact]
    public void LookupSymbols_IncludesIncompleteScopeAtEndOfFile()
    {
        Compilation compilation = Create("namespace Example; void Test() { for (int i = 0; i < 2; i++) { i;");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);

        Assert.Contains(model.LookupSymbols(tree.Source.Length), symbol => symbol.Name == "i");
    }

    [Fact]
    public void Diagnostics_HaveStableIdsRelatedLocationsAndSpanFiltering()
    {
        Compilation compilation = Create("namespace Example; int Main() { return 1; } int Main() { return 2; }");
        SyntaxTree tree = compilation.SyntaxTrees[0];
        SemanticModel model = compilation.GetSemanticModel(tree);
        Diagnostic duplicate = Assert.Single(model.GetDiagnostics().Where(d => d.Id == DiagnosticIds.DuplicateDeclaration));
        RelatedDiagnosticLocation related = Assert.Single(duplicate.RelatedLocations);
        Assert.Equal("Main", related.Location.Source.GetText(related.Location.Span));
        Assert.Contains(duplicate, model.GetDiagnostics(duplicate.Location.Span));
        Assert.All(model.GetDiagnostics(), diagnostic => Assert.Matches("^XE[0-9]{4}$", diagnostic.Id));
    }

    [Fact]
    public void DiagnosticSpanFiltering_UsesHalfOpenIntervalsAndHandlesMissingTokens()
    {
        Compilation duplicateCompilation = Create("namespace Example; int Main() { return 1; } int Main() { return 2; }");
        SemanticModel duplicateModel = duplicateCompilation.GetSemanticModel(duplicateCompilation.SyntaxTrees[0]);
        Diagnostic duplicate = Assert.Single(duplicateModel.GetDiagnostics()
            .Where(diagnostic => diagnostic.Id == DiagnosticIds.DuplicateDeclaration));

        Assert.Contains(duplicate, duplicateModel.GetDiagnostics(new TextSpan(duplicate.Location.Span.Start, 0)));
        Assert.DoesNotContain(duplicate, duplicateModel.GetDiagnostics(new TextSpan(duplicate.Location.Span.End, 0)));
        Assert.DoesNotContain(duplicate, duplicateModel.GetDiagnostics(new TextSpan(duplicate.Location.Span.End, 1)));

        Compilation incomplete = Create("namespace Example; void Test() { return");
        SyntaxTree tree = incomplete.SyntaxTrees[0];
        SemanticModel model = incomplete.GetSemanticModel(tree);
        Assert.Contains(model.GetDiagnostics(new TextSpan(tree.Source.Length, 0)),
            diagnostic => diagnostic.Location.Span.Length == 0 && diagnostic.Location.Span.Start == tree.Source.Length);
    }

    [Fact]
    public void RepresentativeSemanticFailuresHaveSpecificStableIds()
    {
        Compilation compilation = Create("""
            namespace Example;
            abstract struct Shape {}
            void Test() {
                int value;
                value;
                Shape shape;
                readonly int fixedValue = 1;
                fixedValue = 2;
                switch (1) { case 1: value = 1; case 2: break; }
            }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.DefiniteAssignment);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.AbstractInstantiation);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidAssignmentTarget);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.SwitchFallthrough);
        Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id == "XE2000");
    }

    [Fact]
    public void UnrelatedSemanticRulesHaveDistinctStableDiagnosticIds()
    {
        static string[] Example(string members) => ["namespace Example; " + members];

        (string[] Sources, string ExpectedId)[] cases =
        [
            (Example("void Test() { if (1) {} }"), DiagnosticIds.InvalidCondition),
            (Example("void Test() { break; }"), DiagnosticIds.BreakOutsideLoopOrSwitch),
            (Example("void Test() { continue; }"), DiagnosticIds.ContinueOutsideLoop),
            (Example("void Test() { int value = 1 / 0; }"), DiagnosticIds.DivisionByZero),
            (Example("void Test() { int value = 1 << -1; }"), DiagnosticIds.InvalidShift),
            (Example("void Test() { int& reference; }"), DiagnosticIds.ReferenceRequiresInitializer),
            (Example("void Test() { readonly int value; }"), DiagnosticIds.ReadonlyRequiresInitializer),
            (Example("void Test() { return 1; }"), DiagnosticIds.ReturnValueFromVoid),
            (Example("int Test() { return; }"), DiagnosticIds.MissingReturnValue),
            (Example("void Test() { int* value = cast<int*>(1); }"), DiagnosticIds.InvalidCast),
            (Example("void Test() { int value = 1[0]; }"), DiagnosticIds.TypeNotIndexable),
            (Example("struct Base {} struct Derived : Base, Base {}"), DiagnosticIds.MultipleStructBaseTypes),
            (Example("enum Value { A, A }"), DiagnosticIds.DuplicateEnumMember),
            (Example("const int A = B; const int B = A;"), DiagnosticIds.ConstantCycle),
            (Example("struct Base {} struct Derived : Base { public override void Run() {} }"), DiagnosticIds.NoCompatibleOverrideTarget),
            (Example("struct Box {} void Test(Box<int> value) {}"), DiagnosticIds.GenericTypeArgumentsNotSupported),
            (Example("void Test(void& value) {}"), DiagnosticIds.VoidReferenceElementType),
            (Example("readonly int Test() { return 1; }"), DiagnosticIds.InvalidReadonlyReturnQualifier),
            (Example("enum State { Ready } void Test() { State value = State.Missing; }"), DiagnosticIds.UnknownEnumMember),
            (Example("struct Value {} void Test(Value value) { value.Missing; }"), DiagnosticIds.MissingStructField),
            (Example("struct Value {} void Test(Value value) { value.Missing(); }"), DiagnosticIds.MissingStructMethod),
            (Example("void Test(int value) { value.Missing; }"), DiagnosticIds.InvalidMemberReceiver),
            (Example("struct Value { public int Item { get { return 1; } get { return 2; } } }"), DiagnosticIds.DuplicateGetter),
            (Example("struct Value { public int Item { } }"), DiagnosticIds.AccessorRequired),
            (Example("abstract struct Value { public abstract int Item { get { return 1; } } }"), DiagnosticIds.AbstractAccessorHasBody),
            (Example("extern void* malloc(nuint size); extern void* calloc(nuint count, nuint size);"), DiagnosticIds.ReservedNativeSymbol),
            (["namespace A; extern int Native(int value);", "namespace B; extern int Native(float value);"], DiagnosticIds.NativeSymbolCollision),
            (Example("struct Value {} extern Value Read();"), DiagnosticIds.UnsupportedNativeStructByValue),
            (Example("void Test(int[] values) { values.GetLength(); }"), DiagnosticIds.InvalidGetLengthArguments),
            (Example("void Test(int[] values) { values.GetLength(1); }"), DiagnosticIds.GetLengthDimensionOutOfRange),
            (Example("struct Pair { int Left; int Right; } void Test() { Pair value = Pair { 1 }; }"), DiagnosticIds.PositionalValueCountMismatch),
        ];

        Assert.Equal(cases.Length, cases.Select(item => item.ExpectedId).Distinct(StringComparer.Ordinal).Count());
        foreach ((string[] sources, string expectedId) in cases)
        {
            Compilation compilation = Create(sources);
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == expectedId);
            Assert.DoesNotContain(compilation.Diagnostics, diagnostic => diagnostic.Id is "XE2100" or "XE2200" or "XE2999");
        }
    }

    [Fact]
    public void FormerBroadCategoriesExposeIndependentRuleIds()
    {
        static string Example(string members) => "namespace Example; " + members;

        (string Source, string ExpectedId)[] cases =
        [
            (Example("void Test() { this; }"), DiagnosticIds.ThisOutsideInstanceMember),
            (Example("struct Base { public Base(int value) {} } struct Derived : Base { int Value; public Derived() : base(Value) {} }"), DiagnosticIds.DerivedInstanceInBaseConstructorArguments),
            (Example("struct Value { int Field; public static void Test() { Field; } }"), DiagnosticIds.StaticContextInstanceFieldAccess),
            (Example("struct Value { public int Item { get { return 1; } } public static void Test() { Item; } }"), DiagnosticIds.StaticContextInstancePropertyAccess),
            (Example("struct Value { public void Run() {} public static void Test() { Run(); } }"), DiagnosticIds.StaticContextInstanceMethodCall),
            (Example("struct Value { public static void Run() {} } void Test(Value value) { value.Run(); }"), DiagnosticIds.StaticMethodRequiresTypeReceiver),
            (Example("void Test(int[,] values) { int item = values[0]; }"), DiagnosticIds.IndexArityMismatch),
            (Example("void Test(int[] values) { int item = values[false]; }"), DiagnosticIds.IndexMustBeInteger),
            (Example("void Test(void* values) { byte item = values[0]; }"), DiagnosticIds.VoidPointerIndex),
            (Example("void Test() { int[] values = new int[-1]; }"), DiagnosticIds.ArrayLengthOutOfRange),
            (Example("void Test() { int[] values = new int[false]; }"), DiagnosticIds.ArrayLengthMustBeInteger),
            (Example("void Test() { int[,] values = new int[50000, 50000]; }"), DiagnosticIds.TotalArrayLengthOverflow),
            (Example("struct Value : Value {}"), DiagnosticIds.SelfInheritance),
            (Example("struct Value : int {}"), DiagnosticIds.InvalidBaseType),
            (Example("struct Base {} interface IValue : Base {}"), DiagnosticIds.InterfaceBaseMustBeInterface),
            (Example("struct Value { public int this[] { get { return 1; } } }"), DiagnosticIds.IndexerRequiresParameter),
            (Example("void Test(void value) {}"), DiagnosticIds.VoidParameterType),
            (Example("struct Value { public void Run(int value) {} public void Run(float value) {} }"), DiagnosticIds.MethodOverloadingNotSupported),
            (Example("struct Value { public static int Item { get { return 1; } } }"), DiagnosticIds.StaticPropertyNotSupported),
            (Example("struct Value { public static int this[int index] { get { return index; } } }"), DiagnosticIds.StaticIndexerNotSupported),
            (Example("abstract abstract struct Value {}"), DiagnosticIds.DuplicateModifier),
            (Example("struct Value { public virtual override void Run() {} }"), DiagnosticIds.ConflictingDispatchModifiers),
            (Example("struct Value { public static virtual void Run() {} }"), DiagnosticIds.StaticDispatchModifierNotAllowed),
            (Example("struct Value { static Value() {} }"), DiagnosticIds.ModifierNotAllowed),
            (Example("struct Value { public int* readonly [] Read() {} }"), DiagnosticIds.ReadonlyReturnBindingNotAllowed),
        ];

        Assert.Equal(cases.Length, cases.Select(item => item.ExpectedId).Distinct(StringComparer.Ordinal).Count());
        foreach ((string source, string expectedId) in cases)
        {
            Compilation compilation = Create(source);
            Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == expectedId);
        }
    }

    [Theory]
    [InlineData("virtual")]
    [InlineData("override")]
    [InlineData("abstract")]
    public void StaticDispatchModifierCombinationProducesSingleParserOwnedDiagnostic(string modifier)
    {
        string source = $"namespace Example; struct Value {{ public static {modifier} void Run() {{}} }}";

        Compilation compilation = Create(source);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics
            .Where(item => item.Id == DiagnosticIds.StaticDispatchModifierNotAllowed));
        Assert.Equal(modifier, source.Substring(diagnostic.Location.Span.Start, diagnostic.Location.Span.Length));
    }

    [Fact]
    public void DiagnosticCatalogIdsAreUniqueAndReportRequiresExplicitIdentity()
    {
        string[] ids = typeof(DiagnosticIds).GetFields()
            .Where(field => field.IsLiteral && !field.IsInitOnly && field.FieldType == typeof(string))
            .Select(field => Assert.IsType<string>(field.GetRawConstantValue()))
            .ToArray();

        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(ids, id => id is "XE2100" or "XE2200" or "XE2999");

        System.Reflection.MethodInfo report = typeof(DiagnosticBag).GetMethod(nameof(DiagnosticBag.Report))
            ?? throw new InvalidOperationException("DiagnosticBag.Report was not found");
        Assert.Equal(typeof(string), report.GetParameters()[2].ParameterType);
        Assert.False(report.GetParameters()[2].IsOptional);
    }

    [Fact]
    public void PerTreeDiagnostics_FilterBySourceSnapshot()
    {
        Compilation compilation = Create(
            "namespace Example; void A() { missing; }",
            "namespace Example; void B() { otherMissing; }");
        SemanticModel first = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        Assert.All(first.GetDiagnostics(), diagnostic => Assert.Equal("test0.xe", diagnostic.Location.Path));
        Assert.Contains(first.GetDiagnostics(), diagnostic => diagnostic.Message.Contains("missing", StringComparison.Ordinal));
        Assert.DoesNotContain(first.GetDiagnostics(), diagnostic => diagnostic.Message.Contains("otherMissing", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancellation_IsNormalAndDoesNotCorruptSubsequentAnalysis()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => Compilation.Create(
            cancellation.Token, SourceText.From("namespace Example; void F() {}")));

        Compilation subsequent = Create("namespace Example; int F() { return 1; }");
        Assert.False(subsequent.HasErrors);
        Assert.Single(subsequent.SemanticModel.Functions);
        Assert.Throws<OperationCanceledException>(() =>
            subsequent.SemanticModel.GetDiagnostics(cancellation.Token));
    }

    [Fact]
    public void MissingTokens_AreExplicitZeroWidthArtifacts()
    {
        Compilation compilation = Create("namespace Example; struct Item { public int Value; } void Test(Item item) { item.");
        FunctionDeclarationSyntax function = compilation.SyntaxTrees[0].Root.Members.OfType<FunctionDeclarationSyntax>().Single();
        var access = Assert.IsType<MemberAccessExpressionSyntax>(
            Assert.IsType<ExpressionStatementSyntax>(Assert.Single(function.Body!.Statements)).Expression);
        Assert.True(access.MemberToken.IsMissing);
        Assert.Equal(0, access.MemberToken.Location.Span.Length);
        Assert.True(function.Body.CloseBraceToken.IsMissing);
        Assert.Equal(0, function.Body.CloseBraceToken.Location.Span.Length);
    }

    [Theory]
    [InlineData("namespace Example; void Foo() {} void Test() { Foo(")]
    [InlineData("namespace Example; void Test() { new<")]
    [InlineData("namespace Example; struct Example {")]
    [InlineData("namespace Example; readonly")]
    public void CanonicalEditorStates_ProduceQueryableModels(string source)
    {
        Compilation compilation = Create(source);
        SemanticModel model = compilation.GetSemanticModel(compilation.SyntaxTrees[0]);
        Assert.True(compilation.HasErrors);
        Assert.NotNull(model.GlobalNamespace);
        Assert.NotEmpty(model.GetDiagnostics());
    }

    private static Compilation Create(params string[] sources) => Compilation.Create(
        sources.Select((source, index) => SourceText.From(source, $"test{index}.xe")).ToArray());
}
