using Xenon.Compiler.Text;

namespace Xenon.ProjectSystem;

/// <summary>
/// A totally ordered editor/effective-text generation. Only editor-controlled text/lifecycle
/// transitions consume this version; disk backing changes have an independent revision.
/// </summary>
public readonly record struct DocumentVersion : IComparable<DocumentVersion>
{
    public DocumentVersion(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }
    public static DocumentVersion Initial { get; } = new(0);

    public int CompareTo(DocumentVersion other) => Value.CompareTo(other.Value);
    public static bool operator <(DocumentVersion left, DocumentVersion right) => left.Value < right.Value;
    public static bool operator >(DocumentVersion left, DocumentVersion right) => left.Value > right.Value;
    public static bool operator <=(DocumentVersion left, DocumentVersion right) => left.Value <= right.Value;
    public static bool operator >=(DocumentVersion left, DocumentVersion right) => left.Value >= right.Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A Workspace-owned revision of the latest known disk/backing state.</summary>
public readonly record struct BackingVersion : IComparable<BackingVersion>
{
    public BackingVersion(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }
    public static BackingVersion Initial { get; } = new(0);

    public int CompareTo(BackingVersion other) => Value.CompareTo(other.Value);
    public static bool operator <(BackingVersion left, BackingVersion right) => left.Value < right.Value;
    public static bool operator >(BackingVersion left, BackingVersion right) => left.Value > right.Value;
    public static bool operator <=(BackingVersion left, BackingVersion right) => left.Value <= right.Value;
    public static bool operator >=(BackingVersion left, BackingVersion right) => left.Value >= right.Value;
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct ProjectVersion(long Value) : IComparable<ProjectVersion>
{
    public static ProjectVersion Initial { get; } = new(0);
    public int CompareTo(ProjectVersion other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct WorkspaceGeneration(long Value) : IComparable<WorkspaceGeneration>
{
    public static WorkspaceGeneration Initial { get; } = new(0);
    public int CompareTo(WorkspaceGeneration other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A replacement range expressed in UTF-16 positions of the prior immutable source.</summary>
public readonly record struct DocumentTextChange
{
    public DocumentTextChange(TextSpan span, string newText)
    {
        ArgumentNullException.ThrowIfNull(newText);
        Span = span;
        NewText = newText;
    }

    public TextSpan Span { get; }
    public string NewText { get; }
}

public enum DocumentChangeKind
{
    None,
    BackingStateOnly,
    BodyOnly,
    Declaration,
}

public sealed class StaleDocumentVersionException : InvalidOperationException
{
    public StaleDocumentVersionException(DocumentId documentId, DocumentVersion current,
        DocumentVersion requested)
        : base($"Document '{documentId}' is at version {current}; version {requested} is stale or out of order.")
    {
        DocumentId = documentId;
        CurrentVersion = current;
        RequestedVersion = requested;
    }

    public DocumentId DocumentId { get; }
    public DocumentVersion CurrentVersion { get; }
    public DocumentVersion RequestedVersion { get; }
}
