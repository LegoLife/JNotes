namespace JNotes.Core;

public interface INoteStore
{
    Task<IReadOnlyList<TreeEntry>> GetTreeAsync(CancellationToken ct = default);

    Task<Folder?> GetFolderAsync(Guid id, CancellationToken ct = default);
    Task<Folder> CreateFolderAsync(string name, Guid? parentId, CancellationToken ct = default);
    Task SetDefaultTemplateAsync(Guid folderId, Guid? templateId, CancellationToken ct = default);
    Task RenameFolderAsync(Guid id, string name, CancellationToken ct = default);
    Task MoveFolderAsync(Guid id, Guid? newParentId, CancellationToken ct = default);
    /// <summary>Moves the folder and everything under it to Trash. Returns the trash batch (for Undo).</summary>
    Task<Guid> DeleteFolderAsync(Guid id, CancellationToken ct = default);

    Task<Note?> GetNoteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> GetRecentNotesAsync(int take, CancellationToken ct = default);
    /// <summary>Notes whose markdown contains the given literal text (case-sensitive).</summary>
    Task<IReadOnlyList<Note>> FindNotesContainingAsync(string text, CancellationToken ct = default);
    Task<Note> CreateNoteAsync(string title, Guid? folderId, string content = "", CancellationToken ct = default);
    Task RenameNoteAsync(Guid id, string title, CancellationToken ct = default);
    Task MoveNoteAsync(Guid id, Guid? folderId, CancellationToken ct = default);
    Task SaveContentAsync(Guid id, string markdown, CancellationToken ct = default);
    /// <summary>Sets original dates (used when importing from another app).</summary>
    Task SetTimestampsAsync(Guid id, DateTimeOffset createdAt, DateTimeOffset updatedAt, CancellationToken ct = default);
    /// <summary>Moves the note to Trash. Returns the trash batch (for Undo).</summary>
    Task<Guid> DeleteNoteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Lets the store tell other parts of the app that the tree changed (e.g. after a Trash restore).</summary>
    void NotifyTreeChanged();

    /// <summary>Raised after any change to the folder/note tree (not content saves).</summary>
    event Action? TreeChanged;
}
