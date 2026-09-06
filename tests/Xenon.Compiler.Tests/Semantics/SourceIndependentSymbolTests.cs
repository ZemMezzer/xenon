using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Semantics;

public sealed class SourceIndependentSymbolTests
{
    [Fact]
    public void LibraryOriginSymbolsRepresentSemanticsWithoutSyntaxNodes()
    {
        var global = new NamespaceSymbol(string.Empty, null);
        var ns = new NamespaceSymbol("Library", global);
        SymbolDocumentation docs = SymbolDocumentation.Parse("<summary>A box.</summary>");
        var structure = new StructTypeSymbol("Box", ns, isAbstract: true,
            SymbolOrigin.Library, docs);
        var parameter = new ParameterSymbol("value", BuiltinTypes.Int, 0, isReadonly: true);
        var method = new FunctionSymbol("Read", structure, FunctionKind.Method, BuiltinTypes.Int,
            [parameter], Accessibility.Public, isReadonly: true, isVirtual: true,
            isDefinition: true, origin: SymbolOrigin.Library, documentation: docs);
        var field = new FieldSymbol("Value", structure, BuiltinTypes.Int, 0,
            Accessibility.Public, isStatic: false, isReadonly: true, isThreadLocal: false,
            hasInitializer: false, constantValue: null, SymbolOrigin.Library, docs);
        var template = new TemplateSymbol("Contract", ns, SymbolOrigin.Library, docs);
        var requirement = new TemplateMethodRequirementSymbol("Read", template, BuiltinTypes.Int,
            [new ParameterSymbol("value", BuiltinTypes.Int, 0)], Accessibility.Public,
            isStatic: false, isReadonly: true, SymbolOrigin.Library, docs);
        template.SetMembers([requirement]);
        var typeParameter = new GenericParameterSymbol("T", 0, structure, SymbolOrigin.Library,
            SymbolDocumentation.Parse("<summary>The value type.</summary>"));
        typeParameter.SetConstraints([
            new GenericConstraintSymbol(GenericConstraintKind.StructuralTemplate, template,
                SymbolOrigin.Library),
        ]);

        structure.SetFields([field]);
        structure.SetMethods([method]);
        structure.SetTypeParameters([typeParameter]);

        Assert.True(structure.IsAbstract);
        Assert.Equal("struct", structure.DeclarationKind);
        Assert.Equal("A box.", structure.Documentation.Summary);
        Assert.Empty(structure.DeclaringSyntaxReferences);
        Assert.Equal(SymbolOriginKind.Library, structure.Origin.Kind);
        Assert.True(method.IsReadonly);
        Assert.True(method.IsVirtual);
        Assert.True(method.IsDefinition);
        Assert.Empty(method.DeclaringSyntaxReferences);
        Assert.True(field.IsReadonly);
        Assert.False(field.HasInitializer);
        Assert.Same(template, typeParameter.Constraints.Single().Target);
        Assert.Empty(typeParameter.DeclaringSyntaxReferences);
        Assert.True(requirement.IsReadonly);
        Assert.Empty(requirement.DeclaringSyntaxReferences);
    }

    [Fact]
    public void LibraryAccessorsDisplayFromSemanticIdentityWithoutSyntax()
    {
        var global = new NamespaceSymbol(string.Empty, null);
        var ns = new NamespaceSymbol("Library", global);
        var structure = new StructTypeSymbol("Box", ns, isAbstract: false,
            SymbolOrigin.Library);
        var property = new PropertySymbol("Value", structure, BuiltinTypes.Int,
            Accessibility.Public, isStatic: false, isReadonly: false, isVirtual: false,
            isOverride: false, isAbstract: false, SymbolOrigin.Library);
        var getter = new FunctionSymbol("get_Value", property, FunctionKind.Method,
            BuiltinTypes.Int, [], Accessibility.Public, isDefinition: true,
            origin: SymbolOrigin.Library, accessorKind: AccessorKind.Getter);
        var setter = new FunctionSymbol("set_Value", property, FunctionKind.Method,
            BuiltinTypes.Void, [new ParameterSymbol("value", BuiltinTypes.Int, 0)],
            Accessibility.Public, isDefinition: true, origin: SymbolOrigin.Library,
            accessorKind: AccessorKind.Setter);
        property.SetAccessors(getter, setter);

        Assert.True(getter.IsAccessor);
        Assert.Equal("Library.Box.Value.get",
            getter.ToDisplayString(SymbolDisplayFormat.QualifiedName));
        Assert.Equal("void Library.Box.Value.set(int value)",
            setter.ToDisplayString(SymbolDisplayFormat.QualifiedSignature));
        Assert.Empty(getter.DeclaringSyntaxReferences);
        Assert.Empty(setter.DeclaringSyntaxReferences);
    }

    [Fact]
    public void MissingImplementationForSourceLessGenericReportsAtConsumerUseSite()
    {
        var global = new NamespaceSymbol(string.Empty, null);
        var ns = global.GetOrAddNamespace("Library");
        var typeParameter = new GenericParameterSymbol("T", 0, ns, SymbolOrigin.Library);
        var function = new FunctionSymbol("Identity", ns, FunctionKind.Ordinary,
            typeParameter, [new ParameterSymbol("value", typeParameter, 0)],
            Accessibility.Public, isDefinition: true, typeParameters: [typeParameter],
            origin: SymbolOrigin.Library);
        typeParameter.SetDeclaringSymbol(function);
        Assert.True(ns.TryDeclareFunction(function));

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SemanticOnlyReference(global)], SourceText.From("""
                using Library;
                namespace App;
                int Main() { return Identity<int>(42); }
                """, "consumer.xe"));

        Diagnostic diagnostic = Assert.Single(app.Diagnostics,
            item => item.Id == DiagnosticIds.GenericSpecializationNotImplemented);
        Assert.Equal("consumer.xe", diagnostic.Location.Path);
        Assert.Contains("implementation is unavailable", diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLessGenericStructInitializerFailureUsesConsumerLocation()
    {
        var global = new NamespaceSymbol(string.Empty, null);
        var ns = global.GetOrAddNamespace("Library");
        var box = new StructTypeSymbol("Box", ns, isAbstract: false, SymbolOrigin.Library);
        var typeParameter = new GenericParameterSymbol("T", 0, box, SymbolOrigin.Library);
        box.SetTypeParameters([typeParameter]);
        box.SetFields([
            new FieldSymbol("Value", box, typeParameter, 0, Accessibility.Public,
                isStatic: false, isReadonly: false, isThreadLocal: false,
                hasInitializer: true, constantValue: null, SymbolOrigin.Library),
        ]);
        Assert.True(ns.TryDeclareType(box));

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SemanticOnlyReference(global)], SourceText.From("""
                using Library;
                namespace App;
                void Use(Box<int>* value) { }
                """, "consumer-struct.xe"));

        Diagnostic diagnostic = Assert.Single(app.Diagnostics,
            item => item.Id == DiagnosticIds.GenericSpecializationNotImplemented);
        Assert.Equal("consumer-struct.xe", diagnostic.Location.Path);
        Assert.Contains("cannot initialize its fields", diagnostic.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SourceLessReferencedBaseVirtualLayoutIsConsumedWithoutMutation()
    {
        (NamespaceSymbol global, StructTypeSymbol baseType) = CreateSourceLessVirtualBase();
        var before = baseType.VirtualMethods;

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SemanticOnlyReference(global)], SourceText.From("""
                using Library;
                namespace App;
                struct Derived : Base
                {
                    public override int Read() { return 40; }
                    public override int Required() { return 42; }
                    public override int Value { get { return 1; } set { } }
                    public override int this[int index] { get { return index; } set { } }
                    public override ~Derived() { }
                }
                """, "consumer-virtual.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        StructTypeSymbol derived = app.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "App").Structs.Single();
        Assert.Same(baseType, derived.BaseType);
        Assert.True(before == baseType.VirtualMethods);
        Assert.All(before, member => Assert.Same(member, baseType.VirtualMethods[member.VTableSlot!.Value]));

        Assert.Equal(baseType.Methods.Single(method => method.Name == "Read").VTableSlot,
            derived.Methods.Single(method => method.Name == "Read").VTableSlot);
        Assert.Equal(baseType.Methods.Single(method => method.Name == "Required").VTableSlot,
            derived.Methods.Single(method => method.Name == "Required").VTableSlot);
        Assert.Equal(baseType.Properties.Single().Getter!.VTableSlot,
            derived.Properties.Single().Getter!.VTableSlot);
        Assert.Equal(baseType.Properties.Single().Setter!.VTableSlot,
            derived.Properties.Single().Setter!.VTableSlot);
        Assert.Equal(baseType.Indexers.Single().Getter!.VTableSlot,
            derived.Indexers.Single().Getter!.VTableSlot);
        Assert.Equal(baseType.Indexers.Single().Setter!.VTableSlot,
            derived.Indexers.Single().Setter!.VTableSlot);
        Assert.Equal(baseType.Destructor!.VTableSlot, derived.Destructor!.VTableSlot);
    }

    [Fact]
    public void SourceLessAbstractMemberDiagnosticUsesConsumerDeclaration()
    {
        (NamespaceSymbol global, StructTypeSymbol baseType) = CreateSourceLessVirtualBase();
        var before = baseType.VirtualMethods;

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SemanticOnlyReference(global)], SourceText.From("""
                using Library;
                namespace App;
                struct Incomplete : Base { }
                """, "consumer-abstract.xe"));

        Diagnostic diagnostic = Assert.Single(app.Diagnostics,
            item => item.Id == DiagnosticIds.UnimplementedInterfaceMember);
        Assert.Equal("consumer-abstract.xe", diagnostic.Location.Path);
        Assert.Equal("Incomplete", diagnostic.Location.Source.GetText(diagnostic.Location.Span));
        Assert.Contains("Library.Base.Required", diagnostic.Message, StringComparison.Ordinal);
        Assert.True(before == baseType.VirtualMethods);
    }

    [Fact]
    public void OverrideDiagnosticAgainstSourceLessMemberUsesConsumerDeclaration()
    {
        (NamespaceSymbol global, StructTypeSymbol baseType) = CreateSourceLessVirtualBase();

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SemanticOnlyReference(global)], SourceText.From("""
                using Library;
                namespace App;
                abstract struct Invalid : Base
                {
                    public int Read() { return 0; }
                }
                """, "consumer-override.xe"));

        Diagnostic diagnostic = Assert.Single(app.Diagnostics,
            item => item.Id == DiagnosticIds.MissingOverrideModifier);
        Assert.Equal("consumer-override.xe", diagnostic.Location.Path);
        Assert.Equal("Read", diagnostic.Location.Source.GetText(diagnostic.Location.Span));
        Assert.Contains("must be declared 'override'", diagnostic.Message, StringComparison.Ordinal);
        Assert.Empty(baseType.Locations);
        Assert.All(baseType.VirtualMethods, member => Assert.Empty(member.Locations));
    }

    [Fact]
    public void GenericSpecializationPreservesSourceLessBaseVirtualSlots()
    {
        (NamespaceSymbol global, StructTypeSymbol baseType) = CreateSourceLessVirtualBase();
        var before = baseType.VirtualMethods;

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SemanticOnlyReference(global)], SourceText.From("""
                using Library;
                namespace App;
                abstract struct Wrapper<T> : Base { T Item; }
                void Use(Wrapper<int>* value) { }
                """, "consumer-generic-virtual.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        StructTypeSymbol specialization = app.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "App").Structs
            .Single(type => type.IsGenericSpecialization);
        Assert.Same(baseType, specialization.BaseType);
        Assert.Equal(baseType.VirtualMethods.Length, specialization.VirtualMethods.Length);
        Assert.All(baseType.VirtualMethods, inherited =>
            Assert.Same(inherited, specialization.VirtualMethods[inherited.VTableSlot!.Value]));
        Assert.True(before == baseType.VirtualMethods);
    }

    [Fact]
    public void SourceLessAbstractPropertyAndIndexerCanBeSatisfied()
    {
        (NamespaceSymbol global, StructTypeSymbol baseType) =
            CreateSourceLessVirtualBase(abstractAccessors: true);

        Compilation app = Compilation.Create(new CompilationOptions(),
            [new SemanticOnlyReference(global)], SourceText.From("""
                using Library;
                namespace App;
                struct Derived : Base
                {
                    public override int Required() { return 40; }
                    public override int Value { get { return 1; } set { } }
                    public override int this[int index] { get { return index; } set { } }
                }
                """, "consumer-abstract-accessors.xe"));

        Assert.False(app.HasErrors, string.Join(Environment.NewLine, app.Diagnostics));
        StructTypeSymbol derived = app.SemanticModel.GlobalNamespace.Namespaces
            .Single(scope => scope.Name == "App").Structs.Single();
        Assert.Equal(baseType.Properties.Single().Getter!.VTableSlot,
            derived.Properties.Single().Getter!.VTableSlot);
        Assert.Equal(baseType.Properties.Single().Setter!.VTableSlot,
            derived.Properties.Single().Setter!.VTableSlot);
        Assert.Equal(baseType.Indexers.Single().Getter!.VTableSlot,
            derived.Indexers.Single().Getter!.VTableSlot);
        Assert.Equal(baseType.Indexers.Single().Setter!.VTableSlot,
            derived.Indexers.Single().Setter!.VTableSlot);
    }

    private static (NamespaceSymbol Global, StructTypeSymbol Base) CreateSourceLessVirtualBase(
        bool abstractAccessors = false)
    {
        var global = new NamespaceSymbol(string.Empty, null);
        NamespaceSymbol ns = global.GetOrAddNamespace("Library");
        var baseType = new StructTypeSymbol("Base", ns, isAbstract: true, SymbolOrigin.Library);

        var read = new FunctionSymbol("Read", baseType, FunctionKind.Method, BuiltinTypes.Int,
            [], Accessibility.Public, isVirtual: true, isDefinition: true,
            origin: SymbolOrigin.Library);
        var required = new FunctionSymbol("Required", baseType, FunctionKind.Method, BuiltinTypes.Int,
            [], Accessibility.Public, isVirtual: true, isAbstract: true,
            origin: SymbolOrigin.Library);

        var property = new PropertySymbol("Value", baseType, BuiltinTypes.Int,
            Accessibility.Public, isStatic: false, isReadonly: false,
            isVirtual: !abstractAccessors,
            isOverride: false, isAbstract: abstractAccessors, SymbolOrigin.Library);
        var propertyGetter = new FunctionSymbol("get_Value", property, FunctionKind.Method,
            BuiltinTypes.Int, [], Accessibility.Public, isVirtual: !abstractAccessors,
            isAbstract: abstractAccessors, isDefinition: !abstractAccessors,
            origin: SymbolOrigin.Library, accessorKind: AccessorKind.Getter);
        var propertySetter = new FunctionSymbol("set_Value", property, FunctionKind.Method,
            BuiltinTypes.Void, [new ParameterSymbol("value", BuiltinTypes.Int, 0)],
            Accessibility.Public, isVirtual: !abstractAccessors,
            isAbstract: abstractAccessors, isDefinition: !abstractAccessors,
            origin: SymbolOrigin.Library, accessorKind: AccessorKind.Setter);
        property.SetAccessors(propertyGetter, propertySetter);

        var indexer = new IndexerSymbol(baseType, BuiltinTypes.Int,
            [new ParameterSymbol("index", BuiltinTypes.Int, 0)], Accessibility.Public,
            isStatic: false, isReadonly: false, isVirtual: !abstractAccessors,
            isOverride: false,
            isAbstract: abstractAccessors, SymbolOrigin.Library);
        var indexerGetter = new FunctionSymbol(indexer.GetAccessorName(getter: true), indexer,
            FunctionKind.Method, BuiltinTypes.Int,
            [new ParameterSymbol("index", BuiltinTypes.Int, 0)], Accessibility.Public,
            isVirtual: !abstractAccessors, isAbstract: abstractAccessors,
            isDefinition: !abstractAccessors, origin: SymbolOrigin.Library,
            accessorKind: AccessorKind.Getter);
        var indexerSetter = new FunctionSymbol(indexer.GetAccessorName(getter: false), indexer,
            FunctionKind.Method, BuiltinTypes.Void,
            [new ParameterSymbol("index", BuiltinTypes.Int, 0),
                new ParameterSymbol("value", BuiltinTypes.Int, 1)], Accessibility.Public,
            isVirtual: !abstractAccessors, isAbstract: abstractAccessors,
            isDefinition: !abstractAccessors, origin: SymbolOrigin.Library,
            accessorKind: AccessorKind.Setter);
        indexer.SetAccessors(indexerGetter, indexerSetter);

        var destructor = new FunctionSymbol("~Base", baseType, FunctionKind.Destructor,
            BuiltinTypes.Void, [], Accessibility.Public, isVirtual: true, isDefinition: true,
            origin: SymbolOrigin.Library);

        FunctionSymbol[] virtualMethods =
            [read, required, propertyGetter, propertySetter, indexerGetter, indexerSetter, destructor];
        for (int slot = 0; slot < virtualMethods.Length; slot++)
            virtualMethods[slot].SetVTableSlot(slot);

        baseType.SetProperties([property]);
        baseType.SetIndexers([indexer]);
        baseType.SetMethods([propertyGetter, propertySetter, indexerGetter, indexerSetter, read, required]);
        baseType.SetDestructor(destructor);
        baseType.SetHasVirtualDispatch();
        baseType.SetVirtualMethods([.. virtualMethods]);
        Assert.True(ns.TryDeclareType(baseType));
        return (global, baseType);
    }

    private sealed class SemanticOnlyReference(NamespaceSymbol globalNamespace)
        : CompilationReference(Guid.NewGuid())
    {
        public override NamespaceSymbol GlobalNamespace { get; } = globalNamespace;
    }
}
