using JNotes.Core;
using Microsoft.EntityFrameworkCore;

namespace JNotes.Data;

/// <summary>Soft-deleted notes and folders, grouped into the batches they were deleted in.</summary>
public sealed class TrashStore(IDbContextFactory<JNotesDbContext> factory, TimeProvider clock, INoteStore store) : ITrash
{
    public async Task<IReadOnlyList<TrashItem>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var allFolders = await db.Folders.IgnoreQueryFilters().AsNoTracking().ToListAsync(ct);
        var trashedNotes = await db.Notes.IgnoreQueryFilters().AsNoTracking().Where(n => n.DeletedAt != null)
            .Select(n => new { n.Id, n.Title, n.FolderId, n.TrashBatch, n.DeletedAt }).ToListAsync(ct);
        var byId = allFolders.ToDictionary(f => f.Id);

        string PathOf(Guid? folderId)
        {
            var parts = new List<string>();
            for (var id = folderId; id is { } i && byId.TryGetValue(i, out var f) && parts.Count < 50; id = f.ParentId) parts.Add(f.Name);
            parts.Reverse();
            return parts.Count == 0 ? "Top level" : string.Join(" / ", parts);
        }

        var items = new List<TrashItem>();
        foreach (var batch in allFolders.Where(f => f.TrashBatch is not null).Select(f => f.TrashBatch!.Value)
                     .Concat(trashedNotes.Select(n => n.TrashBatch!.Value)).Distinct())
        {
            var folders = allFolders.Where(f => f.TrashBatch == batch).ToList();
            var notes = trashedNotes.Where(n => n.TrashBatch == batch).ToList();
            var folderIds = folders.Select(f => f.Id).ToHashSet();
            var rootFolder = folders.FirstOrDefault(f => f.ParentId is null || !folderIds.Contains(f.ParentId.Value));
            items.Add(rootFolder is not null
                ? new TrashItem(batch, rootFolder.Id, rootFolder.Name, true, notes.Count, folders.Count - 1, rootFolder.DeletedAt!.Value, PathOf(rootFolder.ParentId))
                : new TrashItem(batch, notes[0].Id, notes[0].Title, false, 1, 0, notes[0].DeletedAt!.Value, PathOf(notes[0].FolderId)));
        }
        return items.OrderByDescending(i => i.DeletedAt).ToList();
    }

    public async Task RestoreAsync(Guid batch, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var folders = await db.Folders.IgnoreQueryFilters().Where(f => f.TrashBatch == batch).ToListAsync(ct);
        var notes = await db.Notes.IgnoreQueryFilters().Where(n => n.TrashBatch == batch).ToListAsync(ct);
        if (folders.Count == 0 && notes.Count == 0) throw new DomainException("That item is no longer in the Trash.");

        // If the item's original folder is gone (or still in the Trash), restore it to the top level.
        var batchFolderIds = folders.Select(f => f.Id).ToHashSet();
        var liveFolderIds = (await db.Folders.Select(f => f.Id).ToListAsync(ct)).ToHashSet();
        bool Orphaned(Guid? parent) => parent is { } p && !batchFolderIds.Contains(p) && !liveFolderIds.Contains(p);

        foreach (var f in folders)
        {
            if (Orphaned(f.ParentId)) f.ParentId = null;
            f.DeletedAt = null;
            f.TrashBatch = null;
        }
        foreach (var n in notes)
        {
            if (Orphaned(n.FolderId)) n.FolderId = null;
            n.DeletedAt = null;
            n.TrashBatch = null;
        }
        await db.SaveChangesAsync(ct);
        store.NotifyTreeChanged();
    }

    public async Task DeleteForeverAsync(Guid batch, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await DeleteBatchAsync(db, batch, ct);
    }

    public async Task<int> EmptyAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var batches = await TrashedBatchesAsync(db, null, ct);
        foreach (var b in batches) await DeleteBatchAsync(db, b, ct);
        return batches.Count;
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var batches = await TrashedBatchesAsync(db, clock.GetUtcNow() - Retention.Trash, ct);
        foreach (var b in batches) await DeleteBatchAsync(db, b, ct);
        return batches.Count;
    }

    private static async Task<List<Guid>> TrashedBatchesAsync(JNotesDbContext db, DateTimeOffset? olderThan, CancellationToken ct)
    {
        var folders = db.Folders.IgnoreQueryFilters().Where(f => f.TrashBatch != null);
        var notes = db.Notes.IgnoreQueryFilters().Where(n => n.TrashBatch != null);
        if (olderThan is { } cutoff)
        {
            folders = folders.Where(f => f.DeletedAt < cutoff);
            notes = notes.Where(n => n.DeletedAt < cutoff);
        }
        var ids = await folders.Select(f => f.TrashBatch!.Value).ToListAsync(ct);
        ids.AddRange(await notes.Select(n => n.TrashBatch!.Value).ToListAsync(ct));
        return ids.Distinct().ToList();
    }

    private static async Task DeleteBatchAsync(JNotesDbContext db, Guid batch, CancellationToken ct)
    {
        var folderIds = await db.Folders.IgnoreQueryFilters().Where(f => f.TrashBatch == batch).Select(f => f.Id).ToListAsync(ct);

        // Items trashed separately but still inside these folders must survive the cascade: lift them to the top level.
        await db.Notes.IgnoreQueryFilters()
            .Where(n => n.FolderId != null && folderIds.Contains(n.FolderId.Value) && n.TrashBatch != batch)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.FolderId, (Guid?)null), ct);
        await db.Folders.IgnoreQueryFilters()
            .Where(f => f.ParentId != null && folderIds.Contains(f.ParentId.Value) && f.TrashBatch != batch)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.ParentId, (Guid?)null), ct);

        // Notes first (their FTS rows and versions go via trigger/cascade), then folders.
        await db.Notes.IgnoreQueryFilters().Where(n => n.TrashBatch == batch).ExecuteDeleteAsync(ct);
        await db.Folders.IgnoreQueryFilters().Where(f => f.TrashBatch == batch).ExecuteDeleteAsync(ct);
    }
}
