namespace JNotes.Core;

public static class Retention
{
    public static readonly TimeSpan Trash = TimeSpan.FromDays(30);
    public static readonly TimeSpan History = TimeSpan.FromDays(30);
    /// <summary>While editing, keep at most one automatic snapshot per this interval.</summary>
    public static readonly TimeSpan EditSnapshotInterval = TimeSpan.FromMinutes(10);
}

public sealed record VersionInfo(Guid Id, string Title, string Reason, DateTimeOffset CreatedAt, int Length);

public interface INoteHistory
{
    /// <summary>Saves the note's current state (e.g. before an AI edit).</summary>
    Task SnapshotAsync(Guid noteId, string reason, CancellationToken ct = default);
    Task<IReadOnlyList<VersionInfo>> ListAsync(Guid noteId, CancellationToken ct = default);
    Task<NoteVersion?> GetAsync(Guid versionId, CancellationToken ct = default);
    /// <summary>Restores title and content from a version (snapshotting the current state first). Returns the new content.</summary>
    Task<Note> RestoreAsync(Guid versionId, CancellationToken ct = default);
    /// <summary>Deletes versions older than the retention period. Returns how many were removed.</summary>
    Task<int> PruneAsync(CancellationToken ct = default);
}

/// <summary>One thing the user deleted (a note, or a folder with everything in it).</summary>
public sealed record TrashItem(Guid Batch, Guid RootId, string Name, bool IsFolder, int NoteCount, int FolderCount, DateTimeOffset DeletedAt, string OriginalLocation);

public interface ITrash
{
    Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken ct = default);
    /// <summary>Restores everything deleted together. If its original folder is gone, it goes to the top level.</summary>
    Task RestoreAsync(Guid batch, CancellationToken ct = default);
    Task DeleteForeverAsync(Guid batch, CancellationToken ct = default);
    Task<int> EmptyAsync(CancellationToken ct = default);
    /// <summary>Permanently removes items older than the retention period. Returns how many batches were removed.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken ct = default);
}

/// <summary>A note was replaced wholesale (e.g. restored from history); open editors should reload it.</summary>
public sealed record ReplacedEdit(Guid NoteId, string NewContent, string Title) : NoteEdit(NoteId, NewContent);
