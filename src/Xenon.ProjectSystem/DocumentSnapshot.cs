using System.Collections.Immutable;
using Xenon.Compiler.Syntax;
using Xenon.Compiler.Text;

namespace Xenon.ProjectSystem;

/// <summary>One immutable disk/editor source generation.</summary>
public sealed class DocumentSnapshot
{
    internal DocumentSnapshot(DocumentId id, string? physicalPath, SourceText? diskText,
        SourceText? overlayText, DocumentVersion version, SyntaxTree? reusableTree = null,
        CancellationToken cancellationToken = default,
        BackingVersion backingVersion = default)
    {
        if (id.ProjectId == default) throw new ArgumentException("A document must belong to a project.", nameof(id));
        if (diskText is null && overlayText is null)
            throw new ArgumentException("A document requires disk text or an editor overlay.");
        if (diskText is not null && overlayText is not null && diskText.FileId != overlayText.FileId)
            throw new ArgumentException("Disk and overlay text must preserve one source identity.");
        Id = id;
        ProjectId = id.ProjectId;
        PhysicalPath = physicalPath;
        Version = version;
        BackingVersion = backingVersion;
        SourceText effective = overlayText ?? diskText!;
        bool canReuseTree = reusableTree is not null &&
            reusableTree.Source.FileId == effective.FileId &&
            reusableTree.Source.Path == effective.Path &&
            reusableTree.Source.Text == effective.Text;
        if (canReuseTree)
        {
            effective = reusableTree!.Source;
            if (overlayText is not null) overlayText = effective;
            else diskText = effective;
        }
        DiskText = diskText;
        OverlayText = overlayText;
        EffectiveText = effective;
        SyntaxTree = canReuseTree ? reusableTree! : SyntaxTree.Parse(EffectiveText, cancellationToken);
        DeclarationFingerprint = Xenon.ProjectSystem.DeclarationFingerprint.Create(SyntaxTree,
            cancellationToken);
    }

    public DocumentId Id { get; }
    public ProjectId ProjectId { get; }
    public DocumentVersion Version { get; }
    public BackingVersion BackingVersion { get; }
    public string? PhysicalPath { get; }
    public SourceText? DiskText { get; }
    public SourceText? OverlayText { get; }
    public SourceText EffectiveText { get; }
    public SyntaxTree SyntaxTree { get; }
    public SourceFileId SourceFileId => EffectiveText.FileId;
    public bool IsOpen => OverlayText is not null;
    public bool IsUnsaved => OverlayText is not null &&
        (DiskText is null || OverlayText.Text != DiskText.Text);
    public bool HasPhysicalFile => PhysicalPath is not null;
    internal string DeclarationFingerprint { get; }

    internal (DocumentSnapshot Snapshot, DocumentChangeKind Kind) WithEditorState(
        SourceText? diskText, SourceText? overlayText, DocumentVersion version,
        CancellationToken cancellationToken = default)
    {
        EnsureNewer(version);
        SourceText effective = overlayText ?? diskText ??
            throw new ArgumentException("A document requires effective source text.");
        bool sameText = effective.Text == EffectiveText.Text && effective.Path == EffectiveText.Path;
        var snapshot = new DocumentSnapshot(Id, PhysicalPath, diskText, overlayText, version,
            sameText ? SyntaxTree : null, cancellationToken, BackingVersion);
        return (snapshot, ClassifyChange(snapshot, sameText));
    }

    internal (DocumentSnapshot Snapshot, DocumentChangeKind Kind) WithBackingState(
        SourceText? diskText, SourceText? overlayText,
        CancellationToken cancellationToken = default)
    {
        SourceText effective = overlayText ?? diskText ??
            throw new ArgumentException("A document requires effective source text.");
        bool sameText = effective.Text == EffectiveText.Text && effective.Path == EffectiveText.Path;
        var backingVersion = new BackingVersion(checked(BackingVersion.Value + 1));
        var snapshot = new DocumentSnapshot(Id, PhysicalPath, diskText, overlayText, Version,
            sameText ? SyntaxTree : null, cancellationToken, backingVersion);
        return (snapshot, ClassifyChange(snapshot, sameText));
    }

    private DocumentChangeKind ClassifyChange(DocumentSnapshot snapshot, bool sameText)
    {
        DocumentChangeKind kind = sameText ? DocumentChangeKind.BackingStateOnly :
            snapshot.DeclarationFingerprint == DeclarationFingerprint
                ? DocumentChangeKind.BodyOnly : DocumentChangeKind.Declaration;
        return kind;
    }

    internal (DocumentSnapshot Snapshot, DocumentChangeKind Kind) ApplyChanges(
        DocumentVersion expectedVersion, DocumentVersion newVersion,
        ImmutableArray<DocumentTextChange> changes, CancellationToken cancellationToken = default)
    {
        if (expectedVersion != Version)
            throw new StaleDocumentVersionException(Id, Version, expectedVersion);
        EnsureNewer(newVersion);
        if (changes.IsDefault) throw new ArgumentException("Changes must be initialized.", nameof(changes));
        int priorEnd = 0;
        foreach (DocumentTextChange change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (change.Span.Start < priorEnd || change.Span.End > EffectiveText.Length)
                throw new ArgumentOutOfRangeException(nameof(changes),
                    "Changes must be ordered, non-overlapping ranges within the prior source.");
            priorEnd = change.Span.End;
        }
        string text = EffectiveText.Text;
        for (int index = changes.Length - 1; index >= 0; index--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DocumentTextChange change = changes[index];
            text = string.Concat(text.AsSpan(0, change.Span.Start), change.NewText,
                text.AsSpan(change.Span.End));
        }
        SourceText overlay = EffectiveText.WithText(text);
        return WithEditorState(DiskText, overlay, newVersion, cancellationToken);
    }

    private void EnsureNewer(DocumentVersion version)
    {
        if (version <= Version) throw new StaleDocumentVersionException(Id, Version, version);
    }
}
