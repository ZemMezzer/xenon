namespace Xenon.Compiler.Semantics.Symbols;

public enum Accessibility
{
    Private,
    Public,
}

public enum FunctionKind
{
    Ordinary,
    Method,
    Constructor,
    Destructor,
}

public enum ArrayStorageKind
{
    Unknown,
    Heap,
    Stack,
}
