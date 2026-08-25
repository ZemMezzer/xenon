namespace Xenon.Compiler.Semantics.Symbols;

public static class NativeSymbolNames
{
    public static string Get(FunctionSymbol function)
    {
        ArgumentNullException.ThrowIfNull(function);

        if (function.IsExtern)
        {
            return function.Name;
        }

        return function.IsExport
            ? function.FullName.Replace('.', '_')
            : function.FullName;
    }
}
