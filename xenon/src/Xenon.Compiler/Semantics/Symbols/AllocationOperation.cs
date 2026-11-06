namespace Xenon.Compiler.Semantics.Symbols;

/// <summary>Language-only lifecycle operations; their reserved names cannot be spelled by source identifiers.</summary>
public enum AllocationOperation { None, Allocate, Deallocate, Construct }
