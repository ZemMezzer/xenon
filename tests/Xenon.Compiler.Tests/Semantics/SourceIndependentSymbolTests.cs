using Xenon.Compiler.Semantics.Symbols;
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
}
