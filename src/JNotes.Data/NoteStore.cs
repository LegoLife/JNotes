using JNotes.Core;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Data;

/// <summary>Singleton; each operation uses its own short-lived DbContext.</summary>
public sealed class NoteStore(IDbContextFactory<JNotesDbContext> factory, TimeProvider clock) : INoteStore
{
    public event Action? TreeChanged;

    public async Task<IReadOnlyList<TreeEntry>> GetTreeAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folders = await db.Folders.AsNoTracking()
            .Select(f => new TreeEntry(f.Id, f.Name, f.ParentId, true)).ToListAsync(ct);
        var notes = await db.Notes.AsNoTracking()
            .Select(n => new TreeEntry(n.Id, n.Title, n.FolderId, false)).ToListAsync(ct);
        return folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Concat(notes.OrderBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase))
            .ToList();
    }

    public async Task<Folder?> GetFolderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);
    }

    public async Task SetDefaultTemplateAsync(Guid folderId, Guid? templateId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folder = await RequireFolder(db, folderId, ct);
        if (templateId is { } t && !await db.Templates.AnyAsync(x => x.Id == t, ct))
            throw new DomainException("Template not found.");
        folder.DefaultTemplateId = templateId;
        folder.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task<Folder> CreateFolderAsync(string name, Guid? parentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (parentId is { } p) await RequireFolder(db, p, ct);
        var now = clock.GetUtcNow();
        var folder = new Folder { Name = Names.Normalize(name, "Folder"), ParentId = parentId, CreatedAt = now, UpdatedAt = now };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
        return folder;
    }

    public async Task RenameFolderAsync(Guid id, string name, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folder = await RequireFolder(db, id, ct);
        folder.Name = Names.Normalize(name, "Folder");
        folder.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
    }

    public async Task MoveFolderAsync(Guid id, Guid? newParentId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folder = await RequireFolder(db, id, ct);
        if (newParentId is { } target)
        {
            await RequireFolder(db, target, ct);
            // Walk up from the target; if we meet the folder being moved, it would create a cycle.
            var parents = await db.Folders.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.ParentId, ct);
            for (Guid? cur = target; cur is { } c; cur = parents.GetValueOrDefault(c))
                if (c == id) throw new DomainException("Can't move a folder into itself or one of its subfolders.");
        }
        folder.ParentId = newParentId;
        folder.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
    }

    public async Task DeleteFolderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folder = await RequireFolder(db, id, ct);
        db.Folders.Remove(folder); // FK cascade removes subfolders and notes
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
    }

    public async Task<Note?> GetNoteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Notes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);
    }

    public async Task<IReadOnlyList<Note>> GetRecentNotesAsync(int take, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Notes.AsNoTracking().OrderByDescending(n => n.UpdatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<Note> CreateNoteAsync(string title, Guid? folderId, string content = "", CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (folderId is { } f) await RequireFolder(db, f, ct);
        var now = clock.GetUtcNow();
        var note = new Note { Title = Names.Normalize(title, "Note"), FolderId = folderId, Content = content, CreatedAt = now, UpdatedAt = now };
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
        return note;
    }

    public async Task RenameNoteAsync(Guid id, string title, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var note = await RequireNote(db, id, ct);
        note.Title = Names.Normalize(title, "Note");
        note.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
    }

    public async Task MoveNoteAsync(Guid id, Guid? folderId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var note = await RequireNote(db, id, ct);
        if (folderId is { } f) await RequireFolder(db, f, ct);
        note.FolderId = folderId;
        note.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
    }

    public async Task SaveContentAsync(Guid id, string markdown, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var note = await RequireNote(db, id, ct);
        if (note.Content == markdown) return;
        note.Content = markdown;
        note.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteNoteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Notes.Remove(await RequireNote(db, id, ct));
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
    }

    private static async Task<Folder> RequireFolder(JNotesDbContext db, Guid id, CancellationToken ct) =>
        await db.Folders.FindAsync([id], ct) ?? throw new DomainException("Folder not found.");

    private static async Task<Note> RequireNote(JNotesDbContext db, Guid id, CancellationToken ct) =>
        await db.Notes.FindAsync([id], ct) ?? throw new DomainException("Note not found.");
}
