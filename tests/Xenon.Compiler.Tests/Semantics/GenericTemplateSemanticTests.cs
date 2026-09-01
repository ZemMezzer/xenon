using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class GenericTemplateSemanticTests
{
    [Fact]
    public void Analyzer_ResolvesGenericParametersThroughoutFunctionAndStructSignatures()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T>
            {
                T value;
                public T GetValue() { return value; }
            }
            T Identity<T>(T value) { return value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        SyntaxTree tree = Assert.Single(compilation.SyntaxTrees);
        var structureSyntax = Assert.IsType<StructDeclarationSyntax>(tree.Root.Members[0]);
        var functionSyntax = Assert.IsType<FunctionDeclarationSyntax>(tree.Root.Members[1]);
        var structure = Assert.IsType<StructTypeSymbol>(compilation.SemanticModel.GetDeclaredSymbol(structureSyntax));
        var function = Assert.IsType<FunctionSymbol>(compilation.SemanticModel.GetDeclaredSymbol(functionSyntax));

        GenericParameterSymbol structParameter = Assert.Single(structure.TypeParameters);
        Assert.Same(structParameter, Assert.Single(structure.Fields).Type);
        Assert.Same(structParameter, structure.Methods.Single(method => method.Name == "GetValue").ReturnType);

        GenericParameterSymbol functionParameter = Assert.Single(function.TypeParameters);
        Assert.Same(functionParameter, function.ReturnType);
        Assert.Same(functionParameter, Assert.Single(function.Parameters).Type);
        Assert.Same(function, functionParameter.ContainingSymbol);
        Assert.NotSame(structParameter, functionParameter);
        Assert.True(new GenericConstraintValidator().Validate(functionParameter, BuiltinTypes.Int).IsValid);
    }

    [Fact]
    public void Analyzer_ClassifiesNominalAndStructuralConstraintsWithoutConflatingThem()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct BaseEntity { }
            interface IEntity { int Id(); }
            template Equalable { bool Equals(Equalable other); }
            struct Box<T> where T : BaseEntity, IEntity, Equalable { T value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        var boxSyntax = Assert.IsType<StructDeclarationSyntax>(compilation.SyntaxTrees[0].Root.Members[3]);
        var box = Assert.IsType<StructTypeSymbol>(compilation.SemanticModel.GetDeclaredSymbol(boxSyntax));
        GenericParameterSymbol parameter = Assert.Single(box.TypeParameters);
        Assert.Equal(
            [GenericConstraintKind.BaseStruct, GenericConstraintKind.Interface, GenericConstraintKind.StructuralTemplate],
            parameter.Constraints.Select(constraint => constraint.Kind));
        Assert.IsType<StructTypeSymbol>(parameter.Constraints[0].Target);
        Assert.IsType<InterfaceTypeSymbol>(parameter.Constraints[1].Target);
        Assert.IsType<TemplateSymbol>(parameter.Constraints[2].Target);
    }

    [Fact]
    public void Analyzer_BindsTemplateRequirementsAndKeepsSelfTypeCompileTimeOnly()
    {
        Compilation compilation = Create("""
            namespace Example;
            template VectorLike
            {
                VectorLike();
                VectorLike(float x, float y, float z);
                float Length();
                VectorLike& Normalize(VectorLike* other);
                float X { get; }
                float this[int index] { get; set; }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        var syntax = Assert.IsType<TemplateDeclarationSyntax>(Assert.Single(compilation.SyntaxTrees[0].Root.Members));
        var template = Assert.IsType<TemplateSymbol>(compilation.SemanticModel.GetDeclaredSymbol(syntax));
        Assert.Equal(2, template.Members.OfType<TemplateConstructorRequirementSymbol>().Count());
        Assert.Equal(2, template.Members.OfType<TemplateMethodRequirementSymbol>().Count());
        Assert.Single(template.Members.OfType<TemplatePropertyRequirementSymbol>());
        Assert.Single(template.Members.OfType<TemplateIndexerRequirementSymbol>());

        TemplateMethodRequirementSymbol normalize = template.Members
            .OfType<TemplateMethodRequirementSymbol>().Single(member => member.Name == "Normalize");
        Assert.IsType<ReferenceTypeSymbol>(normalize.ReturnType);
        Assert.IsType<PointerTypeSymbol>(Assert.Single(normalize.Parameters).Type);
    }

    [Fact]
    public void Analyzer_ReportsMalformedConstraintsAndRuntimeTemplateUse()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Contract { void Run(); }
            struct Box<T>
                where U : Contract
                where T : int
            { }
            void Use(Contract value) { }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.UnknownConstraintTypeParameter);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.InvalidGenericConstraint);
        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.TemplateCannotBeUsedAsType);
    }

    [Fact]
    public void Analyzer_CreatesAndCachesConcreteFunctionSpecializations()
    {
        Compilation compilation = Create("""
            namespace Example;
            T Identity<T>(T value) { return value; }
            int Main() { return Identity<int>(42) + Identity(1); }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction specialization = Assert.Single(compilation.SemanticModel.Functions,
            function => function.Symbol.IsGenericSpecialization);
        Assert.Equal("Identity<int>", specialization.Symbol.Name);
        Assert.Same(BuiltinTypes.Int, specialization.Symbol.ReturnType);
        Assert.Same(BuiltinTypes.Int, Assert.Single(specialization.Symbol.Parameters).Type);
        Assert.Same(BuiltinTypes.Int, Assert.Single(specialization.Symbol.TypeArguments));
    }

    [Fact]
    public void StructuralMatcher_RequiresExactSignaturesAndReportsFailureCategories()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Shape
            {
                Shape(float value);
                int Compare(Shape other);
                float X { get; }
                int this[int index] { get; }
            }
            struct Good
            {
                public Good(float value) { }
                public int Compare(Good other) { return 0; }
                public float X { get { return 0.0f; } }
                public int this[int index] { get { return index; } }
            }
            struct Bad
            {
                private Bad(float value) { }
                public bool Compare(Bad other) { return false; }
                public float X { get { return 0.0f; } }
            }
            """);
        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        TemplateSymbol template = Assert.Single(ns.Templates);
        StructTypeSymbol good = ns.Structs.Single(type => type.Name == "Good");
        StructTypeSymbol bad = ns.Structs.Single(type => type.Name == "Bad");
        var matcher = new TemplateConformanceMatcher();

        Assert.True(matcher.Match(good, template).IsValid);
        TemplateMatchResult failure = matcher.Match(bad, template);
        Assert.False(failure.IsValid);
        Assert.Single(failure.AccessibilityFailures);
        Assert.Single(failure.SignatureMismatches);
        Assert.Single(failure.MissingMembers);
    }

    [Fact]
    public void ConstraintValidator_KeepsNominalAndStructuralRelationshipsDistinct()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Base { }
            interface IRun { void Run(); }
            template Runnable { void Run(); }
            struct Generic<T> where T : Base, IRun, Runnable { }
            struct Derived : Base, IRun { public void Run() { } }
            struct Lookalike { public void Run() { } }
            """);
        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        GenericParameterSymbol parameter = ns.Structs.Single(type => type.Name == "Generic").TypeParameters[0];
        var validator = new GenericConstraintValidator();

        Assert.True(validator.Validate(parameter, ns.Structs.Single(type => type.Name == "Derived")).IsValid);
        GenericConstraintValidationResult lookalike =
            validator.Validate(parameter, ns.Structs.Single(type => type.Name == "Lookalike"));
        Assert.False(lookalike.IsValid);
        Assert.Equal(2, lookalike.Failures.Length);
        Assert.DoesNotContain(lookalike.Failures,
            failure => failure.Constraint.Kind == GenericConstraintKind.StructuralTemplate);
    }

    [Fact]
    public void Analyzer_ChecksGenericBodiesAgainstGuaranteedTemplateMembers()
    {
        Compilation compilation = Create("""
            namespace Example;
            template VectorLike
            {
                VectorLike(float value);
                float Length();
                float X { get; set; }
                float this[int index] { get; set; }
            }
            float Measure<T>(T value) where T : VectorLike
            {
                value.X = 2.0f;
                value[0] = value.X;
                return value.Length() + value[0];
            }
            T* Create<T>(float value) where T : VectorLike
            {
                return new T(value);
            }
            T CreateValue<T>(float value) where T : VectorLike
            {
                return T(value);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        Assert.DoesNotContain(compilation.SemanticModel.Functions,
            function => function.Symbol.IsGenericDefinition);
    }

    [Fact]
    public void SemanticModel_ClassifiesGenericParameterValueConstructionAsTemplateConstructor()
    {
        Compilation compilation = Create("""
            namespace Example;
            template VectorLike { VectorLike(float x, float y, float z); }
            T Create<T>(float x, float y, float z) where T : VectorLike
            {
                return T(x, y, z);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        var function = Assert.IsType<FunctionDeclarationSyntax>(compilation.SyntaxTrees[0].Root.Members[1]);
        var @return = Assert.IsType<ReturnStatementSyntax>(Assert.Single(function.Body!.Statements));
        var construction = Assert.IsType<CallExpressionSyntax>(@return.Expression);
        var constructor = Assert.IsType<TemplateConstructorRequirementSymbol>(
            compilation.SemanticModel.GetSymbolInfo(construction.Target).Symbol);
        var functionSymbol = Assert.IsType<FunctionSymbol>(compilation.SemanticModel.GetDeclaredSymbol(function));
        GenericParameterSymbol parameter = Assert.IsType<GenericParameterSymbol>(functionSymbol.ReturnType);

        Assert.Equal("VectorLike", constructor.Name);
        Assert.Same(parameter, compilation.SemanticModel.GetTypeInfo(construction.Target).Type);
    }

    [Fact]
    public void Analyzer_SubstitutesTemplateSelfWithTheConstrainedTypeParameter()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Equalable { bool Equals(Equalable other); }
            bool Same<T>(T left, T right) where T : Equalable
            {
                return left.Equals(right);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void Analyzer_RejectsMembersAndConstructionNotGuaranteedByConstraints()
    {
        Compilation compilation = Create("""
            namespace Example;
            void Invoke<T>(T value) { value.Run(); }
            T* Create<T>() { return new T(); }
            T CreateValue<T>() { return T(); }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.GenericMemberNotGuaranteed);
        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.GenericConstructorNotGuaranteed);
        Assert.DoesNotContain(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.UnknownFunction);
    }

    [Fact]
    public void SemanticModel_OffersOnlyMembersGuaranteedByGenericConstraints()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable
            {
                void Run();
                int State { get; }
            }
            void Invoke<T>(T value) where T : Runnable { value.Run(); }
            """);
        Assert.Empty(compilation.Diagnostics);
        var functionSyntax = Assert.IsType<FunctionDeclarationSyntax>(compilation.SyntaxTrees[0].Root.Members[1]);
        var function = Assert.IsType<FunctionSymbol>(compilation.SemanticModel.GetDeclaredSymbol(functionSyntax));
        GenericParameterSymbol parameter = Assert.Single(function.TypeParameters);

        string[] members = compilation.SemanticModel.LookupMembers(parameter,
            new MemberLookupOptions(MemberAccessKind.Instance)).Select(symbol => symbol.Name).ToArray();

        Assert.Contains("Run", members);
        Assert.Contains("State", members);
        Assert.DoesNotContain("Missing", members);
    }

    [Fact]
    public void Specialization_ValidatesStructuralConstraintsBeforeRebinding()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { void Run(); }
            void Invoke<T>(T value) where T : Runnable { value.Run(); }
            struct Missing { }
            void Use(Missing value) { Invoke<Missing>(value); }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.DoesNotContain(compilation.SemanticModel.Functions,
            function => function.Symbol.IsGenericSpecialization);
    }

    [Fact]
    public void Analyzer_CreatesConcreteGenericStructLayoutsAndMemberSignatures()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T>
            {
                T value;
                public Box(T initial) { value = initial; }
                public T Get() { return value; }
            }
            struct UsesBox { Box<int> item; }
            Box<int>* Create() { return new Box<int>(42); }
            Box<int> CreateValue() { return Box<int>(42); }
            int Read(Box<int>* box) { return box->Get(); }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol box = Assert.Single(ns.Structs, type => type.IsGenericSpecialization);
        Assert.Equal("Box<int>", box.Name);
        Assert.Same(BuiltinTypes.Int, Assert.Single(box.Fields).Type);
        Assert.Same(BuiltinTypes.Int, Assert.Single(box.Constructors).Parameters[0].Type);
        Assert.Same(BuiltinTypes.Int, box.Methods.Single(method => method.Name == "Get").ReturnType);
        Assert.Same(box, Assert.Single(ns.Structs.Single(type => type.Name == "UsesBox").Fields).Type);
        Assert.Equal(2, compilation.SemanticModel.Functions.Count(function =>
            ReferenceEquals(function.Symbol.ContainingStruct, box)));
    }

    [Fact]
    public void StructSpecialization_RejectsConcreteTypesThatViolateConstraints()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { void Run(); }
            struct Constrained<T> where T : Runnable { T value; }
            struct Missing { }
            void Use(Constrained<Missing>* value) { }
            """);

        Assert.Contains(compilation.Diagnostics,
            diagnostic => diagnostic.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        Assert.DoesNotContain(ns.Structs, type => type.IsGenericSpecialization);
    }

    [Fact]
    public void StructSpecialization_CreatesIndependentMembersAndBodiesForThreeTypeArguments()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T>
            {
                T value;
                public Box(T initial) { value = initial; }
                public T Get() { T local = value; return local; }
            }
            int ReadInt(int value) { return Box<int>(value).Get(); }
            float ReadFloat(float value) { return Box<float>(value).Get(); }
            long ReadLong(long value) { return Box<long>(value).Get(); }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol[] boxes = ns.Structs.Where(type => type.IsGenericSpecialization).ToArray();
        Assert.Equal(3, boxes.Length);
        Assert.Equal(3, boxes.Select(type => type.Methods.Single(method => method.Name == "Get")).Distinct().Count());
        Assert.Equal(3, boxes.Select(type => Assert.Single(type.Constructors)).Distinct().Count());
        foreach (StructTypeSymbol box in boxes)
        {
            TypeSymbol argument = Assert.Single(box.TypeArguments);
            Assert.Same(argument, box.Methods.Single(method => method.Name == "Get").ReturnType);
            Assert.Same(argument, Assert.Single(box.Constructors).Parameters[0].Type);
            Assert.Contains(compilation.SemanticModel.Functions,
                function => ReferenceEquals(function.Symbol, box.Methods.Single(method => method.Name == "Get")));
            Assert.Contains(compilation.SemanticModel.Functions,
                function => ReferenceEquals(function.Symbol, Assert.Single(box.Constructors)));
        }
    }

    [Fact]
    public void StructSpecialization_RecursivelyClosesNestedOpenGenericFields()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Pair<TKey, TValue> { TKey key; TValue value; }
            struct Wrapper<T> { Pair<int, T> pair; }
            struct Uses { Wrapper<float> value; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol wrapper = ns.Structs.Single(type => type.Name == "Wrapper<float>");
        StructTypeSymbol pair = Assert.IsType<StructTypeSymbol>(Assert.Single(wrapper.Fields).Type);
        Assert.Equal("Pair<int,float>", pair.Name);
        Assert.Collection(pair.TypeArguments,
            argument => Assert.Same(BuiltinTypes.Int, argument),
            argument => Assert.Same(BuiltinTypes.Float, argument));
    }

    [Fact]
    public void StructSpecialization_TerminatesForRecursiveConstructedPointerFields()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Node<T> { T value; Node<T>* next; }
            struct Uses { Node<int> first; Node<float> second; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        foreach (StructTypeSymbol node in ns.Structs.Where(type => type.Name is "Node<int>" or "Node<float>"))
        {
            var next = Assert.IsType<PointerTypeSymbol>(node.Fields.Single(field => field.Name == "next").Type);
            Assert.Same(node, next.ElementType);
        }
    }

    [Fact]
    public void StructSpecialization_CompletesCandidateMembersBeforeTemplateConstraintMatching()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { void Run(); }
            struct Runner<T> { public void Run() { } }
            struct Wrapper<T> where T : Runnable { T value; }
            struct Uses { Wrapper<Runner<int>> value; }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void GenericBody_BaseStructConstraintProvidesInheritedFields()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Base { public int id; }
            struct Derived : Base { }
            int Read<T>(T value) where T : Base { return value.id; }
            int Use(Derived value) { return Read<Derived>(value); }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void StructuralMatcher_TreatsAccessorsAsMinimumCapabilitiesAndRecognizesImplicitDefaultConstruction()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Shape { Shape(); int Value { get; } int this[int index] { set; } }
            struct Implicit
            {
                public int Value { get { return 0; } set { } }
                public int this[int index] { get { return 0; } set { } }
            }
            struct Explicit
            {
                public Explicit() { }
                public int Value { get { return 0; } }
                public int this[int index] { set { } }
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        TemplateSymbol template = Assert.Single(ns.Templates);
        var matcher = new TemplateConformanceMatcher();
        Assert.True(matcher.Match(ns.Structs.Single(type => type.Name == "Implicit"), template).IsValid);
        Assert.True(matcher.Match(ns.Structs.Single(type => type.Name == "Explicit"), template).IsValid);
    }

    [Fact]
    public void ConstraintDiagnostic_IncludesRequiredAndFoundStructuralSignatures()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Comparable { int Compare(float value); }
            struct Foo { public int Compare(double value) { return 0; } }
            void Use<T>(T value) where T : Comparable { }
            void Test(Foo value) { Use<Foo>(value); }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.Contains("required int Compare(float value)", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("found int Compare(double value)", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GenericFunctionSpecialization_InfersAndSubstitutesNestedConstructedTypes()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T> { T value; }
            Box<T> IdentityBox<T>(Box<T> value) { return value; }
            Box<int> Use(Box<int> value) { return IdentityBox(value); }
            """);

        Assert.Empty(compilation.Diagnostics);
        BoundFunction function = Assert.Single(compilation.SemanticModel.Functions,
            item => item.Symbol.IsGenericSpecialization);
        var result = Assert.IsType<StructTypeSymbol>(function.Symbol.ReturnType);
        Assert.Equal("Box<int>", result.Name);
        Assert.Same(result, Assert.Single(function.Symbol.Parameters).Type);
    }

    [Fact]
    public void TemplateMembers_CanReferenceConcreteGenericStructTypes()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T> { T value; }
            template Processor { void Process(Box<int> value); }
            struct Worker { public void Process(Box<int> value) { } }
            void Execute<T>(T value, Box<int> box) where T : Processor { value.Process(box); }
            void Use(Worker value, Box<int> box) { Execute<Worker>(value, box); }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void StructuralConstructorMatching_IgnoresBaseAndThisInitializerImplementationDetails()
    {
        Compilation compilation = Create("""
            namespace Example;
            template VectorLike { VectorLike(float x, float y, float z); }
            template IntConstructible { IntConstructible(int value); }
            struct Vector2 { public Vector2(float x, float y) { } }
            struct Vector3 : Vector2
            {
                public Vector3(float x, float y, float z) : base(x, y) { }
            }
            struct Value
            {
                public Value() { }
                public Value(int value) : this() { }
            }
            void AcceptVector<T>(T value) where T : VectorLike { }
            void AcceptValue<T>(T value) where T : IntConstructible { }
            void Use(Vector3 vector, Value value)
            {
                AcceptVector<Vector3>(vector);
                AcceptValue<Value>(value);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void NestedConstrainedGenerics_ProduceIndependentClosedSpecializations()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Equalable { bool Equals(Equalable other); }
            struct First { public bool Equals(First other) { return true; } }
            struct Second { public bool Equals(Second other) { return false; } }
            struct Pair<T> where T : Equalable
            {
                T first;
                T second;
                public Pair(T left, T right) { first = left; second = right; }
                public bool Same() { return first.Equals(second); }
            }
            struct Container<T> where T : Equalable { Pair<T> pair; }
            struct Uses { Container<First> first; Container<Second> second; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol[] pairs = ns.Structs.Where(type =>
            type.Name is "Pair<Example.First>" or "Pair<Example.Second>").ToArray();
        Assert.Equal(2, pairs.Length);
        foreach (StructTypeSymbol pair in pairs)
            Assert.Contains(compilation.SemanticModel.Functions,
                function => ReferenceEquals(function.Symbol, pair.Methods.Single(method => method.Name == "Same")));
    }

    [Fact]
    public void LateRecursiveSpecialization_RemainsMonotonicAndBindsMembersOnce()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Node<T>
            {
                T value;
                Node<T>* next;
                public Node(T value) { this.value = value; next = null; }
                public T GetValue() { return value; }
            }
            int Use() { Node<int> node = Node<int>(42); return node.GetValue(); }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol node = ns.Structs.Single(type => type.Name == "Node<int>");
        Assert.Single(node.Methods.Where(method => method.Name == "GetValue"));
        Assert.Single(node.Constructors);
        Assert.Same(node, Assert.IsType<PointerTypeSymbol>(node.Fields.Single(field => field.Name == "next").Type).ElementType);
    }

    [Fact]
    public void GenericStructSpecialization_PreservesInstanceAndStaticInitializersAndConstants()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data<T>
            {
                const int Size = 16;
                const nuint Width = sizeof(T);
                int version = 42;
                static int state = 7;
                public int GetVersion() { return version; }
                public int GetSize() { return Size; }
                public int GetState() { return Data.state; }
                public void SetState(int value) { Data.state = value; }
            }
            int Use()
            {
                Data<int> first = Data<int>();
                Data<float> second = Data<float>();
                first.SetState(10);
                second.SetState(20);
                return first.GetVersion() + first.GetSize() + first.GetState() + second.GetState();
            }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        foreach (StructTypeSymbol data in ns.Structs.Where(type => type.IsGenericSpecialization))
        {
            Assert.NotNull(data.InstanceInitializer);
            Assert.Equal(42, data.Fields.Single(field => field.Name == "version").Initializer is BoundLiteralExpression literal
                ? literal.Value : null);
            Assert.Equal(7, data.StaticFields.Single(field => field.Name == "state").ConstantValue);
            Assert.Equal(16, data.Constants.Single(constant => constant.Name == "Size").Value);
            BoundTypeLayoutExpression width = Assert.IsType<BoundTypeLayoutExpression>(
                data.Constants.Single(constant => constant.Name == "Width").BoundValue);
            Assert.Same(Assert.Single(data.TypeArguments), width.TargetType);
        }
    }

    [Fact]
    public void ConcreteGenericSpecialization_RejectsRecursiveByValueLayout()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Recursive<T> { Recursive<T> value; }
            int Use() { Recursive<int> value = Recursive<int>(); return 0; }
            """);

        Assert.Contains(compilation.Diagnostics, diagnostic => diagnostic.Id == DiagnosticIds.RecursiveValueLayout);
    }

    [Fact]
    public void TemplateSelf_SubstitutesRecursivelyInsideNestedGenericTypes()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T> { public T value; public Box(T value) { this.value = value; } }
            struct Wrapper<T> { public T value; public Wrapper(T value) { this.value = value; } }
            template Processor { int Process(Wrapper<Box<Processor>> value); }
            struct Worker
            {
                public int value;
                public Worker(int value) { this.value = value; }
                public int Process(Wrapper<Box<Worker>> input) { return input.value.value.value; }
            }
            int Execute<T>(T worker, Wrapper<Box<T>> value) where T : Processor
            { return worker.Process(value); }
            int Use()
            {
                Worker worker = Worker(42);
                Box<Worker> box = Box<Worker>(worker);
                Wrapper<Box<Worker>> wrapper = Wrapper<Box<Worker>>(box);
                return Execute<Worker>(worker, wrapper);
            }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void TemplateSelf_NestedGenericMismatchRetainsDetailedDiagnostic()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Box<T> { T value; }
            template Processor { void Process(Box<Processor> value); }
            struct Worker { public void Process(Box<int> value) { } }
            void Execute<T>(T value) where T : Processor { }
            void Use(Worker value) { Execute<Worker>(value); }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.Contains("Box<Processor>", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Box<int>", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GenericStructConstraintFailure_IsReportedAtFirstConcreteInstantiation()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { void Run(); }
            struct Bad { }
            struct Wrapper<T> where T : Runnable { public T value; }
            int Main()
            {
                Wrapper<Bad> value = Wrapper<Bad>();
                return 0;
            }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.Equal(new LinePosition(6, 11), diagnostic.Location.Start);
    }

    [Fact]
    public void SpecializedInitializerCompletion_ReachesSpecializationsCreatedWhileBindingInitializer()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Inner<T> { public int value = 42; }
            int ReadInner<T>()
            {
                Inner<T> inner = Inner<T>();
                return inner.value;
            }
            struct Outer<T> { public int observed = ReadInner<T>(); }
            int Main() { Outer<int> value = Outer<int>(); return value.observed; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol inner = ns.Structs.Single(type => type.Name == "Inner<int>");
        Assert.NotNull(inner.InstanceInitializer);
        Assert.Contains(compilation.SemanticModel.Functions,
            function => ReferenceEquals(function.Symbol, inner.InstanceInitializer));
    }

    [Fact]
    public void SpecializedInitializerCompletion_AlsoBindsNestedStaticInitializer()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Inner<T> { public static int value = 42; }
            struct Outer<T> { public nuint size = sizeof(Inner<T>); }
            int Main() { Outer<int> value = Outer<int>(); return 0; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol inner = ns.Structs.Single(type => type.Name == "Inner<int>");
        Assert.Equal(42, Assert.Single(inner.StaticFields).ConstantValue);
    }

    [Fact]
    public void OpenConstructedStructConstraints_RequireProofFromContainingGenericParameter()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { void Run(); }
            struct Required<T> where T : Runnable { public T value; }
            struct BadOuter<T> { public Required<T> value; }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.Contains("do not guarantee 'Runnable'", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenConstructedStructConstraints_PropagateStructuralInterfaceAndBaseGuarantees()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { int Run(); }
            struct StructuralRequired<T> where T : Runnable { public T value; }
            struct StructuralOuter<T> where T : Runnable { public StructuralRequired<T> value; }

            interface IBase { int Get(); }
            interface IDerived : IBase { }
            struct InterfaceRequired<T> where T : IBase { public T value; }
            struct InterfaceOuter<T> where T : IDerived { public InterfaceRequired<T> value; }

            struct Base { }
            struct Derived : Base { }
            struct BaseRequired<T> where T : Base { public T value; }
            struct BaseOuter<T> where T : Derived { public BaseRequired<T> value; }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void NestedConcreteSpecialization_PropagatesOuterInstantiationOrigin()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { void Run(); }
            struct Inner<T> where T : Runnable { }
            struct Outer<T> where T : Runnable { public Inner<T> value; }
            struct Bad { }
            int Main()
            {
                Outer<Bad> value = Outer<Bad>();
                return 0;
            }
            """);

        Diagnostic[] diagnostics = compilation.Diagnostics
            .Where(item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied).ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic => Assert.Equal(new LinePosition(7, 9), diagnostic.Location.Start));
    }

    [Fact]
    public void SpecializedConstants_RegisterAllScopesBeforeFollowingForwardDependencies()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct Data<T>
            {
                const nuint A = B;
                const nuint B = C;
                const nuint C = sizeof(T);
                public nuint Get() { return A; }
            }
            int Main() { Data<int> value = Data<int>(); return 0; }
            """);

        Assert.Empty(compilation.Diagnostics);
        NamespaceSymbol ns = Assert.Single(compilation.SemanticModel.GlobalNamespace.Namespaces);
        StructTypeSymbol data = ns.Structs.Single(type => type.Name == "Data<int>");
        foreach (ConstantSymbol constant in data.Constants)
        {
            BoundTypeLayoutExpression layout = Assert.IsType<BoundTypeLayoutExpression>(constant.BoundValue);
            Assert.Same(BuiltinTypes.Int, layout.TargetType);
        }
    }

    [Fact]
    public void RecursiveGenericStructuralCandidate_DefersUntilMembersAreStable()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { int Run(); }
            struct Required<T> where T : Runnable { }
            struct Node<T>
            {
                Required<Node<T>>* metadata;
                public int Run() { return 42; }
            }
            int Main() { Node<int> value = Node<int>(); return value.Run(); }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void RecursiveGenericStructuralCandidate_EventuallyReportsStableFailure()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { int Run(); }
            struct Required<T> where T : Runnable { }
            struct BadNode<T> { Required<BadNode<T>>* metadata; }
            int Main() { BadNode<int> value = BadNode<int>(); return 0; }
            """);

        Diagnostic[] diagnostics = compilation.Diagnostics
            .Where(item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied).ToArray();
        Assert.Equal(2, diagnostics.Length);
        Assert.All(diagnostics, diagnostic =>
            Assert.Contains("Run", diagnostic.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void BaseStructConstraint_ImpliesInterfacesImplementedByItsHierarchy()
    {
        Compilation compilation = Create("""
            namespace Example;
            interface IRunnable { int Run(); }
            struct Base : IRunnable { public int Run() { return 42; } }
            struct Derived : Base { }
            struct Required<T> where T : IRunnable { public T value; }
            struct FromBase<T> where T : Base { public Required<T> value; }
            struct FromDerived<T> where T : Derived { public Required<T> value; }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void StructuralTemplateGuarantees_UseCapabilitySubsumptionAndTemplateSelfNormalization()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { int Run(); }
            template AdvancedRunnable { int Run(); int State(); }
            struct RequiredRunnable<T> where T : Runnable { public T value; }
            struct RunnableOuter<T> where T : AdvancedRunnable { public RequiredRunnable<T> value; }

            template Equalable { bool Equals(Equalable other); }
            template HashEqualable { bool Equals(HashEqualable other); int Hash(); }
            struct RequiredEqualable<T> where T : Equalable { public T value; }
            struct EqualableOuter<T> where T : HashEqualable { public RequiredEqualable<T> value; }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void WeakerStructuralTemplate_DoesNotGuaranteeStrongerTemplate()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { int Run(); }
            template AdvancedRunnable { int Run(); int State(); }
            struct Required<T> where T : AdvancedRunnable { public T value; }
            struct BadOuter<T> where T : Runnable { public Required<T> value; }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.Contains("AdvancedRunnable", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("State", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeStructuralConstraints_JointlyGuaranteeRequiredTemplate()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { int Run(); }
            template Stateful { int State(); }
            template Advanced { int Run(); int State(); }
            struct Required<T> where T : Advanced { public T value; }
            struct Outer<T> where T : Runnable, Stateful { public Required<T> value; }

            template Equalable { bool Equals(Equalable other); }
            template Hashable { int Hash(); }
            template HashEqualable { bool Equals(HashEqualable other); int Hash(); }
            struct EqualityRequired<T> where T : HashEqualable { public T value; }
            struct EqualityOuter<T> where T : Equalable, Hashable { public EqualityRequired<T> value; }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void CompositeStructuralConstraints_ComposeAccessorsAndConstructorsByExactShape()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Readable { int Value { get; } }
            template Writable { int Value { set; } }
            template ReadWrite { int Value { get; set; } }
            struct PropertyRequired<T> where T : ReadWrite { public T value; }
            struct PropertyOuter<T> where T : Readable, Writable { public PropertyRequired<T> value; }

            template IndexReadable { int this[int index] { get; } }
            template IndexWritable { int this[int index] { set; } }
            template IndexReadWrite { int this[int index] { get; set; } }
            struct IndexRequired<T> where T : IndexReadWrite { public T value; }
            struct IndexOuter<T> where T : IndexReadable, IndexWritable { public IndexRequired<T> value; }

            template DefaultConstructible { DefaultConstructible(); }
            template IntConstructible { IntConstructible(int value); }
            template FullConstructible { FullConstructible(); FullConstructible(int value); }
            struct ConstructorRequired<T> where T : FullConstructible { public T value; }
            struct ConstructorOuter<T> where T : DefaultConstructible, IntConstructible
            { public ConstructorRequired<T> value; }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void NominalAndStructuralConstraints_JointlyGuaranteeStructuralTemplate()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct BaseRunnable { public int Run() { return 40; } }
            interface IStateful { int State(); }
            template Advanced { int Run(); int State(); }
            struct Required<T> where T : Advanced { public T value; }
            struct MixedOuter<T> where T : BaseRunnable, IStateful { public Required<T> value; }

            template Runnable { int Run(); }
            struct BaseOuter<T> where T : BaseRunnable { public RequiredRun<T> value; }
            interface IRunnable { int Run(); }
            struct InterfaceOuter<T> where T : IRunnable { public RequiredRun<T> value; }
            struct RequiredRun<T> where T : Runnable { public T value; }

            interface IReadValue { int Value { get; } }
            template Readable { int Value { get; } }
            struct ReadRequired<T> where T : Readable { public T value; }
            struct ReadOuter<T> where T : IReadValue { public ReadRequired<T> value; }
            """);

        Assert.Empty(compilation.Diagnostics);
    }

    [Fact]
    public void CompositeGuaranteeFailure_ReportsOnlyMissingCombinedCapability()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { int Run(); }
            template Stateful { int State(); }
            template Advanced { int Run(); int State(); int Serialize(); }
            struct Required<T> where T : Advanced { public T value; }
            struct InvalidOuter<T> where T : Runnable, Stateful { public Required<T> value; }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.Contains("Serialize", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("missing guarantee: public int Run()", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("missing guarantee: public int State()", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedConstraintGuaranteeFailure_ReportsMissingCombinedCapability()
    {
        Compilation compilation = Create("""
            namespace Example;
            struct BaseRunnable { public int Run() { return 0; } }
            interface IUnrelated { int SomethingElse(); }
            template Advanced { int Run(); int State(); }
            struct Required<T> where T : Advanced { public T value; }
            struct InvalidOuter<T> where T : BaseRunnable, IUnrelated { public Required<T> value; }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.Contains("State", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("missing guarantee: public int Run()", diagnostic.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("struct Base { private int Run() { return 0; } }", "Base")]
    [InlineData("struct Base { public int Run(double value) { return 0; } }", "Base")]
    [InlineData("interface IBase { int Run(double value); }", "IBase")]
    public void NominalConstraint_DoesNotGuaranteeInaccessibleOrMismatchedStructuralMember(
        string declaration, string constraint)
    {
        Compilation compilation = Create($$"""
            namespace Example;
            template Runnable { int Run(float value); }
            {{declaration}}
            struct Required<T> where T : Runnable { public T value; }
            struct InvalidOuter<T> where T : {{constraint}} { public Required<T> value; }
            """);

        Diagnostic diagnostic = Assert.Single(compilation.Diagnostics,
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied);
        Assert.Contains("Run", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StructuralConstraint_DoesNotImplyNominalInterfaceOrBase()
    {
        Compilation compilation = Create("""
            namespace Example;
            template Runnable { int Run(); }
            interface IRunnable { int Run(); }
            struct Base { public int Run() { return 0; } }
            struct InterfaceRequired<T> where T : IRunnable { public T value; }
            struct BaseRequired<T> where T : Base { public T value; }
            struct InvalidOuter<T> where T : Runnable
            {
                public InterfaceRequired<T> interfaceValue;
                public BaseRequired<T> baseValue;
            }
            """);

        Assert.Equal(2, compilation.Diagnostics.Count(
            item => item.Id == DiagnosticIds.GenericConstraintNotSatisfied));
    }

    private static Compilation Create(string source) => Compilation.Create(
        [SyntaxTree.Parse(SourceText.From(source, "generic-templates.xe"))]);
}
