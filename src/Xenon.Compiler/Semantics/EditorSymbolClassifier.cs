using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Semantics;

/// <summary>Protocol-independent editor classification shared by all tooling consumers.</summary>
public enum EditorSymbolKind
{
    Namespace,
    Struct,
    Interface,
    Enum,
    EnumMember,
    Function,
    Method,
    Constructor,
    Destructor,
    Property,
    Indexer,
    Field,
    Constant,
    Parameter,
    LocalVariable,
    Type,
    Template,
    TypeParameter,
}

public static class EditorSymbolClassifier
{
    public static EditorSymbolKind GetKind(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol switch
        {
            NamespaceSymbol => EditorSymbolKind.Namespace,
            StructTypeSymbol => EditorSymbolKind.Struct,
            InterfaceTypeSymbol => EditorSymbolKind.Interface,
            TemplateSymbol => EditorSymbolKind.Template,
            GenericParameterSymbol => EditorSymbolKind.TypeParameter,
            EnumTypeSymbol => EditorSymbolKind.Enum,
            ConstantSymbol { ContainingSymbol: EnumTypeSymbol } => EditorSymbolKind.EnumMember,
            ConstantSymbol => EditorSymbolKind.Constant,
            FunctionSymbol { FunctionKind: FunctionKind.Constructor } => EditorSymbolKind.Constructor,
            FunctionSymbol { FunctionKind: FunctionKind.Destructor } => EditorSymbolKind.Destructor,
            FunctionSymbol { FunctionKind: FunctionKind.Method } => EditorSymbolKind.Method,
            FunctionSymbol => EditorSymbolKind.Function,
            FieldSymbol => EditorSymbolKind.Field,
            IndexerSymbol or InterfaceIndexerSymbol => EditorSymbolKind.Indexer,
            PropertySymbol or InterfacePropertySymbol => EditorSymbolKind.Property,
            TemplateMethodRequirementSymbol => EditorSymbolKind.Method,
            TemplateConstructorRequirementSymbol => EditorSymbolKind.Constructor,
            TemplateIndexerRequirementSymbol => EditorSymbolKind.Indexer,
            TemplatePropertyRequirementSymbol => EditorSymbolKind.Property,
            SyntheticMemberSymbol { MemberKind: SyntheticMemberKind.Method } => EditorSymbolKind.Method,
            SyntheticMemberSymbol => EditorSymbolKind.Property,
            ParameterSymbol => EditorSymbolKind.Parameter,
            LocalVariableSymbol => EditorSymbolKind.LocalVariable,
            DeclaredTypeSymbol => EditorSymbolKind.Type,
            _ => EditorSymbolKind.Type,
        };
    }

    public static bool IsEditorVisible(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return symbol.IsUserVisible && (!symbol.IsCompilerGenerated || symbol is SyntheticMemberSymbol);
    }

    public static bool CanRename(Symbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return IsEditorVisible(symbol) && symbol.HasUserEditableIdentifier && symbol.IsSourceDefined &&
            symbol is not NamespaceSymbol and not AliasSymbol and not SyntheticMemberSymbol and not ErrorSymbol &&
            symbol is not FunctionSymbol { IsExtern: true };
    }
}
