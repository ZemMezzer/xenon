using System.Collections.Immutable;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>Presentation only: these formats must not be used for type identity or native names.</summary>
public enum SymbolDisplayFormat
{
    ShortName,
    QualifiedName,
    Signature,
    QualifiedSignature,
    Declaration,
    /// <summary>Qualified member name and parameter types, without return type or parameter names.</summary>
    Diagnostic,
}

/// <summary>Compiler-owned formatting for symbols. Embedded types always use the type display API.</summary>
public static class SymbolDisplay
{
    public static string ToDisplayString(Symbol symbol, SymbolDisplayFormat format = SymbolDisplayFormat.Signature)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));

        bool qualified = format is SymbolDisplayFormat.QualifiedName or SymbolDisplayFormat.QualifiedSignature or SymbolDisplayFormat.Diagnostic;
        TypeDisplayFormat typeFormat = format is SymbolDisplayFormat.QualifiedName or SymbolDisplayFormat.QualifiedSignature
            ? TypeDisplayFormat.FullyQualified : TypeDisplayFormat.Short;
        if (symbol is TypeSymbol type)
        {
            string display = type.ToDisplayString(qualified ? TypeDisplayFormat.FullyQualified : typeFormat);
            return format == SymbolDisplayFormat.Declaration && type is DeclaredTypeSymbol declared
                ? $"{(declared is StructTypeSymbol { IsAbstract: true } ? "abstract " : "")}{declared.DeclarationKind} {display}"
                : display;
        }

        string name = GetName(symbol, qualified);
        if (format is SymbolDisplayFormat.ShortName or SymbolDisplayFormat.QualifiedName) return name;

        bool diagnostic = format == SymbolDisplayFormat.Diagnostic;
        string displayText = symbol switch
        {
            FunctionSymbol function => Function(function, name, typeFormat, diagnostic),
            IndexerSymbol indexer => Indexer(indexer.Type, indexer.Parameters, name, typeFormat, diagnostic),
            InterfaceIndexerSymbol indexer => Indexer(indexer.Type, indexer.Parameters, name, typeFormat, diagnostic),
            FieldSymbol field => diagnostic ? name : $"{VariableType(field.Type, field.IsReadonly, typeFormat)} {name}",
            PropertySymbol property => diagnostic ? name : $"{property.Type.ToDisplayString(typeFormat)} {name}",
            InterfacePropertySymbol property => diagnostic ? name : $"{property.Type.ToDisplayString(typeFormat)} {name}",
            SyntheticMemberSymbol { MemberKind: SyntheticMemberKind.Method } member =>
                SyntheticMethod(member, name, typeFormat, diagnostic),
            SyntheticMemberSymbol member => diagnostic ? name : $"{member.Type.ToDisplayString(typeFormat)} {name}",
            ConstantSymbol constant => diagnostic ? name : $"const {constant.Type.ToDisplayString(typeFormat)} {name}",
            VariableSymbol variable => diagnostic ? name : $"{VariableType(variable.Type, variable.IsReadonly, typeFormat)} {name}",
            _ => name,
        };
        return format == SymbolDisplayFormat.Declaration ? Modifiers(symbol) + displayText : displayText;
    }

    private static string GetName(Symbol symbol, bool qualified)
    {
        static string Part(Symbol part) => part is FunctionSymbol { Declaration: PropertyAccessorDeclarationSyntax accessor }
            ? accessor.KeywordToken.Text : part.Name;
        if (!qualified) return Part(symbol);
        var parts = new Stack<string>();
        for (Symbol? current = symbol; current is not null; current = current.ContainingSymbol)
        {
            string part = Part(current);
            if (part.Length != 0) parts.Push(part);
        }
        return string.Join('.', parts);
    }

    private static string Function(FunctionSymbol function, string name, TypeDisplayFormat format, bool diagnostic)
    {
        string signature = $"{name}({Parameters(function.Parameters, format, !diagnostic)})";
        if (diagnostic) return signature;
        if (function.FunctionKind is not (FunctionKind.Constructor or FunctionKind.Destructor))
            signature = $"{function.ReturnType.ToDisplayString(format)} {signature}";
        // A suffix distinguishes receiver readonly from a readonly pointer/reference return type.
        return function.IsReadonly ? signature + " readonly" : signature;
    }

    private static string Indexer(TypeSymbol type, ImmutableArray<ParameterSymbol> parameters, string name,
        TypeDisplayFormat format, bool diagnostic)
    {
        string signature = $"{name}[{Parameters(parameters, format, !diagnostic)}]";
        return diagnostic ? signature : $"{type.ToDisplayString(format)} {signature}";
    }

    private static string SyntheticMethod(SyntheticMemberSymbol member, string name,
        TypeDisplayFormat format, bool diagnostic)
    {
        string signature = $"{name}({Parameters(member.Parameters, format, !diagnostic)})";
        return diagnostic ? signature : $"{member.ReturnType.ToDisplayString(format)} {signature}";
    }

    private static string Parameters(ImmutableArray<ParameterSymbol> parameters, TypeDisplayFormat format, bool includeNames) =>
        string.Join(", ", parameters.Select(parameter => includeNames
            ? $"{VariableType(parameter.Type, parameter.IsReadonly, format)} {parameter.Name}"
            : parameter.Type.ToDisplayString(format)));

    private static string VariableType(TypeSymbol type, bool isReadonly, TypeDisplayFormat format)
    {
        string display = type.ToDisplayString(format);
        if (!isReadonly) return display;
        // Pointer binding readonly and pointee readonly are independent qualifiers.
        if (type is PointerTypeSymbol) return display + " readonly";
        // For other source forms a single prefix can qualify both the binding and
        // a nested reference (including arrays of references). Reuse type rendering.
        return display.StartsWith("readonly ", StringComparison.Ordinal) ? display : "readonly " + display;
    }

    private static string Modifiers(Symbol symbol) => symbol switch
    {
        NamespaceSymbol => "namespace ",
        FunctionSymbol function => MemberModifiers(function.Accessibility, function.IsStatic, function.IsAbstract,
            function.IsVirtual, function.IsOverride) + (function.IsExtern ? "extern " : function.IsExport ? "export " : ""),
        FieldSymbol field => MemberModifiers(field.Accessibility, field.IsStatic),
        PropertySymbol property => MemberModifiers(property.Accessibility, property.Declaration.IsStatic,
            property.Declaration.IsAbstract, property.Declaration.IsVirtual, property.Declaration.IsOverride)
            + (property.Declaration.IsReadonly ? "readonly " : ""),
        IndexerSymbol indexer => MemberModifiers(indexer.Accessibility, indexer.Declaration.IsStatic,
            indexer.Declaration.IsAbstract, indexer.Declaration.IsVirtual, indexer.Declaration.IsOverride)
            + (indexer.Declaration.IsReadonly ? "readonly " : ""),
        InterfacePropertySymbol property => "public abstract " + (property.Declaration.IsReadonly ? "readonly " : ""),
        InterfaceIndexerSymbol indexer => "public abstract " + (indexer.Declaration.IsReadonly ? "readonly " : ""),
        _ => "",
    };

    private static string MemberModifiers(Accessibility accessibility, bool isStatic, bool isAbstract = false,
        bool isVirtual = false, bool isOverride = false) =>
        (accessibility == Accessibility.Public ? "public " : "private ")
        + (isStatic ? "static " : "")
        + (isAbstract ? "abstract " : "")
        + (isVirtual ? "virtual " : "")
        + (isOverride ? "override " : "");
}
