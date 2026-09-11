using Xenon.Compiler.Semantics;

namespace Xenon.LanguageServer;

/// <summary>
/// Presentation-only conversion from Xenon's semantic symbol categories to the
/// closest standardized LSP CompletionItemKind understood by editor clients.
/// </summary>
internal static class LspCompletionItemKindAdapter
{
    public static int ToCompletionItemKind(EditorSymbolKind kind) => kind switch
    {
        EditorSymbolKind.Namespace => 9,      // Module
        EditorSymbolKind.Struct => 22,        // Struct
        EditorSymbolKind.Interface => 8,      // Interface
        EditorSymbolKind.Template => 18,      // Reference
        EditorSymbolKind.Function => 3,       // Function
        EditorSymbolKind.Method => 3,         // Function (intentionally shared)
        EditorSymbolKind.Constructor => 4,    // Constructor
        EditorSymbolKind.Destructor => 24,    // Operator
        EditorSymbolKind.Field => 5,          // Field
        EditorSymbolKind.Property => 10,      // Property
        EditorSymbolKind.Indexer => 23,       // Event
        EditorSymbolKind.LocalVariable => 6,  // Variable
        EditorSymbolKind.Parameter => 12,     // Value
        EditorSymbolKind.Constant => 21,      // Constant
        EditorSymbolKind.TypeParameter => 25, // TypeParameter
        EditorSymbolKind.Enum => 13,           // Enum
        EditorSymbolKind.EnumMember => 20,     // EnumMember
        EditorSymbolKind.Type => 22,           // Struct: built-in and other value-like types
        _ => 1,                                // Text
    };

    public static string XenonKindName(EditorSymbolKind kind) => kind switch
    {
        EditorSymbolKind.Namespace => "namespace",
        EditorSymbolKind.Struct => "struct",
        EditorSymbolKind.Interface => "interface",
        EditorSymbolKind.Template => "template",
        EditorSymbolKind.Function => "function",
        EditorSymbolKind.Method => "method",
        EditorSymbolKind.Constructor => "constructor",
        EditorSymbolKind.Destructor => "destructor",
        EditorSymbolKind.Field => "field",
        EditorSymbolKind.Property => "property",
        EditorSymbolKind.Indexer => "indexer",
        EditorSymbolKind.LocalVariable => "local",
        EditorSymbolKind.Parameter => "parameter",
        EditorSymbolKind.Constant => "constant",
        EditorSymbolKind.TypeParameter => "type parameter",
        EditorSymbolKind.Enum => "enum",
        EditorSymbolKind.EnumMember => "enum member",
        EditorSymbolKind.Type => "type",
        _ => "symbol",
    };
}
