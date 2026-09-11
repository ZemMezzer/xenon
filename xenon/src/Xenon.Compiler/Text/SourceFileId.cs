namespace Xenon.Compiler.Text;

/// <summary>Stable identity of one logical source file across immutable text/tree versions.</summary>
public readonly record struct SourceFileId(Guid Value)
{
    public static SourceFileId CreateNew() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
