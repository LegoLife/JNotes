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
            .Select(f => new { Entry = new TreeEntry(f.Id, f.Name, f.ParentId, true), f.SortOrder }).ToListAsync(ct);
        var notes = await db.Notes.AsNoTracking()
            .Select(n => new { Entry = new TreeEntry(n.Id, n.Title, n.FolderId, false), n.SortOrder }).ToListAsync(ct);
        // Custom order first; items never ordered (0) fall back to folders-then-notes, alphabetical.
        return folders.Concat(notes)
            .OrderBy(x => x.SortOrder)
            .ThenByDescending(x => x.Entry.IsFolder)
            .ThenBy(x => x.Entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.Entry)
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
        var folder = new Folder { Name = Names.Normalize(name, "Folder"), ParentId = parentId, SortOrder = await NextSortOrder(db, parentId, ct), CreatedAt = now, UpdatedAt = now };
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
        if (folder.ParentId != newParentId) folder.SortOrder = await NextSortOrder(db, newParentId, ct);
        folder.ParentId = newParentId;
        folder.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
    }

    public async Task<Guid> DeleteFolderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folder = await RequireFolder(db, id, ct);

        // Collect the folder's live subtree; everything goes to Trash together as one batch.
        var all = await db.Folders.ToListAsync(ct);
        var subtree = new List<Folder> { folder };
        for (var i = 0; i < subtree.Count; i++)
            subtree.AddRange(all.Where(f => f.ParentId == subtree[i].Id));
        var folderIds = subtree.Select(f => f.Id).ToList();
        var notes = await db.Notes.Where(n => n.FolderId != null && folderIds.Contains(n.FolderId.Value)).ToListAsync(ct);

        var (now, batch) = (clock.GetUtcNow(), Guid.NewGuid());
        foreach (var f in subtree) { f.DeletedAt = now; f.TrashBatch = batch; }
        foreach (var n in notes) { n.DeletedAt = now; n.TrashBatch = batch; }
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
        return batch;
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

    public async Task<IReadOnlyList<Note>> FindNotesContainingAsync(string text, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Notes.AsNoTracking().Where(n => n.Content.Contains(text)).ToListAsync(ct);
    }

    public async Task<Note> CreateNoteAsync(string title, Guid? folderId, string content = "", CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        if (folderId is { } f) await RequireFolder(db, f, ct);
        var now = clock.GetUtcNow();
        var note = new Note { Title = Names.Normalize(title, "Note"), FolderId = folderId, SortOrder = await NextSortOrder(db, folderId, ct), Content = content, CreatedAt = now, UpdatedAt = now };
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
        if (note.FolderId != folderId) note.SortOrder = await NextSortOrder(db, folderId, ct);
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

        // While editing, keep the state from before this burst of changes (at most one per interval).
        var now = clock.GetUtcNow();
        var cutoff = now - Retention.EditSnapshotInterval;
        if (note.Content.Length > 0 && !await db.NoteVersions.AnyAsync(v => v.NoteId == id && v.CreatedAt > cutoff, ct))
            db.NoteVersions.Add(new NoteVersion { NoteId = id, Title = note.Title, Content = note.Content, Reason = "While editing", CreatedAt = now });

        note.Content = markdown;
        note.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
    }

    public async Task<Guid> DeleteNoteAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var note = await RequireNote(db, id, ct);
        var batch = Guid.NewGuid();
        note.DeletedAt = clock.GetUtcNow();
        note.TrashBatch = batch;
        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
        return batch;
    }

    public async Task SetTimestampsAsync(Guid id, DateTimeOffset createdAt, DateTimeOffset updatedAt, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var note = await RequireNote(db, id, ct);
        note.CreatedAt = createdAt;
        note.UpdatedAt = updatedAt;
        await db.SaveChangesAsync(ct);
    }

    public async Task ReorderAsync(Guid? parentId, IReadOnlyList<Guid> orderedIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folders = await db.Folders.Where(f => f.ParentId == parentId).ToListAsync(ct);
        var notes = await db.Notes.Where(n => n.FolderId == parentId).ToListAsync(ct);
        var rank = orderedIds.Distinct().Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);

        // Listed ids first in the given order, then anything not listed in its current display order.
        var children = folders.Select(f => (f.Id, f.SortOrder, IsFolder: true, f.Name, Set: (Action<int>)(v => f.SortOrder = v)))
            .Concat(notes.Select(n => (n.Id, n.SortOrder, IsFolder: false, Name: n.Title, Set: (Action<int>)(v => n.SortOrder = v))))
            .OrderBy(c => rank.GetValueOrDefault(c.Id, int.MaxValue))
            .ThenBy(c => c.SortOrder).ThenByDescending(c => c.IsFolder).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        for (var i = 0; i < children.Count; i++) children[i].Set(i + 1);

        await db.SaveChangesAsync(ct);
        TreeChanged?.Invoke();
    }

    /// <summary>
    /// Where a new arrival goes: at the end if the folder has a custom order, otherwise 0 so it
    /// sorts alphabetically with everything else.
    /// </summary>
    private static async Task<int> NextSortOrder(JNotesDbContext db, Guid? parentId, CancellationToken ct)
    {
        var maxFolder = await db.Folders.Where(f => f.ParentId == parentId).MaxAsync(f => (int?)f.SortOrder, ct) ?? 0;
        var maxNote = await db.Notes.Where(n => n.FolderId == parentId).MaxAsync(n => (int?)n.SortOrder, ct) ?? 0;
        var max = Math.Max(maxFolder, maxNote);
        return max == 0 ? 0 : max + 1;
    }

    public void NotifyTreeChanged() => TreeChanged?.Invoke();

    // FirstOrDefaultAsync (not FindAsync) so the "not in Trash" query filter always applies.
    private static async Task<Folder> RequireFolder(JNotesDbContext db, Guid id, CancellationToken ct) =>
        await db.Folders.FirstOrDefaultAsync(f => f.Id == id, ct) ?? throw new DomainException("Folder not found.");

    private static async Task<Note> RequireNote(JNotesDbContext db, Guid id, CancellationToken ct) =>
        await db.Notes.FirstOrDefaultAsync(n => n.Id == id, ct) ?? throw new DomainException("Note not found.");
}
