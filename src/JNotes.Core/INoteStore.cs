namespace JNotes.Core;

public interface INoteStore
{
    Task<IReadOnlyList<TreeEntry>> GetTreeAsync(CancellationToken ct = default);

    Task<Folder?> GetFolderAsync(Guid id, CancellationToken ct = default);
    Task<Folder> CreateFolderAsync(string name, Guid? parentId, CancellationToken ct = default);
    Task SetDefaultTemplateAsync(Guid folderId, Guid? templateId, CancellationToken ct = default);
    Task RenameFolderAsync(Guid id, string name, CancellationToken ct = default);
    Task MoveFolderAsync(Guid id, Guid? newParentId, CancellationToken ct = default);
    /// <summary>Deletes the folder and everything under it.</summary>
    Task DeleteFolderAsync(Guid id, CancellationToken ct = default);

    Task<Note?> GetNoteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Note>> GetRecentNotesAsync(int take, CancellationToken ct = default);
    Task<Note> CreateNoteAsync(string title, Guid? folderId, string content = "", CancellationToken ct = default);
    Task RenameNoteAsync(Guid id, string title, CancellationToken ct = default);
    Task MoveNoteAsync(Guid id, Guid? folderId, CancellationToken ct = default);
    Task SaveContentAsync(Guid id, string markdown, CancellationToken ct = default);
    Task DeleteNoteAsync(Guid id, CancellationToken ct = default);

    /// <summary>Raised after any change to the folder/note tree (not content saves).</summary>
    event Action? TreeChanged;
}
