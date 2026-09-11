namespace Xenon.ProjectSystem;

/// <summary>Stable identity of one logical persisted Workspace.</summary>
public readonly record struct WorkspaceId(Guid Value) : IComparable<WorkspaceId>
{
    public static WorkspaceId CreateNew() => new(Guid.NewGuid());
    internal static WorkspaceId FromNormalizedPath(string path)
    {
        string identity = ProjectPath.StableIdentity(ProjectPath.Normalize(path));
        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity));
        return new WorkspaceId(new Guid(hash.AsSpan(0, 16)));
    }
    public int CompareTo(WorkspaceId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("D");
}

/// <summary>Stable identity of one logical project across Workspace generations.</summary>
public readonly record struct ProjectId(Guid Value) : IComparable<ProjectId>
{
    public static ProjectId CreateNew() => new(Guid.NewGuid());
    public int CompareTo(ProjectId other) => Value.CompareTo(other.Value);
    public override string ToString() => Value.ToString("D");
}

/// <summary>Stable project-scoped identity of one logical document.</summary>
public readonly record struct DocumentId(ProjectId ProjectId, Guid Value) : IComparable<DocumentId>
{
    public static DocumentId CreateNew(ProjectId projectId) => new(projectId, Guid.NewGuid());

    public int CompareTo(DocumentId other)
    {
        int project = ProjectId.CompareTo(other.ProjectId);
        return project != 0 ? project : Value.CompareTo(other.Value);
    }

    public override string ToString() => $"{ProjectId}/{Value:D}";
}
